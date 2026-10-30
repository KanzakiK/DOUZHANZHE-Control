import { useState, useEffect, useRef, useCallback } from "react";
import { fetchHotkeyConfig, setHotkeyConfig, monitorOff, applyWallpaperCss } from "../../services/uxtuAdapter";
import { useToast } from "../ui/Toast";

const THEMES = [
  { id: "light", label: "浅色" },
  { id: "dark", label: "深色" },
  { id: "auto", label: "跟随系统" },
];

const COLORS = [
  { id: "#4cc2ff", label: "天蓝" },
  { id: "#7c5cff", label: "紫罗兰" },
  { id: "#5dd68a", label: "翠绿" },
  { id: "#ffb454", label: "琥珀" },
  { id: "#ff6b6b", label: "珊瑚红" },
  { id: "#ff8ac2", label: "粉" },
  { id: "#4dd0e1", label: "青" },
];

const BG_INTERVALS = [
  { id: "30m", label: "每 30 分钟" },
  { id: "1h", label: "每 1 小时" },
  { id: "3h", label: "每 3 小时" },
  { id: "6h", label: "每 6 小时" },
  { id: "12h", label: "每 12 小时" },
  { id: "1d", label: "每天" },
];

const ICONS = {
  appearance: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>,
  palette: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><circle cx="13.5" cy="6.5" r="1.5"/><circle cx="17.5" cy="10.5" r="1.5"/><circle cx="8.5" cy="7.5" r="1.5"/><circle cx="6.5" cy="12.5" r="1.5"/><path d="M12 2a10 10 0 1 0 0 20 2.5 2.5 0 0 0 2.5-2.5c0-.6-.2-1.1-.6-1.5-.4-.4-.6-.9-.6-1.5A2.5 2.5 0 0 1 15.8 14H18a4 4 0 0 0 4-4c0-4.4-4.5-8-10-8Z"/></svg>,
  autostart: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><path d="M12 3v9"/><path d="M5.6 6.6a8 8 0 1 0 12.8 0"/><path d="M12 12l4-2"/></svg>,
  background: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><rect x="3" y="4" width="18" height="14" rx="2"/><circle cx="8.5" cy="9" r="1.5"/><path d="m4 17 5-5 4 4 3-3 4 4"/></svg>,
  hotkey: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/></svg>,
  backup: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><path d="M21 8v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8"/><path d="M1 3h22v5H1z"/><path d="M10 12h4"/></svg>,
  about: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><path d="M13 2 3 14h7l-1 8 10-12h-7l1-8Z"/></svg>,
  upload: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 16V4M6 10l6-6 6 6"/><path d="M4 20h16"/></svg>,
  delete: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/></svg>,
  export: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 4v12M6 10l6 6 6-6"/><path d="M4 20h16"/></svg>,
  import: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 20V8M6 14l6-6 6 6"/><path d="M4 4h16"/></svg>,
  update: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/></svg>,
  log: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M9 15h6M9 11h2"/></svg>,
  close: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M18 6 6 18M6 6l12 12"/></svg>,
};

