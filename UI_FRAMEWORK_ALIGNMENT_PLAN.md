# UI 框架对齐执行计划

> 目标：把已交付的 Ardot 交互设计稿从"通用 Fluent 视觉示意"改写为"WPF-UI 4.3.0 组件可直接落地"的规格，并在源码侧完成对应改造。
>
> 决策依据（用户已确认）：
> - **问题 1 → 方案 B**：窗口放大到 **1440×900**，`MainWindow.xaml` 同步修改 Height/Width。
> - **问题 2 → 方案 A**：主色**改用系统强调色**（`SystemAccentColor` / `AccentFillColorDefaultBrush`），不再硬编码 `#0B6BCB`。

---

## 一、现状事实（已核实）

### 1.1 技术栈
| 项 | 值 | 来源 |
|---|---|---|
| UI 框架 | WPF-UI (Wpf.Ui) **4.3.0** | `Mmod.App.csproj` |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | 同上 |
| TFM | net10.0-windows | 同上 |
| 主题挂载 | `ui:ThemesDictionary Theme="Light"` + `ui:ControlsDictionary` | `App.xaml` |
| 窗口 | `FluentWindow` + `NavigationView` + `TitleBar`（Mica 背板） | `MainWindow.xaml` |

### 1.2 关键差异清单（本次要修的东西）

| # | 问题 | 现状 | 目标 |
|---|---|---|---|
| D1 | **窗口尺寸** | `960×720`（Min `780×600`） | `1440×900`，Min 保持 `960×720` |
| D2 | **主色硬编码** | 设计稿硬编码 `#0B6BCB` | 全量改用系统强调色资源键 |
| D3 | **零 `ui:Card`/`ui:InfoBar`/`ui:InfoBadge`/`ui:ContentDialog`** | 项目当前**完全未引入**这些组件 | 按设计稿引入并落地 |
| D4 | **卡片为裸 `Border` 手绘** | `ComposePage` 2 处 + `SettingsPage` `QualityModuleTemplate` 1 处 | 换成 `ui:CardControl` / `ui:CardExpander` |
| D5 | **状态一律扁平文本** | `StatusText`/`TgaMetricsText`/`DiskSpaceText` 直接 TextBlock | 换成 `ui:InfoBar` / `ui:InfoBadge` / 指标块 |
| D6 | **5 处系统 MessageBox** | `TasksViewModel` 行 139/207/216/307 | 换成 `ui:ContentDialog` |
| D7 | **任务页几乎无框架组件** | 15 个标准 `Button`、`GroupBox`、`TabControl`、`TreeView` | 换 `ui:Button` / `ui:Card` / `ui:TabView` / `ui:TreeView` |
| D8 | **硬编码颜色 3 处** | `SettingsPage:74` `#C8A45C`；`TasksPage:93,99` `#33000000` | 换语义色资源键 |
| D9 | **硬编码字号** | `FontSize="22"` / `"28"` / `"18"` / `"11"` 散落 | 换 `ui:TextBlock` + `FontTypography` |
| D10 | **设置页单列长滚动** | 单列 + `MaxWidth=720` 全页滚动 | 左侧子导航 + 右侧分组（`NavigationView` 或分段卡片） |

### 1.3 框架能力边界（不能做的事）

| 需求 | 框架支持 | 处理策略 |
|---|---|---|
| 磁盘"安全线刻度"（旋钮标记） | ❌ 无对应组件 | 保留 `Grid` + 细 `Border` 自绘，**标注为允许的自定义**（纯几何、无交互） |
| 指标大数字 + 副标题的 tile | △ 需组合 | `ui:Card` + `ui:TextBlock(FontTypography=Title)` + `ui:TextBlock(Appearance=Tertiary)` 组合 |
| 就地确认条 | ✅ 可用 `InfoBar`(Warning) + 两个 `ui:Button` 组合 | 保留就地模式，不退回弹层 |
| 环形加载 | ✅ `ui:ProgressRing` | 直接用 |
| 线性进度 | ✅ 标准 `ProgressBar` | 用标准控件（框架未提供专属 ProgressBar，`ProgressRing` 是环形） |

---

## 二、设计稿修订（Ardot）

文件：`https://ardot.tencent.com/file/725691702565967`

### A 阶段：画板尺寸与栅格（影响 00–06 全部画板）

| 步骤 | 内容 |
|---|---|
| A1 | 画板 01/02/03/05/06 内容区从 `1232×860` 重排为 1440×900 下的新栅格 |
| A2 | 导航栏 208 → 保持 208（1440 下有足够空间）；内容 padding 20 保持 |
| A3 | 任务页三栏 `300 / 380 / 自适应` 在 1232 内容宽下复核：300+380+14×2=708，剩余 524 → **可行，保持** |
| A4 | 设置页 `176 + 420 / 自适应`：176+420+14=610，剩余 622 → **可行，保持** |

