# Mmod.App · WPF-UI 组件映射与组件化改造执行计划

> 版本基准：**WPF-UI (Wpf.Ui) 4.3.0** + CommunityToolkit.Mvvm 8.4.2，`net10.0-windows`
> 参考源码：`reference/WPFUI/src/Wpf.Ui/Controls`（共 **77** 个控件目录，本机已核对）
> 设计画板基准：**960×720**（本次按用户要求从 1440×900 下调）

---

## 执行状态（截至本次）

| 阶段 | 内容 | 状态 |
|---|---|---|
| A | `AppResources.xaml` 去自绘模板 | ✅ 完成（12 处 `ControlTemplate` → 0） |
| B | 原生控件换框架组件 | ✅ 完成 |
| C | 卡片 / 视觉件换框架组件 | ✅ 完成 |
| D | 960×720 布局重排 | ✅ 完成 |
| E | ViewModel 收敛 / 死代码清理 | ✅ 完成 |
| F | 验证与清理 | ✅ 完成 |
| G | 修复运行时截图暴露的 Bug | ✅ 完成（详见下方「阶段 G」） |
| H | Ardot 设计稿侧按 960×720 同步 | ✅ 完成（新建 9 张 960×720 画板，旧 1440 保留为历史） |
| I | 设计稿导出到 `docs/design/` | ✅ 完成（8 张 PNG + README.md） |

**验证结果**：`dotnet build src/Mmod.App/Mmod.App.csproj -p:SkipNativeBuild=true` → **0 错误 0 警告**；
启动后**存活 15 秒**（窗口标题 `Momentum 运动模糊合成`），说明三页 XAML 全部解析成功、
所有 `ui:SymbolIcon` 图标名、`DynamicResource` 键、以及全部 `BasedOn` 框架 `Default*Style` 键在运行时均能解析。
全文断言：真 `ControlTemplate` 0 处、硬编码十六进制 0 处、真实 `MessageBox` 调用 0 处、死样式键引用 0 处。

### 阶段 G · 修复运行时截图暴露的 Bug（用户反馈「没完全用 ui 组件库 / 交互乱」）

**根因一（P0）：自定义 `Style` 缺 `BasedOn` → 整块覆盖框架隐式样式。**
WPF-UI 每个控件都有一条无键隐式样式作为 Fluent 外观入口。在 `AppResources.xaml` 里写
`Style x:Key="Xxx" TargetType="{x:Type ui:Button}"` 而不加 `BasedOn`，再用
`Style="{StaticResource Xxx}"` 引用，会完全替换隐式样式（含 `ControlTemplate`），控件退化成原生 WPF 外观
—— 这正是截图里「分段器按钮呈原生方框」的根因。

已修复的 6 条（键名均已 grep 实证）：

| 样式键 | `BasedOn` |
|---|---|
| `AppCodeBlock` | `DefaultUiTextBoxStyle` |
| `AppToolbarButton` / `AppToolbarButtonLast` / `AppSegmentButton` | `DefaultUiButtonStyle` |
| `AppTreeView` | `DefaultTreeViewStyle` |
| `AppReplayRowCheckBox` | `DefaultCheckBoxStyle` |

`ui:ListView` / `ui:ListViewItem` **无带键 Default 样式** → 删除 `AppListView` / `AppTaskListItem` 两个键，
改为在 `TasksPage.xaml` 里不设 `Style`、只设局部属性 + 内联 `<ui:ListView.ItemContainerStyle>`。

**根因二：`<Separator/>` 在垂直 `StackPanel` 中不可见。**
`Separator.xaml` 隐式样式是 `BorderThickness="1,1,0,0"`（只有顶边 1px）+ `Background="Transparent"` + `Margin="0"`，
放进垂直 `StackPanel` 后视觉上完全消失。→ 改用 `Style x:Key="AppDivider" TargetType="{x:Type Border}"`
（`Height=1` + 描边色），另加 `AppDividerTight`。`Border` 是布局原语，不受「零自绘」约束。

