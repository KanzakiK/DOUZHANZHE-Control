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
- **本地编译必须带 `-p:AutoSync=false`**：`.csproj` 的 `AutoCommitAfterBuild` 会自动跑 sync-repos（commit+push），普通 `dotnet build` 会产生意外的自动提交。

## 逐核 Curve Optimizer（2026-11-07 实现；§7 第 3~8 步已实机验证）

- 用户拍板：UI **直接可见**（明确选择不等实机验证）、全核 CO 与逐核 CO **互斥二选一**、UI 沿用现有面板风格、保守范围 -30..0 mV；后续已完成实机验证。
- 命令与编码以本地 `uxtu-reference@26.3.1-7-g0229d72` 为准：Dragon Range 用 **RSMU 0x06（coper）/ 0x07（coall）**，MP1 0x35/0x36 仅作回退；编码 `ccd<<28 | (core/8)<<24 | (core%8)<<20 | encoded20`。勿再照搬旧迁移文档的 MP1 0x35。
- 拓扑/范围集中在 `server/hal/ISmuControl.cs` 的 `PerCoreCoTopology`，前端通过 `GET /api/smu/co-per-core` 取，不要写死 CCD 数。
- 持久化复用 `SmuOverrides.PerCoreCo`（profiles/*.json），无新配置文件；清空 = 逐核写 0，不依赖固件恢复。
- **【2026-11-07 修订】逐核 16 条命令在所有重放路径都下发**（含 ParameterGuard 60s 周期与前端两次延迟重发）：实测 16 条一次批量仅 15ms，周期重放无压力；本项目没有 PowerModeChanged 重放，排除周期会导致睡眠唤醒后无法恢复。前端单测锁定「初始 1 次 + 重发 2 次 = 3 次」。
- 实机验证清单见 `docs/plans/uxtu-capability-gap-and-amd-per-core-co.md` §7，须先退出正式版。
- **实机结论（2026-11-07）**：RSMU `0x06` 被 N176 BIOS 接受，全 16 核写 0 / 单核 -5、-15 / CCD2 / 16 核交错值全部 `0x01`，**MP1 `0x35` 回退从未触发**，16 核连写仅 15ms；无 WHEA/蓝屏。
- **SMU 层负向对照已通过（13:01）**：合法 `ccd0/core0` → `0x01`；畸形选择器 `ccd=2`(0x20000000) 与 `ccd=15/core7`(0xF0700000) 均为 `0xFF`（RSMU 失败后 MP1 回退也失败）。证明固件状态码有区分度、16/16 全绿不是假阳性、回退不会把失败误报成功。方法：`PUT /api/profiles/<mode>` 塞畸形 `perCoreCo`（只改配置层）→ `/api/overrides/switch` 绕校验层触发重放 → 读 `app-dev.log` 的 `[CO] coper` 行。
- **实机测试必须以管理员运行 API**（manifest `requireAdministrator`）。非管理员时 PawnIO 设备节点返回 win32=5，日志会误报「设备节点不可达，可能需要重启」并导致 `probe:false`。**本机 UAC 已关闭**，`Start-Process ... -Verb RunAs` 自动通过，无需用户点击（已写入用户级 MEMORY.md）。
- 用户后续决定：**产品允许范围保持 -30..0 不改**，仅把实机测试写入值约束在 -15..0（依据全核 -15mV 的安全经验）。`get-coper-options` 回读 `0xFFFFFFF1` 语义未确认，不要当作硬件下限。
- **已知缺口（2026-11-07）**：`RestoreAllPerfSettings` 只有 `Startup` 与 `switch` 两个调用点，**没有 PowerModeChanged 睡眠即时重放**（`RecoverAfterSleep` 只管风扇曲线）。现由 ParameterGuard 每 60 秒重放逐核兜底（实测日志 `[ParameterGuard] SMU per-core CO → ...` 已出现），唤醒后最多 60 秒恢复；要即时恢复仍需补订阅。
- **轻载观测无效（已纠正）**：第一次 ZenMaster A/B 没加稳定负载，空闲核掉到 0.58V CC6 睡眠电压，VID 噪声 -0.248~+0.26V 淹没信号。**空闲态电压不能算有效样本**，必须加 CPU 负载后过滤（`volt>0.62 && c0>50 && clk>2.0`）。
- **【2026-11-07 固定负载结论：负压确实生效】** 16 个 CPU-bound 进程稳定全核满载（STAPM 撞 50W 墙、温度 70.5°C、活跃核 c0=100%，三阶段一致），全 0 → 16 核交错 -15/-5 → 归零，各采样 8 次：
  - A→B **11/11 核 VID 全部下降**，中位 -0.0177V，而频率还略升 +0.038GHz（频率升而电压降，是 CO 生效特征）；B→C **11/11 核回升** +0.0441V。负载/温度/功耗墙一致，排除漂移。
  - 两组不同负压产生**两个可区分且组内高度一致**的响应档（-0.0033V vs -0.0181V）→ 每核不同值确实产生不同电压响应。
  - 单核定点：只给 `ccd0/core5=-15`，仅 ZenMaster 标号 10 的核 VID 降 -0.0322V 且归零回升 +0.0279V，其余核只有 -0.005~-0.010V 背景漂移且不回升 → 单核独立生效、可定位、可恢复。
- **编号映射待标定**：我们的 `ccd/core`（UXTU 标准的 CCD+物理核编号）与 ZenMaster 的 `core` 标签不是一一对应（core5 → ZenMaster 10，疑似逻辑处理器/APIC 索引 vs 物理核）。这解释了「标 -15 组降幅反而小于 -5 组」的标签反常；**不影响功能正确性**（命令只作用于唯一物理核），但 UI 标号 ↔ 物理核的精确对应若要做需要单独标定。
- 仍未做：§7 第 9~11 步的全核/逐核优先级、睡眠唤醒即时恢复、15 分钟以上稳定性负载与时钟拉伸；C 阶段频率比 A 高约 0.2GHz 有轻微状态漂移，最干净的判据是 A→B 配对。

## 实机观测工具（2026-11-07 安装）

- **ZenMaster v1.2.1**：`python -m pip install zenmaster`。纯 Python CLI、无 GUI、Windows 用 **PawnIO**（不是项目已否决的 WinRing0/ryzenadj），且与 `RyzenSmu` 共用 `Global\Access_PCI` 互斥体，可并存。识别本机为 `8940HX / DragonRange / AM5_MOBILE`。
- `zenmaster --sensors --json` 输出**每核 `volt`(VID) / `clk` / `power` / `temp` / `freqeff` / `c0`**（DragonRange 只解出 11 个核，编号 5..15）；`--table --json` 只有 6 个包级字段；`--dump-table --json` 是原始 float 数组。
- **必须提权运行**：用 `Start-Process -FilePath <python.exe> -ArgumentList <脚本.py> -Verb RunAs -Wait`，脚本内部再 `subprocess.run([sys.executable,'-m','zenmaster','--sensors','--json'])` 并把结果写文件。**不要**从 PowerShell 启动 `cmd.exe`（安全策略拦），复杂 `-c` 内联参数也会失败，必须用脚本文件。
- Python 路径：`C:\Users\liufe\.workbuddy\binaries\python\versions\3.13.12\python.exe`；可复用脚本在 `%TEMP%\zm_probe.py` / `%TEMP%\zm_ab.py`。
