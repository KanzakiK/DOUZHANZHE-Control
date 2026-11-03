import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { renderHook, waitFor, act, cleanup } from '@testing-library/react'
import { useControlState } from '../useControlState'
import {
  fetchOverrides,
  fetchProfiles,
  fetchPlatformInfo,
  fetchTelemetry,
  createTelemetrySocket,
  migrateLocalStorageOverrides,
  FULL_PARAMS,
  MODE_FAN_DEFAULTS,
} from '../../services/uxtuAdapter'

// ── 基线契约测试（useControlState） ──
//
// 目标：在共享 store 改造（docs/v2.0/tab-switch-shared-state.md Option A）前后，
// 用同一套用例保证对外行为不回归。这些用例断言 hook「对外契约」：
//   - 启动后按预期填充 settings / uxtuParams / overrides / profiles / currentProfile
//   - switchProfile 能改 settings.mode
//   - saveOverride 只广播 overrides（灰显态），setUxtuParams 单独驱动值
//   - WebSocket 遥测能更新 telemetry
//   - 稀疏性：后端缺数据的项由 uxtuParams 用 FULL_PARAMS/模式默认补齐，overrides 保持稀疏
//   - 竞态：模式快速连切时，过期的 switchMode 响应被丢弃，UI 停在最新模式（含 switching 生命周期）
//   - 后端失败时回退 FULL_PARAMS 默认（backendOnline 保持关闭）
//
// 只 mock 「网络」层（fetch / WebSocket / 迁移 / ui-state / switchMode），
// 保留 FULL_PARAMS / MODE_FAN_DEFAULTS / flattenBackendOverrides 等真实纯逻辑。

// 共享容器：在 vi.mock factory 与测试体之间传递回调 / 样例 / 竞态测试的受控 switchMode
// 以及 fetchOverrides 的每例响应（避免 mockResolvedValueOnce 与 clearAllMocks 的队列竞态）
const { socketHolder, MOCK_PROFILE, switchControl, fetchControl } = vi.hoisted(() => {
  const switchControl = {
    manualMode: false, // 默认自动 resolve；竞态用例置 true 改为手控
    deferreds: [],     // [{ resolve, mode }]
    callArgs: [],      // 记录 switchMode 调用顺序（mode）
  }
  const fetchControl = {
    // 每次调用 fetchOverrides 返回的模式与 overrides（测试体可改；beforeEach 重置）
    mode: 'office',
    overrides: {},
  }
  return {
    socketHolder: { lastOnData: null },
    MOCK_PROFILE: { id: 'office', name: '均衡模式', builtIn: true },
    switchControl,
    fetchControl,
  }
})

vi.mock('../../services/uxtuAdapter', async (importOriginal) => {
  const actual = await importOriginal()
  return {
    ...actual,
    migrateLocalStorageOverrides: vi.fn().mockResolvedValue(0),
    fetchPlatformInfo: vi.fn().mockResolvedValue({
      oem: 'Bellator', vendor: 'Intel', model: 'SSD3',
      isElevated: true,
    }),
    fetchProfiles: vi.fn().mockResolvedValue({ profiles: [MOCK_PROFILE] }),
    fetchTelemetry: vi.fn().mockResolvedValue({ cpuUsage: 11, gpuUsage: 22 }),
    // fetchOverrides 响应由 fetchControl 提供（测试体可逐例改写，beforeEach 重置）
    // 比 mockResolvedValueOnce 更稳：不受 clearAllMocks 清空 once 队列影响。
    fetchOverrides: vi.fn().mockImplementation(async () => ({
      mode: fetchControl.mode,
      overrides: fetchControl.overrides,
    })),
    fetchUiState: vi.fn().mockResolvedValue({}),
    saveUiState: vi.fn().mockResolvedValue({}),
    createTelemetrySocket: vi.fn((onData) => {
      socketHolder.lastOnData = onData
      return { close: vi.fn() }
    }),
    // switchMode：默认即时 resolve 空 overrides（让普通用例正常收尾并清掉 5s 兜底超时）；
    // 竞态用例把 switchControl.manualMode 置 true，改为手控 resolve 时机以模拟“过期响应后到”。
    switchMode: vi.fn((mode) => {
      switchControl.callArgs.push(mode)
      if (switchControl.manualMode) {
        let resolve
        const p = new Promise((r) => { resolve = r })
        switchControl.deferreds.push({ resolve, mode })
        return p
      }
      return Promise.resolve({ mode, overrides: {} })
    }),
  }
})

