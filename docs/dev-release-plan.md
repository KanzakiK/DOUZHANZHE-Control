# Release 1 定义与对比表

> **📋 更新规则**：
> - 已验证的功能 → 在对比表中将对应项标记为 ✅
> - 新增功能需求 → 在 Release 1 或后续版本章节追加
> - 已验证/废弃的驱动依赖 → 更新核心原则中的状态
> - 同步更新主记忆 §2 对应字段（Last synced / Infrastructure / 近期工作）
> 核心原则：
> 1. 所有驱动依赖仅 `inpoutx64` (MIT 自管)，不依赖任何外部控制台的 `.sys`。
> 2. `斗战者控制台.dll` 部署时自动复制（仅 GPUMode 切换必需），不要求用户完整安装。
> 3. WinRing0 依赖链 (`ryzenadj.exe` -> `WinRing0x64.dll` -> `WinRing0_1_2_0.sys`) 已被 SmuController 全面替代，已淘汰。

---

## 一、官方控制台 vs 本项目 · 完整功能对比

| 功能区 | 官方控制台 | 本项目 | Release 1 目标 |
|:-------|:----------|:------|:--------------:|
| **CPU 监控**（负载/频率/温度/曲线图） | ✅ | ✅ 全部 + WebSocket | ✅ 必含 |
| **GPU 监控**（负载/显存/频率/温度/曲线图） | ✅ | ✅ | ✅ 必含 |
| **风扇转速显示**（大扇/小扇 RPM） | ✅ | ✅ | ✅ 必含 |
| **GPU 核心频率控制**（上限/锁定/nvidia-smi） | ❌ | ✅ GpuController + POST /api/gpu/set | ✅ 必含 |
| **GPU 显存频率控制**（3频点+自动） | ❌ | ✅ limit-memory + reset-memory-clocks | ✅ 必含 |
| **风扇手动控制**（调节最大转速+恢复默认） | ✅ | 🚧 C# HAL + API + Debug 页待实现 | ✅ 必含 |
| **键盘背光调节** | ✅ | ✅ | ✅ 必含 |
| **存储信息**（内存/硬盘占用率+总量） | ✅ | ✅ | ✅ 必含 |
| **4 种性能模式**（斗战/野兽/均衡/安静） | ✅ | ✅ Debug 页已实现 | ✅ 必含 |
| **独显直连** | ✅ | ✅ AppBridge GPUMode 2 已验证 | ✅ 必含 |
| **集显模式** | ✅ | ✅ AppBridge GPUMode 1 已验证 | ✅ 必含 |
| **Fn 锁** | ✅ | ✅ | ✅ 必含 |
| **NumLock / CapsLock** | ✅ | ✅ | ✅ 必含 |
| **触摸板锁** | ✅ | ✅ | ✅ 必含 |
| **关闭 OSD** | ✅ | ✅ WMI Bellator 未实现 | ✅ WMI Bellator Release 2 |
| **五维雷达图** | ✅ | ✅ WMI Bellator 未实现 | ✅ WMI Bellator 可选加分 |
| **屏幕校色入口** | ✅ | ✅ WMI Bellator 不适用（依赖硬件） | ✅ WMI Bellator 放弃 |
| **SMU 功耗墙/温度墙** | ✅ WMI Bellator 官方没有 | ✅ SmuController 已验证 | ➕ 独有特色 |
| **电源计划切换**（平衡/高性能/节能） | ✅ WMI Bellator 官方没有 | ✅ powrprof.dll | ➕ 独有特色 |
| **GPU 模式切换**（混合/集显/独显） | ✅ WMI Bellator 官方无显式切换 | ✅ AppBridge 已验证 | ➕ 独有特色 |

---

## 二、启动方式

三终端并行：
- **C# HAL API**: `server/api/run.ps1` → `http://127.0.0.1:3100`
- **Node.js 后端**: `node server/server.js` → `http://localhost:3099`
- **Vite 前端**: `npx vite` → `http://localhost:5173`

> Release 1 目标：`npm start` 一键启动三个服务。

---

## 三、必须完成项（硬门槛）

### P0 — 不可发布

| 项目 | 依赖 | 难度 |
|:-----|:----|:----:|
| 安装程序/打包（Inno Setup 或 NSIS） | 无 | ★★★ |
| 开机自启动（双后端注册服务） | 打包后 | ★ |
| `npm start` 一键启动脚本 | 无 | ★ |

### P1 — 核心功能

