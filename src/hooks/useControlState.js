import { useCallback, useEffect, useSyncExternalStore } from "react";
import {
  createTelemetrySocket, FULL_PARAMS, MODE_FAN_DEFAULTS,
  fetchOverrides, switchMode, syncOverrides, log,
  migrateLocalStorageOverrides, flattenBackendOverrides,
  fetchTelemetry, fetchUiState, saveUiState,
  fetchProfiles, fetchPlatformInfo,
  clearOverrides,
  setMaxCoresForPercent,
  resolvePerfMode,
  refreshWallpaperOnRotate,
} from "../services/uxtuAdapter";

// 壁纸轮换：记录已处理的 bgLastRotated 戳，避免遥测帧（250ms）重复触发刷新
let _bgLastRotatedSeen = 0;

// ─────────────────────────────────────────────────────────────────────────────
// 共享 store：把原本「每次组件挂载各自实例化一份」的 useControlState
// 提升为模块级单例，配合 useSyncExternalStore 让所有 Tab 共享同一份数据。
//
// 设计要点（对应 docs/v2.0/tab-switch-shared-state.md Option A）：
//   1. 全进程只维护一份 state，所有组件订阅同一快照，切 Tab 秒开、不断线；
//   2. getSnapshot 返回同一个 state 引用（未变化时引用稳定，满足
//      useSyncExternalStore 不变量，避免无限重渲染）；
//   3. 后台任务（bootstrap / WebSocket / mock）懒启动：首个订阅者出现时开始，
//      订阅者清零时回收 —— 生产环境由常驻的 App 订阅保持常开，
//      测试环境经 renderHook 的 cleanup 卸载后自动回收，互不污染；
//   4. 对外 API（返回值）与改造前完全一致，5 个调用点零改动。
//
//   ⚠️ 保留原地解耦不变量（测试已锁定）：
//     - overrides 只表示“被用户改过的稀疏项”（UI 高亮/灰显）；
//       uxtuParams 才是完整生效值（FULL_PARAMS + 模式默认 + overrides 覆盖）。
//     - saveOverride 只更新 overrides，不连带 uxtuParams（写后端错峰）。
//     - 模式切换的竞态：过期 switchMode 响应被 _switchGen 丢弃，UI 停在最新模式。
// ─────────────────────────────────────────────────────────────────────────────

const MAX_HISTORY = 60;

function makeInitialState() {
  const fanDefaults = MODE_FAN_DEFAULTS["office"] || {};
  return {
    theme: (typeof document !== "undefined" && document.documentElement?.getAttribute("data-theme-mode")) || "dark",
    telemetry: {},
    history: { cpu: [], gpu: [], fan: [], cpuTemp: [], gpuTemp: [] },
    settings: {
      mode: "cfg-office", dGpuDirect: true, fanBoost: false,
      numLock: true, capsLock: false, fnLock: false,
      touchpadLock: false, osdDisabled: false, kbBrightnessLevel: 0,
    },
    uxtuParams: { ...FULL_PARAMS, ...fanDefaults },
    overrides: {},
    switching: false,
    // 三态：null=未知（启动中/尚未确认），true=在线，false=明确离线。
    // 原为 false —— 前端一加载 1.5s 后必弹"后端服务未连接"，哪怕后端完全正常
    backendOnline: null,
    profiles: [],
    currentProfile: null,
    platformInfo: { oem: 'Unknown', vendor: '', model: '', isElevated: null },
    platformInfoReady: false,
  };
}

// ── 模块级 store 单例 ──
let state = makeInitialState();
const listeners = new Set();

// 后台任务生命周期 / 竞态 guard（从原 hook 的 useRef 提升为模块级）
let initialized = false;
let _bootstrapDone = false;
let _switchGen = 0;
let _switchTimeout = null;
let _lastTick = null;
let _maxCores = 16;
let _ws = null;
let _mockTimer = null;
let _reconnectTimer = null;

