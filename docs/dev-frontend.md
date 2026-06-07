# 前端架构

> **📋 更新规则**：
> - 新增/移除组件 → 更新文件结构树对应位置
> - 组件接口/数据流变更 → 更新对应章节说明
> - 新增第三方依赖 → 在依赖章节追加
> - 同步更新主记忆 §1 文档地图中 `dev-frontend.md` 的描述

[TOC]

## 文件结构

```
src/
├── App.jsx                         # 根组件
├── main.jsx                        # 入口
├── index.css                       # Tailwind 基础 + 4 套主题 CSS 变量
├── components/
│   ├── SortableDashboard.jsx        # @dnd-kit 可拖拽仪表盘外壳
│   ├── ThemeSwitcher.jsx            # 主题切换下拉框
│   ├── panels/
│   │   ├── TelemetryPanel.jsx       # CPU/GPU 监控仪表 + 风扇控制
│   │   ├── PerformancePanel.jsx     # CPU/GPU 调优滑块
│   │   ├── SettingsPanel.jsx        # 系统开关 + 键盘灯 + 策略摘要 + 关于
│   │   └── SystemInfoPanel.jsx      # 操作系统/硬件信息
│   └── ui/
│       ├── Card.jsx                 # 通用卡片容器
│       ├── Gauge.jsx                # 进度条指标组件
│       ├── SliderRow.jsx            # 带标签的范围滑块
│       ├── SwitchRow.jsx            # 开关行
│       ├── Sparkline.jsx            # 迷你趋势线
│       ├── SortableCard.jsx         # @dnd-kit 可拖拽卡片包装器
│       └── Toast.jsx                # Toast 通知系统
├── hooks/
│   ├── useControlState.js           # 中央状态管理器
│   └── useCardOrder.js              # 仪表盘排序/隐藏持久化
├── services/
│   └── uxtuAdapter.js               # API 客户端封装
└── data/
    ├── mockTelemetry.js             # 开发期 Mock 数据
    └── themes.js                    # 主题配置数据
```

## 核心数据流

### 1. 遥测数据流

```
C# HAL WebSocket (ws://127.0.0.1:3100/ws)
  → createTelemetrySocket() [uxtuAdapter.js]
  → useControlState: setTelemetry(prev => ({...prev, ...data}))
  → App.jsx props → SortableDashboard → TelemetryPanel / PerformancePanel
```

- WebSocket 直连 `ws://127.0.0.1:3100/ws`
- 断线自动重连（3 秒间隔）
- 前端合并更新：`setTelemetry(prev => ({...prev, ...data}))`，只覆盖 C# HAL 推送的实时字段（温度/风扇转速/键盘灯）
- 无后端时回退到 `mockTelemetry`

### 2. SMU 参数下发

```
PerformancePanel → update() → setUxtuParams()
  → useMemo → uxtuPayload { chipset, profile, params }
  → 滑块 600ms 去抖 queueSmu() → POST /api/smu/set (单参)
  → 模式切换 → applyUxtuLimits() → POST /api/uxtu/apply (全量)
```

- 滑块微调 **单参数下发**：`queueSmu(parameter, valueM)` 600ms 去抖 → `POST /api/smu/set`
- 模式切换 **全量下发**：`applyUxtuLimits()` → `POST /api/uxtu/apply`
- 非自定义模式（silent/office/gaming/beast）滑块不锁定

### 3. 每模式独立参数记忆 (localStorage)

每个模式（silent/office/gaming/beast）独立保存一份参数到 localStorage，
键名 `douzhanzhe_params_{模式名}`，字段结构同 `uxtuParams` + `fanLargeRpmTarget` + `fanSmallRpmTarget`。

```
切换模式触发:
  useEffect [settings.mode]
  → 保存当前 uxtuParams+fans 到旧模式 key
  → 从新模式 key 加载 (loadFromLS)
  → 有记忆 → 恢复该模式上次调的值
  → 无记忆 → fallback MODE_PRESETS 出厂值

参数变化时自动保存:
  useEffect [uxtuParams, fanLargeRpmTarget, fanSmallRpmTarget]
  → saveToLS(douzhanzhe_params_{当前模式}, {...})
  → 跳过 custom 模式 (走服务端持久化)
```

### 4. 模式切换与恢复预设

```
模式按钮 onClick()
  → setSettings({ mode })  仅改当前模式名
  → useControlState prevModeRef effect 自动加载该模式记忆
  → applyHardwareControl("thermal_mode") 设散热模式

恢复预设按钮 (模式选择 Card action)
  → localStorage.removeItem(douzhanzhe_params_{当前模式})
  → 应用 MODE_PRESETS 出厂值
  → 风扇目标恢复 + applyUxtuLimits() 全量下发
  → Toast "已恢复预设值"

恢复预设按钮 (模式选择 Card action)
  → MODE_PRESETS[settings.mode] 覆盖全部参数
  → 风扇目标恢复
  → applyUxtuLimits() 全量下发
```

