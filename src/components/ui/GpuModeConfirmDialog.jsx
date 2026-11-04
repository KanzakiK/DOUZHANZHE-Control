import { useEffect } from "react";
import { createPortal } from "react-dom";

export default function GpuModeConfirmDialog({
  open,
  currentLabel,
  targetLabel,
  externalDisplayRisk = false,
  busy = false,
  error = "",
  onCancel,
  onConfirm,
  onRestart,
}) {
  useEffect(() => {
    if (!open || busy) return undefined;

    const handleKeyDown = (event) => {
      if (event.key === "Escape") onCancel();
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, busy, onCancel]);

  if (!open) return null;

  return createPortal(
    <div
      className="modal"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) onCancel();
      }}
    >
      <div
        className="panel gpu-mode-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="gpu-mode-dialog-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="p-head">
          <span className="gpu-mode-dialog-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7">
              <rect x="3" y="5" width="18" height="13" rx="2" />
              <path d="M8 21h8M12 18v3M8 9h8M8 13h5" />
            </svg>
          </span>
          <span>
            <b id="gpu-mode-dialog-title">确认切换 GPU 模式</b>
            <small>显卡模式写入后需要重启系统才能生效</small>
          </span>
        </div>

        <div className="p-body">
          <div className="gpu-mode-route" aria-label={`${currentLabel || "未知"} 切换到 ${targetLabel}`}>
            <span className="gpu-mode-chip">
              <small>当前</small>
              <b>{currentLabel || "未知"}</b>
            </span>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
              <path d="M5 12h14M14 7l5 5-5 5" />
            </svg>
            <span className="gpu-mode-chip target">
              <small>切换至</small>
              <b>{targetLabel}</b>
            </span>
          </div>

          <div className={`gpu-mode-warning${externalDisplayRisk ? " danger" : ""}`}>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
              <path d="M12 3 2.8 20h18.4L12 3Z" />
              <path d="M12 9v5M12 17h.01" />
            </svg>
            <div>
              <b>{externalDisplayRisk ? "集显模式可能暂时中断外接显示输出" : "切换不会立即改变当前显示状态"}</b>
              <p>
                确认后仅写入目标模式，必须重启电脑后才会生效。
                立即重启会关闭所有程序，请先保存工作。
              </p>
            </div>
          </div>

          {error && <p className="gpu-mode-error" role="alert">{error}</p>}
        </div>

        <div className="p-foot gpu-mode-dialog-foot">
          <button type="button" className="btn ghost" onClick={onCancel} disabled={busy} autoFocus>
            取消
          </button>
          <button type="button" className="btn primary" onClick={onConfirm} disabled={busy}>
            {busy ? "正在切换…" : "确认切换"}
          </button>
          <button type="button" className="btn danger" onClick={onRestart} disabled={busy}>
            {busy ? "正在处理…" : "立即重启"}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
