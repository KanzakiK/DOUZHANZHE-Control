import { applyHardwareControl } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";
import { useState, useEffect } from "react";

export default function SettingsPanel({ settings, setSettings, uxtuPayload, showSwitches = true, showKeyboard = true, showSummary = true, showSmu = true, showAbout = true, showAutoStart = false }) {
  const toast = useToast();
  const [autoStart, setAutoStart] = useState(null);
  useEffect(() => {
    if (!showAutoStart) return;
    fetch("/api/auto-start")
      .then(r => r.json())
      .then(d => setAutoStart(d.enabled))
      .catch(() => setAutoStart(false));
  }, [showAutoStart]);
  const toggleAutoStart = (v) => {
    fetch("/api/auto-start", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: v })
    })
      .then(r => r.json())
      .then(d => {
        if (d.ok) { setAutoStart(v); toast?.(v ? "开机自启已开启" : "开机自启已关闭", "success"); }
        else toast?.(d.error || "设置失败", "error");
      })
      .catch(() => toast?.("请求失败", "error"));
  };
  const toggleSetting = (key, value) => {
    setSettings((prev) => ({ ...prev, [key]: value }));
    // C# HAL 支持的硬件控制走 /api/control
    const halMap = {
      fnLock: "fn_lock",
      numLock: "num_lock",
      capsLock: "caps_lock",
      kbBrightnessLevel: "kb_light",
      gpuOnly: "igpu_only",
      touchpadLock: "touchpad_lock",
      dGpuDirect: "gpu_mode",
    };
    if (key === "osdDisabled") {
      toast?.("关闭 OSD 显示暂不支持", "info");
      return;
    }
    if (key in halMap) {
      // kb_light 透传数值 0-3，其余开关做 bool→0/1 映射
      const mappedValue = key === "kbBrightnessLevel" ? value : (key === "dGpuDirect" ? (value ? 2 : 0) : (value ? 1 : 0));
      applyHardwareControl(halMap[key], mappedValue)
        .catch(() => toast?.("设置下发失败", "error"));
    } else {
      console.warn("[SettingsPanel] unknown key:", key, value);
    }
  };

  return (
    <>
      {showSwitches && (
        <Card title="系统开关" className="!p-3">
          <div className="space-y-1">
            <SwitchRow label="数字键锁定" checked={settings.numLock} onChange={(v) => toggleSetting("numLock", v)} />
            <SwitchRow label="大写键锁定" checked={settings.capsLock} onChange={(v) => toggleSetting("capsLock", v)} />
            <SwitchRow label="触摸板锁定" checked={settings.touchpadLock} onChange={(v) => toggleSetting("touchpadLock", v)} />
            <SwitchRow label="Fn 锁定" checked={settings.fnLock} onChange={(v) => toggleSetting("fnLock", v)} />
          </div>
        </Card>
      )}
      {showAutoStart && (
        <Card title="开机自启" className="!p-3">
          <div className="space-y-1">
            <SwitchRow label="开机自动启动" checked={autoStart === true} onChange={toggleAutoStart} />
          </div>
        </Card>
      )}
      {showKeyboard && (
        <Card title="键盘灯亮度">
          <SliderRow label="亮度" value={settings.kbBrightnessLevel}
            min={0} max={3} step={1} unit=""
            onChange={(v) => toggleSetting("kbBrightnessLevel", v)} />
        </Card>
      )}
      {showSummary && (
        <Card title="当前策略">
          <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
            <p>显卡 TDP：{uxtuPayload.params?.gpuPptLimitW ?? "?"}W | 显卡温度墙：{uxtuPayload.params?.gpuTempLimitC ?? "?"}°C</p>
          </div>
        </Card>
      )}
      {showAbout && (<Card title="关于" className="!p-3">
        <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
          <p>Douzhanzhe Console v1.0.0</p>
          <p>适用于联想 Legion N176 2025 (宝龙达 OEM)</p>
          <p className="mt-2"><span className="font-semibold">开发者：</span>KanzakiK</p>
          <p><span className="font-semibold">开源协议：</span>GNU General Public License v3.0</p>
          <p><span className="font-semibold">GitHub：</span>
            <a href="https://github.com/KanzakiK/douzhanzhe-console" target="_blank" rel="noopener noreferrer"
              style={{ color: "var(--primary)" }}>KanzakiK/douzhanzhe-console</a>
          </p>
        </div>
      </Card>)}
    </>
  );
}
