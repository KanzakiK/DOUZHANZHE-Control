# 斗战者控制台 开发文档

[TOC]

## 项目概述
联想 Legion N176 2025 (宝龙达 OEM) 硬件控制面板，开源替代官方联想电脑管家。

## 文档索引

| 文档 | 说明 |
|------|------|
| [整体架构](dev-architecture.md) | 系统总览：双后端部署、Vite 代理、数据流 |
| [后端架构](dev-backend.md) | C# HAL 三层（DriverBridge/HAL/API）+ Node.js |
| [前端架构](dev-frontend.md) | 组件树、状态管理、自适应、仪表盘自定义、主题 |
| [EC 寄存器地图](dev-ec-map.md) | DSDT 反编译确认的完整 EC 寄存器表 |
| [API 接口定义](dev-api.md) | C# HAL 和 Node.js 后端的所有 API 端点 |
| [任务看板](dev-task-board.md) | 已完成功能 / 待开发 / 待修 Bug |
| [会话归档](session-archive.md) | 迭代日志历史记录 |
| [官方控制台参考](reference-consoles.md) | 斗战者/蛟龙功能详情与依赖关系 |
| [看板整理原则](task-board-conventions.md) | 任务看板排序规则与约定 |

> **项目主记忆**：`/memories/douzhanzhe-progress.md`（自动加载前 200 行，聚合项目状态/文档索引/关键参考/归档指针）
> **操作守则**：`.github/copilot-instructions.md`（AI 行为钢印：GSD 工作流/写入防火墙/熔断机制）

## 环境要求

| 工具 | 版本要求 | 验证命令 |
|------|---------|---------|
| Node.js | >= 18 | `node --version` |
| .NET SDK | 8.0 (net8.0-windows) | `dotnet --list-sdks` |
| npm | >= 9 | `npm --version` |
| OS | Windows 10/11 x64 | — |

**首次运行检查清单：**

- [ ] 以**管理员身份**打开 PowerShell/终端（C# HAL 需要驱动权限）
- [ ] `npm install` — 前端依赖安装
- [ ] `cd server && npm install` — Node.js 依赖安装
- [ ] `cd server/api && dotnet restore` — C# HAL 依赖恢复
- [ ] C# HAL 编译：`cd server/api && dotnet build`
- [ ] Node.js 启动：`cd server && node server.js`
- [ ] Vite 启动：`npx vite --host 0.0.0.0 --port 5173`

**调试指南：**
- C# HAL Debug 面板：`http://127.0.0.1:3100/debug`（按钮/滑块测试所有功能，WS 遥测可视化）
- Node.js Debug 面板：`http://127.0.0.1:3099/debug`（SMU 信息、风扇控制、参数管理）
- 前端 Vite：`http://localhost:5173`
- API 直接测试：`http://localhost:3100/api/health`（C# HAL 健康检查）
- WebSocket 遥测：浏览器 DevTools → Network → WS → `ws://127.0.0.1:3100/ws`

## 快速启动

```powershell
# 终端 1 - C# HAL API (必须管理员)
cd server/api
dotnet run --urls http://0.0.0.0:3100

# 终端 2 - Node.js 后端
cd server
node server.js

# 终端 3 - Vite 前端
npx vite --host 0.0.0.0 --port 5173
```
cd server/api
dotnet run --urls http://0.0.0.0:3100

# 终端 2 - Node.js
cd server
node server.js

# 终端 3 - Vite 前端
cd .
npx vite --host 0.0.0.0 --port 5173
`

## 技术栈
| 层级 | 技术 | 许可证 |
|------|------|--------|
| 前端 | React 19 + Vite 8 + Tailwind CSS 3 + dnd-kit | MIT |
| C# HAL | .NET 8 + Minimal API + inpoutx64 | MIT |
| Node.js | Express 5 + WebSocket (ws) | MIT |
| SMU | RyzenAdj (LGPL-3.0) | LGPL-3.0 |
| 驱动 | inpoutx64 (MIT) | MIT |

## 系统架构

```
浏览器 → Vite Dev Server (:5173) → Vite Proxy（详见 dev-architecture.md）
  ├── C# HAL API (:3100) — 遥测 + 硬件控制 + WebSocket
  └── Node.js Express (:3099) — SMU 调优 + 配置持久化 + 遗留端点
```

详见：[整体架构](dev-architecture.md) | [后端架构](dev-backend.md) | [前端架构](dev-frontend.md)

## 关键文件路径

### 后端
| 文件 | 说明 |
|------|------|
| server/hal/DriverBridge.cs | inpoutx64 P/Invoke 单例桥接层 |
| server/hal/HardwareAbstractionLayer.cs | EC 寄存器语义化映射层 |
| server/api/Program.cs | Minimal API 端点 + WebSocket |
| server/api/TelemetryBackgroundService.cs | 后台遥测心跳推送 |
| server/server.js | Node.js 后端（UI 持久化 + SMU）|
| server/tools/ec_kb_map.exe | 键盘背光控制独立工具 |
| server/tools/ec_reader.cs/exe | EC 寄存器读取工具 |
| ite.config.js | Vite 代理配置（分流 :3100 和 :3099）|

### 前端
| 文件 | 说明 |
|------|------|
| src/App.jsx | 根组件、导航、模式选择、自动下发 |
| src/components/SortableDashboard.jsx | 可拖拽仪表盘外壳 |
| src/components/panels/TelemetryPanel.jsx | CPU/GPU 监控 + 风扇控制 |
| src/components/panels/PerformancePanel.jsx | CPU/GPU 调优面板 |
| src/components/panels/SettingsPanel.jsx | 系统开关 + 键盘灯 + 关于 |
| src/components/panels/SystemInfoPanel.jsx | 系统信息展示 |
| src/hooks/useControlState.js | 中央状态管理器（遥测/设置/WS/历史） |
| src/hooks/useCardOrder.js | 卡片排序/隐藏持久化 |
| src/services/uxtuAdapter.js | API 客户端封装 |

## 参考项目
- [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit)
- [Universal x86 Tuning Utility](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility)
- [RyzenAdj](https://github.com/FlyGoat/RyzenAdj)
- [inpoutx64](https://www.highrez.co.uk/downloads/inpout32/)
