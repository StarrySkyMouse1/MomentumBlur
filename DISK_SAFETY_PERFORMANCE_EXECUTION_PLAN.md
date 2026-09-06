# 磁盘安全与合成性能可观测性执行计划

## 1. 目标与状态

本计划覆盖 TGA 任务模式下的百分比磁盘安全、真实吞吐观测、磁盘压力受控收尾、性能预检和 UI 集成。全局风险等级为 **I3**：实现跨越文件系统监视、录制状态机、Native ABI、持久化和 WPF，但不包含不可逆数据库迁移。

已完成并经独立审核：

- S1：`DiskSafetyFreePercent` 设置、任务快照和纯领域契约。
- S2：监视盘健康快照与百分比运行时判断。

当前代码基线为 `84bfaf682c317d13404f5dfff0114ba17babd51c`。后续采用三个宏阶段，不再按模型、Native 函数、单个错误路径拆成微阶段。

## 2. 冻结决策

### 2.1 磁盘安全

- 安全下限范围 `0..50%`，默认 `10%`，`0%` 表示关闭保护。
- 预警线为 `min(100, safety + 5)`；Warning 只提示，Critical 触发受控停止。
- 运行时只使用任务创建或刷新时冻结的百分比，不读取后续变化的全局设置。
- 采样对象必须是 TGA 监视目录所在卷，不是成片输出盘。
- 磁盘健康连续不可用达到限制时失败并等待用户处理，不自动重试。

### 2.2 性能与画质

- 性能指标必须来自真实计数，不根据硬件名称、设置偏好或理论帧数猜测。
- 画质处理继续使用任务快照；运行过程中不得自动关闭处理器、降低超采样、降低码率或切换质量档。
- 画质处理后端使用 `Unknown / Disabled / Gpu / CpuFallback`。
- 编码后端使用 `Unknown / Hardware / Software`，表达实际路径而不是偏好。
- 性能不足只能给出预检结论和运行时提示，不改变任务配置。

### 2.3 输出安全

- 正式 Clip 仍需完整 Envelope、Native Finish、媒体校验和原子提交。
- DiskPressure 下产生的有效 partial 使用独立身份和状态，绝不能冒充 Completed Clip。
- 中断节点恢复时从节点开头重录；不尝试从 partial 接续编码。
- 阶段任务继续按独立片段保存，后期由剪辑软件拼接。

## 3. 总体数据流

```text
TgaDirectoryWatcher
  -> 当前会话的稳定 TGA、候选、积压帧和积压字节
TgaPipelineOrchestrator
  -> 成功提交量、Native 输出量、处理/编码后端
CapturePerformanceTracker
  -> 滚动速率、消费比、趋势、积压追赶时间、峰值
CaptureEnvelopeRecorder / NodeExecutionCoordinator
  -> 运行日志、磁盘压力收尾、节点结果
RenderTaskRepository
  -> 正式 Clip 或独立 Partial 元数据
TasksViewModel / TasksView
  -> 预检、运行状态和诊断展示
```

依赖只能沿该方向流动。Core/Native 不依赖 WPF；纯计算模型不访问磁盘、数据库或真实硬件。

## 4. 宏阶段 M3：真实吞吐遥测与 Native 后端诊断

### 4.1 结果

建立后续预检和 UI 可直接消费的可信、不可变运行时快照。本阶段不修改 UI、数据库和 partial 生命周期。

### 4.2 精确范围

允许修改：

- `src/Mmod.Core/Models/RecordingModels.cs`
- `src/Mmod.Core/Services/RecordingAbstractions.cs`
- `src/Mmod.Core/Services/TgaDirectoryWatcher.cs`
- `src/Mmod.Core/Services/TgaPipelineOrchestrator.cs`
- `src/Mmod.Core/Services/CaptureEnvelopeRecorder.cs`
- `src/Mmod.Core/Services/NativeSessionDiagnostics.cs`
- `src/Mmod.Core/Native/MmodNativeInterop.cs`
- `src/Mmod.Core/Native/NativeBlendSession.cs`
- `src/Mmod.Native/include/mmod_native.h`
- `src/Mmod.Native/src/session.cpp`
- `src/Mmod.SmokeTest/Program.cs`
- `src/Mmod.SmokeTest/RecordingStateTests.cs`

