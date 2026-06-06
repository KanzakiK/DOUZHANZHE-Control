export default function SliderRow({ label, value, min, max, step = 1, onChange, unit = "", displayValue, disabled = false }) {
  return (
    <label className="block" style={{ opacity: disabled ? 0.5 : 1, cursor: disabled ? "not-allowed" : "auto" }}>
      <div className="flex justify-between text-sm mb-1">
        <span>{label}</span>
        <span style={{ color: "var(--muted)" }}>
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
        className="w-full accent-cyan-400"
      />
    </label>
  );
}
