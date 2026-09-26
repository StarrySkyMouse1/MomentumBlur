# OBS / TGA 顶层模式工作台 · 验收记录

计划真相源：`docs/roadmap/plans/archive/OBS_TGA_MODE_SHELL_RELAY_PLAN.md`（R9 已归档）
执行提示词：`docs/roadmap/plans/archive/OBS_TGA_MODE_SHELL_EXECUTION_PROMPT.md`
工作流：Standard Relay（单执行 AI 连续完成实现 / Review / 验证 / 修复 / 文档同步 / 回执）
记录建立时间：2026-09-15

状态口径：**完成** / **合理偏差**（含理由）/ **未验证**（含原因）

---

## R0 · 冻结现场与差异证据

### 仓库与工作区

| 项 | 值 |
|---|---|
| `git branch --show-current` | `main` |
| 运行中应用进程 | 无（`mmod_record_next.exe` 未在运行，默认输出目录未被锁定） |
| 未提交 UI 对齐改动 | `src/Mmod.App` 下 11 个已跟踪文件被修改（`git diff --stat`：+2051 / -659） |
| 未跟踪目录 | `.workbuddy/`、`docs/design/`、`docs/roadmap/`、`reference/WPFUI/`、`src/Mmod.App/Converters/`、`Services/`、`Styles/` 等 |

已跟踪修改清单（全部视为用户受保护资产，本任务只在其上做增量修改，不还原、不覆盖、不格式化）：
`App.xaml`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`ViewModels/ComposeViewModel.cs`、
`ViewModels/QualityModuleViewModel.cs`、`ViewModels/SettingsViewModel.cs`、`ViewModels/TasksViewModel.cs`、
`Views/Pages/ComposePage.xaml`、`Views/Pages/SettingsPage.xaml`、`Views/Pages/SettingsPage.xaml.cs`、
`Views/Pages/TasksPage.xaml`。

### 改动前基线（代码位置）

| 关注点 | 改动前位置 | 事实 |
|---|---|---|
| 当前模式入口 | `views/Pages/SettingsPage.xaml` L98–113 | 「捕获模式」分段器，`AppSegmentButton` + `SegmentAppearance`，命令 `SetTgaModeCommand` / `SetObsModeCommand` |
| 模式持久化 | `ViewModels/SettingsViewModel.cs` L24、L90–91、L95–100、L188 | `[ObservableProperty] captureMode`；`SetTgaMode`/`SetObsMode` 直接赋值；`OnCaptureModeChanged` → `IsObsMode`/`IsTgaMode` 通知 + `Persist()`；`ApplyFrom` 从 `UserSettings.CaptureMode` 恢复 |
| 启动落点 | `MainWindow.xaml.cs` L27 | `RootNavigation.Navigate(typeof(Views.Pages.ComposePage))` —— 固定 Compose，与已保存模式无关 |
| 主导航项 | `MainWindow.xaml` L29–39 | 固定三项：合成（`ComposePage`）/ 任务（`TasksPage`）/ 设置（`SettingsPage`），无模式投影 |
| Compose 双模式区域 | `ComposePage.xaml` L54–176（TGA 监视卡，`Visibility={Binding IsTgaMode}`）、L179–226（OBS 批量卡，`IsObsMode`）、L231–254（拖放落区）、L259–300（批量队列表格）、L303–395（侧栏三卡） | 同页并列两套卡，靠 `Visibility` 互斥；侧栏「监视盘空间 / 本会话」为 TGA 语义 |
| Compose 模式状态 | `ComposeViewModel.cs` L146–147、L151–175 | `IsTgaMode`/`IsObsMode` 由 `_settings.CaptureMode` 派生；`RefreshModeSummary` 汇总真实配置 |
| Settings 六页签 | `SettingsPage.xaml` L79–568（L82 捕获与合成 / L204 画质处理 / L255 后期·4K / L320 游戏 TGA / L440 OBS 模式 / L498 磁盘与安全） | 六页签全部常显，两模式专属配置同时可见 |
| 标题栏 | `MainWindow.xaml` L41–43 | `ui:TitleBar` 仅设 `Title` / `CloseWindowByDoubleClickOnIcon`，未使用 `Header` / `TrailingContent` |

### WPF-UI 4.3.0 事实核对（`reference/WPFUI/src/Wpf.Ui/`）

| 事实 | 依据 |
|---|---|
| `TitleBar` 公开三个内容插槽：`Header`（左）/ `CenterContent`（中）/ `TrailingContent`（右），均为 `ContentPresenter`，**无需覆盖 `ControlTemplate`** | `Controls/TitleBar/TitleBar.xaml` L144–160、`TitleBar.cs` L62–87、L240–262 |
| 命中测试：鼠标位于「非默认标题文本的 Header/Trailing/Center 内容」上时不返回 `HTCAPTION`，控件可收到点击；空白标题栏仍返回 `HTCAPTION`（可拖动、可双击最大化） | `TitleBar.cs` L703–747 |
| `TitleBar` 构造函数默认 `Header = _titleBlock`（标题文本）；自定义 `Header` 会替换默认文本块，故应用名需在自定义内容中重新呈现 | `TitleBar.cs` L434–448 |
| `NavigationView.TitleBar` 被附加时会把 `TitleBar.Margin` 设为 `35,0,0,0`，为窗格切换按钮留位 | `NavigationView.Properties.cs` L701–702 |
| `NavigationView.MenuItems` 是 `ObservableCollection<object>`，集合变化被框架支持（`UpdateMenuItemsTemplate` + `AddItemsToDictionaries`）；后者按 `Id`/`TargetPageTag`/`TargetPageType` 去重登记 | `NavigationView.Properties.cs` L293、L542–552；`NavigationView.Base.cs` L343–360 |
| 因此菜单投影必须**复用同一批 `NavigationViewItem` 实例**：按 `TargetPageType` 新建实例不会更新已登记的字典，会留下悬空引用 | 同上（`ContainsKey` 短路） |
| `NavigationView.Navigated` 提供 `NavigatedEventArgs.Page`，可据此准确跟踪当前页 | `INavigationView.cs` L202、`NavigatedEventArgs.cs` |
| `ui:TabView` / `ui:TabViewItem` 是 `TabControl` / `TabItem` 的空派生 → `Visibility=Collapsed` 同时隐藏页签头与内容，且**索引不位移** | `Controls/TabView/TabView.cs`、`TabViewItem.cs` |
| `ControlAppearance` 只有 `Primary/Secondary/Info/Dark/Light/Danger/Success/Caution/Transparent`，**没有 Tertiary/Neutral** | `Controls/ControlAppearance.cs` |
| `Appearance=Transparent` 时按钮背景透明但仍保留 `ButtonBackgroundPointerOver` 悬停反馈；`Secondary` 为受控填充 + 描边 | `Controls/Button/Button.xaml` L289–311 |
| `Snackbar` / `ContentDialog` 走 `DialogService`（宿主已由 `MainWindow.OnLoaded` 注入） | `Services/DialogService.cs` L47–111 |

