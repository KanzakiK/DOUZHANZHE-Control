export default function SliderRow({ label, value, min, max, step = 1, onChange, unit = "", displayValue, disabled = false, isCustom }) {
  // isCustom: undefined = 不启用双态；true = 已自定义(高亮)；false = EC 管理(灰色)
  const dimmed = isCustom === false;
  const highlighted = isCustom === true;
  // 滑动条颜色：高亮=主题紫，灰色=主题 muted 灰，未启用=系统青
  const accentColor = highlighted ? "var(--primary-2)" : dimmed ? "var(--muted)" : "var(--primary-2)";
  return (
    <label className="block" style={{ opacity: disabled ? 0.5 : dimmed ? 0.6 : 1, cursor: disabled ? "not-allowed" : "auto" }}>
      <div className="flex justify-between text-sm mb-1">
        <span className="flex items-center gap-1.5">
          {highlighted && <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ background: "var(--primary-2)" }} />}
          <span style={{ color: highlighted ? "var(--text)" : isCustom === false ? "var(--muted)" : undefined }}>{label}</span>
          {dimmed && <span className="text-xs" style={{ color: "var(--muted)" }}>默认</span>}
        </span>
        <span style={{ color: highlighted ? "var(--primary-2)" : "var(--muted)" }}>
          {displayValue ?? value}{displayValue === "自动" ? "" : unit}
        </span>
      </div>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(Number(e.target.value))}
        className="w-full"
        style={{ accentColor }}
      />
    </label>
  );
}
