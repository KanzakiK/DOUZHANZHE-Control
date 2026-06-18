import { useState, useEffect } from "react";

const MODES = {
  silent: { label: "安静模式", color: "#4CAF50" },
  office: { label: "均衡模式", color: "#2196F3" },
  beast: { label: "野兽模式", color: "#FF9800" },
  gaming: { label: "斗战模式", color: "#F44336" },
};

export default function AutoSwitchStatus() {
  const [status, setStatus] = useState(null);

  useEffect(() => {
    const fetchStatus = async () => {
      try {
        const res = await fetch("/api/game-profiles/status");
        if (res.ok) {
          setStatus(await res.json());
        }
      } catch {}
    };

    fetchStatus();
    const timer = setInterval(fetchStatus, 3000);
    return () => clearInterval(timer);
  }, []);

  // 未启用或无活跃游戏时不显示
  if (!status || !status.globalEnabled || status.activeGames.length === 0) {
    return null;
  }

  const gameCount = status.activeGames.length;
  const effectiveMode = MODES[status.effectiveMode];

  return (
    <div className="rounded-xl p-3 flex items-center gap-3" style={{ background: "var(--card-2)", border: `1px solid ${effectiveMode?.color || "var(--border)"}` }}>
      <div className="w-2 h-2 rounded-full animate-pulse" style={{ background: effectiveMode?.color || "#888" }} />
      <div className="flex-1 min-w-0">
        <p className="text-xs" style={{ color: "var(--muted)" }}>自动切换</p>
        <p className="text-sm font-medium truncate">
          {gameCount === 1
            ? `${status.activeGames[0].name} → ${effectiveMode?.label || status.effectiveMode}`
            : `${gameCount} 款游戏运行中 → ${effectiveMode?.label || status.effectiveMode}`
          }
        </p>
      </div>
    </div>
  );
}
