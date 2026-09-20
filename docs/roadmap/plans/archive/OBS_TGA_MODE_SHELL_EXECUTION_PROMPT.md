# OBS / TGA 顶层模式工作台：执行 AI 全量提示词

> **状态：已完成并归档（2026-09-15）。**
> 执行结果与全部证据见 `docs/design/OBS_TGA_MODE_ACCEPTANCE.md`；
> 计划已归档为 `docs/roadmap/plans/archive/OBS_TGA_MODE_SHELL_RELAY_PLAN.md`（R0–R9 全部勾选）。
> 本文件保留为历史执行提示词，路径引用已按归档位置修正。

第一步完整读取并遵守 `C:\Projects\else\.net\WPF\mmod_record\.codex\skills\mmod-record-workflow\SKILL.md` 和唯一进度真相源 `C:\Projects\else\.net\WPF\mmod_record\docs\roadmap\plans\archive\OBS_TGA_MODE_SHELL_RELAY_PLAN.md`。

你是本任务唯一的执行 AI。请在 `C:\Projects\else\.net\WPF\mmod_record` 的当前 `main` 工作区连续完成计划 R0–R9：实现、Review、修复、最小相关验证、视觉/运行验收、文档同步和最终回执，不要完成一个阶段后等待下一份提示词。

核心目标：在 WPF 标题栏应用名右侧增加 OBS/TGA 双态开关，并把它做成应用顶层模式。OBS 模式主导航仅显示“录制与处理、设置”，其工作台按“准备/复制现有 CFG 与慢放指令 → 用户在外部 OBS 录制 → 导入视频并批量处理”组织；TGA 模式主导航仅显示“任务、设置”，保留无人值守回放任务、TGA 录制状态机与遥测。公共的输出、运动模糊、画质、编码和后期设置使用同一份持久化字段；设置页只显示当前模式专属项。模式唯一真相源必须是现有 `UserSettings.CaptureMode` / `SettingsViewModel.CaptureMode`。

先读取 `AGENTS.md`、`README.md`、计划列出的设计/规范、8 张 960×720 PNG、当前白名单代码与其 diff，并核对 WPF-UI 4.3.0 TitleBar/NavigationView/所选控件源码。先执行 `git branch --show-current`、`git status --short` 和相关 `git diff`。工作区已有大量未提交 WPF-UI 对齐修改，全部是用户受保护资产；不得 branch、worktree、stash、reset、clean、checkout 覆盖、删除或格式化无关内容。

严格按计划的唯一方向、白名单、状态/错误契约、验收矩阵、命令和防震荡协议执行。特别注意：

- 顶栏切换必须复用现有模式保存逻辑，重启可恢复；不得维护第二份模式状态。
- OBS 运行、TGA 捕获/任务运行或受控收尾期间必须拒绝模式切换并显示原因，不能只换 UI。
- 切换后动态更新有效导航和默认落点；在公共设置页切换时留在设置页，只替换专属设置。
- OBS 页不得虚构应用自动控制 OBS；使用已有真实 CFG/慢放、文件导入、拖放、队列与处理命令。
- TGA 模式不再暴露手工合成导航，但不得因此破坏或重写任务录制状态机。
- 标题栏开关必须位于可交互 Header 区，不能覆盖 WPF-UI TitleBar 模板；验证拖窗、双击和系统按钮。
- 不新增/修改测试、断言、fixture、stub、故障注入或测试专用生产钩子；不改 Native；不提交、不推送、不发布。

每完成一个计划复选项并取得证据，立即在计划中勾选并更新“当前执行状态”，再继续下一项。建立并持续更新 `C:\Projects\else\.net\WPF\mmod_record\docs\design\OBS_TGA_MODE_ACCEPTANCE.md`。构建若被运行中应用锁定，记录事实并改用 `.workbuddy\build-obs-tga-mode\` 临时输出，不得强停用户应用。构建/Smoke 不能代替 OBS 实录、真实游戏、GPU 或视觉验收。

遇到计划定义的停止条件就立即停止震荡：终止长进程、停止编辑、保留现场，把首个错误、已试策略、最后成功证据和下一步写回计划，只提出一个真正改变方向的问题。否则持续到所有可完成项闭环，并按计划第 9 节返回完整回执。

