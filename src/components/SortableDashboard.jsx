import { useState, useCallback, useEffect, useRef } from "react";
import {
  DndContext,
  closestCenter,
  PointerSensor,
  TouchSensor,
  useSensor,
  useSensors,
} from "@dnd-kit/core";
import {
  SortableContext,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import Card from "./ui/Card";
import Gauge from "./ui/Gauge";
import SliderRow from "./ui/SliderRow";
import Sparkline from "./ui/Sparkline";
import SortableCard from "./ui/SortableCard";
import PerformancePanel from "./panels/PerformancePanel";
import SettingsPanel from "./panels/SettingsPanel";
import { getFanRange, applyHardwareControl, startFanCurve, stopFanCurve, reapplyOverrides } from "../services/uxtuAdapter";
import { useCardOrder } from "./../hooks/useCardOrder";
import { useToast } from "./ui/Toast";

const CARD_MAP = {
  "cpu-monitor": { label: "CPU 监控" },
  "gpu-monitor": { label: "GPU 监控" },
  "mem-disk": { label: "内存+硬盘" },
  "fan-info": { label: "风扇信息" },
  "cpu-adjust": { label: "CPU 频率控制" },
  "cpu-power": { label: "CPU 功耗与温度" },
  "gpu-adjust": { label: "GPU 调节" },
  "system-switches": { label: "系统开关" },
  "keyboard-light": { label: "键盘灯亮度" },
  "gpu-mode": { label: "GPU 模式" },
  "about": { label: "关于" },
};

export default function SortableDashboard({
  telemetry, settings, setSettings,
  uxtuParams, setUxtuParams,
  history, editMode,
  fanCurveActive, onFanCurveStop,
  overrides, saveOverride,
}) {
  const toast = useToast();

  // ── 风扇去抖 (400ms，合并大小风扇一次请求) ──
  const fanTimer = useRef(null);
  const latestFanRef = useRef({ large: uxtuParams.fanLargeRpmTarget ?? 2900, small: uxtuParams.fanSmallRpmTarget ?? 6400 });

  function queueFan(largeRpm, smallRpm) {
    latestFanRef.current = { large: largeRpm, small: smallRpm };
    clearTimeout(fanTimer.current);
    fanTimer.current = setTimeout(() => {
      const { large, small } = latestFanRef.current;
      fetch("/api/fan/set-target", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ largeRpm: large, smallRpm: small }),
      }).catch(() => {});
    }, 400);
  }

  // ── 分组自定义状态计数 ──
  const cpuFreqKeys = ["cpuFreqLimitEnabled", "cpuFreqLimitMhz", "cpuTurboDisabled", "cpuCoreLimit", "cpuPowerPlan"];
  const cpuPowerKeys = ["cpuTempLimitC", "cpuVoltageOffset", "cpuLongPptW", "cpuShortPptW"];
  const gpuKeys = ["gpuCoreFreqMhz", "ocCoreOffsetMhz", "gpuMemFreqMhz", "gpuTempLimitC"];
  const fanKeys = ["fanLargeRpmTarget", "fanSmallRpmTarget"];
  const countCustom = (keys) => keys.filter(k => k in (overrides || {})).length;

  function customLabel(keys) {
    const n = countCustom(keys);
    return n > 0 ? `  ·  ${n}项已自定义` : "";
  }
  const onSyncResult = useCallback((ok) => {
    toast?.(ok ? "排序已保存" : "排序保存失败", ok ? "success" : "error");
  }, [toast]);
  const { order, visibleCards, hiddenList, moveCard, resetOrder, toggleHidden, showAll, syncToServer } = useCardOrder(onSyncResult);
  // 退出编辑模式时同步到服务端
  const prevEditRef = useRef(editMode);
  useEffect(() => {
    if (prevEditRef.current === true && editMode === false) {
      syncToServer();
    }
    prevEditRef.current = editMode;
  }, [editMode, syncToServer]);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 8 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 6 } })
  );

  function handleDragEnd(event) {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIdx = order.indexOf(active.id);
    const newIdx = order.indexOf(over.id);
    if (oldIdx === -1 || newIdx === -1) return;
    moveCard(oldIdx, newIdx);
  }

  function renderCard(id) {
    switch (id) {
      case "cpu-monitor":
        return (
          <Card title="CPU 监控" className="!p-5">
            <div className="space-y-3">
              <Gauge label="占用率" value={telemetry.cpuUsage}/>
              <Gauge label="温度" value={telemetry.cpuTemp} unit="°C" color="var(--warn)"/>
              <Gauge label="频率" value={telemetry.cpuFreq} unit=" GHz" color="var(--ok)" max={5.2}/>
              <Sparkline data={history.cpu} title="CPU 负载曲线"/>
            </div>
          </Card>
        );
      case "gpu-monitor":
        return (
          <Card title="GPU 监控" className="!p-5">
            <div className="space-y-3">
              <Gauge label="占用率" value={telemetry.gpuUsage}/>
              <Gauge label="频率" value={telemetry.gpuFreq} unit=" GHz" color="var(--primary-2)" max={3.2}/>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <p className="text-xs" style={{ color: "var(--muted)" }}>温度</p>
                  <p className="text-xl font-bold">{telemetry.gpuTemp || 0}<span className="text-sm font-normal" style={{ color: "var(--muted)" }}> °C</span></p>
                </div>
                <div>
                  <p className="text-xs" style={{ color: "var(--muted)" }}>显存</p>
                  <p className="text-xl font-bold">{typeof telemetry.gpuVramUsed === "number" ? telemetry.gpuVramUsed.toFixed(1) : "0.0"}<span className="text-sm font-normal" style={{ color: "var(--muted)" }}> GB</span></p>
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <p className="text-xs" style={{ color: "var(--muted)" }}>功耗</p>
                  <p className="text-xl font-bold">{typeof telemetry.gpuPowerDrawW === "number" ? telemetry.gpuPowerDrawW.toFixed(1) : "0.0"}<span className="text-sm font-normal" style={{ color: "var(--muted)" }}> W</span></p>
                </div>
                <div>
                  <p className="text-xs" style={{ color: "var(--muted)" }}>显存频率</p>
                  <p className="text-xl font-bold">{telemetry.gpuMemMhz || 0}<span className="text-sm font-normal" style={{ color: "var(--muted)" }}> MHz</span></p>
                </div>
              </div>
              <Sparkline data={history.gpu} title="GPU 负载曲线" color="var(--primary-2)"/>
            </div>
          </Card>
        );
      case "mem-disk":
        return (
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <Card title="内存">
              <div className="space-y-3">
                <Gauge label="占用" value={telemetry.memoryUsage}/>
                <p className="text-sm" style={{ color: "var(--muted)" }}>{telemetry.memoryTotalGB} GB | {telemetry.memoryFreq} MT/s</p>
              </div>
            </Card>
            <Card title="硬盘">
              <div className="space-y-3">
                <Gauge label="占用" value={telemetry.diskUsage}/>
                <p className="text-sm" style={{ color: "var(--muted)" }}>{telemetry.diskFreeGB} GB / {telemetry.diskTotalGB} GB</p>
              </div>
            </Card>
          </div>
        );
      case "fan-info":
        const fanRange = getFanRange(settings?.mode || "silent");
        const fanLargeCustom = "fanLargeRpmTarget" in (overrides || {});
        const fanSmallCustom = "fanSmallRpmTarget" in (overrides || {});
        return (
          <Card title={"风扇信息" + customLabel(fanKeys)}
            action={!editMode && (fanCurveActive ? (
              <button onClick={async () => {
                try {
                  const r = await stopFanCurve();
                  if (r.ok) {
                    // 与 FanCurvePanel.handleStop 对齐：Stop() 触发 ACPI 链重置 SMU/GPU/NVAPI/CPU
                    // 等 500ms 让固件完成模式切换，再重发用户自定义参数
                    const hasOverrides = overrides && Object.keys(overrides).length > 0;
                    if (hasOverrides) {
                      setTimeout(() => {
                        reapplyOverrides(settings.mode, overrides).catch(e => console.warn("[Fan] reapplyOverrides:", e));
                      }, 500);
                      toast?.("已停止曲线，正在恢复用户自定义参数…", "success");
                    } else {
                      toast?.("已恢复固件控制", "success");
                    }
                    onFanCurveStop?.();
                  } else {
                    toast?.("停止失败: " + (r.error || ""), "error");
                  }
                }
                catch (e) { toast?.("关闭自定义散热失败: " + e.message, "error"); }
              }}
                className="text-xs px-2 py-1 rounded-lg flex items-center gap-1"
                style={{ border: "1px solid var(--ok)", color: "var(--ok)", background: "transparent" }}
              >
                <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ background: "var(--ok)", animation: "pulse 1.5s infinite" }} />
                自定义散热运行中
              </button>
            ) : (
              <div className="flex items-center gap-1.5">
                <button onClick={async () => {
                  try { await startFanCurve(); toast?.("自定义散热已开启", "success"); }
                  catch { toast?.("开启自定义散热失败", "error"); }
                }}
                  className="text-xs px-2 py-1 rounded-lg"
                  style={{ border: "1px solid var(--primary-2)", color: "var(--primary-2)", background: "transparent" }}
                >自定义</button>
              </div>
            ))}
          >
            <div className="space-y-3" style={{ opacity: fanCurveActive ? 0.5 : 1 }}>
              <div className="space-y-1">
                <div className="flex items-center justify-between mb-2">
                  <p className="text-sm">大风扇(CPU): <span className="font-bold">{telemetry.fanLargeRpm}</span> RPM</p>
                  {telemetry.fanLargeRpm > 0 && (
                    <span className="inline-flex items-center justify-center w-6 h-6"
                      style={{ animation: `spin ${Math.max(0.5, 3 - telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax) * 2.5)}s linear infinite` }}
                    >⊙</span>
                  )}
                </div>
                <SliderRow label="大风扇目标转速" value={uxtuParams.fanLargeRpmTarget ?? 2900}
                  min={fanRange.largeMin} max={fanRange.largeMax} step={100} unit="RPM"
                  disabled={fanCurveActive}
                  isCustom={fanLargeCustom}
                  onChange={(v) => {
                    setUxtuParams(p => ({ ...p, fanLargeRpmTarget: v }));
                    saveOverride?.(settings.mode, "fanLargeRpmTarget", v);
                    queueFan(v, uxtuParams.fanSmallRpmTarget ?? 6400);
                  }}/>
                <Gauge label="大风扇负载" value={Math.round((telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax)) * 100)}/>
              </div>
              <div className="space-y-1">
                <div className="flex items-center justify-between mb-2">
                  <p className="text-sm">小风扇(GPU): <span className="font-bold">{telemetry.fanSmallRpm}</span> RPM</p>
                  {telemetry.fanSmallRpm > 0 && (
                    <span className="inline-flex items-center justify-center w-6 h-6"
                      style={{ animation: `spin ${Math.max(0.5, 3 - telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax) * 2.5)}s linear infinite` }}
                    >⊙</span>
                  )}
                </div>
                <SliderRow label="小风扇目标转速" value={uxtuParams.fanSmallRpmTarget ?? 6400}
                  min={fanRange.smallMin} max={fanRange.smallMax} step={100} unit="RPM"
                  disabled={fanCurveActive}
                  isCustom={fanSmallCustom}
                  onChange={(v) => {
                    setUxtuParams(p => ({ ...p, fanSmallRpmTarget: v }));
                    saveOverride?.(settings.mode, "fanSmallRpmTarget", v);
                    queueFan(uxtuParams.fanLargeRpmTarget ?? 2900, v);
                  }}/>
                <Gauge label="小风扇负载" value={Math.round((telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax)) * 100)}/>
              </div>
            </div>
          </Card>
        );
      case "cpu-adjust":
        return <PerformancePanel showCpu={true} showGpu={false} showPower={false} settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} overrides={overrides} saveOverride={saveOverride} editMode={editMode} customLabel={customLabel(cpuFreqKeys)}/>;
      case "cpu-power":
        return <PerformancePanel showCpu={false} showGpu={false} showPower={true} settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} overrides={overrides} saveOverride={saveOverride} editMode={editMode} customLabel={customLabel(cpuPowerKeys)}/>;
      case "gpu-adjust":
        return <PerformancePanel showCpu={false} showPower={false} settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} overrides={overrides} saveOverride={saveOverride} editMode={editMode} customLabel={customLabel(gpuKeys)}/>;
      case "keyboard-light":
        return <SettingsPanel settings={settings} setSettings={setSettings} showSwitches={false} showKeyboard={true} showAbout={false}/>;
      case "system-switches":
        return <SettingsPanel settings={settings} setSettings={setSettings} showSwitches={true} showKeyboard={false} showAbout={false}/>;
      case "gpu-mode": {
        const gpuModes = [
          { id: 0, label: "混合模式" },
          { id: 1, label: "独显模式" },
          { id: 2, label: "集显模式" },
        ];
        const currentGpuMode = telemetry.gpuMode != null ? parseInt(telemetry.gpuMode) : -1;
        return (
          <Card title="GPU 模式" className="!p-3">
            <div className="grid grid-cols-3 gap-2">
              {gpuModes.map((m) => (
                <button key={m.id} onClick={() => {
                  if (m.id === 2 && !confirm("切换到集显模式会导致独显断电，笔记本 HDMI/DP 输出口将停止输出信号。\n确定要继续吗？")) return;
                  applyHardwareControl("gpu_mode", m.id).then(() => {
                    toast?.("GPU 模式切换将在重启后生效，请重启电脑", "info");
                  }).catch(() => {
                    toast?.("GPU 模式设置失败", "error");
                  });
                }}
                  className="text-xs md:text-sm rounded-lg px-2 py-3 transition-all"
                  style={{ border: "1px solid var(--border)", background: currentGpuMode === m.id ? "var(--primary-2)" : "var(--card-2)", color: currentGpuMode === m.id ? "#ffffff" : "var(--text)", boxShadow: currentGpuMode === m.id ? "0 0 24px rgba(167, 139, 250, 0.35)" : "none" }}
                >{m.label}</button>
              ))}
            </div>
          </Card>
        );
      }
      case "about":
        return <SettingsPanel settings={settings} setSettings={setSettings} showSwitches={false} showKeyboard={false} showAbout={true}/>;
      default:
        return null;
    }
  }

  return (
    <div>
      <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
        <SortableContext items={order} strategy={verticalListSortingStrategy}>
          <section className="columns-1 md:columns-2 lg:columns-3 gap-3 space-y-3 [column-fill:balance]">
            {visibleCards.map((id) => (
              <SortableCard key={id} id={id} editMode={editMode} onHide={editMode ? () => toggleHidden(id) : undefined}>
                {renderCard(id)}
              </SortableCard>
            ))}
          </section>
        </SortableContext>
      </DndContext>

      {editMode && (
        <div className="mt-6 p-3 rounded-2xl"
          style={{ border: "1px dashed var(--border)", background: "var(--bg-secondary)" }}>
          {hiddenList.length > 0 && (
            <>
              <p className="text-xs font-semibold mb-2" style={{ color: "var(--muted)" }}>已隐藏模块</p>
              <div className="flex flex-wrap gap-2 mb-3">
                {hiddenList.map((id) => (
                  <div key={id}
                    className="flex items-center gap-2 px-3 py-1.5 rounded-lg text-xs"
                    style={{ background: "var(--bg)", border: "1px solid var(--border)" }}
                  >
                    <span style={{ color: "var(--muted)" }}>{CARD_MAP[id]?.label || id}</span>
                    <button onClick={() => toggleHidden(id)}
                      className="font-bold hover:opacity-80"
                      style={{ color: "var(--ok)" }}
                    >显示</button>
                  </div>
                ))}
              </div>
            </>
          )}
          <div className="flex flex-wrap items-center gap-2">
            {hiddenList.length > 0 && (
              <button onClick={showAll}
                className="text-xs px-3 py-1.5 rounded-lg"
                style={{ border: "1px solid var(--border)", color: "var(--muted)" }}
              >☰ 全部显示</button>
            )}
            <button onClick={resetOrder}
              className="text-xs px-3 py-1.5 rounded-lg"
              style={{ border: "1px solid var(--border)", color: "var(--muted)" }}
            >↺ 重置排序</button>
          </div>
        </div>
      )}
    </div>
  );
}
