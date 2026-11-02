import { useState, useEffect } from "react";
import { useControlState } from "./hooks/useControlState";
import Dashboard from "./pages/Dashboard";
import ControlPanel from "./pages/ControlPanel";
import FanControl from "./pages/FanControl";
import PlatformControl from "./pages/PlatformControl";
import Games from "./pages/Games";
import SysInfo from "./pages/SysInfo";
import Settings from "./pages/Settings";
import SwitchingOverlay from "./components/ui/SwitchingOverlay";
import UpdateDialog from "./components/ui/UpdateDialog";
import { applyWallpaperCss } from "./services/uxtuAdapter";

const PAGES = [
  { key: "dashboard", label: "仪表盘", icon: "M3 12l9-8 9 8M5 10v10h14V10" },
  { key: "control",  label: "控制面板", icon: "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0" },
  { key: "fan",      label: "风扇控制", icon: "M12 3a4 4 0 0 1 4 4c0 1.5-.8 2.7-2 3.4V13h3a3 3 0 0 1 3 3v1M8 7M6 20v-2a3 3 0 0 1 3-3h1" },
  { key: "platform", label: "平台控制", icon: "M3 4h18v14H3M7 21h10M9 8h6M9 12h4" },
  { key: "games",    label: "游戏", icon: "M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2M9 9l3 3-3 3M14 15h3" },
  { key: "sysinfo",  label: "系统信息", icon: "M4 4h16v16H4M9 4v16M4 9h5M13 8h4M13 12h4" },
  { key: "settings", label: "设置", icon: "M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2" },
];

export default function App() {
  const { theme, setTheme, backendOnline, platformInfo, platformInfoReady, switching } = useControlState();
  const [adminGuideOpen, setAdminGuideOpen] = useState(false);
  const [systemTheme, setSystemTheme] = useState(() =>
    typeof window !== "undefined" && window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light"
  );

  const [activePage, setActivePage] = useState(() => {
    try { return localStorage.getItem("dz_page") || "dashboard"; } catch { return "dashboard"; }
  });
  useEffect(() => {
    try { localStorage.setItem("dz_page", activePage); } catch { /* 隐私模式等场景忽略 */ }
  }, [activePage]);

  const isBellator = !platformInfoReady || platformInfo.oem === "Bellator";
  const visiblePages = isBellator ? PAGES : PAGES.filter(p => p.key !== "fan" && p.key !== "platform");
  const resolvedPage = !visiblePages.some(p => p.key === activePage) ? "dashboard" : activePage;
  useEffect(() => {
    if (resolvedPage !== activePage) setActivePage(resolvedPage);
  }, [resolvedPage, activePage]);

  useEffect(() => {
    const mq = window.matchMedia?.("(prefers-color-scheme: dark)");
    if (!mq) return;
    const update = (e) => setSystemTheme(e.matches ? "dark" : "light");
    setSystemTheme(mq.matches ? "dark" : "light");
    mq.addEventListener?.("change", update);
    return () => mq.removeEventListener?.("change", update);
  }, []);
  const effectiveTheme = theme === "auto" ? systemTheme : theme;
  useEffect(() => {
    const el = document.documentElement;
    el.setAttribute("data-theme-mode", theme);
    el.setAttribute("data-theme", effectiveTheme);
  }, [theme, effectiveTheme]);
  useEffect(() => {
    let disposed = false;
    (async () => {
      try {
        const res = await fetch("/api/background-opts");
        const d = await res.json();
        if (disposed || !d) return;
        applyWallpaperCss({ ...d, enabled: !!d.enabled, hasImage: !!d.hasImage, url: d.hasImage ? "/api/background" : null });
      } catch { /* backend offline */ }
    })();
    return () => { disposed = true; };
  }, []);
  useEffect(() => { const h = (e) => { document.querySelectorAll('.reveal').forEach((el) => { const r = el.getBoundingClientRect(); el.style.setProperty('--mx', ((e.clientX - r.left) / r.width * 100) + '%'); el.style.setProperty('--my', ((e.clientY - r.top) / r.height * 100) + '%'); }); }; window.addEventListener('mousemove', h); return () => window.removeEventListener('mousemove', h); }, []);


  function Svg({ d }) {
    return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><path d={d}/></svg>;
  }

  return (
    <div className="app">
      {/* Mica wallpaper layer */}
      <div className="wallpaper" aria-hidden="true"></div>
      {/* 窄边栏 */}
      <nav className="sidebar">
        {visiblePages.map(p => (
          <button key={p.key} className={`nav-item${resolvedPage === p.key ? ' active' : ''}`}
            onClick={() => setActivePage(p.key)}>
            <Svg d={p.icon} />
            <span>{p.label}</span>
          </button>
        ))}
        <div className="grow"></div>
        <button className="theme-toggle sidebar-btn" onClick={() => setTheme(effectiveTheme === "dark" ? "light" : "dark")} title="切换深浅主题">
          <svg className="moon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z"/></svg>
          <svg className="sun" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="12" cy="12" r="4.2"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>
        </button>
        <span className="env-pill sidebar-btn">
          <span className="dot" data-online={backendOnline === true}></span>
          {backendOnline === null ? "连接中" : backendOnline ? "已连接" : "离线"}
        </span>
      </nav>

      {/* 内容区 */}
      <main className="content">
        {platformInfo.isElevated === false && (
          <div className="admin-banner reveal enter">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="18" height="18"><path d="M12 2 4 5v6c0 5 3.5 8 8 10 4.5-2 8-5 8-10V5l-8-3Z"/><path d="M9 12l2 2 4-4"/></svg>
            <span className="ab-text"><b>未以管理员身份运行</b><small>PawnIO / EC / NVAPI 等硬件控制需要管理员权限</small></span>
            <button className="btn" onClick={() => setAdminGuideOpen(v => !v)}>提权引导</button>
            {adminGuideOpen && (
              <div className="admin-guide">
                <p>1. 完全退出斗战者控制台（含托盘图标）。</p>
                <p>2. 右键应用快捷方式，选择“以管理员身份运行”。</p>
                <p>3. 安装版可在开始菜单右键“斗战者控制台”，选择“更多 → 以管理员身份运行”。</p>
              </div>
            )}
          </div>
        )}
        {resolvedPage === "dashboard" && <Dashboard onNavigate={setActivePage} />}
        {resolvedPage === "control" && <ControlPanel />}
        {resolvedPage === "fan" && <FanControl />}
        {resolvedPage === "platform" && <PlatformControl />}
        {resolvedPage === "games" && <Games />}
        {resolvedPage === "sysinfo" && <SysInfo />}
        {resolvedPage === "settings" && <Settings theme={theme} setTheme={setTheme} />}
      </main>
      <SwitchingOverlay active={switching} />
      <UpdateDialog />
    </div>
  );
}
