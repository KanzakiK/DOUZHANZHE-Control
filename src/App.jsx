import ThemeSwitcher from "./components/ThemeSwitcher";
import PerformancePanel from "./components/panels/PerformancePanel";
import SettingsPanel from "./components/panels/SettingsPanel";
import TelemetryPanel from "./components/panels/TelemetryPanel";
import SystemInfoPanel from "./components/panels/SystemInfoPanel";
import Card from "./components/ui/Card";
import { ToastProvider } from "./components/ui/Toast";
import { useControlState } from "./hooks/useControlState";
import { useState } from "react";

const NAV_ITEMS = ["主页", "系统", "设置"];
const NAV_TABS = { "主页": "dashboard", "系统": "system", "设置": "settings" };
const MODE_ITEMS = [
  { id: "silent", label: "安静模式" },
  { id: "office", label: "均衡模式" },
  { id: "gaming", label: "游戏模式" },
  { id: "beast", label: "狂暴模式" },
  { id: "custom", label: "自定义模式" },
];

export default function App() {
  const { theme, setTheme, telemetry, setTelemetry, uxtuParams, setUxtuParams, settings, setSettings, uxtuPayload, fanLargeRpmTarget, fanSmallRpmTarget, setFanLargeRpmTarget, setFanSmallRpmTarget, history } =
    useControlState();
  const [activeTab, setActiveTab] = useState("dashboard");

  return (
    <ToastProvider>
    <div className={`${theme} min-h-screen p-3 md:p-4`}>
      <div className="max-w-[1750px] mx-auto grid grid-cols-1 lg:grid-cols-[220px_1fr] gap-4">
        <aside className="rounded-2xl p-3 flex flex-col gap-4" style={{ background: "var(--card)", border: "1px solid var(--border)" }}>
          <div className="rounded-xl p-3" style={{ background: "var(--card-2)" }}>
            <p className="text-xs uppercase tracking-widest" style={{ color: "var(--muted)" }}>DOUZHANZHE</p>
            <p className="text-sm font-semibold mt-1">联想斗战者控制台</p>
          </div>
          <nav className="space-y-2">
            {NAV_ITEMS.map((item) => (
              <button key={item} onClick={() => setActiveTab(NAV_TABS[item])}
                className="w-full text-left text-sm rounded-lg px-3 py-2 transition"
                style={{ border: "1px solid var(--border)", background: activeTab === NAV_TABS[item] ? "var(--primary-2)" : "var(--card-2)", color: activeTab === NAV_TABS[item] ? "#ffffff" : "var(--text)" }}
              >{item}</button>
            ))}
          </nav>
          <div className="mt-auto">
            <p className="text-xs mb-2" style={{ color: "var(--muted)" }}>皮肤切换</p>
            <ThemeSwitcher currentTheme={theme} onThemeChange={setTheme} />
          </div>
        </aside>
        <main className="grid grid-rows-[1fr_auto] gap-4">
          {activeTab === "dashboard" && (
          <section className="columns-1 md:columns-2 lg:columns-3 gap-4 space-y-4 [column-fill:balance]">
              <TelemetryPanel telemetry={telemetry} setTelemetry={setTelemetry} settings={settings} setSettings={setSettings}
                uxtuPayload={uxtuPayload} fanLargeRpmTarget={fanLargeRpmTarget} fanSmallRpmTarget={fanSmallRpmTarget}
                setFanLargeRpmTarget={setFanLargeRpmTarget} setFanSmallRpmTarget={setFanSmallRpmTarget} history={history} />
              <PerformancePanel settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} uxtuPayload={uxtuPayload} />
              <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload} />
          </section>
          )}
          {activeTab === "system" && <SystemInfoPanel />}
          {activeTab === "settings" && (
            <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload}
              showSwitches={true} showKeyboard={true} showSummary={true} />
          )}
          {activeTab === "dashboard" && (
          <Card title="模式选择" className="console-dock !p-3">
            <div className="grid grid-cols-2 md:grid-cols-5 gap-2">
              {MODE_ITEMS.map((mode) => (
                <button key={mode.id} onClick={() => setSettings((prev) => ({ ...prev, mode: mode.id }))}
                  className="text-xs md:text-sm rounded-lg px-2 py-3 transition-all"
                  style={{ border: "1px solid var(--border)", background: settings.mode === mode.id ? "var(--primary-2)" : "var(--card-2)", color: settings.mode === mode.id ? "#ffffff" : "var(--text)", boxShadow: settings.mode === mode.id ? "0 0 24px rgba(167, 139, 250, 0.35)" : "none" }}
                >{mode.label}</button>
              ))}
            </div>
          </Card>
          )}
        </main>
      </div>
    </div>
    </ToastProvider>
  );
}
