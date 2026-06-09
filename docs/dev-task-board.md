# 任务看板

> **板块说明**：
> - **🧭 后续版本**：候选功能或低优先级改进
> - **✅ 已完成**：已验证通过的功能模块
>
> **整理规则**：
> - **两大板块**：🧭 后续版本 → ✅ 已完成
> - **排序**：开发实施 → 文档同步 → 清理/废弃 → 打包/部署
> - **分类**：后端 → 前端 → 其他；Bug 统一放到所属板块的"已知 Bug"子分类
> - **粒度**：一行 = 一个独立原子任务
> - **状态标记**：`[ ]` 待完成 → `[x]` 已完成（移至 ✅） → `~~删除线~~` 已废弃/已修复 Bug

---

## 🧭 一、后续版本（待完成）

### 后端
- [ ] **五模式全量配置覆盖**: 安静/均衡/野兽/斗战/自定义各保存一套完整配置（风扇转速×2、CPU功耗墙/温度墙、GPU频率偏移），后端新增 GET|POST /api/mode-config 持久化接口（前端 localStorage 已按模式隔离，缺后端持久化）
- [ ] **模式预设**: 新增"安静性能"模式（GPU满血 + 风扇低速）

### 前端
- [ ] **自建 OSD Toast**（前端全局 Toast，所有 POST /api/control 操作成功后弹出操作反馈，替代官方 BLDFnHotkeyUtility OSD）
- [ ] **五维雷达图**可视化（CPU/GPU/内存/磁盘/风扇综合评分）

### 参数覆盖层重构（方案规划: [plan-override-layer.md](plan-override-layer.md)）

> 核心改动：删除 MODE_PRESETS，系统只剩 EC 官方默认 + 用户稀疏覆盖。控件灰色 = EC 管理，高亮 = 用户自定义。
> 单项下发三步：UI 更新 → 立即存 override → 按需去抖下发硬件。存储不去抖，只有硬件下发按需去抖。

> **Phase 0 — 补充缺失的 API 端点** ✅ 已完成
> ~~从 git 历史（`3f6c637`）恢复被 `198f650` 意外删除的 8 个 API 端点~~
- [x] ~~NVAPI 端点~~（NvapiGpuController.cs 已存在，已恢复 HTTP API）
  - [x] `POST /api/nvapi/overclock`
  - [x] `POST /api/nvapi/thermal-limit`
  - [x] `GET /api/nvapi/status`
- [x] ~~CPU powercfg 端点~~（CpuPowerController.cs 已存在，已恢复 HTTP API）
  - [x] `POST /api/cpu/freq-limit`
  - [x] `POST /api/cpu/turbo`
  - [x] `POST /api/cpu/core-limit`
  - [x] `POST /api/cpu/reset`
  - [x] `GET /api/cpu/status`

> **Phase 1 — 存储 + 下发** ✅ 已完成
- [x] **① 存储模型重构 + 删除 MODE_PRESETS**
  - [x] [前端] `services/uxtuAdapter.js`: 删除 `MODE_PRESETS` 常量
  - [x] [前端] `hooks/useControlState.js`: 删除全量持久化 useEffect → 新增 `saveOverride` / `loadOverrides` / `clearOverrides` → 模式切换改为读 overrides + 条件 dispatch → 暴露 `overrides` 到返回值
  - [x] [前端] `hooks/useControlState.js`: 移除风扇去抖 useEffect，风扇下发职责转移到 SortableDashboard 组件内 `queueFan`
  - [x] [前端] `hooks/useControlState.js`: 启动时检测旧 `douzhanzhe_params_*` 数据并清空（不迁移，直接从 EC 默认开始）
- [x] **② dispatchFullMode overrides 感知**
  - [x] [前端] `services/uxtuAdapter.js`: `dispatchFullMode(mode, overrides)` 改为只接收 overrides，每步检查是否有相关字段再决定是否下发，overrides 为空时只发 thermal_mode
  - [x] [前端] `services/uxtuAdapter.js`: SMU 延迟重发仅在有 CPU SMU 字段时触发
  - [x] [前端] `services/uxtuAdapter.js`: 新增 `resetToFactoryDefaults(mode)` — thermal_mode 重切（EC 自动恢复 CPU/GPU/风扇预设）+ resetCpuPower（CPU 频率/睿频/核心数单独处理）

