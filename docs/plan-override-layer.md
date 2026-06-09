## 参数覆盖层重构 — 方案规划

> **状态**: ✅ 全部实施完成
> **日期**: 2026-06-09
> **替代**: 旧版 plan-override-layer.md + design-override-layer.md

---

### 〇、前置任务：补充缺失的 API 端点

> ~~**发现**：前端 `uxtuAdapter.js` 调用了多个 API，但后端 `Program.cs` 未实现这些端点，当前被 `.catch()` 静默忽略。~~
>
> **更新（2026-06-09 10:30）**：这些端点**曾经实现过**，但在 `198f650`（auto: build success）提交中被意外删除。已从 git 历史（`3f6c637`）恢复。

#### 已恢复的端点

| 端点 | 控制器 | 状态 |
|------|--------|------|
| `POST /api/nvapi/overclock` | `NvapiGpuController` | ✅ 已恢复 |
| `POST /api/nvapi/thermal-limit` | `NvapiGpuController` | ✅ 已恢复 |
| `GET /api/nvapi/status` | `NvapiGpuController` | ✅ 已恢复 |
| `POST /api/cpu/freq-limit` | `CpuPowerController` | ✅ 已恢复 |
| `POST /api/cpu/turbo` | `CpuPowerController` | ✅ 已恢复 |
| `POST /api/cpu/core-limit` | `CpuPowerController` | ✅ 已恢复 |
| `POST /api/cpu/reset` | `CpuPowerController` | ✅ 已恢复 |
| `GET /api/cpu/status` | `CpuPowerController` | ✅ 已恢复 |

**恢复来源**：`git checkout 3f6c637 -- server/api/Program.cs`

**优先级**：~~这些端点应在 Phase 1 之前完成~~ ✅ 已完成。

---

### 一、核心问题

当前系统在 EC 官方预设之上多维护了一层 `MODE_PRESETS`（我们自己定义的参数值），导致三个问题：

1. **恢复预设不是真默认** — 所有「恢复预设」按钮恢复的是 MODE_PRESETS，不是 BIOS/EC 出厂值
2. **改一个存全部** — 用户动了一个滑块，localStorage 全量快照保存 22 个字段，下次切回模式时加载的是快照而不是 EC 预设
3. **参数竞态** — 模式切换时全量下发 22 个参数，与 EC 切换 thermal_mode 加载的出厂值互相覆盖

---

### 二、设计原则

**砍掉 MODE_PRESETS，系统只剩两层：EC 官方默认 + 用户稀疏覆盖。**

- EC 是唯一的「预设」来源，thermal_mode 切换即加载出厂值
- 用户改了什么就存什么，没改过的参数永远不碰
- 控件灰色 = EC 在管（可操作，拖动后自动变为高亮并接管）
- 恢复预设 = 清空覆盖 + EC 重新接管

---

### 三、两层模型

```
┌─────────────────────────────────────────────────┐
│  用户覆盖层 (sparse overrides)                    │
│  localStorage: douzhanzhe_overrides_{mode}       │
│  只存用户改过的字段，改一个存一个                     │
├─────────────────────────────────────────────────┤
│  EC 官方默认                                      │
│  thermal_mode 切换时 EC 自动加载出厂值               │
│  管理 CPU 基础 PPT/温度 + 风扇 PID 温控             │
└─────────────────────────────────────────────────┘
```

不存在中间层。没有 MODE_PRESETS，没有「我们的预设」。

---

### 四、删除项

| 删除内容 | 当前位置 | 说明 |
|----------|---------|------|
| `MODE_PRESETS` 常量 | `uxtuAdapter.js` L137-143 | 整个对象删除，5 个模式 × 18 个字段全部不再维护 |
| App.jsx 全局「恢复预设」 | `App.jsx` L137-151 | 改为统一的「恢复默认」（发官方重置命令） |
| PerformancePanel 三个分组「恢复预设」 | CPU 频率 L167-192、CPU 功耗 L237-261、GPU L280-308 | **删除按钮**（MODE_PRESETS 已不存在，无需恢复预设） |
| SortableDashboard 风扇「恢复预设」 | `SortableDashboard.jsx` L158-175 | **删除按钮**（MODE_PRESETS 已不存在，无需恢复预设） |
| 全量持久化 useEffect | `useControlState.js` L116-124 | 替换为稀疏存储 |

