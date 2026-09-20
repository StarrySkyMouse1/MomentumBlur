# OBS / TGA 顶层模式工作台：Standard Relay 执行计划

## 当前执行状态

- 状态：已完成（R0–R9 全部完成并勾选；视觉/真实交互项按计划列明为未验证）
- 当前步骤：无待执行项；本计划已归档到 `docs/roadmap/plans/archive/`
- 工作流：Standard Relay；由一个执行 AI 连续完成实现、Review、验证、修复、文档同步和回执
- 计划真相源：本文件；每一项只有在取得证据后才能勾选，并须同步更新本节
- 计划建立时间：2026-09-15；完成时间：2026-09-15
- 仓库：`C:\Projects\else\.net\WPF\mmod_record`
- 分支：`main`
- 基线说明：工作区已有大量未提交的 WPF-UI 对齐修改，尤其覆盖 `MainWindow`、合成/任务/设置页面及其 ViewModel；全部视为用户受保护修改，不得还原、覆盖或顺手整理
- 验收记录：`docs/design/OBS_TGA_MODE_ACCEPTANCE.md`（R0–R8 证据已同步）
- 环境事实：运行中的应用实例锁定了 `bin\Debug\net10.0-windows\mmod_record_next.exe`，全部构建改用 `src/Mmod.App/.workbuddy/build-obs-tga-mode/` 临时输出，未强停用户进程
- 环境限制：`Add-Type` 被安全策略拦截 → 无法脚本化截图 / UIA；视觉验收项需用户提供截图（见验收记录 R8）
- 防震荡协议：未触发（无同一失败签名连续 3 次、无 60 秒无输出命令、未越过白名单、未修改测试）

## 1. 目标与唯一产品方向

在截图红框所示标题栏应用名称右侧加入明确的 `OBS / TGA` 双态切换开关。该开关不是合成页内部筛选器，而是整个应用的顶层工作模式：切换后，截图蓝框所示主导航、默认落点、页面内容和设置页中的模式专属配置同步变化；公共处理配置继续使用同一份数据。

模式契约冻结如下：

| 范围 | OBS 模式 | TGA 模式 |
|---|---|---|
| 顶栏 | `OBS` 为选中态 | `TGA` 为选中态 |
| 主导航 | `录制与处理`、`设置` | `任务`、`设置` |
| 默认页 | `录制与处理` | `任务` |
| 主流程 | ① 准备/复制现有 CFG 与慢放指令；② 用户进入游戏并由外部 OBS 完成录制；③ 将录制视频加入队列并做运动模糊、画质和编码处理 | 扫描 Momentum 回放、创建持久化任务、自动启动并控制游戏、通过 `startmovie` TGA 录制、流式合成、验证并输出 MP4 |
| 公共设置 | 输出目录、超采样/运动模糊、画质处理、后期 4K 指引、中间母版编码等继续共用 | 同左，读写同一份字段，不复制两套配置 |
| 专属设置 | OBS 源帧率、并行路数、OBS 录制所需慢放/恢复指令 | 游戏目录、TGA 监视目录、序列/热键/CFG、RAM 盘/Junction、TGA 积压与磁盘安全 |

`UserSettings.CaptureMode` 是唯一持久化模式字段，现有默认值和旧设置兼容行为保持不变。顶栏切换、导航投影、页面可见性和设置页筛选都读取同一个 `SettingsViewModel.CaptureMode`，禁止再造第二个仅存在于 UI 的模式状态。

OBS 模式中的“通过 CFG 然后录制”沿用已有 `SlowMotionBlock`、`RestoreBlock`、复制命令及当前 OBS 批处理能力。除非仓库已经存在可复用的真实接口，否则本任务不自动启动/操控 OBS、不伪造 OBS 录制状态，也不新增注入、虚拟摄像头或音轨能力。

