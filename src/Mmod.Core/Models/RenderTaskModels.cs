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

public sealed record RenderSettingsSnapshot(
    int SupersamplingMultiplier,
    double Exposure,
    string WatchDirectory,
    string OutputDirectory,
    string GameRootPath,
    bool HideHud,
    int OutputFramerate,
    int TargetBitrate);

public sealed record NewRenderNode(
    string ReplayPath,
    int StageNumber,
    int Sequence,
    double ExpectedDurationSeconds);

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
    string? LastError);

public sealed record TaskLogRecord(
    long Id,
    string TaskId,
    string? NodeId,
    DateTimeOffset Timestamp,
    string Level,
    string Message);
