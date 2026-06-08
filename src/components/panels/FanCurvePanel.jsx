import { useState, useEffect, useRef, useCallback } from "react";
import Card from "../ui/Card";
import { useToast } from "../ui/Toast";
import {
  fetchFanCurveStatus,
  saveFanCurve,
  startFanCurve,
  stopFanCurve,
} from "../../services/uxtuAdapter";

// ── 图表常量 ──
const W = 700, H = 380;
const PL = 52, PR = 18, PT = 18, PB = 36;
const CW = W - PL - PR;
const CH = H - PT - PB;
const T_MIN = 40, T_MAX = 105;
const RPM_MAX = 8400;

// 默认曲线 (对齐 BellatorFanControl + 向两端延展)
// 40°C = 设备允许的最低转速, 90-100°C = 高温满载区
const DEFAULT_POINTS = [
  { temp: 40, largeRpm: 1900, smallRpm: 1700 },
  { temp: 50, largeRpm: 2200, smallRpm: 2000 },
  { temp: 55, largeRpm: 2600, smallRpm: 3500 },
  { temp: 60, largeRpm: 2900, smallRpm: 4800 },
  { temp: 65, largeRpm: 3200, smallRpm: 5900 },
  { temp: 70, largeRpm: 3500, smallRpm: 6400 },
  { temp: 75, largeRpm: 3800, smallRpm: 6900 },
  { temp: 80, largeRpm: 4000, smallRpm: 7500 },
  { temp: 85, largeRpm: 4300, smallRpm: 8000 },
  { temp: 90, largeRpm: 4400, smallRpm: 8200 },
  { temp: 95, largeRpm: 4400, smallRpm: 8200 },
  { temp: 100, largeRpm: 4400, smallRpm: 8200 },
];

// ── 坐标映射 ──
const tX = (t) => PL + ((t - T_MIN) / (T_MAX - T_MIN)) * CW;
const rY = (r) => PT + CH - (r / RPM_MAX) * CH;
const xT = (x) => T_MIN + ((x - PL) / CW) * (T_MAX - T_MIN);
const yR = (y) => ((PT + CH - y) / CH) * RPM_MAX;
const snap5 = (v) => Math.round(v / 5) * 5;
const snap100 = (v) => Math.round(v / 100) * 100;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

// ── 构建折线路径 ──
function buildPath(pts, rpmKey) {
  const sorted = [...pts].sort((a, b) => a.temp - b.temp);
  return sorted.map((p, i) => `${i === 0 ? "M" : "L"}${tX(p.temp).toFixed(1)},${rY(p[rpmKey]).toFixed(1)}`).join(" ");
}

// ── 首尾延伸（虚线段）──
// 用首/尾两点的斜率向两侧延展到 T_MIN / T_MAX
function buildExtendPaths(pts, rpmKey, maxRpm) {
  const sorted = [...pts].sort((a, b) => a.temp - b.temp);
  if (sorted.length < 2) return { head: "", tail: "" };
  const first = sorted[0], second = sorted[1];
  const last = sorted[sorted.length - 1], prev = sorted[sorted.length - 2];

  // 向下延伸: first → T_MIN
  const slopeHead = (first[rpmKey] - second[rpmKey]) / (first.temp - second.temp || 1);
  const headRpm = clamp(Math.round(first[rpmKey] + slopeHead * (T_MIN - first.temp)), 0, maxRpm);
  const head = first.temp > T_MIN
    ? `M${tX(T_MIN).toFixed(1)},${rY(headRpm).toFixed(1)} L${tX(first.temp).toFixed(1)},${rY(first[rpmKey]).toFixed(1)}`
    : "";

  // 向上延伸: last → T_MAX
  const slopeTail = (last[rpmKey] - prev[rpmKey]) / (last.temp - prev.temp || 1);
  const tailRpm = clamp(Math.round(last[rpmKey] + slopeTail * (T_MAX - last.temp)), 0, maxRpm);
  const tail = last.temp < T_MAX
    ? `M${tX(last.temp).toFixed(1)},${rY(last[rpmKey]).toFixed(1)} L${tX(T_MAX).toFixed(1)},${rY(tailRpm).toFixed(1)}`
    : "";

  return { head, tail };
}

