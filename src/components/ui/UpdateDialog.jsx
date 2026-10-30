import { useState, useEffect } from "react";
import { useToast } from "./Toast";

const STORAGE_KEY = "douzhanzhe_update_check";
const SKIP_KEY = "douzhanzhe_update_skip_version";
const CLOSE_MS = 1800;

function formatSize(bytes) {
  if (!bytes && bytes !== 0) return "";
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export default function UpdateDialog({ autoCheck = true }) {
  const toast = useToast();
  const [visible, setVisible] = useState(false);
  const [updateInfo, setUpdateInfo] = useState(null);
  const [downloadState, setDownloadState] = useState("idle");
  const [downloadInfo, setDownloadInfo] = useState(null);
  const [errorMsg, setErrorMsg] = useState("");

  const canDownload = !!updateInfo?.downloadUrl && /\.exe(\?.*)?$/i.test(updateInfo.downloadUrl);

  const checkUpdate = async (manual = false) => {
    try {
      const res = await fetch("/api/update/check");
      const data = await res.json();
      if (data.error) {
        console.warn("[update] check failed:", data.error);
        if (manual) {
          toast?.(`检查更新失败: ${data.error}`, "error");
          window.dispatchEvent(new CustomEvent("update-check-result", { detail: { error: true, msg: `检查更新失败: ${data.error}` } }));
        }
        return;
      }

      if (data.available) {
        const skipped = localStorage.getItem(SKIP_KEY);
        if (skipped === data.latestVersion) {
          if (manual) {
            toast?.(`版本 ${data.latestVersion} 已被你选择跳过，如需恢复可清除浏览器数据`, "info");
            window.dispatchEvent(new CustomEvent("update-check-result", { detail: { skipped: true, version: data.latestVersion } }));
          }
          return;
        }

        setUpdateInfo(data);
        setDownloadState("idle");
        setDownloadInfo(null);
        setErrorMsg("");
        setVisible(true);
      } else {
        if (manual) {
          toast?.(`当前已是最新版本（${data.currentVersion || "未知"}）`, "info");
          window.dispatchEvent(new CustomEvent("update-check-result", { detail: { upToDate: true, version: data.currentVersion } }));
        }
      }
    } catch (e) {
      console.warn("[update] check error:", e);
      if (manual) {
        toast?.("检查更新失败：无法连接更新服务", "error");
        window.dispatchEvent(new CustomEvent("update-check-result", { detail: { error: true, msg: "检查更新失败" } }));
      }
    }
  };

  useEffect(() => {
    const handler = () => checkUpdate(true);
    window.addEventListener("check-update-manual", handler);
    return () => window.removeEventListener("check-update-manual", handler);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!autoCheck) return;

    const lastCheck = localStorage.getItem(STORAGE_KEY);
    const now = Date.now();
    const DAY_MS = 24 * 60 * 60 * 1000;

    if (!lastCheck || now - parseInt(lastCheck, 10) > DAY_MS) {
      const timer = setTimeout(() => {
        checkUpdate().then(() => {
          localStorage.setItem(STORAGE_KEY, Date.now().toString());
        });
      }, 0);
      return () => clearTimeout(timer);
    }
    return undefined;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoCheck]);

  useEffect(() => {
    if (downloadState !== "done") return;
    const t = setTimeout(() => setVisible(false), CLOSE_MS);
    return () => clearTimeout(t);
  }, [downloadState]);

  const handleDownload = async () => {
    const target = updateInfo?.downloadUrl || updateInfo?.url;
    if (!target || !canDownload) {
      setErrorMsg("未找到可下载的安装包，请前往发布页手动下载。");
      setDownloadState("error");
      return;
    }

    setDownloadState("downloading");
    setErrorMsg("");
    try {
      const res = await fetch("/api/update/download", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url: target }),
      });
      const data = await res.json();
      if (!res.ok || !data?.ok) throw new Error(data?.error || `下载失败: ${res.status}`);
      setDownloadInfo(data);
      setDownloadState("ready");
    } catch (e) {
      console.error("[update] download error:", e);
      setErrorMsg(e.message || "下载失败");
      setDownloadState("error");
    }
  };

  const handleInstall = async () => {
    if (!downloadInfo?.path) return;
    setDownloadState("installing");
    setErrorMsg("");
    try {
      const res = await fetch("/api/update/install", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: downloadInfo.path }),
      });
      const data = await res.json();
      if (!res.ok || !data?.ok) throw new Error(data?.error || "启动安装失败");
      setDownloadState("done");
    } catch (e) {
      console.error("[update] install error:", e);
      setErrorMsg(e.message || "启动安装失败");
      setDownloadState("error");
    }
  };

  const handleOpenRelease = () => {
    const target = updateInfo?.url || updateInfo?.downloadUrl;
    if (target) window.open(target, "_blank", "noopener,noreferrer");
    setVisible(false);
  };

  const handleMain = () => {
    if (downloadState === "downloading" || downloadState === "installing" || downloadState === "done") return;
    if (downloadState === "ready" || (downloadState === "error" && downloadInfo)) {
      handleInstall();
      return;
    }
    if (canDownload) {
      handleDownload();
    } else {
      handleOpenRelease();
    }
  };

  const handleSkip = () => {
    if (updateInfo?.latestVersion) {
      localStorage.setItem(SKIP_KEY, updateInfo.latestVersion);
    }
    setVisible(false);
  };

  const handleLater = () => {
    setVisible(false);
  };

  if (!visible || !updateInfo) return null;

  const busy = downloadState === "downloading" || downloadState === "installing";
  const mainLabel = downloadState === "downloading"
    ? "下载中..."
    : downloadState === "ready"
      ? "立即安装"
      : downloadState === "installing"
        ? "正在启动安装..."
        : downloadState === "done"
          ? "已启动安装程序"
          : downloadState === "error"
            ? (downloadInfo ? "重试安装" : "重试下载")
            : canDownload
              ? "下载并安装"
              : "前往发布页";

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
        <div style={{ display: "flex", alignItems: "center", gap: "12px", marginBottom: "16px" }}>
          <span
            style={{
              width: 40,
              height: 40,
              borderRadius: 10,
              display: "grid",
              placeItems: "center",
              flexShrink: 0,
              background: "color-mix(in srgb, var(--primary) 16%, transparent)",
              color: "var(--primary)",
            }}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="20" height="20"><path d="M12 3v12M7 10l5 5 5-5M4 21h16" /></svg>
          </span>
          <div>
            <h3 style={{ margin: 0, fontSize: "18px", fontWeight: 600, color: "var(--text, #fff)" }}>
              发现新版本
            </h3>
            <p style={{ margin: "4px 0 0", fontSize: "13px", color: "var(--muted, #888)" }}>
              v{updateInfo.currentVersion} → v{updateInfo.latestVersion}
            </p>
          </div>
        </div>

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

        {updateInfo.publishedAt && (
          <p style={{ margin: "0 0 16px", fontSize: "12px", color: "var(--muted, #888)" }}>
            发布于 {new Date(updateInfo.publishedAt).toLocaleDateString("zh-CN")}
          </p>
        )}

        {(downloadState !== "idle" || errorMsg) && (
          <div
            style={{
              margin: "0 0 16px",
              padding: "10px 12px",
              borderRadius: "8px",
              fontSize: "12.5px",
              lineHeight: 1.55,
              color: downloadState === "error" ? "#ffb454" : "var(--text-secondary, #ccc)",
              background: "var(--card-2, #181825)",
              border: "1px solid var(--border, #333)",
            }}
          >
            {downloadState === "downloading" && "正在应用内下载安装包，请稍候..."}
            {downloadState === "ready" && `安装包已下载${formatSize(downloadInfo?.size) ? `（${formatSize(downloadInfo.size)}）` : ""}，点击“立即安装”启动安装程序。`}
            {downloadState === "installing" && "正在启动安装程序..."}
            {downloadState === "done" && "安装程序已启动，安装完成后请重新打开应用。"}
            {downloadState === "error" && `操作失败：${errorMsg}`}
          </div>
        )}

        <div style={{ display: "flex", gap: "8px", justifyContent: "flex-end" }}>
          <button
            onClick={handleSkip}
            disabled={busy}
            style={{
              padding: "8px 16px",
              borderRadius: "6px",
              border: "1px solid var(--border, #333)",
              background: "transparent",
              color: "var(--muted, #888)",
              cursor: busy ? "not-allowed" : "pointer",
              opacity: busy ? 0.5 : 1,
              fontSize: "13px",
            }}
          >
            跳过此版本
          </button>
          <button
            onClick={handleLater}
            disabled={busy}
            style={{
              padding: "8px 16px",
              borderRadius: "6px",
              border: "1px solid var(--border, #333)",
              background: "transparent",
              color: "var(--text, #fff)",
              cursor: busy ? "not-allowed" : "pointer",
              opacity: busy ? 0.5 : 1,
              fontSize: "13px",
            }}
          >
            稍后提醒
          </button>
          <button
            onClick={handleMain}
            disabled={busy || downloadState === "done"}
            style={{
              padding: "8px 20px",
              borderRadius: "6px",
              border: "none",
              background: "var(--primary, #4f46e5)",
              color: "#fff",
              cursor: busy || downloadState === "done" ? "default" : "pointer",
              opacity: busy || downloadState === "done" ? 0.6 : 1,
              fontSize: "13px",
              fontWeight: 500,
            }}
          >
            {mainLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