// ── 订阅原语（供 useSyncExternalStore 使用）──
function subscribe(listener) {
  listeners.add(listener);
  maybeStart();
  return () => {
    listeners.delete(listener);
    maybeStop();
  };
}
function getSnapshot() {
  return state;
}

// ── 状态写入：唯一入口。产出新引用 + 处理联动副作用 + 触发一次监听 ──
function setState(updater, { suppressModeSwitch = false } = {}) {
  const prev = state;
  const next = typeof updater === "function" ? updater(prev) : { ...prev, ...updater };
  if (next === prev) return;

  const modeChanged = prev.settings?.mode !== next.settings?.mode;
  const themeChanged = prev.theme !== next.theme;
  const telChanged = prev.telemetry !== next.telemetry;

  // 遥测历史（原 useEffect([telemetry])）——写入 next 后再统一 emit 一次，避免闪烁
  if (telChanged) {
    const t = next.telemetry || {};
    next.history = {
      cpu: [...(prev.history?.cpu || []), t.cpuUsage].slice(-MAX_HISTORY),
      gpu: [...(prev.history?.gpu || []), t.gpuUsage].slice(-MAX_HISTORY),
      fan: [...(prev.history?.fan || []), t.fanLargeRpm].slice(-MAX_HISTORY),
      cpuTemp: [...(prev.history?.cpuTemp || []), t.cpuTemp].slice(-MAX_HISTORY),
      gpuTemp: [...(prev.history?.gpuTemp || []), t.gpuTemp].slice(-MAX_HISTORY),
    };
  }

  state = next;
  emit();

  // 模式切换联动（原 useEffect([settings.mode])，跳过首次加载）
  if (modeChanged && _bootstrapDone && !suppressModeSwitch) startModeSwitch(next.settings.mode);
  // 主题 → 后端（原 useEffect([theme])）
  if (themeChanged) saveUiState({ theme: next.theme }).catch(() => {});
}

function emit() {
  for (const fn of listeners) {
    try { fn(); } catch (e) { console.error("[useControlState] listener error:", e); }
  }
}

// ── 后台任务：懒启动 / 回收 ──
function maybeStart() {
  if (initialized) return;
  initialized = true;
  if (!_bootstrapDone) {
    bootstrap();
  }
  connectSocket();
  watchTelemetryMock();
}
function maybeStop() {
  if (listeners.size !== 0) return;
  initialized = false;
  _bootstrapDone = false;
  if (_reconnectTimer) { clearTimeout(_reconnectTimer); _reconnectTimer = null; }
  if (_mockTimer) { clearInterval(_mockTimer); _mockTimer = null; }
  if (_ws) { try { _ws.close(); } catch { /* 关闭可能抛错则忽略 */ } _ws = null; }
  if (_switchTimeout) { clearTimeout(_switchTimeout); _switchTimeout = null; }
  // 清空到初始状态：生产（App 常驻订阅）不会走到这里；测试期间提供干净隔离
  listeners.clear();
  state = makeInitialState();
  _switchGen = 0;
  _lastTick = null;
}

