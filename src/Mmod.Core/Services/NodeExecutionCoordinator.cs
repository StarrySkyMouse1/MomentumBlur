namespace Mmod.Core.Services;

using System.IO;
using Mmod.Core.Models;

/// <summary>Inputs for executing one node with the attempt state machine.</summary>
public sealed record NodeExecutionContext(
    RenderTaskRecord Task,
    RenderNodeRecord Node,
    RenderSettingsSnapshot Settings,
    ReplayRecord Replay,
    string WorkDirectory,
    string StableClipPath,
    IGameProcessController Game,
    RenderTaskRepository Repository,
    IMediaProbe MediaProbe,
    CaptureCleanupCoordinator CleanupCoordinator,
    RecordingTimeoutPolicy Timeouts,
    Action<string, string?> Log,
    Action<string>? Phase,
    Action<RenderNodeRecord>? OnNodeStatusChanged,
    Action<DiskHealthSnapshot?, PerformanceSnapshot>? Telemetry = null);

/// <summary>Node execution failed after retry policy exhaustion.</summary>
public sealed class NodeExecutionFailedException : Exception
{
    public NodeExecutionFailedException(RecordingFailureKind kind, Exception inner)
        : base($"节点执行失败（{kind}）：{inner.Message}", inner)
    {
        FailureKind = kind;
    }

    public RecordingFailureKind FailureKind { get; }
}

/// <summary>
/// Per-node attempt state machine: creates a unique CaptureSessionId + TGA
/// prefix per attempt, drives ChangeMap → pipeline → envelope → media probe →
/// atomic commit, classifies failures, runs the unified cleanup barrier, and
/// applies the retry/recovery policy. No irreversible boundary is crossed
/// without positive proof.
/// </summary>
public sealed class NodeExecutionCoordinator
{
    private ICapturePipeline? _activePipeline;
    private string? _privateLobbyGameSessionId;
    private string? _readyGameSessionId;
    private string? _readyMap;
    private string? _readyNodeId;
    private bool _forceMapReload;

