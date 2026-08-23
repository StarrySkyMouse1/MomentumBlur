using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Capture-first envelope: startmovie → CaptureReady → watch → ActivityAnchor → SafeEnd → endmovie.
/// TGA wraps the entire replay (prefer idle head/tail over missing opening).
/// </summary>
public static class CaptureEnvelopeRecorder
{
    public const int CaptureReadyMinFrames = 3;
    public const double PreSafetySeconds = 2.0;
    public const double TailSafetySeconds = 2.5;
    public static readonly TimeSpan CaptureReadyTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TgaStopSettle = TimeSpan.FromMilliseconds(750);

    /// <summary>Frames after ActivityAnchor: RunTime + PreSafety + TailSafety at capture FPS.</summary>
    public static int ComputeEnvelopeFrameCount(double runTimeSeconds, int supersamplingMultiplier)
    {
        var captureFps = Math.Max(1, supersamplingMultiplier) * ProjectConstants.FinalOutputFramerate;
        var envelopeSeconds = Math.Max(0.1, runTimeSeconds) + PreSafetySeconds + TailSafetySeconds;
        return Math.Max(1, (int)Math.Ceiling(envelopeSeconds * captureFps));
    }

    public static int ComputeSafeEndFrame(int activityAnchorFrame, double runTimeSeconds, int supersamplingMultiplier) =>
        activityAnchorFrame + ComputeEnvelopeFrameCount(runTimeSeconds, supersamplingMultiplier);

