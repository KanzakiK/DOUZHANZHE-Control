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
import { getFanRange } from "../services/uxtuAdapter";
import { useCardOrder } from "./../hooks/useCardOrder";
import { useToast } from "./ui/Toast";

const CARD_MAP = {
  "cpu-monitor": { label: "CPU 监控" },
  "gpu-monitor": { label: "GPU 监控" },
  "mem-disk": { label: "内存+硬盘" },
  "fan-info": { label: "风扇信息" },
  "cpu-adjust": { label: "CPU 调节" },
  "gpu-adjust": { label: "GPU 调节" },
  "system-switches": { label: "系统开关" },
  "keyboard-light": { label: "键盘灯亮度" },
  "current-strategy": { label: "当前策略" },
  "about": { label: "关于" },
};

export default function SortableDashboard({
  telemetry, setTelemetry, settings, setSettings,
  uxtuPayload, uxtuParams, setUxtuParams,
  fanLargeRpmTarget, fanSmallRpmTarget,
  setFanLargeRpmTarget, setFanSmallRpmTarget, history,
  editMode, setEditMode,
}) {
  const toast = useToast();
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
              <p className="text-sm" style={{ color: "var(--muted)" }}>核心: {telemetry.cpuCores}</p>
              <Sparkline data={history.cpu} title="CPU 负载曲线"/>
            </div>
          </Card>
        );
      case "gpu-monitor":
        return (
          <Card title="GPU 监控" className="!p-5">
            <div className="space-y-3">
              <Gauge label="占用率" value={telemetry.gpuUsage}/>
              <Gauge label="温度" value={telemetry.gpuTemp} unit="°C" color="var(--warn)"/>
              <Gauge label="频率" value={telemetry.gpuFreq} unit=" GHz" color="var(--primary-2)" max={3.2}/>
              <p className="text-sm" style={{ color: "var(--muted)" }}>显存: {typeof telemetry.gpuVramUsed === "number" ? telemetry.gpuVramUsed.toFixed(1) : "?"}/{telemetry.gpuVram} GB</p>
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
                <p className="text-sm" style={{ color: "var(--muted)" }}>总容量: {telemetry.memoryTotalGB} GB | 频率: {telemetry.memoryFreq} MT/s</p>
              </div>
            </Card>
            <Card title="硬盘">
              <div className="space-y-3">
                <Gauge label="占用" value={telemetry.diskUsage}/>
                <p className="text-sm" style={{ color: "var(--muted)" }}>总容量: {telemetry.diskTotalGB} GB | 可用: {telemetry.diskFreeGB} GB</p>
              </div>
            </Card>
          </div>
        );
      case "fan-info":
        return (
          <Card title="风扇信息">
            <div className="space-y-3">
              <div className="space-y-1">
                <div className="flex items-center justify-between mb-2">
                  <p className="text-sm">大风扇(CPU): <span className="font-bold">{telemetry.fanLargeRpm}</span> RPM / {telemetry.fanLargeMax}</p>
                  {telemetry.fanLargeRpm > 0 && (
                    <span className="inline-flex items-center justify-center w-6 h-6"
                      style={{ animation: `spin ${Math.max(0.5, 3 - telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax) * 2.5)}s linear infinite` }}
                    >⊙</span>
                  )}
                </div>
                <SliderRow label="大风扇目标转速" value={fanLargeRpmTarget}
                  min={0} max={telemetry.fanLargeMax} step={100} unit="RPM"
                  onChange={(v) => setFanLargeRpmTarget(v)}/>
                <Gauge label="大风扇负载" value={Math.round((telemetry.fanLargeRpm / Math.max(1, telemetry.fanLargeMax)) * 100)}/>
              </div>
              <div className="space-y-1">
                <div className="flex items-center justify-between mb-2">
                  <p className="text-sm">小风扇(GPU): <span className="font-bold">{telemetry.fanSmallRpm}</span> RPM / {telemetry.fanSmallMax}</p>
                  {telemetry.fanSmallRpm > 0 && (
                    <span className="inline-flex items-center justify-center w-6 h-6"
                      style={{ animation: `spin ${Math.max(0.5, 3 - telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax) * 2.5)}s linear infinite` }}
                    >⊙</span>
                  )}
                </div>
                <SliderRow label="小风扇目标转速" value={fanSmallRpmTarget}
                  min={0} max={telemetry.fanSmallMax} step={100} unit="RPM"
                  onChange={(v) => setFanSmallRpmTarget(v)}/>
                <Gauge label="小风扇负载" value={Math.round((telemetry.fanSmallRpm / Math.max(1, telemetry.fanSmallMax)) * 100)}/>
              </div>
              {/* 风扇负载曲线已隐藏 — EC 16 位竞态导致心电图问题 */}
            </div>
          </Card>
        );
      case "cpu-adjust":
        return <PerformancePanel showGpu={false} settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} uxtuPayload={uxtuPayload}/>;
      case "gpu-adjust":
        return <PerformancePanel showCpu={false} settings={settings} setSettings={setSettings} uxtuParams={uxtuParams} setUxtuParams={setUxtuParams} uxtuPayload={uxtuPayload}/>;
      case "keyboard-light":
        return <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload} showSwitches={false} showKeyboard={true} showSummary={false} showSmu={false} showAbout={false}/>;
      case "current-strategy":
        return <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload} showSwitches={false} showKeyboard={false} showSummary={true} showSmu={false} showAbout={false}/>;
      case "system-switches":
        return <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload} showSwitches={true} showKeyboard={false} showSummary={false} showSmu={false} showAbout={false}/>;
      case "about":
        return <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload} showSwitches={false} showKeyboard={false} showSummary={false} showSmu={false} showAbout={true}/>;
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
