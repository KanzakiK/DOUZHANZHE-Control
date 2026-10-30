const BACKEND = "";

// 核心数 → 百分比换算基数（Intel 251HX=18，其余默认 16），启动时由平台信息校准
let _maxCoresForPercent = 16;

export function setMaxCoresForPercent(n) {
  if (Number.isFinite(n) && n > 0) _maxCoresForPercent = n;
}

export function coreToPercent(coreCount) {
  return coreCount > 0 ? Math.round(coreCount / _maxCoresForPercent * 100) : 100;
}

// 统一日志: 同时写入后端 AppLog (tag=UI) 和浏览器 console
export function log(tag, msg) {
  console.log(`[${tag}]`, msg);
  fetch(`${BACKEND}/api/log`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ tag, msg }),
  }).catch(() => {});
}

// ── SMU 重发取消机制 ──
// 快速切模式时，新 dispatch 必须取消上一轮 dispatch 遗留的 setTimeout，
// 否则旧闭包里的 mode/overrides 会覆盖新模式的 SMU 值
let _smuResendTimers = [];   // 存储 setTimeout 返回的 timer ID
let _smuDispatchGen = 0;     // generation 计数器，用于二次校验

function cancelSmuResendTimers() {
  for (const id of _smuResendTimers) clearTimeout(id);
  _smuResendTimers = [];
}

// C# HAL thermal_mode value mapping
const thermalModeMap = {
  silent: 2,
  office: 0,
  gaming: 3,
  beast: 1,
};

// C# HAL power_plan value mapping
export const powerPlanHALMap = {
  efficiency: 2,
  balance: 0,
  performance: 1,
};

// 反向映射：HAL 整数 → 前端字符串 id（flattenBackendOverrides 用它把后端下发的
// powerPlan 整数归一成 id，避免 uxtuParams.cpuPowerPlan 变成整数导致选中不高亮）
const _halToPowerPlanId = Object.entries(powerPlanHALMap).reduce((acc, [id, hal]) => {
  acc[hal] = id;
  return acc;
}, {});

// 内置配置显式展示顺序（用于 Dashboard 配置 dock / 控制面板配置下拉）。
// 注意：这些是「配置 id」（cfg- 前缀），与 EC 性能模式裸名(silent/office/…) 区分。
// 后端 /api/profiles 的顺序由被持久化的 .index.json 决定、改源码不生效，
// 所以前端用这里的固定顺序渲染，确保「斗战(功耗最高)排在最后」在界面稳定一致。
export const BUILTIN_MODE_ORDER = ['cfg-silent', 'cfg-office', 'cfg-beast', 'cfg-gaming'];

// 配置 id → 性能模式裸名（唯一解包点，供 MODE_FAN_DEFAULTS/FAN_RANGES 等按性能模式取值）
export function perfModeOf(profile) {
  return profile?.thermalMode || 'office';
}

// 由「配置 id（或裸性能模式名，兼容旧值）」解析出性能模式裸名。
// profileList 用于把 cfg- 配置 id 查成其 thermalMode；裸名则原样返回。
export function resolvePerfMode(mode, profiles) {
  if (!mode) return 'office';
  if (mode === 'silent' || mode === 'office' || mode === 'beast' || mode === 'gaming') return mode;
  const pf = (profiles || []).find(p => p.id === mode);
  return pf?.thermalMode || 'office';
}

// 按 BUILTIN_MODE_ORDER 排序内置配置（未知 id 置末尾，保持稳定）
export function sortBuiltinProfiles(profiles) {
  return [...profiles].sort((a, b) => {
    const ia = BUILTIN_MODE_ORDER.indexOf(a.id);
    const ib = BUILTIN_MODE_ORDER.indexOf(b.id);
    const va = ia === -1 ? BUILTIN_MODE_ORDER.length : ia;
    const vb = ib === -1 ? BUILTIN_MODE_ORDER.length : ib;
    return va - vb;
  });
}

// C# HAL 硬件控制 (kb_light, fn_lock, num_lock, caps_lock, thermal_mode)
export async function applyHardwareControl(target, value, mode) {
  const url = mode ? `/api/control?mode=${mode}` : `/api/control`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ target, value }),
  });
  if (!res.ok) throw new Error(`HAL 返回 ${res.status}`);
  return res.json();
}


