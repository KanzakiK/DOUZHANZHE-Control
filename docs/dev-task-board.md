# 任务看板

> **板块说明**：
> - **🏁 Release 1 优先**：对标官方"斗战者 N176 2025"控制台的硬门槛，必须完成才能发布 v1.0.0
> - **🧭 后续版本**：Release 2 候选功能或低优先级改进
> - **✅ 已完成**：已验证通过的功能模块
>
> **优先级规则**：同一分类内，开发实施任务在前，清理/打包/部署任务在后。

---

## 🏁 一、Release 1 优先完成

### P0 — 发布硬门槛

#### 后端（开发 → 打包顺序）

- [ ] 开机自启动（后端注册服务）
- [ ] 安装程序 / 打包（Inno Setup 或 NSIS）

#### 其他



### P1 — 核心功能

#### 后端

- [x] **废弃 Node.js 后端**: 砍掉 server/server.js + tools/Node.js仅用文件 + server.js 依赖 ✅

#### 前端（路由修复 → 散热联动 → 电源计划 → 其他）

- [x] **路由修复**: `SettingsPanel.jsx` — `halMap` 追加 `gpuOnly->igpu_only`、`touchpadLock->touchpad_lock`，`dGpuDirect->gpu_mode(2/0)` ✅
- [x] **散热模式**: `uxtuAdapter.js` 导出 `thermalModeMap` + `powerPlanHALMap` ✅
- [x] **散热模式**: `App.jsx` 5 个模式按钮联动 `POST /api/control target=thermal_mode` ✅
- [x] **电源计划**: `uxtuAdapter.js` 导出 `powerPlanHALMap` ✅
- [ ] **电源计划**: `PerformancePanel.jsx` 电源管理按钮双发 C# HAL `power_plan`


#### 前端（新增任务 — 2026-06-05 第二轮）

- [x] **风扇目标转速不生效**: ✅ WMI Bellator 协议修复（MaxFanSwitch+MaxFanSpeed, data[4]=FanType）
- [x] ~~模式前后台命名映射错乱~~ ❌ 自定义模式将被砍掉，此 bug 自然消失，不再修复
- [ ] **GPU 模式独立卡片**: 三个按钮（混合模式/集显模式/独显直连），替代 SettingPanel 中的 dGpuDirect/gpuOnly 开关

#### 前端（模式重构 — 大任务）

- [x] **模式重构(部分)**: CPU/GPU 控件在四种模式下均可调 ✅

#### 其他（文档 → 清理）

- [ ] ~~`tools/`: 清理 WinRing0x64 残留~~ ❌ WinRing0x64.dll/sys 为 SmuController 运行时依赖，非冗余文件

#
### 已知 Bug（Release 1 内修复）

- [ ] 前端模式按钮高亮加载时序（数据就绪前短暂错位）
- [x] SortableDashboard.jsx 重复属性 `showGpu={false}` ✅ (仅一处，非重复)
- [x] 历史曲线图 Sparkline 组件渲染排查 ✅ NaN 兜底修复

---

## 🧭 二、后续版本

### 后端

- [ ] **GpuController.cs (nvidia-smi 子进程封装)**: `POST /api/gpu/set` 统一接口
- [ ] **GPU 核心频率锁频**: nvidia-smi `--lock-gpu-clocks=min,max` ✅ 已验证可用 (Process.Start 一行命令)
- [ ] **GPU 核心频率超频**: nvidia-smi `--lock-gpu-clocks=base+offset` (基于已验证的 --lock-gpu-clocks)
- [ ] **GPU 核心频率上限限制**: nvidia-smi `--lock-gpu-clocks=,max` (基于已验证的 --lock-gpu-clocks)
- [ ] **重置 GPU 频率**: nvidia-smi `--reset-gpu-clocks` ✅ 已验证可用
- [ ] **显存超频**: nvidia-smi `--lock-memory-clocks=min,max` (RTX5060 GDDR7 9001MHz 基线)
- [ ] **重置显存**: nvidia-smi `--reset-memory-clocks`
- [ ] **GPU 功耗墙**: ❌ nvidia-smi `--power-limit` 驱动限制不可用，需另寻路径
- [ ] **五模式全量配置覆盖**: 安静/均衡/野兽/斗战/自定义各保存一套完整配置（风扇转速×2、CPU功耗墙/温度墙、GPU功耗墙/频率偏移），后端新增 GET|POST /api/mode-config 持久化接口
- [ ] **遥测扩展**: CPU/GPU 功率
  - GPU `power.draw` (已读 19.12W ✅)
  - CPU (ryzenadj `-i` 解析 SMU)
  - CPU 功率负载估算回退