// ── 启动拉取（原 app 启动 useEffect，只在首个订阅者出现时跑一次）──
async function bootstrap() {
  try {
    const count = await migrateLocalStorageOverrides();
    if (count > 0) log("Startup", `migrated ${count} mode(s) from localStorage`);
  } catch (e) {
    log("Startup", `localStorage migration error: ${e.message}`);
  }
  // 平台检测：确定核心数转换基数
  let maxCores = 16;
  try {
    const plat = await (await fetch("/api/platform/info")).json();
    if (plat?.vendor === "Intel") maxCores = 18;
    setMaxCoresForPercent(maxCores);
  } catch { /* 后端不可用时默认 16 */ }
  _maxCores = maxCores;
  // 加载机型信息
  try {
    const pi = await fetchPlatformInfo();
    setState(prev => ({
      ...prev,
      platformInfo: {
        oem: pi.oem || 'Unknown',
        vendor: pi.vendor || '',
        model: pi.model || '',
        isElevated: pi.isElevated ?? null,
        ecCpuTemp: pi.ecCpuTemp ?? null,
        ecGpuTemp: pi.ecGpuTemp ?? null,
      },
      platformInfoReady: true,
    }));
  } catch { /* ignore */ }
  // 加载配置列表
  let profileList = [];
  try {
    const { profiles: list } = await fetchProfiles();
    profileList = list || [];
    console.log('[useControlState] profiles loaded:', profileList.length, profileList.map(p => p.id));
    setState(prev => ({ ...prev, profiles: profileList }));
  } catch (e) { console.error('[useControlState] profiles fetch error:', e); }
  // 先 HTTP 拿初始遥测数据，避免 mock 闪烁
  try {
    const initialTel = await fetchTelemetry();
    if (initialTel) {
      setState(prev => ({ ...prev, telemetry: initialTel || {}, backendOnline: true }));
    }
  } catch { /* 后端离线时保持空，等 WebSocket 或 mock 托底 */ }
  try {
    const { mode, overrides: rawOv } = await fetchOverrides();
    const ov = flattenBackendOverrides(rawOv, maxCores);
    const fanDefaults = MODE_FAN_DEFAULTS[resolvePerfMode(mode, profileList)] || {};
    // 说明：无需再写 prevMode —— setState 直接通过 prev/next 比较判定模式变更，
    // 且此刻 _bootstrapDone 尚未置 true，初始加载的 mode 不会触发 switchMode。
    setState(prev => ({
      ...prev,
      settings: { ...prev.settings, mode },
      uxtuParams: { ...FULL_PARAMS, ...fanDefaults, ...ov },
      overrides: ov,
      currentProfile: profileList.find(p => p.id === mode) || null,
    }));
  } catch {
    // 后端不可用时回退到 FULL_PARAMS 默认
  }
  // 初始加载完成：此后用户对 mode 的改动才会触发 switchMode（原 initialLoadRef=false）
  _bootstrapDone = true;
}

// ── 模式切换（原 useEffect([settings.mode])，改为 store 内驱动）──
function startModeSwitch(currentMode) {
  if (_switchTimeout) { clearTimeout(_switchTimeout); _switchTimeout = null; }
  const gen = ++_switchGen;
  setState(prev => ({ ...prev, switching: true }));

  // 安全兜底: 5 秒内后端未响应则强制解锁 UI
  _switchTimeout = setTimeout(() => {
    setState(prev => ({ ...prev, switching: false }));
  }, 5000);

  // 后端 /api/overrides/switch 内部已完成:
  // thermal_mode + last-mode.json + GPU/NVAPI/CPU 重置 + RestoreAllPerfSettings
  switchMode(currentMode).then(({ overrides: rawOv }) => {
    if (gen !== _switchGen) return; // 丢弃过期响应
    const ov = flattenBackendOverrides(rawOv, _maxCores);
    const fanDefaults = MODE_FAN_DEFAULTS[resolvePerfMode(currentMode, state.profiles)] || {};
    setState(prev => ({
      ...prev,
      uxtuParams: { ...FULL_PARAMS, ...fanDefaults, ...ov },
      overrides: ov,
    }));
  }).catch(() => {
    if (gen !== _switchGen) return;
    const fanDefaults = MODE_FAN_DEFAULTS[resolvePerfMode(currentMode, state.profiles)] || {};
    setState(prev => ({
      ...prev,
      uxtuParams: { ...FULL_PARAMS, ...fanDefaults },
      overrides: {},
    }));
  }).finally(() => {
    if (gen === _switchGen) {
      if (_switchTimeout) { clearTimeout(_switchTimeout); _switchTimeout = null; }
      setState(prev => ({ ...prev, switching: false }));
    }
  });
}