    public static async Task RecordAsync(
        MomentumNetConClient netCon,
        TgaPipelineOrchestrator pipeline,
        UserSettings user,
        string gameRelativeReplayPath,
        double runTimeSeconds,
        Action<string>? phase,
        CancellationToken token)
    {
        async Task FailCleanupAsync(Exception ex)
        {
            phase?.Invoke($"StoppingCapture（失败清理）：{ex.Message}");
            await TryEndMovieAsync(netCon);
        }

        try
        {
            phase?.Invoke("PreparingCapture");
            await TryEndMovieAsync(netCon);
            await Task.Delay(300, token);

            phase?.Invoke("StartMovie");
            var startmovie = WatchDirectoryHelper.BuildGameStartmovieCommand(user.MovieSequenceName);
            phase?.Invoke($"StartMovie：{startmovie}");
            await netCon.ExecuteAsync(startmovie, TimeSpan.FromSeconds(30), token);

            phase?.Invoke("WaitingCaptureReady");
            await pipeline.WaitUntilFedAsync(CaptureReadyMinFrames, CaptureReadyTimeout, token);
            phase?.Invoke($"WaitingCaptureReady：已确认 {pipeline.FedCount} 帧 TGA");
            pipeline.ResetActivityTracking();

            phase?.Invoke("StartingReplay");
            await MomentumReplaySession.StartWatchAsync(netCon, gameRelativeReplayPath, phase, token);

            phase?.Invoke("WaitingReplayActivity");
            await pipeline.WaitUntilActivityAsync(ActivityTimeout, token);
            var anchor = pipeline.ActivityAnchorFrame
                ?? throw new InvalidOperationException("ActivityAnchorFrame 未设置。");
            phase?.Invoke($"WaitingReplayActivity：ActivityAnchorFrame={anchor}");

            var safeEnd = ComputeSafeEndFrame(anchor, runTimeSeconds, user.SupersamplingMultiplier);
            phase?.Invoke($"Recording：SafeEndFrame={safeEnd}（Anchor={anchor} + RunTime/Pre/Tail）");

            var lastFed = pipeline.FedCount;
            var lastFedAt = DateTime.UtcNow;
            var lastVisualFrame = pipeline.LastVisualChangeFrame ?? anchor;
            var lastVisualAt = DateTime.UtcNow;

            while (pipeline.FedCount < safeEnd)
            {
                token.ThrowIfCancellationRequested();

                if (pipeline.FedCount != lastFed)
                {
                    lastFed = pipeline.FedCount;
                    lastFedAt = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastFedAt > TimeSpan.FromMinutes(2))
                {
                    throw new TimeoutException("TGA 帧连续两分钟没有增长（Recording Stall）。");
                }

                var visual = pipeline.LastVisualChangeFrame;
                if (visual is not null && visual.Value != lastVisualFrame)
                {
                    lastVisualFrame = visual.Value;
                    lastVisualAt = DateTime.UtcNow;
                }
                else
                {
                    // Stall only when clearly early in the envelope; idle tail near SafeEnd is expected.
                    var progress = pipeline.FedCount - anchor;
                    var envelope = Math.Max(1, safeEnd - anchor);
                    if (progress < envelope * 0.85
                        && DateTime.UtcNow - lastVisualAt > StallTimeout)
                    {
                        throw new InvalidOperationException(
                            $"PlaybackStalled：Activity 之后 {StallTimeout.TotalSeconds:0}s 画面无新变化（Fed={pipeline.FedCount}/{safeEnd}）。静止不等于播完。");
                    }
                }

                phase?.Invoke($"Recording：{pipeline.FedCount}/{safeEnd}");
                await Task.Delay(250, token);
            }

            phase?.Invoke("StoppingCapture");
            await TryEndMovieAsync(netCon);
            await WaitTgaGrowthStopAsync(pipeline, token);

            phase?.Invoke("VerifyingCapture");
            if (pipeline.ActivityAnchorFrame is null || !pipeline.HasVisualChange)
                throw new InvalidOperationException("成片校验失败：录制过程中未检测到 VisualActivity。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailCleanupAsync(ex);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryEndMovieAsync(netCon);
            throw;
        }
    }

    /// <summary>
    /// Mini envelope for「验证回放」: CaptureReady → watch → Activity → endmovie. Does not record full run.
    /// </summary>
    public static async Task VerifyActivityAsync(
        MomentumNetConClient netCon,
        TgaPipelineOrchestrator pipeline,
        UserSettings user,
        string gameRelativeReplayPath,
        Action<string>? phase,
        CancellationToken token)
    {
        try
        {
            phase?.Invoke("PreparingCapture");
            await TryEndMovieAsync(netCon);
            await Task.Delay(300, token);

            phase?.Invoke("StartMovie");
            var startmovie = WatchDirectoryHelper.BuildGameStartmovieCommand(user.MovieSequenceName);
            phase?.Invoke($"StartMovie：{startmovie}");
            await netCon.ExecuteAsync(startmovie, TimeSpan.FromSeconds(30), token);

            phase?.Invoke("WaitingCaptureReady");
            await pipeline.WaitUntilFedAsync(CaptureReadyMinFrames, CaptureReadyTimeout, token);
            phase?.Invoke($"WaitingCaptureReady：已确认 {pipeline.FedCount} 帧");
            pipeline.ResetActivityTracking();

            phase?.Invoke("StartingReplay");
            await MomentumReplaySession.StartWatchAsync(netCon, gameRelativeReplayPath, phase, token);

            phase?.Invoke("WaitingReplayActivity");
            await pipeline.WaitUntilActivityAsync(ActivityTimeout, token);
            phase?.Invoke($"WaitingReplayActivity：ActivityAnchorFrame={pipeline.ActivityAnchorFrame}");

            phase?.Invoke("StoppingCapture");
            await TryEndMovieAsync(netCon);
            await WaitTgaGrowthStopAsync(pipeline, token);
            phase?.Invoke("VerifyingCapture：回放可被自动拉起（已检测到 VisualActivity）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            phase?.Invoke($"StoppingCapture（失败清理）：{ex.Message}");
            await TryEndMovieAsync(netCon);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryEndMovieAsync(netCon);
            throw;
        }
    }

    /// <summary>Only stops movie capture. Does not reset host_framerate (needed across CaptureReady→Recording).</summary>
    public static async Task TryEndMovieAsync(MomentumNetConClient netCon)
    {
        try
        {
            await netCon.ExecuteAsync("endmovie", TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // best-effort
        }
    }

    private static async Task WaitTgaGrowthStopAsync(TgaPipelineOrchestrator pipeline, CancellationToken token)
    {
        var last = pipeline.FedCount;
        var stableSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (pipeline.FedCount != last)
            {
                last = pipeline.FedCount;
                stableSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stableSince >= TgaStopSettle)
            {
                return;
            }

            await Task.Delay(100, token);
        }
    }
}
