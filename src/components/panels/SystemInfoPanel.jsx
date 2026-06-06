import { useState, useEffect } from "react";
import Card from "../ui/Card";

export default function SystemInfoPanel() {
  const [info, setInfo] = useState(null);

  useEffect(() => {
    fetch("/api/system/info")
      .then((r) => r.json())
      .then(setInfo)
      .catch(() => setInfo({}));
  }, []);

  const i = info || {};

  return (
    <Card title="电脑配置" className="!p-5">
      <div className="space-y-4 text-sm">
        <Section title="型号">
          <Row label="品牌/型号" value={i.systemModel || "加载中..."} />
        </Section>
        <Section title="处理器">
          <Row label="CPU" value={i.cpuName || "加载中..."} />
          <Row label="核心数" value={i.cpuCores != null ? i.cpuCores + " 核" : "加载中..."} />
        </Section>
        <Section title="显卡">
          <Row label="独显" value={i.gpuDiscrete || "加载中..."} />
          <Row label="集显" value={i.gpuIntegrated || "加载中..."} />
        </Section>
        <Section title="内存">
          <Row label="容量" value={i.memoryTotalGB != null ? i.memoryTotalGB + " GB" : "加载中..."} />
          <Row label="频率" value={i.memoryFreq != null ? i.memoryFreq + " MHz" : "加载中..."} />
        </Section>
        <Section title="存储">
          <Row label="硬盘" value={i.diskTotalGB != null ? i.diskTotalGB + " GB" : "加载中..."} />
        </Section>
        <Section title="开源">
          <Row label="源码" value="github.com/KanzakiK/DOUZHANZHE-Control" />
          <Row label="协议" value="GNU General Public License v3.0" />
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
      <span>{value}</span>
    </div>
  );
}