---

### 五、稀疏存储

#### 5.1 存储格式

```
localStorage key:  douzhanzhe_overrides_{mode}
value:             { "cpuLongPptW": 85, "gpuCoreFreqMhz": 2400 }
                   ↑ 只有用户改过的字段，没改的不存
```

每个模式独立一个 key，互不影响。

#### 5.2 写入逻辑

用户每次调整参数时，只存这一个字段：

```js
function saveOverride(mode, key, value) {
  const overrides = loadOverrides(mode);
  overrides[key] = value;
  saveToLS("douzhanzhe_overrides_" + mode, overrides);
}
```

不做「值是否等于默认值」的判断 — 因为 MODE_PRESETS 已删除，没有「我们的默认值」可以比较。用户设置了就存，恢复默认时整体清空。

#### 5.3 读取逻辑

```js
function loadOverrides(mode) {
  return loadFromLS("douzhanzhe_overrides_" + mode, {});
}
```

返回空对象 = 该模式没有任何自定义，纯 EC 管理。

#### 5.4 清除

```js
// 清空某模式全部覆盖
function clearOverrides(mode) {
  saveToLS("douzhanzhe_overrides_" + mode, {});
}
```

---

### 六、模式切换

切换模式时根据 overrides 是否为空走两条分支：

```
用户点击模式按钮
  │
  ├─→ setSettings({ mode: newMode })
  │
  └─→ useControlState useEffect 检测到 mode 变化
        │
        ├─ overrides 为空 {}
        │    └─→ 只发 thermal_mode 命令
        │         EC 自动加载该模式的出厂值（CPU/GPU/风扇全归官方）
        │
        └─ overrides 非空 { cpuLongPptW: 85, ... }
             ├─→ 发 thermal_mode 命令（EC 加载出厂值）
             └─→ 批量下发 overrides 中的字段（覆盖 EC 值）
                  只发用户改过的，没改的不动
```

#### 6.1 状态加载

```js
// useControlState 模式切换 useEffect
const overrides = loadOverrides(newMode);
setUxtuParams(prev => {
  // 保留 FULL_PARAMS 作为 UI 兜底值（滑块范围/默认位置）
  // overrides 中的字段覆盖上去
  return { ...FULL_PARAMS, ...overrides };
});

// 下发
dispatchFullMode(newMode, overrides);
```

`FULL_PARAMS` 仍然保留，但仅用于 UI 层（滑块的 min/max/default 显示），不参与硬件下发。硬件下发只看 overrides。

#### 6.2 dispatchFullMode 改造

```
dispatchFullMode(mode, overrides)

  ① thermal_mode     → 永远执行（切模式的基础）

  ② SMU 批量         → overrides 有 cpuLongPptW/cpuShortPptW/cpuTempLimitC/
                        cpuVoltageOffset 任一才执行
  ③ GPU 频率/显存     → overrides 有 gpuFreqLimitEnabled/gpuCoreFreqMhz/gpuMemFreqMhz 才执行
  ④ NVAPI OC/温度     → overrides 有 ocCoreOffsetMhz/ocMemOffsetMhz/gpuTempLimitC 才执行
  ⑤ CPU powercfg     → overrides 有 cpuFreqLimitEnabled/cpuTurboDisabled/cpuCoreLimit/cpuPowerPlan 才执行
  ⑥ 风扇 RPM         → overrides 有 fanLargeRpmTarget/fanSmallRpmTarget 才执行
  ⑦ SMU 延迟重发 x2  → 仅当 ② 实际执行时触发
```

overrides 为空时，只有步骤 ① 执行，其他全部跳过。

---

### 七、恢复默认

只有一种恢复操作：**恢复官方默认**。砍掉「恢复预设」和「恢复我们的预设」的概念。

> **实验验证（2026-06-09）**：
> - WMI 方法 8（SystemPerMode）和 EC 寄存器 0xE4 都会加载完整预设（包括风扇）
> - 官方控制台切换模式保留风扇的机制：切换后立即重新下发用户风扇设置
> - 因此「恢复默认」只需重发 thermal_mode + resetCpuPower() 即可

