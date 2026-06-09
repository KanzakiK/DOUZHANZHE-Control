export default function SwitchRow({ label, checked, onChange, disabled = false, isCustom }) {
  // isCustom: undefined = 不启用双态；true = 已自定义(高亮)；false = EC 管理(灰色)
  const dimmed = isCustom === false;
  const highlighted = isCustom === true;
  return (
    <div className="flex items-center justify-between py-1" style={{ opacity: disabled ? 0.5 : dimmed ? 0.6 : 1 }}>
      <span className="text-sm flex items-center gap-1.5">
        {highlighted && <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ background: "var(--primary-2)" }} />}
        <span style={{ color: highlighted ? "var(--text)" : isCustom === false ? "var(--muted)" : undefined }}>{label}</span>
        {dimmed && <span className="text-xs" style={{ color: "var(--muted)" }}>默认</span>}
      </span>
      <button
        onClick={() => !disabled && onChange(!checked)}
        className="w-12 h-7 rounded-full p-1 transition-all"
        style={{
          background: checked ? "var(--primary)" : "var(--card-2)",
          border: `1px solid ${highlighted ? "var(--primary-2)" : "var(--border)"}`,
          cursor: disabled ? "not-allowed" : "pointer",
        }}
      >
        <div
          className="w-5 h-5 rounded-full bg-white transition-all"
          style={{ transform: checked ? "translateX(20px)" : "translateX(0)" }}
        />
      </button>
    </div>
  );
}
