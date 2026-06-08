# Douzhanzhe Console

**斗战者控制台** — Lenovo Legion N176 2025 (宝龙达 OEM) 开源硬件控制面板。
替代官方联想电脑管家，提供完整的硬件监控、性能调优和系统控制能力。

**Douzhanzhe Console** — Open-source hardware control panel for Lenovo Legion N176 2025 (BaoLongDa OEM).
A full-featured alternative to Lenovo Vantage for hardware monitoring, performance tuning, and system control.

---

## 功能 Features

**实时监控** — CPU/GPU 温度、频率、占用率、风扇转速、内存与磁盘，通过 WebSocket 每 500ms 推送。

**性能调优** — CPU 功耗墙/温度墙 (SMU via RyzenAdj)、GPU 功耗/超频偏移/锁频 (NVAPI + nvidia-smi)、四档模式预设一键切换。

**散热曲线** — 自定义温度-转速曲线编辑器，SVG 可视化预览，支持保存/加载/启停。

**GPU 模式** — 混合/集显/独显三档切换 (WMI MiInterface)，重启后自动恢复用户选择。

**系统控制** — 键盘背光亮度、FnLock、电源计划、CapsLock/NumLock、风扇转速直写 (EC 寄存器)。

**个性化** — @dnd-kit 拖拽排序仪表盘、模块隐藏/显示、四套主题皮肤。

---

## 快速开始 Quick Start

### 环境要求 Prerequisites

| 工具 Tool | 版本 Version | 验证 Command |
|:-----|:-----|:-----|
| .NET SDK | 8.0 (net8.0-windows) | `dotnet --list-sdks` |
| Node.js | >= 18 | `node --version` |
| OS | Windows 10/11 x64 | — |
| 权限 | **管理员身份** (Admin) | inpoutx64 + WMI 需要 |

### 开发 Development

```powershell
# 1. 启动后端 API（必须管理员）
cd server/api
dotnet run --urls http://0.0.0.0:3100

# 2. 启动前端开发服务器
npx vite --host 0.0.0.0 --port 5173
```

### 部署 Deployment

```powershell
# 构建前端 + 同步到后端 wwwroot
.\deploy.ps1

# 仅同步（跳过构建）
.\deploy.ps1 -SkipBuild

# 启动生产服务（管理员）
cd server/api/bin/build
dotnet Douzhanzhe.API.dll --urls=http://127.0.0.1:3100
```

生产模式访问 `http://127.0.0.1:3100`，Debug 面板在 `http://127.0.0.1:3100/debug`。

---

## 技术栈 Tech Stack

| 层级 Layer | 技术 |
|:-----|:-----|
| 前端 Frontend | React 19 + Vite 8 + TailwindCSS 3 + @dnd-kit |
| 后端 Backend | .NET 8 Minimal API + WMI + inpoutx64 |
| SMU 控制 | RyzenAdj (子进程调用) |
| GPU 控制 | nvidia-smi 锁频 + NVAPI 超频 + WMI 模式切换 |
| EC 直写 | inpoutx64 (IO 端口 0x62/0x66) |

---

## 项目结构 Structure

```
DOUZHANZHE-Control/
├── src/                        # React 前端
│   ├── App.jsx                 # 主布局 + 标签页路由
│   ├── components/
│   │   ├── SortableDashboard   # 拖拽排序仪表盘
│   │   ├── panels/             # 性能/散热曲线/遥测/系统/设置面板
│   │   └── ui/                 # Card, Gauge, Toast 等通用组件
│   ├── hooks/                  # useCardOrder, useControlState
│   └── services/               # uxtuAdapter (API 通信)
├── server/
│   ├── api/                    # ASP.NET 8 后端
│   │   ├── Program.cs          # Minimal API 端点 + WebSocket 遥测
│   │   ├── WmiInterface.cs     # WMI MiInterface 硬件通信
│   │   ├── FanCurveService.cs  # 散热曲线后台服务
│   │   └── TelemetryBackgroundService.cs
│   └── hal/                    # 硬件抽象层
│       ├── DriverBridge.cs     # inpoutx64 P/Invoke
│       ├── HardwareAbstractionLayer.cs  # EC 寄存器语义映射
│       └── SmuController.cs    # RyzenAdj 子进程封装
├── deploy.ps1                  # 一键构建部署脚本
└── vite.config.js              # Vite 配置 + API 代理
```

---

## 参考项目 References

- [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit) — 开源 LLT 实现 (宝龙达 OEM 不兼容)
- [BellatorFanControl](https://github.com/Aveare/BellatorFanControl) — 斗战者笔记本风扇控制与性能模式 GUI 工具
- [Universal x86 Tuning Utility](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility) — UXTU 通用调优工具
- [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) — AMD SMU 控制接口
- [inpoutx64](https://www.highrez.co.uk/downloads/inpout32/) — 用户态物理内存/IO 访问

---

## 许可证 License

[GNU General Public License v3.0](LICENSE)
