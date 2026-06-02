export default function Gauge({ label, value, unit = "%", color = "var(--primary)", max = 100 }) {
  const pct = max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0;

  return (
    <div className="flex items-center justify-between gap-3">
      <div>
        <p className="text-xs" style={{ color: "var(--muted)" }}>
          {label}
        </p>
        <p className="text-xl font-bold">
          {value}
          {unit}
        </p>
      </div>
      <div className="w-24 h-2 rounded-full" style={{ background: "var(--card-2)" }}>
        <div className="h-2 rounded-full transition-all" style={{ width: `${pct}%`, background: color }} />
      </div>
    </div>
  );
}
