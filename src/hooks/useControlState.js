import { useEffect, useMemo, useRef, useState } from "react";
import { mockTelemetry } from "../data/mockTelemetry";
import {
  createTelemetrySocket, MODE_PRESETS, FULL_PARAMS, dispatchFullMode,
} from "../services/uxtuAdapter";

const LS_THEME = "douzhanzhe_theme";
const LS_SETTINGS = "douzhanzhe_settings";
const LS_PARAMS_PREFIX = "douzhanzhe_params_";
const API_CUSTOM_PARAMS = "/api/custom-params";

function loadFromLS(key, defaultValue) {
  try {
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) : defaultValue;
  } catch {
    return defaultValue;
  }
}

function saveToLS(key, value) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch { /* quota exceeded etc */ }
}

export function useControlState(onSaveResult) {
  const onSaveRef = useRef(onSaveResult);
  onSaveRef.current = onSaveResult;

  // ── Theme ──
  const [theme, setTheme] = useState(() => loadFromLS(LS_THEME, "theme-mech-violet"));

  // ── Telemetry + History ──
  const [telemetry, setTelemetry] = useState(mockTelemetry);
  const lastTickRef = useRef(null);
  const MAX_HISTORY = 60;
  const [history, setHistory] = useState({ cpu: [], gpu: [], fan: [], cpuTemp: [], gpuTemp: [] });
  const prevTelemetryRef = useRef(telemetry);
  useEffect(() => {
    if (prevTelemetryRef.current === telemetry) return;
    prevTelemetryRef.current = telemetry;
    setHistory((prev) => ({
      cpu: [...prev.cpu, telemetry.cpuUsage].slice(-MAX_HISTORY),
      gpu: [...prev.gpu, telemetry.gpuUsage].slice(-MAX_HISTORY),
      fan: [...prev.fan, telemetry.fanLargeRpm].slice(-MAX_HISTORY),
      cpuTemp: [...prev.cpuTemp, telemetry.cpuTemp].slice(-MAX_HISTORY),
      gpuTemp: [...prev.gpuTemp, telemetry.gpuTemp].slice(-MAX_HISTORY),
    }));
  }, [telemetry]);

  // ── Settings (含 mode) ──
  const [settings, setSettings] = useState(() => loadFromLS(LS_SETTINGS, {
    mode: "office", dGpuDirect: true, fanBoost: false,
    numLock: true, capsLock: false, fnLock: false,
    touchpadLock: false, osdDisabled: false, kbBrightnessLevel: 0,
  }));

  // ── uxtuParams: 唯一全量参数状态 (包含 CPU/GPU/风扇所有可控字段) ──
  const [uxtuParams, setUxtuParams] = useState(() => {
    const mode = loadFromLS(LS_SETTINGS, { mode: "office" }).mode;
    const saved = loadFromLS(LS_PARAMS_PREFIX + mode, null);
    const preset = MODE_PRESETS[mode] || {};
    return { ...FULL_PARAMS, ...preset, ...(saved || {}) };
  });

  const [paramsLoaded, setParamsLoaded] = useState(false);

  // 持久化 theme + settings
  useEffect(() => { saveToLS(LS_THEME, theme); }, [theme]);
  useEffect(() => { saveToLS(LS_SETTINGS, settings); }, [settings]);

  // ── 模式切换: 保存旧参数 → 加载新参数 → dispatchFullMode 全量下发 ──
  const prevModeRef = useRef(settings.mode);
  useEffect(() => {
    const prevMode = prevModeRef.current;
    const currentMode = settings.mode;
    prevModeRef.current = currentMode;
    if (prevMode === currentMode) return;

    // 保存当前参数到旧模式 localStorage key
    if (prevMode !== "custom") {
      saveToLS(LS_PARAMS_PREFIX + prevMode, uxtuParams);
    }

    // 切换到自定义模式 → 从服务端加载
    if (currentMode === "custom") {
      if (paramsLoaded) {
        fetch(API_CUSTOM_PARAMS)
          .then(r => r.json())
          .then(data => {
            if (data && Object.keys(data).length > 0) {
              if (data.gpuMemFreqMhz !== undefined && (data.gpuMemFreqMhz > 3 || data.gpuMemFreqMhz < 0)) {
                data.gpuMemFreqMhz = 1;
              }
              setUxtuParams(prev => ({ ...prev, ...data }));
            }
          })
          .catch(() => {});
      }
      // 仍然下发当前参数到硬件
      dispatchFullMode(currentMode, uxtuParams);
      return;
    }

    // 加载新模式: localStorage → MODE_PRESETS → FULL_PARAMS 兜底
    const saved = loadFromLS(LS_PARAMS_PREFIX + currentMode, null);
    const preset = MODE_PRESETS[currentMode] || {};
    const newParams = { ...FULL_PARAMS, ...preset, ...(saved || {}) };
    setUxtuParams(newParams);

    // 全量硬件下发 (含写入顺序保证)
    dispatchFullMode(currentMode, newParams);
  }, [settings.mode]);

  // ── 每模式 localStorage 持久化 (非自定义模式, 参数变化时保存) ──
  const prevParamsRef = useRef(uxtuParams);
  useEffect(() => {
    if (!paramsLoaded) return; // 跳过首次渲染，避免覆盖已保存数据
    if (settings.mode === "custom") return;
    if (prevParamsRef.current === uxtuParams) return;
    prevParamsRef.current = uxtuParams;
    saveToLS(LS_PARAMS_PREFIX + settings.mode, uxtuParams);
  }, [uxtuParams, settings.mode, paramsLoaded]);

  // ── 自定义模式: 服务端 + localStorage 持久化 (去抖 1s) ──
  const saveTimerRef = useRef(null);
  useEffect(() => {
    if (!paramsLoaded || settings.mode !== "custom") return;
    saveToLS(LS_PARAMS_PREFIX + "custom", uxtuParams);
    clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      fetch(API_CUSTOM_PARAMS, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(uxtuParams),
      })
        .then(r => { onSaveRef.current?.(r.ok); })
        .catch(() => onSaveRef.current?.(false));
    }, 1000);
    return () => clearTimeout(saveTimerRef.current);
  }, [uxtuParams, paramsLoaded, settings.mode]);

  // ── 启动时从服务端加载自定义参数 (仅自定义模式) ──
  useEffect(() => {
    const mode = loadFromLS(LS_SETTINGS, { mode: "office" }).mode;
    if (mode !== "custom") {
      setParamsLoaded(true);
      return;
    }
    fetch(API_CUSTOM_PARAMS)
      .then(r => r.json())
      .then(data => {
        if (data && Object.keys(data).length > 0) {
          if (data.gpuMemFreqMhz !== undefined && (data.gpuMemFreqMhz > 3 || data.gpuMemFreqMhz < 0)) {
            data.gpuMemFreqMhz = 1;
          }
          setUxtuParams(prev => ({ ...prev, ...data }));
        }
      })
      .catch(() => {})
      .finally(() => setParamsLoaded(true));
  }, []);

  // ── 风扇目标转速去抖下发 (600ms) ──
  const fanTimerRef = useRef(null);
  useEffect(() => {
    clearTimeout(fanTimerRef.current);
    fanTimerRef.current = setTimeout(() => {
      fetch("/api/fan/set-target", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          largeRpm: uxtuParams.fanLargeRpmTarget ?? 2900,
          smallRpm: uxtuParams.fanSmallRpmTarget ?? 6400,
        }),
      }).catch(() => {});
    }, 600);
    return () => clearTimeout(fanTimerRef.current);
  }, [uxtuParams.fanLargeRpmTarget, uxtuParams.fanSmallRpmTarget]);

  // ── uxtuPayload (用于手动下发按钮) ──
  const uxtuPayload = useMemo(
    () => ({ chipset: "Ryzen 9 8940HX", profile: settings.mode, params: uxtuParams }),
    [settings.mode, uxtuParams]
  );

  // ── WebSocket 遥测 ──
  const [backendOnline, setBackendOnline] = useState(false);
  useEffect(() => {
    let disposed = false;
    let ws;
    let reconnectTimer;

    const connect = () => {
      ws = createTelemetrySocket(
        (data) => {
          setBackendOnline(true);
          setTelemetry(prev => ({ ...prev, ...data }));
        },
        () => setBackendOnline(false)
      );
      ws.onclose = () => {
        setBackendOnline(false);
        if (!disposed) reconnectTimer = setTimeout(connect, 3000);
      };
    };

    connect();
    return () => {
      disposed = true;
      clearTimeout(reconnectTimer);
      if (ws) ws.close();
    };
  }, []);

  // ── Mock 模拟 (后端不可用时) ──
  useEffect(() => {
    if (backendOnline) return;
    if (lastTickRef.current === null) lastTickRef.current = Date.now();

    const timer = setInterval(() => {
      const now = Date.now();
      const dt = Math.max(0.5, Math.min(3, (now - (lastTickRef.current || Date.now())) / 1000));
      lastTickRef.current = now;

      setTelemetry(prev => {
        const fanLargeRpm = Math.round(prev.fanLargeRpm + (uxtuParams.fanLargeRpmTarget - prev.fanLargeRpm) * 0.1 * dt);
        const fanSmallRpm = Math.round(prev.fanSmallRpm + (uxtuParams.fanSmallRpmTarget - prev.fanSmallRpm) * 0.1 * dt);
        const fanLargePct = prev.fanLargeMax > 0 ? fanLargeRpm / prev.fanLargeMax : 0;
        const fanSmallPct = prev.fanSmallMax > 0 ? fanSmallRpm / prev.fanSmallMax : 0;
        const cooling = 0.4 * fanLargePct + 0.25 * fanSmallPct;
        const modeBias = settings.mode === "silent" ? -0.12 : settings.mode === "office" ? -0.05 : settings.mode === "gaming" ? 0.05 : settings.mode === "beast" ? 0.14 : 0;
        const cpuTargetUsage = Math.max(5, Math.min(95, 25 + (uxtuParams.cpuLongPptW / 120) * 55 + modeBias * 100));
        const gpuTargetUsage = Math.max(2, Math.min(95, 15 + (uxtuParams.gpuPptLimitW / 180) * 55 + modeBias * 80));
        const drift = (target, current, strength) => current + (target - current) * strength * dt + (Math.random() - 0.5) * 1.5;
        const nextCpuUsage = drift(cpuTargetUsage, prev.cpuUsage, 0.18);
        const nextGpuUsage = drift(gpuTargetUsage, prev.gpuUsage, 0.16);
        return {
          ...prev,
          cpuUsage: Math.round(nextCpuUsage),
          cpuFreq: Number(Math.max(0.6, prev.cpuFreq + (nextCpuUsage - prev.cpuUsage) * 0.02).toFixed(2)),
          cpuTemp: Math.round(drift(uxtuParams.cpuTempLimitC - cooling * 12, prev.cpuTemp, 0.10)),
          gpuUsage: Math.round(nextGpuUsage),
          gpuFreq: Number(Math.max(0.4, prev.gpuFreq + (nextGpuUsage - prev.gpuUsage) * 0.03).toFixed(2)),
          gpuTemp: Math.round(drift(uxtuParams.gpuTempLimitC - cooling * 10, prev.gpuTemp, 0.09)),
          memoryUsage: Math.round(Math.max(1, Math.min(99, prev.memoryUsage + (Math.random() - 0.5) * 0.8))),
          diskUsage: Math.round(Math.max(1, Math.min(99, prev.diskUsage + (Math.random() - 0.5) * 0.6))),
          fanLargeRpm, fanSmallRpm,
        };
      });
    }, 1000);
    return () => clearInterval(timer);
  }, [settings.mode, uxtuParams, backendOnline]);

  return {
    theme, setTheme,
    telemetry, setTelemetry,
    uxtuParams, setUxtuParams,
    settings, setSettings,
    uxtuPayload,
    history,
  };
}