// HTTP 获取初始遥测数据（用于方案 C：先 HTTP 后 WebSocket）
export async function fetchTelemetry() {
  const res = await fetch(`/api/telemetry`);
  if (!res.ok) throw new Error(`telemetry returned ${res.status}`);
  return res.json();
}
export function createTelemetrySocket(onData, onError) {
  // WebSocket 跟随当前页面地址（开发 3101 / 安装版 3100）
  const wsProto = location.protocol === "https:" ? "wss:" : "ws:";
  const ws = new WebSocket(`${wsProto}//${location.host}/ws`);
  ws.onmessage = (e) => {
    try {
      onData(JSON.parse(e.data));
    } catch { /* ignore */ }
  };
  ws.onerror = () => onError?.();
  // 不在此处自动重连，由调用方 (useControlState) 管理生命周期
  return ws;
}

const GPU_BASE_CLOCK = 2750; // RTX 5060 典型 boost 频率 (nvidia-smi 无法读取，用户提供)

// GPU 控制: action = "limit-max" | "lock-exact" | "reset-clocks" | "reset-memory-clocks"
export async function applyGpuControl(action, value, min, max, mode) {
  const body = value !== undefined
    ? { action, min, max, value }
    : { action };
  const url = mode ? `/api/gpu/set?mode=${mode}` : `/api/gpu/set`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`GPU 控制返回 ${res.status}`);
  return res.json();
}

// NVAPI 状态 (超频偏移/功率/温度限制)
export async function fetchNvapiStatus() {
  const res = await fetch(`/api/nvapi/status`);
  if (!res.ok) throw new Error(`NVAPI 状态返回 ${res.status}`);
  return res.json();
}

// NVAPI 超频: coreOffsetMhz / memOffsetMhz 偏移值
export async function applyNvapiOverclock(coreOffsetMhz, memOffsetMhz, mode) {
  const url = mode ? `/api/nvapi/overclock?mode=${mode}` : `/api/nvapi/overclock`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ coreOffsetMhz, memOffsetMhz }),
  });
  if (!res.ok) throw new Error(`NVAPI 超频返回 ${res.status}`);
  return res.json();
}

// NVAPI 温度限制
export async function applyNvapiThermalLimit(tempC, mode) {
  const url = mode ? `/api/nvapi/thermal-limit?mode=${mode}` : `/api/nvapi/thermal-limit`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ tempC }),
  });
  if (!res.ok) throw new Error(`NVAPI 温度限制返回 ${res.status}`);
  return res.json();
}


// 各模式风扇转速合法区间（数据来源：后端 ModeFanRanges）
// /api/fan/set-target 不经过 RouteMode，滑动条需按模式限制
const FAN_RANGES = {
  silent: { largeMin: 1900, largeMax: 2900, smallMin: 1700, smallMax: 6400 },
  office: { largeMin: 2600, largeMax: 3500, smallMin: 5900, smallMax: 6900 },
  gaming: { largeMin: 4000, largeMax: 4400, smallMin: 7500, smallMax: 8200 },
  beast:  { largeMin: 3200, largeMax: 3800, smallMin: 6400, smallMax: 7200 },
};

// 全范围（散热曲线跨模式使用）
export const FULL_FAN_RANGE = {
  largeMin: 1900, largeMax: 4400,
  smallMin: 1700, smallMax: 8200,
};

// 全量参数默认值 (兆底，用于 UI 层显示)
// 注意：风扇转速随模式变化，这里只是占位符，实际使用时会被各模式默认值覆盖
export const FULL_PARAMS = {
  cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false,
  cpuTempLimitC: 80, cpuCoreLimit: 0, cpuPowerPlan: "balance", cpuVoltageOffset: 0,
  cpuLongPptW: 55, cpuShortPptW: 70,
  gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, gpuCoreFreqMhz: 2750,
  gpuMemFreqMhz: 0, gpuPptLimitW: 75, gpuTempLimitC: 87,
  ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0,
  fanLargeRpmTarget: 2900, fanSmallRpmTarget: 5200,  // 均衡模式默认
};