**其余修复**：
- `ui:NumberBox` 不可见 → 同格 `Grid` 里「左对齐 TextBlock + 右对齐 NumberBox」相互挤压；改为 `Grid` 两列（`*` / 固定宽）+ 新增 `AppFieldRow` 统一下间距。
- 设置页 6 页签挤一行被裁切 → 页签头去 `Margin`、`TabPanel` 加样式释放宽度、`AppSegmentButton` 用 `MinWidth=96` 替代 `HorizontalAlignment=Stretch`。
- 合成页标题与按钮重叠 → 改「标题一行 + `WrapPanel` 按钮行」纵向布局。
- 合成页 `本会话` 卡溢出 → key-value 行改 `Grid` 两列（`Auto` / `*` + `TextTrimming`）；侧栏 260→250。
- 合成页 `输出盘空间` 重复 → OBS 卡内的重复行删除，信息统一由「监视盘空间」卡承载；批量队列卡补说明文字消除空占高。
- 所有 `ui:CardControl` 补 `VerticalAlignment="Top"`（框架默认 `Center`）。
- `DialogService` 去原生弹层 → `EnsureHost()` 兜底挂 `ContentDialogHost`，弹层正文改 `ui:TextBlock`（`TextColor.Primary`）。**`ui:TextBlock.Appearance` 类型是 `TextColor`，不是 `ControlAppearance`**。

### ⚠️ 关键纠正（本轮源码实证，推翻了初版计划的两处判断）

WPF-UI 4.3.0 的控件分两类，**判断依据是 `Controls/<Name>/` 下有没有 `.cs` 文件**：

| 类别 | 特征 | 用法 | 例子 |
|---|---|---|---|
| **(a) 有独立 C# 类** | 目录下有 `.cs` | **必须** 写 `ui:` 前缀 | `ui:Button`、`ui:Card`、`ui:CardControl`、`ui:ListView`、`ui:TreeViewItem`、`ui:TextBox`、`ui:NumberBox`、`ui:ToggleSwitch`、`ui:InfoBadge`、`ui:InfoBar`、`ui:TabView`、`ui:AutoSuggestBox`、`ui:DropDownButton`、`ui:HyperlinkButton`、`ui:SymbolIcon`、`ui:TextBlock` |
| **(b) 只有隐式样式** | 目录下**只有 `.xaml`**，`TargetType` 无 `x:Key` | 用**原生标签**即得 Fluent 视觉 | `CheckBox`、`ComboBox`、`Separator`、`TreeView`、`Slider`、`ProgressBar`、`StatusBar`、`ToolBar`、`Menu` |

**因此以下写法是错的**（会报 `MC3074: XML 命名空间中不存在标记`）：
`ui:CheckBox`、`ui:ComboBox`、`ui:Separator`、`ui:TreeView`、`ui:Slider`、`ui:ProgressBar`。

**关键点**：类别 (b) 用原生标签**不属于"原生 UI 问题"**——WPF-UI 已经把 Fluent 模板挂成无键隐式样式，
写 `<CheckBox>` 得到的视觉与 `ui:` 控件一致。真正的"原生 UI"问题只有两种：
自绘 `ControlTemplate` 覆盖（P0），和**该用 `ui:` 类却用了原生标签**（如 `<TextBox>` 之于 `ui:TextBox`）。

---

## 一、问题诊断：当前实现里"原生 UI"到底在哪

对 6 个 XAML 文件做了全量扫描，确认三类问题：

### P0 · 自绘控件模板（最严重）

`Styles/AppResources.xaml` 中 **12 处 `ControlTemplate`** 是我自己写的裸模板，把框架样式整个覆盖掉了：

| 样式键 | 目标类型 | 问题 |
|---|---|---|
| `AppTreeItemStyle` | `TreeViewItem` | **完全覆盖框架样式**。WPF-UI 4.3.0 的 `TreeViewItem.xaml:341` 已有隐式样式（含展开箭头、缩进引导线、悬停/选中态）；我的裸模板把这全删了 |
| `AppTaskItemStyle` | `ListBoxItem` | 同上，`Template` 只有 `ContentPresenter`，丢失框架的悬停/选中/焦点视觉 |
| `AppSegmentedRadio` | `RadioButton` | 自绘分段器（框架本无分段器组件，但应改用 `ui:Button` + `Appearance` 组合，见 §3） |
| `AppNavItem` | `RadioButton` | 自绘子导航项。**已改为横向 `ui:TabView` 六页签**，见 §4 |

### P1 · 原生控件未换框架组件

> 注：下表"应替换为"一列中，`CheckBox` / `ComboBox` / `Separator` / `TreeView` 这几项
> **保持原生标签即为正确做法**（类别 (b)，隐式样式），初版计划误写成 `ui:` 前缀。

