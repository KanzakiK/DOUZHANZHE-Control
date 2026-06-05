# 整体架构

[TOC]

## 系统分层

```
                   浏览器 (React SPA)
                   :5173 Vite Dev Server + Proxy
                       |
                   Vite Proxy 分流
                  /                  \
         C# HAL API               Node.js
         :3100                     :3099
         ┌─────────────────┐      JSON 持久化
         │  WmiInterface   │      SMU (遗留)
         │  (System.Manage)│      nvidia-smi
         │  GPU/Fn/TP/恢复 │
         │─────────────── │
         │DriverBridge     │
         │inpoutx64 EC直写 │
         │风扇/背光/散热   │
         │SmuController    │
         └─────────────────┘
         WebSocket
                  \                  /
                   Windows 内核 / 硬件
```

### 打包部署架构 (生产环境)

```
                   浏览器 (React SPA)
                   访问 127.0.0.1:3100
                           |
                   C# HAL API (单一进程)
                   :3100
                   ┌─────────────────────────┐
                   │  wwwroot/ (静态文件)     │
                   │  UseStaticFiles()        │
                   │  MapFallbackToFile()     │
                   ├─────────────────────────┤
                   │  WmiInterface            │
                   │  (System.Management)     │
                   │  GPU/Fn/TP/恢复固件      │
                   ├─────────────────────────┤
                   │  DriverBridge            │
                   │  inpoutx64 EC 直写       │
                   │  风扇/背光/散热/SMU      │
                   ├─────────────────────────┤
                   │  Node.js 遗留路由代理    │
                   │  (vite build → /api/ →   │
                   │   C# MapProxy → :3099)  │
                   └─────────────────────────┘
                           |
                    Windows 内核 / 硬件
```

> **生产部署**：`npm start` 执行 `vite build` → 静态文件输出到 `wwwroot/` → C# 用 `UseStaticFiles()` + `MapFallbackToFile("index.html")` 自托管前端。C# 反向代理 `/api/*` 到 Node.js :3099 遗留端点。**单一端口 :3100**，Vite Dev Server 不复存在。
详见：
- [后端架构](dev-backend.md) — C# HAL 四层 (DriverBridge/HAL/SmuController/API) + Node.js
- [前端架构](dev-frontend.md) — 组件树、状态管理、响应式、仪表盘自定义、主题

## 数据流总图

### 遥测
```
[硬件 EC 寄存器] -> inpoutx64 -> C# HAL (500ms poll)
  -> WebSocket /ws -> 前端 setTelemetry()
  +-> Node.js (3s poll: nvidia-smi + ec_reader + WMI) -> Express GET /api/telemetry

WebSocket 双通道：
  C# /ws  ← 500ms 心跳（EC 直读：温度/风扇/系统开关状态）
  Node.js 无 WS 通道，前端通过 REST GET /api/telemetry 获取全量遥测
```

### 控制
```
[前端滑块/开关] -> 500ms debounce -> POST /api/control (C# HAL EC 直写 + WmiInterface)
                              +-> POST /api/fan/set-target (C# HAL EC 0x5F/0x5B)
                              +-> POST /api/fan/restore (WmiInterface MaxFanSwitch=0)
                              +-> POST /api/smu/set (C# SmuController)
                              +-> POST /api/uxtu/apply (Node.js -> 遗留 ryzenadj, 回退通道)

C# HAL EC 直写(WmiInterface)：GPUMode(9)/FnLock(11)/TPLock(12)/Power_Plan/恢复固件
C# HAL EC 直写(DriverBridge)：风扇0x5F/0x5B/键盘背光0x9A/Fn锁0x20/散热0xE4/集显0xFED81E56
C# SmuController：SetPowerLimit(mW) / SetTempLimit(°C) / Probe
Node.js 适用：JSON 持久化 (custom-params/ui-state/default-config)
~~AppBridge(斗战者.dll)~~ ❌ 已废弃，全部由 WmiInterface 替代
```

### 持久化
```
localStorage (即时) <-> [前端状态]
  | 启动时加载
服务端 JSON (Node.js: config/*.json) <- POST /api/* (退出编辑/1s去抖)
```

## 双后端部署

| 服务 | 端口 | 技术 | 职责 |
|------|------|------|------|
| C# HAL API | :3100 | .NET 8 | 遥测、EC 直写、WebSocket、SmuController SMU、AppBridge 代理 |
| Node.js | :3099 | Express 5 | UI 持久化 JSON、遥测补充、SMU (遗留 ryzenadj) |
| AppBridge | 子进程 | net8.0-windows | 反射调用官方斗战者.dll（GPU Mode 主要） |
| SmuController | 内联 | inpoutx64 | SMU 物理地址直写，零外部依赖 |
| Vite | :5173 | React 19 | 前端开发服务器 |

## Vite 代理规则

| 路径 | 目标 |
|------|------|
| /api/telemetry, /api/control, /api/health, /api/discover, /api/smu, /ws | C# :3100 |
| /api/uxtu, /api/system, /api/ryzenadj, /api/fan, /api/custom-params, /api/ui-state, /api/default-config | Node :3099 |

前端的 WebSocket 直连 ws://127.0.0.1:3100/ws (绕过 Vite 代理)。

## 驱动依赖

- **inpoutx64 (MIT)** — 核心内核驱动，通过 inpoutx64.dll P/Invoke 调用
  - EC 寄存器直写 (EC IO 协议 0x62/0x66)
  - SMU 物理地址直写 (SetPhysLong/GetPhysLong)
  - 32/8-bit IO 端口读写
- **斗战者控制台.dll** — 部署时自动复制，仅 GPU Mode 切换必需
- 必须以管理员权限运行 C# API
- 驱动首次加载自动触发

## 已知架构限制

- MapPhysToLin 预映射缓存写入无效，KBNL 用 SetPhysLong 单地址写入
- SetPhysLong 只能访问 32 位物理地址 (< 4GB)
- Dragon Range (R9 8940HX) 硬件锁定 SMU PM 表 API，无法读取实时功率表值；但 SMU 写命令正常可用
- C# DLL 被锁定导致编译失败 (dotnet build --force 到临时目录绕过)

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
