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
