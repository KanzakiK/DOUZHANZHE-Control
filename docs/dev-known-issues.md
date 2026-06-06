# 已知遗留问题

> 从主记忆 `douzanzhe-progress.md` §3 迁出，集中管理项目已知问题。

| 问题 | 状态 | 备注 |
|:-----|:-----|:------|
| ryzenadj crash on exit (0xC0000005) | 上游 v0.19.0 bug #370，写入在 cleanup crash 前已完成，已适配为成功退出码 | 运行时无害 |
| `