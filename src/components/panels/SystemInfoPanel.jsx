import { useState, useEffect } from "react";
import Card from "../ui/Card";

const LS_SYS_INFO = "douzhanzhe_sys_info";
const LS_SYS_EXT  = "douzhanzhe_sys_info_ext";
const LS_CACHE_VER = "douzhanzhe_cache_ver";

// 缓存版本 2: 清除旧版编码乱码缓存
if (localStorage.getItem(LS_CACHE_VER) !== "2") {
  localStorage.removeItem(LS_SYS_INFO);
  localStorage.removeItem(LS_SYS_EXT);
  localStorage.setItem(LS_CACHE_VER, "2");
}

export default function SystemInfoPanel() {
  const [info, setInfo] = useState(() => {
    try { const r = localStorage.getItem(LS_SYS_INFO); return r ? JSON.parse(r) : null; } catch { return null; }
  });
  const [ext, setExt] = useState(() => {
    try { const r = localStorage.getItem(LS_SYS_EXT); return r ? JSON.parse(r) : null; } catch { return null; }
  });
  const [refreshing, setRefreshing] = useState(false);

  useEffect(() => {
    const hasInfo = info && info.cpuName;
    const hasExt = ext && ext.biosVersion && ext.battDesign;

    if (!hasInfo) {
      fetch("/api/system/info")
        .then(r => r.json())
        .then(data => { setInfo(data); try { localStorage.setItem(LS_SYS_INFO, JSON.stringify(data)); } catch {} })
        .catch(() => setInfo(prev => prev || {}));
    }

    if (!hasExt) {
      fetch("/api/system/info-ext")
        .then(r => r.json())
        .then(data => { setExt(data); try { localStorage.setItem(LS_SYS_EXT, JSON.stringify(data)); } catch {} })
        .catch(() => setExt(prev => prev || {}));
    }
  }, []);

  const handleRefresh = async () => {
    if (refreshing) return;
    setRefreshing(true);
    try {
      await Promise.all([
        fetch("/api/system/info")
          .then(r => r.json())
          .then(data => { setInfo(data); try { localStorage.setItem(LS_SYS_INFO, JSON.stringify(data)); } catch {} })
          .catch(err => console.error("刷新基本系统信息失败:", err)),
        fetch("/api/system/info-ext")
          .then(r => r.json())
          .then(data => { setExt(data); try { localStorage.setItem(LS_SYS_EXT, JSON.stringify(data)); } catch {} })
          .catch(err => console.error("刷新扩展系统信息失败:", err))
      ]);
    } catch (err) {
      console.error("刷新系统配置异常:", err);
    } finally {
      setRefreshing(false);
    }
  };

  const i = info || {};
  const e = ext || {};
  const loading = "加载中...";

  return (
    <Card
      title="电脑配置"
      className="!p-5"
      action={
        <button
          onClick={handleRefresh}
          disabled={refreshing}
          className="text-xs px-2 py-1.5 rounded-lg flex items-center gap-1 transition-all disabled:opacity-50"
          style={{
            background: "var(--card-2)",
            border: "1px solid var(--border)",
            color: "var(--text)",
            cursor: refreshing ? "not-allowed" : "pointer"
          }}
        >
          <svg
            className={`h-3.5 w-3.5 ${refreshing ? "animate-spin" : ""}`}
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" />
            <path d="M16 3h5v5" />
            <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" />
            <path d="M8 21H3v-5" />
          </svg>
          <span>{refreshing ? "刷新中..." : "刷新"}</span>
        </button>
      }
    >
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
