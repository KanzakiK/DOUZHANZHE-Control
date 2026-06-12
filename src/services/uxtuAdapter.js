const BACKEND = "";

// ── SMU 重发取消机制 ──
// 快速切模式时，新 dispatch 必须取消上一轮 dispatch 遗留的 setTimeout，
// 否则旧闭包里的 mode/overrides 会覆盖新模式的 SMU 值
let _smuResendTimers = [];   // 存储 setTimeout 返回的 timer ID
let _smuDispatchGen = 0;     // generation 计数器，用于二次校验

function cancelSmuResendTimers() {
  for (const id of _smuResendTimers) clearTimeout(id);
  _smuResendTimers = [];
}

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

export const CPU_BASE_CLOCK = 2400; // Ryzen 9 8940HX 基础频率 (WMI MaxClockSpeed ≈ 2401 MHz)
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


// 全范围风扇转速（EC 直写 ITSM 方案：全区间可用，不再受模式限制）
// 大扇: 安静下限 1900 ~ 斗战上限 4400，小扇: 安静下限 1700 ~ 斗战上限 8200
export const FULL_FAN_RANGE = {
  largeMin: 1900, largeMax: 4400,
  smallMin: 1700, smallMax: 8200,
};

// 保留各模式区间作为参考（仅供路由表使用，前端不再显示区间限制）
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
  gpuMemFreqMhz: 0, gpuPptLimitW: 75, gpuTempLimitC: 87,
  ocCoreOffsetMhz: 0, ocMemOffsetMhz: 0,
  fanLargeRpmTarget: 2900, fanSmallRpmTarget: 5200,  // 均衡模式默认
};