// 各模式的 EC 官方风扇默认转速（用于恢复默认时设置正确的 UI 值）
// 数据来源：docs/reference-consoles.md 官方预设表
export const MODE_FAN_DEFAULTS = {
  silent: { fanLargeRpmTarget: 2200, fanSmallRpmTarget: 2000 },
  office: { fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
  gaming: { fanLargeRpmTarget: 4300, fanSmallRpmTarget: 8000 },
  beast:  { fanLargeRpmTarget: 3500, fanSmallRpmTarget: 6900 },
};

// 参数合法范围 — 用于写入硬件前钳位
const PARAM_RANGES = {
  cpuTempLimitC: { min: 60, max: 100 },
  cpuLongPptW: { min: 15, max: 120 },
  cpuShortPptW: { min: 15, max: 140 },
  cpuVoltageOffset: { min: -30, max: 0 },
  cpuFreqLimitMhz: { min: 2000, max: 5500 },
  cpuCoreLimit: { min: 0, max: 18 },
  gpuTempLimitC: { min: 60, max: 100 },
  gpuPptLimitW: { min: 30, max: 150 },
  gpuCoreFreqMhz: { min: 1000, max: 3100 },
  gpuMemFreqMhz: { min: 0, max: 3 },
  ocCoreOffsetMhz: { min: -200, max: 300 },
  ocMemOffsetMhz: { min: -200, max: 300 },
};

// ── 散热曲线 (Fan Curve) ──

export async function fetchFanCurveStatus() {
  const res = await fetch("/api/fan-curve/status");
  if (!res.ok) throw new Error("Fan curve status returned " + res.status);
  return res.json();
}

export async function saveFanCurve(points, intervalMs, hysteresisC) {
  const res = await fetch("/api/fan-curve/save", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ points, intervalMs, hysteresisC }),
  });
  if (!res.ok) throw new Error("Fan curve save returned " + res.status);
  return res.json();
}

export async function startFanCurve(intervalMs, hysteresisC) {
  const res = await fetch("/api/fan-curve/start", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ intervalMs, hysteresisC }),
  });
  if (!res.ok) throw new Error("Fan curve start returned " + res.status);
  return res.json();
}

export async function stopFanCurve() {
  const res = await fetch("/api/fan-curve/stop", { method: "POST" });
  if (!res.ok) throw new Error("Fan curve stop returned " + res.status);
  return res.json();
}

export async function fetchRouteInfo() {
  const res = await fetch("/api/fan-curve/route-info");
  if (!res.ok) throw new Error("Route info returned " + res.status);
  return res.json();
}

function clampParam(key, value) {
  const r = PARAM_RANGES[key];
  if (!r) return value;
  return Math.max(r.min, Math.min(r.max, value));
}

export function getFanRange(mode) {
  return FAN_RANGES[mode] || FAN_RANGES.silent;
}

export async function applySmuSet(parameter, valueM, mode) {
  const url = mode ? `/api/smu/set?mode=${mode}` : `/api/smu/set`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ parameter, valueM }),
  });
  if (!res.ok) throw new Error("SMU return " + res.status);
  return res.json();
}

// ── CPU 性能控制 (powercfg 电源计划 API) ──

export async function fetchCpuPowerStatus() {
  const res = await fetch("/api/cpu/status");
  if (!res.ok) throw new Error("CPU status returned " + res.status);
  return res.json();
}

export async function setCpuFreqLimit(mhz, mode) {
  const url = mode ? `/api/cpu/freq-limit?mode=${mode}` : `/api/cpu/freq-limit`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mhz }),
  });
  if (!res.ok) throw new Error("CPU freq-limit returned " + res.status);
  return res.json();
}

export async function setCpuTurbo(enabled, mode) {
  const url = mode ? `/api/cpu/turbo?mode=${mode}` : `/api/cpu/turbo`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled }),
  });
  if (!res.ok) throw new Error("CPU turbo returned " + res.status);
  return res.json();
}

export async function setCpuCoreLimitPercent(percent, mode) {
  const url = mode ? `/api/cpu/core-limit?mode=${mode}` : `/api/cpu/core-limit`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ percent }),
  });
  if (!res.ok) throw new Error("CPU core-limit returned " + res.status);
  return res.json();
}

export async function resetCpuPower(mode) {
  const url = mode ? `/api/cpu/reset?mode=${mode}` : `/api/cpu/reset`;
  const res = await fetch(url, { method: "POST" });
  if (!res.ok) throw new Error("CPU reset returned " + res.status);
  return res.json();
}

// ── Overrides API (后端持久化) ──

export async function fetchOverrides() {
  const res = await fetch("/api/overrides");
  if (!res.ok) throw new Error("overrides fetch failed");
  return res.json(); // { mode, overrides }
}

export async function switchMode(mode) {
  log("switchMode", `→ ${mode}`);
  const res = await fetch("/api/overrides/switch", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode }),
  });
  if (!res.ok) throw new Error("mode switch failed");
  const data = await res.json();
  log("switchMode", `✓ ${Object.keys(data.overrides || {}).length} keys`);
  return data;
}

export async function syncOverrides(mode, overrides) {
  await fetch("/api/overrides/sync", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode, overrides }),
  }).catch(e => console.warn("[syncOverrides]", e));
}

export async function clearOverrides(mode, fields) {
  const res = await fetch("/api/overrides/clear", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode, fields }),
  });
  if (!res.ok) throw new Error("clear overrides failed");
  return res.json();
}

