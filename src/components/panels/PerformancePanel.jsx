import { useState, useEffect, useCallback, useRef } from "react";
import {
  applyUxtuLimits, applySmuSet, applyHardwareControl,
  powerPlanHALMap, applyGpuControl, applyNvapiOverclock, applyNvapiThermalLimit,
  fetchNvapiStatus, fetchCpuPowerStatus, setCpuFreqLimit, setCpuTurbo,
  setCpuCoreLimitPercent, resetCpuPower, GPU_BASE_CLOCK,
} from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";

const POWER_PLANS = [
  { id: "efficiency", label: "最高能效", halValue: powerPlanHALMap.efficiency },
  { id: "balance", label: "平衡", halValue: powerPlanHALMap.balance },
  { id: "performance", label: "最佳性能", halValue: powerPlanHALMap.performance },
];

export default function PerformancePanel({
  settings, setSettings, uxtuParams, setUxtuParams,
  uxtuPayload, onApplied, showCpu = true, showGpu = true,
  showPower = true, telemetry, editMode = false,
  overrides, saveOverride, customLabel,
}) {
  const toast = useToast();
  const [isApplying, setIsApplying] = useState(false);
  const [applyMessage, setApplyMessage] = useState("");
  const [cpuPowerStatus, setCpuPowerStatus] = useState(null);

  // GPU 频率锁定状态 (useState 以驱动 UI 更新)
  const [gpuFreqLocked, setGpuFreqLocked] = useState(false);
  const gpuFreqLockedRef = useRef(false); // ref 供回调中读取最新值
  const latestParamsRef = useRef(uxtuParams);
  latestParamsRef.current = uxtuParams;

  // 所有去抖 timer refs
  const smuTimer = useRef(null);
  const cpuFreqTimer = useRef(null); // 修复: 之前未声明
  const coreTimer = useRef(null);
  const ocTimer = useRef(null);
  const thermalTimer = useRef(null);
  const gpuCoreTimer = useRef(null);

  // ── 带重试的 GPU 命令 ──
  async function gpuCmd(action, value, retries = 2) {
    for (let i = 0; i <= retries; i++) {
      try {
        const r = await applyGpuControl(action, value);
        if (r?.ok) return r;
      } catch (err) {
        if (i === retries) throw err;
      }
      await new Promise(r => setTimeout(r, 300));
    }
  }

  // GPU 核心频率: 默认只设上限 (limit-max)，锁定状态下才精确锁定
  async function applyGpuCoreFreq(mhz) {
    if (gpuFreqLockedRef.current) {
      await gpuCmd("reset-clocks").catch(() => {});
      await gpuCmd("limit-max", mhz);
      gpuFreqLockedRef.current = true;
      await gpuCmd("lock-exact", mhz);
    } else {
      await gpuCmd("reset-clocks").catch(() => {});
      await gpuCmd("limit-max", mhz);
    }
  }

  function queueGpuCore(mhz) {
    clearTimeout(gpuCoreTimer.current);
    gpuCoreTimer.current = setTimeout(() => applyGpuCoreFreq(mhz), 400);
  }

  // GPU 频率锁定切换
  async function toggleGpuLock() {
    const mhz = latestParamsRef.current.gpuCoreFreqMhz;
    if (gpuFreqLockedRef.current) {
      // 解锁: reset → limit-max
      await gpuCmd("reset-clocks").catch(() => {});
      await gpuCmd("limit-max", mhz);
      gpuFreqLockedRef.current = false;
      setGpuFreqLocked(false);
    } else {
      // 锁定: limit-max → lock-exact
      await gpuCmd("limit-max", mhz);
      await gpuCmd("lock-exact", mhz);
      gpuFreqLockedRef.current = true;
      setGpuFreqLocked(true);
    }
  }

  function queueCpuFreq(mhz) {
    clearTimeout(cpuFreqTimer.current);
    cpuFreqTimer.current = setTimeout(async () => {
      try { await setCpuFreqLimit(mhz); }
      catch (err) { console.error("CPU freq-limit failed:", err); }
    }, 600);
  }

  // SMU 单参数去抖 (600ms)
  function queueSmu(parameter, valueM) {
    clearTimeout(smuTimer.current);
    smuTimer.current = setTimeout(async () => {
      try { await applySmuSet(parameter, valueM); }
      catch (err) { console.error("SMU set failed:", err); }
    }, 600);
  }

  // SMU 批量下发 (重置卡片时使用，单次调用发送全部 CPU 参数)
  function applySmuBatch(p) {
    return Promise.all([
      applySmuSet("temp_limit", p.cpuTempLimitC).catch(e => console.warn("SMU temp:", e)),
      applySmuSet("co_all", p.cpuVoltageOffset).catch(e => console.warn("SMU co:", e)),
      applySmuSet("power_limit", p.cpuLongPptW).catch(e => console.warn("SMU power:", e)),
      applySmuSet("short_power_limit", p.cpuShortPptW).catch(e => console.warn("SMU short:", e)),
    ]);
  }

  function queueCoreLimit(coreCount) {
    clearTimeout(coreTimer.current);
    coreTimer.current = setTimeout(async () => {
      try {
        const percent = coreCount > 0 ? Math.round(coreCount / 16 * 100) : 100;
        await setCpuCoreLimitPercent(percent);
      } catch (err) { console.error("Core limit failed:", err); }
    }, 600);
  }

  // 统一 NVAPI OC 去抖: 读取最新 core + mem 偏移一起下发
  function queueOc() {
    clearTimeout(ocTimer.current);
    ocTimer.current = setTimeout(async () => {
      try {
        const p = latestParamsRef.current;
        await applyNvapiOverclock(p.ocCoreOffsetMhz ?? 0, p.ocMemOffsetMhz ?? 0);
        if (gpuFreqLockedRef.current) {
          await gpuCmd("lock-exact", p.gpuCoreFreqMhz).catch(() => {});
        }
      } catch (err) { console.error("NVAPI OC failed:", err); }
    }, 600);
  }

  function queueThermal(tempC) {
    clearTimeout(thermalTimer.current);
    thermalTimer.current = setTimeout(async () => {
      try { await applyNvapiThermalLimit(tempC); }
      catch (err) { console.error("NVAPI thermal limit failed:", err); }
    }, 600);
  }

  const paramsLocked = false;

  // 加载 CPU 电源控制状态 (仅读取显示，不覆盖 uxtuParams — 信任 localStorage)
  useEffect(() => {
    fetchCpuPowerStatus()
      .then(s => { if (s.ok) setCpuPowerStatus(s); })
      .catch(err => console.warn("CPU power status load failed:", err));
  }, []);

  // 加载 NVAPI 状态 (仅读取显示，不覆盖 uxtuParams)
  useEffect(() => {
    fetchNvapiStatus()
      .then(s => { if (s.ok) setCpuPowerStatus(prev => prev ? { ...prev, nvapi: s } : { nvapi: s }); })
      .catch(err => console.warn("NVAPI status load failed:", err));
  }, []);

  const update = useCallback((key) => (value) => {
    setUxtuParams(p => ({ ...p, [key]: value }));
    saveOverride?.(settings.mode, key, value);
  }, [setUxtuParams, saveOverride, settings.mode]);

  async function handleApply() {
    setIsApplying(true); setApplyMessage("");
    try {
      const result = await applyUxtuLimits(uxtuPayload);
      setApplyMessage(result.message || "参数已下发");
      toast?.(result.message || "参数已下发", "success");
      onApplied?.(uxtuPayload);
    } catch (error) {
      const msg = `下发失败: ${error.message}`;
      setApplyMessage(msg);
      toast?.(msg, "error");
    } finally { setIsApplying(false); }
  }

  const isC = useCallback((key) => (overrides ? key in overrides : undefined), [overrides]);

  return (
    <>
      {showCpu && <Card title={"CPU 频率控制" + (customLabel || "")} className="!p-3">
        <div className="space-y-3">
          <SwitchRow label="频率限制" checked={uxtuParams.cpuFreqLimitEnabled}
            isCustom={isC("cpuFreqLimitEnabled")}
            onChange={(on) => { update("cpuFreqLimitEnabled")(on); queueCpuFreq(on ? uxtuParams.cpuFreqLimitMhz : 0); }}
            disabled={paramsLocked} />
          {uxtuParams.cpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.cpuFreqLimitMhz}
              min={2000} max={5500} step={100} unit="MHz"
              isCustom={isC("cpuFreqLimitMhz")}
              onChange={(v) => { update("cpuFreqLimitMhz")(v); queueCpuFreq(v); }} />
          )}
          <SwitchRow label="关闭睿频" checked={uxtuParams.cpuTurboDisabled}
            isCustom={isC("cpuTurboDisabled")}
            onChange={async (disabled) => {
              update("cpuTurboDisabled")(disabled);
              try { await setCpuTurbo(!disabled); }
              catch (err) { console.error("CPU turbo toggle failed:", err); }
            }}
            disabled={paramsLocked} />
          <SwitchRow label="限制核心数" checked={uxtuParams.cpuCoreLimit > 0}
            isCustom={isC("cpuCoreLimit")}
            onChange={(on) => { const v = on ? 8 : 0; update("cpuCoreLimit")(v); queueCoreLimit(v); }}
            disabled={paramsLocked} />
          {uxtuParams.cpuCoreLimit > 0 && (
            <SliderRow label="核心数" value={uxtuParams.cpuCoreLimit}
              min={2} max={14} step={2} unit="核"
              isCustom={isC("cpuCoreLimit")}
              onChange={(v) => { update("cpuCoreLimit")(v); queueCoreLimit(v); }} disabled={paramsLocked} />
          )}
          <div>
            <p className="text-xs mb-1" style={{ color: "var(--muted)" }}>电源管理</p>
            <div className="flex gap-1">
              {POWER_PLANS.map((plan) => (
                <button key={plan.id} onClick={() => {
                  update("cpuPowerPlan")(plan.id);
                  if (plan.halValue !== undefined) applyHardwareControl("power_plan", plan.halValue).catch(() => {});
                }}
                  disabled={paramsLocked}
                  className="text-xs px-2 py-1 rounded-lg"
                  style={{ border: "1px solid var(--border)", background: uxtuParams.cpuPowerPlan === plan.id ? "var(--primary)" : "var(--card-2)", color: uxtuParams.cpuPowerPlan === plan.id ? "#fff" : "var(--text)" }}
                >{plan.label}</button>
              ))}
            </div>
          </div>
        </div>
      </Card>}

      {showPower && <Card title={"CPU 功耗与温度" + (customLabel || "")} className="!p-3">
        <div className="space-y-3">
          <SliderRow label="温度墙" value={uxtuParams.cpuTempLimitC}
            min={60} max={100} unit="°C"
            isCustom={isC("cpuTempLimitC")}
            onChange={(v) => { update("cpuTempLimitC")(v); queueSmu("temp_limit", v); }} disabled={paramsLocked} />
          <SliderRow label="电压调节(降压)" value={uxtuParams.cpuVoltageOffset}
            min={-30} max={0} step={1} unit="mV"
            isCustom={isC("cpuVoltageOffset")}
            onChange={(v) => { update("cpuVoltageOffset")(v); queueSmu("co_all", v); }} disabled={paramsLocked} />
          <SliderRow label="长时功耗" value={uxtuParams.cpuLongPptW}
            min={15} max={120} unit="W"
            isCustom={isC("cpuLongPptW")}
            onChange={(v) => { update("cpuLongPptW")(v); queueSmu("power_limit", v); }} disabled={paramsLocked} />
          <SliderRow label="短时功耗" value={uxtuParams.cpuShortPptW}
            min={15} max={140} unit="W"
            isCustom={isC("cpuShortPptW")}
            onChange={(v) => { update("cpuShortPptW")(v); queueSmu("short_power_limit", v); }} disabled={paramsLocked} />
        </div>
      </Card>}

      {showGpu && <Card title={"GPU 调节" + (customLabel || "")} className="!p-3"
        >

        <div className="space-y-3">
          <SliderRow label="核心频率" value={uxtuParams.gpuCoreFreqMhz}
            min={1000} max={3100} step={50} unit="MHz"
            isCustom={isC("gpuCoreFreqMhz")}
            onChange={(v) => { update("gpuCoreFreqMhz")(v); queueGpuCore(v); }}
            action={
              <button onClick={toggleGpuLock}
                className="text-xs px-2.5 py-1 rounded-lg transition whitespace-nowrap"
                style={{
                  border: gpuFreqLocked ? "1px solid var(--ok)" : "1px solid var(--border)",
                  background: gpuFreqLocked ? "var(--ok)" : "var(--card-2)",
                  color: gpuFreqLocked ? "#fff" : "var(--text)",
                }}>
                {gpuFreqLocked ? "已锁定" : "锁定频率"}
              </button>
            }
          />
          <SliderRow label="核心偏移" value={uxtuParams.ocCoreOffsetMhz ?? 0}
            min={-200} max={300} step={25} unit="MHz"
            displayValue={((uxtuParams.ocCoreOffsetMhz ?? 0) >= 0 ? "+" : "") + (uxtuParams.ocCoreOffsetMhz ?? 0)}
            isCustom={isC("ocCoreOffsetMhz")}
            onChange={(v) => { update("ocCoreOffsetMhz")(v); queueOc(); }} />
          <SliderRow label="显存频率" value={uxtuParams.gpuMemFreqMhz}
            min={0} max={3} step={1} unit=" MHz"
            displayValue={["自动", "9001", "11001", "12001"][uxtuParams.gpuMemFreqMhz] || ""}
            isCustom={isC("gpuMemFreqMhz")}
            onChange={async (v) => {
              update("gpuMemFreqMhz")(v);
              const map = [0, 9001, 11001, 12001];
              if (v === 0) await gpuCmd("reset-memory-clocks");
              else await gpuCmd("limit-memory", map[v]);
            }} />
          <SliderRow label="温度限制" value={uxtuParams.gpuTempLimitC ?? 87}
            min={60} max={100} step={1} unit="°C"
            isCustom={isC("gpuTempLimitC")}
            onChange={(v) => { update("gpuTempLimitC")(v); queueThermal(v); }} />
        </div>
      </Card>}
    </>
  );
}