TGA 模式的用户工作台只显示“任务”和“设置”；原合成页里的手工 TGA 监视入口不再暴露为主导航功能。任务执行仍必须复用现有 `RenderTaskRunner`、Attempt/CaptureSession 隔离、正向成功证据、受控清理、重试分类、媒体验证和原子输出契约，不能把任务页降级成手工监视器。

## 2. 非目标与禁止捷径

- 不重写 `Mmod.Core` 录制、合成、Native/GPU、编码和任务状态机；仅在模式切换安全门确需暴露现有运行态时做最小只读投影。
- 不自动控制 OBS，不新增音频处理，不把 OBS 模式误实现为 TGA `startmovie`。
- 不为两个模式复制公共设置字段、JSON 节点或 ViewModel；公共配置必须真正共用。
- 不删除 TGA 手工合成代码来制造“只显示任务”；优先停止导航暴露并保持兼容，除非 Review 证明代码已无任何调用且删除得到单独授权。
- 不用 `Visibility` 留下可聚焦的隐藏导航项，不允许切换后停留在另一模式的无效页面。
- 不硬编码示例路径、帧数、任务数或运行状态，不吞异常，不静默 fallback 到另一模式。
- 不改 `src/Mmod.Native/`、录制测试、断言、fixture、stub、故障注入入口或测试专用生产钩子。
- 不覆盖 `docs/design/` 的八张基线 PNG；现有 `R1_DIFF_TABLE.md` 与未提交 UI 对齐成果必须保留。

## 3. 读写白名单与受保护范围

执行前必须阅读：

- `AGENTS.md`
- `.codex/skills/mmod-record-workflow/SKILL.md`
- `README.md`
- 本计划
- `docs/design/README.md`、`docs/design/R1_DIFF_TABLE.md` 及 8 张 960×720 PNG
- `docs/superpowers/specs/2026-07-17-mmod-record-next-design.md`
- `docs/superpowers/specs/2026-08-03-unattended-render-tasks-design.md`
- 下列允许修改文件的当前内容与 diff
- WPF-UI 4.3.0 `reference/WPFUI/src/Wpf.Ui/Controls/TitleBar/`、`NavigationView/`、`ToggleSwitch/` 或实际采用组件的源码/API

允许修改的生产文件：

- `src/Mmod.App/MainWindow.xaml`
- `src/Mmod.App/MainWindow.xaml.cs`
- `src/Mmod.App/ViewModels/MainViewModel.cs`
- `src/Mmod.App/ViewModels/SettingsViewModel.cs`
- `src/Mmod.App/ViewModels/ComposeViewModel.cs`（仅 OBS 工作台投影、模式切换安全状态或已有命令复用所需）
- `src/Mmod.App/ViewModels/TasksViewModel.cs`（仅模式切换安全状态或 TGA 页面投影所需）
- `src/Mmod.App/Views/Pages/ComposePage.xaml`
- `src/Mmod.App/Views/Pages/SettingsPage.xaml`
- `src/Mmod.App/Views/Pages/TasksPage.xaml`（只在模式文案或无效入口确需调整时）
- `src/Mmod.App/Styles/AppResources.xaml`（仅复用型顶栏分段样式确需时）
- `src/Mmod.App/Converters/`（仅现有转换器不能表达模式可见性/选中态时新增或扩展）

允许新增一份验收记录：`docs/design/OBS_TGA_MODE_ACCEPTANCE.md`。默认不新增其他文件，不删除、不重命名。若实现必须越过白名单，先停止并把原因、所需文件和契约影响写回本计划。

现有未提交文件及未跟踪目录都是用户资产。禁止 branch/worktree/stash/reset/clean/checkout 覆盖；禁止格式化无关文件。测试文件无修改授权。用户本轮只要求计划，因此执行 AI 也无 commit、push、发布或外部操作权限。

## 4. 状态、导航与错误契约

