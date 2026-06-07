import { useState, useEffect, useCallback, useRef } from "react";
import { MODE_PRESETS, applyUxtuLimits, applySmuSet, applyHardwareControl, powerPlanHALMap, applyGpuControl, fetchGpuStatus } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import { useToast } from "../ui/Toast";

const POWER_PLANS = [
  { id: "efficiency", label: "最高能效", halValue: powerPlanHALMap.efficiency },
  { id: "balance", label: "平衡", halValue: powerPlanHALMap.balance },
  { id: "performance", label: "最佳性能", halValue: powerPlanHALMap.performance },
];

export default function PerformancePanel({ settings, setSettings, uxtuParams, setUxtuParams, uxtuPayload, onApplied, showCpu = true, showGpu = true }) {
  const toast = useToast();
  const [isApplying, setIsApplying] = useState(false);
  const [applyMessage, setApplyMessage] = useState("");
  const [smuInfo, setSmuInfo] = useState(null);
  const [smuError, setSmuError] = useState(false);
  const smuTimer = useRef(null);

  function queueSmu(parameter, valueM) {
    clearTimeout(smuTimer.current);
    smuTimer.current = setTimeout(async () => {
      try { await applySmuSet(parameter, valueM); }
      catch (err) { console.error("SMU set failed:", err); }
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
      {showCpu && <Card title="CPU 调节" className="!p-3">
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuFreqLimitEnabled}
              onChange={(e) => update("cpuFreqLimitEnabled")(e.target.checked)}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">频率限制</span>
          </div>
          {uxtuParams.cpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.cpuFreqLimitMhz}
              min={2000} max={5500} step={50} unit="MHz" onChange={update("cpuFreqLimitMhz")} />
          )}
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuTurboDisabled}
              onChange={(e) => update("cpuTurboDisabled")(e.target.checked)}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">关闭睿频</span>
          </div>
          <SliderRow label="温度墙" value={uxtuParams.cpuTempLimitC}
            min={60} max={100} unit="°C" onChange={(v) => { update("cpuTempLimitC")(v); queueSmu("temp_limit", v); }} disabled={paramsLocked} />
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuCoreLimit > 0}
              onChange={(e) => update("cpuCoreLimit")(e.target.checked ? 8 : 0)}
              disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">限制核心数</span>
          </div>
          {uxtuParams.cpuCoreLimit > 0 && (
            <SliderRow label="核心数" value={uxtuParams.cpuCoreLimit}
              min={2} max={14} step={2} unit="核" onChange={update("cpuCoreLimit")} disabled={paramsLocked} />
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
          <div className="flex gap-2 pt-2">
            <button onClick={() => {
              const preset = MODE_PRESETS[settings.mode] || {};
              setUxtuParams((p) => ({ ...p, ...preset, gpuCoreFreqMhz: 2700, gpuMemFreqMhz: 0, gpuFreqLimitMhz: 2600, gpuFreqLimitEnabled: false, gpuFreqLocked: false }));
              if (preset.cpuTempLimitC) queueSmu("temp_limit", preset.cpuTempLimitC);
              if (preset.cpuLongPptW) queueSmu("power_limit", preset.cpuLongPptW);
              if (preset.cpuShortPptW) queueSmu("short_power_limit", preset.cpuShortPptW);
              if (preset.cpuVoltageOffset !== undefined) queueSmu("co_all", preset.cpuVoltageOffset);
              if (preset.cpuFreqLimitMhz) queueSmu("cpu_freq_limit", preset.cpuFreqLimitMhz);
              toast?.("\u5df2\u6062\u590d\u9884\u8bbe\u503c", "success");
            }}
              className="text-xs px-3 py-1.5 rounded-lg cursor-pointer"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >\u6062\u590d\u9884\u8bbe</button>
          </div>


// DEBUG_MARKER_20260606
