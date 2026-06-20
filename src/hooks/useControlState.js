import { useCallback, useEffect, useRef, useState } from "react";
import { mockTelemetry } from "../data/mockTelemetry";
import {
  createTelemetrySocket, FULL_PARAMS, MODE_FAN_DEFAULTS,
  fetchOverrides, switchMode, syncOverrides, reapplyOverrides, log,
  migrateLocalStorageOverrides, flattenBackendOverrides,
} from "../services/uxtuAdapter";

const LS_THEME = "douzhanzhe_theme";
const LS_SETTINGS = "douzhanzhe_settings";

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

export function useControlState() {

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
    const fanDefaults = MODE_FAN_DEFAULTS["office"] || {};
    return { ...FULL_PARAMS, ...fanDefaults };
  });

  // ── Overrides 状态（暴露给组件，用于灰色/高亮显示）──
  const [overrides, setOverrides] = useState({});

  // 标记首次加载，防止 fetchOverrides 设置 mode 时触发 switchMode 副作用
  const initialLoadRef = useRef(true);

  // 启动时：先迁移 localStorage 旧数据到后端，再拉取 overrides
  useEffect(() => {
    (async () => {
      try {
        const count = await migrateLocalStorageOverrides();
        if (count > 0) log("Startup", `migrated ${count} mode(s) from localStorage`);
      } catch (e) {
        log("Startup", `localStorage migration error: ${e.message}`);
      }
      try {
        const { mode, overrides: rawOv } = await fetchOverrides();
        const ov = flattenBackendOverrides(rawOv);
        setSettings(prev => {
          prevModeRef.current = mode; // 同步 prevModeRef，防止触发 switchMode
          return { ...prev, mode };
        });
        const fanDefaults = MODE_FAN_DEFAULTS[mode] || {};
        setUxtuParams({ ...FULL_PARAMS, ...fanDefaults, ...ov });
        setOverrides(ov);
        initialLoadRef.current = false;
      } catch {
        // 后端不可用时回退到 FULL_PARAMS 默认
        initialLoadRef.current = false;
      }
    })();
  }, []);

  // ── 重置参数到官方默认 ──
  const resetParams = useCallback(async (mode) => {
    await syncOverrides(mode, {});
    setOverrides({});
    const fanDefaults = MODE_FAN_DEFAULTS[mode] || {};
    setUxtuParams({ ...FULL_PARAMS, ...fanDefaults });
  }, []);

  // 持久化 theme + settings
  useEffect(() => { saveToLS(LS_THEME, theme); }, [theme]);
  useEffect(() => { saveToLS(LS_SETTINGS, settings); }, [settings]);

  // ── 模式切换: switchMode 后端切换 + reapplyOverrides 硬件下发 ──
  const prevModeRef = useRef(settings.mode);

  useEffect(() => {
    // 首次加载由 startup useEffect 处理，这里跳过
    if (initialLoadRef.current) return;

    const prevMode = prevModeRef.current;
    const currentMode = settings.mode;
    prevModeRef.current = currentMode;
    if (prevMode === currentMode) return;

    // switchMode 端点内部已完成 thermal_mode + last-mode.json + ProcessMonitor 同步
    switchMode(currentMode).then(({ overrides: rawOv }) => {
      const ov = flattenBackendOverrides(rawOv);
      const fanDefaults = MODE_FAN_DEFAULTS[currentMode] || {};
      setUxtuParams({ ...FULL_PARAMS, ...fanDefaults, ...ov });
      setOverrides(ov);
      reapplyOverrides(currentMode, ov); // 只发硬件命令，不写文件
    }).catch(() => {
      const fanDefaults = MODE_FAN_DEFAULTS[currentMode] || {};
      setUxtuParams({ ...FULL_PARAMS, ...fanDefaults });
      setOverrides({});
    });
  }, [settings.mode]);

  // ── WebSocket 遥测 + 自动切换 ──
  const [backendOnline, setBackendOnline] = useState(false);
  useEffect(() => {
    let disposed = false;
    let ws;
    let reconnectTimer;

    const connect = () => {
      ws = createTelemetrySocket(
        (data) => {
          setBackendOnline(true);

          // 处理自动切换消息
          if (data.type === "auto_switch" && data.mode) {
            log("AutoSwitch", `收到自动切换请求: ${data.mode}`);
            setSettings(prev => prev.mode === data.mode ? prev : { ...prev, mode: data.mode });
            return; // auto_switch 消息不包含遥测数据
          }

          // 处理遥测数据
          setTelemetry(prev => ({ ...prev, ...data }));
          // 自动同步 dGpuDirect 与实际 GPU mode（mode 1=独显→true，0/2→false）
          if (data.gpuMode != null) {
            const gpuMode = parseInt(data.gpuMode);
            const shouldBeOn = gpuMode === 1;
            setSettings(prev => prev.dGpuDirect === shouldBeOn ? prev : { ...prev, dGpuDirect: shouldBeOn });
          }
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

  // ── Overrides 稀疏存储操作（仅更新 React state，持久化由各独立端点完成） ──
  const saveOverrideFn = useCallback((mode, key, value) => {
    setOverrides(prev => ({ ...prev, [key]: value }));
  }, []);

  return {
    theme, setTheme,
    telemetry, setTelemetry,
    uxtuParams, setUxtuParams,
    settings, setSettings,
    history,
    overrides, setOverrides,
    saveOverride: saveOverrideFn,
    resetParams,
  };
}
