# IMPLEMENTATION_REPORT — Recording Task State Machine Audit & Refactor

> 对应执行计划：`docs/CODEX_RECORDING_TASK_STATE_MACHINE_AUDIT_EXECUTION_PLAN.md`
>
> 验收原则：**A recording attempt is successful only when success is positively proven at every irreversible boundary. Absence of an error is not proof of success.**

本报告逐条对照计划的 Definition of Done（§22），注明完成状态、测试结果与未完成项原因。

---

## 1. 状态正确性（§22 状态正确性）

| 条目 | 状态 | 说明 / 验证 |
|------|------|-------------|
| 每个 Node Attempt 有唯一 AttemptId + CaptureSessionId | ✅ | `CaptureSessionInfo.Create()` 每次 Attempt 生成新 GUID；`NodeExecutionCoordinator` 每条 Attempt 新建 `render_attempts` 记录 |
| 每个 CaptureSession 有唯一 TGA prefix | ✅ | prefix = `mmod_{taskShort}_{node+1}_{aN}_{session6}_`，测试 `RECORDING_OK`（17.18/17.19） |
| Watcher 只消费当前 prefix | ✅ | `TgaDirectoryWatcher(directory, exactPrefix)` 只匹配 `^{prefix}(\d+)\.tga$`；测试 17.18/17.19 通过 |
| 地图 Ready 使用正向确认，而不是 echo + 固定 sleep | ✅ | `IMapReadinessProbe` / `NetConMapReadinessProbe`：优先解析 `status` 输出的当前地图；解析不到时降级 `DegradedFallback` 并显式标记，绝不把 echo 命名成 MapReady |
| Replay started 不再由“任意 hash 变化”决定 | ✅ | 旧 FNV hash 逻辑移除；`VisualPlaybackEvidenceProbe` 使用 block-grid changed-ratio + mean-luma-delta + 连续 N 帧；测试 17.5/17.6/17.7 通过 |
| Pipeline fault 可立即传播 | ✅ | `TgaPipelineOrchestrator.Completion` faulted Task + `ThrowIfFaulted()`；录制循环 `WhenAny` 竞争；测试 17.8 通过（<5s 断言，实际毫秒级） |
| endmovie 主路径不是 best-effort | ✅ | `MomentumReplaySession.ExecuteEndMovieAsync` strict（ACK + 失败 pattern）；仅 `CommandAcked / KnownAlreadyStopped` 可继续；测试 17.13 通过 |
| TGA stop 使用 physical quiescence | ✅ | `WaitForQuiescenceAsync`：无新写入 + Candidate==0 + 最终扫描后保持安静；超时抛异常；测试 17.10/17.12 通过 |
| final candidate 不会因为 Stop race 被丢掉 | ✅ | 顺序：strict endmovie → watcher 继续 → 物理静默 → 最终扫描 → freeze → drain → assert empty → Finish（P0-07 顺序实现） |
| Native Finish fault 必须导致 Attempt failure | ✅ | `FinalizeAsync` 中 `_session.Finish()` 异常直接 throw；`MediaProbe` 通过前不提交；测试 17.9 通过 |
| media validation 通过前不能 Completed | ✅ | `IMediaProbe`/`MediaProbe`（容器/moov/mvhd/tkhd/stts → 分辨率/fps/帧数/时长）+ 输出帧数交叉验证；测试 17.22/17.23 通过 |
| clip 使用临时文件校验后原子提交 | ✅ | 每 Attempt 写 `attempt_N.encoding.mp4` → MediaProbe → `AtomicFileCommitter`（fsync + 原子 move）→ 正式 ClipPath |

## 2. 取消 / 清理