    public async Task<string> ExecuteNodeAsync(NodeExecutionContext ctx, CancellationToken token)
    {
        var taskId = ctx.Task.Id;
        var nodeId = ctx.Node.Id;
        var nodeDir = Path.Combine(ctx.WorkDirectory, $"node_{ctx.Node.Sequence + 1:D3}");
        Directory.CreateDirectory(nodeDir);

        // A DiskPressure controlled stop may have produced a fully complete,
        // media-validated clip before the older completion classifier rejected
        // it. Re-check retained Validated partials before spending another full
        // replay pass. The source partial remains untouched as recovery evidence;
        // only a verified copy is atomically promoted to the node clip.
        var recoveredClip = TryRecoverCompleteValidatedPartial(ctx, nodeDir);
        if (recoveredClip is not null)
            return recoveredClip;

        // attempt_number is an audit sequence across the lifetime of the node,
        // while MaxAttempts is the budget for this explicit execution/resume.
        // Using the lifetime count as the loop bound made a failed node with
        // three historical attempts impossible to retry from “开始 / 继续”.
        var attemptNumber = ctx.Repository.GetAttemptsForNode(taskId, nodeId).Count + 1;
        var attemptIndex = 1;
        string? lastError = null;
        RecordingFailureKind? lastKind = null;

        while (attemptIndex <= ctx.Timeouts.MaxAttempts)
        {
            var captureSession = CaptureSessionInfo.Create(taskId, ctx.Node.Sequence, attemptNumber);
            var attemptId = Guid.NewGuid().ToString("N");
            var tempClip = Path.Combine(nodeDir, $"attempt_{attemptNumber}_{captureSession.CaptureSessionId[..6]}.encoding.mp4");
            // M4: deterministic partial path inside the node work directory;
            // independent from the formal ClipPath and never overwrites it.
            var partialClip = Path.Combine(nodeDir, $"attempt_{attemptNumber}_{captureSession.CaptureSessionId[..6]}.partial.mp4");

            var attempt = new RenderAttemptRecord(
                Id: attemptId,
                SessionId: captureSession.CaptureSessionId,
                TaskId: taskId,
                NodeId: nodeId,
                AttemptNumber: attemptNumber,
                Stage: NodeExecutionStage.Created,
                SequencePrefix: captureSession.SequencePrefix,
                TempClipPath: tempClip,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                FinishedAt: null,
                LastError: null,
                FailureKind: null,
                CleanupState: CaptureCleanupState.NotRequired,
                GameProcessId: ctx.Game.ProcessId,
                GameProcessStartedUtc: ctx.Game.ProcessStartTimeUtc,
                NetConPort: null,
                ExpectedMap: ctx.Task.MapName,
                FedCount: 0,
                SubmittedFrameCount: 0,
                LastTgaIndex: null);
            ctx.Repository.CreateAttempt(attempt);

            ctx.Repository.SaveRunnerSession(new RunnerSessionRecord(
                ProcessId: ctx.Game.ProcessId,
                NetConPort: null,
                NetConPassword: null,
                TaskId: taskId,
                NodeId: nodeId,
                ExePath: ctx.Game.ExePath,
                ProcessStartedAt: ctx.Game.ProcessStartTimeUtc,
                GameSessionId: ctx.Game.GameSessionId,
                CaptureSessionId: captureSession.CaptureSessionId,
                SequencePrefix: captureSession.SequencePrefix,
                OwnershipToken: attemptId,
                WatchDirectory: ctx.Settings.WatchDirectory));

            void SetStage(NodeExecutionStage stage)
            {
                if (stage != attempt.Stage)
                {
                    RecordingStateMachine.AssertTransition(attempt.Stage, stage);
                    if (!ctx.Repository.TryTransitionAttemptStage(attemptId, attempt.Stage, stage, attempt.FedCount, attempt.SubmittedFrameCount, attempt.LastTgaIndex))
                        throw new InvalidOperationException($"数据库 stage 转换被拒：{attempt.Stage} → {stage}");
                    attempt = attempt with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow };
                }
            }

            try
            {
                SetStage(NodeExecutionStage.Preflight);
                var clip = await ExecuteAttemptAsync(ctx, attempt, captureSession, tempClip, SetStage, token);
                SetStage(NodeExecutionStage.Completed);
                ctx.Repository.CompleteAttempt(attemptId, attempt.FedCount, attempt.SubmittedFrameCount, attempt.LastTgaIndex);
                return clip;
            }
            catch (OperationCanceledException)
            {
                lastKind = RecordingFailureKind.UserCanceled;
                lastError = "用户取消。";
                await CleanupAndRecordFailureAsync(ctx, attempt, CleanupReason.UserCanceled, lastKind.Value, lastError, token);
                throw;
            }
            catch (Exception ex)
            {
                lastKind = RecordingFailureClassifier.Classify(ex);
                lastError = ex.Message;
                ctx.Log(
                    "Error",
                    BuildAttemptFailureDiagnostics(
                        attempt,
                        lastKind.Value,
                        tempClip,
                        partialClip,
                        ex));

                // M4: DiskPressure with a proven controlled stop → validate the
                // partial, commit it atomically to the independent partial path,
                // and persist it as Validated. Any failure in this chain leaves
                // the attempt with no validated partial (conservative cleanup).
                if (ex is DiskPressureException { ControlledStop: { } controlled } && !string.IsNullOrWhiteSpace(partialClip))
                {
                    try
                    {
                        PersistValidatedPartial(ctx, attempt, tempClip, partialClip, controlled);
                    }
                    catch (Exception partialEx)
                    {
                        ctx.Log("Warning", $"DiskPressure partial 保存失败（不产生 Validated）：{partialEx.Message}");
                        TryDelete(partialClip);
                    }
                }

                using var cleanupCts = new CancellationTokenSource(ctx.Timeouts.CleanupHardLimit);
                var cleanup = await ctx.CleanupCoordinator.CleanupAsync(
                    _activePipeline,
                    ctx.Game as MomentumProcessController,
                    CleanupReason.Failed,
                    cleanupCts.Token);
                _activePipeline = null;

                ctx.Repository.UpdateAttemptFailure(attemptId, lastKind, lastError, cleanup.CleanupState);
                foreach (var secondary in cleanup.SecondaryErrors)
                    ctx.Log("Warning", $"清理次级错误：{secondary}");

                var decision = RecordingRetryPolicy.Decide(
                    lastKind.Value, attemptIndex, ctx.Timeouts.MaxAttempts,
                    cleanup.CleanupState == CaptureCleanupState.Clean);

                ctx.Log("Warning",
                    $"Attempt {attemptNumber} 失败：kind={lastKind} cleanup={cleanup.CleanupState} retry={decision.Action} reason={decision.Reason}\n{lastError}");

                if (decision.Action == RetryAction.NoRetryNeedsUser)
                {
                    MarkNodeFailed(ctx, lastKind.Value, lastError);
                    TryDelete(tempClip);
                    throw new NodeExecutionFailedException(lastKind.Value, ex);
                }

                // Recovery before the next attempt.
                await RecoverAsync(ctx, decision, token);
                TryDelete(tempClip);
                attemptNumber++;
                attemptIndex++;
            }
        }

