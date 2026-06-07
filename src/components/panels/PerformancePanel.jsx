import { useState, useEffect, useCallback, useRef } from "react";
import { MODE_PRESETS, applyUxtuLimits, applySmuSet, applyHardwareControl, powerPlanHALMap, applyGpuControl, fetchGpuStatus, fetchSmuInfo, fetchCpuPowerStatus, setCpuFreqLimit, setCpuTurbo, setCpuCoreLimitPercent, resetCpuPower } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
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
  const [smuInfo, setSmuInfo] = useState(null);
  const [smuError, setSmuError] = useState(false);
  const [cpuPowerStatus, setCpuPowerStatus] = useState(null);
  const smuTimer = useRef(null);
  const cpuFreqTimer = useRef(null);
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
  const paramsLocked = false;

  useEffect(() => {
    const timer = setTimeout(() => {
      fetchSmuInfo()
        .then((data) => {
          if (data.ok) setSmuInfo(data.data);
          setSmuError(false);
        })
        .catch(() => setSmuError(true));
    }, 3000);
    return () => clearTimeout(timer);
  }, []);

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
          {cpuPowerStatus && cpuPowerStatus.ok !== false && (
            <div className="text-xs flex items-center gap-2" style={{ color: "var(--muted)" }}>
              <span>睿频 {cpuPowerStatus.turboEnabled ? "开" : "关"}</span>
              <span>·</span>
              <span>频率限制 {cpuPowerStatus.freqLimitMhz > 0 ? cpuPowerStatus.freqLimitMhz + " MHz" : "无"}</span>
              <span>·</span>
              <span>核心 {cpuPowerStatus.coreLimitPercent > 0 && cpuPowerStatus.coreLimitPercent < 100 ? cpuPowerStatus.coreLimitPercent + "%" : "100%"}</span>
            </div>
          )}
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuFreqLimitEnabled}
              onChange={(e) => {
                const on = e.target.checked;
                update("cpuFreqLimitEnabled")(on);
                queueCpuFreq(on ? uxtuParams.cpuFreqLimitMhz : 0);
              }}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">频率限制</span>
          </div>
          {uxtuParams.cpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.cpuFreqLimitMhz}
              min={2000} max={5500} step={100} unit="MHz" onChange={(v) => { update("cpuFreqLimitMhz")(v); queueCpuFreq(v); }} />
          )}
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuTurboDisabled}
              onChange={async (e) => {
                const disabled = e.target.checked;
                update("cpuTurboDisabled")(disabled);
                try { await setCpuTurbo(!disabled); }
                catch (err) { console.error("CPU turbo toggle failed:", err); }
              }}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">关闭睿频</span>
          </div>
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuCoreLimit > 0}
              onChange={(e) => { const v = e.target.checked ? 8 : 0; update("cpuCoreLimit")(v); queueCoreLimit(v); }}
              disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">限制核心数</span>
          </div>
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
          {telemetry && typeof telemetry.gpuFreq === "number" && (
            <div className="text-xs flex items-center gap-2 flex-wrap" style={{ color: "var(--muted)" }}>
              <span>核心 {telemetry.gpuFreq.toFixed(2)} GHz</span>
              <span>·</span>
              <span>显存 {telemetry.gpuMemMhz || "?"} MHz</span>
              <span>·</span>
              <span>功耗 {typeof telemetry.gpuPowerDrawW === "number" ? telemetry.gpuPowerDrawW.toFixed(1) : "?"}W</span>
              <span>·</span>
              <span>{telemetry.gpuTemp}°C</span>
            </div>
          )}
          <SliderRow label="核心频率" value={uxtuParams.gpuCoreFreqMhz}
            min={1000} max={3090} step={50} unit="MHz"
            disabled={uxtuParams.gpuFreqLocked}
            onChange={async (v) => {
              update("gpuCoreFreqMhz")(v);
              if (uxtuParams.gpuFreqLocked) {
                await applyGpuControl("lock-exact", v);
              } else if (uxtuParams.gpuFreqLimitEnabled) {
                await applyGpuControl("limit-max", Math.min(uxtuParams.gpuFreqLimitMhz, v));
              } else {
                await applyGpuControl("limit-max", v);
              }
            }} />
          <SliderRow label="显存频率" value={uxtuParams.gpuMemFreqMhz}
            min={0} max={3} step={1} unit=" MHz"
            disabled={uxtuParams.gpuFreqLocked}
            displayValue={["自动", "9001", "11001", "12001"][uxtuParams.gpuMemFreqMhz] || ""}
            onChange={async (v) => {
              update("gpuMemFreqMhz")(v);
              const map = [0, 9001, 11001, 12001];
              if (v === 0) await applyGpuControl("reset-memory-clocks");
              else await applyGpuControl("limit-memory", map[v]);
            }} />
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLimitEnabled}
              disabled={uxtuParams.gpuFreqLocked}
              onChange={async (e) => {
                const on = e.target.checked;
                update("gpuFreqLimitEnabled")(on);
                if (!on) await applyGpuControl("reset-clocks");
                else await applyGpuControl("limit-max", uxtuParams.gpuFreqLimitMhz);
              }}
              className="accent-cyan-400" />
            <span className="text-xs">核心频率限制</span>
          </div>
          {uxtuParams.gpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.gpuFreqLimitMhz}
              min={1000} max={3200} step={50} unit="MHz"
              onChange={async (v) => {
                update("gpuFreqLimitMhz")(v);
                await applyGpuControl("limit-max", v);
              }} />
          )}
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLocked}
              disabled={uxtuParams.gpuFreqLimitEnabled}
              onChange={async (e) => {
                const on = e.target.checked;
                update("gpuFreqLocked")(on);
                const mems = [0, 9001, 11001, 12001];
                const memIdx = uxtuParams.gpuMemFreqMhz;
                const memVal = mems[memIdx] || 9001;
                if (on) {
                  await applyGpuControl("lock-exact", uxtuParams.gpuCoreFreqMhz);
                  await applyGpuControl("reset-memory-clocks");
                } else {
                  await applyGpuControl("reset-clocks");
                  if (uxtuParams.gpuMemFreqMhz === 0) await applyGpuControl("reset-memory-clocks");
                  else await applyGpuControl("limit-memory", memVal);
                }
              }}
              className="accent-cyan-400" />
            <span className="text-xs">锁定核心频率</span>
          </div>
          <div className="flex gap-2 pt-1">
            <button onClick={async () => {
              await applyGpuControl("reset-clocks");
              await applyGpuControl("reset-memory-clocks");
              update("gpuFreqLimitEnabled")(false);
              update("gpuFreqLocked")(false);
              update("gpuCoreFreqMhz")(2700);
              update("gpuMemFreqMhz")(0);
            }}
              className="text-xs px-3 py-1.5 rounded-lg cursor-pointer"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >重置 GPU</button>
          </div>
        </div>
      </Card>}</>
  );
}
