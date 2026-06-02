import { useState, useEffect } from "react";
import { applyUxtuLimits, fetchSmuInfo } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import { useToast } from "../ui/Toast";

const POWER_PLANS = [
  { id: "efficiency", label: "最高能效" },
  { id: "balance", label: "平衡" },
  { id: "performance", label: "最佳性能" },
];

export default function PerformancePanel({ settings, setSettings, uxtuParams, setUxtuParams, uxtuPayload, onApplied }) {
  const toast = useToast();
  const [isApplying, setIsApplying] = useState(false);
  const [applyMessage, setApplyMessage] = useState("");
  const [smuInfo, setSmuInfo] = useState(null);
  const [smuError, setSmuError] = useState(false);

  useEffect(() => {
    fetchSmuInfo()
      .then((data) => {
        if (data.ok) setSmuInfo(data.data);
        setSmuError(false);
      })
      .catch(() => {
        setSmuError(true);
        toast?.("SMU 参数读取失败，请确认后端已运行", "error");
      });
  }, []);

  const update = (key) => (value) => setUxtuParams((p) => ({ ...p, [key]: value }));

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
    <div className="space-y-3">
      <Card title="CPU 调节" className="!p-3"
        action={
          <button onClick={handleApply} disabled={isApplying}
            className="text-xs md:text-sm px-3 py-1.5 rounded-lg disabled:opacity-70"
            style={{ border: "1px solid var(--border)", background: "var(--primary-2)" }}
          >{isApplying ? "应用中..." : "应用参数"}</button>
        }
      >
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuFreqLimitEnabled}
              onChange={(e) => update("cpuFreqLimitEnabled")(e.target.checked)}
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
              className="accent-cyan-400" />
            <span className="text-xs">关闭睿频</span>
          </div>
          <SliderRow label="温度墙" value={uxtuParams.cpuTempLimitC}
            min={60} max={100} unit="°C" onChange={update("cpuTempLimitC")} />
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.cpuCoreLimit > 0}
              onChange={(e) => update("cpuCoreLimit")(e.target.checked ? 8 : 0)}
              className="accent-cyan-400" />
            <span className="text-xs">限制核心数</span>
          </div>
          {uxtuParams.cpuCoreLimit > 0 && (
            <SliderRow label="核心数" value={uxtuParams.cpuCoreLimit}
              min={2} max={14} step={2} unit="核" onChange={update("cpuCoreLimit")} />
          )}
          <div>
            <p className="text-xs mb-1" style={{ color: "var(--muted)" }}>电源管理</p>
            <div className="flex gap-1">
              {POWER_PLANS.map((plan) => (
                <button key={plan.id} onClick={() => update("cpuPowerPlan")(plan.id)}
                  className="text-xs px-2 py-1 rounded-lg"
                  style={{ border: "1px solid var(--border)", background: uxtuParams.cpuPowerPlan === plan.id ? "var(--primary)" : "var(--card-2)", color: uxtuParams.cpuPowerPlan === plan.id ? "#fff" : "var(--text)" }}
                >{plan.label}</button>
              ))}
            </div>
          </div>
          <SliderRow label="电压调节(降压)" value={uxtuParams.cpuVoltageOffset}
            min={-30} max={0} step={1} unit="mV" onChange={update("cpuVoltageOffset")} />
          <SliderRow label="长时功耗" value={uxtuParams.cpuLongPptW}
            min={15} max={150} unit="W" onChange={update("cpuLongPptW")} />
          <SliderRow label="短时功耗" value={uxtuParams.cpuShortPptW}
            min={15} max={180} unit="W" onChange={update("cpuShortPptW")} />
        </div>
        <p className="text-xs mt-3" style={{ color: "var(--muted)" }}>
          {applyMessage || "修改参数后点击「应用参数」下发到硬件"}
        </p>
      </Card>

      <Card title="GPU 调节" className="!p-3">
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLimitEnabled}
              onChange={(e) => update("gpuFreqLimitEnabled")(e.target.checked)}
              className="accent-cyan-400" />
            <span className="text-xs">频率限制</span>
          </div>
          {uxtuParams.gpuFreqLimitEnabled && (
            <SliderRow label="最大频率" value={uxtuParams.gpuFreqLimitMhz}
              min={1000} max={3200} step={50} unit="MHz" onChange={update("gpuFreqLimitMhz")} />
          )}
          <SliderRow label="显卡超频(偏移)" value={uxtuParams.gpuCoreOffsetMhz}
            min={-200} max={200} step={25} unit="MHz" onChange={update("gpuCoreOffsetMhz")} />
          <SliderRow label="显存超频(偏移)" value={uxtuParams.gpuMemOffsetMhz}
            min={-500} max={500} step={50} unit="MHz" onChange={update("gpuMemOffsetMhz")} />
          <div className="flex items-center gap-2">
            <input type="checkbox" checked={uxtuParams.gpuFreqLocked}
              onChange={(e) => update("gpuFreqLocked")(e.target.checked)}
              className="accent-cyan-400" />
            <span className="text-xs">锁定频率</span>
          </div>
        </div>
      </Card>

      {(smuInfo && Object.keys(smuInfo).length > 0) || smuError ? (
        <Card title="当前 SMU 参数" className="!p-3">
          <p className="text-xs" style={{ color: "var(--muted)" }}>
            {smuError ? "SMU 参数读取不可用" : "SMU 参数已加载"}
          </p>
        </Card>
      ) : null}
    </div>
  );
}
