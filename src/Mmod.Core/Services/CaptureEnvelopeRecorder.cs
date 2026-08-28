using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Capture-first envelope driven by positive evidence: startmovie → CaptureReady
/// → watch → PlaybackEvidence anchor → envelope end → strict endmovie → physical
/// TGA quiescence → pipeline finalize. No fixed sleep is treated as proof; every
/// boundary must be positively confirmed or the attempt fails. Cleanup on
/// failure is the caller's responsibility (CaptureCleanupCoordinator).
/// </summary>
public static class CaptureEnvelopeRecorder
{
    public const int CaptureReadyMinFrames = 3;
    public const double PreSafetySeconds = 2.0;
    public const double TailSafetySeconds = 2.5;

    /// <summary>Frames after ActivityAnchor: RunTime + PreSafety + TailSafety at capture FPS.</summary>
    public static int ComputeEnvelopeFrameCount(double runTimeSeconds, int supersamplingMultiplier)
    {
        var captureFps = Math.Max(1, supersamplingMultiplier) * ProjectConstants.FinalOutputFramerate;
        var envelopeSeconds = Math.Max(0.1, runTimeSeconds) + PreSafetySeconds + TailSafetySeconds;
        return Math.Max(1, (int)Math.Ceiling(envelopeSeconds * captureFps));
    }

    public static int ComputeSafeEndFrame(int activityAnchorFrame, double runTimeSeconds, int supersamplingMultiplier) =>
        activityAnchorFrame + ComputeEnvelopeFrameCount(runTimeSeconds, supersamplingMultiplier);

