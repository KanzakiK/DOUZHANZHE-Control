# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

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

### 技术堆栈
- 前端：React 19 + Vite 8 + Tailwind CSS 3 + dnd-kit
- 后端：Node.js + Express 5 + WebSocket (ws)
- SMU 控制：RyzenAdj (LGPL-3.0)
- EC/硬件访问：inpoutx64 (MIT)

### 变更
- 替换 WinRing0x64 (OpenLibSys, All Rights Reserved) 为 inpoutx64 (MIT 开源)
- 移除"应用参数"按钮，改为参数变化自动应用（500ms 去抖）
- 移除不必要的实验工具文件（ec_writer, ec_phys, ec_kb_ctrl, ec_scan_v2 等）
- 简化风扇读取逻辑：独立串行化 CPU/GPU 风扇读取避免 EC 状态干扰