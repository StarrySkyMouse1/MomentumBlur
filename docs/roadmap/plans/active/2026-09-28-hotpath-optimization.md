# 热路径与正确性优化批次（2026-09-28）

> 来源：2026-09-28 全仓只读审查（Core 录制链路 / Native GPU / WPF App / 仓库卫生）。
> 用户授权：实施「最大程度的优化措施，排除高风险和无法验证的」，按批次记录进度。
> 模式：Direct（当前 AI 直接实施），本计划文件为唯一进度真相源。

## 当前执行状态

- **状态**：已完成（代码 + 自动化验证；UI 实际手感待用户实机体验）
- **当前批次**：收尾
- **已完成**：
  - A1–A12 代码改造。
  - A13 证据：`dotnet build Mmod.App -c Release -p:CMakeExe=<VS18 Insiders cmake>` 0 警告 0 错误；SmokeTest 默认套件 OK（exit 0）；`recording` 13 组 `RECORDING_OK`。
  - A3 修正记录：首轮 `recording` 出现 1 失败（M3 vanished pending 遥测契约，测试窗口 ~100ms），将降频改为自适应——pending ≤ 500 每 tick 清理（契约保持），backlog > 500 才降为 full scan 同频；`500` 提为常量 `BacklogFrameThreshold` 并统一三处使用。复跑 `RECORDING_OK`。本机 cmake 不在 PATH，命令行构建需显式 `-p:CMakeExe=D:/Software/Professionals/Visual Studio/18/Insiders/.../cmake.exe`（csproj 未覆盖该安装路径，已留待后续处理）。

## 方向与非目标

**方向**：消除录制热路径上的每帧冗余开销（SQLite 同步写、逐像素循环、全帧拷贝、LOH 分配、文件系统热调用），修复已确认的正确性缺陷（预览管线泄漏、静默失败路径），补齐 Native 构建优化。所有条目均可通过现有构建 + SmokeTest 验证。

**非目标 / 明确排除（高风险或无法本机验证）**：

- 证据探针（VisualPlaybackEvidenceProbe）按输出边界节流 —— 改变 ReplayEnd 证据语义，需实机验证，排除。
- `PRAGMA synchronous=NORMAL` —— 改变崩溃持久性契约，排除。
- COM/MF `CoUninitialize` 配对 —— apartment 语义微妙，现有配对方式保守但安全，排除。
- `/fp:fast`、`/arch:AVX2` —— 前者破坏 CPU 参考实现的数值对账，后者有旧 CPU 兼容风险，排除。
- 硬件编码器（NVENC/AMF）、错误码分层重构 —— 契约变更，超出本批次。
- SmokeTest 迁移框架、CI、Directory.Build.props —— 工程化改造，未获授权或风险不匹配，排除。
- TasksViewModel `SelectedTask` 重赋值抑制 —— 会冻结详情面板随录制进度刷新（UX 回归），改为靠查询降本解决。

## 读写范围与保护

**写**：`RenderTaskRepository.cs`、`RenderTaskRunner.cs`、`TgaDirectoryWatcher.cs`、`TgaPipelineOrchestrator.cs`、`TgaFrameReader.cs`、`CaptureEnvelopeRecorder.cs`、`ReplayQualityPreviewCaptureService.cs`、`CaptureCleanupCoordinator.cs`（不动）、`NativeBlendSession.cs`、`NativeSessionFactory.cs`、`ObsSynthesisService.cs`、`UserSettingsStore.cs`、`MomentumDirectoryLinkService.cs`、`TasksViewModel.cs`、`SettingsPage.xaml`、`ComposeViewModel.cs`、`DialogService.cs`、`session.cpp`、`gpu_blend.cpp/.h`、`frame_processing.cpp`、`CMakeLists.txt`。

**保护（禁止顺手修改）**：录制状态机契约（Attempt/CaptureSession 隔离、正向证据、Cleanup Barrier、原子输出）、`RecordingTimeoutPolicy` 数值、SmokeTest 代码（只运行不修改）、未提交用户改动（当前工作区已干净）。