// ── WebSocket 遥测 + 自动切换（原 useEffect，改模块级生命周期）──
function connectSocket() {
  const connect = () => {
    _ws = createTelemetrySocket(
      (data) => {
        setState(prev => ({ ...prev, backendOnline: true }));

        // 处理自动切换消息
        if (data.type === "auto_switch" && data.mode) {
          log("AutoSwitch", `收到自动切换请求: ${data.mode}`);
          setState(
            prev =>
              prev.settings.mode === data.mode
                ? prev
                : {
                    ...prev,
                    settings: { ...prev.settings, mode: data.mode },
                    currentProfile: prev.profiles.find(p => p.id === data.mode) || null,
                  },
            { suppressModeSwitch: data.applied === true }
          );
          return; // auto_switch 消息不包含遥测数据
        }

        // 处理遥测数据
        setState(prev => ({ ...prev, telemetry: { ...prev.telemetry, ...data } }));

        // 壁纸自动轮换：后端在遥测帧里携带 bgLastRotated（Unix 秒），变化即刷新背景图
        if (typeof data.bgLastRotated === "number" && data.bgLastRotated > 0 && data.bgLastRotated !== _bgLastRotatedSeen) {
          _bgLastRotatedSeen = data.bgLastRotated;
          refreshWallpaperOnRotate(data.bgLastRotated);
        }
        // 自动同步 dGpuDirect 与实际 GPU mode（mode 1=独显→true，0/2→false）
        if (data.gpuMode != null) {
          const gpuMode = parseInt(data.gpuMode);
          const shouldBeOn = gpuMode === 1;
          setState(prev =>
            prev.settings.dGpuDirect === shouldBeOn ? prev : { ...prev, settings: { ...prev.settings, dGpuDirect: shouldBeOn } }
          );
        }
      },
      () => setState(prev => ({ ...prev, backendOnline: false }))
    );
    _ws.onclose = () => {
      setState(prev => ({ ...prev, backendOnline: false }));
      if (_reconnectTimer) clearTimeout(_reconnectTimer);
      _reconnectTimer = setTimeout(connect, 3000);
    };
  };
  connect();
}

