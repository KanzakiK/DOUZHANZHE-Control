# Douzhanzhe Console — 主记忆

> 本文件是项目的**唯一主记忆**，聚合项目状态、文档索引、关键参考和归档日志。
> 自动加载前 200 行。其余用户记忆文件已删除或合并至此。
>
> **🔄 跨会话加载流程**：
> 1. AI 自动加载本文件前 200 行 + `.github/copilot-instructions.md`（行为钢印）
> 2. 查顶部速查表 → 确定当前遇到的情况对应守则哪个章节
> 3. 按文档地图超链接 → `read_file` 读取 docs/ 下需要的长效文档
> 4. 要修改文件前 → 参照 §2-3 规则（`git diff` / 文件特征对账 / 跨会话衔接契约）
> 5. 执行 GSD 工作流 → 参照 §5（`.plan` → `.execute` → `.verify` → `.ship`）
---
**🔗 操作守则**：[.github/copilot-instructions.md](.github/copilot-instructions.md) — AI 行为钢印。记忆管"知道什么"，守则管"怎么做"。

**⚡ 最高铁律**：允许编辑器工具写入，但每次写入后必须调用 `_verify_write.py` 后验清洗。详见操作守则 §1。

| 遇到的情况 | → 翻阅守则 |
|:----------|:----------|
| 要写文件 | §1 🔄 编辑器工具 + 后验清洗 |
| 工具返回末尾有 `{"$mid":...}` | §1 🔍 前置破幻对账（`_verify_write.py --check`） |
| 文件疑似被污染 | §1 🧹 事后清洗全流程 |
| 要改 C# 代码 | §1 🛡️ 术前灾备 + 后验 |
| 要改 docs/ 文档 | §1 🔄 编辑器工具 + 后验 |
| 被要求 `.plan/.execute/.verify/.ship` | §5 GSD 全线熔断 |
| 模型要全量覆写或疑似回滚 | §2 🛑 禁止全量覆写 + 禁止隐式回滚 |
---

## 一、文档地图
| 文档 | 用途 | 路径 |
|------|------|------|
| dev-index.md | 项目概述、技术栈、快速启动 | [docs/dev-index.md](docs/dev-index.md) |
| dev-architecture.md | 系统分层、数据流、双后端架构 | [docs/dev-architecture.md](docs/dev-architecture.md) |
| dev-backend.md | C# 后端架构（DriverBridge→HAL→API） | [docs/dev-backend.md](docs/dev-backend.md) |
| dev-frontend.md | React 组件树、状态管理、布局 | [docs/dev-frontend.md](docs/dev-frontend.md) |
| dev-ec-map.md | EC 寄存器映射（DSDT 反编译） | [docs/dev-ec-map.md](docs/dev-ec-map.md) |
| dev-api.md | C# API 端点定义 | [docs/dev-api.md](docs/dev-api.md) |
| dev-task-board.md | 任务看板（Release 1 / 后续 / 已完成） | [docs/dev-task-board.md](docs/dev-task-board.md) |
| dev-release-plan.md | Release 1 定义与对比表 | [docs/dev-release-plan.md](docs/dev-release-plan.md) |
| dev-known-issues.md | 已知遗留问题清单 | [docs/dev-known-issues.md](docs/dev-known-issues.md) |
| session-archive.md | 会话归档（迭代日志） | [docs/session-archive.md](docs/session-archive.md) |
| reference-consoles.md | 官方控制台参考（DLL 路径/依赖/功能对比） | [docs/reference-consoles.md](docs/reference-consoles.md) |

## 二、项目状态
Last synced: 2026-06-07 (构建流程根治: A2+B1+B3+C1+C2 前端流程优化)
- **Git**: main
- **C# HAL API**: :3100 (running)
- **Node.js**: ~~:3099~~（已退役，全功能迁至 C#）