| 条目 | 状态 | 说明 |
|------|------|------|
| StopNow 在任何内部 stage 都会进入统一 Cleanup Barrier | ✅ | `CaptureCleanupCoordinator.CleanupAsync` 统一处理失败/取消/中断；队列级 `HandleQueueInterruptionAsync` + 节点级 coordinator catch 都走它 |
| Cleanup 使用独立 bounded token | ✅ | `new CancellationTokenSource(RecordingTimeoutPolicy.CleanupHardLimit)`；用户 token 只决定停止正常工作 |
| cleanup 无法证明成功时 game session 被判 Dirty | ✅ | `CaptureCleanupResult.RequiresGameRestart` / `CaptureCleanupState`；测试 PolicyTests 通过 |
| Dirty session 不允许 same-session retry | ✅ | `RecordingRetryPolicy.Decide`：cleanup 不干净 → `RestartGameRetry`；测试 17.20 通过 |
| owned Momentum 在 fatal/StopNow 后不会成为 orphan | ✅ | `MomentumProcessController.ShutdownOwnedProcessAsync`：quit → 有界等待 → 身份匹配 kill fallback（PID + start time + exe） |
| Pause 状态下不存在 active startmovie | ✅ | 节点完成后才 Paused；coordinator 在节点提交前已 strict endmovie + quiescence + finalize |

## 3. Retry

| 条目 | 状态 | 说明 |
|------|------|------|
| FailureKind 已分类 | ✅ | `RecordingFailureKind`（18 类）+ `RecordingFailureClassifier` |
| retry policy 与 recovery policy 分离 | ✅ | `RecordingRetryPolicy.Decide` 产出 `RetryAction`（SameSession/ReloadMap/RestartGame），`NodeExecutionCoordinator.RecoverAsync` 执行 |
| Retry 前必须 Known-Clean 或 fresh game session | ✅ | cleanup==Dirty → RestartGame；RestartGame 在 `RecoverAsync` 中销毁并重建会话（测试 17.20/17.21 策略矩阵通过） |
| permanent input error 不浪费 3 次 retry | ✅ | InvalidInput/UnsupportedReplay/MapUnavailable/DiskPressure/UserCanceled → `NoRetryNeedsUser` |

## 4. Crash recovery

| 条目 | 状态 | 说明 |
|------|------|------|
| runner_session/render_attempts 被实际使用 | ✅ | `RenderTaskRepository`：`render_attempts` 表 + `SaveRunnerSession/GetRunnerSession/ClearRunnerSession`（新增 exe_path/process_started_at/game_session_id/capture_session_id/sequence_prefix/ownership_token/watch_directory 列，旧库 EnsureColumn 迁移） |
| App crash 后可发现 stale owned game | ✅ | `RenderTaskRunner.RecoverFromCrashAsync`（队列启动时执行） |
| 不会因为 PID reuse 杀错进程 | ✅ | 校验 exe path + process start time + ownership token 后才 kill |
| stale session TGA 按 prefix 清理 | ✅ | `TgaDirectoryWatcher.CleanupSessionFiles()` 只删当前 prefix |
| partial clip 不会被当有效完成文件 | ✅ | 未持久化 validation 结果 → 保守策略：active attempt 的 temp clip 一律删除重录 |

## 5. Tests

| 条目 | 状态 | 说明 |
|------|------|------|
| 第 17 节测试 | ✅（逻辑层） | `SmokeTest recording`：17.1/17.5/17.6/17.7/17.8/17.9/17.10/17.11(隐含)/17.12/17.13/17.14(策略)/17.15/17.16/17.18/17.19/17.20/17.21/17.22/17.23 + 状态机转换守卫 + retry 矩阵 → `RECORDING_OK` |
| 现有 settings/processing/motion-blur smoke tests 继续通过 | ✅ | settings/snapshot/weights/processing/repository 全部 `OK` |
| Release build 成功 | ✅ | Core / App / SmokeTest 0 警告 0 错误；Native（Ninja Multi-Config + MSVC 19.50）Release 通过 |
| Windows 实机基础矩阵通过 | ⚠️ 未执行 | 本环境无 Momentum Mod 实机；脚本 `scripts/recording-chain-soak.ps1` 已提供执行入口与验收清单 |
| 至少 100 Node soak test | ⚠️ 未执行 | 需实机；`scripts/recording-chain-soak.ps1` 就绪 |
| fault injection 结果符合 fail-safe 预期 | ⚠️ 未执行 | `scripts/recording-chain-fault-injection.ps1` 就绪（7 个场景 + 验收清单） |