1. **单一模式状态**：切换调用 `SettingsViewModel.SetObsModeCommand` / `SetTgaModeCommand` 或等价的单一入口，沿用现有 `OnCaptureModeChanged` 自动保存；应用重启恢复上次模式。
2. **导航投影**：模式改变后原地重建/更新 `NavigationView` 的有效菜单集合。OBS 仅能导航到 Compose（显示为“录制与处理”）与 Settings；TGA 仅能导航到 Tasks 与 Settings。设置页在两模式都存在。
3. **合法落点**：如果当前页不属于目标模式，切换成功后立即导航到目标模式默认页；若正在公共设置页则留在设置页，并让内容随模式刷新。启动时直接进入持久化模式的默认页，不再固定 Compose。
4. **切换安全门**：OBS 批处理、TGA 手工管线或无人值守任务处于不可安全切换的运行/停止收尾状态时，拒绝切换并通过现有 `ContentDialog`/`Snackbar`/`InfoBar` 给出明确原因；不得仅换 UI 而让后台按旧模式继续造成状态错配。优先从现有状态派生只读 `CanSwitchCaptureMode` 和阻塞原因，不改变核心状态机。
5. **设置投影**：去掉设置页中的第二套模式选择器，避免两个入口竞争。设置页保留公共区，并只呈现当前模式专属区；切换后公共字段值保持，隐藏字段仍保存在原模型中且不被重置。
6. **OBS 工作台**：Compose 页在 OBS 模式下形成连续三步信息层级：CFG/慢放准备、外部 OBS 录制说明、视频导入与处理队列。所有按钮绑定现有真实命令；外部录制步骤明确标注需要用户在 OBS 中操作。
7. **TGA 工作台**：Tasks 页继续呈现回放记录、执行队列、任务详情与常驻遥测；创建任务和执行前置检查仍要求 TGA 模式。隐藏 Compose 导航不改变任务的设置快照或执行契约。
8. **标题栏交互**：模式开关位于应用标题右侧、系统窗口按钮左侧，属于 WPF-UI TitleBar 的可交互 Header 区；必须验证拖窗、双击标题栏、最小化/最大化/关闭、键盘焦点和 960×720 布局不被破坏。采用 WPF-UI 真实公开属性/模板插槽，不覆盖 TitleBar 的 ControlTemplate。

## 5. 串行执行清单

- [x] **R0 — 冻结现场和差异证据。** 记录 `git branch --show-current`、`git status --short`、白名单文件 diff；确认用户 UI 改动和运行中进程锁。把“当前模式入口、启动落点、导航项、Compose 双模式区域、Settings 六页签”的代码位置写入 `docs/design/OBS_TGA_MODE_ACCEPTANCE.md`。证据：文件行号与简要基线，不修改生产代码。
  - 证据：验收记录 R0 节（分支 `main`；11 个已跟踪文件被修改，+2051/-659；`mmod_record_next (14828)` 锁定默认输出；模式入口/启动落点/导航项/Compose 双模式/Settings 六页签的改动前行号已逐条记录；WPF-UI 4.3.0 `TitleBar`/`NavigationView`/`TabView` API 事实表）。
- [x] **R1 — 建立顶层模式状态与安全门。** 让 `MainViewModel`/壳层可观察同一个 `SettingsViewModel.CaptureMode`，复用持久化命令，汇总 OBS/TGA 当前活动状态并阻止不安全切换。必须覆盖成功切换、同模式点击、运行中拒绝、保存失败/异常反馈。证据：相关 diff、自审说明与可观察属性/命令调用链。
  - 证据：`SettingsViewModel.TrySwitchCaptureMode`（唯一写入口）+ `CaptureModeSwitchBlocker` 注入点；`MainViewModel` 汇总 `ComposeViewModel.DescribeCaptureModeSwitchBlock()` / `TasksViewModel.DescribeCaptureModeSwitchBlock()`；四条路径均显式处理（同模式早退、运行中拒绝并弹层说明、成功走既有 `OnCaptureModeChanged`→`Persist`、保存异常回滚 + 尽力写回 + 报错文案）。模式仍只有 `SettingsViewModel.CaptureMode` 一份状态。
