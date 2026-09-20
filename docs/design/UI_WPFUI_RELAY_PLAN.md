# WPF-UI 设计稿对齐：AI 执行计划

## 1. 基线与目标

本计划采用 Relay 路线，只授权另一名执行 AI 在现有工作区继续实现。2026-09-14 核对：仓库 `main` 位于 `5d94430`；`src/Mmod.App/Mmod.App.csproj` 引用 WPF-UI 4.3.0；`docs/design/` 的八张 PNG 和 README 是当前 960×720 设计基线。当前工作区已经有大量未提交 UI 文件、两份旧计划以及新增目录；执行 AI 必须先记录并保护现状，不得把旧计划视为当前验收事实。

设计目标：在 960×720 基线和允许的最小窗口宽高下，合成、任务、设置三页的结构、信息层级、状态反馈和交互与设计稿一致；有对应 WPF-UI 组件时使用其真实组件或框架隐式样式，不用自制控件模板伪装。保持录制、合成、任务、设置的现有业务行为。参考源码 `reference/WPFUI/` 用于核对 API、默认样式和 Gallery 用法，不把该目录当成应用运行时依赖。

优先级：`docs/design/README.md` 与 PNG > 当前可运行的业务语义 > 旧草稿 `UI_FRAMEWORK_ALIGNMENT_PLAN.md` / `WPFUI_COMPONENT_MAPPING_AND_PLAN.md`。两份旧草稿的 1440×900 方案、`ui:TreeView` 等互相矛盾段落不可直接照抄。`README.md` 中组件映射也须以 4.3.0 源码核实，尤其是同名原生控件、带键默认样式和隐式样式。若原型是示意数据，必须绑定真实 ViewModel 数据，不能硬编码示例数值。

## 2. 工作分级、范围与策略

I2：跨窗口壳、三个页面、共享样式、反馈服务和 ViewModel 的一致性改造，但业务数据和持久化边界保持不变。一名执行 AI 可在一个完整回合内完成“差异表 → 实现 → 自审 → 构建 → 可行的运行验收 → 回执”；只在实际上下文不足或发现无法界定的业务架构冲突时停止。不要因每个页面单独拆成多轮。

**允许修改的生产文件**：`src/Mmod.App/App.xaml`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`Styles/AppResources.xaml`、`Views/Pages/ComposePage.xaml`、`ComposePage.xaml.cs`、`Views/Pages/TasksPage.xaml`、`TasksPage.xaml.cs`、`Views/Pages/SettingsPage.xaml`、`SettingsPage.xaml.cs`、`ViewModels/ComposeViewModel.cs`、`ViewModels/TasksViewModel.cs`、`ViewModels/SettingsViewModel.cs`、`ViewModels/QualityModuleViewModel.cs`、`Converters/TaskPresentationConverters.cs`、`Services/DialogService.cs`、`Services/DialogServiceLocator.cs`。这些文件仅在设计交互或数据绑定确需时编辑，不为美化改业务计算。

**新增文件**：默认不新增；若共享转换器或资源确需拆分，仅允许在 `src/Mmod.App/Converters/`、`Styles/`、`Services/` 创建，并在回执逐项解释。**删除/重命名**：默认不允许。**测试文件**：默认不改；已有 SmokeTest 可运行。**保留**：`src/Mmod.Core/`、`src/Mmod.Native/`、`reference/WPFUI/`、`docs/design/` 原型和两份旧草稿、其他用户改动。设计差异记录与验收截图可放在 `docs/design/`，不得覆盖原型 PNG。不得提交、推送或清理工作区；本次用户仅请求执行计划。

## 3. 唯一执行回合 R1：设计差异修复

**入口**：执行 AI 阅读仓库 `AGENTS.md`、`.codex/skills/mmod-record-workflow/SKILL.md`、本计划、`docs/design/README.md` 和八张 PNG；检查 `git status --short`、当前 XAML/VM/服务和 `reference/WPFUI/` 的实际 4.3.0 实现。先做逐画板差异表：设计元素、当前代码位置、真实 WPF-UI 组件或隐式样式、交互状态、待修问题。差异表是执行过程证据，不是一个独立的只读交接回合。

