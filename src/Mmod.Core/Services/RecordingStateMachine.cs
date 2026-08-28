namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Transition guard for the fine-grained NodeExecutionStage state machine
/// (plan P1-02). Illegal transitions (e.g. StartingReplay → Completed) are
/// rejected with an exception instead of silently updating state.
/// </summary>
public static class RecordingStateMachine
{
    /// <summary>Stages that may abort into failure/cleanup from anywhere.</summary>
    private static readonly NodeExecutionStage[] UniversalAborts =
        [NodeExecutionStage.CleaningUp, NodeExecutionStage.Failed, NodeExecutionStage.Canceled];

    private static readonly Dictionary<NodeExecutionStage, NodeExecutionStage[]> Legal = new()
    {
        [NodeExecutionStage.Created] = [NodeExecutionStage.Preflight],
        [NodeExecutionStage.Preflight] = [NodeExecutionStage.EnsuringGameSession],
        [NodeExecutionStage.EnsuringGameSession] = [NodeExecutionStage.ConnectingNetCon],
        [NodeExecutionStage.ConnectingNetCon] = [NodeExecutionStage.ChangingMap],
        [NodeExecutionStage.ChangingMap] = [NodeExecutionStage.WaitingMapReady],
        [NodeExecutionStage.WaitingMapReady] = [NodeExecutionStage.PreparingCaptureBaseline, NodeExecutionStage.RetryRecovery],
        [NodeExecutionStage.PreparingCaptureBaseline] = [NodeExecutionStage.StartingMovie],
        [NodeExecutionStage.StartingMovie] = [NodeExecutionStage.WaitingCaptureReady],
        [NodeExecutionStage.WaitingCaptureReady] = [NodeExecutionStage.StartingReplay],
        [NodeExecutionStage.StartingReplay] = [NodeExecutionStage.WaitingPlaybackEvidence],
        [NodeExecutionStage.WaitingPlaybackEvidence] = [NodeExecutionStage.Capturing, NodeExecutionStage.RetryRecovery],
        [NodeExecutionStage.Capturing] = [NodeExecutionStage.RequestingMovieStop, NodeExecutionStage.DiskPressureRequested],
        [NodeExecutionStage.DiskPressureRequested] = [NodeExecutionStage.WaitingCaptureQuiescence, NodeExecutionStage.FinalizingEncoder, NodeExecutionStage.RetryRecovery],
        [NodeExecutionStage.RequestingMovieStop] = [NodeExecutionStage.WaitingCaptureQuiescence],
        [NodeExecutionStage.WaitingCaptureQuiescence] = [NodeExecutionStage.FreezingWatcher],
        [NodeExecutionStage.FreezingWatcher] = [NodeExecutionStage.DrainingFrames],
        [NodeExecutionStage.DrainingFrames] = [NodeExecutionStage.FinalizingEncoder],
        [NodeExecutionStage.FinalizingEncoder] = [NodeExecutionStage.ValidatingClip, NodeExecutionStage.PartialValidated],
        [NodeExecutionStage.PartialValidated] = [NodeExecutionStage.Failed, NodeExecutionStage.Canceled],
        [NodeExecutionStage.ValidatingClip] = [NodeExecutionStage.CommittingClip],
        [NodeExecutionStage.CommittingClip] = [NodeExecutionStage.Completed],
        [NodeExecutionStage.CleaningUp] = [NodeExecutionStage.RetryRecovery, NodeExecutionStage.Failed, NodeExecutionStage.Canceled],
        [NodeExecutionStage.RetryRecovery] = [NodeExecutionStage.Preflight, NodeExecutionStage.Failed, NodeExecutionStage.Canceled],
    };

    public static void AssertTransition(NodeExecutionStage from, NodeExecutionStage to)
    {
        if (from == to)
            return;

        if (UniversalAborts.Contains(to))
            return;

        if (!Legal.TryGetValue(from, out var allowed) || !allowed.Contains(to))
        {
            throw new InvalidOperationException(
                $"非法状态转换：{from} → {to}（状态机拒绝，防止跳过关键校验阶段）。");
        }
    }

    public static bool IsTerminal(NodeExecutionStage stage) =>
        stage is NodeExecutionStage.Completed or NodeExecutionStage.Failed or NodeExecutionStage.Canceled;
}