- [x] **R2 — 实现标题栏分段开关。** 在应用名右侧放置 `OBS`、`TGA` 双态按钮或符合现有设计语言的分段器，选中态使用系统强调色，含 ToolTip/AutomationProperties，且不覆盖框架模板。验证标题、开关、系统按钮在 960 宽及最小 880 宽不重叠。证据：WPF-UI 4.3.0 API 来源与实际截图。
  - 证据：`MainWindow.xaml` 使用 `ui:TitleBar.Header` 公开内容槽（未覆盖 `ControlTemplate`）；`AppTitleModeSegmentButton` + `BooleanToControlAppearanceConverter(ConverterParameter=titlebar)`（选中 = Secondary 受控填充）+ `BooleanToThemeBrushConverter`（选中态文字用 `AccentTextFillColorPrimaryBrush` 系统强调色）；带 ToolTip 与 `AutomationProperties.Name/HelpText`；应用名与分隔线 `IsHitTestVisible="False"` 以保留该区域的 HTCAPTION 拖动。宽度预算：可用 ~777px（960）/~697px（880），标题+开关约 270px。截图见 R8 未验证项。
- [x] **R3 — 模式化主导航和合法落点。** OBS 菜单为“录制与处理/设置”，TGA 菜单为“任务/设置”；启动按已保存模式进入默认页，模式切换时遵守公共设置页留驻规则和非法页跳转规则。连续 OBS→TGA→OBS 后不得出现重复菜单项、失效选中态或空 Frame。证据：每种模式导航截图和切换记录。
  - 证据：`MainWindow.ApplyCaptureModeNavigation()` 复用同一批 `NavigationViewItem` 常驻实例、只用 Remove/Insert 增量通知（避免 Reset 重建容器）；启动落点由 `DefaultPageFor(CaptureMode)` 决定；`Navigated` 事件权威跟踪当前页；设置页留驻规则在 `OnSettingsPropertyChanged` 中实现。两种落点冒烟均通过（见 R8）。持续 OBS↔TGA 切换的截图归入 R8 未验证项。
- [x] **R4 — 收束 OBS 工作台。** Compose 页移除对 TGA 主工作流的可见投影，按“准备 CFG/慢放 → 外部 OBS 录制 → 导入视频并处理”重排；复用现有指令、文件选择/拖放、队列、批处理、取消和状态反馈。不得宣称已自动录制。证据：空队列、有队列、处理中/取消或不可运行态截图与命令映射。
  - 证据：`ComposePage.xaml` 全文重写为三步工作台（① 准备：`Settings.SlowMotionBlock`/`RestoreBlock` + `CopySlowMotionCommand`/`CopyRestoreCommand`；② 外部录制：`ObsRecordingHint` 明确“本应用不会自动启动、控制或读取 OBS” + `OpenOutputFolderCommand`；③ 导入并处理：`AddVideosCommand`/`StartObsBatchCommand`/`CancelObsBatchCommand`/`ClearBatchCommand` + 拖放落区 + `ui:DataGrid` 队列 + `StatusSeverity`/`StatusText` 运行消息）。原 TGA 监视卡、侧栏「监视盘空间/本会话（已喂入/待处理）」等 TGA 投影已移除；`ComposeViewModel` 的 `_tga` 编排器与 `StartTgaCommand`/`StopTgaCommand` **未删除**。VM 新增只读投影 `PageTitleText`/`DiskCardTitle`/`ObsRecordingHint`/`ObsOutputDirectoryText`/`Settings`。