    /// <summary>
    /// Full recording envelope for a node attempt. Throws on any unproven
    /// boundary; cleanup is delegated to the caller. Returns the pipeline
    /// finalize result (real counters) for downstream media validation.
    /// </summary>
    public static async Task<PipelineFinalizeResult> RecordAsync(
        INetConClient netCon,
        ICapturePipeline pipeline,
        UserSettings user,
        string gameRelativeReplayPath,
        double runTimeSeconds,
        IGameSessionHealthMonitor? health,
        Action<string>? phase,
        Action<RecordingLogEntry>? structuredLog,
        CancellationToken token,
        RecordingTimeoutPolicy? timeouts = null,
        Action<NodeExecutionStage>? onStage = null)
    {
        timeouts ??= RecordingTimeoutPolicy.Default;
        var startedAt = DateTime.UtcNow;

        void Stage(NodeExecutionStage stage, string ev, string message, RecordingFailureKind? kind = null)
        {
            onStage?.Invoke(stage);
            var watcher = pipeline.Watcher;
            structuredLog?.Invoke(new RecordingLogEntry(
                TaskId: "?",
                NodeId: null,
                AttemptId: null,
                CaptureSessionId: pipeline.CaptureSessionId,
                Stage: stage,
                Event: ev,
                ElapsedMs: (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                FedCount: pipeline.FedCount,
                CandidateCount: watcher.CandidateCount,
                PendingCount: watcher.PendingCount,
                LastPhysicalWriteUtc: watcher.LastPhysicalFileWriteUtc,
                GamePid: null,
                FailureKind: kind,
                Message: message));
        }

        try
        {
            // 1. Clean baseline: no active startmovie from a previous session.
            Stage(NodeExecutionStage.PreparingCaptureBaseline, "Begin", "开始录制 Envelope");
            var baselineStop = await MomentumReplaySession.ExecuteEndMovieAsync(netCon, timeouts, phase, token);
            if (baselineStop is not StopMovieResult.CommandAcked and not StopMovieResult.KnownAlreadyStopped)
            {
                throw new CaptureStopUnconfirmedException($"准备阶段 endmovie 未确认（{baselineStop}）。");
            }

            // 2. Start movie with the unique session prefix (strict, failure patterns).
            Stage(NodeExecutionStage.StartingMovie, "StartMovie", "开始 startmovie");
            await MomentumReplaySession.ExecuteStartMovieAsync(netCon, user.MovieSequenceName, timeouts, phase, token);

            // 3. CaptureReady evidence: the current session is actually producing TGA.
            Stage(NodeExecutionStage.WaitingCaptureReady, "WaitingCaptureReady", "等待 CaptureReady");
            await pipeline.WaitUntilFedAsync(CaptureReadyMinFrames, timeouts.CaptureReadyTimeout, token);
            phase?.Invoke($"WaitingCaptureReady：已确认 {pipeline.FedCount} 帧 TGA");
            pipeline.ResetActivityTracking();

            // 4. Start replay watch (typed result).
            Stage(NodeExecutionStage.StartingReplay, "StartingReplay", "发送 replay watch");
            await MomentumReplaySession.StartWatchAsync(netCon, gameRelativeReplayPath, phase, token, timeouts);

            // 5. Playback evidence (robust visual probe; no hash-only anchor).
            Stage(NodeExecutionStage.WaitingPlaybackEvidence, "WaitingPlaybackEvidence", "等待播放证据");
            await pipeline.WaitUntilActivityAsync(timeouts.PlaybackEvidenceTimeout, token);
            var anchor = pipeline.ActivityAnchorFrame
                ?? throw new InvalidOperationException("PlaybackEvidence anchor 未建立。");
            phase?.Invoke($"PlaybackEvidenceConfirmed @ Anchor={anchor}");

            var safeEnd = ComputeSafeEndFrame(anchor, runTimeSeconds, user.SupersamplingMultiplier);
            phase?.Invoke($"Recording：SafeEndFrame={safeEnd}（Anchor={anchor} + RunTime/Pre/Tail）");
            Stage(NodeExecutionStage.Capturing, "Capturing", $"目标 SafeEnd={safeEnd}");

            // 6. Capturing loop: race user cancellation / pipeline fault / game exit /
            //    expected progress / stage timeout. A static frame only lowers
            //    confidence — it is never treated as replay-finished. Disk health
            //    is sampled once immediately, then at most once per
            //    DiskHealthSampleInterval (time-throttled, never per frame).
            var lastFed = pipeline.FedCount;
            var lastFedAt = DateTime.UtcNow;
            var lastDiskSampleAt = DateTime.MinValue;
            var consecutiveUnavailableSamples = 0;
            var lastDiskState = (DiskSafetyState?)null;
            while (pipeline.FedCount < safeEnd)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfFaulted(pipeline);

                if (health is not null)
                {
                    if (health.GameExitedTask.IsCompleted)
                        throw new GameExitedException("录制中游戏进程退出。");
                    if (DateTime.UtcNow - lastDiskSampleAt >= timeouts.DiskHealthSampleInterval)
                    {
                        lastDiskSampleAt = DateTime.UtcNow;
                        var snapshot = health.GetWatchDiskHealth(user.DiskSafetyFreePercent);
                        switch (snapshot.State)
                        {
                            case DiskSafetyState.Disabled:
                                consecutiveUnavailableSamples = 0;
                                lastDiskState = null;
                                break;
                            case DiskSafetyState.Normal:
                            case DiskSafetyState.Warning:
                                if (lastDiskState != snapshot.State)
                                {
                                    lastDiskState = snapshot.State;
                                    if (snapshot.State == DiskSafetyState.Warning)
                                        phase?.Invoke($"磁盘警告：监视盘 {FormatDriveRoot(snapshot.DriveRoot)} 剩余 {snapshot.FreePercent:0.0}%（警告线 {snapshot.WarningPercent}%）");
                                }
                                consecutiveUnavailableSamples = 0;
                                break;
                            case DiskSafetyState.Critical:
                                throw new DiskPressureException(
                                    $"监视盘 {FormatDriveRoot(snapshot.DriveRoot)} 剩余 {snapshot.FreePercent:0.0}% / {ToGiB(snapshot.FreeBytes):0.0} GiB，已达到安全下限 {snapshot.SafetyPercent}% / {ToGiB(snapshot.SafetyBytes):0.0} GiB。",
                                    snapshot);
                            case DiskSafetyState.Unavailable:
                                consecutiveUnavailableSamples++;
                                if (consecutiveUnavailableSamples >= timeouts.DiskHealthUnavailableMaxConsecutiveSamples)
                                {
                                    throw new DiskHealthUnavailableException(
                                        $"监视盘 {FormatDriveRoot(snapshot.DriveRoot)} 健康采样连续 {consecutiveUnavailableSamples} 次不可用，无法确认磁盘空间安全。",
                                        snapshot);
                                }
                                phase?.Invoke($"磁盘采样不可用（{consecutiveUnavailableSamples}/{timeouts.DiskHealthUnavailableMaxConsecutiveSamples}）：{FormatDriveRoot(snapshot.DriveRoot)}");
                                break;
                        }
                    }
                }

                if (pipeline.FedCount != lastFed)
                {
                    lastFed = pipeline.FedCount;
                    lastFedAt = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastFedAt > timeouts.NoPhysicalTgaProgressTimeout)
                {
                    throw new TimeoutException("TGA 帧连续无增长（Recording Stall）。");
                }

                phase?.Invoke($"Recording：{pipeline.FedCount}/{safeEnd}");
                await Task.WhenAny(
                    Task.Delay(timeouts.ProgressSampleInterval, token),
                    pipeline.Completion,
                    health?.GameExitedTask ?? Task.CompletedTask);
                // Immediate exit when the pipeline faulted or the game exited
                // while we were sampling — do not wait for the next tick.
                ThrowIfFaulted(pipeline);
                if (health?.GameExitedTask.IsCompleted == true)
                    throw new GameExitedException("录制中游戏进程退出。");
            }

            // 7. Strict endmovie: only CommandAcked / KnownAlreadyStopped proceed.
            Stage(NodeExecutionStage.RequestingMovieStop, "EndMovie", "请求停止录制");
            var stop = await MomentumReplaySession.ExecuteEndMovieAsync(netCon, timeouts, phase, token);
            if (stop is not StopMovieResult.CommandAcked and not StopMovieResult.KnownAlreadyStopped)
            {
                if (stop == StopMovieResult.NetConLost)
                    throw new CaptureStopUnconfirmedException("endmovie：NetCon 已断开，无法确认停止。");
                throw new CaptureStopUnconfirmedException($"endmovie 未确认（{stop}）。");
            }

            // 8. Physical TGA quiescence: proof the writer actually stopped.
            Stage(NodeExecutionStage.WaitingCaptureQuiescence, "Quiescence", "等待 TGA 物理静默");
            await pipeline.Watcher.WaitForQuiescenceAsync(
                timeouts.TgaQuiescenceQuietWindow,
                timeouts.TgaQuiescenceHardTimeout,
                token);

            // 9. Finalize: freeze → drain → native Finish; faults propagate.
            Stage(NodeExecutionStage.FinalizingEncoder, "Finalize", "Native Finish");
            var result = await pipeline.FinalizeAsync(timeouts, token);

            // 10. Playback evidence must have survived end-to-end.
            if (pipeline.ActivityAnchorFrame is null || !pipeline.HasVisualChange)
                throw new InvalidOperationException("成片校验失败：录制过程中未建立 PlaybackEvidence。");

            Stage(NodeExecutionStage.Completed, "Completed",
                $"Fed={result.SubmittedFrames} Out={result.ProducedFrames} 输出={result.OutputPath}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Stage(NodeExecutionStage.CleaningUp, "Failure", ex.Message, RecordingFailureClassifier.Classify(ex));
            throw;
        }
    }

    /// <summary>
    /// Mini envelope for「验证回放」: CaptureReady → watch → evidence → strict stop
    /// → quiescence. Does not record a full run.
    /// </summary>
    public static async Task VerifyActivityAsync(
        INetConClient netCon,
        ICapturePipeline pipeline,
        UserSettings user,
        string gameRelativeReplayPath,
        Action<string>? phase,
        CancellationToken token,
        RecordingTimeoutPolicy? timeouts = null,
        Action<NodeExecutionStage>? onStage = null)
    {
        timeouts ??= RecordingTimeoutPolicy.Default;
        void Stage(NodeExecutionStage stage) => onStage?.Invoke(stage);
        try
        {
            phase?.Invoke("PreparingCapture");
            Stage(NodeExecutionStage.PreparingCaptureBaseline);
            var baselineStop = await MomentumReplaySession.ExecuteEndMovieAsync(netCon, timeouts, phase, token);
            if (baselineStop is not StopMovieResult.CommandAcked and not StopMovieResult.KnownAlreadyStopped)
                throw new CaptureStopUnconfirmedException($"准备阶段 endmovie 未确认（{baselineStop}）。");

            Stage(NodeExecutionStage.StartingMovie);
            await MomentumReplaySession.ExecuteStartMovieAsync(netCon, user.MovieSequenceName, timeouts, phase, token);

            phase?.Invoke("WaitingCaptureReady");
            Stage(NodeExecutionStage.WaitingCaptureReady);
            await pipeline.WaitUntilFedAsync(CaptureReadyMinFrames, timeouts.CaptureReadyTimeout, token);
            phase?.Invoke($"WaitingCaptureReady：已确认 {pipeline.FedCount} 帧");
            pipeline.ResetActivityTracking();

            phase?.Invoke("StartingReplay");
            Stage(NodeExecutionStage.StartingReplay);
            await MomentumReplaySession.StartWatchAsync(netCon, gameRelativeReplayPath, phase, token, timeouts);

            phase?.Invoke("WaitingReplayActivity");
            Stage(NodeExecutionStage.WaitingPlaybackEvidence);
            await pipeline.WaitUntilActivityAsync(timeouts.PlaybackEvidenceTimeout, token);
            phase?.Invoke($"WaitingReplayActivity：ActivityAnchorFrame={pipeline.ActivityAnchorFrame}");

            phase?.Invoke("StoppingCapture");
            Stage(NodeExecutionStage.RequestingMovieStop);
            var stop = await MomentumReplaySession.ExecuteEndMovieAsync(netCon, timeouts, phase, token);
            if (stop is not StopMovieResult.CommandAcked and not StopMovieResult.KnownAlreadyStopped)
                throw new CaptureStopUnconfirmedException($"endmovie 未确认（{stop}）。");

            Stage(NodeExecutionStage.WaitingCaptureQuiescence);
            await pipeline.Watcher.WaitForQuiescenceAsync(
                timeouts.TgaQuiescenceQuietWindow,
                timeouts.TgaQuiescenceHardTimeout,
                token);

            Stage(NodeExecutionStage.FinalizingEncoder);
            await pipeline.FinalizeAsync(timeouts, token);
            phase?.Invoke("VerifyingCapture：回放可被自动拉起（已建立 PlaybackEvidence）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            phase?.Invoke($"StoppingCapture（失败清理）：{ex.Message}");
            throw;
        }
    }

    private static void ThrowIfFaulted(ICapturePipeline pipeline)
    {
        if (pipeline.IsFaulted)
            throw new PipelineFaultException(pipeline.Fault?.Message ?? "pipeline fault", pipeline.Fault ?? new Exception("unknown"));
    }

    private static double ToGiB(long bytes) => bytes / 1024d / 1024d / 1024d;

    private static string FormatDriveRoot(string driveRoot) =>
        string.IsNullOrWhiteSpace(driveRoot) ? "(未知)" : driveRoot;
}
