import { useState, useEffect, useCallback, useRef } from "react";
import { MODE_PRESETS, applyUxtuLimits, applySmuSet, applyHardwareControl, powerPlanHALMap, applyGpuControl, applyNvapiOverclock, applyNvapiThermalLimit, fetchNvapiStatus, fetchCpuPowerStatus, setCpuFreqLimit, setCpuTurbo, setCpuCoreLimitPercent, resetCpuPower } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";

const POWER_PLANS = [
  { id: "efficiency", label: "最高能效", halValue: powerPlanHALMap.efficiency },
  { id: "balance", label: "平衡", halValue: powerPlanHALMap.balance },
  { id: "performance", label: "最佳性能", halValue: powerPlanHALMap.performance },
];

export default function PerformancePanel({ settings, setSettings, uxtuParams, setUxtuParams, uxtuPayload, onApplied, showCpu = true, showGpu = true, showPower = true, telemetry }) {
  const toast = useToast();
  const [isApplying, setIsApplying] = useState(false);
  const [applyMessage, setApplyMessage] = useState("");
  const [cpuPowerStatus, setCpuPowerStatus] = useState(null);
  const [ocCoreOffset, setOcCoreOffset] = useState(0);
  const [ocMemOffset, setOcMemOffset] = useState(0);
  const [thermalLimit, setThermalLimit] = useState(87);
  const gpuFreqLocked = useRef(false);
  const smuTimer = useRef(null);
  const ocCoreTimer = useRef(null);
  const ocMemTimer = useRef(null);
  const thermalTimer = useRef(null);
  const gpuCoreTimer = useRef(null);

  // 带重试的命令下发
  async function gpuCmd(action, value, retries = 2) {
    for (let i = 0; i <= retries; i++) {
      try {
        const r = await applyGpuControl(action, value);
        if (r?.ok) return r;
      } catch (err) {
        if (i === retries) throw err;
      }
      await new Promise((r) => setTimeout(r, 300));
    }
  }

  // 核心频率下发: 如果已锁定则先解锁→下发→重新锁定
  async function applyGpuCoreFreq(mhz) {
    if (gpuFreqLocked.current) {
      await gpuCmd("reset-clocks").catch(() => {});
      gpuFreqLocked.current = false;
    }
    await gpuCmd("limit-max", mhz);
    gpuFreqLocked.current = true;
    await gpuCmd("lock-exact", mhz);
  }

  function queueGpuCore(mhz) {
    clearTimeout(gpuCoreTimer.current);
    gpuCoreTimer.current = setTimeout(() => applyGpuCoreFreq(mhz), 400);
  }

  function queueCpuFreq(mhz) {
    clearTimeout(cpuFreqTimer.current);
    cpuFreqTimer.current = setTimeout(async () => {
      try { await setCpuFreqLimit(mhz); }
      catch (err) { console.error("CPU freq-limit failed:", err); }
    }, 600);
  }

  function queueSmu(parameter, valueM) {
    clearTimeout(smuTimer.current);
    smuTimer.current = setTimeout(async () => {
      try { await applySmuSet(parameter, valueM); }
      catch (err) { console.error("SMU set failed:", err); }
    }, 600);
  }
  const coreTimer = useRef(null);
  function queueCoreLimit(coreCount) {
    clearTimeout(coreTimer.current);
    coreTimer.current = setTimeout(async () => {
      try {
        // powercfg 路径: 核心数 → 百分比 (基于 16 物理核)
        const percent = coreCount > 0 ? Math.round(coreCount / 16 * 100) : 100;
        await setCpuCoreLimitPercent(percent);
      }
      catch (err) { console.error("Core limit failed:", err); }
    }, 600);
  }
  function queueOcCore(offset) {
    clearTimeout(ocCoreTimer.current);
    ocCoreTimer.current = setTimeout(async () => {
      try {
        await applyNvapiOverclock(offset, 0);
        if (gpuFreqLocked.current) {
          await gpuCmd("lock-exact", uxtuParams.gpuCoreFreqMhz).catch(() => {});
        }
      } catch (err) { console.error("NVAPI OC core failed:", err); }
    }, 600);
  }
  function queueOcMem(offset) {
    clearTimeout(ocMemTimer.current);
    ocMemTimer.current = setTimeout(async () => {
      try {
        await applyNvapiOverclock(ocCoreOffset, offset);
        if (gpuFreqLocked.current) {
          await gpuCmd("lock-exact", uxtuParams.gpuCoreFreqMhz).catch(() => {});
        }
      } catch (err) { console.error("NVAPI OC mem failed:", err); }
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

  // 加载 CPU 电源控制状态 (powercfg)
  useEffect(() => {
    fetchCpuPowerStatus()
      .then((s) => {
        if (s.ok) {
          setCpuPowerStatus(s);
          // 从实际系统状态同步到 UI
          setUxtuParams((p) => ({
            ...p,
            cpuFreqLimitEnabled: s.freqLimitMhz > 0,
            cpuFreqLimitMhz: s.freqLimitMhz > 0 ? s.freqLimitMhz : p.cpuFreqLimitMhz,
            cpuTurboDisabled: !s.turboEnabled,
          }));
        }
      })
      .catch((err) => console.warn("CPU power status load failed:", err));
  }, []);

  // 加载 NVAPI 状态 (当前偏移/温度限制)
  useEffect(() => {
    fetchNvapiStatus()
      .then((s) => {
        if (s.ok) {
          setOcCoreOffset(s.coreOffsetMhz || 0);
          setOcMemOffset(s.memOffsetMhz || 0);
          if (s.thermalLimitC > 0) setThermalLimit(s.thermalLimitC);
        }
      })
      .catch((err) => console.warn("NVAPI status load failed:", err));
  }, []);

  // 监听散热模式切换联动的 GPU 温度限制更新
  useEffect(() => {
    const handler = (e) => setThermalLimit(e.detail);
    window.addEventListener("gpu-thermal-updated", handler);
    return () => window.removeEventListener("gpu-thermal-updated", handler);
  }, []);

  // 监听模式切换联动的 GPU 核心/显存偏移更新
  useEffect(() => {
    const handler = (e) => {
      const { core, mem } = e.detail;
      if (core !== undefined) setOcCoreOffset(core);
      if (mem !== undefined) setOcMemOffset(mem);
    };
    window.addEventListener("gpu-oc-updated", handler);
    return () => window.removeEventListener("gpu-oc-updated", handler);
  }, []);

  const update = useCallback((key) => (value) => {
    setUxtuParams((p) => ({ ...p, [key]: value }));
  }, [setUxtuParams]);

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

  return (
    <>
      {showCpu && <Card title="CPU 频率控制" className="!p-3">
        <div className="space-y-3">
          <SwitchRow label="频率限制" checked={uxtuParams.cpuFreqLimitEnabled}
            onChange={(on) => { update("cpuFreqLimitEnabled")(on); queueCpuFreq(on ? uxtuParams.cpuFreqLimitMhz : 0); }}
            disabled={paramsLocked} />
          {uxtuParams.cpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.cpuFreqLimitMhz}
              min={2000} max={5500} step={100} unit="MHz" onChange={(v) => { update("cpuFreqLimitMhz")(v); queueCpuFreq(v); }} />
          )}
          <SwitchRow label="关闭睿频" checked={uxtuParams.cpuTurboDisabled}
            onChange={async (disabled) => {
              update("cpuTurboDisabled")(disabled);
              try { await setCpuTurbo(!disabled); }
              catch (err) { console.error("CPU turbo toggle failed:", err); }
            }}
            disabled={paramsLocked} />
          <SwitchRow label="限制核心数" checked={uxtuParams.cpuCoreLimit > 0}
            onChange={(on) => { const v = on ? 8 : 0; update("cpuCoreLimit")(v); queueCoreLimit(v); }}
            disabled={paramsLocked} />
          {uxtuParams.cpuCoreLimit > 0 && (
            <SliderRow label="核心数" value={uxtuParams.cpuCoreLimit}
              min={2} max={14} step={2} unit="核" onChange={(v) => { update("cpuCoreLimit")(v); queueCoreLimit(v); }} disabled={paramsLocked} />
          )}
          <div className="flex gap-2 pt-1">
            <button onClick={async () => {
              try {
                await resetCpuPower();
                update("cpuFreqLimitEnabled")(false);
                update("cpuTurboDisabled")(false);
                update("cpuCoreLimit")(0);
                setCpuPowerStatus((s) => s ? { ...s, turboEnabled: true, freqLimitMhz: 0, coreLimitPercent: 100 } : s);
                toast?.("CPU 限制已重置", "success");
              } catch (err) {
                toast?.("重置失败: " + err.message, "error");
              }
            }}
              className="text-xs px-3 py-1.5 rounded-lg cursor-pointer"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >重置 CPU 限制</button>
          </div>
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
      </Card>
      }

      {showPower && <Card title="CPU 功耗与温度" className="!p-3">
        <div className="space-y-3">
          <SliderRow label="温度墙" value={uxtuParams.cpuTempLimitC}
            min={60} max={100} unit="°C" onChange={(v) => { update("cpuTempLimitC")(v); queueSmu("temp_limit", v); }} disabled={paramsLocked} />
          <SliderRow label="电压调节(降压)" value={uxtuParams.cpuVoltageOffset}
            min={-30} max={0} step={1} unit="mV" onChange={(v) => { update("cpuVoltageOffset")(v); queueSmu("co_all", v); }} disabled={paramsLocked} />
          <SliderRow label="长时功耗" value={uxtuParams.cpuLongPptW}
            min={15} max={120} unit="W" onChange={(v) => { update("cpuLongPptW")(v); queueSmu("power_limit", v); }} disabled={paramsLocked} />
          <SliderRow label="短时功耗" value={uxtuParams.cpuShortPptW}
            min={15} max={140} unit="W" onChange={(v) => { update("cpuShortPptW")(v); queueSmu("short_power_limit", v); }} disabled={paramsLocked} />
        </div>
      </Card>
      }

{showGpu && <Card title="GPU 调节" className="!p-3">
        <div className="space-y-3">
          <SliderRow label="核心频率" value={uxtuParams.gpuCoreFreqMhz}
            min={1000} max={3100} step={50} unit="MHz"
            onChange={(v) => { update("gpuCoreFreqMhz")(v); queueGpuCore(v); }} />
          <SliderRow label="核心偏移" value={ocCoreOffset}
            min={-200} max={300} step={25} unit="MHz"
            displayValue={(ocCoreOffset >= 0 ? "+" : "") + ocCoreOffset}
            onChange={(v) => { setOcCoreOffset(v); queueOcCore(v); }} />
          <SliderRow label="显存频率" value={uxtuParams.gpuMemFreqMhz}
            min={0} max={3} step={1} unit=" MHz"
            displayValue={["自动", "9001", "11001", "12001"][uxtuParams.gpuMemFreqMhz] || ""}
            onChange={async (v) => {
              update("gpuMemFreqMhz")(v);
              const map = [0, 9001, 11001, 12001];
              if (v === 0) await gpuCmd("reset-memory-clocks");
              else await gpuCmd("limit-memory", map[v]);
            }} />
          <SliderRow label="温度限制" value={thermalLimit}
            min={60} max={100} step={1} unit="°C"
            onChange={(v) => { setThermalLimit(v); queueThermal(v); }} />
          <div className="flex gap-2 pt-1">
            <button onClick={async () => {
              gpuFreqLocked.current = false;
              await gpuCmd("reset-clocks").catch(() => {});
              await gpuCmd("reset-memory-clocks").catch(() => {});
              setOcCoreOffset(0);
              try { await applyNvapiOverclock(0, 0); }
              catch (err) { console.error("NVAPI OC reset failed:", err); }
              update("gpuCoreFreqMhz")(2750);
              update("gpuMemFreqMhz")(0);
              // 从 NVAPI 读取默认温度限制并同步
              try {
                const s = await fetchNvapiStatus();
                if (s.ok) {
                  if (s.thermalDefaultC > 0) setThermalLimit(s.thermalDefaultC);
                  await applyNvapiThermalLimit(s.thermalDefaultC || 87);
                }
              } catch {}
            }}
              className="text-xs px-3 py-1.5 rounded-lg cursor-pointer"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >重置 GPU</button>
          </div>
        </div>
      </Card>}</>
  );
}