export default function FanCurvePanel({ telemetry }) {
  const toast = useToast();

  const [points, setPoints] = useState(DEFAULT_POINTS);
  const [curveActive, setCurveActive] = useState(false);
  const [selIdx, setSelIdx] = useState(-1);
  const [interval, setInterval_] = useState(5);   // 秒
  const [hysteresis, setHysteresis] = useState(3);  // °C

  // 拖拽状态
  const svgRef = useRef(null);
  const dragRef = useRef(null); // { idx, series }

  // ── 加载后端状态 ──
  useEffect(() => {
    fetchFanCurveStatus()
      .then((s) => {
        if (s.ok) {
          setCurveActive(s.active);
          if (s.points?.length >= 2) setPoints(s.points);
        }
      })
      .catch(() => {});
  }, []);

  // ── 排序后的点 (用于绘制折线) ──
  const sorted = [...points].sort((a, b) => a.temp - b.temp);

  // ── 当前 hotspot 指示线 ──
  const hotspot = telemetry
    ? Math.max(telemetry.cpuTemp || 0, telemetry.gpuTemp || 0)
    : null;

  // ══════════════════════════════════════
  //  SVG 拖拽处理
  // ══════════════════════════════════════

  const handleMouseDown = useCallback((idx, series) => (e) => {
    e.preventDefault();
    e.stopPropagation();
    dragRef.current = { idx, series };
    setSelIdx(idx);
  }, []);

  const handleMouseMove = useCallback(
    (e) => {
      if (!dragRef.current || !svgRef.current) return;
      const rect = svgRef.current.getBoundingClientRect();
      const scaleX = W / rect.width;
      const scaleY = H / rect.height;
      const svgX = (e.clientX - rect.left) * scaleX;
      const svgY = (e.clientY - rect.top) * scaleY;

      let temp = snap5(clamp(xT(svgX), T_MIN, T_MAX));
      let largeRpm, smallRpm;

      const { idx, series } = dragRef.current;
      const pt = points[idx];

      if (series === "large") {
        largeRpm = snap100(clamp(yR(svgY), 0, 4400));
        smallRpm = pt.smallRpm;
      } else {
        largeRpm = pt.largeRpm;
        smallRpm = snap100(clamp(yR(svgY), 0, 8200));
      }

      setPoints((prev) =>
        prev.map((p, i) =>
          i === idx ? { ...p, temp, largeRpm, smallRpm } : p
        )
      );
    },
    [points]
  );

  const handleMouseUp = useCallback(() => {
    dragRef.current = null;
  }, []);

  // 点击空白取消选中
  const handleSvgClick = useCallback(() => {
    if (!dragRef.current) setSelIdx(-1);
  }, []);

  // ══════════════════════════════════════
  //  表格编辑
  // ══════════════════════════════════════

  const updateField = (idx, field, raw) => {
    const v = parseInt(raw, 10);
    if (isNaN(v)) return;
    setPoints((prev) =>
      prev.map((p, i) => {
        if (i !== idx) return p;
        if (field === "temp") return { ...p, temp: snap5(clamp(v, T_MIN, T_MAX)) };
        if (field === "largeRpm") return { ...p, largeRpm: snap100(clamp(v, 0, 4400)) };
        if (field === "smallRpm") return { ...p, smallRpm: snap100(clamp(v, 0, 8200)) };
        return p;
      })
    );
  };

  const addPoint = () => {
    if (points.length >= 16) {
      toast?.("最多 16 个控制点", "error");
      return;
    }
    const maxT = points.length > 0 ? Math.max(...points.map((p) => p.temp)) : 40;
    const newT = snap5(clamp(maxT + 5, T_MIN, T_MAX));
    setPoints((prev) => [...prev, { temp: newT, largeRpm: 3000, smallRpm: 5000 }]);
  };

  const removePoint = (idx) => {
    if (points.length <= 2) {
      toast?.("至少保留 2 个控制点", "error");
      return;
    }
    setPoints((prev) => prev.filter((_, i) => i !== idx));
    if (selIdx === idx) setSelIdx(-1);
  };

  // ══════════════════════════════════════
  //  操作按钮
  // ══════════════════════════════════════

  const handleSave = async () => {
    try {
      const r = await saveFanCurve(points, interval * 1000, hysteresis);
      toast?.(r.ok ? "曲线已保存" : "保存失败: " + (r.error || ""), r.ok ? "success" : "error");
    } catch (e) {
      toast?.("保存失败: " + e.message, "error");
    }
  };

  const handleApply = async () => {
    try {
      // 先保存再启用
      await saveFanCurve(points, interval * 1000, hysteresis);
      const r = await startFanCurve(interval * 1000, hysteresis);
      if (r.ok) {
        setCurveActive(true);
        toast?.("自定义散热曲线已启用", "success");
      } else {
        toast?.("启用失败: " + (r.error || ""), "error");
      }
    } catch (e) {
      toast?.("启用失败: " + e.message, "error");
    }
  };

  const handleStop = async () => {
    try {
      const r = await stopFanCurve();
      if (r.ok) {
        setCurveActive(false);
        toast?.("已恢复固件控制", "success");
      } else {
        toast?.("停止失败: " + (r.error || ""), "error");
      }
    } catch (e) {
      toast?.("停止失败: " + e.message, "error");
    }
  };

  const handleReset = () => {
    setPoints(DEFAULT_POINTS);
    setSelIdx(-1);
  };

  // ══════════════════════════════════════
  //  渲染
  // ══════════════════════════════════════

  return (
    <div className="space-y-4" style={{ maxWidth: 900 }}>
      {/* ── 曲线图 ── */}
      <Card
        title="散热曲线"
        action={
          <div className="flex items-center gap-2">
            <span className="flex items-center gap-1 text-xs" style={{ color: "#4fc3f7" }}>
              <span style={{ width: 14, height: 3, background: "#4fc3f7", borderRadius: 2, display: "inline-block" }} />
              大风扇
            </span>
            <span className="flex items-center gap-1 text-xs" style={{ color: "#ce93d8" }}>
              <span style={{ width: 14, height: 3, background: "#ce93d8", borderRadius: 2, display: "inline-block" }} />
              小风扇
            </span>
          </div>
        }
      >
        <svg
          ref={svgRef}
          viewBox={`0 0 ${W} ${H}`}
          className="w-full select-none"
          style={{ cursor: dragRef.current ? "grabbing" : "default" }}
          onMouseMove={handleMouseMove}
          onMouseUp={handleMouseUp}
          onMouseLeave={handleMouseUp}
          onClick={handleSvgClick}
        >
          {/* 图表背景 */}
          <rect x={PL} y={PT} width={CW} height={CH} rx="4" fill="var(--card-2)" opacity="0.5" />

          {/* ── 网格线 ── */}
          {/* Y 轴 (每 1000 RPM) */}
          {Array.from({ length: 9 }, (_, i) => i * 1000).map((rpm) => {
            const y = rY(rpm);
            return (
              <g key={`gy-${rpm}`}>
                <line x1={PL} y1={y} x2={PL + CW} y2={y} stroke="var(--border)" strokeWidth="0.5" />
                <text x={PL - 6} y={y + 3} textAnchor="end" fontSize="9" fill="var(--muted)">
                  {rpm > 0 ? `${(rpm / 1000).toFixed(0)}k` : "0"}
                </text>
              </g>
            );
          })}
          {/* X 轴 (每 10°C: 40, 50, ..., 100) */}
          {[40, 50, 60, 70, 80, 90, 100].map((t) => {
            const x = tX(t);
            return (
              <g key={`gx-${t}`}>
                <line x1={x} y1={PT} x2={x} y2={PT + CH} stroke="var(--border)" strokeWidth="0.5" />
                <text x={x} y={PT + CH + 16} textAnchor="middle" fontSize="9" fill="var(--muted)">
                  {t}°
                </text>
              </g>
            );
          })}

          {/* ── 当前 hotspot 指示线 ── */}
          {hotspot && hotspot > T_MIN && hotspot < T_MAX && (
            <g>
              <line
                x1={tX(hotspot)} y1={PT}
                x2={tX(hotspot)} y2={PT + CH}
                stroke="var(--warn)" strokeWidth="1.5" strokeDasharray="6,4" opacity="0.5"
              />
              <text x={tX(hotspot)} y={PT - 4} textAnchor="middle" fontSize="9" fill="var(--warn)" opacity="0.8">
                {hotspot}°C
              </text>
            </g>
          )}

          {/* ── 大扇折线 (蓝色) ── */}
          <path d={buildPath(points, "largeRpm")} fill="none" stroke="#4fc3f7" strokeWidth="2.5" strokeLinejoin="round" />
          {/* ── 小扇折线 (紫色) ── */}
          <path d={buildPath(points, "smallRpm")} fill="none" stroke="#ce93d8" strokeWidth="2.5" strokeLinejoin="round" />

          {/* ── 控制点 ── */}
          {points.map((p, i) => {
            const lx = tX(p.temp), ly = rY(p.largeRpm);
            const sx = tX(p.temp), sy = rY(p.smallRpm);
            const isSel = selIdx === i;
            return (
              <g key={`pt-${i}`}>
                {/* 连接虚线 */}
                <line x1={lx} y1={ly} x2={sx} y2={sy} stroke="var(--border)" strokeWidth="0.8" strokeDasharray="3,3" />
                {/* 大扇点 */}
                <circle
                  cx={lx} cy={ly} r={isSel ? 7 : 5}
                  fill="#4fc3f7" stroke={isSel ? "#fff" : "none"} strokeWidth="2"
                  style={{ cursor: "grab" }}
                  onMouseDown={handleMouseDown(i, "large")}
                />
                {/* 小扇点 */}
                <circle
                  cx={sx} cy={sy} r={isSel ? 7 : 5}
                  fill="#ce93d8" stroke={isSel ? "#fff" : "none"} strokeWidth="2"
                  style={{ cursor: "grab" }}
                  onMouseDown={handleMouseDown(i, "small")}
                />
              </g>
            );
          })}

          {/* 轴标签 */}
          <text x={W / 2} y={H - 2} textAnchor="middle" fontSize="10" fill="var(--muted)">温度 °C</text>
          <text x={12} y={H / 2} textAnchor="middle" fontSize="10" fill="var(--muted)" transform={`rotate(-90,12,${H / 2})`}>RPM</text>
        </svg>

        {/* 拖拽提示 */}
        <p className="text-xs mt-1 text-center" style={{ color: "var(--muted)" }}>
          拖拽圆点调整曲线 · 蓝色 = 大风扇 · 紫色 = 小风扇
        </p>
      </Card>

      {/* ── 配置表格 + 控制 ── */}
      <Card
        title="曲线参数"
        action={
          <div className="flex gap-2 items-center">
            <label className="text-xs flex items-center gap-1" style={{ color: "var(--muted)" }}>
              间隔
              <input
                type="number" min={1} max={30} value={interval}
                onChange={(e) => setInterval_(clamp(parseInt(e.target.value, 10) || 5, 1, 30))}
                className="w-12 text-center rounded px-1 py-0.5 text-xs"
                style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
              />s
            </label>
            <label className="text-xs flex items-center gap-1" style={{ color: "var(--muted)" }}>
              回差
              <input
                type="number" min={0} max={10} value={hysteresis}
                onChange={(e) => setHysteresis(clamp(parseInt(e.target.value, 10) || 3, 0, 10))}
                className="w-12 text-center rounded px-1 py-0.5 text-xs"
                style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
              />°C
            </label>
          </div>
        }
      >
        {/* 表头 */}
        <div
          className="grid gap-x-4 gap-y-1 text-xs font-medium px-1 py-1"
          style={{ gridTemplateColumns: "1fr 1fr", color: "var(--muted)" }}
        >
          <div className="grid gap-1" style={{ gridTemplateColumns: "1fr 1fr 1fr 28px" }}>
            <span>温度 °C</span>
            <span style={{ color: "#4fc3f7" }}>大风扇</span>
            <span style={{ color: "#ce93d8" }}>小风扇</span>
            <span />
          </div>
          <div className="grid gap-1" style={{ gridTemplateColumns: "1fr 1fr 1fr 28px" }}>
            <span>温度 °C</span>
            <span style={{ color: "#4fc3f7" }}>大风扇</span>
            <span style={{ color: "#ce93d8" }}>小风扇</span>
            <span />
          </div>
        </div>

        {/* 数据行：两列 */}
        <div className="grid gap-x-4 gap-y-1 max-h-[220px] overflow-y-auto" style={{ gridTemplateColumns: "1fr 1fr" }}>
          {sorted.map((p, sortIdx) => {
            const realIdx = points.indexOf(p);
            const isSel = selIdx === realIdx;
            return (
              <div
                key={`row-${sortIdx}`}
                className="grid gap-1 items-center rounded px-1 py-0.5"
                style={{
                  gridTemplateColumns: "1fr 1fr 1fr 28px",
                  background: isSel ? "var(--primary-2)" : "transparent",
                  opacity: isSel ? 0.9 : 1,
                }}
                onClick={() => setSelIdx(realIdx)}
              >
                <input
                  type="number" value={p.temp} min={T_MIN} max={T_MAX} step={5}
                  onChange={(e) => updateField(realIdx, "temp", e.target.value)}
                  className="w-full rounded px-2 py-1 text-xs"
                  style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
                />
                <input
                  type="number" value={p.largeRpm} min={0} max={4400} step={100}
                  onChange={(e) => updateField(realIdx, "largeRpm", e.target.value)}
                  className="w-full rounded px-2 py-1 text-xs"
                  style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
                />
                <input
                  type="number" value={p.smallRpm} min={0} max={8200} step={100}
                  onChange={(e) => updateField(realIdx, "smallRpm", e.target.value)}
                  className="w-full rounded px-2 py-1 text-xs"
                  style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
                />
                <button
                  onClick={(e) => { e.stopPropagation(); removePoint(realIdx); }}
                  className="text-xs rounded py-1"
                  style={{ color: "var(--danger)", background: "transparent" }}
                  title="删除"
                >
                  ×
                </button>
              </div>
            );
          })}
        </div>

        {/* 添加按钮 */}
        <button
          onClick={addPoint}
          className="w-full mt-2 text-xs rounded-lg py-1.5 transition"
          style={{ border: "1px dashed var(--border)", color: "var(--muted)", background: "transparent" }}
        >
          + 添加控制点
        </button>
      </Card>

      {/* ── 操作按钮 ── */}
      <div className="flex gap-2 flex-wrap">
        {curveActive ? (
          <button
            onClick={handleStop}
            className="flex-1 text-sm rounded-lg px-3 py-2.5 transition"
            style={{ background: "var(--danger)", color: "#fff" }}
          >
            停止曲线 · 恢复固件
          </button>
        ) : (
          <button
            onClick={handleApply}
            className="flex-1 text-sm rounded-lg px-3 py-2.5 transition"
            style={{ background: "var(--primary-2)", color: "#fff" }}
          >
            应用自定义曲线
          </button>
        )}
        <button
          onClick={handleSave}
          className="text-sm rounded-lg px-3 py-2.5 transition"
          style={{ border: "1px solid var(--border)", color: "var(--text)", background: "var(--card-2)" }}
        >
          保存配置
        </button>
        <button
          onClick={handleReset}
          className="text-sm rounded-lg px-3 py-2.5 transition"
          style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
        >
          恢复默认
        </button>
      </div>

      {/* ── 状态栏 ── */}
      {curveActive && (
        <div
          className="text-xs rounded-xl px-3 py-2 flex items-center gap-2"
          style={{ background: "var(--card)", border: "1px solid var(--ok)" }}
        >
          <span
            className="inline-block w-2 h-2 rounded-full"
            style={{ background: "var(--ok)", animation: "pulse 1.5s infinite" }}
          />
          <span style={{ color: "var(--ok)" }}>
            自定义曲线运行中 · 间隔 {interval}s · 回差 {hysteresis}°C
            {hotspot ? ` · 当前热点 ${hotspot}°C` : ""}
          </span>
        </div>
      )}

      {/* 脉冲动画 (内联样式) */}
      <style>{`
        @keyframes pulse {
          0%, 100% { opacity: 1; }
          50% { opacity: 0.3; }
        }
      `}</style>
    </div>
  );
}
