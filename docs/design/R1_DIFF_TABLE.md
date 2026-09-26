# R1 逐画板差异表（执行过程证据）

基线：仓库 `main` / `5d94430`，工作区含未提交 UI 改动（执行前已 `git diff` 备份到 `.workbuddy/backup-r1/uncommitted-ui-changes.patch`）。
设计目标：`docs/design/README.md` + 8 张 PNG（960×720）。组件事实：`reference/WPFUI/src/Wpf.Ui/`（4.3.0）。

状态口径：**完成** / **合理偏差**（含理由）/ **未验证**（含原因）。

---

## 画板 00 · 总览与设计原则

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 窗口 960×720 | `MainWindow.xaml` Height/Width | `ui:FluentWindow` | — | 完成 |
| 标题栏 40 | `MainWindow.xaml` `ui:TitleBar` | `ui:TitleBar` | — | 完成 |
| 导航 176 / Compact 44 | `OpenPaneLength`/`CompactPaneLength` | `ui:NavigationView` | 展开/收起 | 完成 |
| 内边距 16 / 间距 14 | `AppPagePadding` = `16,12,16,12` | `Thickness` 资源 | — | 合理偏差：垂直 12 而非 16，960×679 内容高度下 12 才能容纳任务页「三页签 + 常驻遥测条」而不裁切 |
| 卡片圆角 8 / 描边 1px / 无投影 | `ui:CardControl CornerRadius="8"`、`ui:Card` | `ui:CardControl` / `ui:Card` | — | 完成 |
| 控件圆角 4 | 框架 `ControlCornerRadius` | 主题资源 | — | 完成 |
| 主色 = 系统强调色 | 全部 `AccentFillColor*` / `AccentTextFillColor*` | 主题资源 | — | 完成 |
| 排版语义层级 | `Styles/AppResources.xaml` 各 `FontTypography` 样式 | `ui:TextBlock` | — | 完成 |

## 画板 01 · 合成页 TGA 监视

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 窗口标题「Momentum 运动模糊合成」 | `MainWindow.xaml` `Title` | `ProjectConstants.ApplicationDisplayName` | — | 完成（真实常量，非稿内文案） |
| 左主栏「TGA 监视」卡 | `ComposePage.xaml` `MonitorTitle` | `ui:CardControl` | — | 完成 |
| 卡头右侧「监视中」徽标 | `MonitorSeverity`/`MonitorStateText` | `ui:InfoBadge` | Informational/Critical | 完成 |
| 监视目录 / 本次文件 键值行 | `WatchDirectoryText`、`DiskSpaceText` | `Grid` 两列 | — | 完成 |
| 已合成 84/128 + 65.6% 进度 | `FedCountText`/`PendingCountText`、`DiskUsedPercent` | `ProgressBar`（隐式样式） | OneWay 真实数据 | 完成 |
| 主操作按钮「开始合成 / 停止 / 清空」 | `StartTgaCommand`/`StopTgaCommand` Legacy | `ui:Button` | `CanExecute` 禁用态 | 合理偏差：实为「开始监视 / 停止并收尾」两项 + 无「清空」——保留现有命令语义，不为匹配稿面新增不存在的命令 |
| 右栏「本会话」卡 | `本会话` Grid 键值 | `ui:CardControl` | — | 完成 |
| 右栏「监视盘空间」卡 + 百分比 | `DiskUsedPercent`、`DiskDetailText` | `ui:CardControl` + `ProgressBar` | 安全线配色 | 完成 |
| 右栏「快速操作」：打开输出目录 / 复制路径 | `OpenOutputFolderCommand` | `ui:Button` | — | 合理偏差：仅保留 `OpenOutputFolderCommand`（唯一真实存在）；不新增「复制路径」命令 |
| 运行日志区 | `TgaMetricsText` 原为整段文本 | `ui:InfoBar`（运行消息） | — | 合理偏差：真实数据源只有单条 `StatusText`，不是结构化日志流，故用 `ui:InfoBar` 而非伪造多行日志列表 |
| 三指标块（已喂入 / 待处理 / 监视盘已用） | `UniformGrid Columns=3` | `ui:Card` ×3 | — | 完成 |

