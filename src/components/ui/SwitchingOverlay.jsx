// 模式切换期间的全屏遮罩：拦截点击，防止切换过程中 setter 写入错误模式的配置文件
export default function SwitchingOverlay({ active }) {
  if (!active) return null;
  return (
    <div style={{
      position: "fixed", inset: 0, zIndex: 9999,
      background: "rgba(0,0,0,0.25)",
      display: "flex", alignItems: "center", justifyContent: "center",
      cursor: "wait",
    }}>
      <div style={{
        background: "var(--card-2, #1e1e2e)",
        color: "var(--text, #e0e0e0)",
        padding: "12px 28px",
        borderRadius: 12,
        fontSize: 14,
        boxShadow: "0 4px 24px rgba(0,0,0,0.4)",
      }}>
        切换模式中…
      </div>
    </div>
  );
}
