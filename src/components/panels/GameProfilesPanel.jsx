import { useState, useEffect } from "react";
import Card from "../ui/Card";

const MODES = [
  { id: "silent", label: "安静模式", color: "#4CAF50" },
  { id: "office", label: "均衡模式", color: "#2196F3" },
  { id: "beast", label: "野兽模式", color: "#FF9800" },
  { id: "gaming", label: "斗战模式", color: "#F44336" },
];

export default function GameProfilesPanel() {
  const [config, setConfig] = useState({ enabled: true, defaultMode: "gaming" });
  const [profiles, setProfiles] = useState([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState(null);
  const [editForm, setEditForm] = useState({ name: "", exePath: "", exeName: "", targetMode: "gaming", enabled: true });
  const [showAddForm, setShowAddForm] = useState(false);
  const [addForm, setAddForm] = useState({ name: "", exePath: "", targetMode: "gaming" });
  
  // 扫描相关状态
  const [scanning, setScanning] = useState(false);
  const [showScanModal, setShowScanModal] = useState(false);
  const [scanResults, setScanResults] = useState([]);
  const [selectedGames, setSelectedGames] = useState(new Set());
  const [batchTargetMode, setBatchTargetMode] = useState("gaming");

  // 加载数据
  const fetchData = async () => {
    try {
      const res = await fetch("/api/game-profiles");
      const data = await res.json();
      setConfig({ enabled: data.enabled, defaultMode: data.defaultMode });
      setProfiles(data.profiles || []);
    } catch (err) {
      console.error("Failed to load game profiles:", err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchData(); }, []);

  // 更新全局配置
  const updateConfig = async (patch) => {
    try {
      const res = await fetch("/api/game-profiles/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(patch),
      });
      const data = await res.json();
      setConfig({ enabled: data.enabled, defaultMode: data.defaultMode });
    } catch (err) {
      console.error("Failed to update config:", err);
    }
  };

  // 添加规则
  const addProfile = async () => {
    if (!addForm.exePath || !addForm.name) return;
    // exeName 从路径自动提取，不依赖用户输入
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
      setShowAddForm(false);
      setAddForm({ name: "", exePath: "", targetMode: config.defaultMode });
      fetchData();
    } catch (err) {
      console.error("Failed to add profile:", err);
    }
  };

  // 更新规则
  const updateProfile = async (id) => {
    // exeName 从路径自动提取
    const exeName = editForm.exePath ? editForm.exePath.split(/[/\\]/).pop() : editForm.exeName;
    try {
      const res = await fetch(`/api/game-profiles/${id}`, {
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

  // 删除规则
  const deleteProfile = async (id) => {
    if (!confirm("确定删除此规则？")) return;
    try {
      await fetch(`/api/game-profiles/${id}`, { method: "DELETE" });
      fetchData();
    } catch (err) {
      console.error("Failed to delete profile:", err);
    }
  };

  // 文件选择
  const pickFile = async (setter) => {
    try {
      const res = await fetch("/api/game-profiles/file-pick");
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
    }
  };

  // 扫描游戏
  const scanGames = async () => {
    setScanning(true);
    try {
      const res = await fetch("/api/game-profiles/scan");
      const data = await res.json();
      setScanResults(data);
      setSelectedGames(new Set());
      setBatchTargetMode(config.defaultMode);
      setShowScanModal(true);
    } catch (err) {
      console.error("Failed to scan games:", err);
      alert("扫描失败，请稍后重试");
    } finally {
      setScanning(false);
    }
  };

  // 切换游戏选择
  const toggleGameSelection = (index) => {
    setSelectedGames(prev => {
      const next = new Set(prev);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }
      return next;
    });
  };

  // 全选/取消全选
  const toggleSelectAll = () => {
    const available = scanResults.filter((_, i) => !scanResults[i].alreadyAdded);
    if (selectedGames.size === available.length) {
      setSelectedGames(new Set());
    } else {
      setSelectedGames(new Set(available.map((_, i) => scanResults.indexOf(available[i]))));
    }
  };

  // 批量添加
  const batchAddGames = async () => {
    const games = [...selectedGames].map(i => ({
      name: scanResults[i].name,
      exePath: scanResults[i].exePath,
      targetMode: batchTargetMode,
      source: scanResults[i].source,
    }));

    if (games.length === 0) {
      setShowScanModal(false);
      return;
    }

    try {
      const res = await fetch("/api/game-profiles/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ games }),
      });
      const data = await res.json();
      setShowScanModal(false);
      setSelectedGames(new Set());
      setScanResults([]);
      fetchData();
      alert(`成功添加 ${data.added} 个游戏`);
    } catch (err) {
      console.error("Failed to batch add:", err);
      alert("批量添加失败");
    }
  };

  if (loading) {
    return <Card title="游戏管理" className="!p-5"><p className="text-sm" style={{ color: "var(--muted)" }}>加载中...</p></Card>;
  }

  const modeLabel = (id) => MODES.find(m => m.id === id)?.label || id;

  return (
    <>
      {/* 全局配置 */}
      <Card title="游戏管理" className="!p-5">
        <div className="space-y-4">
          {/* 开关 + 默认模式 */}
          <div className="flex items-center justify-between flex-wrap gap-3">
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={config.enabled}
                onChange={(e) => updateConfig({ enabled: e.target.checked })}
                className="w-4 h-4 rounded"
              />
              <span className="text-sm font-medium">启用自动切换</span>
            </label>
            <div className="flex items-center gap-2">
              <span className="text-xs" style={{ color: "var(--muted)" }}>默认模式：</span>
              <select
                value={config.defaultMode}
                onChange={(e) => updateConfig({ defaultMode: e.target.value })}
                className="text-sm rounded-lg px-2 py-1"
                style={{ background: "var(--card-2)", border: "1px solid var(--border)", color: "var(--text)" }}
              >
                {MODES.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
              </select>
            </div>
          </div>

          {/* 规则列表 */}
          <div className="space-y-2">
            <p className="text-xs font-medium" style={{ color: "var(--muted)" }}>规则列表</p>
            {profiles.length === 0 ? (
              <p className="text-xs py-4 text-center" style={{ color: "var(--muted)" }}>
                暂无规则，请添加游戏或扫描
              </p>
            ) : (
              <div className="space-y-1">
                {profiles.map(p => (
                  <div key={p.id} className="rounded-lg p-3" style={{ background: "var(--card-2)", border: "1px solid var(--border)" }}>
                    {editingId === p.id ? (
                      // 编辑模式
                      <div className="space-y-2">
                        <div>
                          <label className="text-xs mb-1 block" style={{ color: "var(--muted)" }}>游戏可执行文件</label>
                          <div className="flex gap-2">
                            <input
                              type="text"
                              value={editForm.exePath}
                              onChange={(e) => setEditForm({ ...editForm, exePath: e.target.value })}
                              placeholder="可执行文件路径"
                              className="flex-1 text-sm rounded-lg px-2 py-1.5"
                              style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                            />
                            <button
                              onClick={() => pickFile(setEditForm)}
                              className="text-xs px-2 py-1.5 rounded-lg"
                              style={{ background: "var(--card)", color: "var(--text)", border: "1px solid var(--border)" }}
                            >浏览</button>
                          </div>
                        </div>
                        <input
                          type="text"
                          value={editForm.name}
                          onChange={(e) => setEditForm({ ...editForm, name: e.target.value })}
                          placeholder="游戏名称"
                          className="w-full text-sm rounded-lg px-2 py-1.5"
                          style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                        />
                        <select
                          value={editForm.targetMode}
                          onChange={(e) => setEditForm({ ...editForm, targetMode: e.target.value })}
                          className="w-full text-sm rounded-lg px-2 py-1"
                          style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                        >
                          {MODES.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
                        </select>
                        <div className="flex gap-2">
                          <button onClick={() => updateProfile(p.id)} className="text-xs px-3 py-1.5 rounded-lg" style={{ background: "var(--primary-2)", color: "#fff" }}>保存</button>
                          <button onClick={() => setEditingId(null)} className="text-xs px-3 py-1.5 rounded-lg" style={{ background: "var(--card)", color: "var(--text)", border: "1px solid var(--border)" }}>取消</button>
                        </div>
                      </div>
                    ) : (
                      // 显示模式
                      <div className="flex items-center justify-between gap-2">
                        <div className="flex items-center gap-2 min-w-0">
                          <span className={`w-2 h-2 rounded-full ${p.enabled ? "" : "opacity-50"}`} style={{ background: MODES.find(m => m.id === p.targetMode)?.color || "#888" }} />
                          <div className="min-w-0">
                            <p className="text-sm font-medium truncate">{p.name}</p>
                            <p className="text-xs truncate" style={{ color: "var(--muted)" }}>{p.exeName} → {modeLabel(p.targetMode)}</p>
                          </div>
                        </div>
                        <div className="flex items-center gap-1">
                          <button
                            onClick={() => { setEditingId(p.id); setEditForm({ name: p.name, exePath: p.exePath, exeName: p.exeName, targetMode: p.targetMode, enabled: p.enabled }); }}
                            className="text-xs px-2 py-1 rounded-lg"
                            style={{ background: "transparent", color: "var(--text)", border: "1px solid var(--border)" }}
                          >编辑</button>
                          <button
                            onClick={() => deleteProfile(p.id)}
                            className="text-xs px-2 py-1 rounded-lg"
                            style={{ background: "transparent", color: "var(--error)", border: "1px solid var(--error)" }}
                          >删除</button>
                        </div>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* 操作按钮 */}
          <div className="flex gap-2 flex-wrap">
            <button
              onClick={() => { setShowAddForm(true); setAddForm({ name: "", exePath: "", targetMode: config.defaultMode }); }}
              className="text-sm px-4 py-2 rounded-lg"
              style={{ background: "var(--primary-2)", color: "#fff" }}
            >+ 手动添加</button>
            <button
              onClick={scanGames}
              disabled={scanning}
              className="text-sm px-4 py-2 rounded-lg disabled:opacity-50"
              style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}
            >{scanning ? "扫描中..." : "扫描游戏"}</button>
          </div>
        </div>
      </Card>

      {/* 添加表单弹窗 */}
      {showAddForm && (
        <div className="fixed inset-0 flex items-center justify-center p-4" style={{ background: "rgba(0,0,0,0.5)", zIndex: 1000 }}>
          <div className="rounded-2xl p-5 w-full max-w-md" style={{ background: "var(--card)", border: "1px solid var(--border)" }}>
            <h3 className="text-sm font-medium mb-4">添加游戏规则</h3>
            <div className="space-y-3">
              {/* 第一步：选择可执行文件路径 */}
              <div>
                <label className="text-xs mb-1 block" style={{ color: "var(--muted)" }}>游戏可执行文件</label>
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={addForm.exePath}
                    onChange={(e) => {
                      const path = e.target.value;
                      const exeName = path.split(/[/\\]/).pop();
                      setAddForm(prev => ({
                        ...prev,
                        exePath: path,
                        name: prev.name || exeName.replace(/\.exe$/i, ""),
                      }));
                    }}
                    placeholder="选择或输入 .exe 路径"
                    className="flex-1 text-sm rounded-lg px-3 py-2"
                    style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                  />
                  <button
                    onClick={() => pickFile(setAddForm)}
                    className="text-sm px-3 py-2 rounded-lg whitespace-nowrap"
                    style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}
                  >浏览</button>
                </div>
              </div>
              {/* 第二步：游戏名称（自动填充，可修改） */}
              <div>
                <label className="text-xs mb-1 block" style={{ color: "var(--muted)" }}>游戏名称</label>
                <input
                  type="text"
                  value={addForm.name}
                  onChange={(e) => setAddForm({ ...addForm, name: e.target.value })}
                  placeholder="自动填充，可修改"
                  className="w-full text-sm rounded-lg px-3 py-2"
                  style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                />
              </div>
              {/* 第三步：目标模式 */}
              <div>
                <label className="text-xs mb-1 block" style={{ color: "var(--muted)" }}>切换到此模式</label>
                <select
                  value={addForm.targetMode}
                  onChange={(e) => setAddForm({ ...addForm, targetMode: e.target.value })}
                  className="w-full text-sm rounded-lg px-3 py-2"
                  style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                >
                  {MODES.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
                </select>
              </div>
            </div>
            <div className="flex gap-2 mt-5">
              <button onClick={addProfile} className="flex-1 text-sm px-4 py-2 rounded-lg" style={{ background: "var(--primary-2)", color: "#fff" }}>添加</button>
              <button onClick={() => setShowAddForm(false)} className="flex-1 text-sm px-4 py-2 rounded-lg" style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}>取消</button>
            </div>
          </div>
        </div>
      )}

      {/* 扫描结果弹窗 */}
      {showScanModal && (
        <div className="fixed inset-0 flex items-center justify-center p-4" style={{ background: "rgba(0,0,0,0.5)", zIndex: 1000 }}>
          <div className="rounded-2xl p-5 w-full max-w-lg max-h-[80vh] flex flex-col" style={{ background: "var(--card)", border: "1px solid var(--border)" }}>
            <h3 className="text-sm font-medium mb-3">扫描结果</h3>
            
            {/* 游戏列表 */}
            <div className="flex-1 overflow-y-auto space-y-1 min-h-0">
              {scanResults.length === 0 ? (
                <p className="text-sm text-center py-8" style={{ color: "var(--muted)" }}>未发现新游戏</p>
              ) : (
                scanResults.map((game, index) => (
                  <label
                    key={index}
                    className={`flex items-center gap-3 p-3 rounded-lg cursor-pointer ${game.alreadyAdded ? "opacity-50" : ""}`}
                    style={{ background: selectedGames.has(index) ? "var(--primary-2)" : "var(--card-2)", border: "1px solid var(--border)" }}
                  >
                    <input
                      type="checkbox"
                      checked={selectedGames.has(index)}
                      onChange={() => !game.alreadyAdded && toggleGameSelection(index)}
                      disabled={game.alreadyAdded}
                      className="w-4 h-4 rounded"
                    />
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium truncate">{game.name}</span>
                        <span className="text-xs px-1.5 py-0.5 rounded" style={{ background: game.source === "steam" ? "#1b2838" : "#2a2a2a", color: "#fff" }}>
                          {game.source === "steam" ? "Steam" : "Epic"}
                        </span>
                        {game.alreadyAdded && (
                          <span className="text-xs" style={{ color: "var(--muted)" }}>已添加</span>
                        )}
                      </div>
                      <p className="text-xs truncate" style={{ color: "var(--muted)" }}>{game.exePath}</p>
                    </div>
                  </label>
                ))
              )}
            </div>

            {/* 底部操作区 */}
            {scanResults.length > 0 ? (
              <div className="mt-4 pt-4" style={{ borderTop: "1px solid var(--border)" }}>
                <div className="flex items-center justify-between mb-3">
                  <button
                    onClick={toggleSelectAll}
                    className="text-xs px-3 py-1.5 rounded-lg"
                    style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}
                  >
                    {selectedGames.size === scanResults.filter(g => !g.alreadyAdded).length ? "取消全选" : "全选"}
                  </button>
                  <span className="text-xs" style={{ color: "var(--muted)" }}>
                    已选 {selectedGames.size} 个
                  </span>
                </div>
                <div className="flex items-center gap-2 mb-3">
                  <span className="text-xs" style={{ color: "var(--muted)" }}>目标模式：</span>
                  <select
                    value={batchTargetMode}
                    onChange={(e) => setBatchTargetMode(e.target.value)}
                    className="text-xs rounded-lg px-2 py-1"
                    style={{ background: "var(--bg)", border: "1px solid var(--border)", color: "var(--text)" }}
                  >
                    {MODES.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
                  </select>
                </div>
                <div className="flex gap-2">
                  <button
                    onClick={batchAddGames}
                    disabled={selectedGames.size === 0}
                    className="flex-1 text-sm px-4 py-2 rounded-lg disabled:opacity-50"
                    style={{ background: "var(--primary-2)", color: "#fff" }}
                  >添加选中</button>
                  <button
                    onClick={() => { setShowScanModal(false); setSelectedGames(new Set()); setScanResults([]); }}
                    className="flex-1 text-sm px-4 py-2 rounded-lg"
                    style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}
                  >取消</button>
                </div>
              </div>
            ) : (
              <div className="mt-4 pt-4 flex justify-end" style={{ borderTop: "1px solid var(--border)" }}>
                <button
                  onClick={() => { setShowScanModal(false); setSelectedGames(new Set()); setScanResults([]); }}
                  className="px-6 py-2 text-sm rounded-lg"
                  style={{ background: "var(--card-2)", color: "var(--text)", border: "1px solid var(--border)" }}
                >关闭</button>
              </div>
            )}
          </div>
        </div>
      )}
    </>
  );
}
