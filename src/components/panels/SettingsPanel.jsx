import { applyHardwareControl, monitorOff, fetchHotkeyConfig, setHotkeyConfig } from "../../services/uxtuAdapter";
import Card from "../ui/Card";
import SliderRow from "../ui/SliderRow";
import SwitchRow from "../ui/SwitchRow";
import { useToast } from "../ui/Toast";
import { useState, useEffect, useRef, useCallback } from "react";

export default function SettingsPanel({ settings, setSettings, showSwitches = true, showKeyboard = true, showAbout = true, showAutoStart = false, showBackground = false, showHotkey = false, bg, updateBg }) {
  const toast = useToast();
  const [autoStart, setAutoStart] = useState(() => localStorage.getItem("dz_autostart") === "1");
  const [autoStartMinimized, setAutoStartMinimized] = useState(() => localStorage.getItem("dz_autostart_min") === "1");
  useEffect(() => {
    if (!showAutoStart) return;
    fetch("/api/auto-start")
      .then(r => r.json())
      .then(d => { setAutoStart(!!d.enabled); localStorage.setItem("dz_autostart", d.enabled ? "1" : "0"); })
      .catch(() => {});
    fetch("/api/auto-start-opts")
      .then(r => r.json())
      .then(d => { const m = d.minimized === true; setAutoStartMinimized(m); localStorage.setItem("dz_autostart_min", m ? "1" : "0"); })
      .catch(() => {});
  }, [showAutoStart]);

  // 监听手动检查更新结果，显示 toast
  useEffect(() => {
    if (!showAbout) return;
    const handler = (e) => {
      const d = e.detail;
      if (d.error) toast?.(d.msg, "error");
      else if (d.upToDate) toast?.("当前已是最新版本", "success");
      else if (d.skipped) toast?.(`已跳过 v${d.version}，可在跳过列表中取消`, "info");
    };
    window.addEventListener("update-check-result", handler);
    return () => window.removeEventListener("update-check-result", handler);
  }, [showAbout]);

  const toggleAutoStart = (v) => {
    localStorage.setItem("dz_autostart", v ? "1" : "0");
    setAutoStart(v);
    fetch("/api/auto-start", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: v })
    })
      .then(r => r.json())
      .then(d => {
        if (d.ok) { toast?.(v ? "开机自启已开启" : "开机自启已关闭", "success"); }
        else { setAutoStart(!v); localStorage.setItem("dz_autostart", !v ? "1" : "0"); toast?.(d.error || "设置失败", "error"); }
      })
      .catch(() => { setAutoStart(!v); localStorage.setItem("dz_autostart", !v ? "1" : "0"); toast?.("请求失败", "error"); });
  };
  const toggleAutoStartMinimized = (v) => {
    localStorage.setItem("dz_autostart_min", v ? "1" : "0");
    setAutoStartMinimized(v);
    fetch("/api/auto-start-opts", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ minimized: v })
    })
      .then(r => r.json())
      .then(d => {
        if (d.ok) { toast?.(v ? "开机自启最小化已开启" : "开机自启最小化已关闭", "success"); }
        else { setAutoStartMinimized(!v); localStorage.setItem("dz_autostart_min", !v ? "1" : "0"); toast?.(d.error || "设置失败", "error"); }
      })
      .catch(() => { setAutoStartMinimized(!v); localStorage.setItem("dz_autostart_min", !v ? "1" : "0"); toast?.("请求失败", "error"); });
  };

  // ── 自定义背景 ──
  const fileInputRef = useRef(null);
  const bgEnabled = bg?.enabled ?? false;
  const bgOpacity = bg?.opacity ?? 50;
  const bgMask = bg?.maskColor ?? "black";
  const bgHasImage = bg?.hasImage ?? false;
  const bgPreview = bg?.url ?? null;

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
    updateBg({ enabled: v });
    await saveBgOpts({ enabled: v });
  };

  const handleBgOpacity = async (v) => {
    updateBg({ opacity: v });
    await saveBgOpts({ opacity: v });
  };

  const handleBgMask = async (v) => {
    updateBg({ maskColor: v });
    await saveBgOpts({ maskColor: v });
  };

  const handleFileSelect = (e) => {
    const file = e.target.files?.[0];
    if (!file) return;
    if (file.size > 10 * 1024 * 1024) { toast?.("图片不能超过 10MB", "error"); return; }

    // 立即显示预览（不等待 base64 编码）
    const previewUrl = URL.createObjectURL(file);
    updateBg({ hasImage: true, url: previewUrl, enabled: true });

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
            updateBg({ hasImage: true, enabled: true });
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
        updateBg({ enabled: false, hasImage: false, url: null });
        toast?.("背景图片已移除", "success");
      }
    } catch {
      toast?.("操作失败", "error");
    }
  };

  const toggleSetting = (key, value) => {
    setSettings((prev) => ({ ...prev, [key]: value }));
    // C# HAL 支持的硬件控制走 /api/control
    const halMap = {
      fnLock: "fn_lock",
      numLock: "num_lock",
      capsLock: "caps_lock",
      kbBrightnessLevel: "kb_light",
      touchpadLock: "touchpad_lock",
      dGpuDirect: "gpu_mode",
    };
    if (key === "osdDisabled") {
      toast?.("关闭 OSD 显示暂不支持", "info");
      return;
    }
    if (key in halMap) {
      // kb_light 透传数值 0-3，其余开关做 bool→0/1 映射
      const mappedValue = key === "kbBrightnessLevel" ? value : (key === "dGpuDirect" ? (value ? 1 : 0) : (value ? 1 : 0));
      applyHardwareControl(halMap[key], mappedValue)
        .then(() => {
          if (key === "dGpuDirect") toast?.("GPU 模式切换将在重启后生效，请重启电脑", "info");
        })
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
            <SwitchRow label="开机自动启动" checked={autoStart} onChange={toggleAutoStart} />
            <SwitchRow label="开机自启最小化" checked={autoStartMinimized} onChange={toggleAutoStartMinimized} />
          </div>
        </Card>
      )}
      {showHotkey && (
        <HotkeyCard toast={toast} />
      )}
      {showBackground && (
        <Card title="自定义背景" className="!p-3">
          <div className="space-y-2">
            <input ref={fileInputRef} type="file" accept="image/png,image/jpeg,image/webp" onChange={handleFileSelect} className="hidden" />
            <div className="flex gap-2">
              <button onClick={() => fileInputRef.current?.click()}
                className="flex-1 text-sm py-1.5 rounded-lg transition-colors"
                style={{ background: "var(--card-2)", border: "1px solid var(--border)" }}>
                选择图片
              </button>
              {bgHasImage && (
                <button onClick={handleBgDelete}
                  className="text-sm px-3 py-1.5 rounded-lg transition-colors"
                  style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--danger)" }}>
                  移除
                </button>
              )}
            </div>
            {bgPreview && (
              <div className="rounded-lg overflow-hidden" style={{ border: "1px solid var(--border)", aspectRatio: "16/9" }}>
                <img src={bgPreview} alt="背景预览" className="w-full h-full object-cover" />
              </div>
            )}
            <SwitchRow label="启用背景" checked={bgEnabled} onChange={handleBgToggle} disabled={!bgHasImage} />
            <SliderRow label="透明度" value={bgOpacity} min={0} max={100} step={5} unit="%"
              onChange={handleBgOpacity} disabled={!bgEnabled} />
            <div className="flex items-center justify-between py-1" style={{ opacity: bgEnabled ? 1 : 0.5 }}>
              <span className="text-sm">遮罩颜色</span>
              <div className="flex gap-1">
                <button onClick={() => handleBgMask("black")} disabled={!bgEnabled}
                  className="text-xs px-3 py-1 rounded-md transition-colors"
                  style={{
                    background: bgMask === "black" ? "var(--primary)" : "var(--card-2)",
                    border: "1px solid var(--border)", cursor: bgEnabled ? "pointer" : "not-allowed",
                    color: bgMask === "black" ? "#000" : "var(--text)",
                  }}>黑色</button>
                <button onClick={() => handleBgMask("white")} disabled={!bgEnabled}
                  className="text-xs px-3 py-1 rounded-md transition-colors"
                  style={{
                    background: bgMask === "white" ? "var(--primary)" : "var(--card-2)",
                    border: "1px solid var(--border)", cursor: bgEnabled ? "pointer" : "not-allowed",
                    color: bgMask === "white" ? "#000" : "var(--text)",
                  }}>白色</button>
              </div>
            </div>
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
      {showAbout && (<Card title="关于" className="!p-3">
        <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
          <p>{`Douzhanzhe Console v${__APP_VERSION__}`}</p>
          <p>适用于联想 Legion N176 2025 (宝龙达 OEM)</p>
          <p className="mt-2"><span className="font-semibold">开发者：</span>KanzakiK</p>
          <p><span className="font-semibold">开源协议：</span>GNU General Public License v3.0</p>
          <p><span className="font-semibold">GitHub：</span>
            <a href="https://github.com/KanzakiK/DOUZHANZHE-Control" target="_blank" rel="noopener noreferrer"
              style={{ color: "var(--primary)" }}>KanzakiK/DOUZHANZHE-Control</a>
          </p>
          <div className="mt-3 pt-3 flex gap-2" style={{ borderTop: "1px solid var(--border)" }}>
            <button
              onClick={() => {
                // 触发 UpdateDialog 组件检查更新并弹窗
                window.dispatchEvent(new Event("check-update-manual"));
              }}
              style={{
                padding: "6px 12px",
                borderRadius: "6px",
                border: "1px solid var(--border)",
                background: "transparent",
                color: "var(--primary)",
                cursor: "pointer",
                fontSize: "12px",
              }}
            >
              检查更新
            </button>
            <button
              onClick={async () => {
                try {
                  const res = await fetch("/api/logs/export");
                  if (!res.ok) throw new Error(`HTTP ${res.status}`);
                  const blob = await res.blob();
                  const url = URL.createObjectURL(blob);
                  const a = document.createElement("a");
                  a.href = url;
                  const cd = res.headers.get("content-disposition") || "";
                  const m = cd.match(/filename="([^"]+)"/) || cd.match(/filename=([^;\s]+)/);
                  a.download = m?.[1] || `douzhanzhe-log-${Date.now()}.log`;
                  document.body.appendChild(a);
                  a.click();
                  a.remove();
                  URL.revokeObjectURL(url);
                } catch (e) {
                  toast?.("导出日志失败: " + e.message, "error");
                }
              }}
              style={{
                padding: "6px 12px",
                borderRadius: "6px",
                border: "1px solid var(--border)",
                background: "transparent",
                color: "var(--muted)",
                cursor: "pointer",
                fontSize: "12px",
              }}
            >
              导出日志
            </button>
          </div>
        </div>
      </Card>)}
    </>
  );
}