**测试授权**：仅运行现有 SmokeTest（全量 + recording 子命令），不新增/修改测试。

## 契约与禁止捷径

- 每帧 `OnNodeStatusChanged → UpdateNode` 只做节流（状态变化立即写 + 250ms 兜底），不删除事件契约；关键节点状态（开始/完成/失败/取消）无条件落库路径保持不变。
- watcher 的语义不变：前缀隔离、永久 dedup、物理静默、候选稳定判定全部保留；`PruneMissingPending` 降频只影响已消失文件的检测延迟（该路径本身是异常路径，且排空断言仍会以帧序不连续失败）。
- `TryParseFrameIndex` 零正则化必须与原正则语义等价（前缀大小写不敏感 + 尾部全 ASCII 数字 + `.tga` 大小写不敏感）。
- `IsValidTgaFile` 移除调用前先 grep 确认无其他调用方。
- GPU 侧所有优化必须保持数值语义：pack shader 输出字节序 = BGRA 小端，memcpy 等价性基于此；数学对账测试（CPU 参考）必须继续通过。
- 禁止吞异常、禁止静默 fallback、禁止降低断言。

## 批次 A —— C# 热路径与持久化（Mmod.Core / 数据层）

- [x] A1 `RenderTaskRepository`：`GetLogs(taskId, limit=0)`（DESC LIMIT 子查询，默认 0=全量兼容旧调用）；schema 初始化加 `CREATE INDEX IF NOT EXISTS ix_task_logs_task ON task_logs(task_id, id)`；新增 `GetNodesForTasks(ids)` 批量查询（IN 分块 ≤500）。
- [x] A2 `RenderTaskRunner`：`OnNodeStatusChanged` 节流（状态变化立即写 + 250ms 兜底，消除每帧同值 UPDATE）；`RunQueueAsync` 的 catch 内 `HandleQueueInterruptionAsync`/`AddLog` 再包一层兜底；`DisposeAsync` 忙等改为 await 队列循环 Task。
- [x] A3 `TgaDirectoryWatcher`：`TryParseFrameIndex` 零正则化（删除每次 new Regex 的 `ExactPrefixRegex` 与死代码 `BuildPrefixRegex`）；`PruneMissingPending` 降到 full scan 同频；候选稳定后移除冗余的 `IsValidTgaFile` 二次打开。
- [x] A4 `TgaPipelineOrchestrator`：`GetProgress()` 只在 blend 窗口边界读取（窗口中点输出计数不变，零信息损失）；FinalizeAsync 排空循环 `TryTake` 失败分支加 `Task.Delay(1)` 退让；删除 `WaitUntilFedAsync/WaitUntilActivityAsync` 中以 UI 文案 `Status.StartsWith("错误：")` 为故障信号的判定（已有 `ThrowIfFaulted`）。
- [x] A5 `TgaFrameReader`：BGRA 输出改 `ArrayPool<byte>` 租赁，调用方（管线下单序提交 + 排空）在 Native 提交完成后归还；空数组不归还。
- [x] A6 `CaptureEnvelopeRecorder`：`health` 为 null 时不再把 `Task.CompletedTask` 塞进 WhenAny（消除紧转死循环隐患）。
- [x] A7 `ReplayQualityPreviewCaptureService`：成功路径不再 `pipeline = null` 跳过清理；finally 中按「已完成 → DisposeAsync / 失败 → CleanupAsync + DisposeAsync」释放管线（修复每次成功预览泄漏 Native 会话 + watcher 定时器）。
- [x] A8 `NativeBlendSession`：加 finalizer 兜底释放原生会话（防异常路径漏 Dispose 泄漏 D3D/MF 资源）。
- [x] A9 `NativeSessionFactory.MarshalEffects`：合并 `NativeBlendSession.Create` 与 `ObsSynthesisService` 两份逐字段 marshal 拷贝。
- [x] A10 `UserSettingsStore.Save`：临时文件 + `File.Replace` 原子写（防写盘中途损坏导致设置静默重置）。
- [x] A11 `MomentumDirectoryLinkService.CreateJunctionViaMklink`：stderr/stdout 改并行 `ReadToEndAsync`（消除经典进程流死锁模式）。
- [x] A12 `DialogService.EnsureHost`：移除 `Grid is Panel` 之后永不可达的死分支。
- [x] A13 验证：解决方案构建通过 + SmokeTest 全量 0 失败 + `recording` 子命令 0 失败。

