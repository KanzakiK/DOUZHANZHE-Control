# Douzhanzhe Console

**斗战者控制台** — Lenovo Legion N176 2025 (宝龙达 OEM) 开源硬件控制面板。

替代官方联想电脑管家，提供完整的硬件监控、性能调优和系统控制能力。

---

## 功能

| 类别 | 功能 | 状态 |
|:-----|:-----|:----:|
| 遥测 | CPU/GPU 占用率、温度、频率、显存、风扇转速实时监控 (WebSocket) | ✅ |
| CPU 散热模式 | 静音/均衡/野兽/自定义 — EC ITSM 寄存器直写 | ✅ |
| CPU 风扇 | 独立 EC 寄存器 `0x5F` 写入，精确 RPM/100 控制 | ✅ |
| GPU 风扇 | 独立 EC 寄存器 `0x5B` 写入，精确 RPM/100 控制 | ✅ |
| GPU 模式 | 混合/集显/独显 — WMI MiInterface 切换 | ✅ |
| GPU 锁频 | nvidia-smi `--lock-gpu-clocks` 核心频率锁定 | ✅ |
| GPU 遥测 | 温度、功率、频率 (nvidia-smi) | ✅ |
| SMU 调优 | 功耗墙 (PPT)、温度墙 (TDC/EDC) — SmuController + RyzenAdj | ✅ |
| SMU 探测 | SMU 固件握手 PROBE_OK 验证 | ✅ |
| 键盘背光 | 0-3 级亮度 — 物理内存 `0xFE80049A` 直写 | ✅ |
| FnLock | 功能键锁定切换 — WMI MiInterface 方法 11 | ✅ |
| 电源计划 | Windows 电源方案切换 (powrprof.dll P/Invoke) | ✅ |
| CapsLock/NumLock | Win32 keybd_event 模拟切换 | ✅ |
| 自定义仪表盘 | @dnd-kit 拖拽排序、模块隐藏/显示 | ✅ |
| 主题 | 赛博霓虹、极简现代、极客数据、机甲紫黑 | ✅ |
| 状态持久化 | 本地 (localStorage) + 服务端 (JSON) 双重保障 | ✅ |
| Debug 面板 | C# HAL `/debug` 内联 HTML，按钮/滑块直接测试硬件 | ✅ |

---

## 架构

```
                       浏览器 (React SPA)
                        :5173 Vite Dev / :3100 生产
                           |
                    C# HAL API (:3100)
       ┌──────────────────────────────────┐
       │  WmiInterface  (WMI MiInterface) │
       │  DriverBridge  (inpoutx64 EC 直写)│
       │  SmuController (RyzenAdj 子进程) │
       │  WebSocket     (500ms 遥测推送)  │
       │  Debug 页面    (内联 HTML 测试)  │
       └──────────────────────────────────┘
            |                |
      Windows 内核       Node.js (:3099)
      / 硬件             (可选：JSON 持久化)
```

**C# HAL 三层架构**：

| 层 | 文件 | 职责 |
|:---|:-----|:-----|
| **DriverBridge** | `server/hal/DriverBridge.cs` | inpoutx64 P/Invoke 单例桥接 (Inp32/Out32/ReadPhys32/WritePhys32) |
| **HAL** | `server/hal/HardwareAbstractionLayer.cs` | EC 寄存器语义化映射 (ThermalMode/FanControl/KbLight/CpuFan/GpuFan) |
| **SMU** | `server/hal/SmuController.cs` | RyzenAdj 子进程封装 (Probe/SetPowerLimit/SetTempLimit) |
| **API** | `server/api/Program.cs` | Minimal API 端点 + WebSocket 遥测推送 + Debug 页面 |

> Node.js (`server/server.js`) 仅为可选辅助服务，提供 UI 配置 JSON 持久化。SMU、遥测、硬件控制全部在 C# HAL 完成。

---

## 快速开始

### 前置要求

| 工具 | 版本 | 验证 |
|:-----|:-----|:-----|
| .NET SDK | 8.0 (net8.0-windows) | `dotnet --list-sdks` |
| Node.js | >= 18 | `node --version` |
| npm | >= 9 | `npm --version` |
| OS | Windows 10/11 x64 | — |
| 权限 | **管理员身份** | C# HAL + inpoutx64 需要 |

### 启动

```powershell
# 终端 1 — C# HAL API (唯一必需，必须管理员)
cd server/api
dotnet run --urls http://0.0.0.0:3100
# Debug 面板: http://127.0.0.1:3100/debug

# 终端 2 — Vite 前端
npx vite --host 0.0.0.0 --port 5173

# 终端 3 — Node.js 配置持久化 (可选)
cd server && node server.js
```

### 调试入口

| 地址 | 用途 |
|:-----|:------|
| `http://127.0.0.1:3100/debug` | C# HAL — 所有硬件控制按钮/滑块 (EC/WMI/SMU/Fan) |
| `http://127.0.0.1:3100/api/health` | C# HAL 健康检查 |
| `http://localhost:5173` | 前端 Vite 开发服务器 |
| `http://127.0.0.1:3099` | Node.js 配置持久化服务 (不启动不影响核心功能) |

---

## 技术栈

| 层级 | 技术 | 许可证 |
|:-----|:-----|:-------|
| 前端 | React 19 + Vite 8 + Tailwind CSS 3 + @dnd-kit | MIT |
| C# HAL | .NET 8 + Minimal API + WMI + inpoutx64 | MIT |
| SMU | RyzenAdj (SmuController 子进程调用) | LGPL-3.0 |
| 硬件直连 | inpoutx64 (ReadPhys32 / WritePhys32) | MIT |
| EC 寄存器 | DSDT 反编译 + EC IO 端口 `0x62`/`0x66` | — |
| GPU 控制 | nvidia-smi 锁频 + WMI GPUMode | Proprietary |
| 配置持久化 | Node.js Express 5 (可选辅助服务) | MIT |

---

## 文档

| 文档 | 说明 |
|:-----|:------|
| [整体架构](docs/dev-architecture.md) | 系统分层、Vite 代理、数据流 |
| [后端架构](docs/dev-backend.md) | C# HAL 三层 + Node.js 辅助服务 |
| [前端架构](docs/dev-frontend.md) | 组件树、状态管理、布局 |
| [EC 寄存器地图](docs/dev-ec-map.md) | DSDT 反编译确认的完整 EC 寄存器表 (150+ 寄存器) |
| [API 定义](docs/dev-api.md) | C# HAL + Node.js 所有端点 |
| [任务看板](docs/dev-task-board.md) | 已完成 / 待开发 / Bug |
| [官方控制台参考](docs/reference-consoles.md) | 斗战者/蛟龙 DLL 路径、WMI 功能对比 |
| [会话归档](docs/session-archive.md) | 开发迭代日志 |

---

## 参考项目

- [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit) — 官方 LLT 开源实现 (本机宝龙达 OEM 模具不兼容)
- [Universal x86 Tuning Utility](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility) — UXTU 通用调优工具
- [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) — AMD SMU 控制接口
- [inpoutx64](https://www.highrez.co.uk/downloads/inpout32/) — 用户态物理内存/IO 访问

---

## 许可证

GNU General Public License v3.0