- `MODE_PRESETS` 定义在 `uxtuAdapter.js`，含 CPU (7字段) + GPU (6字段) + 风扇 (2字段) = 15 字段
- 模式切换时滑块跟随预设值跳转
- "恢复预设"按钮在模式选择 Card 右上角，恢复当前模式的完整出厂值

### 3. 硬件控制

```
SettingsPanel → toggleSetting()
  → applyHardwareControl() → POST /api/control (C# HAL :3100)
```

- C# HAL 支持：fn_lock, num_lock, caps_lock, kb_light, thermal_mode, gpu_mode, touchpad_lock, fn_lock

### 3a. 开机自启

```
设置标签页 → SettingsPanel showAutoStart
  useEffect → GET /api/auto-start 读取状态
  SwitchRow toggle → POST /api/auto-start { enabled: bool }
    → TaskScheduler LogonTrigger 注册/删除开机启动任务
```

### 4. 仪表盘排序持久化

```
SortableDashboard (DndContext) → onDragEnd → moveCard()
  → useCardOrder → localStorage
  → 退出编辑模式 → POST /api/ui-state (C# :3100)
```

- 启动时优先从服务端加载，回退到 localStorage，再回退到默认排序
- 隐藏的卡片不保存到 localStorage 的排序数组中

## 状态管理策略

### useControlState (src/hooks/useControlState.js)

中央状态管理器，内部管理：

| 状态 | 类型 | 持久化 | 说明 |
|------|------|--------|------|
| theme | string | localStorage `douzhanzhe_theme` | 当前主题 class |
| telemetry | object | 无 | 实时遥测，WS 更新 + mock 回退 |
| uxtuParams | object | 服务端 POST /api/custom-params (1s debounce) | 自定义模式参数 |
| settings | object | localStorage `douzhanzhe_settings` | 工作模式 + 系统开关状态 |
| fanLargeRpmTarget / fanSmallRpmTarget | number | 随 uxtuParams 保存 | 风扇目标转速 |
| history | object | 无 | 60 帧实时负载曲线 |
| backendOnline | boolean | 无 | WS 连接状态 |

**MODE_PRESETS**：5 套预设值（silent/office/gaming/beast/custom），切换模式时覆盖：
- CPU PPT、温度墙
- GPU PPT、温度墙、超频偏移、频率限制
- 风扇目标转速

**useRef 策略**：
- `prevModeRef` — 检测模式切换，避免自定义模式覆盖已保存的参数
- `presetRef` — PerformancePanel 首次挂载不自动切 custom 模式
- `onSaveRef` / `onSaveRef` — 回调函数引用保持最新

### useCardOrder (src/hooks/useCardOrder.js)

| 状态 | 类型 | 说明 |
|------|------|------|
| order | string[] | 卡片 ID 数组，定义显示顺序 |
| hiddenCards | Set | 隐藏的卡片 ID 集合 |

默认卡片列表（10 张）：

```
cpu-monitor, gpu-monitor, cpu-adjust, gpu-adjust, mem-disk,
fan-info, current-strategy, keyboard-light, about, system-switches
```

默认隐藏：`system-switches`

## 组件树

```
App.jsx
├── ToastProvider
│   ├── Sidebar (aside)
│   │   ├── 品牌标题
│   │   ├── 导航按钮（主页/系统/设置）
│   │   ├── 排序按钮（主页时显示）
│   │   └── ThemeSwitcher
│   └── Main
│       ├── [主页] SortableDashboard
│       │   └── DndContext → SortableContext → SortableCard[]
│       │       ├── TelemetryPanel (CPU 监控 / GPU 监控 / 风扇信息)
│       │       └── PerformancePanel (CPU 调节 / GPU 调节)
│       │       └── SettingsPanel (系统开关 / 键盘灯 / 策略 / 关于)
│       ├── [系统] SystemInfoPanel
│       └── [设置] SettingsPanel (完整)
│       └── [主页] 模式选择 Dock
```

## 响应式布局

### 侧边栏 & 主区域

```jsx
// App.jsx
<div className="grid grid-cols-1 md:grid-cols-[220px_1fr] gap-4">
  <aside className="... md:sticky md:top-4 md:self-start md:max-h-[calc(100vh-2rem)]">
```

- **手机** (< 768px)：单列堆叠，侧边栏在上方
- **桌面** (≥ 768px)：侧边栏 220px + 内容区 1fr

### 仪表盘列布局