> **Phase 2 — 恢复默认** ✅ 已完成
- [x] **③ 恢复默认**
  - [x] [前端] `App.jsx`: 「恢复预设」改为「恢复默认」— clearOverrides + resetToFactoryDefaults
  - [x] [前端] `panels/PerformancePanel.jsx`: **删除**三个分组「恢复预设」按钮（CPU 频率、CPU 功耗、GPU）
  - [x] [前端] `SortableDashboard.jsx`: **删除**风扇「恢复预设」按钮
  - [ ] [前端] `App.jsx`: 清理模式按钮残留的 async 双发 dispatchFullMode 逻辑（当前 L155-170）

> **Phase 3 — UI + 收尾**
- [ ] **④ 控件灰色/高亮 + 单项下发三步**
  - [ ] [前端] 各 SliderRow/SwitchRow: 根据 `key in overrides` 显示灰色（EC 管理）或高亮（已自定义），灰色控件可操作，拖动后自动高亮
  - [ ] [前端] 各组件 onChange 统一三步：`setUxtuParams`（UI）+ `saveOverride`（立即存）+ `queueXxx`（按需去抖下发）
  - [ ] [前端] `SortableDashboard.jsx`: 新增 `queueFan(largeRpm, smallRpm)` 去抖函数（400ms，合并大小风扇一次请求），替代 useControlState 集中风扇 useEffect
  - [ ] [前端] 分组卡片标题旁显示自定义状态（如「3项已自定义」或无标记）
  - [ ] [前端] 去抖策略：SMU/GPU核心频率/NVAPI/CPU频率核心数/风扇 = 400-600ms 去抖；CPU 睿频/电源计划/GPU 显存档位 = 直接执行不去抖
- [ ] **⑤ 风扇 EC 自动 + 曲线互斥**
  - [ ] [前端] `SortableDashboard.jsx`: 风扇 `queueFan` 仅在 overrides 有 `fanLargeRpmTarget`/`fanSmallRpmTarget` 时下发，无 override 时不发请求（EC 管理）
  - [ ] [前端] `panels/FanCurvePanel.jsx`: 曲线停止时如 overrides 无风扇字段则自动回归 EC
- [ ] **⑥ 清理废弃代码**
  - [ ] [前端] 全项目清理 MODE_PRESETS 引用（App.jsx / useControlState.js / PerformancePanel.jsx / SortableDashboard.jsx）
  - [ ] [前端] 删除旧的 `design-override-layer.md`（已被 plan-override-layer.md 替代）

### 其他
- [ ] **一键降压**: 参考游戏加加 Lite，实现 CPU/GPU 降压功能（降低电压以减少发热和功耗）
- [ ] **游戏自动切换性能模式**: 参考机械革命控制台，监听游戏进程启动/退出，自动切换预设性能模式
- [ ] **提升控制台进程优先级**: 设置 Douzhanzhe 进程为高优先级（High/Above Normal），减少系统资源竞争导致的性能问题

---

## ✅ 二、已完成

### 后端 — 架构与驱动
- [x] DriverBridge 单例（inpoutx64 P/Invoke）
- [x] DriverBridge 32-bit IO: Inp32/Out32 + ReadPhys32/WritePhys32
- [x] HardwareAbstractionLayer（EC 寄存器语义映射）
- [x] WebConsoleAPI（Minimal API + WebSocket + TelemetryBackgroundService）
- [x] DSDT ACPI 反编译 + EC 寄存器全表导出
- [x] 替换 WinRing0 -> inpoutx64 (MIT 自管)
- [x] SmuController 子进程调用 ryzenadj.exe（程序集零外部依赖，运行时依赖 WinRing0 驱动）
- [x] SMU 子进程集成修复: 路径候选 + run.ps1 自动复制 WinRing0/ryzenadj + 移除 Redirect + 接受 0xC0000005 ✅
- [x] WmiInterface.cs（`root\WMI`，32 字节协议，零依赖）
- [x] C# 静态文件服务: `wwwroot/` + `UseStaticFiles()` + `MapFallbackToFile("index.html")`
- [x] C# 反向代理 Node.js 遗留端点
- [x] 开机自启动（后端注册服务）
- [x] 安装程序 / 打包（Inno Setup）
- [x] 版本号统一 + 构建脚本自动化（`-Version` 参数同步四处版本号 + 打包前验证）