只允许新增：

- `src/Mmod.Core/Services/RateWindow.cs`
- `src/Mmod.Core/Services/CapturePerformanceTracker.cs`

不得删除、重命名或移动文件。

### 4.3 行为契约

- Produced 是当前前缀下通过完整性检查的稳定 TGA 数量；文件系统重复事件不能重复计数。
- Consumed 只在帧成功提交 Native 后增加；Submit 失败不能增加。
- Output 来自 Native `frames_output`，不得由 `submitted / N` 推算。
- 当前积压与峰值包含稳定 pending；candidate 单独表达。积压字节只统计当前会话文件并使用 `long`。
- 速率使用 10 秒滚动窗口和单调时间；窗口未就绪、计数重置和零时间差不得产生负数、NaN 或 Infinity。
- 消费比为 `ConsumedFPS / ProducedFPS`，分母无效时为未知。
- 趋势为 `Unknown / Stable / Growing / Shrinking`，具有集中、可测试的噪声死区。
- 追赶时间只表示当前积压的清空时间，仅在消费速度明显高于生产速度时有效：`pending / (consumed - produced)`。
- Native 诊断使用稳定、版本化的 C ABI。所有效果关闭为 Disabled；实际 GPU 路径为 Gpu；实际 CPU fallback 为 CpuFallback；查询失败为 Unknown。
- 当前 Native 明确禁用硬件 MFT 时，编码后端应报告 Software，不得根据 Auto/Nvenc/Amf 偏好报告 Hardware。
- 遥测采样失败不能掩盖 Submit、Finish、编码、DiskPressure 或 Cleanup 的真实错误。

### 4.4 验收

- Core、Native、App、SmokeTest Release 构建成功且 C# 为 0 警告、0 错误。
- `settings`、`snapshot`、`recording`、`weights`、`processing`、`native-effects` 的现有实际路由通过。
- Fake 覆盖速率窗口、计数重置、趋势死区、追赶时间、积压峰值、文件消失和 Native 查询失败。
- ABI 两端字段布局一致，现有 progress/processing-status API 保持兼容。
- 不修改 WPF、数据库、滤镜数学、编码选择策略和磁盘压力行为。

## 5. 宏阶段 M4：DiskPressure 受控收尾与 Partial 生命周期

### 5.1 依赖和结果

入口要求 M3 已通过或以不影响真实计数的 Ledger 安全推进。Critical 不再只是异常退出：它请求停止 `startmovie`，等待物理静默，冻结 watcher，排空已稳定帧，完成 Native Finish，并对结果做媒体校验。合法结果保存为独立 partial；任何未证明边界仍按失败处理。

### 5.2 预计精确范围

本阶段开始前由 Codex 依据 M3 当前 HEAD 再核验并冻结最终清单。预计修改：

- `src/Mmod.Core/Models/RenderTaskModels.cs`
- `src/Mmod.Core/Models/RecordingModels.cs`
- `src/Mmod.Core/Services/CaptureEnvelopeRecorder.cs`
- `src/Mmod.Core/Services/CaptureCleanupCoordinator.cs`
- `src/Mmod.Core/Services/NodeExecutionCoordinator.cs`
- `src/Mmod.Core/Services/RecordingFailureClassifier.cs`
- `src/Mmod.Core/Services/RecordingRetryPolicy.cs`
- `src/Mmod.Core/Services/RenderTaskRepository.cs`
- `src/Mmod.Core/Services/RenderTaskRunner.cs`
- `src/Mmod.SmokeTest/RecordingStateTests.cs`
- `src/Mmod.SmokeTest/Program.cs`

只有在现有 repository 结构无法安全表达 partial 时才授权新增 migration/模型文件；不得在任务包签发前自行扩大。

### 5.3 状态与失败语义

```text
Capturing
  -> DiskPressureRequested
  -> strict endmovie
  -> quiescence
  -> freeze and drain
  -> Native Finish
  -> media validation
  -> ControlledStopFinalized
  -> node FailedNeedsAttention / Paused
```