// ── Mock 模拟（后端不可用时，原 useEffect 逻辑）──
function watchTelemetryMock() {
    const tick = () => {
        // 仅在"明确离线"时才用模拟数据兜底；未知态（启动中）保持空数据，避免展示假数据
        if (state.backendOnline !== false) return;
    if (_lastTick === null) _lastTick = Date.now();
    const now = Date.now();
    const dt = Math.max(0.5, Math.min(3, (now - (_lastTick || Date.now())) / 1000));
    _lastTick = now;

    setState(prev => {
      // 首次进入 mock 时用默认值兜底，避免 undefined 参与运算产生 NaN
      const base = {
        cpuUsage: 20, cpuFreq: 2.4, cpuTemp: 45, cpuCores: 16,
        gpuUsage: 15, gpuFreq: 1.2, gpuTemp: 45, gpuVramUsed: 2,
        gpuPowerDrawW: 20, gpuMode: null,
        memoryUsage: 30, memoryTotalGB: 32, memoryFreq: 3200,
        diskUsage: 40, diskTotalGB: 952, diskFreeGB: 400,
        fanLargeRpm: 2200, fanSmallRpm: 2000,
        fanLargeMax: 4400, fanSmallMax: 8200,
        thermalMode: 0, powerPlan: 0, kbBrightness: 0,
        ...prev.telemetry,
      };
      const u = prev;
      const fanLargeRpm = Math.round(base.fanLargeRpm + ((u.uxtuParams.fanLargeRpmTarget ?? 2900) - base.fanLargeRpm) * 0.1 * dt);
      const fanSmallRpm = Math.round(base.fanSmallRpm + ((u.uxtuParams.fanSmallRpmTarget ?? 5200) - base.fanSmallRpm) * 0.1 * dt);
      const fanLargePct = base.fanLargeMax > 0 ? fanLargeRpm / base.fanLargeMax : 0;
      const fanSmallPct = base.fanSmallMax > 0 ? fanSmallRpm / base.fanSmallMax : 0;
      const cooling = 0.4 * fanLargePct + 0.25 * fanSmallPct;
      const perfMode = resolvePerfMode(u.settings.mode, u.profiles);
      const modeBias = perfMode === "silent" ? -0.12 : perfMode === "office" ? -0.05 : perfMode === "gaming" ? 0.05 : perfMode === "beast" ? 0.14 : 0;
      const cpuTargetUsage = Math.max(5, Math.min(95, 25 + (u.uxtuParams.cpuLongPptW / 120) * 55 + modeBias * 100));
      const gpuTargetUsage = Math.max(2, Math.min(95, 15 + (u.uxtuParams.gpuPptLimitW / 180) * 55 + modeBias * 80));
      const drift = (target, current, strength) => current + (target - current) * strength * dt + (Math.random() - 0.5) * 1.5;
      const nextCpuUsage = drift(cpuTargetUsage, base.cpuUsage, 0.18);
      const nextGpuUsage = drift(gpuTargetUsage, base.gpuUsage, 0.16);
      return {
        ...prev,
        telemetry: {
          ...base,
          cpuUsage: Math.round(nextCpuUsage),
          cpuFreq: Number(Math.max(0.6, base.cpuFreq + (nextCpuUsage - base.cpuUsage) * 0.02).toFixed(2)),
          cpuTemp: Math.round(drift(u.uxtuParams.cpuTempLimitC - cooling * 12, base.cpuTemp, 0.10)),
          gpuUsage: Math.round(nextGpuUsage),
          gpuFreq: Number(Math.max(0.4, base.gpuFreq + (nextGpuUsage - base.gpuUsage) * 0.03).toFixed(2)),
          gpuTemp: Math.round(drift(u.uxtuParams.gpuTempLimitC - cooling * 10, base.gpuTemp, 0.09)),
          memoryUsage: Math.round(Math.max(1, Math.min(99, base.memoryUsage + (Math.random() - 0.5) * 0.8))),
          diskUsage: Math.round(Math.max(1, Math.min(99, base.diskUsage + (Math.random() - 0.5) * 0.6))),
          fanLargeRpm, fanSmallRpm,
        },
      };
    });
  };
  _mockTimer = setInterval(tick, 1000);
}

// 外部 setter 内部实现：支持 对象 或 函数式 更新，与改造前 React setState 用法一致
const upd = (obj) => typeof obj === "function" ? obj : () => obj;

