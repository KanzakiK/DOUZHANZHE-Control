# Douzhanzhe Console

> [!WARNING]
> **️ 风险提示 & 兼容性声明 / Disclaimer**
>
> **兼容性**：本控制台仅在 **联想斗战者战7000锐龙版 2025款** 上测试通过。酷睿版或其他机型使用可能导致部分功能不可用，甚至引发硬件参数设置错误。请在非兼容机型上谨慎使用。
>
> **操作风险**：本工具提供的部分功能涉及超出厂商预设范围的硬件参数调整（包括但不限于 CPU 功耗墙、温度墙、GPU 超频、EC 寄存器直写等）。**使用此类功能可能导致硬件损坏、系统不稳定、数据丢失，或影响厂商保修及售后服务。**
>
> 请在充分了解相关风险后自行决定是否使用。**因使用本工具造成的一切硬件损坏、系统故障、保修失效等后果，需由用户自行承担，本工具及其开发者不承担任何责任。**
>
> **Compatibility**: This console has only been tested on the **Lenovo Legion 7000 Ryzen 2025 Edition**. Using it on Intel or other models may result in feature incompatibility or incorrect hardware configuration.
>
> **Use at your own risk.** Some features involve hardware adjustments beyond manufacturer defaults. The developers assume no liability for any hardware damage, system failure, or warranty issues.

> [!TIP]
> **📥 下载安装 / Download**
> 获取最新安装包请访问：[GitHub Releases](https://github.com/KanzakiK/DOUZHANZHE-Control/releases/latest)
>
> Get the latest installer from: [GitHub Releases](https://github.com/KanzakiK/DOUZHANZHE-Control/releases/latest)

---

**斗战者控制台** — 专为联想 Legion N176 2025 (宝龙达 OEM) 打造的开源硬件控制面板，替代官方联想电脑管家，提供完整的硬件监控、性能调优和系统控制能力。

**Douzhanzhe Console** — Open-source hardware control panel for Lenovo Legion N176 2025 (BaoLongDa OEM). A full-featured alternative to Lenovo Vantage for hardware monitoring, performance tuning, and system control.

---

## 功能 Features

**实时监控** — CPU/GPU 温度、频率、占用率、功耗、风扇转速、显存、内存与磁盘，WebSocket 每 500ms 全量推送，含负载曲线历史。

**性能调优** — CPU 功耗墙/温度墙/核心数/睿频 (SMU via RyzenAdj)、GPU 功耗/超频偏移/锁频 (NVAPI + nvidia-smi)、四档模式预设一键切换（安静/均衡/野兽/斗战）。

**自定义散热曲线** — 独立标签页，SVG 可视化温度-转速曲线编辑器，支持多点拖拽、保存/加载/启停/恢复预设，后台 FanCurveService 定时执行。

**GPU 模式** — 混合/集显/独显三档切换 (WMI MiInterface)，用户选择持久化到配置文件，重启后自动恢复。

**系统控制** — 键盘背光亮度 (0-3级)、FnLock/CapsLock/NumLock/触摸板锁定、电源计划管理、风扇目标转速直写 (EC 寄存器)。

**自定义背景** — 上传本地图片作为界面背景，支持透明度调节和黑色/白色遮罩切换。

**个性化** — @dnd-kit 拖拽排序仪表盘、模块隐藏/显示、30+ 主题皮肤。

**桌面集成** — WinForms + WebView2 原生桌面壳，单实例运行、系统托盘最小化、开机自启（计划任务）、窗口尺寸/位置记忆。

---

## 安装 Installation

从 [Releases](https://github.com/KanzakiK/DOUZHANZHE-Control/releases) 页面下载最新安装包 `DouzhanzheConsole-*-Setup.exe`，双击运行即可。

安装程序会自动检测并安装所需依赖（.NET 8 Desktop Runtime、WebView2 Runtime）。

---

## 快速开始 Quick Start (开发)

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

### 构建打包 Build & Package

```powershell
# 构建前端 + 同步到后端 wwwroot
.\deploy.ps1

# 完整构建：前端 → API 发布 → Shell 发布 → 合并工具 → 编译安装包
# （需先配置 build-all.ps1 中的路径和文件 hash）
```

生产模式访问 `http://127.0.0.1:3100`，Debug 面板在 `http://127.0.0.1:3100/debug`。

---

## 技术栈 Tech Stack

| 层级 Layer | 技术 |
|:-----|:-----|
| 桌面壳 Shell | WinForms + WebView2 (单实例、托盘、开机自启) |
| 前端 Frontend | React 19 + Vite 8 + TailwindCSS 3 + @dnd-kit |
| 后端 Backend | .NET 8 Minimal API + WMI + inpoutx64 |
| SMU 控制 | RyzenAdj + WinRing0x64 (子进程调用) |
| GPU 控制 | nvidia-smi 锁频 + NVAPI 超频 + WMI 模式切换 |
| EC 直写 | inpoutx64 (IO 端口 0x62/0x66) |
| 安装包 | Inno Setup 6 |

---

## 项目结构 Structure

```
DOUZHANZHE-Control/
├── src/                        # React 前端
│   ├── App.jsx                 # 主布局 + 标签页路由 + 背景层
│   ├── components/
│   │   ├── SortableDashboard   # 拖拽排序仪表盘
│   │   ├── panels/             # 性能/散热曲线/遥测/系统/设置面板
│   │   └── ui/                 # Card, Gauge, Toast 等通用组件
│   ├── hooks/                  # useCardOrder, useControlState
│   └── services/               # uxtuAdapter (API 通信 + 模式预设)
├── server/
│   ├── api/                    # ASP.NET 8 后端
│   │   ├── Program.cs          # Minimal API 端点 + WebSocket 遥测 + 背景 API
│   │   ├── WmiInterface.cs     # WMI MiInterface 硬件通信
│   │   ├── FanCurveService.cs  # 散热曲线后台服务
│   │   └── TelemetryBackgroundService.cs
│   ├── hal/                    # 硬件抽象层
│   │   ├── DriverBridge.cs     # inpoutx64 P/Invoke
│   │   ├── HardwareAbstractionLayer.cs  # EC 寄存器语义映射
│   │   └── SmuController.cs    # RyzenAdj 子进程封装
│   ├── shell/                  # WinForms 桌面壳
│   │   └── Douzhanzhe.Shell/
│   │       ├── Program.cs      # 单实例互斥 + 管理员提权 + 窗口激活
│   │       └── Form1.cs        # WebView2 宿主 + 托盘 + 窗口状态记忆
│   └── tools/                  # 运行时工具 (ryzenadj, WinRing0x64)
├── installer/                  # Inno Setup 安装脚本
│   └── douzhanzhe-setup.iss
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