- 任一停止、静默、排空、Finish 或校验步骤失败，不能写入有效 partial。
- partial 使用独立路径和元数据，不占用正式 ClipPath，不把节点标为 Completed。
- 节点继续时创建新 Attempt 和新 TGA prefix，从头录制。
- 崩溃恢复不得删除已明确验证并持久化的 partial；未验证临时文件仍按保守策略删除。
- Cleanup Barrier 仍由统一协调器拥有，不建立第二套清理状态机。

### 5.4 验收

- Critical、用户取消、游戏退出、Pipeline fault、Finish fault 的状态和文件结果彼此区分。
- 有效 partial 可在历史/诊断层识别但不会参与正式地图合并。
- 数据库升级兼容旧库，重复启动幂等；失败不会半迁移或误标 Completed。
- 节点继续从头执行，旧 partial 不污染新 Attempt。
- repository、recording 和 crash recovery 测试通过。

## 6. 宏阶段 M5：性能预检、UI 与产品集成

### 6.1 依赖和结果

入口要求 M3 的真实遥测和 M4 的输出安全均已成立。使用任务的完整冻结配置执行短时性能预检，并在设置页/任务页显示可解释结果和运行状态。

M5 根据当前执行 AI 的首轮缺陷分布拆成两个自然边界。M5-A 先冻结可信 Core
行为和可靠性收口；Codex 审查后，M5-B 只消费已经证明的 Core 契约完成 UI。
不得再为单个局部缺陷建立独立 Repair 轮；有唯一修法且不破坏下一阶段的发现进入
Ledger 并由下一阶段一并关闭。

### 6.2 M5-A：Core 预检与可靠性收口

#### 6.2.1 精确范围

生产文件可修改：

- `src/Mmod.Core/Models/RecordingModels.cs`
- `src/Mmod.Core/Services/RecordingAbstractions.cs`
- `src/Mmod.Core/Services/CapturePerformanceTracker.cs`
- `src/Mmod.Core/Services/TgaDirectoryWatcher.cs`
- `src/Mmod.Core/Services/TgaPipelineOrchestrator.cs`
- `src/Mmod.Core/Services/CaptureEnvelopeRecorder.cs`
- `src/Mmod.Core/Services/NodeExecutionCoordinator.cs`
- `src/Mmod.Core/Services/RenderTaskRunner.cs`

仅可新增：

- `src/Mmod.Core/Services/PerformancePreflightEvaluator.cs`

测试文件可修改：

- `src/Mmod.SmokeTest/Program.cs`
- `src/Mmod.SmokeTest/RecordingStateTests.cs`

不得修改 WPF、数据库 schema/repository、Native ABI、设置持久化或文档；不得删除、
重命名、移动文件。

#### 6.2.2 预检执行契约

- 预检入口绑定一个已创建且仍为 Pending 的任务，读取该任务持久化的
  `RenderSettingsSnapshot` 和第一个 Pending 节点；不得改用当前全局设置。
- 预检复用真实 Momentum 地图、真实回放、真实 TGA watcher、真实 Native 处理和真实编码后端。
  禁止用低 N、关闭画质处理、降低码率、虚构计数或纯 CPU 小样本冒充任务预检。
- 预检在独立诊断 CaptureSession、独立 TGA prefix 和独立临时输出中运行；不得创建正式
  Attempt，不得改变任务/节点状态、RetryCount、ClipPath 或 partial 元数据。
- 建立 PlaybackEvidence 后采集一个完整 10 秒滚动窗口；窗口不足、后端未知、pipeline fault、
  采样不可用或用户取消均形成 `Unknown` 或明确失败，不得伪造 Pass。
- 预检结束必须走现有严格 `endmovie → quiescence → freeze/drain → Native Finish` 和统一
  Cleanup Barrier；诊断 MP4 与当前 prefix TGA 只在完成诊断后删除，不得进入正式合并输入。
- `PerformancePreflightEvaluator` 是确定性纯策略：输入最终 `PerformanceSnapshot` 和采样充分性，
  输出 `PerformancePreflightResult`。最低判定基线：充分样本且后端已知时，消费比 `>= 0.98`
  且积压不增长为 Pass；`0.90–0.98` 或边界稳定性不足为 Marginal；消费比 `< 0.90` 或持续
  Growing 为 Fail；证据不足为 Unknown。阈值集中定义并覆盖等于边界。
