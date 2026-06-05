function buildPoints(values, width, height, padding) {
  if (!values || !values.length) return "";
  const cleaned = values.filter(v => typeof v === "number" && !Number.isNaN(v));
  if (!cleaned.length) return "";
  const min = Math.min(...cleaned);
  const max = Math.max(...cleaned);
  const range = Math.max(1, max - min);
  const stepX = (width - padding * 2) / Math.max(1, cleaned.length - 1);

  return cleaned
    .map((value, index) => {
      const x = padding + index * stepX;
      const normalized = (value - min) / range;
      const y = height - padding - normalized * (height - padding * 2);
      return `${x},${y}`;
    })
    .join(" ");
}

export default function Sparkline({ data, title = "趋势", color = "var(--primary)" }) {
  const width = 320;
  const height = 96;
  const padding = 10;
  const points = buildPoints(data, width, height, padding);

  return (
    <div
      className="rounded-lg p-2"
      style={{ background: "var(--card-2)", border: "1px solid var(--border)" }}
    >
      <p className="text-xs mb-1" style={{ color: "var(--muted)" }}>
        {title}
      </p>
      <svg viewBox={`0 0 ${width} ${height}`} className="w-full h-24">
        <polyline fill="none" stroke={color} strokeWidth="2.5" points={points} strokeLinejoin="round" />
      </svg>
    </div>
  );
}