### 基线结论

- 顶层模式当前只存在于设置页内部，且启动落点、主导航都不随模式变化。
- 标题栏有框架公开可用的可交互 `Header` 插槽，可在不覆盖模板的前提下放置双态开关。
- 导航菜单支持原地重建，但必须复用同一批项实例。
- 改动前的模式唯一真相源是 `SettingsViewModel.CaptureMode`（持久化到 `UserSettings.CaptureMode`），本任务不引入第二份模式状态。

---

## 最终实现映射

### 模式与导航

| 范围 | OBS 模式 | TGA 模式 |
|---|---|---|
| 持久化真值 | `UserSettings.CaptureMode = Obs (1)` | `UserSettings.CaptureMode = Tga (0)` |
| 顶栏选中态 | `Settings.IsObsMode` → `OBS` 选中 | `Settings.IsTgaMode` → `TGA` 选中 |
| 主导航 | 录制与处理（`ComposePage`）、设置（`SettingsPage`） | 任务（`TasksPage`）、设置（`SettingsPage`） |
| 启动默认落点 | `ComposePage` | `TasksPage` |
| 设置页页签 | 0 捕获与合成 / 1 画质处理 / 2 后期·4K / 4 OBS 模式 / 5 编码 | 0 捕获与合成 / 1 画质处理 / 2 后期·4K / 3 游戏 TGA / 5 编码 |

模式唯一写入口：`SettingsViewModel.SetObsModeCommand` / `SetTgaModeCommand` → `TrySwitchCaptureMode` → 既有 `OnCaptureModeChanged` → `Persist()` → `UserSettingsStore.Save`。除标题栏开关外没有第二个模式入口。

### 运行中切换策略

`MainViewModel` 注入 `SettingsViewModel.CaptureModeSwitchBlocker`，只读汇总两个来源：

| 来源 | 阻塞条件 | 提示 |
|---|---|---|
| `ComposeViewModel` | `IsObsBusy`（OBS 批处理运行中） | 等待队列结束或「取消」完成收尾 |
| `ComposeViewModel` | `_tga.IsRunning`（TGA 手工管线运行中） | 先「停止并收尾」并等待写盘物理静默 |
| `TasksViewModel` | `_runner.IsRunning`（含受控停止/清理收尾） | 先暂停或立即停止并等待收尾完成 |

阻塞时保持模式、菜单、页面完全不变，用 `DialogService.ShowInfoAsync`（`ui:ContentDialog`）给出具体原因；不静默 fallback，不改任何录制状态机。

---

## R7 · 实现后 Review

### 审查范围与结论

| # | 审查项 | 结论 |
|---|---|---|
| 1 | 模式单一真相源：是否出现第二份模式状态 | 通过。UI 只读 `SettingsViewModel.IsObsMode`/`IsTgaMode`/`CaptureMode`；写只经 `TrySwitchCaptureMode` |
| 2 | 运行中切换：是否只换 UI 而让后台按旧模式继续 | 通过。四类运行态先行拒绝（见上表） |
| 3 | 导航生命周期：重复项 / 失效选中态 / 空 Frame | 通过（见修复 F2）。复用常驻项实例，`Navigated` 事件权威跟踪当前页 |
| 4 | 事件订阅释放 | 通过。`ComposeViewModel` 在 `DisposeAsync` 中解除设置订阅（既有）；`MainWindow` 的订阅与窗口同生命周期。另记录既有观察 F4 |
| 5 | WPF 绑定路径 | 通过。新增绑定逐条核对：`Settings.*`、`PageTitleText`、`DiskCardTitle`、`ObsRecordingHint`、`ObsOutputDirectoryText`、`SelectedSettingsSectionIndex`、`DiskDetailText`/`DiskStateText`/`DiskSeverity` 均在对应 VM 上存在且可通知 |
| 6 | 公共字段重复 | 通过（部分解释）。中间母版码率、输出目录、超采样/运动模糊、画质处理只出现一次；`HideHudInCfg` 在「游戏 TGA」(CFG) 与「OBS 模式」(慢放指令) 各出现一次，但绑定同一属性、写同一字段，属同一配置在两种语境下的必要呈现 |
| 7 | 标题栏 hit-test | 发现并修复 F1 |
| 8 | 用户既有未提交改动保护 | 通过。仅增量修改白名单文件；`TasksPage.xaml` 零改动；未 branch / worktree / stash / reset / clean / checkout |
| 9 | 全量资源键与图标名静态审计 | 通过。引用键 64 个、缺失 0 个；使用图标 25 个、缺失 0 个 |
| 10 | 静态资源键与 XAML 编译 | 通过。`dotnet build` 0 错 0 警；`{StaticResource}`/`{DynamicResource}` 全量可解析 |

### 发现项与修复

| 编号 | 严重度 | 发现 | 修复位置 |
|---|---|---|---|
| F1 | 阻断 | 自定义 `TitleBar.Header` 后整块内容不再返回 `HTCAPTION`，应用名文本区域会失去拖动/双击最大化能力 | `MainWindow.xaml`：应用名 `ui:TextBlock` 与分隔线 `Border` 设 `IsHitTestVisible="False"`。依据 `TitleBar.cs` L703–747 + `UiElementExtensions.IsMouseOverElement`：Panel 型 Header 需「鼠标下第一个子元素可命中」才算 header 内容，二者不可命中 → 回落 `HTCAPTION`，空白区与标题文字区都可拖动，只有开关按钮是可交互目标 |
| F2 | 中 | 菜单投影用 `MenuItems.Clear()` 触发 Reset 通知；`OnMenuItems_CollectionChanged` 对 `NewItems == null` 提前返回，且容器被整体重建的风险高于增量变更 | `MainWindow.xaml.cs`：`ApplyCaptureModeNavigation` 改为 Remove/Insert 增量变更并按期望顺序校正 |
| F3 | 中 | 保存失败路径只回滚内存状态，未说明「磁盘上可能已写入目标模式」的不一致 | `SettingsViewModel.TrySwitchCaptureMode`：回滚后尽力 `_store.Save(_settings)` 写回原模式；写回也失败时在报错文案中如实说明并提示检查写入权限 |
| F4 | 低（记录，未改） | `ComposeViewModel` 实现 `IAsyncDisposable`，但 `MainViewModel` 未在窗口关闭时释放它 | 既有行为，事件订阅随进程结束自然终止；属本任务范围外，不顺手改动 |
| F5 | 低 | 设置页页签索引常量与 XAML 页签顺序是隐式耦合 | `SettingsPage.xaml`：在两个模式专属页签注明「页签索引 N」与必须同步的常量名 |