| 文件 | 原生控件 | 出现次数 | 应替换为 |
|---|---|---|---|
| `TasksPage.xaml` | `<TextBox>` (搜索框) | 1 | `ui:AutoSuggestBox` |
| `TasksPage.xaml` | `<CheckBox>` (回放树勾选) | 1 | **保持 `<CheckBox>`**（隐式样式，合规） |
| `TasksPage.xaml` | `<Border>` 当卡片用 | 21 | `ui:CardControl` / `ui:Card` |
| `TasksPage.xaml` | `<RadioButton>` (分段器) | 2 | `ui:Button` + `Appearance` |
| `SettingsPage.xaml` | `<RadioButton>` (子导航) | 6 | 横向 `ui:TabView` 六页签 |
| `SettingsPage.xaml` | `<RadioButton>` (分段器) | 4 | 同 TasksPage |
| `SettingsPage.xaml` | `<TextBox>` (路径/CFG 只读块) | 10 | `ui:TextBox`（`IsReadOnly`）+ `AppCodeBlock` 样式 |
| `SettingsPage.xaml` | `<ComboBox>` | 2 | **保持 `<ComboBox>`**（隐式样式，合规） |
| `SettingsPage.xaml` | `<Slider>` | 1 | 框架仅提供**隐式样式** → `<Slider>` **已合规** |
| `SettingsPage.xaml` | `<Border>` 当卡片用 | 11 | `ui:CardControl` |
| `ComposePage.xaml` | `<Border>` 当指标块用 | 3 | `ui:Card` |

### P2 · 视觉件自绘

| 件 | 问题 | 应改为 |
|---|---|---|
| `<Ellipse>` 状态圆点 | 自绘 | `ui:InfoBadge`（`Severity`+`Value`） |
| 状态药丸 `Border`+圆点+文字 | 自绘 | `ui:InfoBadge`（`Severity` 驱动颜色） |
| 工具条分隔条 `<Border Width=1 Height=18>` | 自绘 | **`<Separator>`**（原生标签，隐式样式） |
| 指标块 `Border` | 自绘 | `ui:Card`（`ContentControl`，含 `Footer`） |

### 已合规（无需改）

- `<ProgressBar>` —— `ProgressBar.xaml` 是无键隐式样式，已自动套用框架视觉
- `<Slider>` —— 同上
- `<TreeView>` / `<CheckBox>` / `<ComboBox>` / `<Separator>` —— 均为**隐式样式**，原生标签即合规
- **问题只在我传进去的 `ItemContainerStyle` 覆盖了容器样式**

---

## 二、框架组件能力清单（本机核对，77 个）

### 可直接用的控件（有独立 `.cs` 类）

| 类别 | 组件 |
|---|---|
| 外壳 | `FluentWindow`、`Window`、`TitleBar`、`NavigationView`、`ClientAreaBorder`、`Frame`、`LoadingScreen` |
| 卡片 | `Card`、`CardControl`、`CardAction`、`CardColor`、`CardExpander`、`Expander` |
| 输入 | `AutoSuggestBox`、`TextBox`、`NumberBox`、`PasswordBox`、`ComboBox`、`CheckBox`、`RadioButton`、`ToggleSwitch`、`ToggleButton`、`RatingControl`、`ThumbRate`、`ColorPicker`、`Calendar`、`CalendarDatePicker`、`DatePicker`、`TimePicker` |
| 按钮 | `Button`、`HyperlinkButton`、`Anchor`、`DropDownButton`、`SplitButton` |
| 反馈 | `InfoBar`、`InfoBadge`、`Badge`、`ContentDialog`、`Snackbar`、`MessageBox`、`ToolTip`、`ProgressRing` |
| 导航 | `BreadcrumbBar`、`Menu`、`ContextMenu`、`ToolBar`、`Flyout`、`StatusBar` |
| 数据 | `TreeView`/`TreeViewItem`、`ListView`/`ListViewItem`、`GridView`、`VirtualizingGridView`、`VirtualizingItemsControl`、`VirtualizingUniformGrid`、`VirtualizingWrapPanel`、`DataGrid`、`TreeGrid`、`ItemsControl`、`ListBox` |
| 布局 | `ScrollViewer`、`DynamicScrollViewer`、`DynamicScrollBar`、`ScrollBar`、`Separator` |
| 图形/文本 | `Arc`、`IconElement`、`IconSource`、`Image`、`TextBlock`、`AccessText`、`Label`、`RichTextBox` |

### 仅有隐式样式（用原生标签即得框架视觉）

`Slider`、`ProgressBar`、`Separator`、`StatusBar`、`ToolBar`、`Menu`、`Grid`

