import Card from "./Card";

export default function AboutPanel() {
  return (
    <Card title="关于" className="!p-3">
      <div className="text-xs space-y-1" style={{ color: "var(--muted)" }}>
        <p>斗战者控制台 v1.0.0</p>
        <p>适用于联想 Legion N176 2025 (宝龙达 OEM)</p>
        <p>后端: Node.js + Express + WebSocket</p>
        <p>前端: React 19 + Vite + Tailwind CSS</p>
        <p>硬件访问: WinRing0 (EC 寄存器) + nvidia-smi</p>
      </div>
    </Card>
  );
}