// 各模式的 EC 官方风扇默认转速（用于恢复默认时设置正确的 UI 值）
// 数据来源：docs/reference-consoles.md 官方预设表
export const MODE_FAN_DEFAULTS = {
  silent: { fanLargeRpmTarget: 2200, fanSmallRpmTarget: 2000 },
  office: { fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },
  gaming: { fanLargeRpmTarget: 4300, fanSmallRpmTarget: 8000 },
  beast:  { fanLargeRpmTarget: 3500, fanSmallRpmTarget: 6900 },
  custom: { fanLargeRpmTarget: 2900, fanSmallRpmTarget: 6400 },  // custom 默认用均衡
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

export async function fetchRouteInfo() {
  const res = await fetch("/api/fan-curve/route-info");
  if (!res.ok) throw new Error("Route info returned " + res.status);
  return res.json();
}

export function clampParam(key, value) {
  const r = PARAM_RANGES[key];
  if (!r) return value;
  return Math.max(r.min, Math.min(r.max, value));
}

export function getFanRange(_mode) {
  return FULL_FAN_RANGE; // EC 直写方案：全范围可用
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
// 清空 overrides + 重发 thermal_mode (EC 恢复出厂值) + CPU/GPU/NVAPI 重置
export async function resetToFactoryDefaults(mode) {
  // 1. 清空 overrides (localStorage + UI 状态)
  localStorage.setItem("douzhanzhe_overrides_" + mode, "{}");
  
  // 2. 重发 thermal_mode (EC 重新加载出厂值，包括 CPU PPT/温度/风扇预设)
  const tv = thermalModeMap[mode];
  if (tv !== null && tv !== undefined) {
    await applyHardwareControl("thermal_mode", tv);
  }
  
  // 3. CPU 频率/睿频/核心数通过 Windows powercfg 控制
  await resetCpuPower().catch(e => console.warn("resetCpuPower:", e));

  // 4. GPU 频率锁定 + NVAPI 超频/温度限制不受 thermal_mode 管理，需要单独重置
  await applyGpuControl("reset-clocks").catch(e => console.warn("[GPU] reset:", e));
  await applyGpuControl("reset-memory-clocks").catch(e => console.warn("[GPU] mem-reset:", e));
  await Promise.all([
    applyNvapiOverclock(0, 0).catch(e => console.warn("[NVAPI] OC-reset:", e)),
    applyNvapiThermalLimit(87).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
  ]);
}


// ── 全量模式下发（overrides 感知） ──
// thermal_mode + GPU/NVAPI 每次必发（不受 EC 管理），其余通道按 overrides 条件下发
// 顺序: 1. thermal_mode → 2. SMU → 3. GPU → 4. NVAPI → 5. CPU powercfg → 6. 风扇 → 7. SMU 重发
/// 重发稀疏 overrides（SMU/GPU/NVAPI/CPU/风扇），不触碰 thermal_mode
/// 用于风扇曲线解锁恢复后，后端已切回 thermal_mode，前端只需重发用户自定义参数
export async function reapplyOverrides(mode, overrides) {
  cancelSmuResendTimers();
  const myGen = ++_smuDispatchGen;

  const isEmpty = !overrides || Object.keys(overrides).length === 0;

  // overrides 为空时，发 GPU/NVAPI/CPU 重置
  if (isEmpty) {
    console.log("[reapply] overrides 为空，发送 GPU/NVAPI/CPU 重置");
    try { await applyGpuControl("reset-clocks"); } catch (e) { console.warn("[GPU] reset:", e); }
    try { await applyGpuControl("reset-memory-clocks"); } catch (e) { console.warn("[GPU] mem-reset:", e); }
    await Promise.all([
      applyNvapiOverclock(0, 0).catch(e => console.warn("[NVAPI] OC-reset:", e)),
      applyNvapiThermalLimit(87).catch(e => console.warn("[NVAPI] thermal-reset:", e)),
    ]);
    await Promise.all([
      setCpuFreqLimit(0).catch(e => console.warn("[CPU] freq-reset:", e)),
      setCpuTurbo(true).catch(e => console.warn("[CPU] turbo-reset:", e)),
      setCpuCoreLimitPercent(100).catch(() => {}),
      applyHardwareControl("power_plan", 0).catch(e => console.warn("[CPU] power-plan-reset:", e)),
    ]);
    return;
  }

  // ② SMU 批量写入（仅当 overrides 有 CPU SMU 字段时执行）
  const smuFields = ["cpuLongPptW", "cpuShortPptW", "cpuTempLimitC", "cpuVoltageOffset"];
  const hasSmu = smuFields.some(f => f in overrides);
  // 过滤掉 powercfg 专属字段，避免 CPU 频率限制走 ryzenadj 路径（未经实机验证）
  const { cpuFreqLimitEnabled, cpuFreqLimitMhz, ...smuParams } = overrides;
  if (hasSmu) {
    await applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: smuParams }).catch(
      e => console.warn("[SMU] batch:", e)
    );
  }

  // ③ GPU 频率控制（nvidia-smi: 先 reset 解锁，再按需 lock）
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
    await applyGpuControl("reset-memory-clocks");
    if (overrides.gpuMemFreqMhz > 0) {
      await applyGpuControl("limit-memory", memMap[overrides.gpuMemFreqMhz]);
    }
  } catch (e) { console.warn("[GPU] memory:", e); }

  // ④ NVAPI: 超频偏移 + 温度限制（并行）
  const thermalC = clampParam("gpuTempLimitC", overrides.gpuTempLimitC ?? 87);
  await Promise.all([
    applyNvapiOverclock(overrides.ocCoreOffsetMhz ?? 0, overrides.ocMemOffsetMhz ?? 0).catch(
      e => console.warn("[NVAPI] OC:", e)
    ),
    applyNvapiThermalLimit(thermalC).catch(
      e => console.warn("[NVAPI] thermal:", e)
    ),
  ]);

  // ⑤ CPU powercfg: 频率限制 / 睿频 / 核心数 / 电源计划
  setCpuFreqLimit(overrides.cpuFreqLimitEnabled ? overrides.cpuFreqLimitMhz : 0).catch(
    e => console.warn("[CPU] freq:", e)
  );
  setCpuTurbo(!(overrides.cpuTurboDisabled ?? false)).catch(
    e => console.warn("[CPU] turbo:", e)
  );
  if ((overrides.cpuCoreLimit ?? 0) > 0) {
    setCpuCoreLimitPercent(Math.round(overrides.cpuCoreLimit / 16 * 100)).catch(
      e => console.warn("[CPU] core-limit:", e)
    );
  } else {
    setCpuCoreLimitPercent(100).catch(() => {});
  }
  const ppHal = powerPlanHALMap[overrides.cpuPowerPlan ?? "balance"];
  if (ppHal !== undefined) {
    applyHardwareControl("power_plan", ppHal).catch(
      e => console.warn("[CPU] power-plan:", e)
    );
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
    const t1 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) {
        console.log("[SMU] re-send skipped (superseded by gen", _smuDispatchGen, ")");
        return;
      }
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: smuParams })
        .then(r => console.log("[SMU] re-send OK:", r))
        .catch(e => console.warn("[SMU] re-send failed:", e));
    }, 500);
    const t2 = setTimeout(() => {
      if (myGen !== _smuDispatchGen) {
        console.log("[SMU] re-send2 skipped (superseded by gen", _smuDispatchGen, ")");
        return;
      }
      applyUxtuLimits({ chipset: "Ryzen 9 8940HX", profile: mode, params: smuParams })
        .then(r => console.log("[SMU] re-send2 OK:", r))
        .catch(e => console.warn("[SMU] re-send2 failed:", e));
    }, 1500);
    _smuResendTimers = [t1, t2];
  }
}

export async function dispatchFullMode(mode, overrides) {
  const tv = thermalModeMap[mode];

  // ① thermal_mode 永远执行（切模式的基础）— 必须 await，等 EC 完成模式切换
  if (tv !== null && tv !== undefined) {
    await applyHardwareControl("thermal_mode", tv).catch(e => console.warn("[EC] thermal_mode:", e));
    await new Promise(r => setTimeout(r, 500));
  }

  // ②-⑦ 重发稀疏 overrides
  await reapplyOverrides(mode, overrides);
}