**实现顺序**：

1. 窗口与共享资源：核对 960×720、标题栏、导航展开/收起、内容安全区、弹层与通知宿主；清除会覆盖框架 `ControlTemplate` 的自定义样式，仅保留布局与语义资源。核对默认样式键后才使用 `BasedOn`；无可引用默认键的控件尽量局部设属性。
2. 合成页（画板 01/02）：核对 TGA 和 OBS 模式切换、标题与操作按钮换行、主副列、状态卡、进度、路径及数值、空/运行/失败状态。所有可见动作仍调用原有 Command/处理器；不可用状态有明确禁用或反馈。不能为了匹配示意稿重复显示同一指标。
3. 任务页（画板 03/04）：核对回放记录/执行队列/任务详情三页签、搜索与筛选、树与列表选择、队列操作、底部常驻遥测、确认/失败/重试反馈。保留用户选中项和已有真实数据流；切页与过滤后不能丢失未完成任务或误操作。搜索框双向更新及清空行为须实际验证。
4. 设置页（画板 05/06）：核对六页签、字段分组、路径/数值输入、模式切换、危险操作确认及保存/恢复反馈。确保页签头可见且内容可滚动，字段变化仍绑定现有设置模型；不改变默认值、存储格式、安全阈值或真实执行逻辑。
5. 组件规范（画板 07）：核对文字层级、主题色资源、卡片描边与圆角、按钮和反馈状态、明暗主题及键盘焦点。布局用 Grid/Border 等原语可接受；交互件优先真实 WPF-UI 类。部分 CheckBox/ComboBox/TreeView/Slider/ProgressBar 可能通过原生标签加载框架隐式样式，不能仅凭缺少 `ui:` 前缀判定违规。不要把“零自绘”误解为禁止纯装饰布局。

**交互不变量**：设计图里的示例文本、指标、任务数不进入生产绑定；已有命令的触发、确认、取消、错误和重试语义保持；`ContentDialog` 和 `Snackbar` 需依附正确宿主；`TabView` 切换不能吞掉内容或破坏 ViewModel 生命周期；主题色仅使用 WPF-UI/系统资源；不添加仅供测试的生产功能。

**完成证据**：逐画板差异表的每一项标注完成、合理偏差或未验证；提供 960×720 与最小窗口下三页及 TGA/OBS、任务三个页签、设置六页签的实际截图或明确说明无法进行视觉验收；验证搜索、切页、模式切换、确认取消、禁用与失败反馈；复核框架源码或 Gallery 用法以证明所选控件和样式键有效。不能用“构建成功”声称视觉和运行交互通过。

**验证命令**（在仓库根目录）：`dotnet build src/Mmod.App/Mmod.App.csproj -p:SkipNativeBuild=true`；`dotnet run --project src/Mmod.SmokeTest/Mmod.SmokeTest.csproj`（若现有测试确实覆盖相关行为才据此声称覆盖）；`git diff --check`；`git diff -- src/Mmod.App`；`git status --short`。若运行中的应用锁定输出，先确认进程与影响，再使用临时 `OutDir` 构建，不得强停用户应用。完整原生构建不是本回合 UI 验收的必要条件。静态扫描颜色、`ControlTemplate`、原生 `MessageBox` 和绑定诊断仅作线索，逐处核查合理例外。

**停止条件**：现有未提交改动无法安全区分；需要修改上述范围外的生产/持久化/安全逻辑；WPF-UI 4.3.0 无法支持原型所要求的关键交互且没有同等组件组合；构建基础失败无法归因；上下文不足以完成自审和真实回执。此时完成所有安全的范围内工作，记录具体阻塞与证据，不宣称完成。

## 4. Codex 审核与收尾

执行 AI 返回简明回执：结论、未提交原因、实际改动文件、每条命令及结果、截图/运行证据、偏离和未完成项。Codex 随后独立检查当前 diff、设计差异表、组件 API、构建与交互证据，按 Skill 判定 Pass / Advance with closeout / Fail。非基础性问题进入有精确文件与验收条件的 closeout ledger；最终必须处理完或由用户明确接受剩余项。最终验收以设计稿可见行为、业务不变量和实测证据同时成立为准。