```
用户点击「恢复默认」
  │
  ├─→ clearOverrides(mode)                    // 清空 localStorage
  ├─→ setUxtuParams({ ...FULL_PARAMS })        // UI 回到兜底值
  │
  └─→ resetToFactoryDefaults(mode)
        ├─ 重发 thermal_mode（EC 重新加载出厂值，包括 CPU/GPU/风扇预设）
        ├─ resetCpuPower()（CPU 频率/睿频/核心数回归默认）⚠️ 单独处理
        └─ GPU/NVAPI 由 thermal_mode 自动恢复（不需要额外命令）
```

**重要**：CPU 频率控制（频率限制/睿频/核心数）通过 Windows powercfg 实现，不受 EC 管理，必须单独调用 `resetCpuPower()` 恢复。其他所有参数（CPU PPT/温度、GPU、风扇）都由 EC 管理，thermal_mode 重发即可恢复。

#### 7.1 分组重置（已取消）

> **注意**：由于 MODE_PRESETS 已删除，各分组的「恢复预设」按钮**全部删除**，不再提供分组重置功能。
> 用户只能通过全局「恢复默认」按钮重置所有参数到官方预设。

**控制域区分**：
- **EC 管理**（thermal_mode 可恢复）：
  - CPU：PPT（长时/短时功耗墙）、温度墙、电压偏移 → `POST /api/smu/set` / `POST /api/uxtu/apply` ✅
  - GPU：频率锁、显存频率 → `POST /api/gpu/set` ✅
  - GPU：NVAPI 超频偏移、温度限制 → ⚠️ **需要新增 API 端点**（NvapiGpuController.cs 已存在，但未暴露为 HTTP API）
  - 风扇：大风扇/小风扇转速目标 → `POST /api/fan/set-target` ✅
- **Windows 管理**（需单独 reset）：
  - CPU 频率限制、睿频开关、核心数限制 → ⚠️ **需要新增 API 端点**（CpuPowerController.cs 已存在，但未暴露为 HTTP API）
  - 全部重置 → ⚠️ **需要新增 `POST /api/cpu/reset` 端点**

---

### 八、UI 状态：灰色与高亮

#### 8.1 控件双态

每个 SliderRow / SwitchRow 根据 overrides 是否有该字段显示不同状态：

| 状态 | 视觉 | 含义 | 操作 |
|------|------|------|------|
| **灰色** | 降低透明度 /  muted 色调 | 该参数由 EC 管理，未自定义 | 可操作 — 拖动/点击后自动变为高亮 |
| **高亮** | 正常亮度 / 主题色标记 | 用户已自定义该参数 | 可操作 — 调整值更新 override |

实现方式：组件接收 `overrides` prop，通过 `key in overrides` 判断状态。

```jsx
const isCustom = key in overrides;

<SliderRow
  label="长时功耗"
  value={uxtuParams.cpuLongPptW}
  // 灰色状态：降低透明度
  style={{ opacity: isCustom ? 1 : 0.5 }}
  onChange={(v) => {
    setUxtuParams(p => ({ ...p, cpuLongPptW: v }));   // UI 更新
    saveOverride(mode, "cpuLongPptW", v);               // 稀疏存储
    queueSmu("power_limit", v);                         // 硬件下发
  }}
/>
```

#### 8.2 分组状态指示

每个参数组卡片标题旁显示简洁标记：

```
CPU 频率控制                      ← 全组灰色（无 override）
CPU 功耗温度  ·  3项已自定义       ← 有高亮项，显示数量
GPU 调节                          ← 全组灰色
风扇          ·  EC 自动          ← 无 override，EC 管理
```

#### 8.3 灰色控件的默认显示值

灰色状态下控件显示什么数值？两个选择：

- **显示 FULL_PARAMS 值**（代码中的兜底默认值）— 简单，但不一定等于 EC 当前实际值
- **显示 "—" 或 placeholder**（如「EC 管理」）— 更准确，但滑块无法显示 placeholder

