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
  // 不在此处自动重连，由调用方 (useControlState) 管理生命周期
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

// 全量参数默认值 (兆底，用于 UI 层显示)
// 注意：风扇转速随模式变化，这里只是占位符，实际使用时会被各模式默认值覆盖
export const FULL_PARAMS = {
  cpuFreqLimitEnabled: false, cpuFreqLimitMhz: 4500, cpuTurboDisabled: false,
  cpuTempLimitC: 80, cpuCoreLimit: 0, cpuPowerPlan: "balance", cpuVoltageOffset: 0,
  cpuLongPptW: 55, cpuShortPptW: 70,
  gpuFreqLimitEnabled: false, gpuFreqLimitMhz: 2600, gpuCoreFreqMhz: 2750,
  gpuMemFreqMhz: 0, gpuPptLimitW: 75, gpuTempLimitC: 85,
  ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0,
  fanLargeRpmTarget: 2900, fanSmallRpmTarget: 5200,  // 均衡模式默认
};

// 各模式的 EC 官方风扇默认转速（用于恢复默认时设置正确的 UI 值）
export const MODE_FAN_DEFAULTS = {
  silent: { fanLargeRpmTarget: 2200, fanSmallRpmTarget: 4100 },
  office: { fanLargeRpmTarget: 2900, fanSmallRpmTarget: 5200 },
  gaming: { fanLargeRpmTarget: 3500, fanSmallRpmTarget: 6400 },
  beast:  { fanLargeRpmTarget: 3800, fanSmallRpmTarget: 7200 },
  custom: { fanLargeRpmTarget: 2900, fanSmallRpmTarget: 5200 },  // custom 默认用均衡
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

// ── 散热曲线 (Fan Curve) ──

export async function fetchFanCurveStatus() {
  const res = await fetch("/api/fan-curve/status");
  if (!res.ok) throw new Error("Fan curve status returned " + res.status);
  return res.json();
}

export async function saveFanCurve(points, intervalMs, hysteresisC) {
  const res = await fetch("/api/fan-curve/save", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ points, intervalMs, hysteresisC }),
  });
  if (!res.ok) throw new Error("Fan curve save returned " + res.status);
  return res.json();
}

export async function startFanCurve(intervalMs, hysteresisC) {
  const res = await fetch("/api/fan-curve/start", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ intervalMs, hysteresisC }),
  });
  if (!res.ok) throw new Error("Fan curve start returned " + res.status);
  return res.json();
}

export async function stopFanCurve() {
  const res = await fetch("/api/fan-curve/stop", { method: "POST" });
  if (!res.ok) throw new Error("Fan curve stop returned " + res.status);
  return res.json();
}

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

// ── 恢复官方默认 ──
// 清空 overrides + 重发 thermal_mode (EC 恢复出厂值) + resetCpuPower
export async function resetToFactoryDefaults(mode) {
  // 1. 清空 overrides (localStorage + UI 状态)
  localStorage.setItem("douzhanzhe_overrides_" + mode, "{}");
  
  // 2. 重发 thermal_mode (EC 重新加载出厂值，包括 CPU/GPU/风扇预设)
  const tv = thermalModeMap[mode];
  if (tv !== null && tv !== undefined) {
    await applyHardwareControl("thermal_mode", tv);
  }
  
  // 3. CPU 频率/睿频/核心数通过 Windows powercfg 控制，必须单独恢复
  await resetCpuPower().catch(e => console.warn("resetCpuPower:", e));
}