- 预检结论只诊断，不修改冻结配置、不自动降 N、不关闭画质处理、不切换编码器。

#### 6.2.3 本阶段同时关闭的 Ledger

- `M3-CF-001`：watcher 在一个锁内返回当前 `PendingFrames + PendingBytes` 的不可变原子快照；
  pipeline 的 `Performance` 和采样只消费该快照，禁止分别读取两个瞬时属性形成撕裂组合。
- `M4-B-004`：异常清理必须先确认 partial candidate 已删除，再清除 Pending 元数据；删除失败时
  保留 Pending 路径供崩溃恢复重试，禁止出现“数据库 None、文件仍存在”的可达顺序。
- `M4-CF-001`：pipeline 记录 Native Finish 已成功完成的事实；`DisposeAsync` 对已完成 Finish 的
  session 不再二次 Finish，未完成 session 仍保留 best-effort 清理。
- `M4-FR-001`：枚举重命名的编译必需涟漪行政关闭，无额外代码工作。

### 6.3 M5-B：UI 与产品集成

#### 6.3.1 精确范围

Core 生产文件可修改：

- `src/Mmod.Core/Models/RecordingModels.cs`
- `src/Mmod.Core/Services/CaptureEnvelopeRecorder.cs`
- `src/Mmod.Core/Services/NodeExecutionCoordinator.cs`
- `src/Mmod.Core/Services/RenderTaskRunner.cs`

WPF 生产文件可修改：

- `src/Mmod.App/ViewModels/SettingsViewModel.cs`
- `src/Mmod.App/ViewModels/TasksViewModel.cs`
- `src/Mmod.App/Views/Pages/SettingsPage.xaml`
- `src/Mmod.App/Views/Pages/TasksPage.xaml`

测试文件可修改：

- `src/Mmod.SmokeTest/Program.cs`
- `src/Mmod.SmokeTest/RecordingStateTests.cs`

不得新增、删除、重命名或移动文件；不得修改数据库、Native ABI、回放目录解析、正式 Clip/partial
状态机或任务树选择规则。

#### 6.3.2 产品契约

- Tasks 页为选中的 Pending 任务提供“性能预检”；按钮只在没有执行/验证/预检会话时可用。
  预检显示冻结配置摘要和 Produced/Consumed/Output FPS、消费比、峰值积压帧/字节、真实画质处理
  后端、真实编码后端、Pass/Marginal/Fail/Unknown 及明确中文解释。
- 尚未预检、Fail 或 Unknown 时开始队列必须弹出确认；用户拒绝则不启动。Marginal 明确警告但允许
  用户继续；Pass 直接继续。任何结论都不得隐式刷新任务快照或修改设置。
- 运行时由 Core 暴露一个不可变状态投影，至少包含最新 `DiskHealthSnapshot`、
  `PerformanceSnapshot`、任务/节点身份和采样时间。WPF 只投影，不重新计算策略或直接轮询 Native。
- Tasks 页运行时显示监视盘剩余百分比/GiB、安全线/预警线、当前积压、趋势、追赶时间和真实后端；
  文案明确“追赶时间不是整项任务 ETA”。Unavailable、Warning、Critical 使用不同中文状态。
- Settings 页增加 `DiskSafetyFreePercent` 的 0–50 数值设置和语义说明：0=关闭保护，Warning=
  safety+5（最高 100）；同时显示当前配置对应的安全线/预警线。不得恢复旧 2 GiB 设置入口。
- UI 状态刷新最高 4 Hz；高频 pipeline 事件先在 Core/VM 合并节流，不能每帧 ReloadTasks、查询数据库
  或触发整页 PropertyChanged。
- 任务创建和刷新仍冻结百分比及完整画质配置；只有 TGA 模式、游戏根目录与监视目录均有效时允许
  创建、预检或启动。旧 JSON 默认值和 Pending-only 刷新守卫保持不变。

### 6.4 M5 共享行为与验收

#### 6.4.1 共享行为契约

