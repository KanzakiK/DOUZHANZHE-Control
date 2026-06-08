export default function SwitchRow({ label, checked, onChange, disabled = false }) {
  return (
    <div className="flex items-center justify-between py-1" style={{ opacity: disabled ? 0.5 : 1 }}>
      <span className="text-sm">{label}</span>
      <button
        onClick={() => !disabled && onChange(!checked)}
        className="w-12 h-7 rounded-full p-1 transition-all"
        style={{
          background: checked ? "var(--primary)" : "var(--card-2)",
          border: "1px solid var(--border)",
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