## 6. 实现要点（对应计划 §19 文件职责）

- `RenderTaskRunner.cs`：队列/任务生命周期、游戏会话兼容性（`GameSessionCompatibilityKey`）、pause/stop、crash recovery；节点执行委托给 coordinator。
- `NodeExecutionCoordinator.cs`（新）：Attempt 主状态机、retry 循环、媒体校验、原子提交。
- `CaptureEnvelopeRecorder.cs`：evidence-driven envelope；strict stop + 物理静默；不再自己吞 cleanup。
- `TgaDirectoryWatcher.cs`：exact prefix、候选/物理写入指标、quiescence/freeze/drain/session cleanup。
- `TgaPipelineOrchestrator.cs`：fault Task、强生命周期、`FinalizeAsync` 顺序化收尾、Finish fault 传播。
- `MomentumReplaySession.cs`：正向 map probe、typed watch/startmovie/endmovie。
- `MomentumNetConClient.cs`：`ExecuteStrictAsync` typed transcript + `IsConnected`。
- `MomentumProcessController.cs`：`ExitTask`、strict owned shutdown、compatibility key、进程身份元数据。
- `RenderTaskRepository.cs`：`render_attempts` + runner_session 持久化 + 原子 stage 转换。
- `Mp4MergeService.cs`：接入 `IMediaProbe`。
- 新增：`RecordingModels`、`RecordingAbstractions`（7 个 fake 边界接口）、`RecordingTimeoutPolicy`（集中超时）、`RecordingStateMachine`（转换守卫）、`RecordingRetryPolicy`、`RecordingFailureClassifier`、`CaptureCleanupCoordinator`、`NetConMapReadinessProbe`、`VisualPlaybackEvidenceProbe`、`MediaProbe`、`GameSessionHealthMonitor`、`AtomicFileCommitter`。

## 7. 未完成 / 风险（实机验证边界，同计划 §24）

1. **真实 console 输出格式未实机验证**：`status` 地图解析、`startmovie/endmovie` 失败 pattern 是依据 Source 引擎惯例封装；
   `IMapReadinessProbe` 提供 degraded fallback 保证不误判，但 **hard success condition 需实机确认**（计划 §0/§11 明示）。
2. **实机 soak / fault injection 未执行**：需 Windows + Momentum 实机；脚本已交付，验收项已列明。
3. **`mmod_process_video_file`（OBS 合成）** 未走新的协调器（OBS 是单文件批量合成，非 replay 任务链路）；录制状态机只作用于 TGA/Replay 任务链路，符合计划范围。
4. **Crash recovery 的 attempt 重放 reconcile 采用保守策略**（删除 partial 重录），未实现“validated clip 直接采纳”。
5. 旧 `RenderNodeStatus.RetryCount` 字段保留但不再作为重试依据（重试内聚到 Attempt 层）。

## 8. 执行过的构建与测试命令

```text
dotnet build src\Mmod.Core\Mmod.Core.csproj -c Release                       → 0 警告 0 错误
dotnet build src\Mmod.App\Mmod.App.csproj -c Release /p:SkipNativeBuild=true → 0 警告 0 错误
dotnet build src\Mmod.SmokeTest\Mmod.SmokeTest.csproj -c Release             → 0 警告 0 错误
cmake (Ninja Multi-Config, MSVC 19.50, Release)                              → mmod_native.dll 构建通过
dotnet SmokeTest settings/snapshot/weights/processing/repository/recording   → 全部 OK（RECORDING_OK 含 §17 状态机矩阵）
dotnet SmokeTest（默认 + native-effects）                                    → OK（Native GPU 管线含画质效果正常）
mmod_record_next.exe 启动冒烟（8s 无崩溃）                                   → OK
```