### 后端 — 控制端点与遥测
- [x] `POST /api/control`: kb_light, fn_lock, num_lock, caps_lock, touchpad_lock, power_plan, thermal_mode, igpu_only, gpu_mode
- [x] `POST /api/smu/set`: stapm_limit, temp_limit
- [x] `POST /api/fan/set-target` + `POST /api/fan/restore`
- [x] `GET /api/telemetry` 全量遥测（28 字段）
- [x] `WS /ws` WebSocket 实时推送
- [x] `GET /debug` 内嵌 Debug 面板
- [x] 跑通全链路: GET /api/health -> POST /api/control -> 物理 EC 写入生效
- [x] Node.js JSON 持久化迁移: C# `GET|POST /api/custom-params`、`GET|POST /api/ui-state`
- [x] Node.js 遥测迁移: C# 补齐 `systeminformation` 类数据（CPU/内存/硬盘全量）
- [x] TelemetryBackgroundService 无条件推送 250ms: 删除变化检测，间隔 500ms→250ms
- [x] 废弃 Vite dev server: 前端迁入 `wwwroot/`，清理 proxy/watch/concurrently/dev/start/server 脚本
- [x] **遥测扩展**: CPU/GPU 功率 — GPU `power.draw` 实时显示 + CPU 功率状态读取/重置按钮
- [x] **Debug 页 GPU 控制区**: 频率锁/超频/重置 + 当前频率/功耗显示（前端 PerformancePanel 实现；`showGpu` 参数控制显示）
- [x] **SMU 监视器**: 值被覆盖时自动重发（替代 `readjustService.ps1`）— `applySmuBatch` 批量下发 + 模式切换双发 SMU
- [x] **跨平台/非管理员降级模式**（无 inpoutx64 时以 WMI/软件方式运行）— try-catch 优雅降级 + WMI fallback
- [x] **Node.js 全部端点迁移到 C#**，砍掉 Node.js 后端（06-06 已退役，全功能迁至 C# wwwroot/）
- [x] **安静性能模式**: `POST /api/uxtu/apply` 扩展支持 `fanLargeRpmTarget`/`fanSmallRpmTarget`

### 后端 — 风扇控制
- [x] EC 寄存器发现 (0xB2/0xB3) + 公式 `val=round(rpm/maxRpm*255)` 经 ec_writer.cs 验证
- [x] ~~0xB2/0xB3 直写~~ ❌ → **0x5F(大扇)/0x5B(小扇) ✅** WriteEc 直接生效，值=RPM/100，受散热区间限制
- [x] **小风扇控制寄存器 0x5B**: EC 直写 `WriteEc(0x5B, RPM/100)` 已验证
- [x] C# API + Debug 页风扇目标转速滑块 ✅ **0x5F 控制已验证**
- [x] WMI Bellator 协议修复: MaxFanSwitch(20)+MaxFanSpeed(21) data[4]=FanType
- [x] 恢复固件控制: `POST /api/fan/restore` — Debug 页按钮
- [x] EC 16 位风扇竞态修复 (HAL 双读仲裁): CpuFanRpm/GpuFanRpm 最多重读 3 次取非零，瞬态 0 率降至 0%

### 后端 — WMI 迁移 + AppBridge 废弃
- [x] WmiInterface 实现: GPUMode(9) ✅ FnLock(11) ✅ TPLock(12) ✅
- [x] AppBridge 子进程 + AppLib.cs 封装（反射调用 BLD.WMIOperation）
- [x] 运行时依赖自动部署（System.Management + 斗战者控制台.dll）
- [x] GPU 模式控制（混合/集显/独显）真机验证
- [x] 端点: `/api/app-cmd`, `/api/app-status`, `/api/ec-scan`
- [x] Debug 页 AppBridge 命令调试区
- [x] 废弃 AppBridge: 砍掉 AppLib.cs + AppBridge 子项目 + 斗战者控制台.dll 依赖