// ── 后端嵌套格式 → 前端扁平格式 ──
// 后端 PerformanceOverrides 类使用嵌套结构 (cpu.freqLimitMhz)，
// 但前端 reapplyOverrides 和 UI 使用扁平格式 (cpuFreqLimitMhz)。
export function flattenBackendOverrides(nested, maxCores = 16) {
  if (!nested) return {};
  const flat = {};

  // CPU
  if (nested.cpu) {
    if (nested.cpu.freqLimitMhz != null) {
      flat.cpuFreqLimitEnabled = nested.cpu.freqLimitMhz > 0;
      flat.cpuFreqLimitMhz = nested.cpu.freqLimitMhz;
    }
    if (nested.cpu.turboEnabled != null) {
      flat.cpuTurboDisabled = !nested.cpu.turboEnabled;
    }
    if (nested.cpu.coreLimitPercent != null && nested.cpu.coreLimitPercent > 0) {
      // 百分比 → 核心数（maxCores 默认 16，Intel 251HX 传 18）
      flat.cpuCoreLimit = Math.round(nested.cpu.coreLimitPercent * maxCores / 100);
    }
  }

  // GPU
  if (nested.gpu) {
    if (nested.gpu.coreFreqMhz != null) flat.gpuCoreFreqMhz = nested.gpu.coreFreqMhz;
    if (nested.gpu.freqLocked != null) {
      flat.gpuFreqLocked = nested.gpu.freqLocked;
      flat.gpuFreqLimitEnabled = nested.gpu.freqLocked;
      // freqLocked=true 时，coreFreqMhz 就是用户设定的频率限制值
      if (nested.gpu.freqLocked && nested.gpu.coreFreqMhz != null) {
        flat.gpuFreqLimitMhz = nested.gpu.coreFreqMhz;
      }
    }
    if (nested.gpu.memFreqLevel != null) flat.gpuMemFreqMhz = nested.gpu.memFreqLevel;
  }

  // NVAPI
  if (nested.nvapi) {
    if (nested.nvapi.ocCoreOffsetMhz != null) flat.ocCoreOffsetMhz = nested.nvapi.ocCoreOffsetMhz;
    if (nested.nvapi.ocMemOffsetMhz != null) flat.ocMemOffsetMhz = nested.nvapi.ocMemOffsetMhz;
    if (nested.nvapi.powerLimitW != null) flat.nvapiPowerLimitW = nested.nvapi.powerLimitW;
    if (nested.nvapi.thermalLimitC != null) flat.gpuTempLimitC = nested.nvapi.thermalLimitC;
  }

  // SMU
  if (nested.smu) {
    if (nested.smu.stapmLimitW != null) flat.cpuLongPptW = nested.smu.stapmLimitW;
    if (nested.smu.shortPowerLimitW != null) flat.cpuShortPptW = nested.smu.shortPowerLimitW;
    if (nested.smu.tempLimitC != null) flat.cpuTempLimitC = nested.smu.tempLimitC;
    if (nested.smu.coAll != null) flat.cpuVoltageOffset = nested.smu.coAll;
  }

  // Fan
  if (nested.fan) {
    if (nested.fan.largeRpm != null) flat.fanLargeRpmTarget = nested.fan.largeRpm;
    if (nested.fan.smallRpm != null) flat.fanSmallRpmTarget = nested.fan.smallRpm;
  }

  // Power Plan
  // 后端存的是 HAL 整数(0/1/2)，前端按钮比的是字符串 id(balance/performance/efficiency)。
  // 这里归一成 id，保证切 tab 重新拉取后 uxtuParams.cpuPowerPlan 仍是 id → 选中项能正确高亮显示。
  if (nested.powerPlan != null) {
    const id = _halToPowerPlanId[nested.powerPlan];
    flat.cpuPowerPlan = id ?? nested.powerPlan;
  }

  return flat;
}

// ── localStorage → 后端一次性迁移 ──
export async function importOverrides(mode, overrides) {
  const res = await fetch("/api/overrides/import", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode, overrides }),
  });
  if (!res.ok) throw new Error(`importOverrides failed for ${mode}`);
}