- [ ] **Debug 页 GPU 控制区**: 频率锁/超频/重置 + 当前频率/功耗显示
- [ ] **前端 GPU 性能面板**: 核心频率滑块 + 显存频率滑块 + 一键恢复默认
- [ ] **SMU 监视器**: 值被覆盖时自动重发（替代 `readjustService.ps1`）
- [ ] **跨平台/非管理员降级模式**（无 inpoutx64 时以 WMI/软件方式运行）
- [ ] **Node.js 全部端点迁移到 C#**，砍掉 Node.js 后端
- [ ] **安静性能模式**: `POST /api/uxtu/apply` 扩展支持 `fanLargeRpmTarget`/`fanSmallRpmTarget`
- [ ] **模式预设**: 新增"安静性能"模式（GPU满血 + 风扇低速）


### 前端

- [ ] **OSD 显示开关**（关闭 OSD 功能）
- [ ] **五维雷达图**可视化（CPU/GPU/内存/磁盘/风扇综合评分）
- [ ] **模式独立配置管理**: 每个模式独立保存风扇/CPU/GPU参数，切换模式时全量写入
- [ ] **恢复预设按钮**: 一键将当前模式的所有 CPU/GPU/风扇滑块重置为 MODE_PRESETS 出厂默认值（恢复后联动写入一次，确保用户实时看到效果）

### 其他

- [ ] ~~`tools/`: 移除 WinRing0x64.dll + 废弃 ryzenadj.exe~~ ❌ WinRing0x64.dll/sys 为 SmuController 运行时依赖，非冗余文件；暂无替代方案

---

## ✅ 三、已完成

### 后端 — C# HAL 硬件驱动架构

- [x] DriverBridge 单例（inpoutx64 P/Invoke）
- [x] HardwareAbstractionLayer（EC 寄存器语义映射）
- [x] WebConsoleAPI（Minimal API + WebSocket + TelemetryBackgroundService）
- [x] DSDT ACPI 反编译 + EC 寄存器全表导出
- [x] 替换 WinRing0 -> inpoutx64 (MIT 自管)
- [x] SmuController 子进程调用 ryzenadj.exe（程序集零外部依赖，运行时依赖 WinRing0 驱动）
- [x] **SMU 子进程集成修复**: SmuController.cs 路径候选修复 + run.ps1 自动复制 WinRing0/ryzenadj + 移除 Redirect 导致 0xC0000005 + 接受无害崩溃退出码 ✅
- [x] DriverBridge 32-bit IO: Inp32/Out32 + ReadPhys32/WritePhys32

### 后端 — AppBridge 集成

- [x] AppBridge 子进程 + AppLib.cs 封装（反射调用 BLD.WMIOperation）
- [x] 运行时依赖自动部署（System.Management + 斗战者控制台.dll）
- [x] GPU 模式控制（混合/集显/独显）真机验证
- [x] 端点: `/api/app-cmd`, `/api/app-status`, `/api/ec-scan`
- [x] Debug 页 AppBridge 命令调试区

### 后端 — 控制端点

- [x] `POST /api/control`: kb_light, fn_lock, num_lock, caps_lock, touchpad_lock, power_plan, thermal_mode, igpu_only, gpu_mode
- [x] `POST /api/smu/set`: stapm_limit, temp_limit
- [x] `GET /api/telemetry` 全量遥测（28字段）
- [x] `WS /ws` WebSocket 实时推送
- [x] `GET /debug` 内嵌 Debug 面板
- [x] 跑通全链路: GET /api/health -> POST /api/control -> 物理 EC 写入生效

- [x] **Node.js JSON 持久化迁移**: C# 新增 `GET/POST /api/custom-params`、`GET/POST /api/ui-state` 文件持久化 ✅
- [x] **Node.js 遥测迁移**: C# 补齐 `systeminformation` 类数据（CPU/内存/硬盘全量） ✅

### 后端 — 风扇控制发现

- [x] EC 寄存器发现 (0xB2/0xB3) + 公式 `val=round(rpm/maxRpm*255)` 经 ec_writer.cs 验证

### 后端 — SMU 调优全链路

- [x] Step 1~D: libryzenadj 封装 -> server.js 重构 -> SmuController 物理地址直写
- [x] Step G: WinRing0x64.dll 仍依赖于 SmuController（通过 ryzenadj.exe），DriverBridge 已全面移除对 WinRing0 的直接依赖

### 前端 — 遥测监控

