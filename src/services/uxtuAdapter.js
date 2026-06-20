const BACKEND = "";

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

export async function applyUxtuLimits(payload) {
  const res = await fetch(`${BACKEND}/api/uxtu/apply`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

// C# HAL 硬件控制 (kb_light, fn_lock, num_lock, caps_lock, thermal_mode)
export async function applyHardwareControl(target, value) {
  const res = await fetch(`/api/control`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ target, value }),
  });
  if (!res.ok) throw new Error(`HAL 返回 ${res.status}`);
  return res.json();
}

export function createTelemetrySocket(onData, onError) {
  // 绕过 Vite 代理直连 C# HAL WebSocket
  const ws = new WebSocket(`ws://127.0.0.1:3100/ws`);
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
export async function applyGpuControl(action, value, min, max) {
  const body = value !== undefined
    ? { action, min, max, value }
    : { action };
  const res = await fetch(`/api/gpu/set`, {
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
export async function applyNvapiOverclock(coreOffsetMhz, memOffsetMhz) {
  const res = await fetch(`/api/nvapi/overclock`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ coreOffsetMhz, memOffsetMhz }),
  });
  if (!res.ok) throw new Error(`NVAPI 超频返回 ${res.status}`);
  return res.json();
}

// NVAPI 温度限制
export async function applyNvapiThermalLimit(tempC) {
  const res = await fetch(`/api/nvapi/thermal-limit`, {
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
  cpuCoreLimit: { min: 0, max: 14 },
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

export async function applySmuSet(parameter, valueM) {
  const res = await fetch("/api/smu/set", {
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

export async function setCpuFreqLimit(mhz) {
  const res = await fetch("/api/cpu/freq-limit", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mhz }),
  });
  if (!res.ok) throw new Error("CPU freq-limit returned " + res.status);
  return res.json();
}

export async function setCpuTurbo(enabled) {
  const res = await fetch("/api/cpu/turbo", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled }),
  });
  if (!res.ok) throw new Error("CPU turbo returned " + res.status);
  return res.json();
}

export async function setCpuCoreLimitPercent(percent) {
  const res = await fetch("/api/cpu/core-limit", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ percent }),
  });
  if (!res.ok) throw new Error("CPU core-limit returned " + res.status);
  return res.json();
}

