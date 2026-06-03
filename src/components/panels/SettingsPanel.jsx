import { applySystemSetting } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";

export default function SettingsPanel({ settings, setSettings, uxtuPayload, showSwitches = true, showKeyboard = true, showSummary = true }) {
  const toast = useToast();
  const toggleSetting = (key, value) => {
    setSettings((prev) => ({ ...prev, [key]: value }));
    applySystemSetting(key, value).catch(() => toast?.("设置下发失败", "error"));
  };

  return (
    <>
      {showSwitches && (
        <Card title="系统开关" className="!p-3">
          <div className="space-y-1">
            <SwitchRow label="独显直连" checked={settings.dGpuDirect} onChange={(v) => toggleSetting("dGpuDirect", v)} />
            <SwitchRow label="集显模式" checked={settings.gpuOnly} onChange={(v) => toggleSetting("gpuOnly", v)} />
            <SwitchRow label="数字键锁定" checked={settings.numLock} onChange={(v) => toggleSetting("numLock", v)} />
            <SwitchRow label="大写键锁定" checked={settings.capsLock} onChange={(v) => toggleSetting("capsLock", v)} />
            <SwitchRow label="触摸板锁定" checked={settings.touchpadLock} onChange={(v) => toggleSetting("touchpadLock", v)} />
            <SwitchRow label="关闭 OSD 显示" checked={settings.osdDisabled} onChange={(v) => toggleSetting("osdDisabled", v)} />
            <SwitchRow label="Fn 锁定" checked={settings.fnLock} onChange={(v) => toggleSetting("fnLock", v)} />
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
          <p className="text-xs" style={{ color: "var(--muted)" }}>
            模式：{uxtuPayload.profile} | CPU 长时: {uxtuPayload.params?.cpuLongPptW ?? "?"}W |
            温度墙: {uxtuPayload.params?.cpuTempLimitC ?? "?"}°C
          </p>
        </Card>
      )}
      <Card title="关于" className="!p-3">
        <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
          <p>斗战者控制台 v1.0.0</p>
          <p>适用于联想 Legion N176 2025 (宝龙达 OEM)</p>
          <p>后端: Node.js + Express + WebSocket</p>
          <p>前端: React 19 + Vite + Tailwind CSS</p>
          <p>硬件访问: WinRing0 (EC 寄存器) + nvidia-smi</p>
        </div>
      </Card>
    </>
  );
}
