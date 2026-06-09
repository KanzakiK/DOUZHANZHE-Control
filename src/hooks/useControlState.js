import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { mockTelemetry } from "../data/mockTelemetry";
import {
  createTelemetrySocket, FULL_PARAMS, dispatchFullMode,
} from "../services/uxtuAdapter";

const LS_THEME = "douzhanzhe_theme";
const LS_SETTINGS = "douzhanzhe_settings";
const LS_PARAMS_PREFIX = "douzhanzhe_params_";      // 旧版全量存储（迁移用）
const LS_OVERRIDES_PREFIX = "douzhanzhe_overrides_"; // 新版稀疏存储
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

// ── Overrides 稀疏存储 API ──

function loadOverrides(mode) {
  return loadFromLS(LS_OVERRIDES_PREFIX + mode, {});
}

function saveOverride(mode, key, value) {
  const overrides = loadOverrides(mode);
  overrides[key] = value;
  saveToLS(LS_OVERRIDES_PREFIX + mode, overrides);
}

function clearOverrides(mode) {
  saveToLS(LS_OVERRIDES_PREFIX + mode, {});
}

// ── 旧数据清空（非自定义模式不迁移；自定义模式迁移到 overrides） ──

function clearOldParams() {
  const modes = ["silent", "office", "beast", "gaming"];
  // 非自定义模式：直接删除旧全量存储
  for (const mode of modes) {
    const oldKey = LS_PARAMS_PREFIX + mode;
    if (localStorage.getItem(oldKey)) {
      localStorage.removeItem(oldKey);
    }
  }
  // 自定义模式：迁移旧全量存储到 overrides（仅当 overrides 不存在时）
  const oldCustomKey = LS_PARAMS_PREFIX + "custom";
  const newCustomKey = LS_OVERRIDES_PREFIX + "custom";
  if (localStorage.getItem(oldCustomKey) && !localStorage.getItem(newCustomKey)) {
    try {
      const data = JSON.parse(localStorage.getItem(oldCustomKey));
      if (data && typeof data === "object") {
        localStorage.setItem(newCustomKey, JSON.stringify(data));
      }
    } catch {}
  }
  localStorage.removeItem(oldCustomKey);
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

  // ── uxtuParams: 唯一全量参数状态 (FULL_PARAMS 兆底 + overrides 覆盖) ──
  const [uxtuParams, setUxtuParams] = useState(() => {
    const mode = loadFromLS(LS_SETTINGS, { mode: "office" }).mode;
    
    // 启动时清空旧数据（不迁移）
    clearOldParams();
    
    // 加载 overrides
    const overrides = loadOverrides(mode);
    return { ...FULL_PARAMS, ...overrides };
  });

  const [paramsLoaded, setParamsLoaded] = useState(false);

  // ── Overrides 状态（暴露给组件，用于灰色/高亮显示）──
  const [overrides, setOverrides] = useState(() => {
    const mode = loadFromLS(LS_SETTINGS, { mode: "office" }).mode;
    return loadOverrides(mode);
  });

  // ── 重置参数到官方默认 ──
  const resetParams = useCallback((mode) => {
    // 1. 清空 overrides (localStorage + UI 状态)
    clearOverrides(mode);
    setOverrides({});
    
    // 2. UI 参数回到 FULL_PARAMS 兆底值（包含该模式的风扇默认转速）
    setUxtuParams({ ...FULL_PARAMS });
    
    console.log("[resetParams] mode:", mode, "FULL_PARAMS:", FULL_PARAMS);
  }, [FULL_PARAMS]);

  // 持久化 theme + settings
  useEffect(() => { saveToLS(LS_THEME, theme); }, [theme]);
  useEffect(() => { saveToLS(LS_SETTINGS, settings); }, [settings]);

  // ── 模式切换: 加载新 overrides → dispatchFullMode 条件下发 ──
  const prevModeRef = useRef(settings.mode);
  useEffect(() => {
    const prevMode = prevModeRef.current;
    const currentMode = settings.mode;
    prevModeRef.current = currentMode;
    if (prevMode === currentMode) return;
  
    // 切换到自定义模式 → 加载 overrides + 从服务端加载
    if (currentMode === "custom") {
      const customOverrides = loadOverrides("custom");
      const customParams = { ...FULL_PARAMS, ...customOverrides };
      setUxtuParams(customParams);
      setOverrides(customOverrides);

      if (paramsLoaded) {
        fetch(API_CUSTOM_PARAMS)
          .then(r => r.json())
          .then(data => {
            if (data && Object.keys(data).length > 0) {
              if (data.gpuMemFreqMhz !== undefined && (data.gpuMemFreqMhz > 3 || data.gpuMemFreqMhz < 0)) {
                data.gpuMemFreqMhz = 1;
              }
              setUxtuParams(prev => ({ ...prev, ...data }));
              setOverrides(data);
            }
          })
          .catch(() => {});
      }
      // custom 模式没有 thermal_mode，下发全部参数
      dispatchFullMode(currentMode, customOverrides);
      return;
    }
  
    // 加载新模式的 overrides
    const newOverrides = loadOverrides(currentMode);
    const newParams = { ...FULL_PARAMS, ...newOverrides };
    setUxtuParams(newParams);
    setOverrides(newOverrides);
  
    // 条件下发硬件命令（overrides 为空时只发 thermal_mode）
    dispatchFullMode(currentMode, newOverrides);
  }, [settings.mode]);

  // ── 自定义模式: 服务端 + localStorage 持久化 (去抖 1s) ──
  const saveTimerRef = useRef(null);
  useEffect(() => {
    if (!paramsLoaded || settings.mode !== "custom") return;
    saveToLS(LS_OVERRIDES_PREFIX + "custom", uxtuParams);
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

  // ── Overrides 稀疏存储操作（暴露给组件） ──
  const saveOverrideFn = useCallback((mode, key, value) => {
    saveOverride(mode, key, value);
    setOverrides(prev => ({ ...prev, [key]: value }));
  }, []);

  const clearOverridesFn = useCallback((mode) => {
    clearOverrides(mode);
    setOverrides({});
  }, []);

  return {
    theme, setTheme,
    telemetry, setTelemetry,
    uxtuParams, setUxtuParams,
    settings, setSettings,
    uxtuPayload,
    history,
    overrides, setOverrides,
    saveOverride: saveOverrideFn,
    clearOverrides: clearOverridesFn,
    resetParams,
  };
}