### 后端 — SMU 调优
- [x] Step 1~D: libryzenadj 封装 -> server.js 重构 -> SmuController 物理地址直写
- [x] Step G: WinRing0x64.dll 仍依赖于 SmuController（通过 ryzenadj.exe），DriverBridge 已全面移除对 WinRing0 的直接依赖
- [x] SMU 写入验证: Dragon Range 地址确认 + ryzenadj 子进程写入 25W 功率墙成功
- [x] Debug 页按钮: SMU 功率/温度设置按钮已整合 ✅
- [x] **SMU BatchApply**: SmuController 批量方法，单次 ryzenadj 子进程调用替代多次串行调用（模式切换 2-3s → ~300ms）
- [x] **CPU 性能控制后端**: CpuPowerController.cs 封装 powercfg，API: `POST /api/cpu/freq-limit`, `/api/cpu/turbo`, `/api/cpu/core-limit`, `/api/cpu/reset`
- [x] **NVAPI 端点**: `GET /api/nvapi/status`, `POST /api/nvapi/overclock`, `POST /api/nvapi/thermal-limit`

### 后端 — GPU 控制 (nvidia-smi)
- [x] **GpuController.cs (nvidia-smi 子进程封装)**: `POST /api/gpu/set` 统一接口
- [x] **GPU 核心频率超频**: KaronOC.dll (蛟龙引擎) P-State 偏移超频，已验证 core +150MHz / mem +300MHz
- [x] **GPU 超频引擎集成**: NVAPI P/Invoke + KaronOC.dll 双层架构，状态读取 + 超频写入
- [x] **GPU 核心频率锁频**: nvidia-smi `--lock-gpu-clocks=min,max` ✅ 已验证可用
- [x] **GPU 核心频率上限限制**: nvidia-smi `--lock-gpu-clocks=,max`
- [x] **重置 GPU 频率**: nvidia-smi `--reset-gpu-clocks` ✅ 已验证可用
- [x] **显存超频**: nvidia-smi `--lock-memory-clocks=min,max` (RTX5060 GDDR7 9001MHz 基线)
- [x] **重置显存**: nvidia-smi `--reset-memory-clocks`
- [x] **前端 GPU 性能面板 (NVAPI + nvidia-smi 统一卡片)**: 核心频率(nvidia-smi lock) + 核心偏移(NVAPI P-State offset) + 显存频率档位 + 温度限制 + 重置按钮
  - [x] **核心频率滑块**: nvidia-smi `--lock-gpu-clocks` 绝对频率 + 自动锁定重试机制
  - [x] **核心偏移滑块**: NVAPI P-State 偏移量(-200~+300 MHz)，模式切换联动
  - [x] **显存频率档位**: 自动/9001/11001/12001 MHz 四档
  - [x] **GPU 温度限制滑块**: `POST /api/nvapi/thermal-limit`，模式切换联动(CustomEvent)
  - [x] **重置 GPU 按钮**: 一键恢复核心频率/显存/偏移/温度限制到默认值
  - [x] ~~**GPU 功耗墙**~~: ❌ nvidia-smi/NVAPI 均不可用，已废弃
  - [x] **不受影响**: CPU 全部控件 / 风扇控制 / 系统开关 / 遥测曲线 / GPU 模式切换 — 均走独立通道，无需改动

### 后端 — 废弃 Node.js
- [x] **废弃 Node.js 后端**: 砍掉 server/server.js + tools/Node.js 仅用文件 + server.js 依赖

### 后端 — CPU 性能控制
- [x] **CPU 性能控制**: 基于 powercfg 实现 CPU 频率限制/关睿频/核心数限制（无需 SMU 驱动）

### 前端 — 遥测与仪表盘
- [x] CPU 占用率/温度/频率/核心数
- [x] GPU 占用率/温度/频率/显存
- [x] 内存占用/总量 + 硬盘占用/总量
- [x] CPU/GPU 风扇转速数字显示
- [x] WebSocket 对接硬件实时遥测（离线时 mock 回退）
- [x] 拖拽排序（@dnd-kit） + 卡片隐藏/显示 + 排序持久化
- [x] 历史曲线图 Sparkline 渲染排查 ✅ NaN 兜底修复
- [x] 隐藏风扇负载曲线: EC 竞态心电图，移除 `<Sparkline>` + `fanPctSeries`

