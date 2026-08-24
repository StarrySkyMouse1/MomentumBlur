namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Retry + recovery policy (plan §12 / P1-07). Decisions are driven by the
/// stable failure kind, the attempt number, and whether cleanup proved clean.
/// Dirty cleanup always escalates to a game restart.
/// </summary>
public static class RecordingRetryPolicy
{
    /// <summary>Kinds that must never auto-retry (user must act).</summary>
    public static readonly RecordingFailureKind[] Permanent =
        [RecordingFailureKind.InvalidInput, RecordingFailureKind.UnsupportedReplay, RecordingFailureKind.MapUnavailable, RecordingFailureKind.DiskPressure, RecordingFailureKind.UserCanceled];

    /// <summary>Kinds that force a fresh game session before retrying.</summary>
    public static readonly RecordingFailureKind[] RequiresRestartGame =
        [RecordingFailureKind.CaptureStopUnconfirmed, RecordingFailureKind.TgaQuiescenceTimeout, RecordingFailureKind.NetConLost, RecordingFailureKind.GameExited, RecordingFailureKind.TgaWriteStalled];

    /// <summary>Kinds limited to a single retry (2 attempts total).</summary>
    public static readonly RecordingFailureKind[] SingleRetry =
        [RecordingFailureKind.MapReadinessTimeout, RecordingFailureKind.ReplayRejected, RecordingFailureKind.EncoderFinalizeFault, RecordingFailureKind.MediaValidationFault];

    public static RetryDecision Decide(
        RecordingFailureKind kind,
        int attemptNumber,
        int maxAttempts,
        bool cleanupSucceeded)
    {
        var remaining = maxAttempts - attemptNumber;
        if (remaining <= 0)
            return new RetryDecision(RetryAction.NoRetryNeedsUser, cleanupSucceeded, "达到最大尝试次数。");

        if (Permanent.Contains(kind))
            return new RetryDecision(RetryAction.NoRetryNeedsUser, cleanupSucceeded, $"永久性失败（{kind}），需要用户处理。");

        if (SingleRetry.Contains(kind) && attemptNumber >= 2)
            return new RetryDecision(RetryAction.NoRetryNeedsUser, cleanupSucceeded, $"{kind} 最多允许 1 次重试。");

        if (!cleanupSucceeded)
            return new RetryDecision(RetryAction.RestartGameRetry, cleanupSucceeded, "cleanup 未证明干净，必须重建游戏会话。");

        if (RequiresRestartGame.Contains(kind))
            return new RetryDecision(RetryAction.RestartGameRetry, cleanupSucceeded, $"{kind} 要求全新游戏会话。");

        return kind switch
        {
            RecordingFailureKind.MapReadinessTimeout => new RetryDecision(RetryAction.ReloadMapRetry, cleanupSucceeded, "地图就绪超时，重载地图。"),
            RecordingFailureKind.ReplayRejected => new RetryDecision(RetryAction.ReloadMapRetry, cleanupSucceeded, "回放被拒绝，重载地图后重试。"),
            RecordingFailureKind.PlaybackEvidenceTimeout => new RetryDecision(RetryAction.ReloadMapRetry, cleanupSucceeded, "播放证据超时，重载地图后重试。"),
            RecordingFailureKind.CaptureStartFailed => new RetryDecision(RetryAction.SameSessionRetry, cleanupSucceeded, "捕获启动失败且 cleanup 干净，同会话重试。"),
            RecordingFailureKind.PipelineFault => new RetryDecision(RetryAction.SameSessionRetry, cleanupSucceeded, "pipeline fault 且 cleanup 干净，重建管线重试。"),
            RecordingFailureKind.EncoderFinalizeFault => new RetryDecision(RetryAction.SameSessionRetry, cleanupSucceeded, "编码 finalize 失败，重建输出临时文件重试。"),
            RecordingFailureKind.MediaValidationFault => new RetryDecision(RetryAction.SameSessionRetry, cleanupSucceeded, "媒体校验失败，整段重录。"),
            _ => new RetryDecision(RetryAction.RestartGameRetry, cleanupSucceeded, $"未知失败（{kind}），保守重启游戏。"),
        };
    }
}