export async function migrateLocalStorageOverrides() {
  // 版本号控制：递增 MIGRATION_VERSION 可强制重新迁移（修复脏数据等场景）
  // 保留旧 key 不删除（方便用户回退旧版时恢复配置）
  const MIGRATION_VERSION = 4;
  const currentVersion = parseInt(localStorage.getItem("douzhanzhe_overrides_migrated") || "0", 10);
  if (currentVersion >= MIGRATION_VERSION) return 0;

  const modes = ["silent", "office", "beast", "gaming"];
  let migrated = 0;
  for (const mode of modes) {
    const key = `douzhanzhe_overrides_${mode}`;
    try {
      const raw = localStorage.getItem(key);
      if (!raw) continue;
      const data = JSON.parse(raw);
      // 旧 localStorage 格式 → 后端 PerformanceOverrides 结构
      // cpuCoreLimit 是核心数(如8)，后端 coreLimitPercent 是百分比(如50)
      // cpuTurboDisabled → turboEnabled (取反)
      // cpuFreqLimitMhz 仅在 cpuFreqLimitEnabled=true 时生效
      // gpuFreqLocked (LS) → gpu.freqLocked
      // gpuCoreFreqMhz (LS) → gpu.coreFreqMhz
      // gpuMemFreqMhz (LS, index 0-3) → gpu.memFreqLevel
      // gpuTempLimitC (LS) → nvapi.thermalLimitC
      // GPU — 条件构建：仅包含 localStorage 中实际存在的字段
      // import 端点是全量替换，null 会覆盖配置文件中的正确值
      const gpu = {};
      if (data.gpuFreqLimitEnabled && data.gpuFreqLimitMhz != null) {
        gpu.coreFreqMhz = data.gpuFreqLimitMhz;
      }
      if (data.gpuFreqLimitEnabled != null) {
        gpu.freqLocked = data.gpuFreqLimitEnabled;
      } else if (data.gpuFreqLocked != null) {
        gpu.freqLocked = data.gpuFreqLocked;
      }
      if (data.gpuMemFreqMhz != null) {
        gpu.memFreqLevel = data.gpuMemFreqMhz;
      }

      const mapped = {
        cpu: {
          freqLimitMhz: data.cpuFreqLimitEnabled ? (data.cpuFreqLimitMhz ?? null) : null,
          turboEnabled: data.cpuTurboDisabled != null ? !data.cpuTurboDisabled : null,
          coreLimitPercent: data.cpuCoreLimit > 0 ? Math.round(data.cpuCoreLimit / 18 * 100) : null,
        },
        gpu,
        nvapi: {
          ocCoreOffsetMhz: data.ocCoreOffsetMhz ?? null,
          ocMemOffsetMhz: data.ocMemOffsetMhz ?? null,
          powerLimitW: data.nvapiPowerLimitW ?? null,
          thermalLimitC: data.gpuTempLimitC ?? null,
        },
        smu: {
          stapmLimitW: data.cpuLongPptW ?? null,
          shortPowerLimitW: data.cpuShortPptW ?? null,
          tempLimitC: data.cpuTempLimitC ?? null,
          coAll: data.cpuVoltageOffset ?? null,
        },
        fan: {
          largeRpm: data.fanLargeRpmTarget ?? null,
          smallRpm: data.fanSmallRpmTarget ?? null,
        },
        powerPlan: data.cpuPowerPlan ?? null,
      };
      await importOverrides(mode, mapped);
      migrated++;
      log("Migration", `✓ migrated ${mode} overrides from localStorage`);
    } catch (e) {
      log("Migration", `✗ failed to migrate ${mode}: ${e.message}`);
    }
  }
  if (migrated > 0) {
    log("Migration", `Completed: ${migrated} mode(s) migrated from localStorage`);
  }
  localStorage.setItem("douzhanzhe_overrides_migrated", String(MIGRATION_VERSION));
  return migrated;
}

// ── 恢复官方默认 ──
// 清空 overrides + 重发 thermal_mode (EC 恢复出厂值) + CPU/GPU/NVAPI 重置
export async function resetToFactoryDefaults(mode) {
  // 1. 清空后端 overrides 文件
  await syncOverrides(mode, {});

  // 2. 重发 thermal_mode (EC 重新加载出厂值，包括 CPU PPT/温度/风扇预设)
  // 配置 id(cfg-…) 需先解出性能模式裸名再查 thermal 字节；裸名则直接查
  const perfName = mode?.startsWith('cfg-') ? mode.slice(4) : mode;
  const tv = thermalModeMap[perfName];
  if (tv !== null && tv !== undefined) {
    await applyHardwareControl("thermal_mode", tv, mode);
  }

  // 3. CPU 频率/睿频/核心数通过 Windows powercfg 控制
  await resetCpuPower(mode).catch(e => console.warn("resetCpuPower:", e));

  // 4. GPU 频率锁定 + NVAPI 超频/温度限制不受 thermal_mode 管理，需要单独重置
  await applyGpuControl("reset-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] reset:", e));
  await applyGpuControl("reset-memory-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] mem-reset:", e));
  await Promise.all([
    applyNvapiOverclock(0, 0, mode).catch(e => console.warn("[NVAPI] OC-reset:", e)),
    applyNvapiThermalLimit(87, mode).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
  ]);
}


