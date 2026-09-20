# Mmod.App 设计稿（960×720 基线）

离线快照，供不打开设计文件时查阅。源文件为 Ardot 画布 `725691702565967`，
本目录 PNG 由画布 1:1 导出（scale=2）。

## 设计基线

| 项 | 值 | 对应代码 |
|---|---|---|
| 窗口 | 960 × 720 | `MainWindow.xaml` |
| 标题栏 | 40 | `ui:TitleBar` |
| 导航栏 | 176（Compact 44） | `ui:NavigationView` `OpenPaneLength` |
| 内容区 | 784 × 679 | 页面根 `Grid` |
| 内边距 | 16 | `Padding="16"` |
| 间距 | 14 | `StackPanel.Spacing` / `Grid` gap |
| 卡片圆角 / 描边 | 8 / 1px，无投影 | `ui:CardControl` + `BorderBrush` |
| 控件圆角 | 4 | `ui:Button` 等 |

主色 = 系统强调色（`SystemAccentColor` / `AccentFillColorDefaultBrush`），不自定义品牌色。
数字 / 路径 / 时间戳用 `JetBrains Mono`；中文用 `Noto Sans SC`。

## 画板清单

| 文件 | 画板 | 对应源码 |
|---|---|---|
| `00-overview-960x720.png` | 总览 · 设计原则与代码文件对照 | —（说明页） |
| `01-compose-tga-960x720.png` | 合成页 · 游戏 TGA 监视中 | `Views/Pages/ComposePage.xaml` |
| `02-compose-obs-batch-960x720.png` | 合成页 · OBS 批量 | `Views/Pages/ComposePage.xaml` |
| `03-tasks-tabview-telemetry-960x720.png` | 任务页 · 三页签 + 底部遥测条 | `Views/Pages/TasksPage.xaml` |
| `04-interaction-states-960x720.png` | 交互状态与反馈 | 全局（`Services/DialogService.cs`） |
| `05-settings-capture-960x720.png` | 设置页 · 捕获与合成 / 输出与编码 | `Views/Pages/SettingsPage.xaml` |
| `06-settings-mode-security-960x720.png` | 设置页 · 模式与安全 | `Views/Pages/SettingsPage.xaml` |
| `07-components-spec-960x720.png` | 组件与状态规范 | `Styles/AppResources.xaml` |

## 与代码一致的关键决策

1. **任务页**：`ui:TabView` 三页签（回放记录 / 执行队列 / 任务详情）+ **底部常驻遥测条**。
   不使用三栏并排（窄窗口下三栏不可读）。
2. **设置页**：**横向六页签**（捕获与合成 / 画质处理 / 后期·4K / 游戏 TGA / OBS 模式 / 磁盘与安全）。
   不使用左侧子导航（会与主 `NavigationView` 形成双重导航）。
3. **排版口径**：按 `FontTypography` 语义层级描述（TitleLarge / Title / BodyStrong / Body / Caption），
   而非固定字号数值。
4. **零自绘**：全部使用 WPF-UI 现成组件；颜色只引 `DynamicResource` 主题资源键。

## 组件映射（WPF-UI 4.3.0）

| 设计元素 | 组件 | 备注 |
|---|---|---|
| 卡片 | `ui:CardControl` / `ui:Card` | `CardControl` 只能一个子元素；`Card` 无 `CornerRadius` |
| 状态徽标 | `ui:InfoBadge` | 只有 `Severity` / `Value` / `CornerRadius` / `Icon` |
| 消息条 | `ui:InfoBar` | `Title` / `Message` / `Severity` / `IsClosable` |
| 轻量通知 | `ui:Snackbar` | 配 `ui:SnackbarPresenter` |
| 确认弹层 | `ui:ContentDialog` | 必配 `ui:ContentDialogHost`，经 `DialogService` 调用 |
| 页签 | `ui:TabView` / `ui:TabViewItem` | 空派生类，`SelectedIndex` 可当切换用 |
| 分段器 | `ui:Button` + `Appearance` | 框架无分段控件 |
| 搜索框 | `ui:AutoSuggestBox` | `Text` 必须显式 `Mode=TwoWay` |
| 数字输入 | `ui:NumberBox` | `Minimum` / `Maximum` / `SmallChange` |
| 下拉按钮 | `ui:DropDownButton` | `Flyout` 接受 `ContextMenu` |
| 分隔线 | `Border Style="{StaticResource AppDivider}"` | 原生 `Separator` 在垂直 `StackPanel` 中不可见 |
| 列表 | `ui:ListView` / `ui:ListViewItem` | 无带键 Default 样式，勿用 `Style` 引用 |
| 树 | `TreeView`（原生）/ `ui:TreeViewItem` | **不存在** `ui:TreeView` |
| 复选框 / 下拉框 / 滑块 / 进度条 | 原生标签 | 框架只提供无键隐式样式 |

> ⚠️ 自定义 `Style` 引用框架控件时**必须** `BasedOn` 框架 Default 样式
> （如 `DefaultUiButtonStyle`），否则整块覆盖 `ControlTemplate` 导致退化成原生外观。

## 重新导出

设计文件：Ardot `725691702565967`。修改后重新导出对应画板 PNG 覆盖本目录即可。