- [x] **R5 — 收束 TGA 任务工作台。** 确认 TGA 模式只有任务与设置入口；Tasks 页所有已有树、队列、详情、运行控制和遥测仍可用，且任务冻结配置不受之后公共设置修改影响。证据：TGA 导航、任务页三页签和前置检查结果。
  - 证据：`TasksPage.xaml`/`TasksPage.xaml.cs` **零改动**（`git diff --stat` 与 R0 基线一致：+636/-…）；TGA 菜单投影为「任务/设置」并在 TGA 落点冒烟中正常到达任务页；`TasksViewModel.ValidateTaskSettings` 的“任务仅在 TGA 模式下可用”前置检查未改动；`CreateTasks` 仍写入冻结快照。
- [x] **R6 — 拆分公共与模式专属设置。** 公共区在两模式一致显示并写同一字段；OBS 只显示 OBS 帧率/并行/录制指令；TGA 只显示游戏/TGA/CFG/Junction/监视盘安全。删除设置页内部 CaptureMode 分段器但不删除模型字段。来回切换后隐藏配置值不丢失、不互相覆盖。证据：设置页两模式截图、重启恢复与 settings.json 差异检查（不得回显用户敏感路径内容）。
  - 证据：删除「捕获模式」分段器（`SetTgaModeCommand`/`SetObsModeCommand` 仍保留为唯一写入口，供标题栏使用，`CaptureMode` 模型字段未删除）；`ui:TabView` 的「游戏 TGA」(索引 3) 与「OBS 模式」(索引 4) 改为 `Visibility` 绑定 `IsTgaMode`/`IsObsMode`；公共页签 0/1/2/5 常显；原「磁盘与安全」页签改为公共「编码」（仅中间母版码率），TGA 专属的监视盘安全下限去重后只留在「游戏 TGA」页签；`SelectedSettingsSectionIndex` 绑定 `SelectedIndex`，切模式后若停留在另一模式专属页签则回到公共页签（否则折叠页签会让内容区变空）。隐藏字段仍在同一批控件内（`Visibility` 折叠，不重建），值不丢失。重启恢复证据见 R8；`settings.json` 仅在 R8 冒烟中临时改 `captureMode` 后按 SHA256 精确还原，未回显任何用户路径内容。
- [x] **R7 — 实现后 Review 并修复。** 逐条核对本计划、两份产品设计、`docs/design/R1_DIFF_TABLE.md` 和当前 diff；重点审查模式单一真相源、运行中切换、导航生命周期、事件订阅释放、WPF 绑定错误、公共字段重复、标题栏 hit-test、用户改动保护。修复阻断项后复审。证据：Review 清单、发现项、修复位置；无发现也要记录检查范围。
  - 证据：见验收记录 R7 节（含 3 处自发现并当场修复的加固项）。
- [x] **R8 — 最小相关验证与视觉验收。** 运行下述矩阵和命令；失败从第一个真实错误处理。将结果、截图路径、合理偏差和未验证项写入验收记录。只有静态、构建、运行交互三类证据满足各自边界时才可勾选。
  - 证据：验收记录 R8 节。构建（临时 `OutDir`）0 错 0 警；`Mmod.SmokeTest` 通过；两种持久化模式各跑一次启动冒烟（存活 + 标题非空 + Responding + 优雅关闭）；`git diff --check` 无空白错误；资源键/图标名全量静态审计 0 缺失。**视觉与真实交互证据缺失**：本环境 `Add-Type` 被安全策略拦截，无法脚本化截图/UIA，已逐条列为未验证项并给出人工验收清单。
- [x] **R9 — 文档和 Git 收口。** 更新本计划当前状态与全部证据；完成后把本计划和执行提示词移动到 `docs/roadmap/plans/archive/`，修正引用。报告实际文件、命令结果、视觉/实机缺口与 `git status --short`。未经用户新增授权不得 commit/push。
  - 证据：本节 R0–R9 全部勾选并附证据；`docs/design/OBS_TGA_MODE_ACCEPTANCE.md` 建立并同步；本计划与 `OBS_TGA_MODE_SHELL_EXECUTION_PROMPT.md` 已归档到 `docs/roadmap/plans/archive/`，`active/` 目录已清空并删除；`active` 目录引用已修正。未 commit、未 push。