复审：F1–F3 修复后重新构建（0 错 0 警）并复跑两种落点冒烟（均通过），未引入新发现。

---

## R8 · 最小相关验证与视觉验收

### 命令与结果

| 命令 | 结果 |
|---|---|
| `dotnet build src/Mmod.App/Mmod.App.csproj -c Debug -p:SkipNativeBuild=true` | 编译阶段通过；链接阶段 `MSB3027/MSB3021` 失败——运行中的 `mmod_record_next (14828)` 锁定 `bin\Debug\net10.0-windows\mmod_record_next.exe`。未强停用户进程 |
| `dotnet build src/Mmod.App/Mmod.App.csproj -c Debug -p:SkipNativeBuild=true -p:OutDir=.workbuddy/build-obs-tga-mode/` | **已成功生成。0 个警告，0 个错误**（实际输出目录：`src/Mmod.App/.workbuddy/build-obs-tga-mode/`，含 `mmod_native.dll`、`Wpf.Ui.dll`、`mmod_record_next.exe`） |
| `dotnet run --project src/Mmod.SmokeTest/Mmod.SmokeTest.csproj -c Release` | `Output: …\mmod_smoke\smoke_20260915_165002.mp4` / `OK size=10642 bytes progress=(30, 30)`；13 秒正常退出。仅覆盖该冒烟实际覆盖的合成路径，不代表顶栏视觉、OBS 实录、游戏实录或 Native/GPU 验收 |
| `git diff --check` | 无空白错误（仅 Git 的 LF→CRLF 提示，退出码 0） |
| `git status --short` | 见文末「Git 状态」；未提交、未推送 |

### 运行时冒烟（临时构建实例，未强停用户应用）

| 场景 | 方法 | 结果 |
|---|---|---|
| 已保存 TGA 启动 | 保持 `captureMode: 0`，启动 `mmod_record_next.exe`，16s 后读进程 | `HasExited=False`、`MainWindowTitle=[Momentum 运动模糊合成]`、`Responding=True`；`CloseMainWindow` 后优雅退出。证明：MainWindow 顶栏（含新开关与 Header 内容）、TGA 菜单投影、`TasksPage` 全部解析成功 |
| 已保存 OBS 启动 | 临时将 `captureMode` 改为 1 后启动，16s 后读进程 | 同上全部通过；另证明 OBS 菜单投影与**重写后的 `ComposePage`（OBS 工作台）** 解析成功 |
| 用户设置文件 | 两次冒烟前备份、后还原 | 还原后 SHA256 与备份一致（`restore_identical=True`），未回显任何用户路径内容 |

冒烟判定口径：进程存活 + `MainWindowTitle` 非空 + `Responding` 证明全部 `StaticResource` / `BasedOn` 键与页面构造可解析；优雅关闭证明无启动期挂死。

### 静态审计（覆盖非初始页的惰性解析风险）

| 审计 | 方法 | 结果 |
|---|---|---|
| `{StaticResource}` / `{DynamicResource}` 键 | 提取 `src/Mmod.App` 全部引用键，与 `Styles/AppResources.xaml` + 页面/窗口内联资源 + `reference/WPFUI/src/Wpf.Ui` 全量 886 个键比对 | 引用 64 个，**未解析 0 个** |
| `SymbolIcon` 图标名 | 提取全部 `Symbol="…"` 与 `{ui:SymbolIcon …}`，与 `SymbolRegular` 9235 个成员比对 | 使用 25 个，**未定义 0 个**（新增 `Code24 = 0xF2F0` 已核实） |
| 枚举取值 | 复核 `InfoBarSeverity` / `InfoBadgeSeverity` / `ControlAppearance` 用法 | 未混用；本任务未新增 `InfoBadge`/`InfoBar` severity 字面量 |

### 验收矩阵结果

| 场景 | 结果 |
|---|---|
| 首次/旧配置启动（CaptureMode 缺省） | 模型默认 `UserSettings.CaptureMode = Obs`；以 `captureMode=1` 冒烟验证 OBS 选中落「录制与处理」。**顶栏选中态视觉未验证** |
| 已保存 TGA 后重启 | 通过（冒烟落任务页）。菜单/顶栏视觉未验证 |
| OBS 默认页切到 TGA | 代码路径成立（`OnSettingsPropertyChanged` → 菜单重建 + 跳转任务页）；**交互未验证** |
| TGA 任务页切到 OBS | 同上；**交互未验证** |
| 任一模式的设置页切换 | 设置页留驻分支成立；页签显隐 + 索引归位逻辑成立；**视觉未验证** |
| OBS 批处理运行中切 TGA | 拒绝路径成立（`IsObsBusy`）；**交互未验证**（需真实批处理） |
| TGA 任务/捕获运行收尾时切 OBS | 拒绝路径成立（`_runner.IsRunning`/`IsVerifying`/`IsPreflighting`、`_tga.IsRunning`）；**未验证**（需真实任务） |
| OBS 页面内容 | 结构成立（三步齐全，无 TGA 手工监视卡）；**视觉未验证** |
| TGA 页面内容 | 通过（`TasksPage` 零改动、冒烟可解析）；三页签与遥测视觉未验证 |
| 设置值隔离 | 结构成立（同一批控件切换 `Visibility`，不重建、不重置）；**来回切换的取值保持未验证** |
| 960×720 / 最小 880×600 | 宽度预算已核算（见 R2 证据）；**实际排布未验证** |
| 标题栏窗口行为 | 命中测试依据已核实（`HwndSourceHook` + `IsMouseOverElement`）；**拖动/双击/系统按钮未实机验证** |
| 深浅主题 | 全部颜色走 `DynamicResource` 主题/强调色键；**对比度未视觉验证** |

### 未验证项与原因