export default function SettingsPanel({ theme, setTheme }) {
  const toast = useToast();
  const showBackground = true;
  const showHotkey = true;

  const [bg, setBg] = useState({ enabled: false, opacity: 60, blur: 45, maskColor: "black", hasImage: false, url: null });

  const [selectedCats, setSelectedCats] = useState(() =>
    Object.fromEntries(["config", "games", "hotkeys", "appearance", "autostart", "background"].map(c => [c, c !== "autostart" && c !== "background"]))
  );
  const importInputRef = useRef(null);

  const [accent, setAccent] = useState("#4cc2ff");

  const [autoStart, setAutoStart] = useState(false);
  const [autoStartMin, setAutoStartMin] = useState(false);

  const [bgSource, setBgSource] = useState("local");
  const [bgInterval, setBgInterval] = useState("1h");
  const [bgApiUrl, setBgApiUrl] = useState("");
  const [bgRotating, setBgRotating] = useState(false);

  const fileInputRef = useRef(null);
  const colorInputRef = useRef(null);

  const bgEnabled = bg.enabled;
  const bgOpacity = bg.opacity;
  const bgBlur = bg.blur;
  const bgMask = bg.maskColor;
  const bgHasImage = bg.hasImage;

  const syncWallpaperCss = (next) => {
    applyWallpaperCss(next);
  };

  useEffect(() => {
    fetch("/api/ui-state")
      .then(r => r.json())
      .then(d => {
        if (d.accentColor) {
          setAccent(d.accentColor);
          document.documentElement.style.setProperty("--seed-primary", d.accentColor);
        }
      })
      .catch(() => {});
    fetch("/api/background-opts")
      .then(r => r.json())
      .then(d => {
        if (d) {
          setBg(prev => {
            const next = { ...prev, ...d, enabled: !!d.enabled, url: d.hasImage ? "/api/background" : prev.url };
            syncWallpaperCss(next);
            return next;
          });
        }
      })
      .catch(() => {});
  }, []);

  useEffect(() => {
    fetch("/api/auto-start")
      .then(r => r.json())
      .then(d => setAutoStart(!!d.enabled))
      .catch(() => {});
    fetch("/api/auto-start-opts")
      .then(r => r.json())
      .then(d => setAutoStartMin(!!d.minimized))
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (!showBackground) return;
    fetch("/api/background-opts")
      .then(r => r.json())
      .then(d => {
        if (d.source) setBgSource(d.source);
        if (d.interval) setBgInterval(d.interval);
        if (d.apiUrl != null) setBgApiUrl(d.apiUrl);
      })
      .catch(() => {});
  }, [showBackground]);

  const updateBg = (patch) => {
    setBg(prev => typeof patch === "function" ? patch(prev) : { ...prev, ...patch });
  };

  const handleExport = async () => {
    const cats = Object.entries(selectedCats).filter(([, v]) => v).map(([k]) => k);
    if (cats.length === 0) { toast?.("请至少选择一个分类", "info"); return; }
    try {
      const res = await fetch("/api/backup/export", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ categories: cats }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      const cd = res.headers.get("content-disposition") || "";
      const m = cd.match(/filename="([^"]+)"/);
      a.download = m?.[1] || `douzhanzhe-backup-${Date.now()}.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      toast?.("备份已导出", "success");
    } catch (e) {
      toast?.("导出失败: " + e.message, "error");
    }
  };

  const handleImportFile = async (e) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      const data = JSON.parse(text);
      const cats = Object.entries(selectedCats).filter(([, v]) => v).map(([k]) => k);
      if (cats.length === 0) { toast?.("请至少选择一个分类", "info"); return; }
      const res = await fetch("/api/backup/import", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ data, categories: cats }),
      });
      const d = await res.json();
      if (d.ok) {
        toast?.(`已恢复 ${d.restored} 项配置，刷新后生效`, "success");
      } else {
        toast?.(d.error || "导入失败", "error");
      }
    } catch (e) {
      toast?.("导入失败: " + e.message, "error");
    } finally {
      e.target.value = "";
    }
  };

  const saveUiState = async (patch) => {
    try {
      await fetch("/api/ui-state", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch),
      });
    } catch { /* ignore */ }
  };

  const setThemeMode = (mode) => {
    setTheme(mode);
  };

  const setAccentColor = (color) => {
    setAccent(color);
    saveUiState({ theme, accentColor: color });
    document.documentElement.style.setProperty("--seed-primary", color);
  };

  const toggleAutoStart = async (v) => {
    setAutoStart(v);
    try {
      const r = await fetch("/api/auto-start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: v }),
      });
      const d = await r.json();
      if (!d.ok) { setAutoStart(!v); toast?.(d.error || "设置失败", "error"); }
    } catch {
      setAutoStart(!v);
      toast?.("请求失败", "error");
    }
  };

  const toggleAutoStartMin = async (v) => {
    setAutoStartMin(v);
    try {
      const r = await fetch("/api/auto-start-opts", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ minimized: v }),
      });
      const d = await r.json();
      if (!d.ok) { setAutoStartMin(!v); toast?.(d.error || "设置失败", "error"); }
    } catch {
      setAutoStartMin(!v);
      toast?.("请求失败", "error");
    }
  };

  const saveBgOpts = async (patch) => {
    try {
      await fetch("/api/background-opts", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch),
      });
    } catch { /* ignore */ }
  };

  const handleBgToggle = async (v) => {
    updateBg((prev) => {
      const next = { ...prev, enabled: v };
      syncWallpaperCss(next);
      return next;
    });
    await saveBgOpts({ enabled: v });
  };

  const handleBgSource = async (v) => {
    setBgSource(v);
    await saveBgOpts({ source: v });
  };

  const handleBgInterval = async (v) => {
    setBgInterval(v);
    await saveBgOpts({ interval: v });
  };

  const handleBgApiUrlChange = (v) => {
    setBgApiUrl(v);
  };

  const handleBgApiUrlCommit = async () => {
    await saveBgOpts({ apiUrl: bgApiUrl.trim() });
  };

  const handleBgRotateNow = async () => {
    const url = bgApiUrl.trim();
    if (!url) { toast?.("请先填写 API 地址", "error"); return; }
    setBgRotating(true);
    try {
      // 先提交当前输入地址，确保后端使用的是最新值
      await saveBgOpts({ apiUrl: url });
      const res = await fetch("/api/background-rotate-now", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url }),
      });
      const d = await res.json();
      if (d.ok) {
        const nextUrl = "/api/background?t=" + Date.now();
        updateBg(prev => ({ ...prev, enabled: true, hasImage: true, url: nextUrl }));
        syncWallpaperCss({ ...bg, enabled: true, hasImage: true, url: nextUrl });
        saveBgOpts({ enabled: true, hasImage: true });
        toast?.("已切换壁纸", "success");
      } else {
        toast?.(d.error || "切换失败", "error");
      }
    } catch (e) {
      toast?.("请求失败: " + e.message, "error");
    } finally {
      setBgRotating(false);
    }
  };

  const handleBgOpacity = async (v) => {
    updateBg((prev) => {
      const next = { ...prev, opacity: v };
      syncWallpaperCss(next);
      return next;
    });
    await saveBgOpts({ opacity: v });
  };

  const handleBgBlur = async (v) => {
    updateBg((prev) => {
      const next = { ...prev, blur: v };
      syncWallpaperCss(next);
      return next;
    });
    await saveBgOpts({ blur: v });
  };

  const handleBgMask = async (v) => {
    updateBg((prev) => {
      const next = { ...prev, maskColor: v };
      syncWallpaperCss(next);
      return next;
    });
    await saveBgOpts({ maskColor: v });
  };

  const handleFileSelect = (e) => {
    const file = e.target.files?.[0];
    if (!file) return;
    if (file.size > 8 * 1024 * 1024) { toast?.("图片不能超过 8MB", "error"); return; }

    const previewUrl = URL.createObjectURL(file);
    updateBg({ hasImage: true, url: previewUrl, enabled: true });
    syncWallpaperCss({ ...bg, hasImage: true, url: previewUrl, enabled: true });

    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = reader.result;
      fetch("/api/background", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ image: dataUrl }),
      })
        .then(r => r.json())
        .then(d => {
          URL.revokeObjectURL(previewUrl);
          if (d.ok) {
            updateBg(prev => {
              const next = { ...prev, hasImage: true, enabled: true, url: "/api/background?t=" + Date.now() };
              syncWallpaperCss(next);
              return next;
            });
            saveBgOpts({ hasImage: true, enabled: true });
            toast?.("背景图片已设置", "success");
          } else {
            toast?.(d.error || "上传失败", "error");
          }
        })
        .catch(() => {
          URL.revokeObjectURL(previewUrl);
          toast?.("上传失败", "error");
        });
    };
    reader.readAsDataURL(file);
    e.target.value = "";
  };

  const handleBgDelete = async () => {
    try {
      const r = await fetch("/api/background", { method: "DELETE" });
      const d = await r.json();
      if (d.ok) {
        updateBg(prev => {
          const next = { ...prev, enabled: false, hasImage: false, url: null };
          syncWallpaperCss(next);
          return next;
        });
        toast?.("背景图片已移除", "success");
      }
    } catch {
      toast?.("操作失败", "error");
    }
  };

  return (
    <div className="set-wrap">
      {/* 外观 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".02s" }}>
        <div className="set-head">
          <span className="ic">{ICONS.appearance}</span>
          <span><b>外观</b><small>主题模式将同步影响所有 Fluent 控件与 OSD 提示</small></span>
        </div>
        <div className="set-body">
          <div className="theme-cards">
            {THEMES.map(t => (
              <button key={t.id} className={`theme-card ${t.id} ${theme === t.id ? "active" : ""}`} onClick={() => setThemeMode(t.id)}>
                <span className="prev">
                  <span className="bar"></span>
                  <span style={{ flex: 1 }}>
                    <span className="ln p" style={{ width: "60%" }}></span>
                    <span className="ln" style={{ width: "88%" }}></span>
                  </span>
                </span>
                <span className="cap">
                  {t.label}
                  <svg className="tick" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4"><path d="M20 6 9 17l-5-5"/></svg>
                </span>
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* 配色 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".05s" }}>
        <div className="set-head">
          <span className="ic">{ICONS.palette}</span>
          <span><b>配色</b><small>只需选择主色，辅助强调色将按色彩规则自动派生</small></span>
        </div>
        <div className="set-body">
          <div className="swatches">
            {COLORS.map(c => (
              <button
                key={c.id}
                className={`swatch ${accent === c.id ? "active" : ""}`}
                style={{ background: c.id, color: c.id }}
                aria-label={c.label}
                onClick={() => setAccentColor(c.id)}
              />
            ))}
            <input
              ref={colorInputRef}
              type="color"
              className="hidden"
              value={/^#[0-9a-fA-F]{6}$/.test(accent) ? accent : "#4cc2ff"}
              onChange={(e) => setAccentColor(e.target.value)}
              aria-label="自定义强调色"
            />
            <button className="swatch swatch-custom" aria-label="自定义颜色" onClick={() => colorInputRef.current?.click()}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 5v14M5 12h14"/></svg>
            </button>
          </div>
        </div>
      </div>

      {/* 开机自启 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".08s" }}>
        <div className="set-head">
          <span className="ic">{ICONS.autostart}</span>
          <span><b>开机自启</b><small>登录 Windows 时自动运行斗战者控制台</small></span>
        </div>
        <div className="set-body">
          <div className="set-row">
            <span className="rk"><b>开机时自动启动</b><small>注册为系统启动项，随 Windows 登录运行</small></span>
            <span className="rctrl">
              <label className="switch">
                <input type="checkbox" checked={autoStart} onChange={(e) => toggleAutoStart(e.target.checked)} aria-label="开机时自动启动" />
                <span className="track"></span>
              </label>
            </span>
          </div>
          <div className="set-row">
            <span className="rk"><b>最小化启动</b><small>启动时收起主窗口至托盘，仅在后台守护硬件</small></span>
            <span className="rctrl">
              <label className="switch">
                <input type="checkbox" checked={autoStartMin} onChange={(e) => toggleAutoStartMin(e.target.checked)} aria-label="最小化启动" />
                <span className="track"></span>
              </label>
            </span>
          </div>
        </div>
      </div>

      {/* 自定义背景 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".11s" }}>
        <div className="set-head">
          <span className="ic">{ICONS.background}</span>
          <span><b>自定义背景</b><small>壁纸经高斯模糊后作为 Mica 材质底层纹理</small></span>
        </div>
        <div className="set-body">
          <div className="set-row">
            <span className="rk"><b>启用自定义背景</b><small>关闭时退化为默认 Mica 纯色材质</small></span>
            <span className="rctrl">
              <label className="switch">
                <input type="checkbox" checked={bgEnabled} onChange={(e) => handleBgToggle(e.target.checked)} aria-label="启用自定义背景" />
                <span className="track"></span>
              </label>
            </span>
          </div>
          <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
            <span className="rk"><b>图片来源</b><small>本地上传固定图片，或接入网络 API 自动轮换</small></span>
            <span className="rctrl">
              <span className="segmented">
                <button className={bgSource === "local" ? "active" : ""} onClick={() => handleBgSource("local")}>本地上传</button>
                <button className={bgSource === "network" ? "active" : ""} onClick={() => handleBgSource("network")}>网络轮换</button>
              </span>
            </span>
          </div>
          {bgSource === "network" && (
            <>
              <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
                <span className="rk"><b>API 地址</b><small>支持 JSON 列表/对象，或接口直接返回 / 302 跳转到图片</small></span>
                <span className="rctrl">
                  <span className="api-row">
                    <input type="text" className="api-input" value={bgApiUrl} placeholder="https://example.com/wallpapers.json"
                      onChange={(e) => handleBgApiUrlChange(e.target.value)}
                      onBlur={() => handleBgApiUrlCommit()} />
                    <button className="btn primary rotate-now-btn" onClick={handleBgRotateNow} disabled={bgRotating}
                      title="从接口地址立即获取一张壁纸并生效">
                      {ICONS.update}{bgRotating ? "切换中…" : "立刻切换"}
                    </button>
                  </span>
                </span>
              </div>
              <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
                <span className="rk"><b>轮换间隔</b><small>网络轮换模式下每张壁纸的停留时长</small></span>
                <span className="rctrl">
                  <select className="sel" value={bgInterval} onChange={(e) => handleBgInterval(e.target.value)}>
                    {BG_INTERVALS.map(o => <option key={o.id} value={o.id}>{o.label}</option>)}
                  </select>
                </span>
              </div>
            </>
          )}
          <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
            <span className="rk"><b>透明度</b><small>壁纸层叠加在控件之下的显现程度</small></span>
            <span className="rctrl"><input type="range" className="slider" min="0" max="100" value={bgOpacity} onChange={(e) => handleBgOpacity(Number(e.target.value))} /><span className="pv">{bgOpacity}%</span></span>
          </div>
          <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
            <span className="rk"><b>模糊度</b><small>0 时退化为普通壁纸模式，调高则强化 Mica 质感</small></span>
            <span className="rctrl"><input type="range" className="slider" min="0" max="100" value={bgBlur} onChange={(e) => handleBgBlur(Number(e.target.value))} /><span className="pv">{bgBlur}%</span></span>
          </div>
          <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
            <span className="rk"><b>遮罩色</b><small>跟随系统主题：深色主题用黑色遮罩，浅色主题用白色遮罩</small></span>
            <span className="rctrl">
              <span className="segmented">
                <button className={bgMask === "black" ? "active" : ""} onClick={() => handleBgMask("black")}>黑色</button>
                <button className={bgMask === "white" ? "active" : ""} onClick={() => handleBgMask("white")}>白色</button>
              </span>
            </span>
          </div>
          <div className={`set-row bg-dep ${bgEnabled ? "" : "disabled"}`}>
            <span className="rk"><b>图片管理</b><small>{bgHasImage ? "当前已上传图片" : "尚未上传图片"}</small></span>
            <span className="rctrl">
              <input ref={fileInputRef} type="file" accept="image/png,image/jpeg,image/webp" onChange={handleFileSelect} className="hidden" />
              <button className="btn" onClick={() => fileInputRef.current?.click()}>{ICONS.upload}上传</button>
              {bgHasImage && <button className="btn ghost" onClick={handleBgDelete}>{ICONS.delete}删除</button>}
            </span>
          </div>
        </div>
      </div>

      {/* 快捷键 */}
      {showHotkey && (
        <div className="card set-card reveal enter" style={{ animationDelay: ".14s" }}>
          <div className="set-head">
            <span className="ic">{ICONS.hotkey}</span>
            <span><b>快捷键</b><small>全局热键即使在桌面或其他应用中也响应</small></span>
          </div>
          <div className="set-body">
            <HotkeySection toast={toast} />
          </div>
        </div>
      )}

      {/* 配置备份 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".17s" }}>
        <div className="set-head">
          <span className="ic">{ICONS.backup}</span>
          <span><b>配置备份</b><small>按分类导出 / 导入，导出写入硬件签名，签名不一致整份拒绝</small></span>
        </div>
        <div className="set-body">
          <div className="bak-grid">
           {[
              { id: "config", label: "配置参数", desc: "各预设的性能 / 功耗曲线" },
              { id: "games", label: "游戏规则", desc: "游戏绑定与自动切换规则" },
              { id: "hotkeys", label: "快捷键", desc: "全局热键绑定" },
              { id: "appearance", label: "外观与配色", desc: "主题模式与强调色" },
              { id: "autostart", label: "开机自启", desc: "自启与最小化启动" },
              { id: "background", label: "自定义背景", desc: "壁纸来源与模糊参数" },
            ].map(item => (
              <label key={item.id} className="bak-item">
                <input
                  type="checkbox"
                  checked={selectedCats[item.id]}
                  onChange={(e) => setSelectedCats(prev => ({ ...prev, [item.id]: e.target.checked }))}
                />
                <span className="bt">{item.label}<small>{item.desc}</small></span>
              </label>
            ))}
          </div>
          <div className="bak-actions">
            <input ref={importInputRef} type="file" accept="application/json" onChange={handleImportFile} className="hidden" />
            <button className="btn primary" onClick={handleExport}>{ICONS.export}导出备份</button>
            <button className="btn" onClick={() => importInputRef.current?.click()}>{ICONS.import}导入恢复</button>
          </div>
        </div>
      </div>

      {/* 关于 */}
      <div className="card set-card reveal enter" style={{ animationDelay: ".2s" }}>
        <div className="about">
          <span className="logo">{ICONS.about}</span>
          <span className="meta">
            <b>斗战者控制台</b><span className="ver">{`v${__APP_VERSION__}${__APP_BUILD__ ? ` · Build ${__APP_BUILD__}` : ''}`}</span>
            <small>DOUZHANZHE Control Center · 构建 {new Date().toISOString().slice(0, 10).replace(/-/g, ".")}<br />© 2025-2026 斗战者科技 · 保留所有权利</small>
          </span>
          <span className="acts">
            <button className="btn" onClick={() => window.dispatchEvent(new Event("check-update-manual"))}>{ICONS.update}检查更新</button>
            <button className="btn ghost" onClick={async () => {
              try {
                const res = await fetch("/api/logs/export");
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const blob = await res.blob();
                const url = URL.createObjectURL(blob);
                const a = document.createElement("a");
                a.href = url;
                const cd = res.headers.get("content-disposition") || "";
                const m = cd.match(/filename="([^"]+)"/);
                a.download = m?.[1] || `douzhanhe-log-${Date.now()}.log`;
                document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
              } catch (e) {
                toast?.("导出日志失败: " + e.message, "error");
              }
            }}>{ICONS.log}导出日志</button>
          </span>
        </div>
      </div>
    </div>
  );
}

const HOTKEY_LABELS = {
  "monitor-off": "关闭屏幕",
  "mode-office": "均衡模式",
  "mode-beast": "野兽模式",
  "mode-silent": "安静模式",
  "mode-gaming": "斗战模式",
};

const CODE_LABELS = (() => {
  const map = {
    Space: "Space", Enter: "Enter", Tab: "Tab", Backspace: "Backspace",
    Delete: "Delete", Insert: "Insert", Home: "Home", End: "End",
    PageUp: "PageUp", PageDown: "PageDown", CapsLock: "CapsLock",
    NumpadEnter: "Num Enter",
    NumpadMultiply: "Num *", NumpadAdd: "Num +", NumpadSubtract: "Num -",
    NumpadDecimal: "Num .", NumpadDivide: "Num /",
    ArrowUp: "↑", ArrowDown: "↓", ArrowLeft: "←", ArrowRight: "→",
    Backquote: "`", Minus: "-", Equal: "=", BracketLeft: "[", BracketRight: "]",
    Backslash: "\\", Semicolon: ";", Quote: "'", Comma: ",", Period: ".",
    Slash: "/", IntlBackslash: "\\",
  };
  for (let i = 0; i <= 9; i++) {
    map["Digit" + i] = String(i);
    map["Numpad" + i] = "Num " + i;
  }
  for (let c = 65; c <= 90; c++) {
    const ch = String.fromCharCode(c);
    map["Key" + ch] = ch;
  }
  return map;
})();

