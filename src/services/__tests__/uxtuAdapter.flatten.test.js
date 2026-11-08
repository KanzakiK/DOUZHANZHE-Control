import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  flattenBackendOverrides, powerPlanHALMap, BUILTIN_MODE_ORDER, resolvePerfMode, sortBuiltinProfiles,
  fetchPerCoreCo, applyPerCoreCo, clearPerCoreCo, probePerCoreCo, reapplyOverrides,
  clearOverrides, syncOverrides, fetchProfiles,
} from '../uxtuAdapter'

// ── flattenBackendOverrides: powerPlan 整数 → 字符串 id 归一化 ──
// 回归说明（修的是切到控制面板时「电源管理」已保存的档位不显示）：
//   后端 overrides.powerPlan 存的是 HAL 整数(0/1/2)，前端按钮比较用的是字符串 id
//   (balance/performance/efficiency)。若 flatten 直接把整数塞进 cpuPowerPlan，
//   则 uxtuParams.cpuPowerPlan === plan.id 永远不成立 → 切过去选中项空白。
//   这里必须在 flatten 时把整数归一成 id。

describe('flattenBackendOverrides powerPlan', () => {
  it('把后端整数 powerPlan 归一成前端字符串 id', () => {
    const HAL_TO_ID = Object.entries(powerPlanHALMap).reduce((acc, [id, hal]) => {
      acc[hal] = id
      return acc
    }, {})

    // 遍历所有合法 HAL 值，断言归一正确
    for (const [hal, id] of Object.entries(HAL_TO_ID)) {
      const flat = flattenBackendOverrides({ powerPlan: Number(hal) })
      expect(flat.cpuPowerPlan).toBe(id)
    }
  })

  it('已是字符串 id 时不重复映射（幂等）', () => {
    const flat = flattenBackendOverrides({ powerPlan: 'performance' })
    expect(flat.cpuPowerPlan).toBe('performance')
  })

  it('无 powerPlan 时不写入 cpuPowerPlan', () => {
    const flat = flattenBackendOverrides({ cpu: { freqLimitMhz: 3000 } })
    expect(flat.cpuPowerPlan).toBeUndefined()
  })
})

// ── 逐核 CO：后端 smu.perCoreCo 必须原样透传到前端稀疏覆盖层 ──
// 否则模式切换 / 刷新后 UI 会以为未启用，且 reapplyOverrides 不会重放。
describe('flattenBackendOverrides perCoreCo', () => {
  it('透传逐核数组（含 0 值核心，用于下轮复位）', () => {
    const entries = [
      { ccd: 0, core: 0, mv: -10 },
      { ccd: 0, core: 1, mv: 0 },
      { ccd: 1, core: 7, mv: -20 },
    ]
    const flat = flattenBackendOverrides({ smu: { perCoreCo: entries } })
    expect(flat.perCoreCo).toEqual(entries)
  })

  it('后端为 null 时不写入，交给 FULL_PARAMS 的空数组默认值', () => {
    const flat = flattenBackendOverrides({ smu: { coAll: -15, perCoreCo: null } })
    expect(flat.cpuVoltageOffset).toBe(-15)
    expect(flat.perCoreCo).toBeUndefined()
  })
})