| 项目 | 当前状态 | 工作量 |
|:-----|:--------|:------:|
| **① 风扇手动控制**（全栈） | | |
| ├ C# HAL: `CpuFanControl`/`GpuFanControl` 语义属性 | ✅ WMI Bellator | 小 |
| ├ C# API: `POST /api/fan/set-target` | ✅ WMI Bellator | 小 |
| └ C# Debug 页: 风扇目标转速滑块 | ✅ WMI Bellator | 中 |
| **② 前端 SettingsPanel 补齐** | | |
| ├ 散热模式下拉框 (`thermal_mode`) | ✅ WMI Bellator | 中 |
| ├ 电源计划下拉框 (`power_plan`) | ✅ WMI Bellator | 中 |
| ├ GPU 模式选择器 (AppBridge GPUMode 0/1/2) | 后端 ✅ | 中 |
| ├ 集显模式开关 (`igpu_only`) | 后端 ✅ | 小 |
| └ 触摸板锁路由修正 (Node.js → C# HAL) | ✅ WMI Bellator | 小 |
| **③ 历史曲线图** | | |
| └ 排查 Sparkline 组件渲染（代码存在，可能卡片被隐藏） | ✅ WMI Bellator | 小 |

---

## 四、Release 1 已知 Bug 白名单

以下 Bug 允许带进 Release 1：

| Bug | 影响范围 | 原因 |
|:----|:---------|:-----|
| `SortableDashboard.jsx` 重复属性 `showGpu={false}` | 不影响功能 | 静默重复声明 |
| 前端模式按钮高亮加载时序 | 视觉体验 | 按钮标签在数据就绪前短暂错位 |
| `tools/` 中 4 个文件仍引用 WinRing0x64 | 文档/注释 | 非代码路径，仅注释提及 |
| OSD 显示开关未实现 | 极低 | 少数用户介意 OSD 弹窗 |

以下 Bug **必须修复**才能 Release 1：

| Bug | 原因 |
|:----|:-----|
| `mockTelemetry.js cpuCores:32` | 已修复 ✅ |
| SettingsPanel: `touchpadLock` → Node.js 废弃端点报错 | 用户可感知 |
| SettingsPanel: `osdDisabled`(关闭OSD) → Node.js 死路由 | 用户可感知 |
| 历史曲线图 Sparkline 显示问题 | 核心功能缺失感 |

---

## 五、Release 2 候选功能

- 五维雷达图可视化
- 安静性能模式（GPU 满血 + 风扇低速）
- 遥测扩展（GPU/CPU 功率）
- SMU 监视器（值被覆盖时重发）
- 关闭 OSD
- 跨平台/非管理员降级模式
- ~~`POST /api/uxtu/apply` 废弃~~, 路由到 C# `/api/smu/set` (SmuController 已验证)
- ~~移除 `server/tools/WinRing0x64.dll`~~ + 废弃 ryzenadj.exe (SmuController 已替代)

---

## 六、版本号约定

Release 1 版本号：**v1.0.0**

版本号格式：`vMAJOR.MINOR.PATCH`
- MAJOR: 功能对标官方控制台 Release 1
- MINOR: 新增功能（独有特性 + Release 2 功能）
- PATCH: Bug 修复

---

*文档创建日期：2026-06-05*
*最后更新：2026-06-05*

## 七、部署架构

### 开发态

```
Vite :5173 (代理) → C# :3100 + Node :3099
```

### 生产态 (Release 1)

```
C# :3100 (单一端口)
  ├── 自有 API
  ├── 静态文件 (vite build -> wwwroot/)
  └── 反向代理 Node (剩余端点)
```

- 前端打包后丢到 C# wwwroot/
- C# UseStaticFiles() + MapFallbackToFile("index.html")
- Node.js 端点通过 C# 反向代理 (YARP / HttpClient) 转发
- 最终用户只需打开 http://localhost:3100

### 迁移路线

| Step | 操作 | 状态 |
|:-----|:---------|:----------|
| 1 | vite.config.js 改 build 输出目录到 wwwroot/ | ⏳ 待实现 |
| 2 | Program.cs 加 UseStaticFiles + MapFallbackToFile | ⏳ 待实现 |
| 3 | C# 反向代理 Node.js | ⏳ 待实现 |
| 4 | npm start 改为 vite build + dotnet run + node server | ⏳ 待实现 |
| 5 | (可选) Node.js 迁移到 C#, 砍掉 Node | ⏳ Release 2 |

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
