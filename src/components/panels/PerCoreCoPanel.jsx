import { useEffect, useRef, useState } from "react";
import { fetchPerCoreCo, applyPerCoreCo, probePerCoreCo } from "../../services/uxtuAdapter";

const FALLBACK_TOPOLOGY = { ccdCount: 2, coresPerCcd: 8 };
const FALLBACK_LIMITS = { minMv: -30, maxMv: 0 };

// 后端是唯一权威源：用持久化值铺满全部核心，未配置的核心为 0。
// 下发时也始终传全量（含 0），否则上一轮设过、本轮改回 0 的核心会残留旧值。
function buildDraft(topology, offsets) {
  const saved = new Map();
  (offsets || []).forEach(e => saved.set(`${e.ccd}:${e.core}`, Number(e.mv) || 0));
  const draft = [];
  for (let ccd = 0; ccd < topology.ccdCount; ccd++) {
    for (let core = 0; core < topology.coresPerCcd; core++) {
      draft.push({ ccd, core, mv: saved.get(`${ccd}:${core}`) ?? 0 });
    }
  }
  return draft;
}

const TONES = {
  ok: "var(--ok)",
  warn: "var(--warn, var(--fg-3))",
  error: "var(--danger)",
};

export default function PerCoreCoPanel({ settings, overrides, saveOverride, clearOverride, switching }) {
  const mode = settings.mode;
  const [topology, setTopology] = useState(FALLBACK_TOPOLOGY);
  const [limits, setLimits] = useState(FALLBACK_LIMITS);
  const [draft, setDraft] = useState(() => buildDraft(FALLBACK_TOPOLOGY, []));
  const [supported, setSupported] = useState(true);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [batch, setBatch] = useState(-10);
  const [status, setStatus] = useState(null);
  const [lastResults, setLastResults] = useState(null);

  const perCoreActive = Array.isArray(overrides?.perCoreCo) && overrides.perCoreCo.length > 0;
  const coAllActive = Object.prototype.hasOwnProperty.call(overrides || {}, "cpuVoltageOffset");
  const disabled = !!switching || busy || !supported || !editing;
  const switchDisabled = !!switching || busy || !supported || coAllActive;
  const wasActiveRef = useRef(perCoreActive);

  useEffect(() => {
    if (perCoreActive) setEditing(true);
    else if (wasActiveRef.current) setEditing(false);
    wasActiveRef.current = perCoreActive;
  }, [perCoreActive]);

  // 只在「逐核配置签名」变化时重拉，而不是跟随整个 overrides 对象：
  // 其它面板每拖一次滑块都会产生新的 overrides 引用，那会把本面板未应用的草稿冲掉。
  // 恢复默认 / 导入备份 / 切换模式都会改变签名，因此草稿能跟着后端回到正确状态。
  const perCoreSignature = JSON.stringify(overrides?.perCoreCo ?? []);
  const lastModeRef = useRef(mode);

  useEffect(() => {
    if (switching) return undefined; // 模式切换期间不拉取，避免拿到上一个配置的值
    const modeChanged = lastModeRef.current !== mode;
    lastModeRef.current = mode;
    let cancelled = false;
    (async () => {
      try {
        const info = await fetchPerCoreCo();
        if (cancelled) return;
        const topo = info.topology || FALLBACK_TOPOLOGY;
        setTopology(topo);
        setLimits(info.limits || FALLBACK_LIMITS);
        setSupported(info.supported !== false);
        setDraft(buildDraft(topo, info.perCoreCo));
        // 只在新模式时清空提示；应用后因签名变化触发的重拉要保留成功回执
        if (modeChanged) {
          setEditing(Array.isArray(info.perCoreCo) && info.perCoreCo.length > 0);
          setStatus(null);
          setLastResults(null);
        }
      } catch (e) {
        if (!cancelled) setStatus({ tone: "warn", text: "读取逐核配置失败：" + e.message });
      }
    })();
    return () => { cancelled = true; };
  }, [mode, switching, perCoreSignature]);

  function setCore(ccd, core, mv) {
    setDraft(prev => prev.map(e => (e.ccd === ccd && e.core === core ? { ...e, mv } : e)));
  }

  async function handleApply() {
    if (coAllActive) {
      setStatus({ tone: "warn", text: "全核「电压调节 · 降压」与逐核心降压互斥，请先清除全核覆盖" });
      return;
    }
    const offsets = draft.map(e => ({ ...e }));
    if (offsets.every(e => e.mv === 0)) {
      setStatus({ tone: "warn", text: "全部核心都是 0，无需应用；若要复位请点「全部归零」" });
      return;
    }
    setBusy(true);
    setStatus(null);
    try {
      const res = await applyPerCoreCo(offsets, mode);
      setLastResults(res.results || []);
      if (res.ok) {
        saveOverride?.(mode, "perCoreCo", offsets);
        setStatus({ tone: "ok", text: `已应用 ${res.applied}/${res.total} 核，配置已保存到当前模式` });
      } else {
        setStatus({ tone: "error", text: res.warning || res.error || "部分核心未通过 SMU 状态检查，配置未保存" });
      }
    } catch (e) {
      setStatus({ tone: "error", text: "下发失败：" + e.message });
    } finally {
      setBusy(false);
    }
  }

  async function handleClear() {
    if (!perCoreActive) {
      setDraft(buildDraft(topology, []));
      setLastResults(null);
      setStatus({ tone: "warn", text: "已清除未应用的逐核草稿" });
      return;
    }
    setBusy(true);
    setStatus(null);
    try {
      // /api/overrides/clear 会同时清配置并逐核写 0，避免重复下发 16 条命令
      await clearOverride?.(mode, ["perCoreCo"]);
      setDraft(buildDraft(topology, []));
      setLastResults(null);
      setEditing(false);
      setStatus({ tone: "ok", text: "已逐核写 0 复位并清除当前模式的逐核配置" });
    } catch (e) {
      setStatus({ tone: "error", text: "复位失败：" + e.message });
    } finally {
      setBusy(false);
    }
  }

  async function handleProbe() {
    setBusy(true);
    try {
      const res = await probePerCoreCo();
      const hex = Number(res.status ?? 0).toString(16).toUpperCase().padStart(2, "0");
      setStatus(res.ok
        ? { tone: "ok", text: `只读探针通过：RSMU 0xD5 状态 0x${hex}（命令可达，不代表降压已生效）` }
        : { tone: "warn", text: `只读探针未通过：状态 0x${hex}${res.error ? " · " + res.error : ""}（BIOS 可能未开放该命令）` });
    } catch (e) {
      setStatus({ tone: "error", text: "探针失败：" + e.message });
    } finally {
      setBusy(false);
    }
  }

  async function handleToggle() {
    if (switchDisabled) return;
    if (editing || perCoreActive) {
      if (perCoreActive) {
        await handleClear();
      } else {
        setEditing(false);
        setDraft(buildDraft(topology, []));
        setLastResults(null);
        setStatus(null);
      }
      return;
    }
    setEditing(true);
    setStatus({ tone: "warn", text: "逐核降压编辑已打开；设置完成后点「应用逐核设置」才会写入硬件。" });
  }

  const failed = (lastResults || []).filter(r => r.status !== 1);
  const stateLabel = perCoreActive ? "已启用" : editing ? "待应用" : "未启用";
  const stateColor = perCoreActive ? "var(--primary)" : editing ? "var(--warn, var(--fg-3))" : "var(--fg-3)";

  return (
    <div className="override-slider" style={{ marginTop: 18 }}>
      <div className="switch-row" style={{ borderTop: "1px solid var(--stroke)", paddingTop: 14 }}>
        <span style={{ display: "flex", flexDirection: "column", gap: 3 }}>
          <span className="k">逐核心降压</span>
          <span style={{ fontSize: 11, color: "var(--fg-3)" }}>逐核调节负偏移，与全核降压互斥</span>
        </span>
        <span style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <span style={{ fontSize: 12, color: stateColor }}>{stateLabel}</span>
          <button
            className={"switch-track" + (editing || perCoreActive ? " on" : " off")}
            type="button"
            disabled={switchDisabled}
            onClick={handleToggle}
            aria-label={(editing || perCoreActive ? "关闭" : "打开") + "逐核心降压"}
          >
            <span className="thumb"></span>
          </button>
        </span>
      </div>

      {!supported && (
        <p style={{ fontSize: 12, color: "var(--danger)", marginBottom: 10 }}>当前平台不支持逐核心降压（仅 AMD 锐龙）。</p>
      )}
      {coAllActive && (
        <p style={{ fontSize: 12, color: "var(--danger)", marginBottom: 10 }}>
          已检测到全核「电压调节 · 降压」覆盖，请先清除它再应用逐核心降压。
        </p>
      )}

      {editing && (
        <div>
          <p style={{ fontSize: 11, color: "var(--fg-3)", margin: "2px 0 10px" }}>
            CCD 0 / 1 · 每列 8 核；编号不等同于 Windows 逻辑处理器
          </p>
          <div style={{ display: "grid", gridTemplateColumns: `repeat(${topology.ccdCount}, minmax(0, 1fr))`, gap: 14 }}>
            {Array.from({ length: topology.ccdCount }, (_, ccd) => (
              <div key={ccd}>
                <div style={{ fontSize: 12, color: "var(--fg-2)", marginBottom: 8 }}>CCD {ccd}</div>
                {draft.filter(e => e.ccd === ccd).map(e => (
                  <div className="slider-group" key={e.core} style={{ marginBottom: 9 }}>
                    <div className="slider-label">
                      <span className="k">核心 {e.core}</span>
                      <span className="v">{e.mv}<span className="u">mV</span></span>
                    </div>
                    <input
                      type="range" className="slider-track"
                      min={limits.minMv} max={limits.maxMv} step={1}
                      value={e.mv} disabled={disabled}
                      aria-label={`CCD ${e.ccd} 核心 ${e.core} 电压偏移`}
                      onChange={ev => setCore(e.ccd, e.core, Number(ev.target.value))}
                    />
                  </div>
                ))}
              </div>
            ))}
          </div>

          <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 6 }}>
            <span style={{ fontSize: 12, color: "var(--fg-3)", flexShrink: 0 }}>批量填入</span>
            <input
              type="range" className="slider-track" style={{ flex: 1 }}
              min={limits.minMv} max={limits.maxMv} step={1}
              value={batch} disabled={disabled}
              aria-label="批量填入电压偏移"
              onChange={ev => setBatch(Number(ev.target.value))}
            />
            <span className="v" style={{ minWidth: 46, textAlign: "right" }}>{batch}<span className="u">mV</span></span>
            <button
              type="button" className="btn" disabled={disabled}
              onClick={() => setDraft(prev => prev.map(e => ({ ...e, mv: batch })))}
              style={{ padding: "5px 10px", fontSize: 12 }}
            >填入全部</button>
          </div>

          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 12 }}>
            <button type="button" className="btn primary" disabled={disabled || coAllActive} onClick={handleApply} style={{ padding: "7px 12px", fontSize: 12 }}>
              {busy ? "处理中…" : "应用逐核设置"}
            </button>
            <button type="button" className="btn" disabled={disabled} onClick={handleClear} style={{ padding: "7px 12px", fontSize: 12 }}>全部归零</button>
            <button type="button" className="btn ghost" disabled={disabled} onClick={handleProbe} style={{ padding: "7px 12px", fontSize: 12 }}>只读探测</button>
          </div>
        </div>
      )}

      {status && (
        <p style={{ fontSize: 12, color: TONES[status.tone] || "var(--fg-3)", marginTop: 10, lineHeight: 1.5 }}>{status.text}</p>
      )}
      {lastResults?.length > 0 && (
        <p style={{ fontSize: 11, color: "var(--fg-3)", marginTop: 6, lineHeight: 1.5 }}>
          SMU 回执：{lastResults.length - failed.length}/{lastResults.length} 成功
          {failed.length > 0 && ` · 未通过 ${failed.map(r => `CCD${r.ccd}/C${r.core}(0x${Number(r.status).toString(16).toUpperCase()})`).join("、")}`}
        </p>
      )}
    </div>
  );
}