## 画板 02 · 合成页 OBS 批量

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 「OBS 批量合成」标题 + 工具条 | `ComposePage.xaml` L153-175 | `ui:TextBlock` + `WrapPanel` | 换行不叠压 | 完成 |
| 添加视频… / 开始批量合成 / 取消 / 清空队列 | 4 个 Command | `ui:Button`（Primary/Secondary/Danger） | `CanExecute` | 完成 |
| 拖放区「把 MP4/MKV 拖到这里」 | `AllowDrop` + `Page_Drop`/`Page_DragOver` | `Border` + `ui:SymbolIcon` | DragOver 高亮 | **待修**：当前拖放到页级生效但无可见落区提示；需补一个明确的拖放落区容器 |
| 文件表格：文件 / 大小 / 状态 | `DataGrid`（原生） | **`ui:DataGrid`** | 行选中 `ListViewItemBackgroundPointerOver` | **待修**：原生 `DataGrid` 未指定样式，`DefaultDataGridStyle` 是**带键**样式；应改用 `ui:DataGrid`（其隐式样式 BasedOn `DefaultUiDataGridStyle`），否则不保证 Fluent 视觉 |
| 底部「共 N 个 · 合计 X GB」 | `BatchSummary` | `ui:TextBlock` | — | **待修**：现文案为「队列：N 个文件，已选 M 个」，与稿面不同但为真实数据；保留真实统计，补足数量与体量呈现 |
| 「已完成 / 合成中 62% / 排队中」状态 | `BatchVideoItem.Status`/`ProgressPercent` | `ui:DataGrid` 列 | — | **待修**：`Status` 为字符串列，无法表达徽标语义；改为 `DataGridTemplateColumn` + `ui:InfoBadge` 需状态→Severity 转换器 |

## 画板 03 · 任务页三页签 + 底部遥测

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 横向三页签 | `TasksPage.xaml` `ui:TabView` | `ui:TabView`/`ui:TabViewItem` | `SelectedIndex` | **待修**：设计稿要求切页签不吞内容，且页签头在 960 下须完整可见（现 3 页签 OK，但需确认 `TabPanel` 不裁切） |
| 「执行队列」页签带计数徽标 | 稿面 `③` | `ui:TabViewItem.Header` + `ui:InfoBadge` | — | **待修**：现 Header 只有图标+文字，无队列计数徽标 |
| 搜索框「搜索回放记录…」 | `ui:AutoSuggestBox` `CatalogFilter` | `ui:AutoSuggestBox` | **`Text` 已显式 Mode=TwoWay** | 完成（双向 + `ClearButtonEnabled` 清空已合规） |
| 「可执行 36 / 旧版 6」 | `CatalogCountText` | `ui:TextBlock` | — | 完成（真实统计） |
| 回放树：可折叠节点 + 记录行 | 原生 `TreeView` + `HierarchicalDataTemplate`（`ReplayRowTemplate`） | `AppTreeView` BasedOn `DefaultTreeViewStyle` | 四层展开/折叠 | **完成**（2026-09-16 修正）：R1 把原 `HierarchicalDataTemplate` 退化成普通 `DataTemplate` 并加了 `x:Key`，丢掉 `ItemsSource="{Binding Children}"` → `HasItems=false` → 无展开箭头、无子容器、无勾选框；已恢复 |
| 树节点选中视觉 | 原生 `TreeViewItem` 隐式样式（`DefaultTreeViewItemStyle` 自带 `ActiveRectangle`） | `DefaultTreeViewItemStyle` | 选中态 | **完成**（2026-09-15 修正）：容器是原生 `TreeViewItem`，`ItemContainerStyle` 写 `ui:TreeViewItem` 会在生成容器时抛 `InvalidOperationException` |
| 记录行 CheckBox 多选 | `AppReplayRowCheckBox` | `CheckBox`（隐式样式）BasedOn `DefaultCheckBoxStyle` | 禁用 + ToolTip | 完成 |
| 底部常驻遥测条（积压/编码速率/消费比/监视盘剩余/预计剩余时间） | `UniformGrid Columns=5` | `ui:Card` ×5 | 真实采样 + 节点耗时估算 | **完成**（2026-09-22 修正）：预计剩余时间以回放时长 × 冻结的超采样倍数为基线，并用已完成节点的真实墙钟耗时校准；编码速率取自原生 `frames_output` 计数 |
| 页头 + 工具条「开始/继续、当前节点后暂停、立即停止」 | 3 个 Command | `ui:Button` | `CanExecute` | 完成 |
| 工具条按钮（仅队列启停） | 3 个 Command | `ui:Button` | `CanExecute` | **完成**（2026-09-16 修正）：「回放操作」下拉按用户要求移除（内容多余），「刷新回放记录」改为回放记录页签搜索行的独立「刷新」按钮；「刷新快照」按用户要求一并删除（任务配置创建时冻结、之后不可变）。`ui:DropDownButton` 在本页不再使用 |

