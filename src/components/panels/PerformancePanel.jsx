import { useState, useEffect, useRef, useCallback } from "react";
import { applyUxtuLimits, applyHardwareControl, powerPlanHALMap } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import { useToast } from "../ui/Toast";

const POWER_PLANS = [
  { id: "efficiency", label: "最高能效" },
  { id: "balance", label: "平衡" },
  { id: "performance", label: "最佳性能" },
];

export default function PerformancePanel({ settings, setSettings, uxtuParams, setUxtuParams, uxtuPayload, onApplied, showCpu = true, showGpu = true }) {
  const toast = useToast();
  const [isApplying, setIsApplying] = useState(false);
  const [applyMessage, setApplyMessage] = useState("");
  const [smuInfo, setSmuInfo] = useState(null);
  const [smuError, setSmuError] = useState(false);
  const paramsLocked = settings.mode !== "custom";
  const presetRef = useRef(true); // 首次挂载/切换模式时不触发自动切 custom

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

  // 模式切换后短暂锁定，避免覆盖用户手动调整
  useEffect(() => {
    presetRef.current = true;
    const t = setTimeout(() => { presetRef.current = false; }, 200);
    return () => clearTimeout(t);
  }, [settings.mode]);

  const update = useCallback((key) => (value) => {
    setUxtuParams((p) => ({ ...p, [key]: value }));
    // 用户手动改参数 → 自动切到自定义模式
    if (!presetRef.current && settings.mode !== "custom") {
      setSettings((prev) => ({ ...prev, mode: "custom" }));
    }
  }, [settings.mode, setSettings, setUxtuParams]);

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
            min={60} max={100} unit="°C" onChange={update("cpuTempLimitC")} disabled={paramsLocked} />
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
            min={-30} max={0} step={1} unit="mV" onChange={update("cpuVoltageOffset")} disabled={paramsLocked} />
          <SliderRow label="长时功耗" value={uxtuParams.cpuLongPptW}
            min={15} max={150} unit="W" onChange={update("cpuLongPptW")} disabled={paramsLocked} />
          <SliderRow label="短时功耗" value={uxtuParams.cpuShortPptW}
            min={15} max={180} unit="W" onChange={update("cpuShortPptW")} disabled={paramsLocked} />
        </div>
      </Card>

      }

{showGpu && <Card title="GPU 调节" className="!p-3">
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLimitEnabled}
              onChange={(e) => update("gpuFreqLimitEnabled")(e.target.checked)}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">频率限制</span>
          </div>
          {uxtuParams.gpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.gpuFreqLimitMhz}
              min={1000} max={3200} step={50} unit="MHz" onChange={update("gpuFreqLimitMhz")} disabled={paramsLocked} />
          )}
          <SliderRow label="显卡超频(偏移)" value={uxtuParams.gpuCoreOffsetMhz}
            min={-200} max={200} step={25} unit="MHz" onChange={update("gpuCoreOffsetMhz")} disabled={paramsLocked} />
          <SliderRow label="显存超频(偏移)" value={uxtuParams.gpuMemOffsetMhz}
            min={-500} max={500} step={50} unit="MHz" onChange={update("gpuMemOffsetMhz")} disabled={paramsLocked} />
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLocked}
              onChange={(e) => update("gpuFreqLocked")(e.target.checked)}
            disabled={paramsLocked}
              className="accent-cyan-400" />
            <span className="text-xs">锁定频率</span>
          </div>
        </div>
      </Card>}</>
  );
}
