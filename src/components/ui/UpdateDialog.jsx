import { useState, useEffect } from "react";

const STORAGE_KEY = "douzhanzhe_update_check";
const SKIP_KEY = "douzhanzhe_update_skip_version";

export default function UpdateDialog({ autoCheck = true }) {
  const [visible, setVisible] = useState(false);
  const [loading, setLoading] = useState(false);
  const [updateInfo, setUpdateInfo] = useState(null);

  // 检查更新
  const checkUpdate = async () => {
    setLoading(true);
    try {
      const res = await fetch("/api/update/check");
      const data = await res.json();
      if (data.error) {
        console.warn("[update] check failed:", data.error);
        return;
      }

      if (data.available) {
        // 检查用户是否跳过了此版本
        const skipped = localStorage.getItem(SKIP_KEY);
        if (skipped === data.latestVersion) {
          return; // 跳过，不提示
        }

        setUpdateInfo(data);
        setVisible(true);
      }
    } catch (e) {
      console.warn("[update] check error:", e);
    } finally {
      setLoading(false);
    }
  };

  // 记录检查时间
  const recordCheck = () => {
    localStorage.setItem(STORAGE_KEY, Date.now().toString());
  };

  // 启动时自动检查
  useEffect(() => {
    if (!autoCheck) return;

    const lastCheck = localStorage.getItem(STORAGE_KEY);
    const now = Date.now();
    const DAY_MS = 24 * 60 * 60 * 1000;

    if (!lastCheck || now - parseInt(lastCheck, 10) > DAY_MS) {
      // 首次或超过 24 小时，检查更新
      checkUpdate().then(() => recordCheck());
    }
  }, [autoCheck]);

  // 前往下载
  const handleDownload = () => {
    if (updateInfo?.url) {
      window.open(updateInfo.url, "_blank", "noopener,noreferrer");
    }
    setVisible(false);
  };

  // 跳过此版本
  const handleSkip = () => {
    if (updateInfo?.latestVersion) {
      localStorage.setItem(SKIP_KEY, updateInfo.latestVersion);
    }
    setVisible(false);
  };

  // 稍后提醒
  const handleLater = () => {
    setVisible(false);
  };

  if (!visible || !updateInfo) return null;

  return (
    <div
      style={{
        position: "fixed",
        inset: 0,
        zIndex: 9999,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        background: "rgba(0,0,0,0.5)",
        backdropFilter: "blur(4px)",
      }}
      onClick={handleLater}
    >
      <div
        style={{
          background: "var(--card, #1e1e2e)",
          border: "1px solid var(--border, #333)",
          borderRadius: "12px",
          padding: "24px",
          maxWidth: "480px",
          width: "90%",
          boxShadow: "0 8px 32px rgba(0,0,0,0.4)",
        }}
        onClick={(e) => e.stopPropagation()}
      >
        {/* 标题 */}
        <div style={{ display: "flex", alignItems: "center", gap: "12px", marginBottom: "16px" }}>
          <span style={{ fontSize: "24px" }}>🎉</span>
          <div>
            <h3 style={{ margin: 0, fontSize: "18px", fontWeight: 600, color: "var(--text, #fff)" }}>
              发现新版本
            </h3>
            <p style={{ margin: "4px 0 0", fontSize: "13px", color: "var(--muted, #888)" }}>
              v{updateInfo.currentVersion} → v{updateInfo.latestVersion}
            </p>
          </div>
        </div>

        {/* 更新日志 */}
        <div
          style={{
            background: "var(--card-2, #181825)",
            borderRadius: "8px",
            padding: "12px",
            marginBottom: "20px",
            maxHeight: "200px",
            overflowY: "auto",
            fontSize: "13px",
            lineHeight: 1.6,
            color: "var(--text-secondary, #ccc)",
            whiteSpace: "pre-wrap",
          }}
        >
          {updateInfo.body || "暂无更新日志"}
        </div>

        {/* 发布时间 */}
        {updateInfo.publishedAt && (
          <p style={{ margin: "0 0 16px", fontSize: "12px", color: "var(--muted, #888)" }}>
            发布于 {new Date(updateInfo.publishedAt).toLocaleDateString("zh-CN")}
          </p>
        )}

        {/* 按钮组 */}
        <div style={{ display: "flex", gap: "8px", justifyContent: "flex-end" }}>
          <button
            onClick={handleSkip}
            style={{
              padding: "8px 16px",
              borderRadius: "6px",
              border: "1px solid var(--border, #333)",
              background: "transparent",
              color: "var(--muted, #888)",
              cursor: "pointer",
              fontSize: "13px",
            }}
          >
            跳过此版本
          </button>
          <button
            onClick={handleLater}
            style={{
              padding: "8px 16px",
              borderRadius: "6px",
              border: "1px solid var(--border, #333)",
              background: "transparent",
              color: "var(--text, #fff)",
              cursor: "pointer",
              fontSize: "13px",
            }}
          >
            稍后提醒
          </button>
          <button
            onClick={handleDownload}
            style={{
              padding: "8px 20px",
              borderRadius: "6px",
              border: "none",
              background: "var(--primary, #4f46e5)",
              color: "#fff",
              cursor: "pointer",
              fontSize: "13px",
              fontWeight: 500,
            }}
          >
            前往下载
          </button>
        </div>
      </div>
    </div>
  );
}
