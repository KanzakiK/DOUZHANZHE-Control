import express from "express";
import { createServer } from "http";
import { WebSocketServer } from "ws";
import si from "systeminformation";
import { spawn, exec, execSync } from "child_process";
import path from "path";
import { fileURLToPath } from "url";
import fs from "fs";
import { atomicWrite, safeRead } from "./utils/jsonStore.js";
import { promisify } from "util";

// ---- SMU 访问封装（libryzenadj.dll / ryzenadj.exe 双通道） ----
import * as smu from "./libryzenadj.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 3099;
const execAsync = promisify(exec);

// ---- 管理员权限检测 ----
let runningAsAdmin = false;
try {
  execSync("net session 2>nul", { windowsHide: true });
  runningAsAdmin = true;
  console.log("[admin] 以管理员权限运行");
} catch {
  console.log("[admin] 非管理员模式，风扇读取需提权");
}

// ---- Express + HTTP ----
const app = express();
app.use(express.json());

// CORS
app.use((_req, res, next) => {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type");
  if (_req.method === "OPTIONS") return res.sendStatus(204);
  next();
});

// ---- Hardware: telemetry read ----
// 风扇平滑 — EMA滤波器
let _prevCpuFan = 0;
let _prevGpuFan = 0;
const FAN_EMA_ALPHA = 0.35;

/** 读取风扇转速 — 分别调用 ec_reader 读取 CPU 和 GPU 风扇 (避免 EC 状态干扰) */
async function readFanSpeed() {
  const ecReader = path.join(__dirname, "tools", "ec_reader.exe");
  const delay = (ms) => new Promise((r) => setTimeout(r, ms));

  try {
    const [cpuOut, gpuOut] = await Promise.all([
      execAsync(`"${ecReader}" cpu`, { timeout: 8000, windowsHide: true }),
      delay(200).then(() => execAsync(`"${ecReader}" gpu`, { timeout: 8000, windowsHide: true })),
    ]);

    const cpuRaw = parseInt(cpuOut.stdout?.trim()) || 0;
    const gpuRaw = parseInt(gpuOut.stdout?.trim()) || 0;

    // CPU 和 GPU 独立处理，一个失败不拖累另一个
    let cpu = _prevCpuFan;
    let gpu = _prevGpuFan;

    if (cpuRaw > 0 && cpuRaw < 9999) {
      cpu = cpuRaw;
      if (_prevCpuFan > 0) {
        const diff = Math.abs(cpu - _prevCpuFan);
        cpu = Math.round(diff > _prevCpuFan * 0.4
          ? _prevCpuFan + (cpu - _prevCpuFan) * 0.15
          : _prevCpuFan + (cpu - _prevCpuFan) * FAN_EMA_ALPHA);
      }
      _prevCpuFan = cpu;
    }

    if (gpuRaw > 0 && gpuRaw < 9999) {
      gpu = gpuRaw;
      if (_prevGpuFan > 0) {
        const diff = Math.abs(gpu - _prevGpuFan);
        gpu = Math.round(diff > _prevGpuFan * 0.4
          ? _prevGpuFan + (gpu - _prevGpuFan) * 0.15
          : _prevGpuFan + (gpu - _prevGpuFan) * FAN_EMA_ALPHA);
      }
      _prevGpuFan = gpu;
    }

    // 大风扇=CPU(max 4400), 小风扇=GPU(max 8200)
    return { fanLargeRpm: cpu, fanSmallRpm: gpu, fanLargeMax: 4400, fanSmallMax: 8200 };
  } catch (err) {
    console.log(`[fan] 读取失败: ${err.message}`);
  }

  // 出错时沿用上次有效值
  return { fanLargeRpm: _prevCpuFan, fanSmallRpm: _prevGpuFan, fanLargeMax: 4400, fanSmallMax: 8200 };
}

