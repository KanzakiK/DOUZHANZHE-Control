# 整体架构

> **📋 更新规则**：
> - 架构分层/数据流变更 → 更新系统分层图和数据流说明
> - 新增/废弃服务 → 更新部署架构表
> - 同步更新主记忆 §1 文档地图中 `dev-architecture.md` 的描述

[TOC]

## 系统分层

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
                 │  风扇/背光/散热          │
                 ├─────────────────────────┤
                 │  SmuController           │
                 │  (ryzenadj 子进程)        │
                 ├─────────────────────────┤
                 │  GpuController           │
                 │  (nvidia-smi 子进程)      │
                 └─────────────────────────┘
                 WebSocket /ws
                           |
                    Windows 内核 / 硬件
```

> Node.js 辅助服务已退役，全功能迁至 C#。
详见：
- [后端架构](dev-backend.md) — C# HAL 四层 (DriverBridge/HAL/SmuController/API)
- [前端架构](dev-frontend.md) — 组件树、状态管理、响应式、仪表盘自定义、主题

## 数据流总图

### 遥测
```
[硬件 EC 寄存器] -> inpoutx64 -> C# HAL (250ms poll)
  -> WebSocket /ws -> 前端 setTelemetry()

C# /ws 单一通道：250ms 推送全量遥测（温度/风扇/系统开关/CPU-GPU 信息）
```

### 控制
```
[前端滑块/开关] -> 500ms debounce -> POST /api/control (C# HAL)
                              +-> POST /api/fan/set-target (C# HAL EC 0x5F/0x5B)
                              +-> POST /api/fan/restore (C# HAL WmiInterface)
                              +-> POST /api/smu/set (C# SmuController)
                              +-> POST /api/gpu/set (C# GpuController)

全部硬件控制由 C# HAL 完成：
  • WmiInterface: GPUMode/FnLock/TPLock/PowerPlan
  • DriverBridge: 风扇0x5F/0x5B/背光0x9A/Fn锁/散热0xE4
  • SmuController: SetPowerLimit/SetTempLimit/Probe
  • GpuController: nvidia-smi 子进程封装
  • JSON 持久化: /api/custom-params, /api/ui-state (C#)
```

### 持久化
```
localStorage (即时) <-> [前端状态]
  | 启动时加载
C# JSON (config/*.json) <- POST /api/custom-params /api/ui-state (退出编辑/1s去抖)
```

## 服务部署

| 服务 | 端口 | 技术 | 职责 |
|------|------|------|------|
| C# HAL API | :3100 | .NET 8 Minimal API | 遥测、EC 直写、WebSocket、SMU、GPU、Debug、配置持久化 |

> Vite Dev Server (:5173) 和 Node.js (:3099) 均已退役。前端由 C# `wwwroot/` 自托管。

## 驱动依赖

- **inpoutx64 (MIT)** — 核心内核驱动，通过 inpoutx64.dll P/Invoke 调用
  - EC 寄存器直写 (EC IO 协议 0x62/0x66)
  - SMU 物理地址直写 (SetPhysLong/GetPhysLong)
  - 32/8-bit IO 端口读写
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
