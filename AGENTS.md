# DOUZHANZHE-Control — AGENTS.md

> **本文件定位**：AI 在本仓库工作的约定与入口，保持精炼；一切**以代码与提交为准**，文档可能滞后。
> **内容类型纪律**：进度流水账 → `docs/dev-task-board.md` + `.workbuddy/memory/` 日志；深度细节 → `docs/dev-*.md`；AI 长期记忆 → `.workbuddy/memory/MEMORY.md`。本文件只收"约定"。

## 项目简介

斗战者控制台（Douzhanzhe Console）：联想斗战者系列（战 7000 锐龙版 2025，主板 N176 / OEM 代号 Bellator）的开源硬件控制面板，替代联想电脑管家（Lenovo Vantage）。
技术栈：.NET 8 Minimal API（`server/api/`）+ React 19 / Vite 前端（`src/`）+ WinForms Shell（WebView2 托盘宿主，`server/shell/`）+ C# HAL（PawnIO 驱动栈，`server/hal/`）。端口：安装版 **3100** / 开发 **3101**（前端由 API 内嵌托管，无 Vite dev server）。

## 目前架构（细节见文档地图）

- **数据流**：React → `/api` HTTP + WebSocket 遥测 → HAL / WMI / PawnIO（EC/SMU/MSR）/ NVAPI；Shell 负责启动/看护 API 并经 WebView2 加载页面，写 `config/permission.json` 上报权限状态。
- **server/api 服务**：`Program.cs`（~70 端点）、`ProfileService`（性能配置）、`GameProfileService`/`GameScannerService`/`GameCoverService`（游戏规则/扫描/封面）、`ProcessMonitorService`（游戏自动切换）、`FanCurveService`（风扇曲线 EC 直写）、`BackgroundRotationService`（壁纸轮换）、`SystemInfoService`、`OsdService`、`TelemetryBackgroundService`。
- **server/hal**：`HardwareAbstractionLayer`/`HardwareDetector`/`AmdSmuController`/`IntelPowerController`/`NvapiGpuController`/`GpuController`/`CpuPowerController`/`DriverBridge`(EC IO)/`AppLog`。
- **前端**：`pages/` 7 页 + `components/panels/` + `components/ui/`；状态用 `hooks/useControlState.js`（模块级单例 + useSyncExternalStore，全 Tab 共享）；API 封装在 `services/uxtuAdapter.js`。
- **配置目录**：开发共享 `server/config/`，安装版 `<BaseDirectory>\config\`；日志 `%LOCALAPPDATA%\Douzhanzhe Console\logs`。
- 测试：vitest 仅 2 个文件（`npm test`），后端无测试项目——改动靠 dev 实例实测。

## 开发流程

| 命令 | 用途 |
|------|------|
| `npm run build` / `lint` / `test` | 前端构建 / 检查 / 单测 |
| `deploy.ps1` | 构建 + 同步 dist 到 5 个 wwwroot + 写 version.txt |
| `start-dev.ps1` | 一键开发环境（杀正式版占位、构建、起 3101）|
| `installer/build-installer.ps1` | 一键打包：[0/6] 版本同步 → [1.5/6] **备份覆盖审计** → 构建 → Inno → [6.5] sync-repos |

- **版本号**：`package.json` 直接写预发布号（`2.0.1-pre.N`），每轮 +1；[0/6] 自动同步 CHANGELOG 顶部标题与 `server/api/version.txt`（只改标题不改内容）。
- **打包前三查**：①CHANGELOG 顶部条目已手写且与版本号一致；②`git log <上版构建点>..HEAD` 审计变更全部入账；③备份覆盖审计 FAIL 先处理。
- **sync-repos.ps1**：按 frontend/api/chore/docs 分组提交 + push 当前分支；**只在工作树脏时动作**（已 commit 未 push 的它不会推，需手动 push）。构建钩子（.csproj AfterBuild）也会自动 commit+push——手动提交前先 `git log` 查重。
- **分支策略（2026-10-31 起）**：开发直接在 **main** 上进行（feature/v2.0 已合并进 main 并停用）；本地工作副本也已切到 main。打包与发布均在 main。

## AI 必须遵守的规则

- **未经用户明确命令，不得创建/推送 GitHub Release**（打 tag、gh release 都算）；"打包" = 本地出 exe。用户明确要发时：`gh release create vX.Y.Z-pre.N --target <当前分支HEAD的SHA> --title ... --notes-file <changelog节> --prerelease <exe>`——**必须带 --target 指向 feature 分支的提交**（默认会打在 main 的 HEAD 上：tag 日期变成 main 的旧提交日期导致 Release 列表排序错乱，且发布页源码指向错误代码——v2.0.2-pre.1 踩过，被迫删除重建）。notes 文件写项目目录内（/tmp 会被沙箱回滚）。
- **声称完成/发布前必须验证远程可见性**（git fetch + ahead 计数 / gh release view），不凭本地命令成功下结论。
- **撤版**：`gh release delete <tag> --yes`（附件随删，tag 默认保留）。
- **新增持久化配置必须过备份覆盖审计**（[1.5/6] FAIL 中断构建）：真用户数据 → 加进 Program.cs `backupCategories`；瞬态/机器相关 → 在 `tools/audit-backup-coverage.ps1` 的 `$allow` 白名单写一行理由。二进制走 `{ "__base64": ... }` 打包机制。
- 新 UI 严格按产品原型还原，不自行发挥；原型缺内容先问用户。旧页面有意重写，不做增量修补。
- **禁止手动复制文件**，统一走脚本流程。

## 已否决方案（AI 请勿重新引入）

1. **Vite dev server / HMR**——构建仅 ~200ms（abac20d, 49bbb69）
2. **Node.js 后端 uxtu-backend**——已全部迁 C# API（61a48a1）
3. **localStorage 配置持久化**——后端 API 是唯一权威源（localStorage 仅路由缓存 dz_page）
4. **WebView2↔Shell 消息桥**——文件 IPC + FileSystemWatcher 替代
5. **WinRing0 / inpoutx64 / ryzenadj**——已迁 PawnIO（feature/pawnio-migration，bae2314）
6. **dashboard-default.json 可排序仪表盘**——组件已删，孤儿端点 `/api/default-config` 待清理

## 环境坑（实测）

- **dev 测试实例锁 DLL**：`dotnet bin/build/Douzhanzhe.API.dll` 起的测试实例锁 bin/build，打包必失败——打包前用 CIM 查 `Douzhanzhe.API.dll` 宿主进程清零（正式 API 是独立 exe，勿误杀）。
- **.ps1 含中文必须带 UTF-8 BOM**：PS 5.1 对无 BOM 按 ANSI 解析，中文必炸（"哈希文本不完整"）。Write 工具默认无 BOM，写完用 `[IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($true))` 补。
- **构建钩子自动提交**：`dotnet build` 触发 sync-repos 自动 commit+push（"api: 更新 API"）——大改动先手动 commit 拿清晰提交信息。
- **docs/ 整体被主仓库 .gitignore**（有意设计），随私有 docs 子仓库（main 分支）同步。

## 文档地图

| 文档 | 主题 |
|------|------|
| `docs/dev-index.md` | 文档索引 / 技术栈 / 快速启动 |
| `docs/dev-architecture.md` | 整体架构分层与数据流 |
| `docs/dev-backend.md` | 后端服务 / 驱动依赖链 / **备份导出导入与覆盖审计** |
| `docs/dev-frontend.md` | 前端组件树与依赖 |
| `docs/dev-api.md` | API 端点定义 |
| `docs/dev-ec-map.md` | EC 寄存器映射（DSDT 反编译佐证）|
| `docs/dev-task-board.md` | 任务看板（进度记录在此）|
| `docs/v2.0/gap-audit.md` | v2.0 文档 vs 代码差距审计 |
| `docs/v2.0/product-design-guide.md` / `tab-switch-shared-state.md` | 产品设计 / 共享状态设计 |
| `docs/archive/` | 历史归档（不再维护）|
| `docs/plans/` | 功能设计文档（如 game-cover-steamgriddb.md）|
| `.workbuddy/memory/MEMORY.md` | AI 项目长期记忆（流程约定与坑）|