建议：滑块显示 FULL_PARAMS 值作为参考（灰色文字），开关显示当前状态但灰色标记。用户拖动/点击后变为高亮并写入 override。

---

### 九、数据流对比

#### 9.1 用户拖动滑块

```
Before:  setUxtuParams → useEffect 全量存 localStorage → 组件内去抖下发
After:   setUxtuParams + saveOverride(mode, key, value) → 组件内去抖下发
         (UI 更新)       (只存这一个字段)                  (不变)
```

#### 9.2 用户切换模式（无自定义值）

```
Before:  thermal_mode + 全量 22 参数下发 + SMU 延迟双发
After:   thermal_mode（一条命令，EC 管一切）
```

#### 9.3 用户切换模式（有自定义值）

```
Before:  thermal_mode + 全量 22 参数下发 + SMU 延迟双发
After:   thermal_mode + 只下发 overrides 中的字段 + SMU 延迟重发（仅当有 SMU 字段时）
```

#### 9.4 用户恢复默认

```
Before:  恢复 MODE_PRESETS（我们的预设值）→ 全量下发
After:   清空 overrides → thermal_mode 重切 + CPU/GPU reset → EC 出厂值
```

---

### 十、单项参数下发逻辑

用户每次调整一个参数时，控件 onChange 统一执行三步：

```
onChange(value)
  ├─→ setUxtuParams(p => ({...p, [key]: value}))   // ① UI 状态即时更新
  ├─→ saveOverride(mode, key, value)                // ② 立即写 localStorage（单项）
  └─→ queueHardware(key, value)                     // ③ 按需去抖 → 硬件下发
```

三步各自独立：UI 更新是同步的（React 立即重渲染），存储是同步的（localStorage 写入很快，不需要去抖），硬件下发按需去抖（见第十一节）。

#### 10.1 不同操作类型的时序

| 操作类型 | 例子 | 存储（步骤②） | 硬件下发（步骤③） |
|---------|------|-------------|----------------|
| 连续拖动 | 功耗滑块 35→85W | 每次 onChange 覆盖写同一 key（最终只留 85） | 必须去抖，否则 50+ 次 ryzenadj 子进程 |
| 离散切换 | 关闭睿频开关 | 写一次 | 单次命令，直接执行 |
| 按钮点击 | 电源计划切换 | 写一次 | 单次命令，直接执行 |

#### 10.2 每个控件的硬件下发通道

| 控件 | 参数 key | 硬件通道 | 去抖? |
|------|---------|---------|------|
| 温度墙滑块 | `cpuTempLimitC` | `applySmuSet("temp_limit", v)` | 是 600ms |
| 电压调节滑块 | `cpuVoltageOffset` | `applySmuSet("co_all", v)` | 是 600ms |
| 长时功耗滑块 | `cpuLongPptW` | `applySmuSet("power_limit", v)` | 是 600ms |
| 短时功耗滑块 | `cpuShortPptW` | `applySmuSet("short_power_limit", v)` | 是 600ms |
| 频率限制开关 | `cpuFreqLimitEnabled` | `setCpuFreqLimit(on ? mhz : 0)` | 是 600ms |
| 最大频率滑块 | `cpuFreqLimitMhz` | `setCpuFreqLimit(v)` | 是 600ms |
| 关闭睿频开关 | `cpuTurboDisabled` | `setCpuTurbo(!disabled)` | 否（toggle） |
| 限制核心数开关 | `cpuCoreLimit` | `setCpuCoreLimitPercent(pct)` | 是 600ms |
| 核心数滑块 | `cpuCoreLimit` | `setCpuCoreLimitPercent(pct)` | 是 600ms |
| 电源计划按钮 | `cpuPowerPlan` | `applyHardwareControl("power_plan", hal)` | 否（按钮） |
| GPU 核心频率滑块 | `gpuCoreFreqMhz` | unlock → limit-max → lock-exact | 是 400ms |
| GPU 核心偏移滑块 | `ocCoreOffsetMhz` | `applyNvapiOverclock(core, mem)` | 是 600ms |
| GPU 显存档位 | `gpuMemFreqMhz` | `applyGpuControl("limit-memory", mhz)` | 否（4 档选择） |
| GPU 温度限制滑块 | `gpuTempLimitC` | `applyNvapiThermalLimit(v)` | 是 600ms |
| 大风扇滑块 | `fanLargeRpmTarget` | `POST /api/fan/set-target` | 是 400ms |
| 小风扇滑块 | `fanSmallRpmTarget` | `POST /api/fan/set-target` | 是 400ms |