// ---- CPU 温度通过 EC 寄存器 0x70 读取 ----
let _prevCpuTemp = 0;
async function readCpuTemp() {
  const ecReader = path.join(__dirname, "tools", "ec_reader.exe");
  try {
    const { stdout } = await execAsync(`"${ecReader}" temp`, { timeout: 5000, windowsHide: true });
    const temp = parseInt(stdout?.trim()) || 0;
    if (temp > 10 && temp < 110) {
      _prevCpuTemp = temp;
      return temp;
    }
  } catch { /* fallback */ }
  return _prevCpuTemp;
}

// ---- GPU 数据通过 nvidia-smi 读取 ----
let _prevGpuTemp = 0;
async function readGpuData() {
  try {
    const { stdout } = await execAsync(
      `nvidia-smi --query-gpu=temperature.gpu,utilization.gpu,clocks.current.graphics,memory.total,memory.used --format=csv,noheader,nounits`,
      { timeout: 5000, windowsHide: true }
    );
    const parts = stdout.trim().split(", ");
    if (parts.length >= 5) {
      const gpuTemp = parseInt(parts[0]) || 0;
      const gpuUsage = parseInt(parts[1]) || 0;
      const gpuFreq = parseInt(parts[2]) || 0;
      const vramTotal = parseInt(parts[3]) || 0;
      const vramUsed = parseInt(parts[4]) || 0;

      if (gpuTemp > 0) _prevGpuTemp = gpuTemp;

      return {
        gpuUsage,
        gpuTemp: gpuTemp > 0 ? gpuTemp : _prevGpuTemp,
        gpuFreq: +(gpuFreq / 1000).toFixed(1),
        gpuVram: Math.round(vramTotal / 1024),
        gpuVramUsed: Math.round(vramUsed / 1024 * 10) / 10, // 保留一位小数
      };
    }
  } catch { /* nvidia-smi 不可用或非 NVIDIA 显卡 */ }
  return { gpuUsage: 0, gpuTemp: _prevGpuTemp, gpuFreq: 0, gpuVram: 0, gpuVramUsed: 0 };
}

// ---- CPU 频率通过 WMI 读取 (比 systeminformation 的固定 2.4GHz 准确) ----
async function readCpuFreq() {
  try {
    const { stdout } = await execAsync(
      `powershell -NoProfile -Command "(Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation | Where-Object Name -Match '_Total').PercentProcessorPerformance"`,
      { timeout: 5000, windowsHide: true }
    );
    const perfPct = parseInt(stdout.trim()) || 0;
    if (perfPct > 0) {
      // PercentProcessorPerformance 是相对于基础频率的百分比
      // 8940HX 基础 2.4GHz, 假设 100% = 2.4GHz
      return +(2.4 * (perfPct / 100)).toFixed(1);
    }
  } catch { /* fallback */ }
  return 0;
}

// ---- 内存频率通过 WMI 读取实际运行频率 ----
async function readMemoryFreq() {
  try {
    const { stdout } = await execAsync(
      `powershell -NoProfile -Command "(Get-CimInstance Win32_PerfFormattedData_Counters_MemoryPerformance -ErrorAction SilentlyContinue).MemoryClock"`,
      { timeout: 5000, windowsHide: true }
    );
    let freq = parseInt(stdout.trim());
    if (freq > 0) return freq;
    // 回退到配置频率
    const { stdout: cfg } = await execAsync(
      `powershell -NoProfile -Command "(Get-CimInstance Win32_PhysicalMemory | Select-Object -First 1).Speed"`,
      { timeout: 5000, windowsHide: true }
    );
    return parseInt(cfg.trim()) || 0;
  } catch { return 0; }
}

