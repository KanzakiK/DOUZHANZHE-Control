# 已知遗留问题

> 从主记忆 `douzhanzhe-progress.md` §3 迁出，集中管理项目已知问题。
>
> **📋 维护规则**：
> - 已修复问题使用 ~~删除线~~ 标记（与 task-board 统一）
> - 新增问题在末尾追加
> - 每条记录包含：问题描述 + 当前状态 + 备注

| 问题 | 状态 | 备注 |
|:-----|:-----|:------|
| ryzenadj crash on exit (0xC0000005) | 上游 v0.19.0 bug #370，写入在 cleanup crash 前已完成，已适配为成功退出码 | 运行时无害 |
| SMU 写入不生效（WinRing0 驱动未加载） | ~~已修复~~ — run.ps1 + Program.cs 启动时自动提权加载驱动 | 2026-06-07 |
| 模式切换 SMU 参数不下发 | ~~已修复~~ — 双发方案：EC 切换前后各写一次 SMU | 2026-06-07 |
| 前端 3 个 CPU 控件只改 state 不下发 | ~~已修复~~ — 频率限制/关睿频/核心数追加 queueSmu 实时调用 | 2026-06-07 |
| reload-fe.ps1 部署目标错误 | ~~已修复~~ — 目标改为 server/api/bin/run/wwwroot | 2026-06-07 |
| NVAPI SetPStates20 在 RTX 5060 Laptop GPU 返回 -104 (NOT_SUPPORTED) | ~~已修复~~ — 通过蛟龙 KaronOC.dll 绕过，超频已验证可用 | 2026-06-07 |
| NVAPI 功率控制在笔记本 GPU 全返回零 | 未修复 — 硬件/驱动限制，功率控制不可用 | 笔记本 GPU 通病 |