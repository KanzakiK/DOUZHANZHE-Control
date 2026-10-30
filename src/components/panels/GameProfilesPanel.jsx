import { useState, useEffect, useMemo } from "react";
import { fetchGameAutoSwitchStatus, launchGameProfile, fetchProfiles } from "../../services/uxtuAdapter";
import { Skeleton, OfflineCard, EmptyState } from "../ui/PageState";

const MODES = [
  { id: "silent", label: "安静", color: "#4CAF50" },
  { id: "office", label: "均衡", color: "#2196F3" },
  { id: "beast", label: "野兽", color: "#FF9800" },
  { id: "gaming", label: "斗战", color: "#F44336" },
];

const MODE_MAP = Object.fromEntries(MODES.map(m => [m.id, m]));

const ICONS = {
  zap: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><path d="M13 2 3 14h7l-1 8 10-12h-7l1-8Z"/></svg>,
  search: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>,
  plus: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9"><path d="M12 5v14M5 12h14"/></svg>,
  play: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M6 4l14 8-14 8V4Z"/></svg>,
  edit: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>,
  trash: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13"/></svg>,
  close: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M18 6 6 18M6 6l12 12"/></svg>,
  info: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 8v5M12 16h.01"/><circle cx="12" cy="12" r="9"/></svg>,
  menu: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M4 6h16M4 12h16M4 18h10"/></svg>,
  steam: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="12" cy="12" r="9"/><path d="M12 3v9l6 3"/></svg>,
  epic: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M12 2 3 7v10l9 5 9-5V7Z"/></svg>,
};

function initials(name) {
  if (!name) return "?";
  const chars = name.match(/[\u4e00-\u9fa5]/g);
  if (chars && chars.length) return chars.slice(0, 2).join("");
  return name.slice(0, 2).toUpperCase();
}

function GameSkeleton() {
  return (
    <div aria-hidden="true">
      <div className="card master-bar skeleton-card" style={{ marginBottom: 10 }}>
        <Skeleton className="sk-chip" style={{ width: 40, height: 40, borderRadius: 10 }} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <Skeleton className="sk-line" style={{ width: "22%", height: 13 }} />
          <Skeleton className="sk-line" style={{ width: "46%", marginTop: 8 }} />
        </span>
        <Skeleton className="sk-line" style={{ width: 52, height: 20, borderRadius: 999 }} />
        <Skeleton className="sk-line" style={{ width: 44, height: 24, borderRadius: 999 }} />
      </div>
      <div className="card auto-switch-status skeleton-card" style={{ marginBottom: 4 }}>
        {[0, 1, 2, 3].map((i) => (
          <span key={i} style={{ flex: 1, minWidth: 90 }}>
            <Skeleton className="sk-line" style={{ width: "48%" }} />
            <Skeleton className="sk-line" style={{ width: "62%", marginTop: 7 }} />
          </span>
        ))}
      </div>
      <div className="list-tools skeleton-card" style={{ marginTop: 18, marginBottom: 12 }}>
        <Skeleton className="sk-line" style={{ width: 96 }} />
        <span style={{ flex: 1 }} />
        <Skeleton className="sk-line" style={{ width: 84, height: 32, borderRadius: 8 }} />
        <Skeleton className="sk-line" style={{ width: 84, height: 32, borderRadius: 8 }} />
      </div>
      <div className="game-grid">
        {Array.from({ length: 6 }).map((_, i) => (
          <div className="card skeleton-card" key={i} style={{ aspectRatio: "2 / 3", borderRadius: 14 }}>
            <div className="sk" style={{ position: "absolute", inset: 0, borderRadius: 14 }} />
          </div>
        ))}
      </div>
    </div>
  );
}