#### 10.3 去抖职责归属

所有去抖统一在组件内管理（PerformancePanel / SortableDashboard），不再依赖 useControlState 的 useEffect 做硬件下发。

- PerformancePanel 已有：`queueSmu`、`queueCpuFreq`、`queueCoreLimit`、`queueGpuCore`、`queueOc`、`queueThermal` — 保持不变
- SortableDashboard 风扇：新增 `queueFan` 函数（400ms 去抖，合并大小风扇一次请求），替代 useControlState L165-180 的集中 useEffect
- useControlState 移除风扇去抖 useEffect（L165-180），只保留状态和存储职责

---

### 十一、去抖策略

#### 11.1 哪些通道需要去抖

根据下发通道的代价分三档：

**需要去抖（400-600ms）** — 重量级操作，涉及子进程或多次 API 往返：

| 通道 | 调用链 | 单次代价 |
|------|--------|---------|
| SMU（功耗/温度/电压） | `applySmuSet` → 后端 → ryzenadj.exe 子进程 | ~200-300ms |
| GPU 核心频率 | `applyGpuControl` × 3（unlock → limit → lock） | ~500ms 完整序列 |
| NVAPI 超频偏移 | `applyNvapiOverclock` → NVAPI P/Invoke | 中等，拖动时高频触发 |
| NVAPI 温度限制 | `applyNvapiThermalLimit` → NVAPI P/Invoke | 中等 |
| 风扇 RPM | `POST /api/fan/set-target` → EC 寄存器写入 | 轻量，但拖动时连续触发 |
| CPU 频率限制 | `setCpuFreqLimit` → powercfg 子进程 | 中等 |
| CPU 核心数限制 | `setCpuCoreLimitPercent` → powercfg 子进程 | 中等 |

**不需要去抖** — 轻量级单次命令，用户操作频率低：

| 通道 | 调用链 | 原因 |
|------|--------|------|
| CPU 睿频开关 | `setCpuTurbo` → powercfg | toggle 操作，不会连续触发 |
| 电源计划 | `applyHardwareControl("power_plan")` → HAL | 按钮点击，非连续操作 |
| GPU 显存档位 | `applyGpuControl("limit-memory")` | 4 档选择，非连续滑动 |

#### 11.2 去抖实现模式

统一使用 `useRef` 持有 timer 的模式（PerformancePanel 现有写法）：

```js
const smuTimer = useRef(null);

function queueSmu(parameter, value) {
  clearTimeout(smuTimer.current);
  smuTimer.current = setTimeout(async () => {
    try { await applySmuSet(parameter, value); }
    catch (err) { console.error("SMU set failed:", err); }
  }, 600);
}
```

风扇去抖特殊处理 — 大小风扇合并为一次请求：

```js
const fanTimer = useRef(null);

function queueFan(largeRpm, smallRpm) {
  clearTimeout(fanTimer.current);
  fanTimer.current = setTimeout(() => {
    fetch("/api/fan/set-target", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ largeRpm, smallRpm }),
    }).catch(() => {});
  }, 400);
}
```

#### 11.3 去抖与存储的关系

存储（`saveOverride`）在去抖之前立即执行，不等待去抖完成。这意味着：

- 即使硬件命令还在去抖队列中，localStorage 已经记录了最新值
- 如果用户在去抖期间切换了模式，新模式的 overrides 不受影响
- 页面刷新时，即使最后一次硬件命令没来得及发出，下次启动也能从 localStorage 恢复正确值

---

### 十二、组件改造清单