## 6. 最小验收矩阵

| 场景 | 预期 |
|---|---|
| 首次/旧配置启动（CaptureMode 缺省） | 沿用模型兼容默认值 `Obs`，顶栏 OBS 选中，落到“录制与处理” |
| 已保存 TGA 后重启 | 顶栏 TGA 选中，只显示“任务/设置”，落到任务页 |
| OBS 默认页切到 TGA | 菜单原地变为“任务/设置”，Frame 导航到任务页，公共设置值不变 |
| TGA 任务页切到 OBS | 菜单变为“录制与处理/设置”，Frame 导航到 OBS 工作台 |
| 任一模式的设置页切换 | 仍停留设置页，专属区立即替换，公共区和值保持 |
| OBS 批处理运行中切 TGA | 切换被拒绝，模式/菜单/页面不变，原因可见 |
| TGA 任务或捕获处于运行/收尾时切 OBS | 切换被拒绝，不破坏 Attempt、任务、游戏会话或清理 |
| OBS 页面 | CFG/慢放准备、外部录制说明、视频队列处理完整；无 TGA 手工监视卡 |
| TGA 页面 | 任务三页签与遥测完整；无 OBS 视频批处理入口 |
| 设置值隔离 | OBS 专属与 TGA 专属字段来回切换后保留；公共字段两边读取同一值 |
| 960×720 / 最小 880×600 | 顶栏无重叠，导航无重复/裁切，页面可滚动，开关可键盘操作 |
| 标题栏窗口行为 | 开关可点击；空白标题栏仍可拖动/双击；系统按钮正常 |
| 深浅主题 | 选中态、焦点、禁用态和文本对比度使用动态主题资源 |

## 7. 验证命令与证据边界

在仓库根目录依次运行：

```powershell
dotnet build src/Mmod.App/Mmod.App.csproj -c Debug -p:SkipNativeBuild=true
dotnet run --project src/Mmod.SmokeTest/Mmod.SmokeTest.csproj -c Release
git diff --check
git diff -- src/Mmod.App docs/design/OBS_TGA_MODE_ACCEPTANCE.md docs/roadmap/plans/archive/OBS_TGA_MODE_SHELL_RELAY_PLAN.md
git status --short
```

若运行中应用锁定默认输出，先记录进程与锁定事实，再使用仓库内临时 `OutDir`（如 `.workbuddy/build-obs-tga-mode/`）构建；不得强停用户应用。SmokeTest 只证明其实际覆盖项，不得把全量通过写成标题栏视觉、OBS 实录、游戏实录或 Native/GPU 验收。真实交互至少要留下 OBS/TGA 顶栏与导航、两模式设置、非法切换反馈的截图。外部 OBS 录制、真实游戏 TGA 任务和 GPU/编码若未执行，必须明确列为未验证，不能用构建代替。

## 8. 防震荡停止协议

出现任一情况立即停止编辑和长进程，保留现场，不提交、不清理，并把完成项、首个错误、尝试策略、当前文件、最后成功证据和下一步写回本计划：

- 同一失败签名连续 3 次；
- 同一子系统尝试 2 种策略后通过数无净增加；
- 连续 3 次修改主要只是调常量、顺序、等待或重试；
- 单个命令 60 秒无新输出；
- 无法区分/保护现有未提交 UI 改动；
- 必须越过白名单、改录制核心契约、修改未授权测试、自动控制外部 OBS、删除用户文件或获取新的 Git/发布权限。

## 9. 完成回执格式

回执必须包含：结论；实际修改/新增文件；模式与导航最终映射；运行中切换策略；Review 发现与修复；每条命令和结果；截图/运行证据；OBS 实录、TGA 实机、Native/GPU 等未验证项；与计划偏差；最终 `git status --short`；明确“未提交/未推送”或获得授权后的 commit/push 事实。只有验收矩阵成立才可报告 Pass。