| 未验证项 | 原因 |
|---|---|
| 两模式顶栏开关与导航截图、设置页两模式截图、非法切换反馈截图 | 本环境的 `Add-Type` 被安全策略拦截（已实测：`Command blocked for security: Add-Type compiles and loads .NET code at runtime`），无法脚本化截图或 UIA；`reference` 中的既有记录也确认 `Reflection.Assembly` 同样被拦截。需用户在真实窗口上确认 |
| `SettingsPage` 的运行时解析 | 设置页不是任何模式的默认落点，且无可用 UI 自动化；已用「资源键审计 + 图标审计 + XAML 编译 + 全量绑定路径核对」覆盖其可静态判定的失败模式，剩余风险是纯运行期视觉 |
| 连续 OBS↔TGA↔OBS 切换的重复菜单/选中态 | 同上，需交互 |
| 真实 OBS 录制、真实游戏 TGA 任务、GPU/编码链路 | 本任务不涉及且未执行；构建与冒烟不能代替 |
| 保存失败路径（`settings.json` 不可写） | 未构造故障场景（不新增故障注入），仅做代码级核对 |

### 已知偏差与设计取舍

1. **顶栏开关选中态用 Secondary 填充 + 强调色文字，而不是 Primary 填充按钮。**
   理由：项目规范「Primary 每屏一个，留给常驻主操作」；若顶栏开关也用 Primary，会在每一屏与页面主操作并列争抢强调色。强调色仍用于选中态文字（`AccentTextFillColorPrimaryBrush`），满足计划 R2 的「选中态使用系统强调色」。
2. **`TitleBar.Header` 替换了框架默认标题文本块**，因此应用名由本页重新呈现；已用 `IsHitTestVisible="False"` 保住该区域的 HTCAPTION 拖动与双击最大化。
3. **设置页页签由 6 个常显改为 5 个按模式显示**（公共 4 + 专属 1），页签索引 3/4 为模式专属；原「磁盘与安全」页签改名「编码」并只保留公共的中间母版码率，TGA 专属的监视盘安全下限去重后留在「游戏 TGA」页签。属计划 R6 的要求，与 `docs/design/README.md` 的「横向六页签」基线有意的差异。
4. **`HideHudInCfg` 在两个模式页签各出现一次**（同一属性、同一持久化字段）：TGA 语境是「CFG 中隐藏 HUD」，OBS 语境是「慢放指令中的 cl_drawhud」。
5. **合成页移除 TGA 监视投影，但未删除任何 TGA 代码**：`TgaPipelineOrchestrator`、`StartTgaCommand`、`StopTgaCommand`、`FedCountText`/`PendingCountText`/`ComposedProgressText` 等仍保留在 `ComposeViewModel` 中；其运行态安全门（`_tga.IsRunning`）作为防御性只读投影保留。
6. **临时构建输出位于 `src/Mmod.App/.workbuddy/build-obs-tga-mode/` 与 `src/Mmod.Core/.workbuddy/build-obs-tga-mode/`**（`OutDir` 相对各自项目解析）。属未跟踪的临时产物，未纳入版本控制，未删除。

---

## Git 状态

```text
（节选自 git status --short；完整输出见执行回执）
 M src/Mmod.App/MainWindow.xaml
 M src/Mmod.App/MainWindow.xaml.cs
 M src/Mmod.App/ViewModels/ComposeViewModel.cs
 M src/Mmod.App/ViewModels/MainViewModel.cs
 M src/Mmod.App/ViewModels/SettingsViewModel.cs
 M src/Mmod.App/ViewModels/TasksViewModel.cs
 M src/Mmod.App/Views/Pages/ComposePage.xaml
 M src/Mmod.App/Views/Pages/SettingsPage.xaml
?? src/Mmod.App/Converters/
?? src/Mmod.App/Styles/
?? docs/design/
?? docs/roadmap/
?? .workbuddy/
```

未提交、未推送、未发布。`TasksPage.xaml` / `TasksPage.xaml.cs` / `SettingsPage.xaml.cs` 与 R0 基线完全一致（未被本任务修改）。

---

## 追加修复 · 设置页卡片布局与滚轮（2026-09-15，用户反馈后）

用户反馈：设置页「画质处理」的卡片要做成左右结构、里面的内容宽度 100%，并且鼠标放在卡片上滚轮无法滚动页面。

### 定位过程与结论（正向证据，非推测）

用临时探针（页面 code-behind 遍历可视树 + 合成 `MouseWheelEventArgs`，把指标写 `%TEMP%` 后读取；**诊断完成后已完整删除**）拿到三段证据：

| 阶段 | 关键指标 | 结论 |
|---|---|---|
| 修复前（T2 布局稳定后） | `[page] Actual=1230.3`（可视区仅 669.6）；内层 `ScrollViewer actual=1105.9 viewportH=1077.9 extentH=1077.9 scrollableH=0`；外层 `DynamicScrollViewer scrollable=560.7` 但滚轮后 `0 -> 0` 且 `handled=True` | 页面被撑到 1230px；内层 ScrollViewer 视口=内容高 → `ScrollableHeight=0` → 它把滚轮标记为已处理却滚不动，外层永远收不到事件 |
| 修复前 | `[cardcontrol] actual=735.2 contentActualWidth=932.8` | `ui:CardControl` 的模板是 `[Icon(Auto)][Header(*)][Content(Auto)]` 三列：`Content` 落在末尾 Auto 列，按**内容自然宽度**右对齐并溢出卡片（无限宽度下测量，`TextWrapping` 文本也不换行）——这正是「内容不是 100% 宽」的根因 |
| 修复后 | `[card] actual=112x735.2 contentWidth=705.2 (pad=14,14,14,14)`；内层 `scrollableH=272.5`；滚轮 `inner 0 -> 48`；`outerSV type=`（外层 DynamicScrollViewer 已消失） | 内容宽 = 卡片宽 − 2×padding → 真正 100%；滚轮可滚；页面回到 669.6 |

### 两处根因

1. **`ui:CardControl` 不是内容容器**。其模板把 `Content` 放在末尾 Auto 列（`CardControl` 还是 `ButtonBase`，语义是「左标题 + 右侧单个控件」的行）。承载整块内容必须用 `ui:Card`（其 `ContentPresenter` 为 `Stretch`）。本仓库 13 处 `ui:CardControl` 都没有用 `Header`/`Icon`，全部属于内容容器误用。
2. **`NavigationViewContentPresenter.OnNavigated` 用 `ScrollViewer.GetCanContentScroll(页面)` 决定是否启用外层 `DynamicScrollViewer`**（`reference/WPFUI/.../NavigationViewContentPresenter.cs` L178–191 + `NavigationViewContentPresenter.xaml` 的两个模板）。页面不设该附加属性时取默认 `true` → 外层滚动容器启用 → 页面被按无限高度测量、撑到内容高度 → 页面内每个 `ScrollViewer` 的视口都等于内容高度 → `ScrollableHeight=0`。**这正是历史代码里被删掉的 `RootScroll.Height = ResolveViewportHeight()` + 宿主窗口 `PreviewMouseWheel` 手工转发所绕过的同一个问题**，正确解法是页面级 `ScrollViewer.CanContentScroll="False"`。

