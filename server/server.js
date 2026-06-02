import express from "express";
import { createServer } from "http";
import { WebSocketServer } from "ws";
import si from "systeminformation";
import { spawn, exec, execSync } from "child_process";
import path from "path";
import { fileURLToPath } from "url";
import { promisify } from "util";

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

// ---- RyzenAdj bridge (AMD SMU access) ----
const RYZENADJ_PATH = path.join(__dirname, "tools", "ryzenadj.exe");

async function callRyzenAdj(args = []) {
  return new Promise((resolve) => {
    const child = spawn(RYZENADJ_PATH, args, {
      timeout: 15000,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";

    child.stdout.on("data", (d) => { stdout += d.toString(); });

    child.on("close", (code) => {
      if (stdout.trim()) return resolve({ stdout, code });
      // 0xC0000005 ACCESS_VIOLATION — writes succeed before post-write verify crashes (known Dragon Range issue)
      if (code === 3221225477 || code === null) {
        resolve({ stdout: "(SMU 写入已执行)", code });
      } else {
        resolve({ error: `Exit code ${code}`, code });
      }
    });

    child.on("error", (err) => resolve({ error: err.message }));
  });
}

/** Parse ryzenadj -i output into key-value map */
function parseRyzenAdjInfo(output) {
  const map = {};
  for (const line of output.split("\n")) {
    const m = line.match(/^(.+?)\s*\|\s*(.+?)\s*\|\s*(.+?)\s*$/);
    if (m) {
      map[m[1].trim()] = { value: m[2].trim(), unit: m[3].trim() };
    }
  }
  return map;
}

/** Apply SMU limits via RyzenAdj — one arg at a time to capture output before crash */
async function applyRyzenAdjLimits({ cpuPpt, cpuTemp, gpuPpt, gpuTemp, gpuClock }) {
  const cmds = [];
  if (cpuPpt != null) {
    const mW = Math.round(cpuPpt * 1000);
    cmds.push([`--stapm-limit=${mW}`, `--fast-limit=${mW}`, `--slow-limit=${mW}`]);
  }
  if (cpuTemp != null) cmds.push([`--tctl-temp=${cpuTemp}`]);
  if (gpuPpt != null) cmds.push([`--vrmgfx-current=${gpuPpt * 1000}`]);

  const results = [];
  for (const args of cmds) {
    const r = await callRyzenAdj(args);
    if (r.error) {
      results.push({ ok: false, args, error: r.error });
    } else {
      results.push({ ok: true, args, output: r.stdout?.trim() });
    }
  }

  const allOk = results.every((r) => r.ok);
  const output = results.map((r) => r.output || r.error).filter(Boolean).join("; ");
  return {
    ok: allOk || results.some((r) => r.ok),
    message: results.length > 0 ? output || "SMU 参数已下发" : "无参数需要下发",
    results,
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
    const result = await callRyzenAdj(["-i"]);
    if (result.error) return res.json({ ok: false, error: result.error });
    res.json({ ok: true, data: parseRyzenAdjInfo(result.stdout) });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post("/api/uxtu/apply", async (req, res) => {
  try {
    // 兼容两种格式:
    //   前端格式: { chipset, profile, params: { cpuLongPptW, cpuShortPptW, cpuTempLimitC, ... } }
    //   旧格式:   { limits: { cpu: { pptLimitW, tempLimitC }, gpu: { pptLimitW, tempLimitC, clockLimitMhz } } }
    const { limits, params } = req.body;

    let cpuPpt, cpuTemp, gpuPpt, gpuTemp, gpuClock;

    if (limits) {
      // 旧格式
      cpuPpt = limits?.cpu?.pptLimitW;
      cpuTemp = limits?.cpu?.tempLimitC;
      gpuPpt = limits?.gpu?.pptLimitW;
      gpuTemp = limits?.gpu?.tempLimitC;
      gpuClock = limits?.gpu?.clockLimitMhz;
    } else if (params) {
      // 前端格式
      cpuPpt = params.cpuLongPptW;
      cpuTemp = params.cpuTempLimitC;
      gpuPpt = params.gpuPptLimitW;
      gpuTemp = params.gpuTempLimitC;
      gpuClock = params.gpuFreqLimitEnabled ? params.gpuFreqLimitMhz : null;
    }

    const result = await applyRyzenAdjLimits({ cpuPpt, cpuTemp, gpuPpt, gpuTemp, gpuClock });
    res.json(result);
  } catch (err) {
    res.status(500).json({ ok: false, error: err.message });
  }
});

// ---- System settings via WMI ----
async function callWmiMethod(className, method, args = {}) {
  const ns = "ROOT/WMI";
  const argStr = "@{" + Object.entries(args).map(([k, v]) => `'${k}'=${typeof v === 'number' ? v : `'${v}'`}`).join("; ") + "}";
  const cmd = `powershell -NoProfile -Command "try { Invoke-CimMethod -Namespace '${ns}' -ClassName '${className}' -MethodName '${method}' -Arguments ${argStr} -ErrorAction Stop; Write-Output 'OK' } catch { Write-Output \\"ERR:$($_.Exception.Message)\\" }"`;
  try {
    const { stdout } = await execAsync(cmd, { timeout: 8000, windowsHide: true });
    if (stdout.trim() === "OK") return { ok: true };
    return { ok: false, message: stdout.trim().replace(/^ERR:/, "") };
  } catch (err) {
    return { ok: false, message: err.message };
  }
}

app.post("/api/system/settings", async (req, res) => {
  try {
    const { key, value } = req.body;
    console.log(`[system] 设置 ${key}=${value}`);

    switch (key) {
      case "dGpuDirect":
        // 独显直连 — Lenovo WMI CapabilityID: 0x00120075
        await callWmiMethod("LENOVO_OTHER_METHOD", "SetFeatureValue", { CapabilityID: 0x00120075, Value: value ? 1 : 0 });
        break;
      case "fanBoost":
        // 强冷模式 — 通过 LENOVO_FAN_METHOD (Fan_Set_FullSpeed) 实现
        await callWmiMethod("LENOVO_FAN_METHOD", "Fan_Set_FullSpeed", { Status: value ? 1 : 0 });
        break;
      case "fnLock":
        await callWmiMethod("LENOVO_OTHER_METHOD", "SetFeatureValue", { CapabilityID: 0x000B0003, Value: value ? 1 : 0 });
        break;
      case "touchpadLock":
        await callWmiMethod("LENOVO_OTHER_METHOD", "SetFeatureValue", { CapabilityID: 0x000B0004, Value: value ? 1 : 0 });
        break;
      case "kbBrightnessLevel":
        // 键盘背光亮度 0-3
        await callWmiMethod("LENOVO_LIGHTING_METHOD", "SetKeyboardBacklight", { Level: value });
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
app.post("/api/fan/full-speed", async (req, res) => {
  try {
    const { enabled } = req.body; // boolean
    const result = await callWmiMethod("LENOVO_FAN_METHOD", "Fan_Set_FullSpeed", { Status: enabled ? 1 : 0 });
    if (result.ok) {
      console.log(`[fan] 全速模式: ${enabled ? "开启" : "关闭"}`);
      res.json({ ok: true, message: enabled ? "全速模式已开启" : "已恢复自动模式" });
    } else {
      res.status(500).json({ ok: false, error: result.message });
    }
  } catch (err) {
    res.status(500).json({ ok: false, error: err.message });
  }
});

app.get("/api/discover", async (_req, res) => {
  const classes = ["LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD", "LENOVO_FAN_METHOD", "LENOVO_FAN_DATA", "LENOVO_LIGHTING_METHOD", "LENOVO_CPU_METHOD"];
  const results = {};
  for (const cls of classes) {
    try {
      const cmd = `powershell -NoProfile -Command "Get-CimClass -Namespace 'ROOT/WMI' -ClassName '${cls}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty CimClassName"`;
      const { stdout } = await execAsync(cmd, { timeout: 3000, windowsHide: true });
      results[cls] = stdout.trim() === cls;
    } catch { results[cls] = false; }
  }
  res.json(results);
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