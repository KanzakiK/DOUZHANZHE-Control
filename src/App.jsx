import ThemeSwitcher from "./components/ThemeSwitcher";
import SettingsPanel from "./components/panels/SettingsPanel";
import SystemInfoPanel from "./components/panels/SystemInfoPanel";
import FanCurvePanel from "./components/panels/FanCurvePanel";
import GameProfilesPanel from "./components/panels/GameProfilesPanel";
import AutoSwitchStatus from "./components/ui/AutoSwitchStatus";
import Card from "./components/ui/Card";
import SortableDashboard from "./components/SortableDashboard";
import UpdateDialog from "./components/ui/UpdateDialog";
import SwitchingOverlay from "./components/ui/SwitchingOverlay";
import { useToast } from "./components/ui/Toast";
import { useControlState } from "./hooks/useControlState";
import { fetchFanCurveStatus, stopFanCurve, resetToFactoryDefaults } from "./services/uxtuAdapter";
import { useCallback, useState, useEffect } from "react";

const NAV_ITEMS = ["主页", "散热曲线", "游戏", "系统", "设置"];
const NAV_TABS = { "主页": "dashboard", "散热曲线": "fancurve", "游戏": "games", "系统": "system", "设置": "settings" };
const MODE_ITEMS = [
  { id: "silent", label: "安静模式" },
  { id: "office", label: "均衡模式" },
  { id: "beast", label: "野兽模式" },
  { id: "gaming", label: "斗战模式" },
];