### 改动

| 文件 | 改动 |
|---|---|
| `Views/Pages/SettingsPage.xaml` | 页面级 `ScrollViewer.CanContentScroll="False"`；9 处 `ui:CardControl` → `ui:Card`；画质模块卡改为左右结构（左 300px：开关 + 模块名 + 风险/说明 + 恢复默认值；右 `*`：参数行，撑满剩余宽度）；画质概览卡改为左右结构（左：标题 + 说明 + 当前预设摘要；右：预设下拉）；页签索引注释补充 |
| `Views/Pages/ComposePage.xaml` | 页面级 `ScrollViewer.CanContentScroll="False"`；4 处 `ui:CardControl` → `ui:Card`；OBS 处理卡补 `VerticalAlignment="Stretch"`，使其 `*` 行（队列表格）按设计生效 |
| `Views/Pages/TasksPage.xaml` | 仅页面级 `ScrollViewer.CanContentScroll="False"`（同一根因；关闭后底部遥测条按设计常驻在可视区底部，不再可能被撑出窗口） |

### 验证

| 项 | 结果 |
|---|---|
| `dotnet build`（临时 OutDir） | 0 错 0 警 |
| 资源键 / 图标名静态审计 | 引用 64 键 0 缺失；使用 25 图标 0 未定义 |
| 两种落点启动冒烟 | TGA / OBS 各一次：存活 + 标题非空 + 优雅关闭；`settings.json` 按 SHA256 精确还原 |
| 卡片宽度与滚轮 | 探针实测通过（见上表） |
| 临时探针清理 | `src/` 内 `TEMP-DIAG`/`DiagDump`/`scroll-diag` 残留 = 0；`SettingsPage.xaml.cs` 恢复为 20 行原状 |

### 已知取舍与未验证

- **卡片圆角**：`ui:Card` 没有 `CornerRadius`，`Border.CornerRadius` 也不是可用附加属性（编译期 `MC3015`），因此合成页卡片由原来的 8px 变为框架 `ControlCornerRadius`（与任务页、设置页卡片一致）。若要保持 8px，需要在主题字典里改 `ui:Card` 的 `Border.CornerRadius`，属独立改动。
- 滚轮停在**预设下拉框**上时仍不会滚动页面：这是 WPF `ComboBox` 关闭状态下处理滚轮改选值的默认行为，本次未改动（可另行按需转发）。
- 视觉仍需用户确认：左右结构实际观感、880×600 最小尺寸下的排布、模块卡左列 300px 固定宽的留白是否合适。

---

## 追加修复 · 任务页回放树崩溃（2026-09-15，用户报错后）

用户报错：任务页执行「回放操作 → 刷新回放记录」时抛

```text
System.InvalidOperationException: 用于类型"TreeViewItem"的样式不能应用于类型"TreeViewItem"。
   at Mmod.App.ViewModels.TasksViewModel.ApplyCatalogFilter()  →  CatalogView.Add(map)
```

### 根因

`TasksPage.xaml` 的 `AppReplayTreeItem` 写成 `TargetType="{x:Type ui:TreeViewItem}"` +
`BasedOn="{StaticResource DefaultUiTreeViewItemStyle}"`，但**原生 `TreeView` 经 `ItemsSource` 生成的容器是原生
`System.Windows.Controls.TreeViewItem`**——两个类型同名（`Wpf.Ui.Controls.TreeViewItem` vs
`System.Windows.Controls.TreeViewItem`），所以异常信息看起来自相矛盾。

WPF-UI **没有** `TreeView` 子类（全量搜索 `GetContainerForItemOverride`：只有 `BreadcrumbBar` 与 `ListView` 覆写），
因此原生 `TreeView` 永远创建原生容器，`ItemContainerStyle` 写 `ui:TreeViewItem` 必然抛异常。
容器生成在「树已可见 + 集合 `Add`」时同步触发，所以是第二次刷新（或树已展开时）才炸，第一次刷新时树还是
`Collapsed` 因此侥幸通过。

同时证伪了原注释的理由：原生 `TreeViewItem` 的隐式样式（`TreeViewItem.xaml` L341
`<Style BasedOn="{StaticResource DefaultTreeViewItemStyle}" TargetType="{x:Type TreeViewItem}" />`）
就是 `DefaultTreeViewItemStyle`，它本身带 `ActiveRectangle`
（`TreeViewItemSelectionIndicatorForeground`，L103/110/153）→ **选中强调色竖条本来就存在**，
不需要为了它换成 `ui:TreeViewItem`。

### 改动

| 文件 | 改动 |
|---|---|
| `Views/Pages/TasksPage.xaml` | `AppReplayTreeItem`：`TargetType` 改为原生 `TreeViewItem`；`BasedOn` 改为 `DefaultTreeViewItemStyle`；注释改为记录上述事实 |

### 验证（临时探针，跑完即删）

| 项 | 结果 |
|---|---|
| 探针做法 | 临时在 `TasksPage` 上挂 `DispatcherTimer(2s)`，连调两次 `RefreshCatalogCommand` 并捕获异常，把结果写 `%TEMP%`；随后删除探针 |
| 结果 | `refresh1=ok catalog=2 view=2`、`refresh2=ok catalog=2 view=2`（**正是原崩溃路径**） |
| 容器事实 | `treeView items=2 visibility=Visible`、`itemContainerStyleTargetType={x:Type System.Windows.Controls.TreeViewItem}`、`container0=System.Windows.Controls.TreeViewItem` |
| 真实数据 | `已解析 153 条回放；可执行 84 条；旧版不兼容 69 条（已隐藏）；无法解析 0 条` → 确实走到了容器生成路径，不是空目录假通过 |
| 附带核对 | 回放树模板此前从未被真正执行过，故核对其图标绑定：`ReplayTreeNode.Icon` 返回的 `Folder24` / `Map24` / `Document24` 均在 `SymbolRegular` 中 |
| 清理 | `src/` 内 `TEMP-DIAG`/`ProbeCatalogRefresh`/`tree-diag` 残留 = 0；`TasksPage.xaml.cs` 恢复为 13 行原状 |
| 构建 / 冒烟 | `dotnet build` 0 错 0 警；TGA、OBS 两种落点冒烟通过，`settings.json` 按 SHA256 精确还原 |

