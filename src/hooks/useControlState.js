import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { mockTelemetry } from "../data/mockTelemetry";
import { createTelemetrySocket } from "../services/uxtuAdapter";

const LS_THEME = "douzhanzhe_theme";
const LS_SETTINGS = "douzhanzhe_settings";
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

// 性能模式预设值映射
const MODE_PRESETS = {
  silent: {
    cpuTempLimitC: 75,
    cpuLongPptW: 35,
    cpuShortPptW: 45,
    gpuPptLimitW: 60,
    gpuTempLimitC: 75,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 1800,
    gpuFreqLocked: false,
    fanLargeRpmTarget: 1320,
    fanSmallRpmTarget: 2460,
  },
  office: {
    cpuTempLimitC: 80,
    cpuLongPptW: 55,
    cpuShortPptW: 70,
    gpuPptLimitW: 75,
    gpuTempLimitC: 85,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 2200,
    gpuFreqLocked: false,
    fanLargeRpmTarget: 2200,
    fanSmallRpmTarget: 4100,
  },
  gaming: {
    cpuTempLimitC: 88,
    cpuLongPptW: 85,
    cpuShortPptW: 100,
    gpuPptLimitW: 100,
    gpuTempLimitC: 90,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 2600,
    gpuFreqLocked: false,
    fanLargeRpmTarget: 3300,
    fanSmallRpmTarget: 6150,
  },
  beast: {
    cpuTempLimitC: 95,
    cpuLongPptW: 120,
    cpuShortPptW: 140,
    gpuPptLimitW: 115,
    gpuTempLimitC: 95,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 3000,
    gpuFreqLocked: false,
    fanLargeRpmTarget: 4400,
    fanSmallRpmTarget: 8200,
  },
  custom: {
    cpuTempLimitC: 90,
    cpuLongPptW: 65,
    cpuShortPptW: 85,
    gpuPptLimitW: 115,
    gpuTempLimitC: 87,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 2600,
    gpuFreqLocked: false,
    fanLargeRpmTarget: 2200,
    fanSmallRpmTarget: 4100,
  },
};