export default function App() {
  const toast = useToast();
  const { theme, setTheme, telemetry, uxtuParams, setUxtuParams, settings, setSettings, history, overrides, saveOverride, resetParams, switching } =
    useControlState();
  const [activeTab, setActiveTab] = useState(() => {
    try { return localStorage.getItem("douzhanzhe_active_tab") || "dashboard"; }
    catch { return "dashboard"; }
  });
  const [editMode, setEditMode] = useState(false);
  // 轮询自定义曲线状态 (每 3s)
  const [fanCurveActive, setFanCurveActive] = useState(false);
  useEffect(() => {
    let cancelled = false;
    const poll = () => {
      fetchFanCurveStatus()
        .then(s => { if (!cancelled && s.ok) setFanCurveActive(s.active); })
        .catch(() => {});
    };
    poll();
    const timer = setInterval(poll, 3000);
    return () => { cancelled = true; clearInterval(timer); };
  }, []);
  // 持久化当前标签页
  useEffect(() => {
    try { localStorage.setItem("douzhanzhe_active_tab", activeTab); } catch {}
  }, [activeTab]);
  // 同步主题类到 body，使 body { background: var(--bg) } 等 CSS 变量生效
  useEffect(() => { document.body.className = theme; }, [theme]);

  // ── 自定义背景 ──
  const [bg, setBg] = useState(() => {
    try {
      const raw = localStorage.getItem("dz_bg");
      if (raw) { const p = JSON.parse(raw); return { ...p, url: p.hasImage ? "/api/background?" + Date.now() : null }; }
    } catch {}
    return { enabled: false, opacity: 50, maskColor: "black", hasImage: false, url: null };
  });
  useEffect(() => {
    // 仅在启动时验证后端图片文件是否存在，不覆盖本地开关/透明度/遮罩设置
    fetch("/api/background", { method: "HEAD" }).then(r => {
      if (r.ok) {
        // 图片存在：确保本地 hasImage 为 true 并刷新 URL
        setBg(prev => {
          if (prev.hasImage && prev.url) return prev;
          const next = { ...prev, hasImage: true, url: "/api/background?" + Date.now() };
          localStorage.setItem("dz_bg", JSON.stringify({ enabled: next.enabled, opacity: next.opacity, maskColor: next.maskColor, hasImage: true }));
          return next;
        });
      } else if (r.status === 404) {
        // 服务端明确返回 404：图片已被删除，清除本地 hasImage
        setBg(prev => {
          if (!prev.hasImage) return prev;
          const next = { ...prev, hasImage: false, url: null };
          localStorage.setItem("dz_bg", JSON.stringify({ enabled: next.enabled, opacity: next.opacity, maskColor: next.maskColor, hasImage: false }));
          return next;
        });
      }
      // 其他错误 (5xx 等)：不修改状态，信任 localStorage 缓存
    }).catch(() => {
      // 网络错误 / 服务端不可达：保留 localStorage 中的缓存状态
    });
  }, []);
  const updateBg = useCallback((patch) => {
    setBg(prev => {
      const next = { ...prev, ...patch };
      const { url, ...cfg } = next;
      localStorage.setItem("dz_bg", JSON.stringify(cfg));
      if (patch.hasImage !== undefined && patch.url === undefined) next.url = patch.hasImage ? "/api/background?" + Date.now() : null;
      return next;
    });
  }, []);

  const bgActive = bg.enabled && bg.hasImage && bg.url;
  const maskAlpha = bgActive ? (1 - bg.opacity / 100).toFixed(2) : 0;
  const maskBg = bgActive ? (bg.maskColor === "white" ? `rgba(255,255,255,${maskAlpha})` : `rgba(0,0,0,${maskAlpha})`) : "transparent";

  return (
    <div className={`${theme} min-h-screen p-3 md:p-4${bgActive ? " dz-bg-active" : ""}`} style={bgActive ? { background: "transparent" } : undefined}>
      {bgActive && (
        <>
          <div style={{ position: "fixed", inset: 0, zIndex: -2, backgroundImage: `url(${bg.url})`, backgroundSize: "cover", backgroundPosition: "center", backgroundRepeat: "no-repeat" }} />
          <div style={{ position: "fixed", inset: 0, zIndex: -1, background: maskBg }} />
        </>
      )}
      <div className="max-w-[1750px] mx-auto grid grid-cols-1 md:grid-cols-[220px_1fr] gap-4">
        <aside className="rounded-2xl p-3 flex flex-col gap-4 md:sticky md:top-4 md:self-start md:max-h-[calc(100vh-2rem)] console-panel" style={{ border: "1px solid var(--border)" }}>
          <div className="rounded-xl p-3 sidebar-brand" style={{ background: "var(--card-2)" }}>
            <p className="text-xs uppercase tracking-widest" style={{ color: "var(--muted)" }}>DOUZHANZHE</p>
            <p className="text-sm font-semibold mt-1">Douzhanzhe Console</p>
          </div>
          <nav className="space-y-2">
            {NAV_ITEMS.map((item) => (
              <button key={item} onClick={() => setActiveTab(NAV_TABS[item])}
                className="w-full text-left text-sm rounded-lg px-3 py-2 transition"
                style={{ border: "1px solid var(--border)", background: activeTab === NAV_TABS[item] ? "var(--primary-2)" : "var(--card-2)", color: activeTab === NAV_TABS[item] ? "#ffffff" : "var(--text)" }}
              >{item}</button>
            ))}
          </nav>
          {activeTab === "dashboard" && (
            <button onClick={() => { setEditMode(!editMode); }}
              className="w-full flex items-center justify-center gap-2 text-sm rounded-lg px-3 py-2.5 transition"
              style={{ border: "1px solid var(--border)", background: editMode ? "var(--primary-2)" : "transparent", color: editMode ? "#fff" : "var(--text)" }}
            >{editMode ? "✓ 完成排序" : "⇅ 排序"}</button>
          )}
          <div className="mt-auto">
            <p className="text-xs mb-2" style={{ color: "var(--muted)" }}>皮肤切换</p>
            <ThemeSwitcher currentTheme={theme} onThemeChange={setTheme} />
          </div>
        </aside>
        <main className="grid gap-4">
          {activeTab === "dashboard" && (
          <Card title="模式选择" className="console-dock !p-3"
            action={<button onClick={async () => {
              const mode = settings.mode;
              try {
                // ① 先关闭自定义风扇曲线（防止 EC 被回写）
                if (fanCurveActive) {
                  await stopFanCurve();
                  setFanCurveActive(false);
                }
                // ② 发送模式恢复命令
                await resetToFactoryDefaults(mode);
                resetParams(mode);  // 同步清空 overrides + UI 回到模式默认
                toast?.("已恢复官方默认", "success");
              } catch (err) {
                toast?.(`恢复失败: ${err.message}`, "error");
              }
            }}
              className="text-xs px-2 py-1 rounded-lg"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >恢复默认</button>}
          >
            <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
              {MODE_ITEMS.map((mode) => (
                <button key={mode.id} onClick={() => {
                  setSettings(prev => ({ ...prev, mode: mode.id }));
                  toast?.(`已切换至${mode.label}`, "success");
                }}
                  className="text-xs md:text-sm rounded-lg px-2 py-3 transition-all"
                  style={{ border: "1px solid var(--border)", background: settings.mode === mode.id ? "var(--primary-2)" : "var(--card-2)", color: settings.mode === mode.id ? "#ffffff" : "var(--text)", boxShadow: settings.mode === mode.id ? "0 0 24px rgba(167, 139, 250, 0.35)" : "none" }}
                >{mode.label}</button>
              ))}
            </div>
          </Card>
          )}
          {activeTab === "dashboard" && <AutoSwitchStatus />}
          {activeTab === "dashboard" && (
          <SortableDashboard
            telemetry={telemetry}
            settings={settings} setSettings={setSettings}
            uxtuParams={uxtuParams} setUxtuParams={setUxtuParams}
            history={history}
            overrides={overrides} saveOverride={saveOverride}
            editMode={editMode}
            fanCurveActive={fanCurveActive}
            onFanCurveStop={() => setFanCurveActive(false)} />
          )}
          {activeTab === "system" && <SystemInfoPanel />}
          {activeTab === "settings" && (
            <SettingsPanel settings={settings} setSettings={setSettings}
              showSwitches={true} showKeyboard={true} showSummary={true} showAutoStart={true}
              showBackground={true} showHotkey={true} bg={bg} updateBg={updateBg} />
          )}
          {activeTab === "fancurve" && <FanCurvePanel telemetry={telemetry} overrides={overrides} settings={settings} />}
          {activeTab === "games" && <GameProfilesPanel />}
        </main>

        {/* 版本更新弹窗 */}
        <UpdateDialog />

        {/* 模式切换遮罩：切换期间拦截点击，防止 setter 写入错误模式文件 */}
        <SwitchingOverlay active={switching} />
      </div>
    </div>
  );
}
