# DOUZHANZHE-Control — AGENTS.md

> 本文件是 **AI 在本仓库工作必须遵守的约定**，保持精炼。
> 内容类型纪律：**进度流水账** → `docs/dev-task-board.md` + `.workbuddy/memory/` 日志；**深度技术细节** → `docs/dev-*.md`；**AI 项目长期记忆** → `.workbuddy/memory/MEMORY.md`。本文件不收进度记录、会话笔记、临时工具说明。

## 已否决 / 已废弃方案（AI 请勿重新引入）

### 1. Vite dev server / HMR 热更新
废弃原因：npm run build 仅 ~200ms，无需 dev server 的额外复杂度。
- vite.config.js 仅保留 build 配置，无 server.proxy
- 前端请求直连 C# API（开发 3101 / 安装版 3100）
- 对应提交: abac20d, 49bbb69

### 2. Node.js 后端 (uxtu-backend)
废弃原因：所有端点已迁移到 C# API (Douzhanzhe.API)。
- 旧端口 3099 已彻底停用；C# API 端口：安装版 3100，开发 3101
- 对应提交: 61a48a1

### 3. localStorage 做配置持久化
否决原因：后端 API 是唯一权威源（single source of truth）。
- localStorage 仅做页面路由缓存 (dz_page)
- 持久化配置全部走后端 API

### 4. 消息桥 (WebView2 ↔ Shell)
否决原因：方案太重，用文件 IPC + FileSystemWatcher 替代。
- Shell 监听 server/config/ 下的信号/状态文件，API 原子写入 (tmp → File.Move)

### 5. WinRing0 / inpoutx64 / ryzenadj
废弃原因：已迁移到 PawnIO 驱动，全部硬件控制通过 PawnIO。
- 对应分支: feature/pawnio-migration，对应提交: bae2314

### 6. dashboard-default.json（v1.x 可排序仪表盘）
废弃原因：SortableDashboard 组件在 v2.0 重构中删除，前端零引用。
- `GET/POST /api/default-config` 两个孤儿端点与其文件待清理（白名单豁免中）
- 请勿在新功能中引用

## 架构要点（AI 请遵守）

- 前端所有 API 请求走相对路径 (/api/...)，由 C# API 处理
- 配置文件统一存放 server/config/（开发共享路径；安装版为 `<安装目录>\config\`）
- 自动提交: .csproj AfterTargets=Build 执行 sync-repos.ps1（**会自动 commit+push**——手动提交前先 `git log` 查重，避免空提交/重复提交）
- 私有备份: KanzakiK/DOUZHANZHE-Control-private (docs/，随 docs 子仓库同步；docs/ 整体被主仓库 .gitignore，属有意设计)

## 构建与发布

- 前端构建：`npm run build`（约 200ms）；部署：`deploy.ps1`；开发启动：`start-dev.ps1`
- 一键打包：`installer/build-installer.ps1`（deploy + .NET publish + 备份覆盖审计 + Inno Setup + sync-repos）
- **版本号做法**：`package.json` 直接写预发布号（如 `2.0.1-pre.N`），每轮修复 +1；脚本 [0/6] 自动同步 CHANGELOG 顶部标题与 version.txt
- **打包前三查**：①CHANGELOG 顶部条目与版本号一致且内容已手写（脚本只改标题）；②`git log <上版构建点>..HEAD` 审计所有变更已入账；③备份覆盖审计 FAIL 时先按提示处理
- **sync-repos.ps1**：只在「工作树有未提交改动」时才 commit+push；已 commit 未 push 的内容它不会推——需手动 `git push origin feature/v2.0`

## 硬性红线（2026-10-30 起）

- **未经用户明确命令，不得自动创建/推送 GitHub Release**（打 tag、gh release create 都算）。"打包" = 本地出 exe。用户明确要发时：`gh release create vX.Y.Z-pre.N --title ... --notes-file <changelog节> --prerelease <exe>`（release notes 文件写项目目录内，/tmp 会被沙箱回滚）
- **声称"完成/发布"前必须实际验证远程可见性**（git fetch + ahead 计数 / gh release view），不能凭本地命令成功下结论
- **撤版**：`gh release delete <tag> --yes`（附件随删，tag 默认保留）

## 备份覆盖审计（打包 [1.5/6] 强制）

- `tools/audit-backup-coverage.ps1` 交叉核对：代码引用的 configDir 配置 × Program.cs `backupCategories` 分类 × 白名单
- **新增功能持久化新配置 → 打包会 FAIL**。处理：真用户数据 → 加进 backupCategories；瞬态/机器相关 → 在脚本 `$allow` 白名单写一行理由。**不要绕过审计**
- 二进制文件（图片等）走 `{ "__base64": ... }` 打包，导入端解码还原
- 分类现状与端点详见 `docs/dev-backend.md`「备份导出/导入与覆盖审计」章节

## 环境坑（实测）

- **dev 测试实例锁文件**：`dotnet bin/build/Douzhanzhe.API.dll` 起的测试实例会锁 bin/build DLL，打包前必须清零（用 CIM 查 `Douzhanzhe.API.dll` 宿主进程；正式 API 是独立 exe，勿误杀）
- **.ps1 含中文必须带 UTF-8 BOM**：PS 5.1 对无 BOM 文件按 ANSI 解析，中文必炸（报"哈希文本不完整"）。Write 工具默认无 BOM，写完用 `[IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($true))` 补
- **构建钩子自动提交**：`dotnet build` 触发 sync-repos 自动 commit+push（"api: 更新 API"）——大改动建议先手动 commit 拿到清晰提交信息

## 开发约定

- 新 UI 开发严格按产品原型样式还原，不自行发挥；发现原型缺内容先找用户商量
- 旧页面有意重写，不做增量修补
- **禁止手动复制文件**，统一走脚本流程
- 已完成的页面迁移记录见 `docs/dev-task-board.md`；v2.0 缺口清单见 `docs/v2.0/gap-audit.md`