## 画板 04 · 交互状态与反馈

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| ① 就地确认（替代弹窗） | `DialogService.ConfirmAsync` | `ui:ContentDialog` + `ui:ContentDialogHost` | 主/关闭按钮、Danger | 完成（宿主已在 `OnLoaded` 注入） |
| ② 多步骤可见（合成进度） | `FrozenComposeText` / 节点进度 `ItemsControl` | `ui:InfoBadge` + `ui:TextBlock` | — | 合理偏差：设计稿的「① 读取 / ② 合成 / ③ 编码输出」三段式当前无对应真实状态源；`TaskNodeItem` 才是真实节点进度，故不伪造三段式 |
| ③ 空态（无任务） | — | — | — | **待修**：树/队列/历史/详情均无空态占位；设计稿要求「暂无回放记录」类占位 |
| ④ 受控停止提示 | `TasksViewModel.SetStatus`（文案 → 语义分级）+ 常驻 `ui:InfoBar` | `ui:InfoBar` Severity | Error/Warning/Success/Informational | **完成**（2026-09-16 修正）：任务页此前 18 处状态写入只有 `StatusText`、无可视出口，失败被静默丢弃（表现为「点创建任务没有反应」）；已补常驻状态条，创建失败另弹 `ContentDialog` |
| ⑤ 状态反馈三色条 | `ui:InfoBar` | `ui:InfoBar` Success/Caution/Critical | — | 完成（合成页 OBS 卡已用 `StatusSeverity`） |
| ⑥ 轻量通知 + 撤销 | `DialogService.Notify` | `ui:Snackbar` + `ui:SnackbarPresenter` | 3s 超时 | 合理偏差：WPF-UI 4.3.0 `Snackbar` 无内建「撤销」按钮契约，且多数操作不可撤销；保留纯通知 |

## 画板 05 · 设置页「捕获与合成 / 输出与编码」

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 六页签横向 | `SettingsPage.xaml` `ui:TabView` | `ui:TabView` | 换行 TabPanel | **待修**：`ui:TabView.Resources` 里放 `TabPanel` 无键样式**未加 BasedOn**，且 `TabPanel` 是框架原语（无 Fluent 模板），当前写法只设 Margin 无实际风险，但需核对 960 下 6 页签是否换行完整可见 |
| 「捕获与合成」标题 + 分段器「游戏 TGA / OBS 批量」 | `SetTgaModeCommand`/`SetObsModeCommand` + `SegmentAppearance` | `ui:Button` + `Appearance` | 选中态 Primary | 完成 |
| 监视目录 + 浏览… | `BrowseWatchDirectoryCommand` | `ui:TextBox` + `ui:Button` | — | 完成 |
| 超采样 N（1–64） | `ui:NumberBox` | `ui:NumberBox` | 直接输入 + 清空回填 | **完成**（2026-09-23 调整）：所有数字框隐藏上下微调箭头，避免窄输入框内容被遮挡；控件 `Value` 是可空 `double`，清空输入框后失焦仍由 `SettingsPage` 统一回填有效值 |
| Exposure | `ui:NumberBox` | `ui:NumberBox` | — | 完成 |
| 「输出与编码」卡：编码器下拉 | `ComboBox` `PresetOptions` | `ComboBox`（隐式样式） | — | 完成（隐式样式类，用原生标签合规） |
| 目标码率 Mbps | `ui:NumberBox` | `ui:NumberBox` | — | 完成 |
| 输出后自动删除源 TGA 开关 | `ToggleSwitch` | `ui:ToggleSwitch` | — | **待修**：稿面为「输出后自动删除源 TGA」，现有 `HideHudInCfg` 等开关语义不同；需核对是否存在真实「删除源」设置项，无则不伪造 |
| 底部黄色磁盘提示条 | `DiskSafetySummary` | `ui:InfoBar` Severity=Caution | — | **待修**：现为 `ui:TextBlock`，稿面是 `ui:InfoBar` 风格警示条 |

## 画板 06 · 设置页「游戏 TGA / OBS / 磁盘与安全」

