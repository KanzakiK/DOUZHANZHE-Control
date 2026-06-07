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

export const GPU_BASE_CLOCK = 2700; // RTX 5060 典型 boost 频率 (nvidia-smi 无法读取，用户提供)
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


// 散热模式风扇区间（硬件限制：大扇 0-4400, 小扇 0-8200，受散热模式约束）
// 数值来源：docs/reference-consoles.md 官方预设表（大扇下限/上限/预设，小扇下限/上限/预设）
const FAN_RANGES = {
  silent: { largeMin: 1900, largeMax: 2900, smallMin: 1700, smallMax: 6400 },
  office: { largeMin: 2600, largeMax: 3500, smallMin: 5900, smallMax: 6900 },
  gaming: { largeMin: 4000, largeMax: 4400, smallMin: 7500, smallMax: 8200 },
  beast:  { largeMin: 3200, largeMax: 3800, smallMin: 6400, smallMax: 7200 },
  custom: { largeMin: 2600, largeMax: 3500, smallMin: 5900, smallMax: 6900 },
};

// 完整模式预设：CPU + GPU 功耗 + 风扇 (11 字段)
export const MODE_PRESETS = {
  silent: { cpuTempLimitC: 75, cpuLongPptW: 35, cpuShortPptW: 45, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 3000, cpuTurboDisabled: false, gpuPptLimitW: 60, gpuTempLimitC: 75, fanLargeRpmTarget: 2200, fanSmallRpmTarget: 2000 },
  office: { cpuTempLimitC: 80, cpuLongPptW: 55, cpuShortPptW: 70, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, gpuPptLimitW: 75, gpuTempLimitC: 85, fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
  gaming: { cpuTempLimitC: 95, cpuLongPptW: 120, cpuShortPptW: 140, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5500, cpuTurboDisabled: false, gpuPptLimitW: 115, gpuTempLimitC: 95, fanLargeRpmTarget: 4300, fanSmallRpmTarget: 8000 },
  beast:  { cpuTempLimitC: 88, cpuLongPptW: 85, cpuShortPptW: 100, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5000, cpuTurboDisabled: false, gpuPptLimitW: 100, gpuTempLimitC: 90, fanLargeRpmTarget: 3500, fanSmallRpmTarget: 6900 },
  custom: { cpuTempLimitC: 80, cpuLongPptW: 55, cpuShortPptW: 70, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, gpuPptLimitW: 75, gpuTempLimitC: 85, fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
};

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
