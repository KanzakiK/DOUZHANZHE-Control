# MEMORY.md — DOUZHANZHE-Control 项目长期记忆

## 开发流程：备份覆盖自动审计（2026-10-30 起，必读）

- **打包第 [1.5/6] 步自动运行** `tools/audit-backup-coverage.ps1`：交叉核对「代码引用的 configDir 配置 × Program.cs backupCategories 分类 × 白名单」，**新增配置未入备份分类或白名单 → FAIL 中断构建**。
- **流程**：加功能写了新配置文件 → 打包被 [1.5/6] 拦下 → 要么在 backupCategories 加分类（真用户数据必须备份），要么在脚本白名单写一行不备份理由（瞬态/机器相关/遗留）。**不要绕过**。
- **坑**：tools 下 .ps1 含中文必须带 UTF-8 BOM（PS5.1 无 BOM 按 ANSI 解析中文必炸、报"哈希文本不完整"）。Write 工具默认无 BOM——写完用 `[IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($true))` 补。
- 备份分类现状（pre.18）：covers（封面图 base64）/sgdbkey（SGDB Key，前端默认不勾选）为新增；background 含 background.jpg 本体；dashboard-default.json 是死配置（SortableDashboard 已删）入白名单，孤儿端点 /api/default-config 待清理。

## 构建 / 发布流程（关键约定，2026-08-19 验证；2026-10-30 修订）

- **一键打包脚本**：`installer/build-installer.ps1 -Version <稳定号>`（如 `2.0.1`）。它内部调用 `deploy.ps1`（前端构建+同步 wwwroot）、`.NET publish`、Inno Setup 编译，末尾 `[6.5]` 调 `sync-repos.ps1`。
- **版本号约定（2026-10-30 起的实际做法）**：`package.json` **直接写预发布号**（如 `2.0.1-pre.N`），每出一轮修复就 +1；脚本 `[0/6]` 自动读取并把 CHANGELOG 顶部标题、version.txt 同步成该号。（旧约定"package.json 保留 base 版本"已不再遵循。）
- **打包前两查**：①CHANGELOG 顶部标题是否与版本号一致（build 的 [0/6] 只改标题不改内容，新功能必须先手写对应条目）；②`git log <上个版本构建点>..HEAD` 审计所有变更已入账。**第三个查：备份覆盖审计若 FAIL，先按 [1.5/6] 提示处理。**
- **sync-repos.ps1 行为坑**：它**只在「工作树有未提交改动」时才提交**；对于已经 commit 但领先远程的提交，它**不会主动 push**。所以提交 changelog 后，必须手动 `git push origin feature/v2.0`（标签需单独 push）。
- **【2026-10-30 修订】Release 规则**：**未经用户明确命令，不得自动创建/推送 GitHub Release**（用户曾手动删掉 AI 自动发的 pre.7 release 并纠正）。push feature 分支是默认流程；用户说"打包"= 本地出 exe 即可。若用户明确要发：`gh release create vX.Y.Z-pre.N --title "..." --notes-file <changelog节> --prerelease <安装包.exe>`（gh 已登录 KanzakiK；release notes 文件写到项目目录内，/tmp 会被沙箱回滚丢失）。
- **WorkBuddy 环境注意**：本会话曾因沙箱/安全删除防护多次卡构建。关闭沙箱、把 Node 批量删除阈值调到 9999、关闭"删除先移到回收站"保护后，整条 `build-installer.ps1` 才能跑通。前几次"卡死"实为工具防护，非脚本/代码问题。**另：dev 测试实例（dotnet 宿主跑 Douzhanzhe.API.dll）锁 bin/build DLL 会让打包失败——打包前用 CIM 清零。**
- **v2.0.1-pre.1 changelog 内容范围**：仅含自 `v2.0.0-pre.1` 的真实增量。Toast/备份签名/应用内更新/平台控制接线等均不在此区间。
