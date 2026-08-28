namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// The single cleanup orchestrator for the recording chain (plan §6 / P0-09).
/// Every failure/cancel path runs through here with an independent bounded
/// cleanup token; the user's cancellation token never gates cleanup. Cleanup
/// failure never overrides the primary error but is recorded as secondary.
/// </summary>
public sealed class CaptureCleanupCoordinator
{
    private readonly RecordingTimeoutPolicy _timeouts;

    public CaptureCleanupCoordinator(RecordingTimeoutPolicy? timeouts = null)
    {
        _timeouts = timeouts ?? RecordingTimeoutPolicy.Default;
    }

    /// <summary>
    /// Order: strict endmovie → TGA physical quiescence → pipeline finalize
    /// (drain + native finish). Returns whether the capture environment is
    /// provably clean or must be rebuilt.
    /// </summary>
    public async Task<CaptureCleanupResult> CleanupAsync(
        ICapturePipeline? pipeline,
        MomentumProcessController? game,
        CleanupReason reason,
        CancellationToken cleanupToken)
    {
        var secondary = new List<string>();

        // 1. Strict endmovie (only when the game console is still reachable).
        var movieStopConfirmed = game is null || !game.NetCon.IsConnected;
        if (game is not null && game.NetCon.IsConnected)
        {
            try
            {
                var stop = await MomentumReplaySession.ExecuteEndMovieAsync(game.NetCon, _timeouts, null, cleanupToken);
                movieStopConfirmed = stop is StopMovieResult.CommandAcked or StopMovieResult.KnownAlreadyStopped;
                if (!movieStopConfirmed)
                    secondary.Add($"endmovie cleanup 未确认（{stop}）。");
            }
            catch (Exception ex)
            {
                movieStopConfirmed = false;
                secondary.Add($"endmovie cleanup 异常：{ex.Message}");
            }
        }

        // 2. Physical TGA quiescence.
        var tgaQuiescent = pipeline is null || pipeline.State is PipelineState.Finalized or PipelineState.Disposed;
        if (!tgaQuiescent && pipeline is not null)
        {
            try
            {
                await pipeline.Watcher.WaitForQuiescenceAsync(
                    _timeouts.TgaQuiescenceQuietWindow,
                    _timeouts.TgaQuiescenceHardTimeout,
                    cleanupToken);
                tgaQuiescent = true;
            }
            catch (Exception ex)
            {
                secondary.Add($"quiescence cleanup 异常：{ex.Message}");
            }
        }

        // 3. Pipeline finalize (drain + native finish) so no encoder is leaked.
        //    Idempotent by design: a pipeline that already completed its
        //    controlled finalize (M4 DiskPressure path or the happy path) is
        //    in Finalized/Faulted/Disposed state and is never finished twice —
        //    a second native Finish must not corrupt the validated partial.
        var pipelineFinalized = pipeline is null
            || pipeline.State is PipelineState.Finalized or PipelineState.Faulted or PipelineState.Disposed;
        var watcherDrained = pipelineFinalized;
        if (!pipelineFinalized && pipeline is not null)
        {
            try
            {
                await pipeline.FinalizeAsync(_timeouts, cleanupToken);
                pipelineFinalized = true;
                watcherDrained = true;
            }
            catch (Exception ex)
            {
                secondary.Add($"pipeline finalize cleanup 异常：{ex.Message}");
            }
        }

        var gameHealthy = game is null || game.IsGameRunning;
        var requiresRestart = !movieStopConfirmed || !tgaQuiescent || !pipelineFinalized || !gameHealthy;
        var cleanupState = requiresRestart ? CaptureCleanupState.Dirty : CaptureCleanupState.Clean;
        if (requiresRestart && game is not null && game.OwnsProcess)
            cleanupState = CaptureCleanupState.GameRestartRequired;

        return new CaptureCleanupResult(
            movieStopConfirmed,
            tgaQuiescent,
            watcherDrained,
            pipelineFinalized,
            gameHealthy,
            requiresRestart,
            cleanupState,
            secondary);
    }
}