> 结论：**栅格本身在 1440 下成立**，主要修的是画板尺寸与总览页元信息（写死的 960×720）。

### B 阶段：颜色与字体规格化（画板 07 规范页重写）

| 步骤 | 内容 |
|---|---|
| B1 | 画板 07「颜色」区重写为 **Fluent 资源键对照表**，不再给裸色值 |
| B2 | 主色卡改为 `SystemAccentColor` 说明 + 深浅三档 |
| B3 | 新增「组件 ↔ 控件名」对照区，列全部 `ui:*` 控件 |
| B4 | 「字体层级」区改为 `FontTypography` 枚举对照（Caption/Body/BodyStrong/Subtitle/Title/TitleLarge/Display） |
| B5 | 「圆角」区对齐框架：卡片用 `CardControl` 默认圆角，按钮用 `ui:Button` `CornerRadius` |
| B6 | 总览页 `BoardMeta` 的 `960×720` 改为 `1440×900` |

### C 阶段：页面画板的组件标注

| 画板 | 修订内容 |
|---|---|
| 00 总览 | 更新窗口尺寸说明；「落地建议」补组件引入顺序 |
| 01 合成 TGA | 卡片→`CardControl`；`LiveDot`+标题→`InfoBadge`；状态行→`InfoBar`；进度→`ProgressBar`；指标 tile→`Card`+`TextBlock` |
| 02 合成 OBS | 同上；队列 `DataGrid` 保留（标准控件，框架未替换） |
| 03 任务页 | 工具条 `Button`→`ui:Button`；栏位容器→`ui:Card`；`TabControl`→`ui:TabView`；遥测条→`ui:Card` |
| 04 交互状态 | 确认弹层→`ui:ContentDialog`；就地确认条→`InfoBar`+`Button`；重试/崩溃恢复卡→`ui:CardControl` |
| 05/06 设置页 | 子导航→`ui:NavigationView`；分组→`ui:CardControl`；字段标签→`ui:TextBlock` |

### D 阶段：新增一页「组件落地对照」

| 步骤 | 内容 |
|---|---|
| D1 | 新增画板 08「WPF-UI 组件落地对照」 |
| D2 | 逐元素三列：设计稿元素 / WPF-UI 控件与关键属性 / 是否需自定义 |
| D3 | 标注自定义项（仅磁盘刻度线）并给出理由 |

---

## 三、源码改造（Mmod.App）

### E 阶段：基础设施（先做，其他都依赖它）

| 步骤 | 文件 | 内容 |
|---|---|---|
| E1 | `MainWindow.xaml` | `Height="720"→"900"`，`Width="960"→"1440"`，`MinHeight="720"`，`MinWidth="960"` |
| E2 | `App.xaml` | 保持系统强调色（方案 A 不动 `SystemAccentColor`）；新增自定义语义资源别名（`AppCardStrokeBrush` 等）便于统一 |
| E3 | 新建 `Styles/AppResources.xaml` | 集中定义：卡片样式、指标 tile 样式、K/V 行样式，避免三页重复 |
| E4 | `App.xaml` 合并 `AppResources.xaml` | 挂载新字典 |

### F 阶段：合成页（`ComposePage.xaml`）

| 步骤 | 内容 |
|---|---|
| F1 | 两处裸 `Border` 卡片 → `ui:CardControl`（`Header` + `Icon`） |
| F2 | TGA 卡：`TgaMetricsText` 拆为指标 tile（`Card`+`TextBlock`）；状态 → `ui:InfoBar`（`Severity` 随运行态） |
| F3 | OBS 卡：`BatchSummary` → 指标行；`DiskSpaceText` → 独立磁盘卡（含安全线刻度） |
| F4 | 按钮统一加 `Icon`（`SymbolIcon`），危险操作 `Appearance="Danger"` 但默认描边 |
| F5 | `DataGrid` 保留标准控件，列头宽度与 1440 适配 |
| F6 | 页面底部 `StatusText` 保留（作为最底层兜底），上层用 `InfoBar` 表达 |

### G 阶段：任务页（`TasksPage.xaml`，改动最大）