function formatKey(code) {
  if (!code) return "";
  if (CODE_LABELS[code]) return CODE_LABELS[code];
  if (/^F\d{1,2}$/.test(code)) return code;
  if (code.length === 1) return code.toUpperCase();
  return code;
}

function formatHotkey(mods, k) {
  const names = { ctrl: "Ctrl", control: "Ctrl", alt: "Alt", shift: "Shift", win: "Win" };
  const parts = (mods || "").split(",").map(m => names[m.trim().toLowerCase()] || m.trim()).filter(Boolean);
  parts.push(formatKey(k));
  return parts.join(" + ");
}

function HotkeySection({ toast }) {
  const [hotkeys, setHotkeys] = useState(null);
  const [globalEnabled, setGlobalEnabled] = useState(true);
  const [recordingId, setRecordingId] = useState(null);
  const [countdown, setCountdown] = useState(null);

  const loadConfig = useCallback(async () => {
    try {
      const cfg = await fetchHotkeyConfig();
      setHotkeys(cfg);
      const values = Object.values(cfg);
      setGlobalEnabled(values.length > 0 ? values.some(c => c.enabled !== false) : true);
    } catch { /* ignore */ }
  }, []);

  useEffect(() => { loadConfig(); }, [loadConfig]);

  const handleGlobalToggle = async (v) => {
    setGlobalEnabled(v);
    if (!hotkeys) return;
    const entries = Object.entries(hotkeys);
    const updated = {};
    for (const [id, cfg] of entries) {
      updated[id] = { ...cfg, enabled: v };
      try { await setHotkeyConfig(id, { enabled: v, modifiers: cfg.modifiers, key: cfg.key }); } catch { /* ignore */ }
    }
    setHotkeys(updated);
    toast?.(v ? "快捷键已开启" : "快捷键已关闭", "success");
  };

  const handleExecute = async () => {
    if (countdown !== null) return;
    setCountdown(3);
    for (let i = 2; i >= 0; i--) {
      await new Promise(r => setTimeout(r, 1000));
      setCountdown(i);
    }
    await new Promise(r => setTimeout(r, 200));
    setCountdown(null);
    try { await monitorOff(); } catch { toast?.("关屏失败", "error"); }
  };

  const handleRowUpdate = useCallback(() => {
    setTimeout(() => loadConfig(), 500);
  }, [loadConfig]);

  if (!hotkeys) return null;

  return (
    <div className="shortcut-list">
      {Object.entries(hotkeys).map(([id, cfg]) => (
        <HotkeyRow key={id} id={id} config={cfg}
          globalEnabled={globalEnabled} toast={toast}
          recordingId={recordingId} setRecordingId={setRecordingId}
          onToggle={handleRowUpdate}
          conflict={!!cfg.conflict}
          onExecute={id === "monitor-off" ? handleExecute : undefined}
          executeCountdown={id === "monitor-off" ? countdown : undefined} />
      ))}
      <div className="set-row">
        <span className="rk"><b>启用全局快捷键</b><small>关闭后下列所有组合键将失效</small></span>
        <span className="rctrl">
          <label className="switch">
            <input type="checkbox" checked={globalEnabled} onChange={(e) => handleGlobalToggle(e.target.checked)} aria-label="启用全局快捷键" />
            <span className="track"></span>
          </label>
        </span>
      </div>
    </div>
  );
}

