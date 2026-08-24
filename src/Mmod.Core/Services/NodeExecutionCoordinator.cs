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
    Action<RenderNodeRecord>? OnNodeStatusChanged);

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

    public async Task<string> ExecuteNodeAsync(NodeExecutionContext ctx, CancellationToken token)
    {
        var taskId = ctx.Task.Id;
        var nodeId = ctx.Node.Id;
        var nodeDir = Path.Combine(ctx.WorkDirectory, $"node_{ctx.Node.Sequence + 1:D3}");
        Directory.CreateDirectory(nodeDir);

        var attemptNumber = ctx.Repository.GetAttemptsForNode(taskId, nodeId).Count + 1;
        string? lastError = null;
        RecordingFailureKind? lastKind = null;

        while (attemptNumber <= ctx.Timeouts.MaxAttempts)
        {
            var captureSession = CaptureSessionInfo.Create(taskId, ctx.Node.Sequence, attemptNumber);
            var attemptId = Guid.NewGuid().ToString("N");
            var tempClip = Path.Combine(nodeDir, $"attempt_{attemptNumber}_{captureSession.CaptureSessionId[..6]}.encoding.mp4");

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
                    lastKind.Value, attemptNumber, ctx.Timeouts.MaxAttempts,
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

        // Positive map readiness.
        setStage(NodeExecutionStage.ChangingMap);
        setStage(NodeExecutionStage.WaitingMapReady);
        await MomentumReplaySession.ChangeMapAsync(ctx.Game.NetCon, ctx.Task.MapName, l => ctx.Log("Info", l), token, timeouts: timeouts);
        ctx.Log("Info", $"MapReady：{ctx.Task.MapName}");

        // Build per-attempt user settings (prefix applied by the pipeline).
        var user = RenderTaskRunner.ToUserSettingsForAttempt(ctx.Settings);
        var relative = MomentumReplaySession.BuildGameRelativeReplayPath(ctx.Settings.GameRootPath, ctx.Node.ReplayPath);

        var pipeline = new TgaPipelineOrchestrator(timeouts);
        pipeline.Changed += () => ctx.OnNodeStatusChanged?.Invoke(ctx.Node);
        var health = new GameSessionHealthMonitor(ctx.Game as MomentumProcessController ?? throw new InvalidOperationException("需要 MomentumProcessController 健康监控"), ctx.Settings.WatchDirectory);

        try
        {
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
                setStage);
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
            await pipeline.DisposeAsync();
        }
    }

    private void WriteStructuredLog(NodeExecutionContext ctx, RecordingLogEntry entry)
    {
        ctx.Log("Info",
            $"[{entry.Stage}] {entry.Event} Fed={entry.FedCount} Candidate={entry.CandidateCount} Pending={entry.PendingCount} " +
            $"LastWrite={entry.LastPhysicalWriteUtc?.ToString("HH:mm:ss.fff") ?? "-"} {entry.Message}");
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
                // Map will be re-probed by the next attempt's ChangeMapAsync.
                break;

            case RetryAction.RestartGameRetry:
                using (var cleanupCts = new CancellationTokenSource(ctx.Timeouts.CleanupHardLimit))
                {
                    if (ctx.Game.OwnsProcess)
                        await ctx.Game.ShutdownOwnedProcessAsync(ctx.Timeouts, cleanupCts.Token);
                }
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