        MarkNodeFailed(ctx, lastKind ?? RecordingFailureKind.Unknown, lastError ?? "超出最大尝试次数");
        throw new NodeExecutionFailedException(lastKind ?? RecordingFailureKind.Unknown,
            new Exception(lastError ?? "超出最大尝试次数"));
    }

    private async Task<string> ExecuteAttemptAsync(
        NodeExecutionContext ctx,
        RenderAttemptRecord attempt,
        CaptureSessionInfo captureSession,
        string tempClip,
        Action<NodeExecutionStage> setStage,
        CancellationToken token)
    {
        var timeouts = ctx.Timeouts;

        // Preflight.
        if (!ctx.Replay.IsCompatible)
            throw new RecordingStageException(RecordingFailureKind.UnsupportedReplay,
                $"回放格式不兼容：{ctx.Replay.CompatibilityIssue}");
        if (string.IsNullOrWhiteSpace(ctx.Settings.GameRootPath) || !Directory.Exists(ctx.Settings.GameRootPath))
            throw new RecordingStageException(RecordingFailureKind.InvalidInput, "游戏根目录不存在。");
        if (string.IsNullOrWhiteSpace(ctx.Settings.WatchDirectory) || !Directory.Exists(ctx.Settings.WatchDirectory))
            throw new RecordingStageException(RecordingFailureKind.InvalidInput, "TGA 监视目录不存在。");

        // 严格闸门（每次 attempt 复检）：链接目录必须已创建且指向内存盘，且实际监视
        // 目录必须是内存盘链接目标；未链接时立即失败，严禁直接在磁盘上进行录制。
        try
        {
            var attemptUser = RenderTaskRunner.ToUserSettingsForAttempt(ctx.Settings);
            var effectiveWatch = WatchDirectoryHelper.ResolveEffectiveWatchDirectory(attemptUser, attemptUser.GameRootPath);
            MomentumDirectoryLinkService.EnsureCaptureTargetOnRam(
                ctx.Settings.GameRootPath, ctx.Settings.WatchDirectory, effectiveWatch);
        }
        catch (InvalidOperationException linkEx)
        {
            throw new RecordingStageException(RecordingFailureKind.InvalidInput, linkEx.Message, linkEx);
        }

        // Ensure owned game session.
        setStage(NodeExecutionStage.EnsuringGameSession);
        if (!ctx.Game.IsGameRunning)
        {
            ctx.Log("Info", "启动 Momentum 游戏会话…");
            await ctx.Game.StartAsync(ctx.Settings.GameRootPath, token);
        }
        else if (!ctx.Game.NetCon.IsConnected)
        {
            throw new RecordingStageException(RecordingFailureKind.NetConLost, "NetCon 已断开且游戏会话仍在。");
        }

        setStage(NodeExecutionStage.ConnectingNetCon);
        if (!ctx.Game.NetCon.IsConnected)
            throw new RecordingStageException(RecordingFailureKind.NetConLost, "NetCon 未连接。");

        var gameSessionChanged = !string.Equals(
            _readyGameSessionId,
            ctx.Game.GameSessionId,
            StringComparison.Ordinal);

        // Initialize one invite-only lobby per game process session. Repeating
        // this for every node needlessly destroys/recreates the same lobby and
        // may race the next replay command.
        if (!string.Equals(_privateLobbyGameSessionId, ctx.Game.GameSessionId, StringComparison.Ordinal))
        {
            await MomentumReplaySession.EnsurePrivateSoloLobbyAsync(
                ctx.Game.NetCon,
                l => ctx.Log("Info", l),
                token);
            _privateLobbyGameSessionId = ctx.Game.GameSessionId;
        }
        else
        {
            ctx.Log("Info", "PrivateSoloLobbyReuse：复用当前游戏会话的私人单人大厅。");
        }

        // A new node must reload the map even when it belongs to the same
        // staged task. Momentum can leave the previous TV replay at its end
        // state; a subsequent mom_tv_replay_watch may be acknowledged without
        // actually restarting playback. Reuse is therefore limited to the
        // same node/attempt context only.
        var mapChanged = !string.Equals(_readyMap, ctx.Task.MapName, StringComparison.OrdinalIgnoreCase);
        var nodeChanged = !string.Equals(_readyNodeId, ctx.Node.Id, StringComparison.Ordinal);
        // Keep the persisted state-machine path explicit even when the gate
        // reuses an already-proven map; these stages mean "resolve/confirm map
        // readiness", not necessarily "always issue a map command".
        setStage(NodeExecutionStage.ChangingMap);
        setStage(NodeExecutionStage.WaitingMapReady);
        if (gameSessionChanged || mapChanged || nodeChanged || _forceMapReload)
        {
            try
            {
                await MomentumReplaySession.ChangeMapAsync(
                    ctx.Game.NetCon,
                    ctx.Task.MapName,
                    l => ctx.Log("Info", l),
                    token,
                    timeouts: timeouts);
            }
            catch (TimeoutException ex)
            {
                throw new RecordingStageException(
                    RecordingFailureKind.MapReadinessTimeout,
                    ex.Message,
                    ex);
            }

            _readyGameSessionId = ctx.Game.GameSessionId;
            _readyMap = ctx.Task.MapName;
            _readyNodeId = ctx.Node.Id;
            _forceMapReload = false;
            ctx.Log("Info", $"MapReady：{ctx.Task.MapName}");
        }
        else
        {
            ctx.Log("Info", $"MapReadyReuse：复用当前游戏会话中的地图 {ctx.Task.MapName}，不重复执行 map。");
        }

        // Build per-attempt user settings (prefix applied by the pipeline).
        var user = RenderTaskRunner.ToUserSettingsForAttempt(ctx.Settings);
        var relative = MomentumReplaySession.BuildGameRelativeReplayPath(ctx.Settings.GameRootPath, ctx.Node.ReplayPath);

        var hostFps = Math.Max(1, user.SupersamplingMultiplier) * ProjectConstants.FinalOutputFramerate;
        var pipeline = new TgaPipelineOrchestrator(timeouts);
        pipeline.Changed += () => ctx.OnNodeStatusChanged?.Invoke(ctx.Node);
        var health = new GameSessionHealthMonitor(ctx.Game as MomentumProcessController ?? throw new InvalidOperationException("需要 MomentumProcessController 健康监控"), ctx.Settings.WatchDirectory);
        var captureEnvironment = new CaptureConVarScope(ctx.Game.NetCon, ctx.Log);

        try
        {
            var foregroundFpsLimit = SettingsMigration.NormalizeForegroundCaptureFpsLimit(user.ForegroundCaptureFpsLimit);
            await captureEnvironment.ApplyAsync(
                hostFps,
                foregroundFpsLimit == 0 ? null : foregroundFpsLimit,
                token);
            ctx.Log("Info", $"录制环境已应用：超采样 {user.SupersamplingMultiplier}x → host_framerate {hostFps} → 成片 {ProjectConstants.FinalOutputFramerate}fps");

            ctx.Log("Info", ctx.Settings.HideHud
                ? "录制 HUD：隐藏（cl_drawhud 0，包含顶部回放控制条）"
                : "录制 HUD：显示（cl_drawhud 1）");
            await ctx.Game.NetCon.ExecuteAsync(
                ctx.Settings.HideHud ? "cl_drawhud 0" : "cl_drawhud 1",
                TimeSpan.FromSeconds(10),
                token);

            setStage(NodeExecutionStage.PreparingCaptureBaseline);
            await pipeline.StartAsync(user, tempClip, captureSession, acceptPreSessionFiles: false);
            _activePipeline = pipeline;

            var result = await CaptureEnvelopeRecorder.RecordAsync(
                ctx.Game.NetCon,
                pipeline,
                user,
                relative,
                ctx.Replay.RunTimeSeconds,
                health,
                ctx.Phase,
                entry => WriteStructuredLog(ctx, entry),
                token,
                timeouts,
                setStage,
                ctx.Telemetry);
            _activePipeline = null;

            // Media validation: real counters vs output file.
            setStage(NodeExecutionStage.ValidatingClip);
            var probe = ctx.MediaProbe.Probe(tempClip, expectedFps: ProjectConstants.FinalOutputFramerate);
            if (!probe.IsValid)
                throw new RecordingStageException(RecordingFailureKind.MediaValidationFault, $"媒体校验失败：{probe.Error}");

            if (result.FirstFrameWidth > 0 && probe.Width > 0 &&
                (probe.Width != result.FirstFrameWidth || probe.Height != result.FirstFrameHeight))
            {
                throw new RecordingStageException(RecordingFailureKind.MediaValidationFault,
                    $"分辨率不一致：clip={probe.Width}x{probe.Height} 源TGA={result.FirstFrameWidth}x{result.FirstFrameHeight}");
            }

            var expectedDuration = result.ProducedFrames / (double)ProjectConstants.FinalOutputFramerate;
            var tolerance = Math.Max(1.5, expectedDuration * 0.05);
            if (Math.Abs(probe.DurationSeconds - expectedDuration) > tolerance)
            {
                throw new RecordingStageException(RecordingFailureKind.MediaValidationFault,
                    $"时长不符：clip={probe.DurationSeconds:0.###}s 期望≈{expectedDuration:0.###}s（输出帧 {result.ProducedFrames}）");
            }

            // The replay metadata is an independent lower-bound proof. A
            // visually static/stale replay state can otherwise satisfy the
            // internal frame-count check with a tiny but self-consistent clip.
            var minimumReplayDuration = Math.Max(0.5, ctx.Replay.RunTimeSeconds * 0.8);
            if (probe.DurationSeconds < minimumReplayDuration)
            {
                _forceMapReload = true;
                throw new RecordingStageException(
                    RecordingFailureKind.MediaValidationFault,
                    $"阶段成片明显不完整：clip={probe.DurationSeconds:0.###}s，" +
                    $"回放元数据={ctx.Replay.RunTimeSeconds:0.###}s，最低要求={minimumReplayDuration:0.###}s。");
            }

            attempt = attempt with
            {
                FedCount = result.SubmittedFrames,
                SubmittedFrameCount = result.SubmittedFrames,
                LastTgaIndex = result.LastFrameIndex,
            };

            // Atomic commit: temp → stable clip.
            setStage(NodeExecutionStage.CommittingClip);
            AtomicFileCommitter.Commit(tempClip, ctx.StableClipPath);
            ctx.Log("Info", $"AtomicCommit：{ctx.StableClipPath}");

            return ctx.StableClipPath;
        }
        finally
        {
            _activePipeline = null;
            ctx.Log("Info", pipeline.MotionDiagnosticsSummary);
            try
            {
                await pipeline.DisposeAsync();
            }
            finally
            {
                // Environment restoration must run even if pipeline disposal
                // surfaces a late encoder/cleanup failure.
                await captureEnvironment.RestoreAsync();
            }
            if (ctx.Settings.HideHud && ctx.Game.NetCon.IsConnected)
            {
                try
                {
                    using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await ctx.Game.NetCon.ExecuteAsync("cl_drawhud 1", TimeSpan.FromSeconds(10), restoreCts.Token);
                    ctx.Log("Info", "录制 HUD：已恢复显示（cl_drawhud 1）");
                }
                catch (Exception ex)
                {
                    // Restoring an attached user's display is best effort and
                    // must not replace the primary recording result/failure.
                    ctx.Log("Warning", $"录制结束后恢复 HUD 失败：{ex.Message}");
                }
            }
        }
    }

    private void WriteStructuredLog(NodeExecutionContext ctx, RecordingLogEntry entry)
    {
        ctx.Log("Info",
            $"[{entry.Stage}] {entry.Event} Fed={entry.FedCount} Candidate={entry.CandidateCount} Pending={entry.PendingCount} " +
            $"LastWrite={entry.LastPhysicalWriteUtc?.ToString("HH:mm:ss.fff") ?? "-"} {entry.Message}");
    }

    /// <summary>
    /// M4: four-step partial persistence protocol (M4-B-001):
    ///   1. media-validate the temp output (real facts, never file-exists-only);
    ///   2. persist PartialState.Pending with the target path + reason;
    ///   3. atomically move temp → deterministic partial path (never ClipPath);
    ///   4. transition Pending → Validated with the real media facts.
    /// Any failure before step 4 leaves the attempt recoverable: the in-process
    /// caller clears the Pending metadata and deletes the partial candidate, so
    /// no terminal Failed + Pending dirty record survives a normal error path.
    /// </summary>
    private void PersistValidatedPartial(
        NodeExecutionContext ctx,
        RenderAttemptRecord attempt,
        string tempClip,
        string partialClip,
        ControlledStopResult controlled)
    {
        var reason = $"DiskPressure Critical（{controlled.Snapshot.DriveRoot} 剩余 {controlled.Snapshot.FreePercent:0.0}%）";
        if (!File.Exists(tempClip))
            throw new FileNotFoundException("DiskPressure 受控收尾输出不存在，无法验证 partial。", tempClip);

        // 1. Media validation with real facts.
        var probe = ctx.MediaProbe.Probe(tempClip, expectedFps: ProjectConstants.FinalOutputFramerate);
        if (!probe.IsValid)
            throw new RecordingStageException(RecordingFailureKind.MediaValidationFault, $"partial 媒体校验失败：{probe.Error}");
        if (controlled.Finalize.FirstFrameWidth > 0 && probe.Width > 0 &&
            (probe.Width != controlled.Finalize.FirstFrameWidth || probe.Height != controlled.Finalize.FirstFrameHeight))
        {
            throw new RecordingStageException(RecordingFailureKind.MediaValidationFault,
                $"partial 分辨率不一致：clip={probe.Width}x{probe.Height} 源TGA={controlled.Finalize.FirstFrameWidth}x{controlled.Finalize.FirstFrameHeight}");
        }
        var expectedDuration = controlled.OutputFrames / (double)ProjectConstants.FinalOutputFramerate;
        var tolerance = Math.Max(1.5, expectedDuration * 0.05);
        if (Math.Abs(probe.DurationSeconds - expectedDuration) > tolerance)
        {
            throw new RecordingStageException(RecordingFailureKind.MediaValidationFault,
                $"partial 时长不符：clip={probe.DurationSeconds:0.###}s 期望≈{expectedDuration:0.###}s（输出帧 {controlled.OutputFrames}）");
        }

        try
        {
            // 2. Pending intent BEFORE the move (crash between here and the
            //    move leaves a recoverable Pending row).
            ctx.Repository.MarkAttemptPartialPending(attempt.Id, partialClip, reason);

            // 3. Atomic commit: temp → deterministic partial path.
            AtomicFileCommitter.Commit(tempClip, partialClip);

            // 4. Pending → Validated (optimistic guard; throws on 0/2+ rows).
            ctx.Repository.UpdateAttemptPartial(
                attempt.Id,
                partialClip,
                DateTimeOffset.UtcNow,
                probe.FrameCount,
                reason);
            ctx.Log("Warning", $"DiskPressure partial 已校验并持久化：{partialClip}（帧 {probe.FrameCount}）");
        }
        catch
        {
            // Preserve the DB pointer until deletion is positively confirmed.
            // If deletion fails, recovery can still locate and retry this
            // Pending candidate; never create an untracked orphan partial.
            var candidateRemoved = TryDeleteConfirmed(partialClip);
            if (candidateRemoved)
            {
                try
                {
                    ctx.Repository.ClearAttemptPartial(attempt.Id);
                }
                catch (Exception clearEx)
                {
                    ctx.Log("Warning", $"DiskPressure partial Pending 清理失败：{clearEx.Message}");
                }
            }
            else
            {
                ctx.Log("Warning", $"DiskPressure partial 删除未确认，保留 Pending 元数据供崩溃恢复重试：{partialClip}");
            }
            throw;
        }
    }

    private static string? TryRecoverCompleteValidatedPartial(
        NodeExecutionContext ctx,
        string nodeDirectory)
    {
        var attempts = ctx.Repository
            .GetAttemptsForNode(ctx.Task.Id, ctx.Node.Id)
            .Where(static attempt => attempt.PartialState == PartialState.Validated)
            .OrderByDescending(static attempt => attempt.AttemptNumber)
            .ToArray();
        if (attempts.Length == 0)
            return null;

        var minimumDuration = Math.Max(0.5, ctx.Replay.RunTimeSeconds);
        var maximumDuration =
            Math.Max(0.1, ctx.Replay.RunTimeSeconds) +
            CaptureEnvelopeRecorder.PreSafetySeconds +
            CaptureEnvelopeRecorder.TailSafetySeconds;

        foreach (var attempt in attempts)
        {
            var partialPath = attempt.PartialPath;
            if (string.IsNullOrWhiteSpace(partialPath) || !File.Exists(partialPath))
            {
                ctx.Log(
                    "Warning",
                    $"PartialRecoveryRejected：Attempt={attempt.AttemptNumber} 原因=文件不存在 " +
                    $"Path={partialPath ?? "-"} PersistedFrames={attempt.PartialOutputFrames?.ToString() ?? "-"}。");
                continue;
            }

            var probe = ctx.MediaProbe.Probe(partialPath, expectedFps: ProjectConstants.FinalOutputFramerate);
            var persistedFramesMatch =
                attempt.PartialOutputFrames is null ||
                attempt.PartialOutputFrames.Value == probe.FrameCount;
            var durationComplete =
                probe.DurationSeconds >= minimumDuration &&
                probe.DurationSeconds <= maximumDuration;

            ctx.Log(
                "Info",
                $"PartialRecoveryEvidence：Attempt={attempt.AttemptNumber} State={attempt.PartialState} " +
                $"Path={partialPath} Valid={probe.IsValid} Size={probe.Width}x{probe.Height} " +
                $"Fps={probe.Fps:0.###} Frames={probe.FrameCount} " +
                $"PersistedFrames={attempt.PartialOutputFrames?.ToString() ?? "-"} " +
                $"Duration={probe.DurationSeconds:0.###}s " +
                $"Envelope=[{minimumDuration:0.###},{maximumDuration:0.###}]s " +
                $"FramesMatch={persistedFramesMatch} DurationComplete={durationComplete} " +
                $"ProbeError={probe.Error ?? "-"}.");

            if (!probe.IsValid || !persistedFramesMatch || !durationComplete)
            {
                ctx.Log(
                    "Warning",
                    $"PartialRecoveryRejected：Attempt={attempt.AttemptNumber} " +
                    $"原因={(probe.IsValid ? "完整性证据不足" : "媒体校验失败")}。");
                continue;
            }

            var recoveryTemp = Path.Combine(
                nodeDirectory,
                $"recovery_{attempt.AttemptNumber}_{Guid.NewGuid():N}.encoding.mp4");
            try
            {
                File.Copy(partialPath, recoveryTemp, overwrite: false);
                AtomicFileCommitter.Commit(recoveryTemp, ctx.StableClipPath);
                ctx.Log(
                    "Warning",
                    $"PartialRecoveryCommitted：Attempt={attempt.AttemptNumber} 已验证完整并恢复为正式阶段文件；" +
                    $"Source={partialPath} Destination={ctx.StableClipPath} Frames={probe.FrameCount} " +
                    $"Duration={probe.DurationSeconds:0.###}s。原 partial 保留用于审计。");
                return ctx.StableClipPath;
            }
            catch (Exception ex)
            {
                TryDelete(recoveryTemp);
                ctx.Log(
                    "Error",
                    $"PartialRecoveryCommitFailed：Attempt={attempt.AttemptNumber} " +
                    $"Source={partialPath} Destination={ctx.StableClipPath} " +
                    $"Exception={ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        return null;
    }

    private static string BuildAttemptFailureDiagnostics(
        RenderAttemptRecord attempt,
        RecordingFailureKind failureKind,
        string tempClip,
        string partialClip,
        Exception exception)
    {
        var chain = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            chain.Add($"{current.GetType().FullName}: {current.Message}");

        return
            $"AttemptFailureDiagnostics：Attempt={attempt.AttemptNumber} AttemptId={attempt.Id} " +
            $"Session={attempt.SessionId} Stage={attempt.Stage} Kind={failureKind} " +
            $"Prefix={attempt.SequencePrefix} Temp={tempClip} Partial={partialClip}\n" +
            $"ExceptionChain={string.Join(" -> ", chain)}\n" +
            $"StackTrace={exception.StackTrace ?? "-"}";
    }

    private async Task CleanupAndRecordFailureAsync(
        NodeExecutionContext ctx,
        RenderAttemptRecord attempt,
        CleanupReason reason,
        RecordingFailureKind kind,
        string error,
        CancellationToken token)
    {
        using var cleanupCts = new CancellationTokenSource(ctx.Timeouts.CleanupHardLimit);
        var cleanup = await ctx.CleanupCoordinator.CleanupAsync(
            _activePipeline,
            ctx.Game as MomentumProcessController,
            reason,
            cleanupCts.Token);
        _activePipeline = null;
        ctx.Repository.UpdateAttemptFailure(attempt.Id, kind, error, cleanup.CleanupState);
        foreach (var secondary in cleanup.SecondaryErrors)
            ctx.Log("Warning", $"清理次级错误：{secondary}");
    }

    private async Task RecoverAsync(NodeExecutionContext ctx, RetryDecision decision, CancellationToken token)
    {
        ctx.Log("Info", $"Recovery：{decision.Action}（{decision.Reason}）");
        switch (decision.Action)
        {
            case RetryAction.SameSessionRetry:
                // Cleanup already proved clean; nothing else to rebuild.
                break;

            case RetryAction.ReloadMapRetry:
                _forceMapReload = true;
                break;

            case RetryAction.RestartGameRetry:
                using (var cleanupCts = new CancellationTokenSource(ctx.Timeouts.CleanupHardLimit))
                {
                    if (ctx.Game.OwnsProcess)
                        await ctx.Game.ShutdownOwnedProcessAsync(ctx.Timeouts, cleanupCts.Token);
                }
                _readyGameSessionId = null;
                _readyMap = null;
                _forceMapReload = false;
                ctx.Log("Info", "游戏会话已销毁，下一 Attempt 将启动全新会话。");
                break;
        }
    }

    private void MarkNodeFailed(NodeExecutionContext ctx, RecordingFailureKind kind, string error)
    {
        ctx.OnNodeStatusChanged?.Invoke(ctx.Node with
        {
            Status = RenderNodeStatus.Failed,
            LastError = $"{kind}：{error}",
            ClipPath = null,
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool TryDeleteConfirmed(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Atomic file commit (temp → destination) with fsync-like durability.</summary>
public static class AtomicFileCommitter
{
    public static void Commit(string tempPath, string destinationPath)
    {
        var fullDest = Path.GetFullPath(destinationPath);
        var dir = Path.GetDirectoryName(fullDest) ?? throw new InvalidOperationException("目标目录无效。");
        Directory.CreateDirectory(dir);

        if (!File.Exists(tempPath))
            throw new FileNotFoundException("临时输出不存在，无法提交。", tempPath);

        // Flush to disk before the atomic move.
        using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, fullDest, overwrite: true);
    }
}