export default function GameProfilesPanel() {
  const [config, setConfig] = useState({ enabled: true, defaultMode: "cfg-gaming" });
  const [profiles, setProfiles] = useState([]);
  const [configProfiles, setConfigProfiles] = useState([]);
  const [loading, setLoading] = useState(true);
  const [offline, setOffline] = useState(false);

  const [showAdd, setShowAdd] = useState(false);
  const [addForm, setAddForm] = useState({ name: "", exePath: "", targetMode: "cfg-gaming" });

  const [editingId, setEditingId] = useState(null);
  const [editForm, setEditForm] = useState({ name: "", exePath: "", targetMode: "cfg-gaming", enabled: true });

  const [scanning, setScanning] = useState(false);
  const [showScan, setShowScan] = useState(false);
  const [scanResults, setScanResults] = useState([]);
  const [selectedGames, setSelectedGames] = useState(new Set());
  const [batchTargetMode, setBatchTargetMode] = useState("cfg-gaming");
  const [scanTab, setScanTab] = useState("steam");
  const [switchStatus, setSwitchStatus] = useState(null);
  const [launchingId, setLaunchingId] = useState(null);

  const resolveProfile = (id) =>
    configProfiles.find(p => p.id === id) ||
    (MODE_MAP[id] ? { id, name: MODE_MAP[id].label, builtIn: true, thermalMode: id } : null) ||
    // 兼容 cfg- 前缀的内置配置 id 回退（MODE_MAP 以裸性能模式名做 key）
    (MODE_MAP[(id || "").replace(/^cfg-/, "")] ? { id, name: MODE_MAP[(id || "").replace(/^cfg-/, "")].label, builtIn: true, thermalMode: (id || "").replace(/^cfg-/, "") } : null);

  const targetLabel = (id) => resolveProfile(id)?.name || id;
  const targetColor = (id) => {
    const p = resolveProfile(id);
    return (p && MODE_MAP[p.thermalMode]?.color) || "#888";
  };

  const firstTargetId = configProfiles[0]?.id || MODES[0].id;
  const validTargetId = (id) => (resolveProfile(id) ? id : firstTargetId);

  const profileOptions = () => {
    const list = configProfiles.length
      ? configProfiles
      : MODES.map(m => ({ id: m.id, name: m.label, builtIn: true, thermalMode: m.id }));
    const builtin = list.filter(p => p.builtIn);
    const user = list.filter(p => !p.builtIn);
    return (
      <>
        <optgroup label="内置配置">
          {builtin.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </optgroup>
        {user.length > 0 && (
          <optgroup label="用户自建配置">
            {user.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
          </optgroup>
        )}
      </>
    );
  };

  const fetchData = async () => {
    try {
      const res = await fetch("/api/game-profiles");
      if (!res.ok) throw new Error(`game-profiles returned ${res.status}`);
      const data = await res.json();
      setConfig({ enabled: data.enabled, defaultMode: data.defaultMode });
      setProfiles(data.profiles || []);
      setOffline(false);
    } catch (err) {
      console.error("Failed to load game profiles:", err);
      setOffline(true);
    } finally {
      setLoading(false);
    }
  };

  const retry = async () => {
    setLoading(true);
    await fetchData();
  };

  useEffect(() => { fetchData(); }, []);

  useEffect(() => {
    let disposed = false;
    fetchProfiles()
      .then(data => { if (!disposed) setConfigProfiles(data.profiles || []); })
      .catch(err => console.error("Failed to load config profiles:", err));
    return () => { disposed = true; };
  }, []);

  useEffect(() => {
    let disposed = false;
    const loadStatus = async () => {
      try {
        const data = await fetchGameAutoSwitchStatus();
        if (!disposed) setSwitchStatus(data);
      } catch { /* backend offline */ }
    };
    loadStatus();
    const timer = setInterval(loadStatus, 2000);
    return () => { disposed = true; clearInterval(timer); };
  }, []);

  const launchGame = async (p) => {
    if (!p?.id || !p?.exePath) return;
    setLaunchingId(p.id);
    try {
      const data = await launchGameProfile(p.id);
      if (!data?.ok) alert(data?.error || "启动失败");
    } catch (err) {
      alert(err.message || "启动失败");
    } finally {
      setLaunchingId(null);
    }
  };

  const updateConfig = async (patch) => {
    try {
      const res = await fetch("/api/game-profiles/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "保存自动切换配置失败");
        return;
      }
      const data = await res.json();
      setConfig({ enabled: data.enabled, defaultMode: data.defaultMode });
    } catch (err) {
      console.error("Failed to update config:", err);
      alert("保存自动切换配置失败");
    }
  };

  const toggleProfile = async (id, enabled) => {
    const p = profiles.find(x => x.id === id);
    if (!p) return;
    try {
      const res = await fetch(`/api/game-profiles/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...p, enabled }),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "更新规则失败");
        return;
      }
      fetchData();
    } catch (err) {
      console.error("Failed to toggle profile:", err);
      alert("更新规则失败");
    }
  };

  const addProfile = async () => {
    if (!addForm.exePath || !addForm.name) return;
    const exeName = addForm.exePath.split(/[/\\]/).pop();
    try {
      const res = await fetch("/api/game-profiles", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...addForm, exeName, source: "manual" }),
      });
      if (!res.ok) {
        const err = await res.json();
        alert(err.error || "添加失败");
        return;
      }
      setShowAdd(false);
      setAddForm({ name: "", exePath: "", targetMode: validTargetId(config.defaultMode) });
      fetchData();
    } catch (err) {
      console.error("Failed to add profile:", err);
    }
  };

  const updateProfile = async () => {
    const exeName = editForm.exePath.split(/[/\\]/).pop();
    try {
      const res = await fetch(`/api/game-profiles/${editingId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...editForm, exeName }),
      });
      if (!res.ok) {
        const err = await res.json();
        alert(err.error || "更新失败");
        return;
      }
      setEditingId(null);
      fetchData();
    } catch (err) {
      console.error("Failed to update profile:", err);
    }
  };

  const deleteProfile = async (id) => {
    if (!confirm("确定删除此规则？")) return;
    try {
      const res = await fetch(`/api/game-profiles/${id}`, { method: "DELETE" });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "删除规则失败");
        return;
      }
      fetchData();
    } catch (err) {
      console.error("Failed to delete profile:", err);
      alert("删除规则失败");
    }
  };

  const pickFile = async (setter) => {
    try {
      const res = await fetch("/api/game-profiles/file-pick");
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "选择文件失败");
        return;
      }
      const data = await res.json();
      if (data.selected) {
        setter(prev => ({
          ...prev,
          exePath: data.path,
          name: prev.name || data.name,
        }));
      }
    } catch (err) {
      console.error("Failed to pick file:", err);
      alert("选择文件失败");
    }
  };

  const scanGames = async () => {
    setScanning(true);
    try {
      const res = await fetch("/api/game-profiles/scan");
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "扫描失败，请稍后重试");
        return;
      }
      const data = await res.json();
      setScanResults(data || []);
      setSelectedGames(new Set());
      setBatchTargetMode(validTargetId(config.defaultMode));
      setShowScan(true);
    } catch (err) {
      console.error("Failed to scan games:", err);
      alert("扫描失败，请稍后重试");
    } finally {
      setScanning(false);
    }
  };

  const toggleGameSelection = (index) => {
    setSelectedGames(prev => {
      const next = new Set(prev);
      if (next.has(index)) next.delete(index);
      else next.add(index);
      return next;
    });
  };

  const toggleSelectAll = () => {
    const available = scanResults
      .map((g, i) => ({ ...g, index: i }))
      .filter(g => !g.alreadyAdded && (scanTab === "all" || g.source === scanTab));
    const allSelected = available.every(g => selectedGames.has(g.index));
    if (allSelected) {
      const next = new Set(selectedGames);
      available.forEach(g => next.delete(g.index));
      setSelectedGames(next);
    } else {
      const next = new Set(selectedGames);
      available.forEach(g => next.add(g.index));
      setSelectedGames(next);
    }
  };

  const batchAddGames = async () => {
    const games = [...selectedGames].map(i => ({
      name: scanResults[i].name,
      exePath: scanResults[i].exePath,
      targetMode: batchTargetMode,
      source: scanResults[i].source,
    }));
    if (games.length === 0) {
      setShowScan(false);
      return;
    }
    try {
      const res = await fetch("/api/game-profiles/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ games }),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        alert(err.error || "批量添加失败");
        return;
      }
      const data = await res.json();
      setShowScan(false);
      setSelectedGames(new Set());
      setScanResults([]);
      fetchData();
      alert(`成功添加 ${data.added || games.length} 个游戏`);
    } catch (err) {
      console.error("Failed to batch add:", err);
      alert("批量添加失败");
    }
  };

  const activeCount = profiles.filter(p => p.enabled).length;

  const filteredScanResults = useMemo(() =>
    scanResults.map((g, i) => ({ ...g, index: i })).filter(g => scanTab === "all" || g.source === scanTab),
    [scanResults, scanTab]
  );

  if (loading && !offline) {
    return <GameSkeleton />;
  }

  if (offline) {
    return (
      <OfflineCard
        title="游戏规则暂不可用"
        description="无法连接后端服务，自动切换与游戏规则列表暂时不可用。"
        onRetry={retry}
        retrying={loading}
      />
    );
  }

  return (
    <>
      <div className={`card master-bar reveal enter ${config.enabled ? "" : "off"}`} style={{ animationDelay: ".02s" }}>
        <span className="mi">{ICONS.zap}</span>
        <span className="mt">
          <b>自动切换</b>
          <small>检测到绑定的游戏进程启动时，自动切换到对应配置；退出后恢复当前配置</small>
        </span>
        <span className="state">{config.enabled ? "已启用" : "已禁用"}</span>
        <label className="switch">
          <input
            type="checkbox"
            checked={config.enabled}
            onChange={(e) => updateConfig({ enabled: e.target.checked })}
            aria-label="游戏自动切换全局开关"
          />
          <span className="track"></span>
        </label>
      </div>

      <div className={`card auto-switch-status reveal enter ${config.enabled && switchStatus?.activeGames?.length ? "active" : ""}`} style={{ animationDelay: ".03s" }}>
        <span className="as-item">
          <span className="as-label">服务状态</span>
          <b>{switchStatus?.serviceRunning ? "运行中" : "未知"}</b>
        </span>
        <span className="as-item">
          <span className="as-label">生效模式</span>
          <b>{switchStatus?.effectiveMode && switchStatus.effectiveMode !== "none" ? targetLabel(switchStatus.effectiveMode) : "无"}</b>
        </span>
        <span className="as-item">
          <span className="as-label">快照模式</span>
          <b>{switchStatus?.snapshotMode && switchStatus.snapshotMode !== "none" ? targetLabel(switchStatus.snapshotMode) : "无"}</b>
        </span>
        <span className="as-item as-games">
          <span className="as-label">运行中游戏</span>
          <span className="as-tags">
            {switchStatus?.activeGames?.length
              ? switchStatus.activeGames.map((g, i) => (
                  <span key={`${g.pid}-${i}`} className="as-tag" style={{ borderColor: targetColor(g.targetMode) }}>
                    {g.name} · {targetLabel(g.targetMode)}
                  </span>
                ))
              : <b>0 个</b>}
          </span>
        </span>
      </div>

      <div className="list-tools reveal enter" style={{ animationDelay: ".04s" }}>
        <span className="count">共 <b>{profiles.length}</b> 条规则 · 启用 <b>{activeCount}</b> 条</span>
        <span className="sp"></span>
        <button className="btn" onClick={scanGames} disabled={scanning}>
          {scanning ? ICONS.search : ICONS.search}扫描游戏
        </button>
        <button className="btn primary" onClick={() => { setShowAdd(true); setAddForm({ name: "", exePath: "", targetMode: validTargetId(config.defaultMode) }); }}>
          {ICONS.plus}添加规则
        </button>
      </div>

      {profiles.length === 0 ? (
        <EmptyState
          title="暂无游戏规则"
          description="添加或扫描游戏后，启动对应进程时会自动切换到绑定的配置。"
          action={<>
            <button className="btn" onClick={scanGames} disabled={scanning}>
              {ICONS.search}扫描游戏
            </button>
            <button
              className="btn primary"
              onClick={() => { setShowAdd(true); setAddForm({ name: "", exePath: "", targetMode: validTargetId(config.defaultMode) }); }}
            >
              {ICONS.plus}添加规则
            </button>
          </>}
        />
      ) : (
        <div className="game-grid reveal enter" style={{ animationDelay: ".06s" }}>
          {profiles.map(p => (
            <div key={p.id} className={`gcard ${p.enabled ? "" : "off"}`}>
              <div className="poster" style={{ background: `linear-gradient(135deg, ${targetColor(p.targetMode)}66, #1a1a2e)` }}>
                <span className="poster-letter">{initials(p.name)}</span>
              </div>
              <span className="shade"></span>
              <div className="top">
                <span className="cfg"><span className="dot" style={{ background: targetColor(p.targetMode) }}></span>{targetLabel(p.targetMode)}</span>
                <label className="switch sw">
                  <input
                    type="checkbox"
                    checked={p.enabled}
                    onChange={(e) => toggleProfile(p.id, e.target.checked)}
                    aria-label={`启用 ${p.name} 规则`}
                  />
                  <span className="track"></span>
                </label>
              </div>
              <div className="info">
                <b>{p.name}</b>
                <small>{p.exeName}</small>
              </div>
              <div className="bottom">
                <button className="btn primary launch" onClick={() => launchGame(p)} disabled={launchingId === p.id}>
                  {ICONS.play}{launchingId === p.id ? "启动中..." : "启动"}
                </button>
                <div className="acts">
                  <button className="ico-btn" title="编辑" aria-label={`编辑 ${p.name} 规则`}
                    onClick={() => { setEditingId(p.id); setEditForm({ name: p.name, exePath: p.exePath, targetMode: p.targetMode, enabled: p.enabled }); }}>
                    {ICONS.edit}
                  </button>
                  <button className="ico-btn danger" title="删除" aria-label={`删除 ${p.name} 规则`} onClick={() => deleteProfile(p.id)}>
                    {ICONS.trash}
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="hint reveal enter" style={{ animationDelay: ".08s" }}>
        {ICONS.info}
        退出游戏后，将自动恢复为<b>控制面板当前选中的配置</b>，而非固定指向某个配置。例：当前选中 gaming，启动原神切到「均衡」，退出后恢复 gaming。
      </div>

      {showAdd && (
        <div className="modal show">
          <div className="panel">
            <div className="p-head">
              <span><b>添加游戏规则</b><small>选择游戏可执行文件，并绑定一个参数配置</small></span>
              <button className="x" onClick={() => setShowAdd(false)} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              <div className="field">
                <label>游戏路径 <small>浏览选择 .exe，自动提取进程名</small></label>
                <div className="in">
                  <input
                    type="text"
                    value={addForm.exePath}
                    onChange={(e) => {
                      const path = e.target.value;
                      const exeName = path.split(/[/\\]/).pop();
                      setAddForm(prev => ({ ...prev, exePath: path, name: prev.name || exeName?.replace(/\.exe$/i, "") || "" }));
                    }}
                    placeholder="D:\\Games\\...\\Game.exe"
                    aria-label="游戏可执行文件路径"
                  />
                  <button className="btn" type="button" onClick={() => pickFile(setAddForm)}>{ICONS.menu}浏览</button>
                </div>
              </div>
              <div className="field">
                <label>显示名称 <small>可留空，默认用进程名</small></label>
                <input
                  type="text"
                  value={addForm.name}
                  onChange={(e) => setAddForm({ ...addForm, name: e.target.value })}
                  placeholder="例如：黑神话·悟空"
                  aria-label="游戏显示名称"
                />
              </div>
              <div className="field">
                <label>绑定配置 <small>启动该游戏时切换到的配置</small></label>
                <select
                  value={addForm.targetMode}
                  onChange={(e) => setAddForm({ ...addForm, targetMode: e.target.value })}
                  aria-label="绑定配置"
                >
                  {profileOptions()}
                </select>
              </div>
            </div>
            <div className="p-foot">
              <button className="btn ghost" onClick={() => setShowAdd(false)}>取消</button>
              <button className="btn primary" onClick={addProfile}>添加规则</button>
            </div>
          </div>
        </div>
      )}

      {editingId && (
        <div className="modal show">
          <div className="panel">
            <div className="p-head">
              <span><b>编辑游戏规则</b><small>修改绑定路径或目标配置</small></span>
              <button className="x" onClick={() => setEditingId(null)} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              <div className="field">
                <label>游戏路径</label>
                <div className="in">
                  <input
                    type="text"
                    value={editForm.exePath}
                    onChange={(e) => setEditForm({ ...editForm, exePath: e.target.value })}
                    placeholder="D:\\Games\\...\\Game.exe"
                  />
                  <button className="btn" type="button" onClick={() => pickFile(setEditForm)}>{ICONS.menu}浏览</button>
                </div>
              </div>
              <div className="field">
                <label>显示名称</label>
                <input
                  type="text"
                  value={editForm.name}
                  onChange={(e) => setEditForm({ ...editForm, name: e.target.value })}
                  placeholder="例如：黑神话·悟空"
                />
              </div>
              <div className="field">
                <label>绑定配置</label>
                <select
                  value={editForm.targetMode}
                  onChange={(e) => setEditForm({ ...editForm, targetMode: e.target.value })}
                >
                  {profileOptions()}
                </select>
              </div>
            </div>
            <div className="p-foot">
              <button className="btn ghost" onClick={() => setEditingId(null)}>取消</button>
              <button className="btn primary" onClick={updateProfile}>保存</button>
            </div>
          </div>
        </div>
      )}

      {showScan && (
        <div className="modal show">
          <div className="panel">
            <div className="p-head">
              <span><b>扫描游戏</b><small>从游戏平台库中批量导入</small></span>
              <button className="x" onClick={() => { setShowScan(false); setSelectedGames(new Set()); }} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              <div className="scan-src">
                <span className={`chip ${scanTab === "steam" ? "active" : ""}`} onClick={() => setScanTab("steam")}>{ICONS.steam}Steam</span>
                <span className={`chip ${scanTab === "epic" ? "active" : ""}`} onClick={() => setScanTab("epic")}>{ICONS.epic}Epic</span>
              </div>
              {filteredScanResults.length === 0 ? (
                <p style={{ textAlign: "center", color: "var(--fg-3)", padding: "24px 0" }}>该平台未发现新游戏</p>
              ) : (
                <>
                  <div className="scan-list">
                    {filteredScanResults.map(g => (
                      <label key={g.index} className={`scan-item ${g.alreadyAdded ? "disabled" : ""}`}>
                        <input
                          type="checkbox"
                          checked={selectedGames.has(g.index)}
                          onChange={() => !g.alreadyAdded && toggleGameSelection(g.index)}
                          disabled={g.alreadyAdded}
                        />
                        <span className="gi" style={{ background: `linear-gradient(135deg, ${targetColor(batchTargetMode)}66, #1a1a2e)` }}>
                          <span className="gi-letter">{initials(g.name)}</span>
                        </span>
                        <span className="gn">
                          {g.name}
                          <small>{g.exePath} · {g.source === "steam" ? "Steam" : "Epic"}{g.alreadyAdded ? " · 已添加" : ""}</small>
                        </span>
                      </label>
                    ))}
                  </div>
                  <div className="scan-bind">
                    <span>统一绑定到</span>
                    <select value={batchTargetMode} onChange={(e) => setBatchTargetMode(e.target.value)} aria-label="批量绑定配置">
                      {profileOptions()}
                    </select>
                  </div>
                </>
              )}
            </div>
            <div className="p-foot">
              {filteredScanResults.length > 0 && (
                <button className="btn ghost" onClick={toggleSelectAll}>全选/取消</button>
              )}
              <span style={{ flex: 1 }}></span>
              <button className="btn ghost" onClick={() => { setShowScan(false); setSelectedGames(new Set()); }}>取消</button>
              <button className="btn primary" onClick={batchAddGames} disabled={selectedGames.size === 0}>导入选中（{selectedGames.size}）</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