## 批次 B —— Native GPU/编码与构建（Mmod.Native）

- [x] B1 `gpu_blend.cpp` 上传/读回逐像素循环改按行 memcpy（BGRA ↔ R32_UINT 小端字节序等价；RowPitch 有 padding 时逐行拷贝）。
- [x] B2 清屏 pass 改 `ClearUnorderedAccessViewUint`，删除 kClearCs/cs_clear。
- [x] B3 GPU 资源创建 HRESULT 全量检查（3 个核心 shader、input_tex/srv、acc_uav、out_uav、out_staging、weight_cb），失败返回 null；`weight_cb` Map 失败改返回 false（不再静默沿用旧权重）。
- [x] B4 shader 字节码按源码指针缓存（进程级、加锁——阶段 2 并行会并发建会话）。
- [x] B5 每输出帧 float4 全帧 CopyResource 4→3：删除首帧两粒种子拷贝（随后必被覆盖）；`work_a ← acc` 种子拷贝消除（acc_tex 加 SRV 绑定，首个 effect 直接读 acc）。
- [x] B6 `session.cpp`：输出帧直写锁定的 MF buffer（`GpuBlendPack` 签名改目标指针；CPU fallback 同步直写），消除 `output_bgra` 中转与一次全帧 memcpy；`stride * height` 改 size_t 防溢出；导出函数加 C ABI 异常屏障（try/catch → MmodError_*）。
- [x] B7 `frame_processing.cpp`：CPU fallback 的 effect 排序缓存到 state（会话内不变，不再每帧分配 + 排序）。
- [x] B8 CMake：加 `/MP`、Release `/GL` + 链接 `/LTCG`、Release PDB（`/Zi` + `/DEBUG`）。
- [x] B9 验证：Native 增量构建通过 + SmokeTest 全量 + `recording`（含 CPU 参考数学对账）0 失败。

## 批次 C —— WPF App 热路径

- [x] C1 `TasksViewModel`：`ReloadTasks` 用 `GetNodesForTasks` 消 N+1；`TaskListItem` 构造时解析并缓存 supersampling（`UpdateRemainingEstimate` 每 tick 不再 JSON 反序列化）；`GetLogs` 调用点传 LIMIT；`UpdateRunnerProjection` 的「停止单拍双重刷新」合并。
- [x] C2 `SettingsPage.xaml`：ShutterAngle Slider 与码率 NumberBox 绑定加 `Delay="300"`（拖动不再每刻度同步写盘 + junction 探测；不改动 Persist 同步语义，模式切换回滚路径不受影响）。
- [x] C3 `ComposeViewModel`：管线 `Changed` 订阅与 OBS 批处理进度回调用 `Dispatcher.Invoke` 改 `InvokeAsync`（消除后台线程同步阻塞 UI 的卡顿/死锁风险）。
- [x] C4 验证：`Mmod.App` 构建通过。UI 实际手感（拖动滑块、任务页运行态）留待用户实机体验，在回执中明确标注。

## 验收矩阵

| 批次 | 命令 | 通过标准 |
|------|------|----------|
| A | `cmake --build src\Mmod.Native\build --config Release`（如被触碰）+ `dotnet build Mmod.App -c Release` | 0 error |
| A/B | `dotnet run --project src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release` | 全部子命令 0 失败 |
| A/B | 同上 `recording` | 13 组状态机测试 0 失败 |
| C | `dotnet build Mmod.App -c Release` | 0 error |

## Git 与停止协议

- 本轮不 commit / 不 push（用户此前授权仅覆盖上一批改动）；完成后由用户决定提交方式。
- 防震荡：同一失败签名连续 3 次、两种策略无净改善、单命令 60 秒无新输出（构建/Smoke 用 10 分钟超时上限）、需要越过白名单 —— 立即停止并保存现场到本计划。
