export default function Card({ title, children, action, className = "", bodyClassName = "" }) {
  return (
    <section
      className={`rounded-2xl p-2.5 md:p-3 break-inside-avoid ${className}`}
      style={{
        background: "var(--card)",
        border: "1px solid var(--border)",
      }}
    >
      <div className="flex items-center justify-between mb-2">
        <h3 className="text-sm md:text-base font-semibold">{title}</h3>
        {action}
      </div>
      <div className={bodyClassName}>{children}</div>
    </section>
  );
}
