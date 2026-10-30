import { useState, useEffect, useRef } from "react";
import { useControlState } from "../hooks/useControlState";
import FanCurvePanel from "../components/panels/FanCurvePanel";
import { fetchFanCurveStatus, fetchRouteInfo, getFanRange, MODE_FAN_DEFAULTS, resolvePerfMode } from "../services/uxtuAdapter";

export default function FanControl() {
  const { telemetry, settings, overrides, profiles, setOverrides, setUxtuParams } = useControlState();
  const [curveActive, setCurveActive] = useState(false);
  // 风扇区间/默认按「性能模式」取，配置 id 先解包成性能模式裸名
  const perfMode = resolvePerfMode(settings.mode, profiles);
  const fanRange = getFanRange(perfMode);
  const [fan1TargetRpm, setFan1TargetRpm] = useState(() => (MODE_FAN_DEFAULTS[perfMode] || MODE_FAN_DEFAULTS.silent).fanLargeRpmTarget);
  const [fan2TargetRpm, setFan2TargetRpm] = useState(() => (MODE_FAN_DEFAULTS[perfMode] || MODE_FAN_DEFAULTS.silent).fanSmallRpmTarget);
  const [routeInfo, setRouteInfo] = useState(null);

  const fan1Rpm = telemetry?.fanLargeRpm ?? 0;
  const fan2Rpm = telemetry?.fanSmallRpm ?? 0;
  const fan1Pct = telemetry?.fanLargeMax ? Math.min(100, (fan1Rpm / telemetry.fanLargeMax) * 100) : 0;
  const fan2Pct = telemetry?.fanSmallMax ? Math.min(100, (fan2Rpm / telemetry.fanSmallMax) * 100) : 0;

  // ovRef：perfMode 定时器回调里读取最新 overrides（避免闭包取到旧值）
  const ovRef = useRef(overrides);
  ovRef.current = overrides;

  useEffect(() => {
    const def = MODE_FAN_DEFAULTS[perfMode] || MODE_FAN_DEFAULTS.silent;
    const timer = setTimeout(() => {
      // 优先用当前配置的实际存值（ParameterGuard 周期重发的就是它），未设置才落模式默认值
      const ov = ovRef.current || {};
      setFan1TargetRpm(ov.fanLargeRpmTarget ?? def.fanLargeRpmTarget);
      setFan2TargetRpm(ov.fanSmallRpmTarget ?? def.fanSmallRpmTarget);
    }, 0);
    return () => clearTimeout(timer);
  }, [perfMode]);

  // overrides 晚于挂载到达（应用启动拉取/切配置响应）时同步滑块。
  // 拖动后 2s 内跳过：POST 保存与 overrides 轮询存在竞态，避免拖动中滑块被旧值拉回。
  const lastDragRef = useRef(0);
  useEffect(() => {
    if (overrides == null) return;
    if (Date.now() - lastDragRef.current < 2000) return;
    if (overrides.fanLargeRpmTarget != null) setFan1TargetRpm(overrides.fanLargeRpmTarget);
    if (overrides.fanSmallRpmTarget != null) setFan2TargetRpm(overrides.fanSmallRpmTarget);
  }, [overrides?.fanLargeRpmTarget, overrides?.fanSmallRpmTarget]);

  useEffect(() => {
    let disposed = false;
    const refresh = async () => {
      try {
        const [status, route] = await Promise.all([fetchFanCurveStatus(), fetchRouteInfo()]);
        if (disposed) return;
        if (status?.ok) setCurveActive(status.active);
        setRouteInfo(route);
      } catch { /* backend offline */ }
    };
    refresh();
    const timer = setInterval(refresh, 2000);
    return () => { disposed = true; clearInterval(timer); };
  }, []);

  const setFanTarget = async (fanIdx, rpm) => {
    lastDragRef.current = Date.now();
    try {
      const body = fanIdx === 0 ? { largeRpm: rpm } : { smallRpm: rpm };
      await fetch(`/api/fan/set-target?mode=${encodeURIComponent(settings.mode)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      // 同步到共享 store：否则切 tab 卸载重挂后滑块读不到刚设的值（overrides 是稀疏存储，fan 属于用户改过项）
      const key = fanIdx === 0 ? "fanLargeRpmTarget" : "fanSmallRpmTarget";
      setOverrides(prev => ({ ...prev, [key]: rpm }));
      setUxtuParams(prev => ({ ...prev, [key]: rpm }));
    } catch { /* 调速失败保持当前滑块值 */ }
  };

  return (
    <section className="page active">
      <div className="page-head">
        <div>
          <h1>风扇控制</h1>
          <p>EC 寄存器绑定 · 手动调速与自定义曲线互斥 · 仅斗战者机型可见</p>
        </div>
      </div>

      {routeInfo?.deviationAlert && (
        <div className="fan-alert reveal enter">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="18" height="18"><path d="M12 9v4M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>
          <span className="fa-text">
            <b>风扇偏离告警</b>
            <small>连续 {routeInfo.consecutiveDeviation} 次采样未达到目标转速：大风扇偏差 {routeInfo.largeDeviationRpm} RPM · 小风扇偏差 {routeInfo.smallDeviationRpm} RPM</small>
          </span>
        </div>
      )}

      {/* 实时监控 */}
      <div className="section-title">实时监控<span className="line"></span></div>
      <div className="card reveal enter" style={{ padding: "6px 20px", animationDelay: ".02s" }}>
        <div className="fan-row">
          <span className="fname"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"><circle cx="12" cy="12" r="2.4"/><path d="M12 9.6c0-3 1.5-5 4-5 1.5 2-.5 5-4 5Zm2.1 3.3c2.6 1.5 3.4 3.7 2.2 5.9-2.4.4-4-2.4-2.2-5.9Zm-6.3.1c-2.6 1.5-4.8.7-5.9-1.6 1.6-1.9 4.7-1 6 1.6Z"/></svg>大风扇</span>
          <div className="bar"><i style={{ width: fan1Pct + "%" }}></i></div>
          <span className="rpm"><b>{fan1Rpm}</b> RPM<small>实时 {Math.round(fan1Pct)}%</small></span>
        </div>
        <div className="fan-row">
          <span className="fname"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6"><circle cx="12" cy="12" r="2.4"/><path d="M12 9.6c0-3 1.5-5 4-5 1.5 2-.5 5-4 5Zm2.1 3.3c2.6 1.5 3.4 3.7 2.2 5.9-2.4.4-4-2.4-2.2-5.9Zm-6.3.1c-2.6 1.5-4.8.7-5.9-1.6 1.6-1.9 4.7-1 6 1.6Z"/></svg>小风扇</span>
          <div className="bar"><i style={{ width: fan2Pct + "%" }}></i></div>
          <span className="rpm"><b>{fan2Rpm}</b> RPM<small>实时 {Math.round(fan2Pct)}%</small></span>
        </div>
      </div>

      {/* 手动调速 */}
      <div className="section-title">手动调速<span className={"tag" + (curveActive ? "" : " hidden")}>曲线运行时禁用</span><span className="line"></span></div>
      <div className="card reveal enter" style={{ padding: "6px 20px 12px", animationDelay: ".06s" }}>
        <div className="param">
          <span className="pk"><b>大风扇目标</b><small>固定转速 · 当前模式 {fanRange.largeMin}–{fanRange.largeMax} RPM</small></span>
          <input type="range" className="slider fan-manual" min={fanRange.largeMin} max={fanRange.largeMax} step="100" value={fan1TargetRpm} disabled={curveActive}
            onChange={e => { const v = Number(e.target.value); setFan1TargetRpm(v); setFanTarget(0, v); }} />
          <span className="pv">{fan1TargetRpm} <small>RPM</small></span>
        </div>
        <div className="param">
          <span className="pk"><b>小风扇目标</b><small>固定转速 · 当前模式 {fanRange.smallMin}–{fanRange.smallMax} RPM</small></span>
          <input type="range" className="slider fan-manual" min={fanRange.smallMin} max={fanRange.smallMax} step="100" value={fan2TargetRpm} disabled={curveActive}
            onChange={e => { const v = Number(e.target.value); setFan2TargetRpm(v); setFanTarget(1, v); }} />
          <span className="pv">{fan2TargetRpm} <small>RPM</small></span>
        </div>
        <div className="hint">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 9v4M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/></svg>
          手动调速与自定义曲线互斥；可调范围随散热模式变化（安静 1900–2900，均衡 2600–3500，野兽 3200–3800，斗战 4000–4400）。
        </div>
      </div>

      {/* 自定义风扇曲线 */}
      <div className="section-title">自定义风扇曲线<span className="line"></span></div>
      <div className="card reveal enter" style={{ animationDelay: ".1s" }}>
        <FanCurvePanel mode={settings.mode} overrides={overrides} onCurveActiveChange={setCurveActive} />
      </div>
    </section>
  );
}