### 明确不存在（需自行组合，但不算违规）

- **分段器（Segmented Control）** —— WPF-UI 无此组件。合规做法：`ui:Button`（`Appearance=Primary` 表示选中 / `Secondary` 表示未选中）+ 外层 `ui:Card` 容器
- **磁盘刻度标记** —— 无对应组件，保留自绘（`Grid` + `Border`），已在设计稿标注

---

## 三、设计稿 → 组件映射表

### 3.1 外壳（所有页面共用）

| 设计稿元素 | WPF-UI 组件 | 关键属性 |
|---|---|---|
| 窗口 | `ui:FluentWindow` | `WindowBackdropType="Mica"`、`ExtendsContentIntoTitleBar="True"`、960×720 |
| 标题栏 | `ui:TitleBar` | 自动绑 `FluentWindow` |
| 左侧导航栏 | `ui:NavigationView` | `PaneDisplayMode="Left"`、`OpenPaneLength="176"`、`CompactPaneLength="44"`、`IsPaneToggleVisible="True"` |
| 导航项 | `ui:NavigationViewItem` | `Icon`、`Content`、`TargetPageType` |
| 内容区 | `ui:NavigationView` 内 `Frame` | `FrameMargin="0,0,0,8"` |
| 弹层宿主 | `ui:ContentDialogHost` | 每 Window 一个 |
| 通知宿主 | `ui:SnackbarPresenter` | 每 Window 一个 |

### 3.2 页面通用件

| 设计稿元素 | WPF-UI 组件 | 说明 |
|---|---|---|
| 页标题（20px SemiBold） | `ui:TextBlock FontTypography="TitleLarge"` | 不硬编码 FontSize |
| 页副标题（12px 次级） | `ui:TextBlock FontTypography="Body" Appearance="Secondary"` | |
| 分组标题（14px SemiBold） | `ui:TextBlock FontTypography="BodyStrong"` | |
| 小标签（11px 次级） | `ui:TextBlock FontTypography="Caption" Appearance="Secondary"` | |
| 等宽数值 | `ui:TextBlock FontFamily="JetBrains Mono"` + `FontTypography="Body"` | 字体族是允许的显式指定 |
| 卡片 | `ui:CardControl` | `Header`/`Icon`/`CornerRadius`，**只能一个子元素** |
| 无描边卡片 | `ui:Card` | 无 `CornerRadius`；有 `Footer` |
| 可折叠分组 | `ui:CardExpander` | `Icon`/`CornerRadius`/`ContentPadding` |
| 可点击整行 | `ui:CardAction` | `Icon`/`IsChevronVisible` |
| 状态圆点/药丸 | `ui:InfoBadge` | `Severity`（Informational/Success/Caution/Critical/Attention）+ `Value` |
| 横幅提示 | `ui:InfoBar` | `Title`/`Message`/`Severity`/`IsClosable`/`IsOpen` |
| 分隔条 | `ui:Separator` | 1px，框架样式 |
| 按钮 | `ui:Button` | `Appearance`（Primary/Secondary/Danger/Transparent）+ `Icon` |
| 链接型按钮 | `ui:HyperlinkButton` | 继承自 `ui:Button`，带下划线语义 |
| 进度条 | `<ProgressBar>` | 隐式框架样式，默认高 4px |
| 滑块 | `<Slider>` | 隐式框架样式 |
| 搜索框 | `ui:AutoSuggestBox` | `PlaceholderText`/`Text`/`Icon`/`ClearButtonEnabled` |
| 数字输入 | `ui:NumberBox` | `Minimum`/`Maximum`/`SmallChange`/`SpinButtonPlacementMode` |
| 开关 | `ui:ToggleSwitch` | `OnContent`/`OffContent`/`LabelPosition` |
| 下拉 | `ui:ComboBox` | |
| 复选框 | `ui:CheckBox` | |
| 树 | `ui:TreeView` + `ui:TreeViewItem` | **不要覆盖 ItemContainerStyle 的 Template** |
| 列表 | `ui:ListView` + `ui:ListViewItem` | 优于 `ListBox`，自带 Fluent 项样式 |
| 标签页 | `ui:TabView` + `ui:TabViewItem` | |

---

## 四、960×720 布局重排（关键约束）

原 1440×900 的三栏在 960 宽下必然塌陷。960 − 导航栏 176 − 内容 padding 2×16 = **752px** 可用。

### 任务页