### Infrastructure
- Build: `server/api/bin/build`
- Run: `server/api/run.ps1` (build + copy + launch, 自动清 .vite-temp)
- FE热更: `server/tools/reload-fe.ps1` (纯前端修改快速部署, 无需重启 C#)
- JSON: atomicWrite (write-tmp-rename) + safeRead (self-healing)
- Sanitize: `_verify_write.py`（后验清洗 + `--check` 只读诊断）
- GSD: `.plan → .execute → .verify → .ship` 工作流

### 近期工作（最近三天 · 2026-06-04 ~ 2026-06-07）
[06-07] - **风扇信息 UI 优化**: TelemetryPanel 删除 RPM 后的 / max 后缀显示
[06-07] - **开机自启 + Docs Node.js 清理**: GET|POST /api/auto-start（TaskScheduler）+ 前端设置页开关；7 个 docs 文件去除 Node.js 过期引用 70+ 处
[06-06] - **风扇控制突破**: WMI Bellator 协议修复（MaxFanSpeed/MaxFanSwitch），风扇 0x5F/0x5B → WMI 直通
[06-06] - **每模式独立参数记忆 localStorage + 恢复预设**: 四个模式独立保存 uxtuParams+fans；切模式保存旧值+加载新值；恢复预设清除 key+MODE_PRESETS

[06-06] - **恢复预设按钮 + 模式切换联动 + 滑块单发 SMU**: MODE_PRESETS 补全 13 字段；模式按钮更新 uxtuParams；滑块 600ms 去抖单发；恢复预设按钮在模式选择 Card 右上角

[06-06] - **Vite Dev Server 废弃**: 前端全量迁入 C# `wwwroot/`，清理 dev/proxy 脚本
[06-06] - **SMU 全命令覆盖**: stapm/fast/slow/tctl 端点，Debug 页整合
[06-06] - **CPU 核心数限制**: CpuAffinityManager.cs + API 端点
[06-05] - **WMI 迁移+AppBridge 废弃**: 砍掉 AppLib.cs + AppBridge 子项目
[06-06] - **EC 16-bit 竞态修复**: HAL 双读仲裁，风扇瞬态 0 率降至 0%
[06-05] - **Node.js 废弃+前端路由修复**: 10+ 端点迁 C#，代理路由移除
[06-06] - **键盘灯/电源计划/持久化修复**: 亮度 0-3、按钮双发、localStorage 恢复
[06-06] - **写入策略改造**: 放弃 Python 管道，恢复编辑器工具 + `_verify_write.py` 后验清洗
- 详见 [docs/session-archive.md](docs/session-archive.md)

## 三、关键参考
### 参考项目
| 参考项目 | 依赖/技术 | 定位说明 |
|:---------|:----------|:---------|
| **斗战者控制台** (`C:\Program Files (x86)\斗战者控制台\`) | `BLD.WMIOperation` (WMI) | 官方参考实现，WMI 协议来源 |
| **BellatorFanControl** ([GitHub](https://github.com/Aveare/BellatorFanControl/)) | WMI MiInterface | WMI 风扇协议源代码参考，`data[4]=FanType` 关键发现来源 |
| **UXTU** (`uxtu-reference/`) | WinRing0 → SMU | SMU 控制架构来源 |
| **EnumDLL** (`_enum_dll_proj/`) | 斗战者控制台.dll | WMI 方法 ID 全表来源（GPUMode=9, FnLock=11 等） |
| **nvidia-smi** | NVIDIA 驱动 CLI | GPU 锁频/重置/功率监控标准工具 |
| **蛟龙控制台** (`D:\Program Files\JiaoLong7.3\`) | WinRing0 驱动 | 第三方改造版，UI/功能对标参考 |
| ~~LLT (Lenovo Legion Toolkit)~~ (`llt-reference/`) | EC IO + WMI | ❌ 历史参考，0xB2/0xB3 等路径对本机无效 |
详见 [docs/reference-consoles.md](docs/reference-consoles.md)（完整详情）。

### 已知遗留问题
详见 [docs/dev-known-issues.md](docs/dev-known-issues.md)
- ryzenadj crash on exit (0xC0000005) — 上游 v0.19.0 bug #370，写入成功，已适配为成功退出码
- `$mid` 工具输出层伪影 — 已识别，`_verify_write.py --check` 破幻对账可确认磁盘无污染

## 四、会话归档
每次迭代日志记录在 [docs/session-archive.md](docs/session-archive.md)。主记忆不内嵌历史，先看任务看板，有需要再查归档。
- 最近: 构建流程根治 — A2(预删.vite-temp) B1(postbuild目标修正) B3(reload-fe.ps1) C1+C2(守则前端流程)
- 最近: 风扇信息 UI 优化 — TelemetryPanel 删除 RPM / max 后缀
- 最近: MODE_PRESETS 精简合并 — 去 GPU 频率 5 字段，11 字段统一；useControlState 私有版删除
- 最近: 风扇预设按官方修正 — 安静=2200/2000，均衡=2900/6400，野兽=3500/6900，斗战=4300/8000
- 最近: 恢复预设 GPU 重置 — applyGpuControl reset-clocks + reset-memory-clocks，不硬编码
- 最近: 开机自启后端 API + 前端设置页开关；Docs Node.js 过期引用清理 70+ 处
- 最近: 每模式独立参数记忆 localStorage（四个模式独立键名，切模式保存旧值+加载新值）