// ── 全量模式下发（overrides 感知） ──
// thermal_mode + GPU/NVAPI 每次必发（不受 EC 管理），其余通道按 overrides 条件下发
// 顺序: 1. thermal_mode → 2. SMU → 3. GPU → 4. NVAPI → 5. CPU powercfg → 6. 风扇 → 7. SMU 重发
/// 重发稀疏 overrides（SMU/GPU/NVAPI/CPU/风扇），不触碰 thermal_mode
/// 用于风扇曲线解锁恢复后，后端已切回 thermal_mode，前端只需重发用户自定义参数
export async function reapplyOverrides(mode, overrides) {
  cancelSmuResendTimers();
  const myGen = ++_smuDispatchGen;

  const isEmpty = !overrides || Object.keys(overrides).length === 0;

  // overrides 为空时，发 GPU/NVAPI/CPU 全通道重置（清理上一个模式可能残留的锁）
  if (isEmpty) {
    log("reapply", "overrides 为空，发送全通道重置");
    // GPU: 解除上一个模式可能残留的核心/显存频率锁
    await Promise.all([
      applyGpuControl("reset-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] reset-clocks:", e)),
      applyGpuControl("reset-memory-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] reset-mem:", e)),
    ]);
    await Promise.all([
      applyNvapiOverclock(0, 0, mode).catch(e => console.warn("[NVAPI] OC-reset:", e)),
      applyNvapiThermalLimit(87, mode).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
    ]);
    await Promise.all([
      setCpuFreqLimit(0, mode).catch(e => console.warn("[CPU] freq-reset:", e)),
      setCpuTurbo(true, mode).catch(e => console.warn("[CPU] turbo-reset:", e)),
      setCpuCoreLimitPercent(100, mode).catch(() => {}),
      applyHardwareControl("power_plan", 0, mode).catch(e => console.warn("[CPU] power-plan-reset:", e)),
    ]);
    return;
  }

  // ② SMU 逐项写入（绕过 uxtu/apply，避免反序列化失败触发 fallback 链）
  const smuFields = ["cpuLongPptW", "cpuShortPptW", "cpuTempLimitC", "cpuVoltageOffset"];
  const hasSmu = smuFields.some(f => f in overrides);
  if (hasSmu) {
    const smuPromises = [];
    if ("cpuLongPptW" in overrides) smuPromises.push(applySmuSet("stapm_limit", overrides.cpuLongPptW, mode));
    if ("cpuShortPptW" in overrides) smuPromises.push(applySmuSet("short_power_limit", overrides.cpuShortPptW, mode));
    if ("cpuTempLimitC" in overrides) smuPromises.push(applySmuSet("tctl_temp", overrides.cpuTempLimitC, mode));
    if ("cpuVoltageOffset" in overrides) smuPromises.push(applySmuSet("co_all", overrides.cpuVoltageOffset, mode));
    await Promise.all(smuPromises.map(p => p.catch(e => console.warn("[SMU] set:", e))));
  }

  // ③ GPU 核心频率
  const hasGpuCore = "gpuFreqLimitEnabled" in overrides || "gpuCoreFreqMhz" in overrides;
  if (hasGpuCore) {
    try {
      await applyGpuControl("reset-clocks", undefined, undefined, undefined, mode);
      if (overrides.gpuFreqLimitEnabled && overrides.gpuCoreFreqMhz !== GPU_BASE_CLOCK) {
        await applyGpuControl("limit-max", overrides.gpuCoreFreqMhz, undefined, undefined, mode);
        await applyGpuControl("lock-exact", overrides.gpuCoreFreqMhz, undefined, undefined, mode);
      }
    } catch (e) { console.warn("[GPU] freq:", e); }
  } else {
    // 新模式未设核心频率 → 清理上一个模式可能残留的核心锁
    await applyGpuControl("reset-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] cleanup reset-clocks:", e));
  }

  // GPU 显存频率
  const hasGpuMem = "gpuMemFreqMhz" in overrides && overrides.gpuMemFreqMhz > 0;
  if (hasGpuMem) {
    try {
      const memMap = [0, 9001, 11001, 12001];
      await applyGpuControl("reset-memory-clocks", undefined, undefined, undefined, mode);
      await applyGpuControl("limit-memory", memMap[overrides.gpuMemFreqMhz], undefined, undefined, mode);
    } catch (e) { console.warn("[GPU] memory:", e); }
  } else {
    // 新模式未设显存频率 → 清理上一个模式可能残留的显存锁
    await applyGpuControl("reset-memory-clocks", undefined, undefined, undefined, mode).catch(e => console.warn("[GPU] cleanup reset-mem:", e));
  }

  // ④ NVAPI: 超频偏移 + 温度限制（仅在 overrides 有对应字段时写入，避免默认值污染）
  const nvapiPromises = [];
  if ("ocCoreOffsetMhz" in overrides || "ocMemOffsetMhz" in overrides) {
    nvapiPromises.push(
      applyNvapiOverclock(overrides.ocCoreOffsetMhz ?? 0, overrides.ocMemOffsetMhz ?? 0, mode).catch(
        e => console.warn("[NVAPI] OC:", e)
      )
    );
  }
  if ("gpuTempLimitC" in overrides) {
    nvapiPromises.push(
      applyNvapiThermalLimit(clampParam("gpuTempLimitC", overrides.gpuTempLimitC), mode).catch(
        e => console.warn("[NVAPI] thermal:", e)
      )
    );
  }
  if (nvapiPromises.length) await Promise.all(nvapiPromises);

  // ⑤ CPU powercfg: 频率限制 / 睿频 / 核心数 / 电源计划（仅在 overrides 有对应字段时写入）
  if ("cpuFreqLimitEnabled" in overrides || "cpuFreqLimitMhz" in overrides) {
    setCpuFreqLimit(overrides.cpuFreqLimitEnabled ? overrides.cpuFreqLimitMhz : 0, mode).catch(
      e => console.warn("[CPU] freq:", e)
    );
  }
  if ("cpuTurboDisabled" in overrides) {
    setCpuTurbo(!overrides.cpuTurboDisabled, mode).catch(
      e => console.warn("[CPU] turbo:", e)
    );
  }
  if ("cpuCoreLimit" in overrides) {
    const pct = coreToPercent(overrides.cpuCoreLimit);
    setCpuCoreLimitPercent(pct, mode).catch(
      e => console.warn("[CPU] core-limit:", e)
    );
  }
  if ("cpuPowerPlan" in overrides) {
    const ppHal = powerPlanHALMap[overrides.cpuPowerPlan];
    if (ppHal !== undefined) {
      applyHardwareControl("power_plan", ppHal, mode).catch(
        e => console.warn("[CPU] power-plan:", e)
      );
    }
  }

  // ⑥ 风扇目标转速
  const fanFields = ["fanLargeRpmTarget", "fanSmallRpmTarget"];
  const hasFan = fanFields.some(f => f in overrides);
  if (hasFan) {
    const fanUrl = mode ? `/api/fan/set-target?mode=${mode}` : `/api/fan/set-target`;
    fetch(fanUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        largeRpm: overrides.fanLargeRpmTarget,
        smallRpm: overrides.fanSmallRpmTarget,
      }),
    }).catch(e => console.warn("[Fan]:", e));
  }

  // ⑦ 延迟重发 SMU（两次），仅当 SMU 实际执行时触发
  if (hasSmu) {
    const resendSmu = () => {
      const ps = [];
      if ("cpuLongPptW" in overrides) ps.push(applySmuSet("stapm_limit", overrides.cpuLongPptW, mode));
      if ("cpuShortPptW" in overrides) ps.push(applySmuSet("short_power_limit", overrides.cpuShortPptW, mode));
      if ("cpuTempLimitC" in overrides) ps.push(applySmuSet("tctl_temp", overrides.cpuTempLimitC, mode));
      if ("cpuVoltageOffset" in overrides) ps.push(applySmuSet("co_all", overrides.cpuVoltageOffset, mode));
      return Promise.all(ps.map(p => p.catch(() => {})));
    };
    const t1 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) { log("SMU", `re-send skipped (gen ${_smuDispatchGen})`); return; }
      resendSmu().then(() => log("SMU", "re-send OK")).catch(e => console.warn("[SMU] re-send:", e));
    }, 500);
    const t2 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) { log("SMU", `re-send2 skipped (gen ${_smuDispatchGen})`); return; }
      resendSmu().then(() => log("SMU", "re-send2 OK")).catch(e => console.warn("[SMU] re-send2:", e));
    }, 1500);
    _smuResendTimers = [t1, t2];
  }
}

