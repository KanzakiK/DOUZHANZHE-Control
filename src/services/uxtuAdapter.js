const BACKEND = "";

// C# HAL thermal_mode value mapping
export const thermalModeMap = {
  silent: 2,
  office: 0,
  gaming: 3,
  beast: 1,
  custom: null,
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

export async function fetchTelemetry() {
  const res = await fetch(`${BACKEND}/api/telemetry`);
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

export async function fetchSmuInfo() {
  const res = await fetch(`/api/ryzenadj/info`);
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

export async function discoverWmi() {
  const res = await fetch(`${BACKEND}/api/discover`);
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
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
  ws.onclose = () => {
    setTimeout(() => createTelemetrySocket(onData, onError), 3000);
  };
  return ws;
}

export const GPU_BASE_CLOCK = 2750; // RTX 5060 典型 boost 频率 (nvidia-smi 无法读取，用户提供)
export const GPU_MEM_BASE_CLOCK = 12001; // 显存最大频率 (limit-memory 作为上限使用) // 显存基准频率

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

// 读取 GPU 状态 (当前频率/基准/最大)
export async function fetchGpuStatus() {
  const res = await fetch(`/api/gpu/status`);
  if (!res.ok) throw new Error(`GPU 状态返回 ${res.status}`);
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


// 散热模式风扇区间（硬件限制：大扇 0-4400, 小扇 0-8200，受散热模式约束）
// 数值来源：docs/reference-consoles.md 官方预设表（大扇下限/上限/预设，小扇下限/上限/预设）
const FAN_RANGES = {
  silent: { largeMin: 1900, largeMax: 2900, smallMin: 1700, smallMax: 6400 },
  office: { largeMin: 2600, largeMax: 3500, smallMin: 5900, smallMax: 6900 },
  gaming: { largeMin: 4000, largeMax: 4400, smallMin: 7500, smallMax: 8200 },
  beast:  { largeMin: 3200, largeMax: 3800, smallMin: 6400, smallMax: 7200 },
  custom: { largeMin: 2600, largeMax: 3500, smallMin: 5900, smallMax: 6900 },
};

// 完整模式预设：全量参数快照 (22 字段)
// 命名约定: oc*OffsetMhz = NVAPI P-State 超频偏移, gpu*FreqMhz = nvidia-smi 频率锁
export const MODE_PRESETS = {
  silent: { cpuTempLimitC: 75, cpuLongPptW: 35, cpuShortPptW: 45, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 3000, cpuTurboDisabled: false, cpuCoreLimit: 0, cpuPowerPlan: "balance", gpuPptLimitW: 60, gpuTempLimitC: 75, gpuCoreFreqMhz: 2750, gpuMemFreqMhz: 0, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0, fanLargeRpmTarget: 2200, fanSmallRpmTarget: 2000 },
  office: { cpuTempLimitC: 80, cpuLongPptW: 55, cpuShortPptW: 70, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, cpuCoreLimit: 0, cpuPowerPlan: "balance", gpuPptLimitW: 75, gpuTempLimitC: 85, gpuCoreFreqMhz: 2750, gpuMemFreqMhz: 0, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0, fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
  gaming: { cpuTempLimitC: 95, cpuLongPptW: 120, cpuShortPptW: 140, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5500, cpuTurboDisabled: false, cpuCoreLimit: 0, cpuPowerPlan: "performance", gpuPptLimitW: 115, gpuTempLimitC: 95, gpuCoreFreqMhz: 2750, gpuMemFreqMhz: 0, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, ocCoreOffsetMhz: 200, ocMemOffsetMhz: 0, fanLargeRpmTarget: 4300, fanSmallRpmTarget: 8000 },
  beast:  { cpuTempLimitC: 88, cpuLongPptW: 85, cpuShortPptW: 100, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5000, cpuTurboDisabled: false, cpuCoreLimit: 0, cpuPowerPlan: "performance", gpuPptLimitW: 100, gpuTempLimitC: 90, gpuCoreFreqMhz: 2750, gpuMemFreqMhz: 0, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, ocCoreOffsetMhz: 100, ocMemOffsetMhz: 0, fanLargeRpmTarget: 3500, fanSmallRpmTarget: 6900 },
  custom: { cpuTempLimitC: 80, cpuLongPptW: 55, cpuShortPptW: 70, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, cpuCoreLimit: 0, cpuPowerPlan: "balance", gpuPptLimitW: 75, gpuTempLimitC: 85, gpuCoreFreqMhz: 2750, gpuMemFreqMhz: 0, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0, fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
};

// 全量参数默认值 (兜底)
export const FULL_PARAMS = {
  cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false,
  cpuTempLimitC: 80, cpuCoreLimit: 0, cpuPowerPlan: "balance", cpuVoltageOffset: 0,
  cpuLongPptW: 55, cpuShortPptW: 70,
  gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, gpuCoreFreqMhz: 2750,
  gpuMemFreqMhz: 0, gpuPptLimitW: 75, gpuTempLimitC: 85,
  ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0,
  fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400,
};

// 参数合法范围 — 用于写入硬件前钳位
export const PARAM_RANGES = {
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

export function clampParam(key, value) {
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

// ── 全量模式下发 ──
// 按正确写入顺序下发所有硬件参数:
// 1. EC 散热模式 → 2. SMU 批量 → 3. GPU 频率(unlock→limit→lock) →
// 4. NVAPI 超频+温度(并行) → 5. CPU powercfg → 6. 风扇 → 7. 500ms SMU 重发
export async function dispatchFullMode(mode, params) {
  const tv = thermalModeMap[mode];

  // 1. EC 散热模式
  if (tv !== null && tv !== undefined) {
    applyHardwareControl("thermal_mode", tv).catch(e => console.warn("[EC] thermal_mode:", e));
  }

  // 2. SMU 批量写入 (后端提取 CPU 相关字段一次性下发)
  await applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params }).catch(
    e => console.warn("[SMU] batch:", e)
  );

  // 3. GPU 频率控制 (nvidia-smi: unlock → limit → lock)
  try {
    await applyGpuControl("reset-clocks");
    if (params.gpuFreqLimitEnabled && params.gpuCoreFreqMhz !== GPU_BASE_CLOCK) {
      await applyGpuControl("limit-max", params.gpuCoreFreqMhz);
      await applyGpuControl("lock-exact", params.gpuCoreFreqMhz);
    }
  } catch (e) { console.warn("[GPU] freq:", e); }

  // GPU 显存频率
  try {
    const memMap = [0, 9001, 11001, 12001];
    if (params.gpuMemFreqMhz === 0) {
      await applyGpuControl("reset-memory-clocks");
    } else {
      await applyGpuControl("limit-memory", memMap[params.gpuMemFreqMhz]);
    }
  } catch (e) { console.warn("[GPU] memory:", e); }

  // 4. NVAPI: 超频偏移 + 温度限制 (并行，互不依赖)
  const thermalC = clampParam("gpuTempLimitC", params.gpuTempLimitC ?? 87);
  Promise.all([
    applyNvapiOverclock(params.ocCoreOffsetMhz ?? 0, params.ocMemOffsetMhz ?? 0).catch(
      e => console.warn("[NVAPI] OC:", e)
    ),
    applyNvapiThermalLimit(thermalC).catch(
      e => console.warn("[NVAPI] thermal:", e)
    ),
  ]);

  // 5. CPU powercfg: 频率限制 / 睿频 / 核心数 / 电源计划
  setCpuFreqLimit(params.cpuFreqLimitEnabled ? params.cpuFreqLimitMhz : 0).catch(
    e => console.warn("[CPU] freq:", e)
  );
  setCpuTurbo(!params.cpuTurboDisabled).catch(
    e => console.warn("[CPU] turbo:", e)
  );
  if (params.cpuCoreLimit > 0) {
    setCpuCoreLimitPercent(Math.round(params.cpuCoreLimit / 16 * 100)).catch(
      e => console.warn("[CPU] core-limit:", e)
    );
  } else {
    setCpuCoreLimitPercent(100).catch(() => {});
  }
  const ppHal = powerPlanHALMap[params.cpuPowerPlan];
  if (ppHal !== undefined) {
    applyHardwareControl("power_plan", ppHal).catch(
      e => console.warn("[CPU] power-plan:", e)
    );
  }

  // 6. 风扇目标转速
  fetch("/api/fan/set-target", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      largeRpm: params.fanLargeRpmTarget ?? 2900,
      smallRpm: params.fanSmallRpmTarget ?? 6400,
    }),
  }).catch(e => console.warn("[Fan]:", e));

  // 7. 500ms 后重发 SMU，防止 EC 刷预设覆盖用户参数
  setTimeout(() => {
    applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params })
      .then(r => console.log("[SMU] re-send OK:", r))
      .catch(e => console.warn("[SMU] re-send failed:", e));
  }, 500);
}