export function useControlState(onSaveResult) {
  const onSaveRef = useRef(onSaveResult);
  onSaveRef.current = onSaveResult;
  const [theme, setTheme] = useState(() => loadFromLS(LS_THEME, "theme-mech-violet"));
  const [telemetry, setTelemetry] = useState(mockTelemetry);
  const lastTickRef = useRef(null);

  // 遥测历史数据 — 用于实时负载曲线
  const MAX_HISTORY = 60;
  const [history, setHistory] = useState({
    cpu: [],
    gpu: [],
    fan: [],
    cpuTemp: [],
    gpuTemp: [],
  });

  // 每次 telemetry 更新时追加到历史
  const prevTelemetryRef = useRef(telemetry);
  useEffect(() => {
    if (prevTelemetryRef.current === telemetry) return;
    prevTelemetryRef.current = telemetry;
    setHistory((prev) => {
      const cpu = [...prev.cpu, telemetry.cpuUsage].slice(-MAX_HISTORY);
      const gpu = [...prev.gpu, telemetry.gpuUsage].slice(-MAX_HISTORY);
      const fan = [...prev.fan, telemetry.fanLargeRpm].slice(-MAX_HISTORY);
      const cpuTemp = [...prev.cpuTemp, telemetry.cpuTemp].slice(-MAX_HISTORY);
      const gpuTemp = [...prev.gpuTemp, telemetry.gpuTemp].slice(-MAX_HISTORY);
      return { cpu, gpu, fan, cpuTemp, gpuTemp };
    });
  }, [telemetry]);

  const defaultParams = {
    cpuFreqLimitEnabled: false,
    cpuFreqLimitMhz: 4500,
    cpuTurboDisabled: false,
    cpuTempLimitC: 90,
    cpuCoreLimit: 0,
    cpuPowerPlan: "balance",
    cpuVoltageOffset: 0,
    cpuLongPptW: 65,
    cpuShortPptW: 85,
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 2600,
    gpuCoreFreqMhz: 2700,
    gpuMemFreqMhz: 1,
    gpuFreqLocked: false,
    gpuPptLimitW: 115,
    gpuTempLimitC: 87,
  };
  const [uxtuParams, setUxtuParams] = useState(() => {
    const saved = loadFromLS(LS_SETTINGS, { mode: "office" });
    const preset = MODE_PRESETS[saved.mode];
    return preset ? { ...defaultParams, ...preset } : defaultParams;
  });
  const [paramsLoaded, setParamsLoaded] = useState(false);

  // 启动时从服务端加载自定义参数（仅自定义模式下应用）
  useEffect(() => {
    fetch(API_CUSTOM_PARAMS)
      .then((r) => r.json())
      .then((data) => {
        if (data && Object.keys(data).length > 0) {
          // Normalize gpuMemFreqMhz from old MHz values to index 0-3
          if (data.gpuMemFreqMhz !== undefined && (data.gpuMemFreqMhz > 3 || data.gpuMemFreqMhz < 0)) {
            data.gpuMemFreqMhz = 1;
          }
          // 仅在自定义模式下应用服务端保存的参数，其他模式走 MODE_PRESETS
          setUxtuParams((prev) => {
            const mode = loadFromLS(LS_SETTINGS, { mode: "office" }).mode;
            if (mode === "custom") return { ...prev, ...data };
            return prev;
          });
        }
      })
      .catch(() => {})
      .finally(() => setParamsLoaded(true));
  }, []);

  const [fanLargeRpmTarget, setFanLargeRpmTarget] = useState(() => loadFromLS("douzhanzhe_fan_large", 2200));
  const [fanSmallRpmTarget, setFanSmallRpmTarget] = useState(() => loadFromLS("douzhanzhe_fan_small", 4100));

  // 风扇目标转速变化时保存到 localStorage + 调用 C# API（去抖 600ms）
  const fanTimerRef = useRef(null);
  useEffect(() => {
    saveToLS("douzhanzhe_fan_large", fanLargeRpmTarget);
    saveToLS("douzhanzhe_fan_small", fanSmallRpmTarget);
    clearTimeout(fanTimerRef.current);
    fanTimerRef.current = setTimeout(() => {
      fetch("/api/fan/set-target", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ largeRpm: fanLargeRpmTarget, smallRpm: fanSmallRpmTarget }),
      }).catch(() => {});
    }, 600);
    return () => clearTimeout(fanTimerRef.current);
  }, [fanLargeRpmTarget, fanSmallRpmTarget]);

  const [settings, setSettings] = useState(() => loadFromLS(LS_SETTINGS, {
    mode: "office",
    dGpuDirect: true,
    fanBoost: false,
    numLock: true,
    capsLock: false,
    fnLock: false,
    touchpadLock: false,
    osdDisabled: false,
    kbBrightnessLevel: 0,
  }));

  // 持久化 theme 和 settings 到 localStorage
  useEffect(() => { saveToLS(LS_THEME, theme); }, [theme]);
  useEffect(() => { saveToLS(LS_SETTINGS, settings); }, [settings]);

  // 自定义参数持久化到服务端（去抖 1s）— 仅在自定义模式下保存
  const saveTimerRef = useRef(null);
  useEffect(() => {
    if (!paramsLoaded || settings.mode !== "custom") return;
    clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => {
      const payload = {
        ...uxtuParams,
        fanLargeRpmTarget,
        fanSmallRpmTarget,
      };
      fetch(API_CUSTOM_PARAMS, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      })
        .then((r) => {
          if (r.ok) onSaveRef.current?.(true);
          else onSaveRef.current?.(false);
        })
        .catch(() => onSaveRef.current?.(false));
    }, 1000);
    return () => clearTimeout(saveTimerRef.current);
  }, [uxtuParams, paramsLoaded, fanLargeRpmTarget, fanSmallRpmTarget, settings.mode]);

  const uxtuPayload = useMemo(
    () => ({
      chipset: "Ryzen 9 8940HX",
      profile: settings.mode,
      params: uxtuParams,
    }),
    [settings.mode, uxtuParams]
  );

  // 当性能模式改变时，自动更新预设值
  const prevModeRef = useRef(settings.mode);
  useEffect(() => {
    const prevMode = prevModeRef.current;
    const currentMode = settings.mode;
    prevModeRef.current = currentMode;

    // 切换到自定义模式 → 从服务端恢复保存的参数
    if (currentMode === "custom") {
      if (paramsLoaded) {
        fetch(API_CUSTOM_PARAMS)
          .then((r) => r.json())
          .then((data) => {
            if (data && Object.keys(data).length > 0) {
              const { fanLargeRpmTarget: f1, fanSmallRpmTarget: f2, ...rest } = data;
              setUxtuParams(rest);
              if (f1 !== undefined) setFanLargeRpmTarget(f1);
              if (f2 !== undefined) setFanSmallRpmTarget(f2);
            }
          })
          .catch(() => {});
      }
      return;
    }

    const preset = MODE_PRESETS[currentMode];
    if (!preset) return;

    setUxtuParams((prev) => ({
      ...prev,
      cpuTempLimitC: preset.cpuTempLimitC,
      cpuLongPptW: preset.cpuLongPptW,
      cpuShortPptW: preset.cpuShortPptW,
      gpuPptLimitW: preset.gpuPptLimitW,
      gpuTempLimitC: preset.gpuTempLimitC,
      gpuCoreFreqMhz: preset.gpuCoreFreqMhz,
      gpuMemFreqMhz: preset.gpuMemFreqMhz,
      gpuFreqLimitEnabled: preset.gpuFreqLimitEnabled,
      gpuFreqLimitMhz: preset.gpuFreqLimitMhz,
      gpuFreqLocked: preset.gpuFreqLocked,
    }));

    setFanLargeRpmTarget(preset.fanLargeRpmTarget);
    setFanSmallRpmTarget(preset.fanSmallRpmTarget);
  }, [settings.mode]);

  // 连接后端 WebSocket 获取真实硬件遥测；后端不可用时退回到 mock
  const [backendOnline, setBackendOnline] = useState(false);

  useEffect(() => {
    const ws = createTelemetrySocket(
      (data) => {
        setBackendOnline(true);
        setTelemetry((prev) => ({
          ...prev,           // 保留 cpuUsage, memoryUsage 等完整字段
          ...data,           // WebSocket 新数据覆盖温度/风扇等实时字段
        }));
      },
      () => setBackendOnline(false)
    );
    return () => ws.close();
  }, []);

  // 后端不可用时，使用 mock 模拟数据
  useEffect(() => {
    if (backendOnline) return;
    if (lastTickRef.current === null) {
      lastTickRef.current = Date.now();
    }

    const timer = setInterval(() => {
      const now = Date.now();
      const dt = Math.max(0.5, Math.min(3, (now - (lastTickRef.current || Date.now())) / 1000));
      lastTickRef.current = now;

      setTelemetry((prev) => {
        // 风扇实际转速：向目标转速缓慢趋近
        const fanLargeRpm = Math.round(
          prev.fanLargeRpm + (fanLargeRpmTarget - prev.fanLargeRpm) * 0.1 * dt
        );
        const fanSmallRpm = Math.round(
          prev.fanSmallRpm + (fanSmallRpmTarget - prev.fanSmallRpm) * 0.1 * dt
        );

        const fanLargePct = prev.fanLargeMax > 0 ? fanLargeRpm / prev.fanLargeMax : 0;
        const fanSmallPct = prev.fanSmallMax > 0 ? fanSmallRpm / prev.fanSmallMax : 0;
        const cooling = 0.4 * fanLargePct + 0.25 * fanSmallPct;

        const modeBias =
          settings.mode === "silent"
            ? -0.12
            : settings.mode === "office"
              ? -0.05
              : settings.mode === "gaming"
                ? 0.05
                : settings.mode === "beast"
                  ? 0.14
                  : 0.0;

        const cpuPptNorm = uxtuParams.cpuLongPptW / 120;
        const gpuPptNorm = uxtuParams.gpuPptLimitW / 180;

        const cpuTargetUsage = Math.max(5, Math.min(95, 25 + cpuPptNorm * 55 + modeBias * 100));
        const gpuTargetUsage = Math.max(2, Math.min(95, 15 + gpuPptNorm * 55 + modeBias * 80));

        const drift = (target, current, strength) =>
          current + (target - current) * strength * dt + (Math.random() - 0.5) * 1.5;

        const nextCpuUsage = drift(cpuTargetUsage, prev.cpuUsage, 0.18);
        const nextGpuUsage = drift(gpuTargetUsage, prev.gpuUsage, 0.16);

        const nextCpuFreq = Math.max(0.6, prev.cpuFreq + (nextCpuUsage - prev.cpuUsage) * 0.02);
        const nextGpuFreq = Math.max(0.4, prev.gpuFreq + (nextGpuUsage - prev.gpuUsage) * 0.03);

        const cpuTempTarget = uxtuParams.cpuTempLimitC - cooling * 12;
        const gpuTempTarget = uxtuParams.gpuTempLimitC - cooling * 10;

        const nextCpuTemp = drift(cpuTempTarget, prev.cpuTemp, 0.10);
        const nextGpuTemp = drift(gpuTempTarget, prev.gpuTemp, 0.09);

        const nextMemoryUsage = Math.max(1, Math.min(99, prev.memoryUsage + (Math.random() - 0.5) * 0.8));
        const nextDiskUsage = Math.max(1, Math.min(99, prev.diskUsage + (Math.random() - 0.5) * 0.6));

        return {
          ...prev,
          cpuUsage: Math.round(nextCpuUsage),
          cpuFreq: Number(nextCpuFreq.toFixed(2)),
          cpuTemp: Math.round(nextCpuTemp),
          gpuUsage: Math.round(nextGpuUsage),
          gpuFreq: Number(nextGpuFreq.toFixed(2)),
          gpuTemp: Math.round(nextGpuTemp),
          memoryUsage: Math.round(nextMemoryUsage),
          diskUsage: Math.round(nextDiskUsage),
          fanLargeRpm,
          fanSmallRpm,
        };
      });
    }, 1000);

    return () => clearInterval(timer);
  }, [settings.mode, uxtuParams.cpuLongPptW, uxtuParams.cpuTempLimitC, uxtuParams.gpuPptLimitW, uxtuParams.gpuTempLimitC, backendOnline]);

  return {
    theme,
    setTheme,
    telemetry,
    setTelemetry,
    uxtuParams,
    setUxtuParams,
    settings,
    setSettings,
    uxtuPayload,
    fanLargeRpmTarget,
    fanSmallRpmTarget,
    setFanLargeRpmTarget,
    setFanSmallRpmTarget,
    history,
  };
}