// ── 逐核 CO API 封装：URL / body / mode 编码，以及重放边界 ──
describe('逐核 CO API 封装与重放', () => {
  let calls
  beforeEach(() => {
    calls = []
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
      calls.push({ url: String(url), init: init || {} })
      return { ok: true, json: async () => ({ ok: true }) }
    }))
  })
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

  it('applyPerCoreCo 原样下发每核不同值，并对 mode 做 URL 编码', async () => {
    const offsets = [{ ccd: 0, core: 0, mv: -5 }, { ccd: 1, core: 7, mv: -9 }]
    await applyPerCoreCo(offsets, 'cfg gaming')
    expect(calls).toHaveLength(1)
    expect(calls[0].url).toBe('/api/smu/co-per-core?mode=cfg%20gaming')
    expect(calls[0].init.method).toBe('POST')
    expect(JSON.parse(calls[0].init.body)).toEqual({ offsets })
  })

  it('fetch / clear / probe 各自打到正确端点', async () => {
    await fetchPerCoreCo()
    await clearPerCoreCo('cfg-gaming')
    await probePerCoreCo()
    expect(calls.map(c => c.url)).toEqual([
      '/api/smu/co-per-core',
      '/api/smu/co-per-core/clear?mode=cfg-gaming',
      '/api/smu/co-per-core/probe',
    ])
    expect(calls[0].init.method).toBeUndefined()
    expect(calls[1].init.method).toBe('POST')
    expect(calls[2].init.method).toBe('POST')
  })

  it('业务层 ok=false 时 clear/sync 必须抛错，不能把硬件未复位当成成功', async () => {
    fetch
      .mockResolvedValueOnce({ ok: true, json: async () => ({ ok: false, error: '逐核写 0 未全部成功' }) })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ ok: false, error: '逐核复位异常' }) })

    await expect(clearPerCoreCo('cfg-gaming')).rejects.toThrow('逐核写 0 未全部成功')
    await expect(syncOverrides('cfg-gaming', {})).rejects.toThrow('逐核复位异常')
  })

  it('通用 clearOverrides 同样检查业务 ok=false，并保留具体错误', async () => {
    fetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({ ok: false, error: '逐核写 0 未全部成功；逐核配置保持不变，请重试' }),
    })

    await expect(clearOverrides('cfg-gaming', ['perCoreCo']))
      .rejects.toThrow('逐核写 0 未全部成功；逐核配置保持不变，请重试')
  })

  it('reapplyOverrides 中逐核初始下发 1 次 + 两次延迟重发，且从不走 /api/smu/set', async () => {
    vi.useFakeTimers()
    await reapplyOverrides('cfg-gaming', { perCoreCo: [{ ccd: 0, core: 0, mv: -5 }] })
    const co = () => calls.filter(c => c.url.startsWith('/api/smu/co-per-core'))
    expect(co()).toHaveLength(1)
    await vi.runAllTimersAsync()
    expect(co()).toHaveLength(3)
    expect(calls.filter(c => c.url.startsWith('/api/smu/set'))).toHaveLength(0)
    expect(JSON.parse(co()[0].init.body).offsets).toEqual([{ ccd: 0, core: 0, mv: -5 }])
  })
})

// ── cfg- 配置 id 辅助（resolvePerfMode / sortBuiltinProfiles / BUILTIN_MODE_ORDER） ──
describe('cfg- 配置 id 解包与排序', () => {
  const profiles = [
    { id: 'cfg-gaming', name: '斗战', builtIn: true, thermalMode: 'gaming' },
    { id: 'cfg-silent', name: '安静', builtIn: true, thermalMode: 'silent' },
    { id: 'cfg-office', name: '均衡', builtIn: true, thermalMode: 'office' },
    { id: 'cfg-beast', name: '野兽', builtIn: true, thermalMode: 'beast' },
  ]

  it('resolvePerfMode：cfg- 配置 id → 性能模式裸名', () => {
    expect(resolvePerfMode('cfg-gaming', profiles)).toBe('gaming')
    expect(resolvePerfMode('cfg-silent', profiles)).toBe('silent')
  })

  it('resolvePerfMode：裸性能模式名原样返回 / 缺失回退 office', () => {
    expect(resolvePerfMode('beast', profiles)).toBe('beast')
    expect(resolvePerfMode('cfg-nonexistent', profiles)).toBe('office')
    expect(resolvePerfMode('', profiles)).toBe('office')
  })

  it('sortBuiltinProfiles：按 cfg- 内置顺序（斗战最后）', () => {
    const sorted = sortBuiltinProfiles(profiles)
    expect(sorted.map(p => p.id)).toEqual(['cfg-silent', 'cfg-office', 'cfg-beast', 'cfg-gaming'])
  })

  it('BUILTIN_MODE_ORDER 是 cfg- 前缀配置 id', () => {
    expect(BUILTIN_MODE_ORDER).toEqual(['cfg-silent', 'cfg-office', 'cfg-beast', 'cfg-gaming'])
  })
})

describe('fetchProfiles 内置配置去重', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('过滤旧版裸名内置配置，只保留 cfg-* 规范项和用户配置', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({
        profiles: [
          { id: 'cfg-silent', name: '安静', builtIn: true },
          { id: 'cfg-office', name: '均衡', builtIn: true },
          { id: 'silent', name: '安静', builtIn: true },
          { id: 'office', name: '均衡', builtIn: true },
          { id: 'cfg-custom', name: '自定义', builtIn: false },
        ],
      }),
    })))

    const data = await fetchProfiles()
    expect(data.profiles.map(p => p.id)).toEqual(['cfg-silent', 'cfg-office', 'cfg-custom'])
  })
})
