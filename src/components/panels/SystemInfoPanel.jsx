import Card from "../ui/Card";

export default function SystemInfoPanel() {
  return (
    <Card title="电脑配置" className="!p-5">
      <div className="space-y-4 text-sm">
        <Section title="型号">
          <Row label="品牌" value="Lenovo (宝龙达 OEM)" />
          <Row label="型号" value="Legion N176 2025" />
        </Section>
        <Section title="处理器">
          <Row label="CPU" value="AMD Ryzen 9 8940HX (Dragon Range)" />
          <Row label="核心/线程" value="16C / 32T" />
          <Row label="基础频率" value="2.4 GHz" />
          <Row label="最高频率" value="5.2 GHz" />
        </Section>
        <Section title="显卡">
          <Row label="独显" value="NVIDIA GeForce RTX 5060 Laptop GPU 8GB GDDR7" />
          <Row label="集显" value="AMD Radeon(TM) 610M" />
        </Section>
        <Section title="内存">
          <Row label="容量" value="32 GB (16GB×2)" />
          <Row label="类型" value="DDR5" />
        </Section>
        <Section title="存储">
          <Row label="硬盘" value="1TB PCIe 4.0 NVMe SSD" />
        </Section>
        <Section title="散热">
          <Row label="风扇" value="3风扇散热系统" />
          <Row label="控制" value="inpoutx64 EC 寄存器直读" />
        </Section>
        <Section title="软件">
          <Row label="控制台版本" value="Douzhanzhe Console v1.0.0" />
          <Row label="后端" value="Node.js + Express + WebSocket" />
          <Row label="前端" value="React 19 + Vite + Tailwind CSS" />
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