// ── hook：底层就是订阅 store，返回值与改造前完全一致 ──
export function useControlState() {
  const s = useSyncExternalStore(subscribe, getSnapshot);

  // 从后端加载 UI 状态（原 hook 的独立 useEffect 一次性逻辑）
  useEffect(() => {
    (async () => {
      try {
        const st = await fetchUiState();
        if (!st) return;
        setState(prev => ({ ...prev, theme: st.theme || prev.theme }));
        if (st.accentColor) document.documentElement.style.setProperty("--seed-primary", st.accentColor);
      } catch { /* 后端离线时保留默认 */ }
    })();
  }, []);

  const setTheme = useCallback((v) => setState(prev => ({ ...prev, theme: upd(v)(prev.theme) })), []);
  const setTelemetry = useCallback((v) => setState(prev => ({ ...prev, telemetry: typeof v === "function" ? v(prev.telemetry) : v })), []);
  const setSettings = useCallback((v) => setState(prev => ({ ...prev, settings: typeof v === "function" ? v(prev.settings) : v })), []);
  const setUxtuParams = useCallback((v) => setState(prev => ({ ...prev, uxtuParams: typeof v === "function" ? v(prev.uxtuParams) : v })), []);
  const setOverrides = useCallback((v) => setState(prev => ({ ...prev, overrides: typeof v === "function" ? v(prev.overrides) : v })), []);
  const setProfiles = useCallback((v) => setState(prev => ({ ...prev, profiles: typeof v === "function" ? v(prev.profiles) : v })), []);
  const setCurrentProfile = useCallback((v) => setState(prev => ({ ...prev, currentProfile: typeof v === "function" ? v(prev.currentProfile) : v })), []);

  // ── Overrides 稀疏存储操作（仅更新 store，持久化由各独立端点完成）──
  const saveOverrideFn = useCallback((mode, key, value) => {
    // 不变量：只更新 overrides（灰显/高亮），不连带 uxtuParams（写后端错峰）
    setState(prev => ({ ...prev, overrides: { ...prev.overrides, [key]: value } }));
  }, []);

  const clearOverrideFn = useCallback(async (mode, fields) => {
    if (!fields?.length) return;
    await clearOverrides(mode, fields);
    const fieldSet = new Set(fields);
    setState(prev => {
      const nextOv = { ...prev.overrides };
      for (const k of fieldSet) delete nextOv[k];
      const nextUp = { ...prev.uxtuParams };
      for (const k of fieldSet) {
        if (k in FULL_PARAMS) nextUp[k] = FULL_PARAMS[k];
        else delete nextUp[k];
      }
      return { ...prev, overrides: nextOv, uxtuParams: nextUp };
    });
  }, []);

  // ── 重置参数到官方默认 ──
  const resetParams = useCallback(async (mode) => {
    await syncOverrides(mode, {});
    const fanDefaults = MODE_FAN_DEFAULTS[resolvePerfMode(mode, state.profiles)] || {};
    setState(prev => ({ ...prev, overrides: {}, uxtuParams: { ...FULL_PARAMS, ...fanDefaults } }));
  }, []);

  // Switch profile (Dashboard Dock / ConfigBar calls this)
  const switchProfile = useCallback((profileId) => {
    setState(prev => {
      if (prev.settings.mode === profileId) return prev;
      return {
        ...prev,
        settings: { ...prev.settings, mode: profileId },
        currentProfile: prev.profiles.find(p => p.id === profileId) || null,
      };
    });
  }, []);

  // After profile deleted
  const afterProfileDeleted = useCallback((deletedId) => {
    setState(prev => ({
      ...prev,
      profiles: prev.profiles.filter(p => p.id !== deletedId),
      currentProfile: (prev.currentProfile && prev.currentProfile.id === deletedId) ? null : prev.currentProfile,
    }));
  }, []);

  // After profile created
  const afterProfileCreated = useCallback((entry) => {
    setState(prev => ({
      ...prev,
      profiles: [...prev.profiles, entry],
      currentProfile: entry,
      settings: { ...prev.settings, mode: entry.id },
    }));
  }, []);

  // 重新拉取当前配置并同步到 UI（恢复出厂/导入后使用）
  const refreshOverrides = useCallback(async () => {
    try {
      const { mode, overrides: rawOv } = await fetchOverrides();
      const ov = flattenBackendOverrides(rawOv, _maxCores);
      const fanDefaults = MODE_FAN_DEFAULTS[resolvePerfMode(mode, state.profiles)] || {};
      setState(prev => ({
        ...prev,
        settings: { ...prev.settings, mode },
        uxtuParams: { ...FULL_PARAMS, ...fanDefaults, ...ov },
        overrides: ov,
        currentProfile: prev.profiles.find(p => p.id === mode) || null,
      }));
    } catch (e) {
      console.error('[useControlState] refresh overrides error:', e);
    }
  }, []);

  return {
    theme: s.theme, setTheme,
    telemetry: s.telemetry, setTelemetry,
    uxtuParams: s.uxtuParams, setUxtuParams,
    settings: s.settings, setSettings,
    history: s.history,
    overrides: s.overrides, setOverrides,
    saveOverride: saveOverrideFn,
    clearOverride: clearOverrideFn,
    resetParams,
    switching: s.switching,
    backendOnline: s.backendOnline,
    profiles: s.profiles, setProfiles,
    currentProfile: s.currentProfile, setCurrentProfile,
    platformInfo: s.platformInfo,
    platformInfoReady: s.platformInfoReady,
    switchProfile,
    afterProfileDeleted,
    afterProfileCreated,
    refreshOverrides,
  };
}