- 预检使用将要执行任务的分辨率、N、画质处理、码率和真实后端，不使用低配替代测试冒充结果。
- 结论为 Pass / Marginal / Fail / Unknown；显示生产、消费、输出 FPS、消费比、峰值积压和真实后端。
- Fail/Unknown 默认要求用户确认，不私自改变冻结配置。
- 运行时显示监视盘剩余百分比和 GiB、警告/安全线、当前积压、趋势、追赶时间和后端。
- “追赶时间”必须明确不是整项任务 ETA；若要显示任务 ETA，必须使用节点剩余量和实测速率单独计算。
- 只有 TGA 模式、监视目录已配置且存在时才允许创建/启动相关任务，保持既有产品规则。
- UI 更新必须节流，不能让高频 PropertyChanged 反过来拖慢管线。

#### 6.4.2 共享验收

- 无 GPU、GPU 处理、CPU fallback、处理关闭、软件编码均能显示真实且不误导的状态。
- 设置保存、任务快照冻结、Pending 刷新规则和旧 JSON 兼容继续成立。
- 低磁盘 Warning/Critical、预检 Fail/Unknown 和正常执行具有明确中文提示。
- Core/App/SmokeTest 构建与相关路由通过，并完成人工 UI 检查清单。

M5-A 额外要求：预检纯策略边界、真实配置传递、诊断会话不写正式状态、原子 backlog、partial
清理顺序和单次 Finish 均有 Fake 或确定性验证。M5-B 额外要求：人工启动应用检查设置页与任务页
布局、按钮门禁、确认框、中文状态和 4 Hz 节流；没有真实 Momentum 环境时实机预检必须标记
Unverified，不得以 Fake 代替实机结论。

## 7. Final Closeout

主阶段完成后，Codex 汇总完整 Closeout Ledger：

- 仅 1–3 个生产文件、局部可逆且不涉及持久化、Native ABI 或外部副作用时，可由 Codex 直接关闭。
- 其他情况发出一次集中 Closeout 任务，统一检查累积修复的相互影响。
- 更新 `README.md`、`IMPLEMENTATION_REPORT.md` 和必要实机脚本说明。
- 最终独立运行 Core、Native、App、SmokeTest、diff/status 检查。
- 实机 Momentum/RAM 盘、长任务 soak 和故障注入若环境不可用，必须记录为 Unverified，不能伪造通过。
- 最终验收要求 Ledger 为空，或由用户明确接受剩余债务。

## 8. 提交和审查节奏

- 每个宏阶段由执行 AI 完整实现、自查、验证、修复后创建一个本地提交。
- 不 push、不创建 PR、不创建分支或 worktree，除非用户之后明确授权。
- 每个提交返回紧凑回执；Codex 独立检查真实状态和 Diff。
- 审查结论仅使用 Pass、Advance with closeout、Fail。
- 只有 Foundation Blocking 才返工当前阶段；其余问题写入 Ledger 并随下一宏阶段或 Final Closeout 处理。

## 9. 当前授权入口与 Closeout Ledger

M5-A、M5-B 与 Final Closeout 已由 Codex 直接连续实施。当前进入最终独立验证；实机 Momentum、
RAM 盘长任务 soak 与故障注入若当前环境不可用，必须保留为明确的 Unverified 实机验收项。

当前 Ledger：

| ID | 分类 | 影响 | 精确范围 | 负责阶段 | 验收 | 升级条件 |
|---|---|---|---|---|---|---|
| M3-CF-001 | Resolved | 帧/字节积压快照改为同锁不可变观察 | `RecordingAbstractions.cs`, `TgaDirectoryWatcher.cs`, `TgaPipelineOrchestrator.cs`, tests | M5-A | pipeline 只消费原子快照 | 无 |
| M4-B-004 | Resolved | 删除确认前保留 Pending 指针 | `NodeExecutionCoordinator.cs`, tests | M5-A | 不存在 None + orphan 文件顺序 | 无 |
| M4-CF-001 | Resolved | 成功 Finish 后 Dispose 不再二次 Finish | `TgaPipelineOrchestrator.cs`, tests | M5-A | 一个 session 只成功 Finish 一次 | 无 |
| M4-FR-001 | Resolved | 枚举语义重命名的单行编译涟漪 | 无待改文件 | M5-A | 无旧枚举残留 | 无 |
