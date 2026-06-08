import ThemeSwitcher from "./components/ThemeSwitcher";
import PerformancePanel from "./components/panels/PerformancePanel";
import SettingsPanel from "./components/panels/SettingsPanel";
import TelemetryPanel from "./components/panels/TelemetryPanel";
import SystemInfoPanel from "./components/panels/SystemInfoPanel";
import FanCurvePanel from "./components/panels/FanCurvePanel";
import Card from "./components/ui/Card";
import Gauge from "./components/ui/Gauge";
import SortableDashboard from "./components/SortableDashboard";
import { ToastProvider, useToast } from "./components/ui/Toast";
import { useControlState } from "./hooks/useControlState";
import { MODE_PRESETS, FULL_PARAMS, dispatchFullMode, fetchFanCurveStatus } from "./services/uxtuAdapter";
import { useCallback, useState, useEffect } from "react";

const NAV_ITEMS = ["主页", "散热曲线", "系统", "设置"];
const NAV_TABS = { "主页": "dashboard", "散热曲线": "fancurve", "系统": "system", "设置": "settings" };
const MODE_ITEMS = [
  { id: "silent", label: "安静模式" },
  { id: "office", label: "均衡模式" },
  { id: "beast", label: "野兽模式" },
  { id: "gaming", label: "斗战模式" },
];

export default function App() {
  const toast = useToast();
  const onCustomSaveResult = useCallback((ok) => {
    toast?.(ok ? "自定义参数已保存" : "自定义参数保存失败", ok ? "success" : "error");
  }, [toast]);
  const { theme, setTheme, telemetry, setTelemetry, uxtuParams, setUxtuParams, settings, setSettings, uxtuPayload, history } =
    useControlState(onCustomSaveResult);
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
    localStorage.setItem("douzhanzhe_active_tab", activeTab);
  }, [activeTab]);
  // 同步主题类到 body，使 body { background: var(--bg) } 等 CSS 变量生效
  useEffect(() => { document.body.className = theme; }, [theme]);

  return (
    <div className={`${theme} min-h-screen p-3 md:p-4`}>
      <div className="max-w-[1750px] mx-auto grid grid-cols-1 md:grid-cols-[220px_1fr] gap-4">
        <aside className="rounded-2xl p-3 flex flex-col gap-4 md:sticky md:top-4 md:self-start md:max-h-[calc(100vh-2rem)]" style={{ background: "var(--card)", border: "1px solid var(--border)" }}>
          <div className="rounded-xl p-3" style={{ background: "var(--card-2)" }}>
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
            action={<button onClick={() => {
              const mode = settings.mode;
              const cleanPreset = { ...FULL_PARAMS, ...(MODE_PRESETS[mode] || {}) };
              // 同步写入 localStorage，确保刷新前数据已落盘
              localStorage.setItem("douzhanzhe_params_" + mode, JSON.stringify(cleanPreset));
              setUxtuParams(cleanPreset);
              dispatchFullMode(mode, cleanPreset).then(() => {
                toast?.("已恢复预设值", "success");
              }).catch(err => {
                toast?.(`恢复失败: ${err.message}`, "error");
              });
            }}
              className="text-xs px-2 py-1 rounded-lg"
              style={{ border: "1px solid var(--warn)", color: "var(--warn)", background: "transparent" }}
            >恢复预设</button>}
          >
            <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
              {MODE_ITEMS.map((mode) => (
                <button key={mode.id} onClick={() => {
                  setSettings(prev => ({ ...prev, mode: mode.id }));
                }}
                  className="text-xs md:text-sm rounded-lg px-2 py-3 transition-all"
                  style={{ border: "1px solid var(--border)", background: settings.mode === mode.id ? "var(--primary-2)" : "var(--card-2)", color: settings.mode === mode.id ? "#ffffff" : "var(--text)", boxShadow: settings.mode === mode.id ? "0 0 24px rgba(167, 139, 250, 0.35)" : "none" }}
                >{mode.label}</button>
              ))}
            </div>
          </Card>
          )}
          {activeTab === "dashboard" && (
          <SortableDashboard
            telemetry={telemetry} setTelemetry={setTelemetry}
            settings={settings} setSettings={setSettings}
            uxtuPayload={uxtuPayload}
            uxtuParams={uxtuParams} setUxtuParams={setUxtuParams}
            history={history}
            editMode={editMode} setEditMode={setEditMode}
            fanCurveActive={fanCurveActive}
            onSwitchTab={setActiveTab} />
          )}
          {activeTab === "system" && <SystemInfoPanel />}
          {activeTab === "settings" && (
            <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload}
              showSwitches={true} showKeyboard={true} showSummary={true} showCredits={true} showAutoStart={true} />
          )}
          {activeTab === "fancurve" && <FanCurvePanel telemetry={telemetry} />}
        </main>
      </div>
    </div>
  );
}