async function gatherTelemetry() {
  const [cpu, mem, gpuData, fsSize, fan, cpuFreq, cpuTemp, memFreq] = await Promise.all([
    si.currentLoad(),
    si.mem(),
    readGpuData(),
    si.fsSize(),
    readFanSpeed(),
    readCpuFreq(),
    readCpuTemp(),
    readMemoryFreq(),
  ]);

  return {
    cpuUsage: Math.round(cpu.currentLoad),
    cpuTemp: cpuTemp,
    cpuFreq: cpuFreq > 0 ? cpuFreq : 2.4,
    cpuCores: cpu.cpus?.length ?? 0,
    gpuUsage: gpuData.gpuUsage,
    gpuTemp: gpuData.gpuTemp,
    gpuFreq: gpuData.gpuFreq,
    gpuVram: gpuData.gpuVram,
    gpuVramUsed: gpuData.gpuVramUsed,
    memoryUsage: Math.round((mem.used / mem.total) * 100),
    memoryTotalGB: Math.round(mem.total / (1024 ** 3)),
    memoryFreq: memFreq,
    diskUsage: Math.round(fsSize.reduce((s,d)=>s+(d.use||0),0) / Math.max(1,fsSize.length)),
    diskTotalGB: Math.round(fsSize.reduce((s,d)=>s+(d.size||0),0) / (1024**3)),
    diskFreeGB: Math.round(fsSize.reduce((s,d)=>s+((d.size||0)-(d.used||0)),0) / (1024**3)),
    fanLargeRpm: fan.fanLargeRpm,
    fanSmallRpm: fan.fanSmallRpm,
    fanLargeMax: fan.fanLargeMax,
    fanSmallMax: fan.fanSmallMax,
    admin: runningAsAdmin,
  };
}