| 步骤 | 内容 |
|---|---|
| G1 | 标题 `FontSize=28` → `ui:TextBlock FontTypography="TitleLarge"` |
| G2 | 7 个工具条 `Button` → `ui:Button`，按语义分组（编目 / 验证 / 运行），主操作 `Appearance="Primary"` |
| G3 | 工具条容器 → `ui:Card`（白底 + 描边 + 圆角 8）——对齐设计稿 `ActionToolbar` |
| G4 | `GroupBox`「回放记录」→ 左栏 `ui:Card` + `ui:TreeView` |
| G5 | `TabControl` → `ui:TabView`（执行队列 / 历史 / 详情日志） |
| G6 | 底部三块 `Border` → 遥测条 `ui:Card`；`PreflightText`/`RuntimeText` → `ui:InfoBar` 或 K/V 行 |
| G7 | 队列增删按钮下沉到队列卡内（对齐设计稿） |

### H 阶段：设置页（`SettingsPage.xaml`）

| 步骤 | 内容 |
|---|---|
| H1 | 单列长滚动 → 左侧 `ui:NavigationView`（176 宽）子导航 + 右侧分组区 |
| H2 | 6 个分组：捕获与合成 / 画质处理 / 后期 4K / 游戏 TGA / OBS / 磁盘与安全 |
| H3 | `QualityModuleTemplate` 外层 `Border` → `ui:CardExpander`（模块名作 Header，可折叠） |
| H4 | `#C8A45C` 风险提示 → `ui:InfoBar Severity="Warning"` |
| H5 | 各分组包进 `ui:CardControl`，统一分组标题层级 |
| H6 | `NumberBox` 保留（已符合框架规范，含 `SpinButtonPlacementMode`） |

### I 阶段：对话框（`TasksViewModel.cs` 等）

| 步骤 | 内容 |
|---|---|
| I1 | 新建 `Services/DialogService.cs`：包装 `ContentDialogService` / `ContentDialog` |
| I2 | 4 处 `System.Windows.MessageBox` → `ui:ContentDialog`（`Title`/`Content`/`PrimaryButtonText`/`CloseButtonText`/`PrimaryButtonAppearance`） |
| I3 | 危险操作（删除输出、删除记录）→ `PrimaryButtonAppearance="Danger"` + 二次确认语义 |
| I4 | 在 `MainWindow` 加 `ui:ContentDialogHost` / `SnackbarPresenter` 承载 |
| I5 | 复制类反馈（"已复制…"）→ 改用 `Snackbar`，不再占用 `StatusText` |

### J 阶段：验证

| 步骤 | 内容 |
|---|---|
| J1 | `dotnet build src/Mmod.App -p:SkipNativeBuild=true` 编译通过 |
| J2 | 全局搜索确认无残留硬编码色（除自定义刻度线） |
| J3 | 三页在 1440×900 与最小 960×720 下目视检查 |
| J4 | 对照设计稿逐画板回看，确认组件映射一致 |

---

## 四、执行顺序与依赖

```
E（基础设施）
  └─> F（合成页）──┐
  └─> G（任务页）──┼─> J（验证）
  └─> H（设置页）──┤
  └─> I（对话框）──┘
A–D（设计稿修订）与 E–I（源码）可并行，但 D 阶段依赖 F–I 的最终结论
```

**推荐批次**：
1. 批次 1：E + A（基础设施 + 画板尺寸）
2. 批次 2：G + B（任务页源码 + 规范页重写）
3. 批次 3：F + H + C（合成页 + 设置页 + 页面标注）
4. 批次 4：I + D（对话框 + 对照页）
5. 批次 5：J（验证）

---

## 五、风险与取舍

| 风险 | 影响 | 应对 |
|---|---|---|
| 窗口 1440×900 在小屏不可用 | 用户笔记本 1366×768 放不下 | Min 设 960×720；布局用 `Grid` 星号列自适应，窄屏自动收窄侧栏 |
| `ui:TabView` 行为与 `TabControl` 不同 | 任务页签切换逻辑需调整 | 保留 `TabControl` 作为降级选项，先试 `TabView` |
| `ContentDialog` 需宿主容器 | 引入服务层，改动 ViewModel | 用 `DialogService` 隔离，ViewModel 不直接引用控件 |
| 设置页子导航改造大 | 回归风险高 | 分步：先做分组卡片，再做子导航 |
| 系统强调色与品牌色差异 | 视觉与设计稿不完全一致 | 方案 A 已确认；设计稿规范页改为强调色说明 |

---

## 六、交付物

1. 本计划文档 `UI_FRAMEWORK_ALIGNMENT_PLAN.md`
2. 修订后的 Ardot 画板（00–08，含新增组件对照页）
3. 改造后的 `MainWindow.xaml` / `ComposePage.xaml` / `TasksPage.xaml` / `SettingsPage.xaml`
4. 新增 `Styles/AppResources.xaml`、`Services/DialogService.cs`
5. 编译验证通过