### 前端 — 性能与调节
- [x] CPU 长时/短时功耗墙、温度墙、睿频控制
- [x] GPU 功耗墙、温度墙
- [x] 模式预设（安静/均衡/斗战/野兽/自定义）+ 非自定义模式锁定控件
- [x] 自动应用参数（500ms 去抖） + 模式切换联动
- [x] 路由修复: `SettingsPanel.jsx` halMap 追加 `gpuOnly->igpu_only`、`touchpadLock->touchpad_lock`
- [x] 散热模式: `uxtuAdapter.js` 导出 `thermalModeMap` + `powerPlanHALMap`
- [x] 散热模式: `App.jsx` 5 个模式按钮联动 `POST /api/control target=thermal_mode`
- [x] 模式重构（部分）: CPU/GPU 控件在四种模式下均可调
- [x] 电源计划: `uxtuAdapter.js` 导出 `powerPlanHALMap`
- [x] **电源计划**: `PerformancePanel.jsx` 电源管理按钮双发 C# HAL `power_plan` halValue 断链修复
- [x] **系统信息动态化**: 新增GET /api/system/info + HAL 4 WMI属性 + 前端useEffect
- [x] **电源计划按钮双发修复**: PerformancePanel.jsx POWER_PLANS 添加 halValue，按钮点击实际下发 C# HAL
- [x] **风扇滑块持久化**: 从 `useState(硬编码)` → `useState(() => loadFromLS(...))` + saveToLS，刷新保留上次设定值
- [x] **电压偏移持久化**: localStorage
- [x] **隐藏系统开关冗余项**: 移除独显直连/集显模式/关闭OSD三个SwitchRow 键 `douzhanzhe_voltage_offset`，所有模式通用
- [x] **非自定义模式 slider 闪跳修复**: fetch(/api/custom-params) 仅在 mode === "custom" 时覆盖 uxtuParams，其他模式保持 MODE_PRESETS
- [x] **GPU 统一卡片**: 合并标准/超频模式为单一 GPU 调节卡片，核心频率(nvidia-smi lock) + 核心偏移(NVAPI offset) + 显存档位 + 温度限制，自动锁定 + 重试机制(gpuCmd 2 retries 300ms)
- [x] **NVAPI 核心偏移模式联动**: MODE_PRESETS 加入 gpuCoreOffsetMhz，模式切换时 CustomEvent 同步 PerformancePanel 偏移滑块 + 异步下发 NVAPI
- [x] **GPU 温度限制模式联动**: CustomEvent(`gpu-thermal-updated`) 乐观更新 UI + `applyNvapiThermalLimit` 异步下发
- [x] **排序跳动修复**: SortableCard 高度锁定(800ms useLayoutEffect) + `break-inside: avoid` + localStorage 优先(跳过服务端覆盖)
- [x] **SMU 死代码清理**: 移除 PerformancePanel 中未使用的 smuInfo/smuError 状态和 3 秒延迟 fetch
- [x] **恢复预设按钮**: 模式选择 Card 右上角 `action` 按钮，恢复当前模式 CPU+GPU+风扇全量出厂值
- [x] **模式切换滑块联动**: 切换模式时 `uxtuParams` 跟随 `MODE_PRESETS` 更新，滑块/开关同步跳转
- [x] **每模式独立参数记忆**: localStorage 按模式名隔离 key（`douzhanzhe_params_`+模式名），切换模式时恢复该模式上次调的值，点"恢复预设"才重置
- [x] **模式独立配置管理**: 每个模式独立保存风扇/CPU/GPU参数，切换模式时全量写入

### 前端 — 系统功能
- [x] 主题切换（4 套皮肤）
- [x] 导航标签页 + 持久化
- [x] 键盘背光滑块 UI
- [x] 系统开关 UI（Fn 锁 / NumLock / CapsLock / 触摸板锁）
- [x] GPL v3 许可证 + 技术信息面板
- [x] Toast 通知反馈
- [x] CHANGELOG.md + README.md
- [x] **版本更新推送**: 检测 GitHub Release 最新版本，启动时自动检查 + 弹窗提示（支持跳过/稍后/前往下载），设置页可手动检查
- [x] **模式切换 SMU 双发**: 模式按钮点击时 EC 切换前后双发 SMU（防固件覆盖），温度墙/功耗墙实时生效
- [x] **CPU 调节实时下发修复**: 频率限制、关睿频、核心数滑块追加 queueSmu 实时调用（不再只改 state）
- [x] **WinRing0 自动加载**: run.ps1 (Start-Process -Verb RunAs) + Program.cs 启动时自动提权加载驱动
- [x] **reload-fe.ps1 部署路径修复**: 部署目标从 server/api/wwwroot 改为 server/api/bin/run/wwwroot（服务器实际读取路径）