### 未验证

- 回放树的实际展开/勾选/筛选视觉，以及选中强调色竖条在真实界面中的显示（需用户确认）。
- 该修复只改了 `ItemContainerStyle` 的 TargetType，未改动 `ItemTemplate`、选择逻辑与 `ReplayTreeNode`。

---

## 追加修复 · 任务页回放树不显示下级与勾选框（2026-09-16，用户报「为什么没有展示出下级和可勾选」）

### 现象

任务页「回放记录」树只显示 2 个根节点（地图名），没有展开箭头，一个勾选框也没有。

### 根因

R1 改造把原有的 `HierarchicalDataTemplate` 换成了带 `x:Key` 的普通 `DataTemplate`，
丢掉 `ItemsSource="{Binding Children}"`。而 `TreeViewItem` 的子树**只**来自
`HierarchicalDataTemplate.ItemsSource`，于是：

- `TreeViewItem.HasItems` 恒为 false（数据层没问题：`map.Children` 非空，84 条记录分组正常）；
- `TreeViewItem.xaml` 模板含 `<Trigger Property="HasItems" Value="False">` → `Expander` 隐藏 → 没有箭头；
- 子容器从不生成 → 只有记录行才显示勾选框（`CheckVisibility`），所以一个都看不到。

排除数据问题的证据：同一时刻 `Catalog.Count == 2` / `CatalogView.Count == 2` /
`已解析 153 条回放；可执行 84 条；旧版不兼容 69 条`。

### 改动

| 文件 | 改动 |
|---|---|
| `Views/Pages/TasksPage.xaml` | `ReplayRowTemplate` 恢复为 `HierarchicalDataTemplate` + `ItemsSource="{Binding Children}"`；`AppReplayTreeItem` 的 `IsExpanded` 由固定 `False` 改为 `{Binding Header.IsExpanded, RelativeSource=Self, Mode=TwoWay}`（容器 DataContext 是页面 VM，节点在 `Header` 上） |
| `ViewModels/TasksViewModel.cs` | `ReplayNodeLevel` 增加 `Player`（原来玩家节点被误标为 `Map`，图标错成 `Folder24`）；`Icon` 映射 `Player → Person24`；地图 / 玩家两级默认展开；`CloneFiltered` 修正：命中的节点复用原实例（原来记录行基例恒返回 `null`，过滤后树枝永远为空，且副本与 `Catalog` 脱钩导致勾选计数不到） |

### 验证（临时探针，跑完即删）

| 项 | 结果 |
|---|---|
| 层次容器 | 四层全部生成 `Map → Player → Track → Record`；非叶行 `hasItems=1`，记录行 `hasItems=0` |
| 勾选框 | 记录行 `checks=1/vis1/en1`（可见且可用）；非记录行 `vis0`（按设计不显示） |
| 展开绑定 | 每行 `tviExp == nodeExp`，`Header.IsExpanded` 双向绑定生效 |
| 勾选链路 | 勾一条 → `已选 1 条`；取消 → `尚未勾选` |
| 过滤链路 | `CatalogFilter='hades2'` → `view=1`、滤出 83 条记录；过滤态下勾选仍写回 `Catalog` |
| 清理 | `src/` 内 `TEMP-DIAG`/`RunTreeDiag`/`mmod-tree-diag` 残留 = 0；`TasksPage.xaml.cs` 恢复为 13 行原状 |
| 构建 / 冒烟 | `dotnet build -p:SkipNativeBuild=true` 0 错 0 警；TGA 落点冒烟 `title=Momentum 运动模糊合成`、`responding=True`、优雅关闭 |

### 未验证

- 真实界面观感（箭头、缩进、勾选框间距）需用户确认。
- 数据层存在同一条回放的重复条目（同一时长 + 同一记录时间多次出现，如 `0:11.894 · 05-16 13:17`），
  属扫描/去重问题，本次未处理。

---

## 追加修复 · 任务页「创建任务」无反应（2026-09-16，用户报「点这个创建任务没有任何反应」）

### 现象

勾选记录后点「创建任务」，界面毫无变化：没有提示、没有错误、队列计数仍是 `0`。

### 根因（两层）

1. **状态没有出口**：`TasksViewModel` 有 18 处写 `StatusText`（所有操作结果与异常消息都写在这里），
   但任务页 XAML 里唯一的 `StatusText` 绑定属于任务卡片模板的 `TaskListItem.StatusText`，
   ViewModel 的 `StatusText` **没有任何可视出口** → 失败原因被静默丢弃。
   这正是本文件 R1 差异表画板 04「④ 受控停止提示 · 现仅纯文本 `StatusText`，无 Severity 语义」待修项。
2. **真实失败点**：`CreateTasks` → `ValidateTaskSettings` 抛「TGA 监视目录未配置或不存在。」
   ——设置里 `ramDiskWatchDirectory = R:\` 当时未挂载。探针实测其余条件全部通过：
   `captureMode=Tga` ✓、`gameRoot exists=True` ✓、`output exists=True` ✓、`ramDisk='R:\' exists=False` ✗。

### 改动

| 文件 | 改动 |
|---|---|
| `ViewModels/TasksViewModel.cs` | 新增 `StatusSeverity` 与 `SetStatus(text, severity?)`：按真实文案分级（失败/错误/无法/不存在/未配置 → Error；请先/请至少/已取消/只有 → Warning；已创建/已删除/已刷新 → Success），异常等不可预测文本由调用方显式给 Error。18 处状态写入统一改走 `SetStatus`。`CreateTasks` 改为异步：成功后切到「执行队列」页签，失败时除状态条外再弹 `ContentDialog`（与 `VerifyReplay` 既有做法一致） |
| `Views/Pages/TasksPage.xaml` | 工具条与页签之间新增常驻 `ui:InfoBar`（`Severity` + `Message` + `IsOpen`），页面网格由 4 行扩为 5 行 |

未改动：`ValidateTaskSettings` 的校验时机与内容。「监视目录尚未挂载就入队」是否允许属契约变更，待用户决定。

### 验证（临时探针，跑完即删）

| 项 | 结果 |
|---|---|
| 状态条存在 | `infoBar found=True visibility=Visible` |
| 刷新可见 | 条上显示「已解析 153 条回放；可执行 84 条；旧版不兼容 69 条（已隐藏）；无法解析 0 条。」`severity=Informational` |
| 创建失败可见 | `vm.StatusText='TGA 监视目录未配置或不存在。'` `severity=Error`，同文本出现在 `ui:InfoBar` 上 |
| 弹层 | `contentDialogFound=True title='创建任务失败'` |
| 副作用 | `queue=0`（校验未通过，未写入任何任务记录） |
| 清理 | 源码内 `TEMP-DIAG` / `PhaseOne` / `RunCreateDiag` 残留 = 0；`TasksPage.xaml.cs` 恢复 13 行 |
| 构建 / 冒烟 | 0 错 0 警；TGA 落点冒烟 title / responding / 优雅关闭通过 |

