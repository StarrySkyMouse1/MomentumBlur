# Mmod Record 项目工作规则

## 每轮硬门禁

处理本仓库的需求、设计、计划、实现、验收或执行提示词时，每一轮都必须：

1. 读取并遵守 `.codex/skills/mmod-record-workflow/SKILL.md`（Cursor 使用 `.cursor/skills/mmod-record-workflow/SKILL.md`，通用 agents 使用 `.agents/skills/mmod-record-workflow/SKILL.md`；三处保持同文）。
2. 读取 `README.md`、本次任务直接相关的 `docs/` 设计/计划和现有代码，不得只凭聊天记忆实施。
3. 先判断 Direct / Relay / Discovery。用户仅要求计划或提示词时默认 Standard Relay，一个执行 AI 串行完成；只有用户明确要求并行才读取 `references/parallel-relay.md`。
4. 开始写入前核对当前分支、`git status --short`、相关 diff、任务白名单和用户已有修改。
5. 禁止自行创建/切换分支或 worktree，禁止 stash、reset、clean 和覆盖用户改动。commit、push、发布及外部操作必须服从用户明确授权。

## 项目边界

- 解决方案为 `MomentumBlur.slnx`；WPF/.NET、Native CMake/Visual C++、运行和 Smoke 命令以 `README.md` 为准。
- 涉及录制链路、Native/GPU、编码、磁盘安全、性能预检、崩溃恢复或持久化时，先读取相关设计与既有状态机契约，使用与风险匹配的验证。
- 普通 UI 和可逆逻辑修改保持范围集中，优先复用现有 ViewModel、Service、Style 和 WPF UI 结构，不顺带改动录制/Native 核心。
- 未经用户精确授权，不新增或修改测试、断言、fixture、stub、故障注入入口或测试专用生产钩子。
- 构建成功不等于真实录制、GPU、游戏或视觉验收；未完成的实机验证必须明确报告。

## 计划与停止规则

- Relay 计划使用可独立验收的 Markdown 复选项；每完成一步并取得证据后，立即勾选并更新计划开头的当前状态，再执行下一步。
- 待执行/执行中计划放 `docs/roadmap/plans/active/`，已完成/已取代/已取消计划放 `docs/roadmap/plans/archive/`；仅在确有 Relay 计划时创建目录。
- 同一失败 3 次、两种策略无净改善、连续 3 次仅调参数/等待、命令 60 秒无新输出，或需要越过授权边界时，必须停止并保存现场，不得继续震荡试错。
