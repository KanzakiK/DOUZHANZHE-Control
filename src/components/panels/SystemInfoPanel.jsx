import { useState, useEffect } from "react";
import Card from "../ui/Card";

const LS_SYS_INFO = "douzhanzhe_sys_info";
const LS_SYS_EXT  = "douzhanzhe_sys_info_ext";

export default function SystemInfoPanel() {
  const [info, setInfo] = useState(() => {
    try { const r = localStorage.getItem(LS_SYS_INFO); return r ? JSON.parse(r) : null; } catch { return null; }
  });
  const [ext, setExt] = useState(() => {
    try { const r = localStorage.getItem(LS_SYS_EXT); return r ? JSON.parse(r) : null; } catch { return null; }
  });

  useEffect(() => {
    if (!info || !info.cpuName) {
      fetch("/api/system/info")
        .then(r => r.json())
        .then(data => { setInfo(data); try { localStorage.setItem(LS_SYS_INFO, JSON.stringify(data)); } catch {} })
        .catch(() => setInfo({}));
    }
    if (!ext || !ext.biosVersion || !ext.battDesign) {
      fetch("/api/system/info-ext")
        .then(r => r.json())
        .then(data => { setExt(data); try { localStorage.setItem(LS_SYS_EXT, JSON.stringify(data)); } catch {} })
        .catch(() => setExt({}));
    }
  }, []);

  const i = info || {};
  const e = ext || {};
  const loading = "加载中...";

  return (
    <Card title="电脑配置" className="!p-5">
      <div className="space-y-4 text-sm">
        {/* 主板 */}
        <Section title="主板">
          <Row label="型号" value={e.boardInfo || i.systemModel || loading} />
          <Row label="BIOS 版本" value={e.biosVersion || loading} />
          <Row label="BIOS 日期" value={e.biosDate || loading} />
        </Section>

        {/* 操作系统 */}
        <Section title="操作系统">
          <Row label="系统" value={e.osName || loading} />
          <Row label="版本号" value={e.osBuild || loading} />
        </Section>

        {/* 处理器 */}
        <Section title="处理器">
          <Row label="CPU" value={i.cpuName || loading} />
          <Row label="核心数" value={i.cpuCores != null ? i.cpuCores + " 核" : loading} />
        </Section>

        {/* 显卡 */}
        <Section title="显卡">
          <Row label="独显" value={i.gpuDiscrete || loading} />
          <Row label="集显" value={i.gpuIntegrated || loading} />
          <Row label="驱动版本" value={e.nvDriver || loading} />
          <Row label="VBIOS" value={e.nvVbios || loading} />
        </Section>

        {/* 内存 - 逐条显示 */}
        <Section title="内存">
          {e.sticks && e.sticks.length > 0
            ? e.sticks.map((s, idx) => (
                <Row
                  key={idx}
                  label={`插槽 ${idx + 1}`}
                  value={`${s.sizeGB} GB · ${s.speed} MHz · ${s.manufacturer || "未知"}`}
                />
              ))
            : <Row label="容量" value={i.memoryTotalGB != null ? i.memoryTotalGB + " GB" : loading} />
          }
        </Section>

        {/* 存储 - 逐盘显示 */}
        <Section title="存储">
          {e.disks && e.disks.length > 0
            ? e.disks.map((d, idx) => (
                <Row
                  key={idx}
                  label={`硬盘 ${idx + 1}`}
                  value={`${d.model} · ${d.sizeGB} GB`}
                />
              ))
            : <Row label="硬盘" value={i.diskTotalGB != null ? i.diskTotalGB + " GB" : loading} />
          }
        </Section>

        {/* 电池 */}
        <Section title="电池">
          <Row label="电量" value={e.battPercent != null && e.battPercent >= 0 ? e.battPercent + "%" : loading} />
          <Row label="健康度" value={
            e.battDesign > 0
              ? `${e.battFull} mWh / ${e.battDesign} mWh（${(e.battFull / e.battDesign * 100).toFixed(2)}%）`
              : loading
          } />
        </Section>
      </div>
    </Card>
  );
}

function Section({ title, children }) {
  return (
    <div>
      <p className="text-xs font-semibold mb-1" style={{ color: "var(--muted)" }}>{title}</p>
      <div className="space-y-0.5">{children}</div>
    </div>
  );
}

function Row({ label, value }) {
  return (
    <div className="flex justify-between text-xs py-0.5" style={{ borderBottom: "1px solid var(--border)" }}>
      <span style={{ color: "var(--muted)" }}>{label}</span>
      <span className="text-right ml-4">{value}</span>
    </div>
  );
}
