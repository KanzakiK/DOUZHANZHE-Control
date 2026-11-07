import { useCallback, useRef, useEffect } from "react";
import { applySmuSet } from "../../services/uxtuAdapter";
import OverrideSlider from "../ui/OverrideSlider";
import PerCoreCoPanel from "./PerCoreCoPanel";

export default function AmdPowerPanel({ settings, uxtuParams, setUxtuParams, overrides, saveOverride, clearOverride, switching }) {
  const latestModeRef = useRef(settings.mode);
  latestModeRef.current = settings.mode;
  const paramsLocked = !!switching;
  const isSet = (key) => Object.prototype.hasOwnProperty.call(overrides, key);
  // 全核 CO 与逐核 CO 互斥：逐核已启用时禁用全核滑块（后端同样会拒绝）
  const perCoreActive = Array.isArray(overrides?.perCoreCo) && overrides.perCoreCo.length > 0;
  const update = useCallback((key) => (value) => {
    setUxtuParams(p => ({ ...p, [key]: value }));
    saveOverride?.(settings.mode, key, value);
  }, [setUxtuParams, saveOverride, settings.mode]);
  const smuTimer = useRef(null);
  useEffect(() => () => clearTimeout(smuTimer.current), []);
  function queueSmu(param, val) {
    clearTimeout(smuTimer.current);
    smuTimer.current = setTimeout(async () => {
      try { await applySmuSet(param, val, latestModeRef.current); }
      catch (err) { console.error("SMU set failed:", err); }
    }, 600);
  }
  return (
    <>
      <OverrideSlider
        label="长时功耗" desc="持续运行时的最大CPU功耗上限，影响长时间负载性能" value={uxtuParams.cpuLongPptW} min={15} max={120} step={1} unit="W"
        set={isSet('cpuLongPptW')} disabled={paramsLocked}
        onEnable={() => { update('cpuLongPptW')(uxtuParams.cpuLongPptW); queueSmu('stapm_limit', uxtuParams.cpuLongPptW); }}
        onClear={() => clearOverride(settings.mode, ['cpuLongPptW'])}
        onChange={v => { update('cpuLongPptW')(v); queueSmu('stapm_limit', v); }}
      />
      <OverrideSlider
        label="短时功耗" desc="短时突发的最大CPU功耗上限，影响短时间加速性能" value={uxtuParams.cpuShortPptW} min={15} max={140} step={1} unit="W"
        set={isSet('cpuShortPptW')} disabled={paramsLocked}
        onEnable={() => { update('cpuShortPptW')(uxtuParams.cpuShortPptW); queueSmu('short_power_limit', uxtuParams.cpuShortPptW); }}
        onClear={() => clearOverride(settings.mode, ['cpuShortPptW'])}
        onChange={v => { update('cpuShortPptW')(v); queueSmu('short_power_limit', v); }}
      />
      <OverrideSlider
        label="温度墙" desc="CPU温度达到此值后会自动降频保护" value={uxtuParams.cpuTempLimitC} min={60} max={100} step={1} unit="°C"
        set={isSet('cpuTempLimitC')} disabled={paramsLocked}
        onEnable={() => { update('cpuTempLimitC')(uxtuParams.cpuTempLimitC); queueSmu('tctl_temp', uxtuParams.cpuTempLimitC); }}
        onClear={() => clearOverride(settings.mode, ['cpuTempLimitC'])}
        onChange={v => { update('cpuTempLimitC')(v); queueSmu('tctl_temp', v); }}
      />
      <OverrideSlider
        label="电压调节 · 降压" desc={perCoreActive ? "逐核心降压已启用，两者互斥，请先清除逐核心降压" : "负值为降压，可降低发热和功耗，需逐机调试稳定性"} value={uxtuParams.cpuVoltageOffset} min={-30} max={0} step={1} unit="mV"
        set={isSet('cpuVoltageOffset')} disabled={paramsLocked || perCoreActive}
        onEnable={() => { update('cpuVoltageOffset')(uxtuParams.cpuVoltageOffset); queueSmu('co_all', uxtuParams.cpuVoltageOffset); }}
        onClear={() => clearOverride(settings.mode, ['cpuVoltageOffset'])}
        onChange={v => { update('cpuVoltageOffset')(v); queueSmu('co_all', v); }}
      />
      <PerCoreCoPanel
        settings={settings}
        overrides={overrides}
        saveOverride={saveOverride}
        clearOverride={clearOverride}
        switching={switching}
      />
    </>
  );
}