// ---- REST API routes ----
app.get("/api/telemetry", async (_req, res) => {
  try {
    const data = await gatherTelemetry();
    res.json(data);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.get("/api/ryzenadj/info", async (_req, res) => {
  try {
    const result = await smu.getInfo();
    res.json(result);
  } catch (err) {
    res.status(500).json({ ok: false, error: err.message });
  }
});

app.post("/api/uxtu/apply", async (req, res) => {
  try {
    // 兼容两种格式:
    //   前端格式: { chipset, profile, params: { cpuLongPptW, cpuShortPptW, cpuTempLimitC, ... } }
    //   旧格式:   { limits: { cpu: { pptLimitW, tempLimitC }, gpu: { pptLimitW, tempLimitC, clockLimitMhz } } }
    const { limits, params } = req.body;

    let cpuPpt, cpuTemp, gpuPpt;

    if (limits) {
      cpuPpt = limits?.cpu?.pptLimitW;
      cpuTemp = limits?.cpu?.tempLimitC;
      gpuPpt = limits?.gpu?.pptLimitW;
    } else if (params) {
      cpuPpt = params.cpuLongPptW;
      cpuTemp = params.cpuTempLimitC;
      gpuPpt = params.gpuPptLimitW;
    }

    const result = await smu.setLimits({ cpuPpt, cpuTemp, gpuPpt });
    res.json(result);
  } catch (err) {
    res.status(500).json({ ok: false, error: err.message });
  }
});

app.get("/api/smu/api-type", (_req, res) => {
  res.json({ apiType: smu.getApiType() });
});


app.post("/api/system/settings", async (req, res) => {
  try {
    const { key, value } = req.body;
    console.log(`[system] 设置 ${key}=${value}`);

    switch (key) {
      case "dGpuDirect":
      case "fanBoost":
      case "touchpadLock":
        console.log(`[system] ${key}=${value} — 废弃 LLT WMI，此硬件不支持`);
        return res.json({ ok: false, error: "此硬件不支持" });
        break;

      default:
        console.log(`[system] ${key}=${value} — 未绑定 WMI 操作`);
    }

    res.json({ ok: true, message: "设置已下发" });
  } catch (err) {
    res.status(500).json({ ok: false, error: err.message });
  }
});

// ---- 风扇控制 (Lenovo WMI) ----
// Lenovo 笔记本风扇控制通过 WMI LENOVO_FAN_METHOD 实现，而非 EC 寄存器。
//   Fan_Set_FullSpeed(Status=1)  → 全速模式
//   Fan_Set_FullSpeed(Status=0)  → 自动模式 (恢复固件风扇曲线)
//   Fan_Set_Table(FanTable)      → 自定义风扇曲线 (需 64 字节数组)
app.post("/api/fan/full-speed", async (_req, res) => {
  console.log("[fan] 全速模式 — 废弃 LLT WMI，此硬件不支持");
  return res.json({ ok: false, error: "此硬件不支持" });
});

app.get("/api/discover", async (_req, res) => {
  // 废弃：Lenovo Legion WMI 类探测已移除（宝龙达模具不支持）
  res.json({ legacy: "Lenovo Legion WMI not supported on this hardware" });
});

// ---- 仪表盘默认配置 (服务端持久化) ----
const CONFIG_PATH = path.join(__dirname, "config", "dashboard-default.json");

app.get("/api/default-config", (_req, res) => {
  const data = safeRead(CONFIG_PATH, { order: [], hidden: [] });
  res.json(data);
});

app.post("/api/default-config", (req, res) => {
  try {
    const { order, hidden } = req.body;
    if (!Array.isArray(order)) {
      return res.status(400).json({ error: "order 必须是数组" });
    }
    const payload = { order, hidden: Array.isArray(hidden) ? hidden : [] };
    atomicWrite(CONFIG_PATH, payload);
    console.log("[config] 默认配置已保存");
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ---- 自定义参数持久化 ----
const CUSTOM_PARAMS_PATH = path.join(__dirname, "config", "custom-params.json");

app.get("/api/custom-params", (_req, res) => {
  const data = safeRead(CUSTOM_PARAMS_PATH, {});
  res.json(data);
});

app.post("/api/custom-params", (req, res) => {
  try {
    const payload = req.body;
    atomicWrite(CUSTOM_PARAMS_PATH, payload);
    console.log("[config] 自定义参数已保存");
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ---- UI 状态持久化（卡片排序 + 隐藏状态） ----
const UI_STATE_PATH = path.join(__dirname, "config", "ui-state.json");

app.get("/api/ui-state", (_req, res) => {
  const data = safeRead(UI_STATE_PATH, { cardOrder: [], hiddenCards: [] });
  res.json(data);
});

app.post("/api/ui-state", (req, res) => {
  try {
    const payload = req.body;
    atomicWrite(UI_STATE_PATH, payload);
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ================================================================
// GET /debug — Node.js 后端调试页
// ================================================================
app.get("/debug", (_req, res) => {
  const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Node.js Debug</title><style>
body{background:#0d1117;color:#c9d1d9;font:13px/1.5 monospace;padding:16px;max-width:900px;margin:0 auto}
h2{color:#58a6ff;border-bottom:1px solid #30363d;padding-bottom:6px;margin:24px 0 12px}
.section{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:12px;margin-bottom:16px}
label{display:inline-block;min-width:140px;color:#8b949e;margin:4px 0}
input,select,textarea{background:#0d1117;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 8px;font:12px monospace}
input{width:80px}input[type=number]{width:90px}
button{background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:4px}
button:hover{background:#30363d}
button.ok{background:#1c4a2b;border-color:#2ea043}
button.warn{background:#542c03;border-color:#d29922}
button.danger{background:#632f2f;border-color:#da3633}
pre{background:#0d1117;border:1px solid #30363d;border-radius:4px;padding:8px;overflow:auto;max-height:300px;font:12px monospace;margin:8px 0}
table{width:100%;border-collapse:collapse;margin:8px 0}
td,th{border:1px solid #30363d;padding:4px 8px;text-align:left}
th{color:#8b949e;font-weight:400}
td{color:#c9d1d9}
.badge{display:inline-block;padding:1px 6px;border-radius:10px;font-size:11px;background:#30363d}
.badge.on{background:#1c4a2b;color:#3fb950}
.badge.off{background:#632f2f;color:#f85149}
.row{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin:6px 0}
.spacer{flex:1}
</style></head><body>
<h2>🧪 Node.js 后端调试</h2>
<p style="color:#8b949e">端口 3099 &middot; 管理员: <span class="badge on">是</span></p>

<!-- SMU 信息 -->
<div class="section">
  <h2>SMU RyzenAdj 信息</h2>
  <div class="row"><button class="ok" onclick="fetchSmuInfo()">查询 SMU 状态</button></div>
  <pre id="smuInfo">点击按钮查询</pre>
</div>

<!-- SMU 参数下发 -->
<div class="section">
  <h2>SMU 参数下发</h2>
  <div class="row">
    <label>CPU 长时功耗 (W)</label><input type="number" id="cpuLongPptW" value="65" min="0" step="1">
    <label>CPU 温度墙 (°C)</label><input type="number" id="cpuTempLimitC" value="90" min="0" step="1">
    <label>GPU 功耗墙 (W)</label><input type="number" id="gpuPptLimitW" value="115" min="0" step="1">
  </div>
  <div class="row"><button class="ok" onclick="applySmu()">下发参数</button><span id="smuResult" style="color:#8b949e"></span></div>
</div>

<!-- 自定义参数 -->
<div class="section">
  <h2>自定义参数 (custom-params)</h2>
  <div class="row"><button class="ok" onclick="loadCustomParams()">读取</button><button onclick="saveCustomParams()">保存</button></div>
  <textarea id="customParams" rows="6" style="width:100%;max-width:100%;font:12px monospace">点击"读取"加载</textarea>
</div>

<!-- 系统开关 -->
<div class="section">
  <h2>系统开关 (WMI)</h2>
  <div class="row">
    <select id="sysKey" style="width:200px">
      <option value="dGpuDirect">独显直连 (dGpuDirect)</option>
      <option value="fanBoost">强冷模式 (fanBoost)</option>
      <option value="fnLock">Fn 锁 (fnLock)</option>
      <option value="touchpadLock">触摸板锁 (touchpadLock)</option>
      <option value="osdDisabled">OSD 显示 (osdDisabled)</option>
      <option value="kbBrightnessLevel">键盘灯 (kbBrightnessLevel 0-3)</option>
    </select>
    <input type="number" id="sysValue" value="1" min="0" max="3" step="1" style="width:60px">
    <button class="ok" onclick="applySystemSetting()">发送</button>
    <span id="sysResult" style="color:#8b949e"></span>
  </div>
</div>

<!-- 风扇全速 -->
<div class="section">
  <h2>风扇控制</h2>
  <div class="row">
    <button class="ok" onclick="setFanFullSpeed(true)">开启全速</button>
    <button class="danger" onclick="setFanFullSpeed(false)">恢复自动</button>
    <span id="fanResult" style="color:#8b949e"></span>
  </div>
</div>

<!-- WMI 探测 -->
<div class="section">
  <h2>WMI 类探测</h2>
  <div class="row"><button class="ok" onclick="discoverWmi()">探测 WMI</button></div>
  <pre id="wmiResult">点击按钮探测</pre>
</div>

<script>
async function api(url, opts) {
  try {
    const r = await fetch(url, opts);
    const d = await r.json();
    return { ok: r.ok, data: d };
  } catch(e) {
    return { ok: false, data: { error: e.message } };
  }
}
function show(id, html) { document.getElementById(id).innerHTML = html; }

async function fetchSmuInfo() {
  show("smuInfo", "查询中...");
  const r = await api("/api/ryzenadj/info");
  if (!r.ok) { show("smuInfo", "错误: " + (r.data.error || r.data)); return; }
  if (!r.data.ok) { show("smuInfo", JSON.stringify(r.data, null, 2)); return; }
  let tbl = "<table><tr><th>参数</th><th>值</th><th>单位</th></tr>";
  for (const k in r.data.data) {
    const v = r.data.data[k];
    tbl += "<tr><td>" + k + "</td><td>" + (v.value||"") + "</td><td>" + (v.unit||"") + "</td></tr>";
  }
  tbl += "</table>";
  show("smuInfo", tbl);
}

async function applySmu() {
  const params = {
    cpuLongPptW: +document.getElementById("cpuLongPptW").value,
    cpuTempLimitC: +document.getElementById("cpuTempLimitC").value,
    gpuPptLimitW: +document.getElementById("gpuPptLimitW").value,
  };
  document.getElementById("smuResult").textContent = "下发中...";
  const r = await api("/api/uxtu/apply", {
    method: "POST",
    headers: {"Content-Type":"application/json"},
    body: JSON.stringify({ params }),
  });
  document.getElementById("smuResult").textContent = r.ok ? "✅ " + (r.data.message||"OK") : "❌ " + (r.data.error||JSON.stringify(r.data));
}

async function loadCustomParams() {
  const r = await api("/api/custom-params");
  if (!r.ok) { show("customParams", "错误: " + JSON.stringify(r.data)); return; }
  show("customParams", JSON.stringify(r.data, null, 2));
}

async function saveCustomParams() {
  const ta = document.getElementById("customParams");
  try {
    const data = JSON.parse(ta.value);
    const r = await api("/api/custom-params", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: JSON.stringify(data),
    });
    ta.style.borderColor = r.ok ? "#2ea043" : "#da3633";
    setTimeout(() => ta.style.borderColor = "", 1000);
  } catch(e) {
    ta.style.borderColor = "#da3633";
    alert("JSON 格式错误: " + e.message);
  }
}

async function applySystemSetting() {
  const key = document.getElementById("sysKey").value;
  const value = +document.getElementById("sysValue").value;
  document.getElementById("sysResult").textContent = "发送中...";
  const r = await api("/api/system/settings", {
    method: "POST",
    headers: {"Content-Type":"application/json"},
    body: JSON.stringify({ key, value }),
  });
  document.getElementById("sysResult").textContent = r.ok ? "✅ " + (r.data.message||"OK") : "❌ " + (r.data.error||JSON.stringify(r.data));
}

async function setFanFullSpeed(enabled) {
  document.getElementById("fanResult").textContent = "执行中...";
  const r = await api("/api/fan/full-speed", {
    method: "POST",
    headers: {"Content-Type":"application/json"},
    body: JSON.stringify({ enabled }),
  });
  document.getElementById("fanResult").textContent = r.ok ? "✅ " + (r.data.message||"OK") : "❌ " + (r.data.error||JSON.stringify(r.data));
}

async function discoverWmi() {
  show("wmiResult", "探测中...");
  const r = await api("/api/discover");
  if (!r.ok) { show("wmiResult", "错误: " + JSON.stringify(r.data)); return; }
  let tbl = "<table><tr><th>WMI 类</th><th>可用</th></tr>";
  for (const k in r.data) {
    tbl += "<tr><td>" + k + "</td><td><span class=\\"badge " + (r.data[k] ? "on" : "off") + "\\">" + (r.data[k] ? "✓" : "✗") + "</span></td></tr>";
  }
  tbl += "</table>";
  show("wmiResult", tbl);
}
</script></body></html>`;
  res.send(html);
});

// ---- HTTP server + WebSocket ----
const server = createServer(app);
const wss = new WebSocketServer({ server });

wss.on("connection", (ws) => {
  console.log("[ws] 客户端已连接");

  let timer;
  const push = async () => {
    try {
      const data = await gatherTelemetry();
      if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(data));
    } catch { /* ignore */ }
  };

  push(); // 立即推送一次
  timer = setInterval(push, 2000);

  ws.on("close", () => {
    clearInterval(timer);
    console.log("[ws] 客户端断开");
  });
});

server.listen(PORT, () => {
  console.log(`[server] 后端已启动 http://localhost:${PORT}`);
  console.log(`[server] WebSocket: ws://localhost:${PORT}`);
  console.log(`[server] 管理员: ${runningAsAdmin ? "是" : "否"}`);
});
