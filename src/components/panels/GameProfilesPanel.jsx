import { useState, useEffect, useMemo, useRef } from "react";
import { fetchGameAutoSwitchStatus, launchGameProfile, fetchProfiles } from "../../services/uxtuAdapter";
import { Skeleton, OfflineCard, EmptyState } from "../ui/PageState";
import { useToast } from "../ui/Toast";

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
  gear: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1 1.55V21a2 2 0 1 1-4 0v-.09a1.7 1.7 0 0 0-1-1.55 1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .34-1.87 1.7 1.7 0 0 0-1.55-1H3a2 2 0 1 1 0-4h.09a1.7 1.7 0 0 0 1.55-1 1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34h.09a1.7 1.7 0 0 0 1-1.55V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1 1.55 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87v.09a1.7 1.7 0 0 0 1.55 1H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.55 1Z"/></svg>,
  image: <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.1-3.1a2 2 0 0 0-2.8 0L6 21"/></svg>,
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

  // ── 封面（SteamGridDB + 官方图兜底）───────────────────────
  const toast = useToast();
  const [imgErrors, setImgErrors] = useState(() => new Set()); // img 加载失败的卡片 id，回退首字母
  const [autoCover, setAutoCover] = useState(true);            // 批量导入自动抓封面（默认开）
  const [coverProgress, setCoverProgress] = useState(null);    // 后台抓取进度
  const [hasKey, setHasKey] = useState(false);                 // 已配置 SGDB Key
  const [showKeyDlg, setShowKeyDlg] = useState(false);
  const [keyInput, setKeyInput] = useState("");
  const [coverTerm, setCoverTerm] = useState("");              // 编辑弹窗搜索词
  const [coverSearching, setCoverSearching] = useState(false);
  const [coverCandidates, setCoverCandidates] = useState([]);  // SGDB 候选游戏
  const [coverGameId, setCoverGameId] = useState(null);
  const [coverThumbs, setCoverThumbs] = useState([]);          // 候选封面缩略图
  const [coverUploading, setCoverUploading] = useState(false);
  const [showCoverPicker, setShowCoverPicker] = useState(false); // 独立选图弹窗

  const coverActive = !!coverProgress && (coverProgress.running > 0 ||
    (coverProgress.total > 0 && coverProgress.done + coverProgress.failed < coverProgress.total));

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

  // 下拉框选项归一化：把任意形式的配置 id（裸名 "gaming"/带前缀 "cfg-gaming"/用户配置 id）
  // 映射到选项列表中真实存在的 id。老配置的 defaultMode 是裸名（如 "gaming"），而选项全是
  // cfg- id——select 的 value 不在选项里时浏览器会静默显示第一个选项（"安静"），
  // 造成「扫描窗显示 A、导入实际存 B、编辑框又显示 A」的三方不一致。
  const profileOptionList = () =>
    configProfiles.length
      ? configProfiles
      : MODES.map(m => ({ id: m.id, name: m.label, builtIn: true, thermalMode: m.id }));
  const normalizeOptionId = (id) => {
    const list = profileOptionList();
    if (!id || list.length === 0) return list[0]?.id || id;
    if (list.some(p => p.id === id)) return id;
    const bare = id.replace(/^cfg-/, "");
    const byThermal = list.find(p => (p.thermalMode || p.id) === bare);
    return byThermal ? byThermal.id : (list[0]?.id || id);
  };

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
      setImgErrors(new Set()); // 数据刷新后清除失败标记（封面可能已补上）
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
      setAddForm({ name: "", exePath: "", targetMode: normalizeOptionId(config.defaultMode) });
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
      closeEditDialog();
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
      setBatchTargetMode(normalizeOptionId(config.defaultMode));
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
      targetMode: normalizeOptionId(batchTargetMode),
      source: scanResults[i].source,
      appId: scanResults[i].appId || "",
    }));
    if (games.length === 0) {
      setShowScan(false);
      return;
    }
    try {
      const res = await fetch("/api/game-profiles/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ games, autoCover }),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        toast(err.error || "批量添加失败", "error");
        return;
      }
      const data = await res.json();
      setShowScan(false);
      setSelectedGames(new Set());
      setScanResults([]);
      fetchData();
      if (data.coverQueued) {
        toast(`成功添加 ${data.added || games.length} 个游戏，${data.coverQueued} 张封面后台获取中`, "success");
        setTimeout(pollCoverProgress, 400);
      } else {
        toast(`成功添加 ${data.added || games.length} 个游戏`, "success");
      }
    } catch (err) {
      console.error("Failed to batch add:", err);
      toast("批量添加失败", "error");
    }
  };

  // ── 封面操作 ─────────────────────────────────────────────

  const loadKeyStatus = async () => {
    try {
      const r = await fetch("/api/game-covers/opts");
      if (r.ok) {
        const v = !!(await r.json()).hasKey;
        setHasKey(v);
        return v;
      }
    } catch { /* 后端未就绪/离线 */ }
    return null; // 状态未知——调用方不应据此覆盖已有状态
  };

  const pollCoverProgress = async () => {
    try {
      const r = await fetch("/api/game-covers/progress");
      if (r.ok) setCoverProgress(await r.json());
    } catch { /* offline */ }
  };

  const fetchMissingCovers = async () => {
    try {
      const res = await fetch("/api/game-covers/fetch-missing", { method: "POST" });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        toast(err.error || "补齐封面失败", "error");
        return;
      }
      const data = await res.json();
      if (data.skippedDead) {
        toast(`${data.skippedDead} 条规则的游戏文件已不存在（可能已卸载），已跳过——建议删除规则或编辑更新路径`, "info");
      }
      if (!data.queued) {
        if (!data.skippedDead) toast("所有卡片都已有封面", "info");
        return;
      }
      toast(`开始获取 ${data.queued} 张封面`, "success");
      pollCoverProgress();
    } catch {
      toast("补齐封面失败", "error");
    }
  };

  const fetchSingleCover = async (id) => {
    try {
      const res = await fetch("/api/game-covers/fetch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ profileIds: [id], force: true }),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        toast(err.error || "获取封面失败", "error");
        return;
      }
      toast("后台获取中，稍候自动刷新", "success");
      pollCoverProgress();
    } catch {
      toast("获取封面失败", "error");
    }
  };

  const openKeyDlg = async () => {
    await loadKeyStatus(); // 实时读取，弹窗内"已配置/未配置"不使用过期状态
    setKeyInput("");
    setShowKeyDlg(true);
  };

  const saveKey = async () => {
    const key = keyInput.trim();
    if (!key) { setShowKeyDlg(false); return; } // 空输入一律视为无操作——绝不误清 Key（清除走「清除 Key」按钮）
    try {
      const res = await fetch("/api/game-covers/opts", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sgdbKey: key }),
      });
      if (res.ok) {
        const d = await res.json();
        setHasKey(d.hasKey);
        setShowKeyDlg(false);
        toast(d.hasKey ? "SteamGridDB Key 已保存" : "SteamGridDB Key 已清除", "success");
      } else {
        const err = await res.json().catch(() => ({}));
        toast(err.error || "保存失败", "error");
      }
    } catch {
      toast("保存失败", "error");
    }
  };

  const clearKey = async () => {
    try {
      const res = await fetch("/api/game-covers/opts", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sgdbKey: "" }),
      });
      if (res.ok) {
        setHasKey(false);
        setShowKeyDlg(false);
        toast("SteamGridDB Key 已清除", "success");
      }
    } catch { toast("操作失败", "error"); }
  };

  const uploadCover = (file) => {
    if (!file || !editingId) return;
    if (file.size > 8 * 1024 * 1024) { toast("图片不能超过 8MB", "error"); return; }
    setCoverUploading(true);
    const reader = new FileReader();
    reader.onload = async () => {
      try {
        const res = await fetch(`/api/game-covers/${editingId}/image`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ dataUrl: reader.result }),
        });
        const d = await res.json().catch(() => ({}));
        if (res.ok) { toast("封面已更新", "success"); fetchData(); }
        else toast(d.error || "上传失败", "error");
      } catch {
        toast("上传失败", "error");
      } finally {
        setCoverUploading(false);
      }
    };
    reader.onerror = () => { setCoverUploading(false); toast("读取文件失败", "error"); };
    reader.readAsDataURL(file);
  };

  const resetCover = async () => {
    if (!editingId) return;
    if (!confirm("确定恢复默认（移除封面图片）？")) return;
    try {
      const res = await fetch(`/api/game-covers/${editingId}/image`, { method: "DELETE" });
      if (res.ok) { toast("已恢复默认", "success"); fetchData(); }
      else toast("操作失败", "error");
    } catch {
      toast("操作失败", "error");
    }
  };

  const searchCovers = async (termArg) => {
    const term = (typeof termArg === "string" ? termArg : coverTerm).trim();
    if (!term || !editingId) return;
    setCoverTerm(term);
    setCoverSearching(true);
    setCoverCandidates([]);
    setCoverThumbs([]);
    setCoverGameId(null);
    try {
      const res = await fetch(`/api/game-covers/search?term=${encodeURIComponent(term)}`);
      const d = await res.json().catch(() => ({}));
      if (!res.ok) { toast(d.error || "搜索失败", "error"); return; }
      if (!d.data?.length) { toast("未找到匹配游戏，可尝试英文名", "info"); return; }
      setCoverCandidates(d.data);
      setCoverGameId(d.data[0].id);
      loadCoverThumbs(d.data[0].id);
    } catch {
      toast("搜索失败", "error");
    } finally {
      setCoverSearching(false);
    }
  };

  const loadCoverThumbs = async (gid) => {
    if (!gid || !editingId) return;
    setCoverThumbs([]);
    try {
      const res = await fetch(`/api/game-covers/candidates?gameId=${gid}`);
      const d = await res.json().catch(() => ({}));
      if (!res.ok) { toast(d.error || "获取候选封面失败", "error"); return; }
      setCoverThumbs(d.data || []);
      if (!d.data?.length) toast("该游戏暂无可用竖版封面", "info");
    } catch {
      toast("获取候选封面失败", "error");
    }
  };

  const applyThumb = async (t) => {
    if (!editingId || !t?.url) return;
    try {
      const res = await fetch(`/api/game-covers/${editingId}/select`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url: t.url }),
      });
      const d = await res.json().catch(() => ({}));
      if (res.ok) { toast("封面已应用", "success"); fetchData(); }
      else toast(d.error || "应用失败", "error");
    } catch {
      toast("应用失败", "error");
    }
  };

  // 封面进度轮询: 有活动任务时每 1.5s 拉一次
  useEffect(() => {
    if (!coverActive) return;
    const t = setInterval(pollCoverProgress, 1500);
    return () => clearInterval(t);
  }, [coverActive]);

  // 任务从进行中 → 完成的瞬间: 刷新列表 + 汇总提示
  const prevCoverActiveRef = useRef(false);
  useEffect(() => {
    if (prevCoverActiveRef.current && !coverActive && coverProgress && coverProgress.total > 0) {
      fetchData();
      if (coverProgress.failed > 0)
        toast(`封面完成：成功 ${coverProgress.done} 个，失败 ${coverProgress.failed} 个（未匹配的游戏可在编辑中手动搜索）`, "info");
      else
        toast(`封面已就绪：${coverProgress.done} 个`, "success");
    }
    prevCoverActiveRef.current = coverActive;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [coverActive]);

  // 关闭编辑弹窗的唯一出口：必须同时收起选图弹窗，否则 showCoverPicker 残留会让
  // 下一次点「编辑」时直接弹出上一条规则的选图窗（看起来像跳到了别的规则的编辑页）
  const closeEditDialog = () => {
    setEditingId(null);
    setShowCoverPicker(false);
  };

  const openCoverPicker = async () => {
    const v = await loadKeyStatus(); // 打开时实时读取，避免挂载期的过期状态误判"未配置"
    setCoverTerm(editingProfile?.name || "");
    setCoverCandidates([]);
    setCoverThumbs([]);
    setCoverGameId(null);
    setShowCoverPicker(true);
    if (v && editingProfile?.name) searchCovers(editingProfile.name); // 打开即自动搜一次
  };

  // 挂载时读取 Key 状态；开机自启最小化场景 API 可能尚未就绪，失败自动重试几次
  useEffect(() => {
    let disposed = false;
    const boot = async () => {
      for (let i = 0; i < 5 && !disposed; i++) {
        const v = await loadKeyStatus();
        if (disposed || v !== null) return;
        await new Promise(r => setTimeout(r, 2000));
      }
    };
    boot();
    return () => { disposed = true; };
  }, []);

  const activeCount = profiles.filter(p => p.enabled).length;
  const editingProfile = profiles.find(p => p.id === editingId);

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
        <button className="btn" onClick={fetchMissingCovers} disabled={coverActive}
          title="为所有缺封面的游戏自动获取封面（Steam 来源无需 Key 即可使用官方图）">
          {coverActive && coverProgress
            ? `补齐中 ${coverProgress.done + coverProgress.failed}/${coverProgress.total}…`
            : "补齐封面"}
        </button>
        <button className="btn" onClick={scanGames} disabled={scanning}>
          {scanning ? ICONS.search : ICONS.search}扫描游戏
        </button>
        <button className="btn primary" onClick={() => { setShowAdd(true); setAddForm({ name: "", exePath: "", targetMode: normalizeOptionId(config.defaultMode) }); }}>
          {ICONS.plus}添加规则
        </button>
        <button className="btn" onClick={openKeyDlg} title="SteamGridDB API Key 设置" aria-label="封面图源设置">
          {ICONS.gear}
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
              onClick={() => { setShowAdd(true); setAddForm({ name: "", exePath: "", targetMode: normalizeOptionId(config.defaultMode) }); }}
            >
              {ICONS.plus}添加规则
            </button>
          </>}
        />
      ) : (
        <div className="game-grid reveal enter" style={{ animationDelay: ".06s" }}>
          {profiles.map(p => (
            <div key={p.id} className={`gcard ${p.enabled ? "" : "off"}`}>
              <div className="poster" style={p.hasCover && !imgErrors.has(p.id) ? undefined : { background: `linear-gradient(135deg, ${targetColor(p.targetMode)}66, #1a1a2e)` }}>
                {p.hasCover && !imgErrors.has(p.id) ? (
                  <img
                    className="poster-img"
                    src={p.coverUrl}
                    alt={p.name}
                    loading="lazy"
                    onError={() => setImgErrors(prev => { const n = new Set(prev); n.add(p.id); return n; })}
                  />
                ) : (
                  <span className="poster-letter">{initials(p.name)}</span>
                )}
              </div>
              <span className="shade"></span>
              <div className="top">
                <span className="cfg"><span className="dot" style={{ background: targetColor(p.targetMode) }}></span>{targetLabel(p.targetMode)}</span>
                {p.exeMissing && <span className="miss-tag" title="游戏文件不存在，可能已卸载——可删除此规则或编辑更新路径">失效</span>}
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
                    onClick={() => { setEditingId(p.id); setEditForm({ name: p.name, exePath: p.exePath, targetMode: p.targetMode, enabled: p.enabled }); setShowCoverPicker(false); }}>
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
        手动添加的非平台游戏（本地导入）不参与「补齐封面」批量获取——可在<b>编辑弹窗</b>中按英文名搜索（需配置 SteamGridDB Key）或直接上传本地图片；游戏已卸载的规则会标「失效」并可删除。
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
                  value={normalizeOptionId(addForm.targetMode)}
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
              <button className="x" onClick={closeEditDialog} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              <div className="field">
                <label>封面 <small>SteamGridDB 搜索 · 本地图片 · 恢复默认</small></label>
                <div className="cover-manage">
                  <div className="cover-preview">
                    {editingProfile?.hasCover
                      ? <img src={editingProfile.coverUrl} alt="当前封面" />
                      : <span className="cover-letter">{initials(editingProfile?.name)}</span>}
                  </div>
                  <div className="cover-ops">
                    <button className="btn" type="button" onClick={() => fetchSingleCover(editingId)}>
                      {ICONS.image}获取封面
                    </button>
                    <button className="btn" type="button" onClick={openCoverPicker}>
                      {ICONS.search}搜索封面
                    </button>
                    <label className="btn" type="button">
                      {coverUploading ? "上传中…" : "本地图片"}
                      <input type="file" accept="image/*" hidden onChange={(e) => { uploadCover(e.target.files?.[0]); e.target.value = ""; }} />
                    </label>
                    <button className="btn ghost" type="button" onClick={resetCover} disabled={!editingProfile?.hasCover}>
                      恢复默认
                    </button>
                  </div>
                </div>
                {!hasKey && (
                  <div className="cover-search-tip">
                    配置 SteamGridDB API Key 后可搜索候选封面（Steam 来源的游戏无需 Key 也能获取官方封面） ·
                    <button className="link-btn" type="button" onClick={openKeyDlg}>去设置</button>
                  </div>
                )}
              </div>
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
                  value={normalizeOptionId(editForm.targetMode)}
                  onChange={(e) => setEditForm({ ...editForm, targetMode: e.target.value })}
                >
                  {profileOptions()}
                </select>
              </div>
            </div>
            <div className="p-foot">
              <button className="btn ghost" onClick={closeEditDialog}>取消</button>
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
                    <select value={normalizeOptionId(batchTargetMode)} onChange={(e) => setBatchTargetMode(e.target.value)} aria-label="批量绑定配置">
                      {profileOptions()}
                    </select>
                    <label className="auto-cover-check" title="导入后在后台自动搜索并下载封面（Steam 来源无需 API Key）">
                      <input type="checkbox" checked={autoCover} onChange={(e) => setAutoCover(e.target.checked)} />
                      自动获取封面
                    </label>
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
      {showCoverPicker && editingId && (
        <div className="modal show">
          <div className="panel" style={{ width: "min(760px, 94vw)" }}>
            <div className="p-head">
              <span><b>选择封面</b><small>{editingProfile?.name} · SteamGridDB</small></span>
              <button className="x" onClick={() => setShowCoverPicker(false)} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              {!hasKey ? (
                <div className="cover-search-tip">
                  未配置 SteamGridDB API Key，无法搜索候选封面。Steam 来源的游戏可直接用「获取封面」拿官方图；
                  其他游戏配置 Key 后搜索，或返回编辑弹窗上传本地图片 ·
                  <button className="link-btn" type="button" onClick={() => { setShowCoverPicker(false); openKeyDlg(); }}>去设置</button>
                </div>
              ) : (
                <>
                  <div className="cover-search">
                    <div className="in">
                      <input
                        type="text"
                        value={coverTerm}
                        onChange={(e) => setCoverTerm(e.target.value)}
                        onKeyDown={(e) => { if (e.key === "Enter") searchCovers(); }}
                        placeholder="搜索 SteamGridDB 游戏名（英文更准）"
                        aria-label="搜索封面关键词"
                      />
                      <button className="btn" type="button" onClick={() => searchCovers()} disabled={coverSearching}>
                        {coverSearching ? "搜索中…" : "搜索"}
                      </button>
                    </div>
                    {coverCandidates.length > 0 && (
                      <select
                        value={coverGameId ?? ""}
                        onChange={(e) => { setCoverGameId(e.target.value); loadCoverThumbs(e.target.value); }}
                        aria-label="候选游戏"
                      >
                        {coverCandidates.map(g => (
                          <option key={g.id} value={g.id}>{g.name}{g.verified ? " ✓" : ""}</option>
                        ))}
                      </select>
                    )}
                    {coverThumbs.length > 0 ? (
                      <div className="cover-thumbs lg">
                        {coverThumbs.map(t => (
                          <button key={t.id} type="button" className="cover-thumb" onClick={() => applyThumb(t)}
                            title={`风格 ${t.style || "默认"} · 评分 ${t.score}（点击应用）`}>
                            <img src={t.thumb} alt="" loading="lazy" />
                          </button>
                        ))}
                      </div>
                    ) : (
                      !coverSearching && coverGameId != null && (
                        <p className="cover-empty">该游戏暂无可用竖版封面，可换一个候选或上传本地图片</p>
                      )
                    )}
                  </div>
                </>
              )}
            </div>
            <div className="p-foot">
              <span style={{ flex: 1 }}></span>
              <button className="btn primary" onClick={() => setShowCoverPicker(false)}>完成</button>
            </div>
          </div>
        </div>
      )}

      {showKeyDlg && (
        <div className="modal show">
          <div className="panel">
            <div className="p-head">
              <span><b>SteamGridDB API Key</b><small>用于搜索与下载游戏封面（Epic / 手动添加的游戏需要）</small></span>
              <button className="x" onClick={() => setShowKeyDlg(false)} aria-label="关闭">{ICONS.close}</button>
            </div>
            <div className="p-body">
              <div className="field">
                <label>API Key <small>{hasKey ? "已配置 ✓" : "未配置"}</small></label>
                <input
                  type="text"
                  value={keyInput}
                  onChange={(e) => setKeyInput(e.target.value)}
                  placeholder={hasKey ? "粘贴新 Key 将覆盖现有配置" : "粘贴 SteamGridDB 的 API Key"}
                  aria-label="SteamGridDB API Key"
                />
              </div>
              <div className="hint">
                {ICONS.info}
                免费 Key 在 <b>steamgriddb.com</b> 的「偏好设置 → API」页面生成（需注册账号）。
                Key 仅保存在本机配置目录，不会上传到任何服务器。未配置 Key 时，Steam 来源的游戏仍会使用官方封面。
              </div>
            </div>
            <div className="p-foot">
              {hasKey && <button className="btn ghost" onClick={clearKey}>清除 Key</button>}
              <span style={{ flex: 1 }}></span>
              <button className="btn ghost" onClick={() => setShowKeyDlg(false)}>取消</button>
              <button className="btn primary" onClick={saveKey}>保存</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