### 未验证

- 真实界面观感（状态条高度对树区域的影响、弹框文案换行）需用户确认。
- 「挂上 RAM 盘后创建任务是否成功」未实测（会写入真实任务记录，留给用户实机确认）。

---

## 追加修复 · 设置页 NumberBox 输入框永久空白（2026-09-16，用户报「没办法赋值数字进显示」）

### 现象

设置页「超采样 N（1–64）」输入框是**空的**，怎么输入都留不下数字；`settings.json` 里的
`supersamplingMultiplier` 却一直有值（当时为 `2`）。

### 根因

`ui:NumberBox.Value` 的类型是 **可空 `double?`**（`NumberBox.cs` L40-52），而本页所有数值绑定源
都是**不可空 `int` / `double`**：

- `NumberBox.ValidateInput()` 在文本为空时执行 `SetCurrentValue(ValueProperty, null)`
  ——用户清空输入框后失焦（改数字时的常规动作）就会走到这里；
- 控件的 `Value` 变成 `null`，`UpdateTextToValue()` 随即把文本也置空 → **输入框永久空白**；
- 回写源时 `null → int` 转换失败，源保持旧值（`settings.json` 仍为 2），UI 与源从此永久不一致。

`NumberBoxValidationMode` 看似是解药，但在 WPF-UI 4.3.0 里**只注册了属性、没有任何实现**
（全文件搜索只有属性定义，无使用点）。

### 改动

| 文件 | 改动 |
|---|---|
| `Views/Pages/SettingsPage.xaml` | 5 个 `ui:NumberBox`（超采样 / Exposure / 磁盘安全下限 / OBS 并行路数 / 目标码率）各挂 `ValueChanged="OnNumberBoxValueChanged"` |
| `Views/Pages/SettingsPage.xaml.cs` | 新增 `OnNumberBoxValueChanged`：`NewValue` 为 `null` 时对该控件的 `Value` 绑定执行 `UpdateTarget()`，从源拉回有效值恢复显示 |

不改变业务语义：清空输入不会修改设置，只是把输入框恢复成当前有效值。

### 验证（临时探针，跑完即删）

| 项 | 结果 |
|---|---|
| 基线 | `value=7 text='7'`，绑定 `status=Active hasError=False` |
| 修复前（对照） | `setValueNull → value=<null> text=''`；`clear+moveFocus → value=<null> text=''`（**与用户截图一致**） |
| 清空后失焦 | `clear+blur → value=7 text='7'`，源保持 7 |
| 直接置空 | `setNull → value=7 text='7'` |
| 正常输入仍可提交 | `type7+blur → value=7 vm=7` |
| 事件确实到达 | 事件日志 `new=null src=NumberBox` 两条，与两次清空一一对应 |
| 副作用 | 探针期间的临时改动已按 SHA256 精确还原 `settings.json`（`f33faf8a…`，`supersamplingMultiplier=2`） |
| 清理 / 构建 | 源码探针残留 = 0，`MainWindow.DefaultPageFor` 已还原；0 错 0 警；冒烟通过 |

### 未验证

- 真实键盘输入（含中文输入法全角数字）路径未实测；规律是「解析不出数字 → 回填当前有效值」，
  由控件既有 `UpdateTextToValue` 负责，本次未改动。

---

## 追加变更 · 移除运行测试功能与追赶时间指标（2026-09-16，用户要求）

### 需求

- 任务页「回放操作」下拉里的「验证回放（Capture Envelope）」与「性能预检（10 秒吞吐窗口）」属于运行测试功能，
  连同相关代码一并移除（含选择条的「验证」按钮与页头「尚未执行性能预检」徽标）。
- 运行遥测里的「追赶时间」卡片不再需要，保留其余 4 项。

### 改动

| 文件 | 改动 |
|---|---|
| `Mmod.Core/Services/RenderTaskRunner.cs` | 删除 `VerifyReplayAsync`、`RunPerformancePreflightAsync`、私有诊断采集 `RunDiagnosticCaptureAsync`、`ShutdownVerifyGameAsync`、`CloneForVerify`；删除 `IsVerifying` / `IsPreflighting` 属性与 `_verifyGame` 字段；`StartAsync` 与 `DisposeAsync` 只保留 `IsRunning` 判断 |
| `Mmod.Core/Services/CaptureEnvelopeRecorder.cs` | 删除只服务于「验证回放」的 `VerifyActivityAsync`；正式链路的 `RecordAsync` 不变 |
| `Mmod.App/ViewModels/TasksViewModel.cs` | 删除 `VerifyReplay` / `RunPerformancePreflight` 命令、`_preflightByTask`、`PreflightText`、`HeaderPreflightText`、`FormatPreflight`、`CatchUpText`；`StartQueue` 去掉预检确认弹窗（直接启动队列）；模式切换安全门只剩 `IsRunning` 分支；`FormatRuntime` 去掉追赶时间 |
| `Mmod.App/Views/Pages/TasksPage.xaml` | 删除下拉里的两个 `MenuItem`、选择条「验证」按钮、页头预检 `InfoBadge`、「追赶时间」卡（`UniformGrid Columns` 5→4）；页头副标题改为「勾选回放 → 创建任务 → 队列执行 → 自动收尾合并」 |

**刻意保留**：`PerformancePreflightEvaluator` / `PerformancePreflightResult` / `PerformancePreflightRating`
（`Mmod.SmokeTest` 引用，删除会破坏测试项目）；`NodeExecutionStage.Preflight` 是录制状态机的阶段枚举，
与界面上的「性能预检」不是同一件事；`CaptureEnvelopeRecorder.RecordAsync` 属正式录制链路。

### 验证

| 项 | 结果 |
|---|---|
| App 层残留 | `Mmod.App` 内 `VerifyReplay` / `Preflight*` / `CatchUp*` / `IsVerifying` / `IsPreflighting` 引用 = 0 |
| 构建 | `Mmod.App` 0 错 0 警；整个 `MomentumBlur.slnx` 0 错 0 警（含 `Mmod.SmokeTest`） |
| 冒烟测试 | `dotnet run --project src/Mmod.SmokeTest -c Release` 通过：`OK size=10642 bytes progress=(30, 30)` |
| 应用冒烟 | `title=Momentum 运动模糊合成`、`responding=True`、优雅关闭 |
| 文档同步 | README 能力表移除「性能预检」行；运行诊断说明移除追赶时间与「真实性能预检」条目 |