export async function resetCpuPower() {
  const res = await fetch("/api/cpu/reset", { method: "POST" });
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

// ── 后端嵌套格式 → 前端扁平格式 ──
// 后端 PerformanceOverrides 类使用嵌套结构 (cpu.freqLimitMhz)，
// 但前端 reapplyOverrides 和 UI 使用扁平格式 (cpuFreqLimitMhz)。
export function flattenBackendOverrides(nested) {
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
      // 百分比 → 核心数 (假设 16 核)
      flat.cpuCoreLimit = Math.round(nested.cpu.coreLimitPercent * 16 / 100);
    } else {
      flat.cpuCoreLimit = 0;
    }
  }

  // GPU
  if (nested.gpu) {
    if (nested.gpu.coreFreqMhz != null) flat.gpuCoreFreqMhz = nested.gpu.coreFreqMhz;
    if (nested.gpu.freqLocked != null) {
      flat.gpuFreqLocked = nested.gpu.freqLocked;
      flat.gpuFreqLimitEnabled = nested.gpu.freqLocked;
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
  if (nested.powerPlan != null) flat.cpuPowerPlan = nested.powerPlan;

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
  const MIGRATION_VERSION = 3;
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
      const mapped = {
        cpu: {
          freqLimitMhz: data.cpuFreqLimitEnabled ? (data.cpuFreqLimitMhz ?? null) : null,
          turboEnabled: data.cpuTurboDisabled != null ? !data.cpuTurboDisabled : null,
          coreLimitPercent: data.cpuCoreLimit > 0 ? Math.round(data.cpuCoreLimit / 16 * 100) : null,
        },
        gpu: {
          coreFreqMhz: data.gpuFreqLimitEnabled ? (data.gpuFreqLimitMhz ?? null) : null,
          freqLocked: data.gpuFreqLimitEnabled != null ? data.gpuFreqLimitEnabled : (data.gpuFreqLocked ?? null),
          memFreqLevel: data.gpuMemFreqMhz ?? null,
        },
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
  const tv = thermalModeMap[mode];
  if (tv !== null && tv !== undefined) {
    await applyHardwareControl("thermal_mode", tv);
  }
  
  // 3. CPU 频率/睿频/核心数通过 Windows powercfg 控制
  await resetCpuPower().catch(e => console.warn("resetCpuPower:", e));

  // 4. GPU 频率锁定 + NVAPI 超频/温度限制不受 thermal_mode 管理，需要单独重置
  await applyGpuControl("reset-clocks").catch(e => console.warn("[GPU] reset:", e));
  await applyGpuControl("reset-memory-clocks").catch(e => console.warn("[GPU] mem-reset:", e));
  await Promise.all([
    applyNvapiOverclock(0, 0).catch(e => console.warn("[NVAPI] OC-reset:", e)),
    applyNvapiThermalLimit(87).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
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
      applyGpuControl("reset-clocks").catch(e => console.warn("[GPU] reset-clocks:", e)),
      applyGpuControl("reset-memory-clocks").catch(e => console.warn("[GPU] reset-mem:", e)),
    ]);
    await Promise.all([
      applyNvapiOverclock(0, 0).catch(e => console.warn("[NVAPI] OC-reset:", e)),
      applyNvapiThermalLimit(87).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
    ]);
    await Promise.all([
      setCpuFreqLimit(0).catch(e => console.warn("[CPU] freq-reset:", e)),
      setCpuTurbo(true).catch(e => console.warn("[CPU] turbo-reset:", e)),
      setCpuCoreLimitPercent(100).catch(() => {}),
      applyHardwareControl("power_plan", 0).catch(e => console.warn("[CPU] power-plan-reset:", e)),
    ]);
    return;
  }

  // ② SMU 批量写入（仅当 overrides 有 CPU SMU 字段时执行）
  const smuFields = ["cpuLongPptW", "cpuShortPptW", "cpuTempLimitC", "cpuVoltageOffset"];
  const hasSmu = smuFields.some(f => f in overrides);
  if (hasSmu) {
    await applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides }).catch(
      e => console.warn("[SMU] batch:", e)
    );
  }

  // ③ GPU 核心频率
  const hasGpuCore = "gpuFreqLimitEnabled" in overrides || "gpuCoreFreqMhz" in overrides;
  if (hasGpuCore) {
    try {
      await applyGpuControl("reset-clocks");
      if (overrides.gpuFreqLimitEnabled && overrides.gpuCoreFreqMhz !== GPU_BASE_CLOCK) {
        await applyGpuControl("limit-max", overrides.gpuCoreFreqMhz);
        await applyGpuControl("lock-exact", overrides.gpuCoreFreqMhz);
      }
    } catch (e) { console.warn("[GPU] freq:", e); }
  } else {
    // 新模式未设核心频率 → 清理上一个模式可能残留的核心锁
    await applyGpuControl("reset-clocks").catch(e => console.warn("[GPU] cleanup reset-clocks:", e));
  }

  // GPU 显存频率
  const hasGpuMem = "gpuMemFreqMhz" in overrides && overrides.gpuMemFreqMhz > 0;
  if (hasGpuMem) {
    try {
      const memMap = [0, 9001, 11001, 12001];
      await applyGpuControl("reset-memory-clocks");
      await applyGpuControl("limit-memory", memMap[overrides.gpuMemFreqMhz]);
    } catch (e) { console.warn("[GPU] memory:", e); }
  } else {
    // 新模式未设显存频率 → 清理上一个模式可能残留的显存锁
    await applyGpuControl("reset-memory-clocks").catch(e => console.warn("[GPU] cleanup reset-mem:", e));
  }

  // ④ NVAPI: 超频偏移 + 温度限制（并行）
  const thermalC = clampParam("gpuTempLimitC", overrides.gpuTempLimitC ?? 87);
  await Promise.all([
    applyNvapiOverclock(overrides.ocCoreOffsetMhz ?? 0, overrides.ocMemOffsetMhz ?? 0).catch(
      e => console.warn("[NVAPI] OC:", e)
    ),
    applyNvapiThermalLimit(thermalC).catch(
      e => console.warn("[NVAPI] thermal:", e)
    ),
  ]);

  // ⑤ CPU powercfg: 频率限制 / 睿频 / 核心数 / 电源计划
  setCpuFreqLimit(overrides.cpuFreqLimitEnabled ? overrides.cpuFreqLimitMhz : 0).catch(
    e => console.warn("[CPU] freq:", e)
  );
  setCpuTurbo(!(overrides.cpuTurboDisabled ?? false)).catch(
    e => console.warn("[CPU] turbo:", e)
  );
  if ((overrides.cpuCoreLimit ?? 0) > 0) {
    setCpuCoreLimitPercent(Math.round(overrides.cpuCoreLimit / 16 * 100)).catch(
      e => console.warn("[CPU] core-limit:", e)
    );
  } else {
    setCpuCoreLimitPercent(100).catch(() => {});
  }
  const ppHal = powerPlanHALMap[overrides.cpuPowerPlan ?? "balance"];
  if (ppHal !== undefined) {
    applyHardwareControl("power_plan", ppHal).catch(
      e => console.warn("[CPU] power-plan:", e)
    );
  }

  // ⑥ 风扇目标转速
  const fanFields = ["fanLargeRpmTarget", "fanSmallRpmTarget"];
  const hasFan = fanFields.some(f => f in overrides);
  if (hasFan) {
    fetch("/api/fan/set-target", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        largeRpm: overrides.fanLargeRpmTarget ?? 2900,
        smallRpm: overrides.fanSmallRpmTarget ?? 6400,
      }),
    }).catch(e => console.warn("[Fan]:", e));
  }

  // ⑦ 延迟重发 SMU（两次），仅当 SMU 实际执行时触发
  if (hasSmu) {
    const t1 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) {
        log("SMU", `re-send skipped (gen ${_smuDispatchGen})`);
        return;
      }
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides })
        .then(r => log("SMU", `re-send OK: ${JSON.stringify(r)}`))
        .catch(e => console.warn("[SMU] re-send failed:", e));
    }, 500);
    const t2 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) {
        log("SMU", `re-send2 skipped (gen ${_smuDispatchGen})`);
        return;
      }
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides })
        .then(r => log("SMU", `re-send2 OK: ${JSON.stringify(r)}`))
        .catch(e => console.warn("[SMU] re-send2 failed:", e));
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

// ── 快捷键配置 ──
export async function fetchHotkeyConfig() {
  const res = await fetch("/api/hotkey/monitor-off");
  if (!res.ok) throw new Error("hotkey config returned " + res.status);
  return res.json();
}

export async function setHotkeyConfig(config) {
  const res = await fetch("/api/hotkey/monitor-off", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(config),
  });
  if (!res.ok) throw new Error("hotkey config returned " + res.status);
  return res.json();
}
