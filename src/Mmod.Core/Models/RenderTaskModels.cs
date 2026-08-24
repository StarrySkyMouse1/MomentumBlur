namespace Mmod.Core.Models;

public enum RenderTaskStatus
{
    Pending,
    Starting,
    Running,
    Merging,
    Paused,
    FailedNeedsAttention,
    ClipsReadyNeedsManualMerge,
    Completed,
    Canceled,
}

public enum RenderNodeStatus
{
    Pending,
    Recording,
    Synthesizing,
    Completed,
    Failed,
    Skipped,
}

/// <summary>
/// Frozen task settings. New fields use default parameter values so old
/// SettingsJson (which lacks them) still deserializes and behaves like the
/// legacy pipeline: Legacy exposure + all quality modules off + auto bitrate.
/// </summary>
public sealed record RenderSettingsSnapshot(
    int SupersamplingMultiplier,
    double Exposure,
    string WatchDirectory,
    string OutputDirectory,
    string GameRootPath,
    bool HideHud,
    int OutputFramerate,
    int TargetBitrate,
    MotionBlurWeightMode MotionBlurMode = MotionBlurWeightMode.LegacyGaussianExposure,
    double ShutterAngle = 270,
    VideoProcessingSettings? VideoProcessing = null);

public sealed record NewRenderNode(
    string ReplayPath,
    int StageNumber,
    int Sequence,
    double ExpectedDurationSeconds,
    int ExpectedTickCount);

public sealed record NewRenderTask(
    string MapName,
    string PlayerName,
    int TrackNumber,
    string OutputPath,
    RenderSettingsSnapshot Settings,
    IReadOnlyList<NewRenderNode> Nodes);

public sealed record RenderTaskRecord(
    string Id,
    string MapName,
    string PlayerName,
    int TrackNumber,
    string OutputPath,
    RenderTaskStatus Status,
    int QueuePosition,
    string SettingsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double ElapsedSeconds,
    string? LastError);

public sealed record RenderNodeRecord(
    string Id,
    string TaskId,
    string ReplayPath,
    int StageNumber,
    int Sequence,
    RenderNodeStatus Status,
    int RetryCount,
    string? ClipPath,
    double ExpectedDurationSeconds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double ElapsedSeconds,
    string? LastError,
    int ExpectedTickCount);

public sealed record TaskLogRecord(
    long Id,
    string TaskId,
    string? NodeId,
    DateTimeOffset Timestamp,
    string Level,
    string Message);

/// <summary>Persisted fine-grained attempt state (plan P1-01).</summary>
public sealed record RenderAttemptRecord(
    string Id,
    string SessionId,
    string TaskId,
    string NodeId,
    int AttemptNumber,
    NodeExecutionStage Stage,
    string SequencePrefix,
    string? TempClipPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    string? LastError,
    RecordingFailureKind? FailureKind,
    CaptureCleanupState CleanupState,
    int? GameProcessId,
    DateTime? GameProcessStartedUtc,
    int? NetConPort,
    string? ExpectedMap,
    long FedCount,
    long SubmittedFrameCount,
    int? LastTgaIndex);

/// <summary>Persisted runner session for crash recovery (plan P1-10 / §7).</summary>
public sealed record RunnerSessionRecord(
    int? ProcessId,
    int? NetConPort,
    string? NetConPassword,
    string? TaskId,
    string? NodeId,
    string? ExePath,
    DateTime? ProcessStartedAt,
    string? GameSessionId,
    string? CaptureSessionId,
    string? SequencePrefix,
    string? OwnershipToken,
    string? WatchDirectory);