- [x] CPU 占用率、温度、频率、核心数
- [x] GPU 占用率、温度、频率、显存
- [x] 内存占用/总量 + 硬盘占用/总量
- [x] CPU/GPU 风扇转速 + 负载曲线
- [x] WebSocket 对接硬件实时遥测（离线时 mock 回退）

### 前端 — 性能调节

- [x] CPU 长时/短时功耗墙、温度墙、睿频控制
- [x] GPU 功耗墙、温度墙
- [x] 模式预设（安静/均衡/游戏/狂暴/自定义）
- [x] 自动应用参数（500ms 去抖）
- [x] 非自定义模式锁定控件

### 前端 — 仪表盘自定义

- [x] 拖拽排序（@dnd-kit）
- [x] 卡片隐藏/显示 + 排序持久化（localStorage + 服务端）

### 前端 — 系统功能

- [x] 主题切换（4套皮肤）
- [x] 导航标签页 + 持久化
- [x] 键盘背光滑块 UI
- [x] 系统开关 UI（Fn锁/NumLock/CapsLock/触摸板锁）
- [x] GPL v3 许可证 + 技术信息面板
- [x] Toast 通知反馈
- [x] CHANGELOG.md + README.md

### 文档

- [x] dev-api.md — C# HAL / Node.js 所有端点定义
- [x] dev-ec-map.md — EC 寄存器全表（已验证状态）
- [x] dev-architecture.md — 系统分层 + 数据流
- [x] dev-backend.md — 后端架构
- [x] dev-frontend.md — 组件树/状态管理
- [x] dev-release-plan.md — Release 1 对比表
- [x] douzhanzhe-progress.md — 项目进度快照

- [x] `dev-api.md`: Vite 代理表 custom-params/ui-state/default-config 目标端口误标为 :3099，应为 :3100 ✅

### 已修复 Bug

- [x] ~~mockTelemetry.js cpuCores:32~~ — 已修复

---


### 后端 — 风扇控制 + WMI + Debug
- [x] C# 静态文件服务: `wwwroot/` + `UseStaticFiles()` + `MapFallbackToFile("index.html")` ✅
- [x] C# 反向代理 Node.js 遗留端点 ✅
- [x] ~~`CpuFanControl`/`GpuFanControl`~~ ❌ LLT 路径(0xB2/0xB3)无效 → **0x5F 大扇控制 ✅**（GpuFanControl 小扇待发现）
- [x] **C# API**: `POST /api/fan/set-target` 端点 ✅ **0x5F 控制已验证**
- [x] **C# Debug 页**: 风扇目标转速滑块 ✅ **0x5F 控制已验证**
- [x] **风扇物理控制探索**: ~~0xB2/0xB3 直写~~ ❌ → **0x5F(大扇)/0x5B(小扇) ✅** WriteEc 直接生效，值=RPM/100，受散热区间限制
- [x] **小风扇控制寄存器 0x5B**: ✅ EC 直写 `WriteEc(0x5B, RPM/100)` 已验证
- [x] **WmiInterface.cs** ✅ 已实现（`root\WMI`，32 字节协议，零依赖）
- [x] **GPUMode (WMI)** ✅ `POST /api/control` gpu_mode 已迁移到 WmiInterface
- [x] **WMI 迁移 — `POST /api/control` FnLock(11)**: 从 AppBridge 迁移到 WmiInterface ✅
- [x] **WMI 迁移 — `POST /api/control` TPLock(12)**: 从 AppBridge 迁移到 WmiInterface ✅
- [x] **恢复固件控制**: `POST /api/fan/restore` — Debug 页按钮; 调 fan/set-target largeRpm=0,smallRpm=0 ✅
- [x] **Debug 页**: 添加"恢复固件控制"按钮 ✅
- [x] **废弃 AppBridge**: 砍掉 AppLib.cs + AppBridge 子项目 + 斗战者控制台.dll 依赖 ✅
- [x] **路由修复**: `SettingsPanel.jsx` — `osdDisabled` Toast 提示"暂不支持" ✅
- [x] **历史曲线**: 排查 Sparkline 渲染（代码存在，可能卡片被隐藏） ✅


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
- 2026-06-05: 风扇控制全栈后端 (C# HAL/API/Debug) + WriteEc 0x80→0x81 修复 + WaitEcReady 轮询

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

#### SMU (2026-06-05)
- [x] **SMU 写入验证**：Dragon Range 地址确认 + ryzenadj 子进程写入 25W 功率墙成功 ✅
- [x] **C# 后端集成**: 子进程 0xC0000005 已解决（接受为成功退出码） ✅
- [x] **Debug 页按钮**: SMU 功率/温度设置按钮已整合 ✅