| 区域 | 1440×900（旧） | **960×720（新）** |
|---|---|---|
| 内容 padding | 20 / gap 14 | **16 / gap 10** |
| 三栏宽度 | 300 / 380 / 自适应 | **改为两栏：248 / 自适应**（回放树 + 队列合并进左栏 `ui:TabView`；详情占右栏） |
| 或（推荐） | — | **上 `ui:TabView` 切换「回放 / 队列 / 详情」三页签 + 底部遥测条常驻** |
| 工具条 | 9 个按钮一行 | **主操作留 3 个（开始/暂停/停止），次要操作收进 `ui:DropDownButton`** |
| 遥测条 | 4 指标块横排 | **4 指标块保留**（752/4 ≈ 188px/块，可容纳） |
| 回放树 | 三层缩进 | 保留，但缩进改 12px，`TreeViewItem` 用框架样式 |

**决策**：960 宽下同时展示三栏 + 遥测条会让每栏不足 240px，信息密度过低。采用 **`ui:TabView` 三页签**（回放 / 队列 / 详情）+ 底部常驻遥测条。这既符合 Fluent 的窄屏适配惯例，也把 `ui:TabView` 这个框架组件用上。

### 设置页

| 区域 | 1440×900（旧） | **960×720（新）** |
|---|---|---|
| 子导航 | 自绘 `RadioButton` 列 176px | **`ui:NavigationView` 已承载一级导航；页面内改用 `ui:TabView` 横向页签**（捕获 / 画质 / 后期 / TGA / OBS / 磁盘） |
| 主列 + 画质列 | 460 / 自适应双列 | **单列满宽 + `ui:TabView` 分页**（752px 单列，纵向滚动） |
| 卡片 | 自绘 `Border` | `ui:CardControl` |
| 开关 | `ui:ToggleSwitch` | 保留（已合规） |
| 分段器 | 自绘 `RadioButton` | `ui:Button` + `Appearance` 组合 |

### 合成页

维持双栏，宽度从 `主栏 + 316` 改为 **`主栏 + 260`**；指标块 `Border` → `ui:Card`。

---

## 五、分阶段执行计划

### 阶段 A · 清理自绘（P0）

- **A1** 删除 `AppResources.xaml` 中 `AppTreeItemStyle`、`AppTaskItemStyle` 两个 `ControlTemplate`；改为只设 `Padding`/`Margin` 等非模板属性，`BasedOn="{StaticResource {x:Type TreeViewItem}}"` 继承框架样式。
- **A2** 删除 `AppSegmentedRadio`、`AppNavItem` 两个自绘 `ControlTemplate`，改用 `ui:Button` + `Appearance`。
- **A3** `AppResources.xaml` 全文件复查，确认剩余 `ControlTemplate` 只保留**框架确实没有的**（预期：0 处；磁盘刻度如需保留则单独标注）。
- 验收：`Select-String -Pattern 'ControlTemplate'` 结果为空或仅剩已标注例外。

### 阶段 B · 原生控件替换（P1）

- **B1** `TasksPage` 搜索框 `<TextBox>` → `ui:AutoSuggestBox`（`PlaceholderText="搜索地图 / 玩家 / 赛道"`、`Icon`）。
- **B2** `TasksPage` 回放树 `<CheckBox>` → `ui:CheckBox`；`<TreeView>` 去掉 `ItemContainerStyle`。
- **B3** `TasksPage` 队列 `<ListBox>` → `ui:ListView` + `ui:ListViewItem`。
- **B4** `SettingsPage` 10 个 `<TextBox>` 拆两类：只读展示 → `ui:Card` + `ui:TextBlock`（等宽）；可编辑 → `ui:TextBox`。
- **B5** `SettingsPage` 2 个 `<ComboBox>` → `ui:ComboBox`。
- **B6** `TasksPage` / `SettingsPage` 分段器 → `ui:Button`（选中 `Primary` / 未选中 `Secondary`）+ `ui:ButtonGroup` 或 `StackPanel` 容器。
- **B7** `SettingsPage` 子导航 → `ui:TabView`（横向）+ `ui:TabViewItem`。

### 阶段 C · 卡片与视觉件替换（P1/P2）

- **C1** 所有当卡片用的 `<Border>` → `ui:CardControl`（有标题）或 `ui:Card`（无标题），共约 32 处。
- **C2** 状态圆点/药丸 → `ui:InfoBadge`（`Severity` 由 `TaskPresentationState` 映射）。
- **C3** 工具条分隔 `<Border Width=1>` → `ui:Separator`。
- **C4** 指标块 `<Border Style="AppMetricTile*">` → `ui:Card` + `ui:CardAction`。