// ── 关闭显示器 ──
export async function monitorOff() {
  const res = await fetch("/api/monitor/off", { method: "POST" });
  if (!res.ok) throw new Error("monitor off returned " + res.status);
  return res.json();
}

// ── UI State (后端持久化) ──
export async function fetchUiState() {
  const res = await fetch("/api/ui-state");
  if (!res.ok) throw new Error("ui-state returned " + res.status);
  return res.json();
}
export async function saveUiState(state) {
  var existing;
  try { existing = await fetchUiState(); } catch { existing = {}; }
  var merged = { ...existing, ...state };
  const res = await fetch("/api/ui-state", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(merged),
  });
  if (!res.ok) throw new Error("ui-state returned " + res.status);
  return res.json();
}

// ── 快捷键配置（数据驱动） ──
export async function fetchHotkeyConfig() {
  const res = await fetch("/api/hotkey");
  if (!res.ok) throw new Error("hotkey config returned " + res.status);
  return res.json();
}

export async function setHotkeyConfig(id, config) {
  const res = await fetch("/api/hotkey", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id, ...config }),
  });
  if (!res.ok) throw new Error("hotkey config returned " + res.status);
  return res.json();
}

// ---- Game auto-switch status / launch ----

export async function fetchGameAutoSwitchStatus() {
  const res = await fetch("/api/game-profiles/status");
  if (!res.ok) throw new Error("game status returned " + res.status);
  return res.json();
}

