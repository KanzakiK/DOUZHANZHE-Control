import { useState, useRef, useEffect } from 'react';
import { createProfile, fetchOverrides, saveProfile, sortBuiltinProfiles } from '../../services/uxtuAdapter';

const MODE_LABELS = { silent: '安静', office: '均衡', gaming: '斗战', beast: '野兽' };

export default function ConfigBar({
  profiles, currentProfile, switching, settings,
  switchProfile, afterProfileCreated, onManageOpen,
}) {
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const dropdownRef = useRef(null);
  const inputRef = useRef(null);

  useEffect(() => { if (creating && inputRef.current) inputRef.current.focus(); }, [creating]);
  useEffect(() => {
    const h = (e) => { if (dropdownRef.current && !dropdownRef.current.contains(e.target)) setDropdownOpen(false); };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, []);

  // 配置列表尚未加载完成时（启动初期），currentProfile 为空，
  // 直接回退到 settings.mode 会把内部 id 暴露给用户（如 "cfg-office"）。
  // 内置配置 id 形如 cfg-<性能模式>，据此推导展示名；用户自建配置 id 即其名称。
  const fallbackProfileName = (id) => {
    if (!id) return '';
    const mode = String(id).replace(/^cfg-/, '');
    return MODE_LABELS[mode] || id;
  };

  const profileName = currentProfile?.name || fallbackProfileName(settings.mode);
  const isBuiltIn = currentProfile?.builtIn ?? true;
  // 绑定的性能模式裸名：从当前配置的 thermalMode 取（配置 id ≠ 性能模式名）
  const boundMode = currentProfile?.thermalMode || 'office';
  const modeLabel = MODE_LABELS[boundMode] || boundMode;
  const subtitle = isBuiltIn
    ? '内置配置 · 绑定散热模式：' + modeLabel
    : '用户自建配置 · 绑定散热模式：' + modeLabel;

  async function handleCreate() {
    if (!newName.trim()) return;
    try {
      const entry = await createProfile(newName.trim(), currentProfile?.thermalMode || 'office');
      try {
        const { overrides } = await fetchOverrides();
        if (overrides) await saveProfile(entry.id, overrides);
      } catch (e) { console.error('copy overrides to new profile failed:', e); }
      afterProfileCreated(entry);
      setCreating(false);
      setNewName('');
    } catch (e) { console.error('create profile failed:', e); }
  }

  const builtinProfiles = sortBuiltinProfiles(profiles.filter(p => p.builtIn));
  const userProfiles = profiles.filter(p => !p.builtIn);

  return (
    <div className="card config-bar reveal enter" style={{ animationDelay: '.02s' }}>
      <div className="cur">
        <span className="badge">{profileName.charAt(0).toUpperCase()}</span>
        <span className="info"><b>{profileName}</b><small>{subtitle}</small></span>
      </div>
      <div className="sel-wrap" ref={dropdownRef}>
        <button className="sel" onClick={() => !switching && setDropdownOpen(!dropdownOpen)} disabled={switching}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="15" height="15"><path d="M4 6h16M4 12h16M4 18h10"/></svg>
          切换配置
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="15" height="15"><path d="m6 9 6 6 6-6"/></svg>
        </button>
        {dropdownOpen && (
          <div className="config-dropdown">
            {builtinProfiles.map(p => (
              <button key={p.id} className={'config-dd-item' + (settings.mode === p.id ? ' active' : '')}
                onClick={() => { switchProfile(p.id); setDropdownOpen(false); }}>
                <span className="dd-name">{p.name}</span>
                <span className="dd-tag">内置</span>
              </button>
            ))}
            {userProfiles.length > 0 && <div className="config-dd-sep" />}
            {userProfiles.map(p => (
              <button key={p.id} className={'config-dd-item' + (settings.mode === p.id ? ' active' : '')}
                onClick={() => { switchProfile(p.id); setDropdownOpen(false); }}>
                <span className="dd-name">{p.name}</span>
                <span className="dd-tag user">用户</span>
              </button>
            ))}
          </div>
        )}
      </div>
      <div className="actions">
        {creating ? (
          <div className="create-inline">
            <input ref={inputRef} type="text" className="config-name-input" placeholder="输入配置名称"
              value={newName} onChange={e => setNewName(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') handleCreate(); if (e.key === 'Escape') { setCreating(false); setNewName(''); } }} />
            <button className="btn primary" onClick={handleCreate}>创建</button>
            <button className="btn" onClick={() => { setCreating(false); setNewName(''); }}>取消</button>
          </div>
        ) : (
          <>
            <button className="btn" onClick={() => setCreating(true)}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="16" height="16"><path d="M12 5v14M5 12h14"/></svg>
              另存为新配置
            </button>
            <button className="btn" onClick={onManageOpen}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" width="16" height="16"><path d="M4 6h16M9 6V4h6v2M6 6l1 14h10l1-14"/></svg>
              管理配置
            </button>
          </>
        )}
      </div>
    </div>
  );
}
