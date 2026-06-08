# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [1.2.0] — 2026-06-08

### 新增

- **自定义散热曲线**: 独立标签页，SVG 可视化温度-转速曲线编辑器，支持保存/加载/启停/恢复预设，后台 FanCurveService 定时执行 (FanCurvePanel.jsx + FanCurveService.cs)
- **GPU 模式持久化**: 用户选择的 GPU 模式 (混合/集显/独显) 写入 `gpu-mode.json`，服务重启后自动通过 WMI 恢复，解决 EC 寄存器掉电重置问题

### 变更

- 散热曲线面板操作按钮和状态栏移至顶部，图表区域视觉更突出
- 首页模式选择卡片移至仪表盘最上方，优先于所有功能卡片
- 统一所有恢复按钮命名为"恢复预设"（原"重置 CPU 限制"、"重置 GPU"、"恢复默认"）
- 更新默认仪表盘卡片排列顺序和隐藏列表 (useCardOrder.js)
- README 重写为中英双语简洁实用风格

### 仓库维护

- `.gitignore` 新增忽略规则: `.qoder/`、`.instructions.md`、`_enum_dll_proj/`、`_lab/`、`_tmp_decompile/`、`_*.csx`、`docs/`、`server/api/config/*.json`、`.venv/`、`.github/`
- 从 Git 跟踪中移除 158 个非源码文件（工具元数据、临时实验目录、开发文档、运行时配置等），磁盘文件保留不变

## [1.1.0] — 2026-06-05

### 新增

- **C# HAL 后端** (`server/api/`, `server/hal/`): .NET 8 Minimal API 替代 AppBridge
  - WmiInterface: WMI MiInterface 直通 (GPUMode/FnLock/TPLock/温度/风扇)
  - DriverBridge: inpoutx64 P/Invoke 单例 (Inp32/Out32/ReadPhys32/WritePhys32)
  - HardwareAbstractionLayer: EC 寄存器语义化映射 (ThermalMode/FanControl/KbLight)
  - SmuController: RyzenAdj 子进程封装 (Probe/SetPowerLimit/SetTempLimit)
  - TelemetryBackgroundService: WebSocket 28 字段全量遥测推送
  - Debug 页面: `/debug` 内联 HTML + CSS (GitHub Dark 风格)，含 EC/WMI/SMU/Fan 全功能测试按钮
- **GPU 模式控制**: WMI MiInterface 方法 9 (混合/集显/独显) ✅
- **GPU 锁频**: nvidia-smi `--lock-gpu-clocks` 频率锁定 (1000MHz 验证) ✅
- **FnLock 控制**: WMI MiInterface 方法 11 + 物理内存 WriteBit 双路径 ✅
- **CpuFanControl**: EC 寄存器 `0x5F` WriteEc 直接生效 (RPM/100) ✅
- **GpuFanControl**: EC 寄存器 `0x5B` WriteEc 直接生效 (RPM/100) ✅
- **SMU 集成**: RyzenAdj 子进程 — Probe/SetPowerLimit/SetTempLimit，含 0xC0000005 优雅退出码适配
- **文档系统**: 完整 10 份开发文档 (架构/后端/前端/EC 寄存器图/API 定义/任务看板/发布计划/会话归档/参考/看板约定)
- **Git 规范化**: `.gitignore` 黑名单补全 (`**/bin/` `**/obj/` `**/wwwroot/`) + `.gitattributes` 行尾规范/语义 diff

### 变更

- AppBridge 退役: `AppLib.cs` + `AppBridge/` 子项目完全删除，全功能由 WmiInterface 替代
- SMU 控制重构: 从 Node.js child_process 迁移至 C# SmuController.Popen() 统一管理
- C# HAL API 从 `server/api/Program.cs` 自包含发布，`run.ps1` 自动化 build+copy+launch
- Debug 页面 GitHub Dark 横向布局重构
- Vite 代理配置支持双后端分流 (:3100 C# HAL / :3099 Node.js)
- 已验证 EC 寄存器:
  - 散热模式 `ITSM` (WritePhys 0xFE8004E4) ✅
  - 键盘背光 `kb_light` (SetPhysLong 0xFE80049A) ✅
  - CPU 温度 (EC IO 0x1C) / GPU 温度 (nvidia-smi) ✅
  - CPU 风扇 (EC IO 0x9D/0x9E) / GPU 风扇 (EC IO 0x96/0x97) ✅
  - 弃用 `0xB2/0xB3` 风扇路径，更正为 `0x5F`(大扇)/`0x5B`(小扇)

### 移除

- `server/tools/`: 删除 WinRing0x64.dll/sys、ec_writer.exe、RyzenAdj Service 脚本等 8 个冗余文件
- AppBridge 依赖: 斗战者官方控制台 BLD.WMIOperation 不再需要

## [1.0.0] — 2026-06-03

### 新增

- 实时遥测监控面板 (CPU/GPU/内存/硬盘/风扇)
- CPU 调节：功耗墙、温度墙、核心数限制、睿频、电源计划
- GPU 调节：功耗墙、温度墙、核心/显存超频偏移、频率锁定
- 风扇控制：目标转速滑块、全速模式开关
- 键盘背光控制：0-3 级亮度滑块（物理内存映射）
- 模式预设：静音/办公/游戏/狂野 — 一键切换，含完整 GPU 参数
- 自定义模式：用户自由调整参数，持久化到服务端
- 自定义仪表盘：@dnd-kit 拖拽排序、模块隐藏/显示
- 状态持久化：本地 (localStorage) + 服务端 (JSON 文件) 双重保障
- 主题切换：赛博霓虹、极简现代、极客数据、机甲紫黑
- Toast 通知反馈（保存成功/失败）
- 当前策略摘要卡片
- 关于/技术信息（GPL v3 许可证、开发信息）

### 变更

- 替换 WinRing0x64 (OpenLibSys, All Rights Reserved) 为 inpoutx64 (MIT 开源)
- 移除"应用参数"按钮，改为参数变化自动应用（500ms 去抖）
- 移除不必要的实验工具文件（ec_writer, ec_phys, ec_kb_ctrl, ec_scan_v2 等）
- 简化风扇读取逻辑：独立串行化 CPU/GPU 风扇读取避免 EC 状态干扰