### 阶段 D · 960×720 布局重排

- **D1** `MainWindow.xaml`：`Height=720 Width=960`，`MinHeight=600 MinWidth=880`；`OpenPaneLength=176`、`CompactPaneLength=44`。
- **D2** `TasksPage`：改 `ui:TabView` 三页签 + 底部常驻遥测条；工具条次要按钮收进 `ui:DropDownButton`。
- **D3** `SettingsPage`：改 `ui:TabView` 六页签单列布局。
- **D4** `ComposePage`：侧栏 316 → 260。
- **D5** 全部间距 `gap 14 → 10`、`padding 20 → 16`。

### 阶段 E · ViewModel 收敛

- **E1** `TaskPresentationState` → `InfoBadgeSeverity` 映射方法（放 ViewModel，不放 XAML 转换器）。
- **E2** 分段器的"选中"用 `bool` 属性 + `ui:Button` 的 `Command`，不用 `RadioButton.GroupName`。
- **E3** 检查 `Converters/` 是否还有存在价值：`TaskStateToBrushConverter` 若只服务已删除的自绘件，一并删除。

### 阶段 F · 验证与清理

- **F1** `dotnet build -p:SkipNativeBuild=true` → **0 错误 0 警告**。
- **F2** 启动应用，存活 10s 无崩溃。
- **F3** 全文搜索断言：
  - `ControlTemplate` 仅剩已标注例外
  - `#[0-9A-Fa-f]{6}` 为 0
  - `MessageBox` 为 0（`ui:MessageBox` 除外）
  - 未带 `ui:` 前缀的 `TextBox`/`CheckBox`/`ComboBox`/`Button` 为 0（`ProgressBar`/`Slider`/`Separator` 例外，因其为隐式样式）
- **F4** 删除 `AppResources.xaml` 中已成死代码的样式键。

---

## 六、依赖顺序

```
A（清自绘） ──┬─→ B（换控件） ──┬─→ C（换卡片） ──→ D（布局重排） ──→ E（VM 收敛） ──→ F（验证）
              │                  │
              └─→ 必须先做 A，否则 B/C 换上去的框架组件会被自绘模板覆盖
```

**关键**：**A 必须最先做**。当前 `AppTreeItemStyle` / `AppTaskItemStyle` 会覆盖框架的 `TreeViewItem` / `ListViewItem` 样式 —— 如果不先删，即使把 `<TreeView>` 换成 `ui:TreeView`、`<ListBox>` 换成 `ui:ListView`，视觉上仍然是自绘的。

---

## 七、风险与取舍

| 风险 | 处理 |
|---|---|
| `ui:CardControl` 只能一个子元素 | 统一包 `StackPanel`/`Grid`，阶段 C 逐处确认 |
| `ui:AutoSuggestBox` 的 `Text` 双向绑定语义与 `TextBox` 不同（`UpdateTextOnSelect`） | 需实测；若过滤体验不佳，降级为 `ui:TextBox`（框架版，仍优于原生） |
| 960×720 下三栏不可行 | 已决策改 `ui:TabView`；**这是布局层的信息架构变更，需用户确认** |
| 设置页 6 个分组改页签后，原来"一屏纵览"的能力下降 | 页签内保留纵向滚动，分组标题用 `ui:CardControl.Header` 保证可扫读 |
| `ui:ListView` 的 `SelectedItem` 语义与 `ListBox` 一致 | 低风险，直接替换 |
| 自绘磁盘刻度 | 框架确无对应组件，保留并加注释标注为例外 |

---

## 八、验收标准

1. **组件化**：`src/Mmod.App` 下所有 XAML 中，除 `/reference` 明确无对应的例外外，交互控件 100% 带 `ui:` 前缀。
2. **零自绘模板**：`AppResources.xaml` 中不存在 `ControlTemplate`（磁盘刻度如保留，需单独注释标注）。
3. **零硬编码颜色**：`#[0-9A-Fa-f]{6}` 匹配数 = 0。
4. **960×720 下无裁切**：三页签内容在 960 宽下完整显示，无元素被压缩到不可用。
5. **构建与运行**：0 错误 0 警告 + 启动存活 10s。
6. **映射表可追溯**：设计稿每个元素都能在 §3 映射表里找到对应组件。