export async function launchGameProfile(id) {
  const res = await fetch(`/api/game-profiles/${encodeURIComponent(id)}/launch`, { method: "POST" });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `launch returned ${res.status}`);
  return data;
}

// ---- Profile CRUD API ----

// 将背景配置同步到 CSS 变量（App 启动与设置页共用）
export function applyWallpaperCss(opts) {
  const root = document.documentElement;
  const show = !!opts.enabled && !!opts.hasImage && !!opts.url;
  const opacity = (opts.opacity ?? 60) / 100; // 0~1，单一滑块同时驱动壁纸与卡片
  root.style.setProperty("--wallpaper-opacity", show ? String(opacity) : "0");
  // 卡片通透度：与壁纸滑块联动（越大越透）。
  // 范围约 88%(接近不透明) → 58%(很透)；默认值 60 时约 70%，正好对标「添加规则」卡片观感。
  const cardMixPct = Math.max(50, 88 - opacity * 30);
  root.style.setProperty("--card-mix", cardMixPct.toFixed(1) + "%");
  // 模糊度：非线性（感知均匀）。0~100 → 0~60px，采用平方曲线。
  // 低值变化平缓、高值变化明显，修正「20 以下变化大、20 以上变化小」的线性错觉。
  const blurPx = Math.round(60 * Math.pow((opts.blur ?? 45) / 100, 2));
  root.style.setProperty("--wallpaper-blur", blurPx + "px");
  root.style.setProperty("--wallpaper-image", show ? `url("${opts.url}")` : "none");
}

export async function fetchProfiles() {
  const res = await fetch("/api/profiles");
  if (!res.ok) throw new Error("profiles fetch failed");
  return res.json();
}

export async function fetchProfile(id) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id));
  if (!res.ok) throw new Error("profile fetch failed");
  return res.json();
}

export async function saveProfile(id, overrides) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(overrides),
  });
  if (!res.ok) throw new Error("profile save failed");
  return res.json();
}

export async function createProfile(name, thermalMode) {
  const res = await fetch("/api/profiles", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, thermalMode }),
  });
  if (!res.ok) throw new Error("profile create failed");
  return res.json();
}

export async function deleteProfile(id) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id), {
    method: "DELETE",
  });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new Error(data.error || `profile delete failed (${res.status})`);
  }
  return res.json();
}

export async function renameProfile(id, name) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id) + "/rename", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
  if (!res.ok) throw new Error("profile rename failed");
  return res.json();
}

export async function copyProfile(id) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id) + "/copy", {
    method: "POST",
  });
  if (!res.ok) throw new Error("profile copy failed");
  return res.json();
}

export async function resetProfile(id) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id) + "/reset", {
    method: "POST",
  });
  if (!res.ok) throw new Error("profile reset failed");
  return res.json();
}

export async function setProfileThermalMode(id, thermalMode) {
  const res = await fetch("/api/profiles/" + encodeURIComponent(id) + "/thermal-mode", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ thermalMode }),
  });
  if (!res.ok) throw new Error("profile thermal-mode failed");
  return res.json();
}

export async function fetchPlatformInfo() {
  const res = await fetch("/api/platform/info");
  if (!res.ok) throw new Error("platform info fetch failed");
  return res.json();
}
