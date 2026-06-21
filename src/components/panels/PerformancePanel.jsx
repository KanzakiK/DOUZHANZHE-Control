import { useState, useEffect, useCallback, useRef } from "react";
import {
  applySmuSet, applyHardwareControl,
  powerPlanHALMap, applyGpuControl, applyNvapiOverclock, applyNvapiThermalLimit,
  setCpuFreqLimit, setCpuTurbo,
  setCpuCoreLimitPercent,
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
  showCpu = true, showGpu = true,
  showPower = true, editMode = false,
  overrides, saveOverride, customLabel,
  gpuMode, // 0=hybrid, 1=dGPU, 2=iGPU (from telemetry)
}) {
  const toast = useToast();

  // GPU 控件禁用逻辑: 混合模式跳时钟, 集显模式跳全部
  const gpuClockDisabled = gpuMode === 0 || gpuMode === 2; // 混合/集显: 禁用核心+显存频率
  const gpuAllDisabled = gpuMode === 2; // 集显: 禁用所有 GPU/NVAPI 控件

  const latestParamsRef = useRef(uxtuParams);
  latestParamsRef.current = uxtuParams;

  // 所有去抖 timer refs
  const smuTimer = useRef(null);
  const cpuFreqTimer = useRef(null); // 修复: 之前未声明
  const coreTimer = useRef(null);
  const ocTimer = useRef(null);
  const turboTimer = useRef(null);
  const gpuMemTimer = useRef(null);

  // 组件卸载时清理所有去抖 timer
  useEffect(() => {
    return () => {
      clearTimeout(smuTimer.current);
      clearTimeout(cpuFreqTimer.current);
      clearTimeout(coreTimer.current);
      clearTimeout(ocTimer.current);
      clearTimeout(turboTimer.current);
      clearTimeout(gpuMemTimer.current);
    };
  }, []);
  const thermalTimer = useRef(null);
  const gpuCoreTimer = useRef(null);

  // ── 带重试的 GPU 命令 ──
  async function gpuCmd(action, value, retries = 2) {
    const mode = settings.mode;
    for (let i = 0; i <= retries; i++) {
      try {
        const r = await applyGpuControl(action, value, undefined, undefined, mode);
        if (r?.ok) return r;
      } catch (err) {
        if (i === retries) throw err;
      }
      await new Promise(r => setTimeout(r, 300));
    }
  }

  // GPU 核心频率: 默认只设上限 (limit-max)，锁定状态下才精确锁定
  async function applyGpuCoreFreq(mhz) {
    if (latestParamsRef.current.gpuFreqLimitEnabled) {
      await gpuCmd("reset-clocks").catch(() => {});
      await gpuCmd("limit-max", mhz);
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

  // GPU 频率锁定切换 (同时写入 overrides，确保跟随模式保存和下发)
  async function toggleGpuLock() {
    const mhz = latestParamsRef.current.gpuCoreFreqMhz;
    if (latestParamsRef.current.gpuFreqLimitEnabled) {
      // 解锁: reset → limit-max
      await gpuCmd("reset-clocks").catch(() => {});
      await gpuCmd("limit-max", mhz);
      update("gpuFreqLimitEnabled")(false);
    } else {
      // 锁定: limit-max → lock-exact
      await gpuCmd("limit-max", mhz);
      await gpuCmd("lock-exact", mhz);
      update("gpuFreqLimitEnabled")(true);
      update("gpuCoreFreqMhz")(mhz);
    }
  }

  function queueCpuFreq(mhz) {
    const mode = settings.mode;
    clearTimeout(cpuFreqTimer.current);
    cpuFreqTimer.current = setTimeout(async () => {
      try { await setCpuFreqLimit(mhz, mode); }
      catch (err) { console.error("CPU freq-limit failed:", err); }
    }, 600);
  }

  // SMU 单参数去抖 (600ms)
  function queueSmu(parameter, valueM) {
    const mode = settings.mode;
    clearTimeout(smuTimer.current);
    smuTimer.current = setTimeout(async () => {
      try { await applySmuSet(parameter, valueM, mode); }
      catch (err) { console.error("SMU set failed:", err); }
    }, 600);
  }

  // CPU 睿频开关: 去抖 600ms + 失败回滚
  function queueTurbo(disabled) {
    const mode = settings.mode;
    clearTimeout(turboTimer.current);
    turboTimer.current = setTimeout(async () => {
      try {
        await setCpuTurbo(!disabled, mode);
      } catch (err) {
        console.error("CPU turbo toggle failed:", err);
        // 回滚 UI 和 override
        setUxtuParams(p => ({ ...p, cpuTurboDisabled: !disabled }));
      }
    }, 600);
  }

  function queueCoreLimit(coreCount) {
    const mode = settings.mode;
    clearTimeout(coreTimer.current);
    coreTimer.current = setTimeout(async () => {
      try {
        const percent = coreCount > 0 ? Math.round(coreCount / 16 * 100) : 100;
        await setCpuCoreLimitPercent(percent, mode);
      } catch (err) { console.error("Core limit failed:", err); }
    }, 600);
  }

  // 统一 NVAPI OC 去抖: 读取最新 core + mem 偏移一起下发
  function queueOc() {
    const mode = settings.mode;
    clearTimeout(ocTimer.current);
    ocTimer.current = setTimeout(async () => {
      try {
        const p = latestParamsRef.current;
        await applyNvapiOverclock(p.ocCoreOffsetMhz ?? 0, p.ocMemOffsetMhz ?? 0, mode);
        if (p.gpuFreqLimitEnabled) {
          await gpuCmd("lock-exact", p.gpuCoreFreqMhz).catch(() => {});
        }
      } catch (err) { console.error("NVAPI OC failed:", err); }
    }, 600);
  }

  function queueThermal(tempC) {
    const mode = settings.mode;
    clearTimeout(thermalTimer.current);
    thermalTimer.current = setTimeout(async () => {
      try { await applyNvapiThermalLimit(tempC, mode); }
      catch (err) { console.error("NVAPI thermal limit failed:", err); }
    }, 600);
  }

  // GPU 显存频率: 去抖 400ms，合并快速连点
  function queueGpuMem(v) {
    const mode = settings.mode;
    clearTimeout(gpuMemTimer.current);
    gpuMemTimer.current = setTimeout(async () => {
      try {
        const map = [0, 9001, 11001, 12001];
        if (v === 0) await gpuCmd("reset-memory-clocks");
        else await gpuCmd("limit-memory", map[v]);
      } catch (err) { console.error("GPU memory freq failed:", err); }
    }, 400);
  }

  const paramsLocked = false;

  const update = useCallback((key) => (value) => {
    setUxtuParams(p => ({ ...p, [key]: value }));
    saveOverride?.(settings.mode, key, value);
  }, [setUxtuParams, saveOverride, settings.mode]);

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
            onChange={(disabled) => { update("cpuTurboDisabled")(disabled); queueTurbo(disabled); }}
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
                  if (plan.halValue !== undefined) applyHardwareControl("power_plan", plan.halValue, settings.mode).catch(() => {});
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
          {gpuClockDisabled && (
            <p className="text-xs" style={{ color: "var(--muted)" }}>
              {gpuMode === 0 ? "混合模式下 GPU 时钟由系统管理，核心/显存频率设置不生效" : "集显模式下独显不可用，GPU 设置不生效"}
            </p>
          )}
          <SliderRow label="核心频率" value={uxtuParams.gpuCoreFreqMhz}
            min={1000} max={3100} step={50} unit="MHz"
            isCustom={isC("gpuCoreFreqMhz")}
            disabled={gpuClockDisabled || paramsLocked}
            onChange={(v) => { update("gpuCoreFreqMhz")(v); queueGpuCore(v); }}
            action={
              <button onClick={toggleGpuLock}
                disabled={gpuClockDisabled || paramsLocked}
                className="text-xs px-2.5 py-1 rounded-lg transition whitespace-nowrap"
                style={{
                  border: uxtuParams.gpuFreqLimitEnabled ? "1px solid var(--ok)" : "1px solid var(--border)",
                  background: uxtuParams.gpuFreqLimitEnabled ? "var(--ok)" : "var(--card-2)",
                  color: uxtuParams.gpuFreqLimitEnabled ? "#fff" : "var(--text)",
                  opacity: (gpuClockDisabled || paramsLocked) ? 0.5 : 1,
                  cursor: (gpuClockDisabled || paramsLocked) ? "not-allowed" : "pointer",
                }}>
                {uxtuParams.gpuFreqLimitEnabled ? "已锁定" : "锁定频率"}
              </button>
            }
          />
          <SliderRow label="核心偏移" value={uxtuParams.ocCoreOffsetMhz ?? 0}
            min={-200} max={300} step={25} unit="MHz"
            displayValue={((uxtuParams.ocCoreOffsetMhz ?? 0) >= 0 ? "+" : "") + (uxtuParams.ocCoreOffsetMhz ?? 0)}
            isCustom={isC("ocCoreOffsetMhz")}
            disabled={gpuAllDisabled || paramsLocked}
            onChange={(v) => { update("ocCoreOffsetMhz")(v); queueOc(); }} />
          <SliderRow label="显存频率" value={uxtuParams.gpuMemFreqMhz}
            min={0} max={3} step={1} unit=" MHz"
            displayValue={["自动", "9001", "11001", "12001"][uxtuParams.gpuMemFreqMhz] || ""}
            isCustom={isC("gpuMemFreqMhz")}
            disabled={gpuClockDisabled || paramsLocked}
            onChange={(v) => { update("gpuMemFreqMhz")(v); queueGpuMem(v); }} />
          <SliderRow label="温度限制" value={uxtuParams.gpuTempLimitC ?? 87}
            min={60} max={100} step={1} unit="°C"
            isCustom={isC("gpuTempLimitC")}
            disabled={gpuAllDisabled || paramsLocked}
            onChange={(v) => { update("gpuTempLimitC")(v); queueThermal(v); }} />
        </div>
      </Card>}
    </>
  );
}