### 文档
- [x] dev-api.md — C# HAL / Node.js 所有端点定义
- [x] dev-ec-map.md — EC 寄存器全表（已验证状态）
- [x] dev-architecture.md — 系统分层 + 数据流
- [x] dev-backend.md — 后端架构
- [x] dev-frontend.md — 组件树/状态管理
- [x] dev-release-plan.md — Release 1 对比表
- [x] douzhanzhe-progress.md — 项目进度快照
- [x] `dev-api.md`: Vite 代理表端口误标为 :3099 → 修复为 :3100 ✅

### 开发流程
- [x] **写入策略改造**: 放弃 Python 管道，恢复编辑器工具写入 + `_verify_write.py` 后验清洗，copilot-instructions.md §1 重写、主记忆同步
- [x] **反覆写/反回滚规则**: copilot-instructions.md §2 新增"禁止全量覆写"和"禁止隐式回滚"，§3 新增"跨会话衔接契约"
- [x] **`_verify_write.py --check` 模式**: 新增只读诊断模式用于前置破幻对账，守则 §1 更新引用，主记忆同步
- [x] **主记忆跨会话加载流程**: 主记忆顶部新增 5 步加载流程，明确新会话时如何读取本文件+守则+速查表+文档地图
- [x] **会话归档重构**: 98 个会话去重为 26 个独立条目，时间倒序排列，顶部新增速查表，体积从 86KB→24KB
- [x] **`.ship` 流程改进**: 会话归档新增去重检查、定位插入（速查表分隔线处）、速查表同步；新增第 4 步 GitHub 同步（git add→commit→push）
- [x] **速查表滚动截断**: session-archive.md 速查表限 15 行+汇总行，溢出时自动删除最旧行
- [x] **主记忆参考表重构+reference-consoles.md 速查表**: 主记忆 §3 从 3 行扩展为 7 行（BellatorFanControl/UXTU/EnumDLL/nvidia-smi），reference-consoles.md 新增顶部速查表、修复章节编号
- [x] **主记忆 §3/§4 追加近期摘要**: 已知遗留问题追加 2 行、会话归档追加 3 行近期摘要信息
- [x] **`dev-api.md` 顶部维护规则**: 新增分组追加/废弃/同步等更新规则
- [x] **`dev-backend.md` 顶部维护规则**: 新增服务表/架构层/驱动链等更新规则
- [x] **`dev-ec-map.md` 顶部维护规则**: 新增寄存器追加/验证标记/废弃处理等更新规则
- [x] **`dev-frontend.md` 顶部维护规则**: 新增组件树/接口/依赖等更新规则
- [x] **`dev-index.md` 顶部维护规则**: 新增技术栈/部署/索引等更新规则
- [x] **`dev-architecture.md` 顶部维护规则**: 新增架构图/数据流/部署表等更新规则
- [x] **`dev-release-plan.md` 顶部维护规则**: 新增对比表/原则同步等更新规则
- [x] **`reference-consoles.md` 顶部维护规则**: 新增参考项目/结论表等更新规则
- [x] **审计修复 P0 — `.ship` 同步规则补齐**: 新增"§1文档地图同步"，区分"§2近期工作"vs"§4近期摘要"- [x] **审计修复 P1/P2**: dev-release-plan 规则+标题、dev-known-issues 规则+拼写、copilot-instructions §4、"地图"→"映射"、"API Interface Defs"→"API 接口定义"
- [x] **GSD 流程清理**: .plan 模板/verify fail/§6 服务重启 3 项死规则修复

### 清理
- [x] ~~`tools/`: 清理 WinRing0x64 残留~~ ✅ WinRing0x64.dll/.sys 已清除（文件已从 `server/tools/` 移除）
- [x] ~~`tools/`: 移除 WinRing0x64.dll + 废弃 ryzenadj.exe~~ ✅ WinRing0x64.dll/.sys 已清除；❌ ryzenadj.exe 仍为 SmuController 运行时依赖，暂无替代方案