```jsx
// SortableDashboard.jsx
<section className="columns-1 md:columns-2 lg:columns-3 gap-3 space-y-3 [column-fill:balance]">
```

- **手机** (< 768px)：1 列
- **平板** (768px-1023px)：2 列 CSS Columns
- **桌面** (≥ 1024px)：3 列 CSS Columns

CSS Columns (`columns-*`) 比 CSS Grid 更灵活，卡片高度不一时自动平衡排列。

### 模式选择 Dock

```jsx
// App.jsx
<div className="grid grid-cols-2 md:grid-cols-5 gap-2">
```

- 手机：2 列（每行 2-3 个按钮）
- 桌面：5 列

### 容器最大宽度

```jsx
<div className="max-w-[1750px] mx-auto">
```

在超大屏幕（>1750px）居中，防止内容过度拉伸。

## 仪表盘自定义实现

### 拖拽排序

基于 `@dnd-kit/core` + `@dnd-kit/sortable`：

1. `SortableDashboard` 包裹 `DndContext`（`closestCenter` 碰撞检测）
2. `PointerSensor` + `TouchSensor`（移动端兼容）
3. `SortableCard` 组件使用 `useSortable({ id })` hook
4. 编辑模式下显示拖拽手柄（⠿）和隐藏按钮（✕）
5. 拖拽时半透明（opacity: 0.5）+ z-50

### 排序持久化

```
localStorage (douzhanzhe_card_order)
  ↕ 启动时合并
服务端 POST /api/ui-state (C# :3100, JSON: { cardOrder, hiddenCards })
  ↑ 退出编辑模式时触发
```

优先顺序：服务端 > localStorage > 默认顺序

### 隐藏 / 显示

- `useCardOrder` 维护 `hiddenCards: Set<string>`
- 隐藏的卡片显示在编辑模式底部的"已隐藏模块"区域
- 点击即可恢复显示

## 主题系统

### CSS 变量

所有组件引用 CSS 变量而非硬编码颜色：

| 变量 | 用途 |
|------|------|
| --bg | 页面背景 |
| --card | 卡片底色 |
| --card-2 | 卡片次级色（侧边栏、按钮默认态） |
| --text | 主文字 |
| --muted | 次要文字 |
| --primary | 主题色（进度条正常态） |
| --primary-2 | 主题辅助色（选中态、按钮高亮） |
| --ok | 正常/良好指示色 |
| --warn | 警告色（温度等） |
| --danger | 危险色 |
| --border | 边框色 |

### 四套主题

| Class | 名称 | 特点 |
|-------|------|------|
| `theme-neon` | 赛博霓虹 | 深蓝底 + 青色 + 紫色强调 |
| `theme-minimal` | 极简现代 | 浅灰底 + 白色卡片 + 蓝色高亮 |
| `theme-geek` | 极客数据 | 极深底 + 青绿 + 荧光绿 |
| `theme-mech-violet` | 机甲紫黑 | 紫黑径向渐变背景 + 紫色高亮 |

每个主题覆盖 11 个 CSS 变量，`:root` 提供默认值（theme-mech-violet 风格）。

## 字段名对联问题

C# HAL WebSocket (`/ws`) 与前端遥测字段名不一致：

| C# HAL 推送 | 前端消费字段 | 状态 |
|-------------|-------------|------|
| cpuTemp | telemetry.cpuTemp | ✅ 匹配 |
| gpuTemp | telemetry.gpuTemp | ❌ C# 返回 0 覆盖 nvidia-smi 值 |
| cpuFanRpm | telemetry.fanLargeRpm | ❌ 字段名不匹配 |
| gpuFanRpm | telemetry.fanSmallRpm | ❌ 字段名不匹配 |
| kbBrightness | telemetry.kbBrightness | ✅ (未在前端 UI 展示) |
| fnLock | telemetry.fnLock | ✅ |

C# HAL 推送使用 `camelCase` 命名（C# JsonSerializer 默认），前端期待 `fanLargeRpm`/`fanSmallRpm` 格式。

## UI 组件库

| 组件 | props | 说明 |
|------|-------|------|
| Card | title, className, children | 圆角卡片 |
| Gauge | label, value, unit, color, max | 水平进度条，百分比自动计算 |
| SliderRow | label, value, min, max, step, onChange, unit, disabled | 带标签范围输入 |
| SwitchRow | label, checked, onChange | 开关行 |
| Sparkline | data, title, color | SVG 迷你趋势线（风扇负载曲线已隐藏，EC 16 位竞态导致心电图问题） |
| SortableCard | id, editMode, onHide | @dnd-kit 拖拽包装 |
| Toast | — | 全局通知 (context)，useToast() hook |

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