describe('useControlState 对外契约', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    socketHolder.lastOnData = null
    switchControl.manualMode = false
    switchControl.deferreds = []
    switchControl.callArgs = []
    fetchControl.mode = 'office'
    fetchControl.overrides = {}
    // 还原这些函数到已知默认实现，避免跨用例污染
    migrateLocalStorageOverrides.mockResolvedValue(0)
    fetchPlatformInfo.mockResolvedValue({
      oem: 'Bellator', vendor: 'Intel', model: 'SSD3', isElevated: true,
    })
    fetchProfiles.mockResolvedValue({ profiles: [MOCK_PROFILE] })
    fetchTelemetry.mockResolvedValue({ cpuUsage: 11, gpuUsage: 22 })
    createTelemetrySocket.mockImplementation((onData) => {
      socketHolder.lastOnData = onData
      return { close: vi.fn() }
    })
    // 原始 fetch(/api/platform/info)（hook 内直接调用）也要可控
    global.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ vendor: 'Intel' }),
    })
  })

  afterEach(() => {
    // 显式卸载已挂载的 hook，中止其 WebSocket/定时器副作用，
    // 避免用例间互相泄漏（Vitest globals=false 时 RTL 不会自动 cleanup）
    cleanup()
  })

  it('启动后：settings.mode 取自 fetchOverrides，overrides 与 uxtuParams 被填充', async () => {
    fetchControl.mode = 'gaming'
    fetchControl.overrides = { cpu: { freqLimitMhz: 3000 } }

    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.settings.mode).toBe('gaming')
    })
    expect(result.current.overrides).toHaveProperty('cpuFreqLimitMhz', 3000)
    expect(result.current.uxtuParams.cpuFreqLimitMhz).toBe(3000)
    // 启动拉取只应发生一次
    expect(fetchOverrides).toHaveBeenCalledTimes(1)
  })

  it('profiles / currentProfile 与 platformInfo 被正确填充', async () => {
    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.platformInfoReady).toBe(true)
    })
    expect(result.current.platformInfo.oem).toBe('Bellator')
    expect(result.current.profiles).toEqual([MOCK_PROFILE])
    expect(result.current.currentProfile?.id).toBe('office')
  })

  it('switchProfile 会切换 settings.mode 并更新 currentProfile', async () => {
    fetchProfiles.mockResolvedValueOnce({
      profiles: [
        MOCK_PROFILE,
        { id: 'beast', name: '高性能', builtIn: true },
      ],
    })

    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.profiles).toHaveLength(2)
    })

    await act(async () => {
      result.current.switchProfile('beast')
    })
    expect(result.current.settings.mode).toBe('beast')
    expect(result.current.currentProfile?.id).toBe('beast')
  })

  it('saveOverride 只广播 overrides 灰显态；setUxtuParams 单独驱动 uxtuParams 值', async () => {
    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.settings.mode).toBe('office')
    })

    act(() => {
      result.current.saveOverride('office', 'cpuLongPptW', 80)
    })
    // saveOverride 仅维护 overrides（UI 高亮/灰显用），不直接改 uxtuParams
    expect(result.current.overrides.cpuLongPptW).toBe(80)
    expect(result.current.uxtuParams.cpuLongPptW).toBe(FULL_PARAMS.cpuLongPptW)

    // uxtuParams 的值由面板组件通过 setUxtuParams 单独驱动
    act(() => {
      result.current.setUxtuParams({ ...result.current.uxtuParams, cpuLongPptW: 80 })
    })
    expect(result.current.uxtuParams.cpuLongPptW).toBe(80)

    act(() => {
      result.current.setOverrides({ cpuLongPptW: 60 })
    })
    expect(result.current.overrides.cpuLongPptW).toBe(60)
  })

  it('WebSocket 遥测会更新 telemetry', async () => {
    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      // 初始遥测经 fetchTelemetry 填充
      expect(result.current.telemetry?.cpuUsage).toBe(11)
    })

    // 模拟后端经 WebSocket 推送一条实时遥测
    expect(socketHolder.lastOnData).toBeTypeOf('function')
    act(() => {
      socketHolder.lastOnData({ cpuUsage: 55, gpuUsage: 66, gpuMode: 1 })
    })
    expect(result.current.telemetry.cpuUsage).toBe(55)
    expect(result.current.telemetry.gpuUsage).toBe(66)
  })

  it('已由后端应用的自动切换只同步 UI，不重复调用 switchMode', async () => {
    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.profiles).toHaveLength(1)
    })

    act(() => {
      socketHolder.lastOnData({
        type: 'auto_switch',
        mode: 'cfg-gaming',
        applied: true,
      })
    })

    expect(result.current.settings.mode).toBe('cfg-gaming')
    expect(switchControl.callArgs).toEqual([])
  })

  // ── 稀疏性：后端可能缺某些参数数据，uxtuParams 用默认补齐，overrides 保持稀疏 ──
  it('稀疏性：uxtuParams 用 FULL_PARAMS/模式默认补齐缺项，overrides 只含被改项', async () => {
    fetchControl.mode = 'office'
    // 稀疏返回：只改过 cpu.freqLimitMhz，其余项后端根本没下发
    fetchControl.overrides = { cpu: { freqLimitMhz: 3000 } }

    const { result } = renderHook(() => useControlState())

    // 注意：settings 初始 mode 就是 'office'，不能用它来“等待启动完成”。
    // 必须等 overrides 真正被 flatten 填充（fetchOverrides → setOverrides 已生效）。
    await waitFor(() => {
      expect(result.current.overrides.cpuFreqLimitMhz).toBe(3000)
    })

    // flattenBackendOverrides 只把 cpu.freqLimitMhz 摊平成两项，overrides 保持稀疏
    expect(result.current.overrides).toEqual({
      cpuFreqLimitEnabled: true,
      cpuFreqLimitMhz: 3000,
    })
    // uxtuParams 是完整生效画面：改动项取后端值…
    expect(result.current.uxtuParams.cpuFreqLimitMhz).toBe(3000)
    // …后端没下发/没改的项用 FULL_PARAMS 兆底补齐
    expect(result.current.uxtuParams.cpuLongPptW).toBe(FULL_PARAMS.cpuLongPptW)
    // …风扇项用当前模式的 EC 官方默认补齐（office=2900）
    expect(result.current.uxtuParams.fanLargeRpmTarget).toBe(
      MODE_FAN_DEFAULTS.office.fanLargeRpmTarget
    )
  })

  // ── 竞态：模式快速连切时，过期的 switchMode 响应必须被丢弃，UI 停在最新模式 ──
  it('竞态：快速连切模式时过期 switchMode 响应被丢弃，switching 正常收敛', async () => {
    // 预置两个内置配置，便于用 switchProfile 切到 beast/gaming
    fetchProfiles.mockResolvedValueOnce({
      profiles: [
        MOCK_PROFILE,
        { id: 'beast', name: '高性能', builtIn: true },
        { id: 'gaming', name: '满血释放', builtIn: true },
      ],
    })

    const { result } = renderHook(() => useControlState())

    await waitFor(() => {
      expect(result.current.profiles).toHaveLength(3)
      expect(result.current.settings.mode).toBe('office')
    })

    // 进入手控 switchMode，模拟不同 settle 时机（含“过期响应后到”）
    switchControl.manualMode = true

    // 连点两下：beast → gaming（中间态 beast 会发起 switchMode，但立刻又被 gaming 覆盖）
    act(() => result.current.switchProfile('beast'))
    act(() => result.current.switchProfile('gaming'))

    // 此时两个 switchMode 都已发出；gaming 是“最新一代”
    expect(switchControl.callArgs[0]).toBe('beast')
    expect(switchControl.callArgs[1]).toBe('gaming')
    expect(switchControl.deferreds).toHaveLength(2)

    // 模拟顺序：先返回 gaming 的最新结果，再返回 beast 的过期结果（后到）
    await act(async () => {
      switchControl.deferreds[1].resolve({ mode: 'gaming', overrides: {} })
      await Promise.resolve()
      switchControl.deferreds[0].resolve({ mode: 'beast', overrides: {} })
      await Promise.resolve()
    })

    // 关键断言：UI 停在最新模式 gaming，而不是被 beast 的过期响应带走
    await waitFor(() => {
      expect(result.current.settings.mode).toBe('gaming')
    })
    // 生效值按 gaming 的官方风扇默认（4300），不被 beast（3500）覆盖
    expect(result.current.uxtuParams.fanLargeRpmTarget).toBe(
      MODE_FAN_DEFAULTS.gaming.fanLargeRpmTarget
    )
    // switching 在中途置 true（UI 冻结防竞争写），最终收敛为 false（5s 兜底被清除）
    expect(result.current.switching).toBe(false)
  })

  it('后端失败时回退到 FULL_PARAMS 默认，且不抛错', async () => {
    fetchOverrides.mockRejectedValueOnce(new Error('backend offline'))
    fetchTelemetry.mockRejectedValueOnce(new Error('offline'))
    fetchPlatformInfo.mockRejectedValueOnce(new Error('offline'))
    fetchProfiles.mockRejectedValueOnce(new Error('offline'))

    const { result } = renderHook(() => useControlState())

    // 等待启动副作用收敛（即使全部失败也不应 unhandled rejection）
    // fetchOverrides 被调用即代表启动流程已推进到配置拉取这一步
    await waitFor(() => {
      expect(fetchOverrides).toHaveBeenCalled()
    })

    // 失败回退：overrides 为空，uxtuParams 使用 FULL_PARAMS 兆底
    expect(result.current.overrides).toEqual({})
    expect(result.current.uxtuParams).toHaveProperty('cpuLongPptW')
    expect(result.current.settings.mode).toBeDefined()
  })

  // ── 共享 store 核心目标：多个实例共享同一份状态，切 Tab 秒开、改配置即跨页广播 ──
  it('单例共享：两个独立 hook 实例读到同一份 uxtuParams/overrides 引用', async () => {
    const a = renderHook(() => useControlState())
    const b = renderHook(() => useControlState())

    await waitFor(() => {
      expect(a.result.current.settings.mode).toBe('office')
    })
    // 两个实例订阅同一 module store，读到的应是同一份对象引用
    expect(a.result.current.uxtuParams).toBe(b.result.current.uxtuParams)
    expect(a.result.current.settings).toBe(b.result.current.settings)
    expect(a.result.current.overrides).toBe(b.result.current.overrides)
  })

  it('单例共享：一个实例切换配置，另一个实例立即同步（改配置即刷所有 Tab）', async () => {
    fetchProfiles.mockResolvedValue({
      profiles: [
        MOCK_PROFILE,
        { id: 'beast', name: '高性能', builtIn: true },
      ],
    })

    const a = renderHook(() => useControlState())
    const b = renderHook(() => useControlState())

    await waitFor(() => {
      expect(a.result.current.profiles).toHaveLength(2)
    })

    // 仅在实例 a 上切换模式
    await act(async () => {
      a.result.current.switchProfile('beast')
    })

    // 实例 b 虽然“没被点击”，也应立即反映出新模式（跨 Tab 广播）
    await waitFor(() => {
      expect(b.result.current.settings.mode).toBe('beast')
      expect(b.result.current.currentProfile?.id).toBe('beast')
    })
    // 实例 a 同步一致
    expect(a.result.current.settings.mode).toBe('beast')
  })
})