### 已修复 Bug
- [x] ~~mockTelemetry.js cpuCores:32~~ — 已修复
- [x] **模式切换竞态修复 (v1.3.1)**: onClick 精简为 setSettings + toast，dispatchFullMode 内部 SMU 两次延迟重发（500ms + 1500ms），消除前端重复 dispatch — 临时修复，根本方案见 [参数覆盖层重构](design-override-layer.md)
- [x] **前端模式按钮高亮加载时序**: 已修复 — uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams，消除 CPU 调节滑块闪跳
- [x] **版本号不一致（CHANGELOG/iss/SettingsPanel/package.json）**: 已修复 — build-installer.ps1 新增 `-Version` 参数，构建时自动同步四处版本号 + 打包前自动验证前端版本号
- [x] **WebView2 缓存导致前端版本号不更新**: 已修复 — Shell 启动时自动清除 Cache/Code Cache/GPUCache/Service Worker/Storage
- [x] SortableDashboard.jsx 重复属性 `showGpu={false}` — 仅一处，非重复 ✅
- [x] 历史曲线图 Sparkline NaN 渲染 — 兜底修复 ✅
- [x] `SettingsPanel.jsx` — `osdDisabled` Toast 提示"暂不支持" ✅

## 附录

### 架构迁移对账
| 旧工具 | 功能 | 新架构位置 | 状态 |
|--------|------|-----------|:----:|
| `ec_reader.cs/.exe` | EC IO 读温度/风扇 | C# HAL `ReadEc()` + Node.js `ec_reader.exe` | ✅ |
| `ec_kb_map.exe` | 键盘背光 0x9A 写入 | C# HAL `KeyboardBrightness` | ✅ |
| `ec_writer.cs/.exe` | EC IO 写风扇 0xB2/0xB3 | `HardwareAbstractionLayer` 已实现 (寄存器写入✅, 物理响应❌) | ✅ 🚧 |
| `demo.bat` | ryzenadj SMU 参数下发 | C# `POST /api/smu/set` | ✅ |
| `WinRing0x64` | 内核驱动 (GPL) | inpoutx64 (MIT) | ✅ |
| `ryzenadj.exe` | SMU CLI | SmuController → ryzenadj.exe → WinRing0 | ✅ |
| `libryzenadj.dll` | SMU C API | ❌ 已从跟踪中删除，由 ryzenadj.exe 替代 | ✅ |
| `readjustService.ps1` | SMU 后台监视器 | ❌ 未迁移 | 🚧 |

### 历史记录
- 2026-06-03: C# HAL 架构 + 双后端部署
- 2026-06-04: AppBridge + 电源计划 + Debug 页 + WS 遥测 + UI重构
- 2026-06-05: GPU 模式真机验证 / SmuController 全链路 / Step C/D/G
- 2026-06-05: 任务看板重构为三板块（Release 1 优先 / 后续 / 已完成）
- 2026-06-06: EC 16 位风扇竞态修复 (HAL 双读仲裁) + TelemetryBackgroundService 无条件推送 250ms + HAL 文档同步
- 2026-06-06: 风扇负载曲线隐藏 (EC 16 位竞态心电图) — 前端 `<Sparkline>` 移除 + `fanPctSeries` 清理 + 文档同步
- 2026-06-06: Vite dev server 废弃 — 前端嵌入 C# wwwroot/，清理 dev/start/server 脚本 + concurrently + 文档架构图
- 2026-06-06: 风扇控制全栈后端 (C# HAL/API/Debug) + WriteEc 0x80→0x81 修复 + WaitEcReady 轮询
- - 2026-06-06: 电源计划按钮修复 — PerformancePanel.jsx POWER_PLANS 添加 halValue，按钮点击实际下发 C# HAL
- - 2026-06-06: 持久化修复 — 风扇滑块/电压偏移 localStorage, fetch 非 custom 模式不覆盖, uxtuParams 初始值改 MODE_PRESETS
- 2026-06-08: GPU 统一卡片 + NVAPI 偏移模式联动 + SMU BatchApply + CPU 性能控制 + 排序跳动修复 + SMU 死代码清理

---

> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

#### SMU (2026-06-05)
- [x] **SMU 写入验证**：Dragon Range 地址确认 + ryzenadj 子进程写入 25W 功率墙成功 ✅
- [x] **C# 后端集成**: 子进程 0xC0000005 已解决（接受为成功退出码） ✅
- [x] **Debug 页按钮**: SMU 功率/温度设置按钮已整合 ✅