### 未验证

- 删除后的任务页视觉（遥测 4 卡排布、页头副标题）需用户确认。
- 队列执行的实机行为本轮未重新录制验证；正式链路代码未改动，仅由冒烟测试覆盖。

---

## 追加变更 · 移除「回放操作」下拉，刷新按钮下移到搜索行（2026-09-16，用户要求）

用户反馈：工具条右侧的「回放操作」下拉内容多余，刷新应放到下方（回放记录的搜索行）单独放一个按钮。

| 文件 | 变更 |
|---|---|
| `Mmod.App/Views/Pages/TasksPage.xaml` | 删除工具条右侧的 `ui:DropDownButton「回放操作」`及其 `ContextMenu`（原含「刷新回放记录」「创建任务」两项）；在回放记录页签的搜索行新增「刷新」按钮（`ArrowSync24`，绑定 `RefreshCatalogCommand`），语义为重新扫描回放记录 |

保留与分工：

- 工具条右侧的「刷新快照」（`RefreshSnapshotCommand`，刷新**待执行任务**的设置快照）按用户要求**保留**，与「刷新回放记录」不是同一件事。
- 「创建任务」不再出现在下拉里，由回放记录页签底部蓝色选择条的常驻按钮承担（`CreateTasksCommand`），功能未丢失。
- `RefreshCatalog` 的状态回执原本就走 `SetStatus` → 页面 `ui:InfoBar`，按钮下移后反馈链路不变。

### 附带修复 · 搜索行计数文本溢出卡片外

同一截图暴露：`CatalogCountText`（「可执行 0 · 旧版 0」）被裁切溢出到卡片右侧之外。
原因是 `ui:AutoSuggestBox` 占 `*` 列但内部按内容测量（含 `Search24` 图标与占位文本），
在窗口较窄时把右侧 `Auto` 列挤出容器。修复：搜索框加 `MinWidth="0"` 使 `*` 列可被正常压缩，
计数文本加 `TextWrapping="NoWrap"`，并给刷新按钮预留独立列。

### 验证

| 项 | 结果 |
|---|---|
| 构建 | 整个 `MomentumBlur.slnx` 0 错 0 警 |
| 应用冒烟 | 启动正常、响应正常、优雅关闭 |
| 工具条 | 右侧仅剩「刷新快照」一个按钮 |
| 搜索行 | 「刷新」按钮存在，计数文本完整可见不溢出 |
| 命令可达性 | `RefreshCatalogCommand` / `CreateTasksCommand` 均仍被 XAML 引用（无孤儿命令） |




---

## 追加变更 · 移除「刷新快照」，冻结配置成为不可变契约（2026-09-16，用户要求）

用户明确要求：**「不需要有刷新快照的功能，这创建的任务就基于创建时候的配置就行了，不需要有任何变化，
暂停中断都不要重新读取」**。

这其实不是新增约束，而是**把既有行为固化为契约**：原设计已经是「创建时冻结配置」，
`RenderTaskRunner` 根本不持有 `SettingsViewModel`/`UserSettings` 依赖，执行只走
`Deserialize(task)` → `JsonSerializer.Deserialize<RenderSettingsSnapshot>(task.SettingsJson)`。
唯一能事后改写快照的入口就是「刷新快照」按钮，删除它之后该契约无法再被打破。

| 文件 | 变更 |
|---|---|
| `Mmod.App/ViewModels/TasksViewModel.cs` | 删除 `RefreshSnapshot` 命令；在 `CreateTasks` 写入快照处补充契约注释（说明这是全局设置写入任务的唯一时机，暂停/恢复/中断重启都只读 `settings_json`） |
| `Mmod.App/Views/Pages/TasksPage.xaml` | 删除工具条右侧「刷新快照」按钮；原两列 `Grid` 退化为单列，改用 `StackPanel`（右侧列已空，无内容可对齐） |
| `Mmod.Core/Services/RenderTaskRepository.cs` | 删除 `UpdatePendingTaskSettings` —— 删除命令后它成为死代码（唯一调用者即 `RefreshSnapshot`），且其存在意义就是「改写已冻结的快照」，与契约相悖 |

### 契约说明（供后续维护）

- **唯一写入口**：`TasksViewModel.CreateTasks`，任务创建时把 `_settings.Snapshot()` 序列化进 `render_tasks.settings_json`。
- **读入口**：`RenderTaskRunner.Deserialize(task)`，`RunQueueAsync` 取 `Pending`/`Paused`/`FailedNeedsAttention`
  三种状态的任务后逐一 `Deserialize`。因此**暂停后恢复、失败后重试、崩溃恢复重启，都重新拿到同一份冻结配置**。
- **删除入口**：曾经的 `UpdatePendingTaskSettings`（SQL `UPDATE render_tasks SET settings_json=...`）已删除，不要加回。
- 用户若要改配置，正确做法是**重新勾选回放记录创建新任务**，而不是修改旧任务。

2026-09-24 例外：任务页允许对 `Pending / Paused / FailedNeedsAttention` 任务单独修改
`ForegroundCaptureFpsLimit`。该字段只控制现实时间中的 TGA 生成速率，不改变输出帧率、
超采样时间步、运动模糊、画质处理或编码规格；更新使用带状态守卫的专用事务，已完成节点与
片段保持不变。禁止据此恢复“刷新整个任务快照”的入口。

### 验证

| 项 | 结果 |
|---|---|
| 残留引用 | `RefreshSnapshot` / `UpdatePendingTaskSettings` 在 `src/` 内引用 = 0（唯一命中是契约注释本身） |
| 构建 | 整个 `MomentumBlur.slnx` 0 错 0 警 |
| 冒烟测试 | `dotnet run --project src/Mmod.SmokeTest -c Release` 通过 |
| 运行时布局探针 | `SNAPSHOT-BUTTON-COUNT=0`（按钮彻底消失）；工具条只剩 `开始/继续 x=16 w=116.3`、`当前节点后暂停 x=138.3 w=144`、`立即停止 x=288.3 w=102`，全部左对齐 |
| 应用冒烟 | `title=Momentum 运动模糊合成`、`responding=True`、优雅关闭 |
| 样式可达性 | `AppToolbarButtonLast` 仍被「立即停止」使用，未成为孤儿样式 |
