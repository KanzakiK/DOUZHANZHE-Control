import { useEffect, useMemo, useRef, useState } from "react";
import { mockTelemetry } from "../data/mockTelemetry";
import { createTelemetrySocket } from "../services/uxtuAdapter";

// 性能模式预设值映射
const MODE_PRESETS = {
  silent: {
    cpuTempLimitC: 75,
    cpuLongPptW: 35,
    cpuShortPptW: 45,
    fanLargeRpmTarget: 1320,
    fanSmallRpmTarget: 2460,
  },
  office: {
    cpuTempLimitC: 80,
    cpuLongPptW: 55,
    cpuShortPptW: 70,
    fanLargeRpmTarget: 2200,
    fanSmallRpmTarget: 4100,
  },
  gaming: {
    cpuTempLimitC: 88,
    cpuLongPptW: 85,
    cpuShortPptW: 100,
    fanLargeRpmTarget: 3300,
    fanSmallRpmTarget: 6150,
  },
  beast: {
    cpuTempLimitC: 95,
    cpuLongPptW: 120,
    cpuShortPptW: 140,
    fanLargeRpmTarget: 4400,
    fanSmallRpmTarget: 8200,
  },
  custom: {
    cpuTempLimitC: 90,
    cpuLongPptW: 65,
    cpuShortPptW: 85,
    fanLargeRpmTarget: 2200,
    fanSmallRpmTarget: 4100,
  },
};

export function useControlState() {
  const [theme, setTheme] = useState("theme-mech-violet");
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

  const [uxtuParams, setUxtuParams] = useState({
    // CPU
    cpuFreqLimitEnabled: false,
    cpuFreqLimitMhz: 4500,
    cpuTurboDisabled: false,
    cpuTempLimitC: 90,
    cpuCoreLimit: 0,        // 0=不限制, 2,4,6,8,10,12,14
    cpuPowerPlan: "balance", // balance, performance, efficiency
    cpuVoltageOffset: 0,     // 0, -5, -10, -20 (mV)
    cpuLongPptW: 65,
    cpuShortPptW: 85,
    // GPU
    gpuFreqLimitEnabled: false,
    gpuFreqLimitMhz: 2600,
    gpuCoreOffsetMhz: 0,
    gpuMemOffsetMhz: 0,
    gpuFreqLocked: false,
    gpuPptLimitW: 115,
    gpuTempLimitC: 87,
  });

  const [fanLargeRpmTarget, setFanLargeRpmTarget] = useState(2200);
  const [fanSmallRpmTarget, setFanSmallRpmTarget] = useState(4100);

  const [settings, setSettings] = useState({
    mode: "office",
    dGpuDirect: true,
    fanBoost: false,
    numLock: true,
    capsLock: false,
    fnLock: false,
    touchpadLock: false,
    osdDisabled: false,
    kbBrightnessLevel: 0,
  });

  const uxtuPayload = useMemo(
    () => ({
      chipset: "Ryzen 9 8940HX",
      profile: settings.mode,
      params: uxtuParams,
    }),
    [settings.mode, uxtuParams]
  );

  // 当性能模式改变时，自动更新预设值
  useEffect(() => {
    const preset = MODE_PRESETS[settings.mode];
    if (!preset) return;

    setUxtuParams((prev) => ({
      ...prev,
      cpuTempLimitC: preset.cpuTempLimitC,
      cpuLongPptW: preset.cpuLongPptW,
      cpuShortPptW: preset.cpuShortPptW,
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
        setTelemetry(data);
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