// ── 全量模式下发（overrides 感知） ──
// overrides 为空时只发 thermal_mode，非空时按通道下发用户改过的字段
// 顺序: 1. thermal_mode → 2. SMU → 3. GPU → 4. NVAPI → 5. CPU powercfg → 6. 风扇 → 7. SMU 重发
export async function dispatchFullMode(mode, overrides) {
  const tv = thermalModeMap[mode];
  const isEmpty = !overrides || Object.keys(overrides).length === 0;

  // ① thermal_mode 永远执行（切模式的基础）
  if (tv !== null && tv !== undefined) {
    applyHardwareControl("thermal_mode", tv).catch(e => console.warn("[EC] thermal_mode:", e));
  }

  // overrides 为空时，只发 thermal_mode，其他全部跳过
  if (isEmpty) {
    console.log("[dispatch] overrides 为空，仅发送 thermal_mode");
    return;
  }

  // ② SMU 批量写入（仅当 overrides 有 CPU SMU 字段时执行）
  const smuFields = ["cpuLongPptW", "cpuShortPptW", "cpuTempLimitC", "cpuVoltageOffset"];
  const hasSmu = smuFields.some(f => f in overrides);
  if (hasSmu) {
    await applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides }).catch(
      e => console.warn("[SMU] batch:", e)
    );
  }

  // ③ GPU 频率控制（nvidia-smi: unlock → limit → lock）
  const gpuFields = ["gpuFreqLimitEnabled", "gpuCoreFreqMhz", "gpuMemFreqMhz"];
  const hasGpu = gpuFields.some(f => f in overrides);
  if (hasGpu) {
    try {
      await applyGpuControl("reset-clocks");
      if (overrides.gpuFreqLimitEnabled && overrides.gpuCoreFreqMhz !== GPU_BASE_CLOCK) {
        await applyGpuControl("limit-max", overrides.gpuCoreFreqMhz);
        await applyGpuControl("lock-exact", overrides.gpuCoreFreqMhz);
      }
    } catch (e) { console.warn("[GPU] freq:", e); }

    // GPU 显存频率
    try {
      const memMap = [0, 9001, 11001, 12001];
      if (overrides.gpuMemFreqMhz === 0 || overrides.gpuMemFreqMhz === undefined) {
        await applyGpuControl("reset-memory-clocks");
      } else {
        await applyGpuControl("limit-memory", memMap[overrides.gpuMemFreqMhz]);
      }
    } catch (e) { console.warn("[GPU] memory:", e); }
  }

  // ④ NVAPI: 超频偏移 + 温度限制（并行）
  const nvapiFields = ["ocCoreOffsetMhz", "ocMemOffsetMhz", "gpuTempLimitC"];
  const hasNvapi = nvapiFields.some(f => f in overrides);
  if (hasNvapi) {
    const thermalC = clampParam("gpuTempLimitC", overrides.gpuTempLimitC ?? 87);
    Promise.all([
      applyNvapiOverclock(overrides.ocCoreOffsetMhz ?? 0, overrides.ocMemOffsetMhz ?? 0).catch(
        e => console.warn("[NVAPI] OC:", e)
      ),
      applyNvapiThermalLimit(thermalC).catch(
        e => console.warn("[NVAPI] thermal:", e)
      ),
    ]);
  }

  // ⑤ CPU powercfg: 频率限制 / 睿频 / 核心数 / 电源计划
  const cpuFields = ["cpuFreqLimitEnabled", "cpuFreqLimitMhz", "cpuTurboDisabled", "cpuCoreLimit", "cpuPowerPlan"];
  const hasCpu = cpuFields.some(f => f in overrides);
  if (hasCpu) {
    if ("cpuFreqLimitEnabled" in overrides || "cpuFreqLimitMhz" in overrides) {
      setCpuFreqLimit(overrides.cpuFreqLimitEnabled ? overrides.cpuFreqLimitMhz : 0).catch(
        e => console.warn("[CPU] freq:", e)
      );
    }
    if ("cpuTurboDisabled" in overrides) {
      setCpuTurbo(!overrides.cpuTurboDisabled).catch(
        e => console.warn("[CPU] turbo:", e)
      );
    }
    if ("cpuCoreLimit" in overrides) {
      if (overrides.cpuCoreLimit > 0) {
        setCpuCoreLimitPercent(Math.round(overrides.cpuCoreLimit / 16 * 100)).catch(
          e => console.warn("[CPU] core-limit:", e)
        );
      } else {
        setCpuCoreLimitPercent(100).catch(() => {});
      }
    }
    if ("cpuPowerPlan" in overrides) {
      const ppHal = powerPlanHALMap[overrides.cpuPowerPlan];
      if (ppHal !== undefined) {
        applyHardwareControl("power_plan", ppHal).catch(
          e => console.warn("[CPU] power-plan:", e)
        );
      }
    }
  }

  // ⑥ 风扇目标转速
  const fanFields = ["fanLargeRpmTarget", "fanSmallRpmTarget"];
  const hasFan = fanFields.some(f => f in overrides);
  if (hasFan) {
    fetch("/api/fan/set-target", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        largeRpm: overrides.fanLargeRpmTarget ?? 2900,
        smallRpm: overrides.fanSmallRpmTarget ?? 6400,
      }),
    }).catch(e => console.warn("[Fan]:", e));
  }

  // ⑦ 延迟重发 SMU（两次），仅当 SMU 实际执行时触发
  if (hasSmu) {
    setTimeout(() => {
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides })
        .then(r => console.log("[SMU] re-send OK:", r))
        .catch(e => console.warn("[SMU] re-send failed:", e));
    }, 500);
    setTimeout(() => {
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: overrides })
        .then(r => console.log("[SMU] re-send2 OK:", r))
        .catch(e => console.warn("[SMU] re-send2 failed:", e));
    }, 1500);
  }
}
