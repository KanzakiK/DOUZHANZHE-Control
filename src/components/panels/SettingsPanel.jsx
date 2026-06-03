import { applySystemSetting } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";

export default function SettingsPanel({ settings, setSettings, uxtuPayload, showSwitches = true, showKeyboard = true, showSummary = true, showSmu = true, showAbout = true, showCredits = false }) {
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
          <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
            <p>显卡 TDP：{uxtuPayload.params?.gpuPptLimitW ?? "?"}W | 显卡温度墙：{uxtuPayload.params?.gpuTempLimitC ?? "?"}°C</p>
          </div>
        </Card>
      )}
      {showAbout && (<Card title="关于" className="!p-3">
        <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
          <p>斗战者控制台 v1.0.0</p>
          <p>适用于联想 Legion N176 2025 (宝龙达 OEM)</p>
        </div>
      </Card>)}
      {showCredits && (
        <Card title="技术信息" className="!p-3">
          <div className="text-xs space-y-1.5" style={{ color: "var(--muted)" }}>
            <p><span className="font-semibold">开发者：</span>KanzakiK</p>
            <p><span className="font-semibold">开源协议：</span>GNU General Public License v3.0</p>
            <p><span className="font-semibold">前端：</span>React 19 + Vite 8 + Tailwind CSS 3</p>
            <p><span className="font-semibold">后端：</span>Node.js + Express + WebSocket</p>
            <p><span className="font-semibold">硬件访问：</span>inpoutx64 (EC 寄存器) + nvidia-smi</p>
            <p><span className="font-semibold">参考项目：</span>
              <a href="https://github.com/BartoszCichecki/LenovoLegionToolkit" target="_blank" rel="noopener noreferrer"
                style={{ color: "var(--primary)" }}>LenovoLegionToolkit</a>、
              <a href="https://github.com/JamesCJ60/Universal-x86-Tuning-Utility" target="_blank" rel="noopener noreferrer"
                style={{ color: "var(--primary)" }}>Universal x86 Tuning Utility</a>
            </p>
            <p><span className="font-semibold">GitHub：</span>
              <a href="https://github.com/KanzakiK/douzhanzhe-console" target="_blank" rel="noopener noreferrer"
                style={{ color: "var(--primary)" }}>KanzakiK/douzhanzhe-console</a>
            </p>
          </div>
        </Card>
      )}
    </>
  );
}