| 文件 | 改动量 | 改动内容 |
|------|--------|---------|
| **`services/uxtuAdapter.js`** | 中 | 删除 `MODE_PRESETS` → `dispatchFullMode` 改为接收 overrides 并按条件执行 → 新增 `resetToFactoryDefaults()` → 导出参数分组 key 常量 |
| **`hooks/useControlState.js`** | 大 | 删除 L116-124 全量持久化 → 新增 `saveOverride` / `loadOverrides` / `clearOverrides` / `clearOverrideGroup` → 模式切换 useEffect 改为读 overrides + 条件 dispatch → 删除 MODE_PRESETS 引用 → 暴露 `overrides` 到返回值 → 旧数据迁移 → 移除风扇去抖 useEffect（L165-180） |
| **`App.jsx`** | 小 | 模式按钮简化（清理当前残留的 async 双发逻辑）→ 「恢复预设」改为「恢复默认」（调用 `clearOverrides` + `resetToFactoryDefaults`） |
| **`panels/PerformancePanel.jsx`** | 中 | 三个分组「恢复预设」改为分组重置（`clearOverrideGroup` + 硬件重置）→ 控件增加灰色/高亮状态 → onChange 增加 `saveOverride` 调用 |
| **`SortableDashboard.jsx`** | 中 | 风扇「恢复预设」改为重置（删 override + `/api/fan/restore`）→ 风扇滑块灰色/高亮状态 → 新增 `queueFan` 去抖函数替代 useControlState 集中 useEffect |
| **`panels/FanCurvePanel.jsx`** | 微 | 曲线停止时如 overrides 无风扇字段则自动回归 EC（已有 `/api/fan/restore` 逻辑，微调互斥） |
| **`panels/TelemetryPanel.jsx`** | 无 | 已废弃，不改 |

---

### 十三、custom 模式

custom 模式走服务端持久化（`/api/custom-params`），与其他模式不同。重构后：

- custom 模式的 overrides 存 localStorage `douzhanzhe_overrides_custom`
- 服务端 `/api/custom-params` 保持不变作为备份
- custom 模式没有 thermal_mode 值（`thermalModeMap.custom = null`），切换时不发 thermal_mode 命令
- custom 模式下所有用户设定的值都是 override

---

### 十四、旧数据处理

**策略：检测到旧数据直接清空，不迁移。**

原因：
- MODE_PRESETS 已删除，无法区分旧数据中哪些是「用户改过的」vs「我们预设的」
- 迁移可能导致用户不想要的设置被保留
- 清空后用户从 EC 官方默认开始，体验更清晰

```js
// 启动时检测并清空旧数据
function clearOldParams() {
  const modes = ["silent", "office", "beast", "gaming", "custom"];
  for (const mode of modes) {
    const oldKey = "douzhanzhe_params_" + mode;
    if (localStorage.getItem(oldKey)) {
      localStorage.removeItem(oldKey);
    }
  }
}
```

注意：清空旧数据后，用户的所有历史设置将丢失，从 EC 官方默认值重新开始。

---

### 十五、实施阶段

```
Phase 1 — 存储 + 下发（基础）
  ├─ useControlState 存储模型重构（全量 → 稀疏）
  ├─ dispatchFullMode 改为 overrides 感知
  ├─ 删除 MODE_PRESETS
  └─ 旧数据迁移

Phase 2 — 恢复 + UI（功能）
  ├─ App.jsx 「恢复默认」按钮
  ├─ 分组重置按钮
  ├─ 控件灰色/高亮状态
  └─ 风扇 EC 自动模式

Phase 3 — 收尾
  ├─ FanCurvePanel 互斥联动
  └─ 清理废弃代码
```

三个阶段严格顺序，每个阶段完成后做一轮验证。

---

### 十六、验证清单

**Phase 1 验证**
- 拖动单个滑块，localStorage 只存该字段
- overrides 为空的模式切换只发 thermal_mode（Network 面板确认无 SMU/GPU/NVAPI 请求）
- 有 overrides 的模式切换只发 overrides 字段
- SMU 延迟重发只在有 SMU 字段时触发

**Phase 2 验证**
- 「恢复默认」后所有控件变灰色，EC 重新接管
- 分组重置只影响该组，其他组不变
- 灰色控件拖动后自动高亮
- 风扇无 override 时 EC 自动管理（RPM 随温度变化）

**Phase 3 验证**
- 自定义曲线 active 时风扇控件回到灰色
- 停止曲线后风扇回归 EC 管理