// ---- 快捷键卡片组件 ----
function HotkeyCard({ toast }) {
  const [enabled, setEnabled] = useState(true);
  const [modifiers, setModifiers] = useState("ctrl,shift");
  const [key, setKey] = useState("Q");
  const [conflict, setConflict] = useState(false);
  const [recording, setRecording] = useState(false);
  const [countdown, setCountdown] = useState(null);
  const inputRef = useRef(null);

  useEffect(() => {
    fetchHotkeyConfig()
      .then(cfg => {
        setEnabled(cfg.enabled !== false);
        setModifiers(cfg.modifiers || "ctrl,shift");
        setKey(cfg.key || "Q");
        setConflict(!!cfg.conflict);
      })
      .catch(() => {});
  }, []);

  const formatHotkey = useCallback((mods, k) => {
    const names = {
      ctrl: "Ctrl", control: "Ctrl",
      alt: "Alt", shift: "Shift", win: "Win"
    };
    const parts = (mods || "").split(",").map(m => names[m.trim().toLowerCase()] || m.trim()).filter(Boolean);
    parts.push((k || "").toUpperCase());
    return parts.join(" + ");
  }, []);

  const handleRecord = () => {
    setRecording(true);
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const handleKeyDown = async (e) => {
    if (!recording) return;
    e.preventDefault();
    e.stopPropagation();

    const mods = [];
    if (e.ctrlKey) mods.push("ctrl");
    if (e.altKey) mods.push("alt");
    if (e.shiftKey) mods.push("shift");
    if (e.metaKey) mods.push("win");

    // 忽略单独的修饰键
    const k = e.key;
    if (["Control", "Alt", "Shift", "Meta"].includes(k)) return;

    // 转换特殊键名
    let keyName = k;
    if (k.length === 1) keyName = k.toUpperCase();
    else if (k === " ") keyName = "Space";
    else if (k.startsWith("F") && /^F\d+$/.test(k)) keyName = k;
    else if (k === "Escape") { setRecording(false); return; }
    else return; // 不支持的键

    if (mods.length === 0) {
      toast?.("请至少按下一个修饰键 (Ctrl/Alt/Shift)", "error");
      return;
    }

    const newMods = mods.join(",");
    setModifiers(newMods);
    setKey(keyName);
    setRecording(false);

    try {
      await setHotkeyConfig({ enabled, modifiers: newMods, key: keyName });
      // 延迟读取冲突状态（Shell 需要时间重新注册）
      setTimeout(() => {
        fetchHotkeyConfig()
          .then(cfg => setConflict(!!cfg.conflict))
          .catch(() => {});
      }, 500);
      toast?.(`快捷键已更新为 ${formatHotkey(newMods, keyName)}`, "success");
    } catch {
      toast?.("保存失败", "error");
    }
  };

  const handleToggle = async (v) => {
    setEnabled(v);
    try {
      await setHotkeyConfig({ enabled: v, modifiers, key });
      toast?.(v ? "快捷键已开启" : "快捷键已关闭", "success");
    } catch {
      setEnabled(!v);
      toast?.("设置失败", "error");
    }
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
    try {
      await monitorOff();
    } catch {
      toast?.("关屏失败", "error");
    }
  };

  return (
    <Card title="快捷键" className="!p-3">
      <div className="space-y-2">
        {/* 关闭屏幕 */}
        <div className="flex items-center justify-between">
          <span className="text-sm">关闭屏幕</span>
          <div className="flex items-center gap-2">
            {/* 快捷键显示 */}
            <span className="text-xs px-2 py-0.5 rounded" style={{
              background: "var(--card-2)", border: "1px solid var(--border)",
              fontFamily: "monospace", color: conflict ? "var(--danger)" : "var(--text)"
            }}>
              {formatHotkey(modifiers, key)}
            </span>
            {/* 录制按钮 */}
            <button onClick={handleRecord}
              className="text-xs px-2 py-1 rounded-lg transition-colors"
              style={{ background: recording ? "var(--primary-2)" : "var(--card-2)", border: "1px solid var(--border)", color: recording ? "#fff" : "var(--text)" }}>
              {recording ? "录制中..." : "录制"}
            </button>
            {/* 执行按钮 */}
            <button onClick={handleExecute}
              className="text-xs px-2 py-1 rounded-lg transition-colors"
              style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: countdown !== null ? "var(--primary)" : "var(--text)" }}
              disabled={countdown !== null}>
              {countdown !== null ? `${countdown}s` : "执行"}
            </button>
          </div>
        </div>
        {/* 录制时的隐藏输入框 */}
        {recording && (
          <input
            ref={inputRef}
            onKeyDown={handleKeyDown}
            onBlur={() => setRecording(false)}
            className="w-full text-xs text-center py-1 rounded"
            style={{ background: "var(--card-2)", border: "1px solid var(--primary)", color: "var(--text)", outline: "none" }}
            placeholder="请按下组合键... (Esc 取消)"
            readOnly
            autoFocus
          />
        )}
        {/* 冲突提示 */}
        {conflict && (
          <p className="text-xs" style={{ color: "var(--danger)" }}>
            该快捷键已被其他程序占用，请更换组合键
          </p>
        )}
        {/* 功能开关 */}
        <SwitchRow label="启用全局快捷键" checked={enabled} onChange={handleToggle} />
      </div>
    </Card>
  );
}