| 设计元素 | 当前代码位置 | 真实组件/样式 | 交互状态 | 结论 |
|---|---|---|---|---|
| 六页签选中态「游戏 TGA」 | `ui:TabView` | `ui:TabViewItem` Selected | — | 完成 |
| 「游戏 TGA 模式」：游戏预设下拉 | — | — | — | 合理偏差：无「游戏预设」真实设置项，不伪造；现有 CFG/序列/快捷键字段是真实语义 |
| 「忽略小于 2 秒的片段」开关 | — | — | — | 合理偏差：无对应真实设置，不伪造 |
| 「OBS 模式」：并行路数（1–8） | `MaxParallelJobs` `ui:NumberBox` | `ui:NumberBox` | — | 完成 |
| 「磁盘与安全」：低磁盘暂停阈值（%） | `DiskSafetyFreePercent` | `ui:NumberBox` | Normalize + 持久化 | 完成 |
| 「删除源文件前二次确认」开关 | — | — | — | 合理偏差：无对应真实设置项；现有确认走 `DialogService.ConfirmAsync`，不新增开关 |
| 页签头可见 + 内容可滚动 | `ScrollViewer` per tab | `ScrollViewer` | — | 完成 |

## 画板 07 · 组件与状态规范

| 规范项 | 当前实现 | 结论 |
|---|---|---|
| 颜色只引 Fluent 资源键 | 全量 `DynamicResource`（`App.xaml.cs` 未覆盖 `SystemAccentColor`） | 完成 |
| 排版用 `FontTypography` 语义层级 | `Styles/AppResources.xaml` 全样式仅设 `FontTypography`+`Appearance` | 完成 |
| 卡片 1px 描边无投影 | `ui:CardControl`/`ui:Card`，无 `Effect`/`DropShadow` | 完成 |
| 度量：960×720 / 40 / 176 / 784×679 / 16·14 / 8·4·1px | `AppPagePadding` 垂直为 12 | 合理偏差（同画板 00） |
| `ui:InfoBadge` 语义：Success/Informational/Caution/Critical/Neutral | `InfoBadgeSeverity` 枚举映射 | **待修**：稿面含 **Neutral**（排队中）；4.3.0 枚举为 `Attention/Informational/Success/Caution/Critical`，**无 Neutral**；需以 `Informational` 或 `Attention` 表达「排队中」 |
| 按钮层级 `ui:Button.Appearance`：Primary 每屏一个 / Secondary / Danger | 合成页 TGA 卡有 2 个 Primary（「停止并收尾」+ 侧栏无） | **待修**：核对「每屏仅一个 Primary」 |
| 状态文字色 | `TaskStateToBrushConverter` 走主题资源 | 完成 |

---

## 汇总：待修项

1. **窗口/共享资源**：无阻断项；保留 `AppPagePadding` 垂直 12 的合理偏差。
2. **合成页**：OBS 拖放落区可见化；`DataGrid` → `ui:DataGrid`；队列「大小/状态」列语义化（`ui:InfoBadge`）。
3. **任务页**：`ItemContainerStyle` → `ui:TreeViewItem`（恢复 Fluent 选中视觉）；「执行队列」页签计数徽标；底部遥测缺项核对；空态占位。
   —— 以上均已处理；其中「`ItemContainerStyle` → `ui:TreeViewItem`」的建议已被证伪（原生容器配 `ui:TreeViewItem` 会抛异常），详见画板 03 表。另：`StatusText` 无可视出口、回放树退化为普通 `DataTemplate` 两项已于 2026-09-16 修复。
4. **设置页**：磁盘提示条 → `ui:InfoBar`；页签换行完整性核对。
5. **组件规范**：确认「每屏一个 Primary」；`Neutral` 徽标以现有枚举表达。
6. **不伪造**：删除源 TGA 开关、游戏预设、忽略 <2s 片段、删除前二次确认、复制路径、三段式进度、日志流、撤销按钮——均无真实设置项/命令/数据源，一律不写入生产界面。

## 验证通道说明

- 运行中的应用实例（PID 48032，`Mmod.App`）锁定 `bin\Debug\net10.0-windows\mmod_record_next.exe`，故所有构建使用临时 `-p:OutDir=.workbuddy\build-r1\`，**未强停用户应用**。
- 控制台为 GBK，PowerShell 工具输出未回传，全部命令输出重定向到 `.workbuddy/backup-r1/*.txt` 后读取。
