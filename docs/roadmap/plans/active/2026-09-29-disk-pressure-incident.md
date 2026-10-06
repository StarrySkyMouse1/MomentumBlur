# TGA DiskPressure 反复失败现场（2026-09-29）

## 当前状态

- **状态**：已按用户要求回退；Core/Native 录制与合成实现恢复到提交 `d74214e`，仅保留 UI 交互优化和所需只读查询兼容层。
- **任务**：`8aaa44f3eb3444f1aa828bd4f14fe585`，节点 2，回放 33.93 秒，60x 超采样。
- **最后失败**：Attempt 5 / `8d79dd89c5b04210b6bfe3bb901b7665`，DiskPressure。
- **保护**：不提交、不清理用户工作区、不修改测试、不再调整阈值或等待时间。

## 已确认事实

- Attempt 5 使用最新 Debug DLL，包含 Candidate 基线、单次纠偏和串行文件所有权修复。
- 初始：`fps_max=60`，Candidate 基线 5，增长阈值 120。
- 11:09:04 Candidate 126，触发一次纠偏；11:09:06 `60 -> 54`。
- 11:09:15 磁盘预测触发 `54 -> 33`，剩余 24.5% / 1.471 GiB。
- 11:37:08 在 `fps_max=33` 下，滚动速率 Produced=32.7、Consumed=32.7，但 Candidate 已增至 302；随后 `33 -> 24`。
- 11:37:09 剩余从 19.5% 快速跨到 9.8%，触发 Critical；受控停止耗时 2.617 秒。
- 最终 Submitted=55620、Output=927、15.45 秒，未达到 35.93 秒完整下限。
- 失败不是完整性判定误报：实际只录到回放约一半。

## 已尝试且不能继续重复的策略

1. 只在 15% 警告线降速：命令往返期间跨过 10% 安全线，失败。
2. 瞬时磁盘消耗预测提前降速：RAM 盘延迟分配使预测跳变，仍失败。
3. Candidate 绝对值连续降速：形成消费速率反馈塌缩，最终降到 10 fps，已撤销。
4. Candidate 相对基线一次纠偏 + 磁盘预测：避免反馈塌缩，但单向降速仍不能排空长期微小生产差，Attempt 5 失败。

## 根因结论

- 当前非 Hook 流程只能通过 `fps_max` 调节生产速率，不能像 SVR Hook 一样在每帧生产边界阻塞游戏，等待消费者完成。
- 生产与消费即使只差约 0.3 fps，长时间 60x 录制也会累计数百张约 6 MiB 的 TGA，6 GiB RAM 盘最终必然耗尽。
- 只允许单向降低 `fps_max` 不是闭环流量控制；必须加入有滞回的“暂停/排空/恢复”状态，否则无法给有限磁盘容量提供稳定上界。

## 已确认实施方向

- 实现非 Hook 的滞回式生产控制：
  - 队列达到高水位时把 `fps_max` 临时降到排空档（建议 1 fps，而不是继续按比例递减）；
  - watcher Candidate + Pending 回落到低水位并保持稳定后，恢复到固定工作档；
  - 高低水位固定基于初始配置，禁止随当前 fps 自缩；
  - 磁盘安全线仍只承担最终保险，不参与日常速率反馈；
  - 每次 EnterDrain / DrainProgress / ExitDrain / DrainTimeout 持久化完整原因、队列、空间和速率。
- 这会改变录制运行时行为和耗时模型，属于架构级修复，不再作为阈值微调继续试验。

## 实施清单

- [x] 将单向比例降速替换为固定高低水位的排空状态机。
- [x] 低水位连续稳定后恢复任务冻结的原始速率，不把低速排空档当作工作档。
- [x] 持久化 EnterDrain / DrainProgress / ExitDrain / DrainTimeout 及完整诊断字段。
- [x] 同步 README 行为说明并完成代码自审。
- [x] 运行现有 Release 构建、默认 Smoke 与 recording Smoke；实机长录制另行验收。

## 本轮验证证据

- `dotnet build MomentumBlur.slnx -c Release /p:SkipNativeBuild=true`：0 warning / 0 error。
- 默认 Smoke：`OK size=10654 bytes progress=(30, 30)`。
- recording Smoke：`RECORDING_OK`。
- 停止旧 Debug 应用 PID 58220 后，`dotnet build src\Mmod.App\Mmod.App.csproj -c Debug /p:SkipNativeBuild=true`：0 warning / 0 error。
- `git diff --check`：无空白错误；仅报告仓库既有的 LF/CRLF 转换提示。
- 未完成：真实 Momentum 回放尚未再次运行，因此尚无 EnterDrain → DrainProgress → ExitDrain 的实机日志闭环证据。

## 2026-09-30 重复自动暂停现场

- Attempt 1 与 Attempt 2 均在输出边界样本恰好 440 时进入 `UserCanceled`，没有出现 `BackpressureEnterDrain`、DiskPressure、PipelineFault 或 GameExited。
- 旧日志把工具栏 `StopNow` 与内部 `RenderTaskRunner.DisposeAsync` 都归类成“用户取消”，无法证明真实调用者。
- 已增加取消前持久化 `ImmediateStopRequested`：记录 Origin、进程、托管线程、Runner 状态与调用栈；工具栏明确标记 `TasksPage.StopNowCommand`，内部释放明确标记 `RenderTaskRunner.DisposeAsync`。
- 本轮不调整背压、磁盘阈值或录制速率；下一次若再次自动暂停，以首条 `ImmediateStopRequested` 为调用来源证据。

## 2026-09-30 回退决定

- 新一轮实机记录为 `CancellationWithoutStopRequest`，证明取消并非工具栏或 Runner Dispose 明确请求。
- 用户决定不再沿当前优化实现继续排查，要求合成算法和后台实现恢复至上一个提交版本，同时保留 UI/交互优化。
- 已恢复全部 Core 录制/合成、Native GPU/编码、背压/超时及后台服务文件至 `HEAD d74214e`。
- 保留 App 层 UI 改动；`RenderTaskRepository` 仅保留批量节点读取和限量日志读取两个只读兼容接口，供任务页 UI 降低查询负担，不改变录制或合成行为。

## 最后成功证据

- Release 完整构建：0 warning / 0 error。
- 默认 Smoke：通过。
- recording Smoke：`RECORDING_OK`。
- Debug 构建：0 warning / 0 error。
- 实机证据：Attempt 5 未通过，以上自动化结果不代表真实长录制验收。

