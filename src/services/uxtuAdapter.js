const BACKEND = "http://localhost:3099";

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

export async function applySystemSetting(key, value) {
  const res = await fetch(`${BACKEND}/api/system/settings`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ key, value }),
  });
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

export async function fetchSmuInfo() {
  const res = await fetch(`${BACKEND}/api/ryzenadj/info`);
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

export async function discoverWmi() {
  const res = await fetch(`${BACKEND}/api/system/discover`);
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

export async function setFanSpeed(fan, rpm) {
  const res = await fetch(`${BACKEND}/api/fan/speed`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ fan, rpm }),
  });
  if (!res.ok) throw new Error(`后端返回 ${res.status}`);
  return res.json();
}

export function createTelemetrySocket(onData, onError) {
  const ws = new WebSocket(`ws://localhost:3099`);
  ws.onmessage = (e) => {
    try {
      onData(JSON.parse(e.data));
    } catch { /* ignore */ }
  };
  ws.onerror = () => onError?.();
  ws.onclose = () => {
    // 断线 3 秒后自动重连
    setTimeout(() => createTelemetrySocket(onData, onError), 3000);
  };
  return ws;
}