function HotkeyRow({ id, config, globalEnabled, toast, recordingId, setRecordingId, onToggle, onExecute, executeCountdown, conflict }) {
  const [modifiers, setModifiers] = useState(config.modifiers);
  const [key, setKey] = useState(config.key);
  const inputRef = useRef(null);
  const isRecording = recordingId === id;

  useEffect(() => {
    setModifiers(config.modifiers);
    setKey(config.key);
  }, [config.modifiers, config.key]);

  const handleRecord = () => {
    setRecordingId(id);
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const handleKeyDown = async (e) => {
    if (!isRecording) return;
    e.preventDefault();
    e.stopPropagation();
    const mods = [];
    if (e.ctrlKey) mods.push("ctrl");
    if (e.altKey) mods.push("alt");
    if (e.shiftKey) mods.push("shift");
    if (e.metaKey) mods.push("win");
    const code = e.code;
    if (["ControlLeft", "ControlRight", "AltLeft", "AltRight", "ShiftLeft", "ShiftRight", "MetaLeft", "MetaRight"].includes(code)) return;
    if (code === "Escape") { setRecordingId(null); return; }
    const keyName = code;
    if (!keyName) return;
    if (mods.length === 0) { toast?.("请至少按下一个修饰键 (Ctrl/Alt/Shift)", "error"); return; }
    const newMods = mods.join(",");
    setModifiers(newMods);
    setKey(keyName);
    setRecordingId(null);
    try {
      await setHotkeyConfig(id, { enabled: globalEnabled, modifiers: newMods, key: keyName });
      if (onToggle) onToggle(globalEnabled);
      toast?.(`快捷键已更新为 ${formatHotkey(newMods, keyName)}`, "success");
    } catch { toast?.("保存失败", "error"); }
  };

  return (
    <div className={"shortcut-row" + (globalEnabled ? "" : " disabled")}>
      <span className="rk"><b>{HOTKEY_LABELS[id] || id}</b><small>{id === "monitor-off" ? "一键关闭显示器" : "切换到对应性能配置"}</small></span>
      <span className="keys">{formatHotkey(modifiers, key)}</span>
      {conflict && <span className="hk-conflict" title="与其他快捷键冲突，可能无法注册">冲突</span>}
      <button className="rec-btn" onClick={handleRecord} disabled={!globalEnabled}>{ICONS.edit}录制</button>
      {id === "monitor-off" && (
        <button className="rec-btn" onClick={onExecute} disabled={executeCountdown != null || !globalEnabled}>
          {executeCountdown != null ? `${executeCountdown}s` : "执行"}
        </button>
      )}
      {isRecording && (
        <input ref={inputRef} onKeyDown={handleKeyDown} onBlur={() => setRecordingId(null)}
          className="hk-input"
          placeholder="请按下组合键... (Esc 取消)" readOnly autoFocus />
      )}
    </div>
  );
}
