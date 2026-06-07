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


// 完整模式预设：CPU + GPU + 风扇 (13 字段)
export const MODE_PRESETS = {
  silent: { cpuTempLimitC: 75, cpuLongPptW: 35, cpuShortPptW: 45, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 3000, cpuTurboDisabled: false, gpuPptLimitW: 60, gpuTempLimitC: 75, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 1, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 1800, gpuFreqLocked: false, fanLargeRpmTarget: 1320, fanSmallRpmTarget: 2460 },
  office: { cpuTempLimitC: 80, cpuLongPptW: 55, cpuShortPptW: 70, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, gpuPptLimitW: 75, gpuTempLimitC: 85, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 1, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2200, gpuFreqLocked: false, fanLargeRpmTarget: 2200, fanSmallRpmTarget: 4100 },
  gaming: { cpuTempLimitC: 95, cpuLongPptW: 120, cpuShortPptW: 140, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5500, cpuTurboDisabled: false, gpuPptLimitW: 115, gpuTempLimitC: 95, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 1, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 3000, gpuFreqLocked: false, fanLargeRpmTarget: 4400, fanSmallRpmTarget: 8200 },
  beast:  { cpuTempLimitC: 88, cpuLongPptW: 85, cpuShortPptW: 100, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 5000, cpuTurboDisabled: false, gpuPptLimitW: 100, gpuTempLimitC: 90, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 1, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, gpuFreqLocked: false, fanLargeRpmTarget: 3300, fanSmallRpmTarget: 6150 },
  custom: { cpuTempLimitC: 90, cpuLongPptW: 65, cpuShortPptW: 85, cpuVoltageOffset: 0, cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false, gpuPptLimitW: 115, gpuTempLimitC: 87, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 1, gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, gpuFreqLocked: false, fanLargeRpmTarget: 2200, fanSmallRpmTarget: 4100 },
};

export async function applySmuSet(parameter, valueM) {
  const res = await fetch("/api/smu/set", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ parameter, valueM }),
  });
  if (!res.ok) throw new Error("SMU return " + res.status);
  return res.json();
}
