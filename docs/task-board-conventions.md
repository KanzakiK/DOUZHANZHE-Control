# 任务看板整理原则

## 三大板块结构
- **🏁 Release 1 优先**：对标官方"斗战者 N176 2025"控制台的硬门槛，必须完成才能发布 v1.0.0
- **🧭 后续版本**：Release 2 候选功能或低优先级改进
- **✅ 已完成**：已验证通过的功能模块

## 排序规则

### 1. 开发任务优先，打包/部署靠后
同一分类内，顺序为：**开发实施 → 文档同步 → 清理/废弃 → 打包/部署**

### 2. 分类原则
每个板块内按 **后端 → 前端 → 其他** 分组，每组内再按具体子功能细分。

### 3. 任务粒度
- 一个 checkbox 行 = 一个独立原子任务
- 不得将逗号分隔列表拆分为多个任务
- 不得合并多个无关功能为一个大任务

### 4. 新增任务规则
- 新任务必须插入到正确板块的正确分类中
- 已经完成的任务立即移到 ✅ 已完成
- 确认是 Release 2 的内容直接放入 🧭 后续版本
- Bug 统一放到所属板块的"已知 Bug"子分类

## 任务状态标记
- `[ ]` = 待完成
- `[x]` = 已完成（放到 ✅ 已完成板块）
- `~~strikethrough~~` = 已修复的 Bug

## 参考文档
- `docs/dev-task-board.md` — 任务看板本体
- `docs/dev-release-plan.md` — Release 1 定义与功能对比表

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
