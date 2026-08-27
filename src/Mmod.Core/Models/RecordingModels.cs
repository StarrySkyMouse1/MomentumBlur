namespace Mmod.Core.Models;

/// <summary>
/// One recording attempt's unique capture identity. A new CaptureSessionId and
/// a fresh TGA sequence prefix are created for every Attempt so stale files can
/// never bleed into a new attempt.
/// </summary>
public sealed record CaptureSessionInfo(
    string CaptureSessionId,
    string SequencePrefix,
    DateTime CreatedUtc)
{
    public static CaptureSessionInfo Create(string taskShortId, int nodeSequence, int attemptNumber)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var taskPart = (taskShortId.Length > 4 ? taskShortId[..4] : taskShortId).ToLowerInvariant();
        var sessionPart = sessionId[..6];
        var prefix = $"mmod_{taskPart}_{nodeSequence + 1:D3}_a{attemptNumber}_{sessionPart}_";
        return new CaptureSessionInfo(sessionId, prefix, DateTime.UtcNow);
    }
}

/// <summary>
/// Fine-grained internal execution stage of a node attempt. Kept separate from
/// the user-visible RenderNodeStatus; persisted in render_attempts.
/// </summary>
public enum NodeExecutionStage
{
    Created = 0,
    Preflight = 1,
    EnsuringGameSession = 2,
    ConnectingNetCon = 3,
    ChangingMap = 4,
    WaitingMapReady = 5,
    PreparingCaptureBaseline = 6,
    StartingMovie = 7,
    WaitingCaptureReady = 8,
    StartingReplay = 9,
    WaitingPlaybackEvidence = 10,
    Capturing = 11,
    RequestingMovieStop = 12,
    WaitingCaptureQuiescence = 13,
    FreezingWatcher = 14,
    DrainingFrames = 15,
    FinalizingEncoder = 16,
    ValidatingClip = 17,
    CommittingClip = 18,
    Completed = 19,
    CleaningUp = 20,
    RetryRecovery = 21,
    Canceled = 22,
    Failed = 23,
}

/// <summary>Stable failure classification driving retry/recovery policy.</summary>
public enum RecordingFailureKind
{
    InvalidInput = 0,
    UnsupportedReplay = 1,
    MapUnavailable = 2,
    MapReadinessTimeout = 3,
    ReplayRejected = 4,
    PlaybackEvidenceTimeout = 5,
    NetConLost = 6,
    GameExited = 7,
    CaptureStartFailed = 8,
    CaptureStopUnconfirmed = 9,
    TgaWriteStalled = 10,
    TgaQuiescenceTimeout = 11,
    PipelineFault = 12,
    EncoderFinalizeFault = 13,
    MediaValidationFault = 14,
    DiskPressure = 15,
    UserCanceled = 16,
    Unknown = 17,
}

/// <summary>Result of the strict endmovie path. Only CommandAcked / KnownAlreadyStopped may continue.</summary>
public enum StopMovieResult
{
    CommandAcked = 0,
    KnownAlreadyStopped = 1,
    NetConLost = 2,
    TimedOut = 3,
    CommandRejected = 4,
    ConsoleEvidence = 5,
}

public enum CleanupReason
{
    Completed = 0,
    Failed = 1,
    UserCanceled = 2,
    PauseAfterNode = 3,
    PipelineFault = 4,
    Fatal = 5,
    AppShutdown = 6,
}

public enum CaptureCleanupState
{
    NotRequired = 0,
    Clean = 1,
    Dirty = 2,
    GameRestartRequired = 3,
}

/// <summary>Result of a strict typed NetCon command.</summary>
public sealed record NetConCommandResult(
    string Command,
    string Marker,
    DateTime SentUtc,
    DateTime AckedUtc,
    IReadOnlyList<string> CapturedConsoleLines,
    string? MatchedFailurePattern)
{
    public bool Succeeded => MatchedFailurePattern is null;
    public TimeSpan RoundTrip => AckedUtc - SentUtc;
}

/// <summary>Playback evidence sample from a visual probe.</summary>
public sealed record PlaybackEvidenceSample(
    double ChangedBlockRatio,
    double MeanLumaDelta,
    bool IsSignificant,
    int ChangedBlockCount,
    int TotalBlockCount);

/// <summary>
/// How strongly the current playback-start evidence should be trusted.
/// VisualFallback means no explicit game-side replay state was available.
/// </summary>
public enum PlaybackEvidenceConfidence
{
    ExplicitState = 0,
    VisualConsecutive = 1,
    VisualFallback = 2,
    None = 3,
}

/// <summary>Positive map-readiness probe result.</summary>
public sealed record MapReadinessResult(
    string? CurrentMap,
    string ExpectedMap,
    bool IsReady,
    bool EngineResponsive,
    bool IsDegradedFallback,
    IReadOnlyList<string> ConsoleTail);

/// <summary>Media-level validation result of an MP4 clip.</summary>
public sealed record MediaProbeResult(
    bool IsValid,
    int Width,
    int Height,
    double Fps,
    long FrameCount,
    double DurationSeconds,
    string? Error)
{
    public static MediaProbeResult Fail(string error) => new(false, 0, 0, 0, 0, 0, error);
}

/// <summary>Pipeline finalization result with real counters.</summary>
public sealed record PipelineFinalizeResult(
    long SubmittedFrames,
    long ProducedFrames,
    int FirstFrameIndex,
    int LastFrameIndex,
    string OutputPath,
    bool FinishSucceeded,
    int FirstFrameWidth = 0,
    int FirstFrameHeight = 0);

/// <summary>User-visible pipeline state; UI is a projection of this.</summary>
public enum PipelineState
{
    Created = 0,
    Watching = 1,
    Processing = 2,
    FreezeRequested = 3,
    Draining = 4,
    Finalizing = 5,
    Finalized = 6,
    Faulted = 7,
    Disposed = 8,
}

/// <summary>Unified cleanup result; SecondaryErrors never override the primary error.</summary>
public sealed record CaptureCleanupResult(
    bool MovieStopConfirmed,
    bool TgaQuiescent,
    bool WatcherDrained,
    bool PipelineFinalized,
    bool GameStillHealthy,
    bool RequiresGameRestart,
    CaptureCleanupState CleanupState,
    IReadOnlyList<string> SecondaryErrors);

/// <summary>Identity of an owned game session for compatibility checks.</summary>
public sealed record GameSessionCompatibilityKey(
    string NormalizedGameRoot,
    string WatchDirectory);

/// <summary>Retry decision produced by the retry policy.</summary>
public enum RetryAction
{
    NoRetryNeedsUser = 0,
    SameSessionRetry = 1,
    ReloadMapRetry = 2,
    RestartGameRetry = 3,
}

public sealed record RetryDecision(
    RetryAction Action,
    bool CleanupSucceeded,
    string Reason);

/// <summary>Structured log line for recording state transitions.</summary>
public sealed record RecordingLogEntry(
    string TaskId,
    string? NodeId,
    string? AttemptId,
    string? CaptureSessionId,
    NodeExecutionStage Stage,
    string Event,
    long ElapsedMs,
    long FedCount,
    int CandidateCount,
    int PendingCount,
    DateTime? LastPhysicalWriteUtc,
    int? GamePid,
    RecordingFailureKind? FailureKind,
    string Message);

// ---- Disk health & performance preflight contracts (S1: pure data only) ----

/// <summary>
/// Watch-drive free-space safety state derived purely from total/free bytes and
/// the configured safety percent. Later runtime stages map this to drive
/// sampling and election of a controlled stop; S1 defines the contract only.
/// </summary>
public enum DiskSafetyState
{
    Disabled = 0,
    Normal = 1,
    Warning = 2,
    Critical = 3,
    Unavailable = 4,
}

/// <summary>Pure disk-safety evaluation result returned by DiskSafetyPolicy.Evaluate.</summary>
public sealed record DiskSafetyEvaluation(
    DiskSafetyState State,
    double FreePercent,
    int SafetyPercent,
    long SafetyBytes,
    int WarningPercent,
    long WarningBytes);

/// <summary>
/// Frozen watch-drive health snapshot (pure data). Building it (via
/// DiskSafetyPolicy.EvaluateSnapshot) performs no drive I/O; sampled values are
/// supplied by later runtime stages.
/// </summary>
public sealed record DiskHealthSnapshot(
    string DriveRoot,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double FreePercent,
    int SafetyPercent,
    long SafetyBytes,
    int WarningPercent,
    long WarningBytes,
    DiskSafetyState State,
    DateTimeOffset SampledAt);

/// <summary>Frame-quality processing backend reported by a performance preflight.</summary>
public enum ProcessingBackend
{
    Unknown = 0,
    Native = 1,
    CpuFallback = 2,
}

/// <summary>Encoder backend reported by a performance preflight.</summary>
public enum EncoderBackend
{
    Unknown = 0,
    Hardware = 1,
    Software = 2,
}

/// <summary>Overall performance-preflight verdict.</summary>
public enum PerformancePreflightRating
{
    Unknown = 0,
    Pass = 1,
    Marginal = 2,
    Fail = 3,
}

/// <summary>
/// Reusable performance-preflight result model (pure data). S1 defines the
/// contract only; no preflight execution is wired into the runtime in this stage.
/// </summary>
public sealed record PerformancePreflightResult(
    double ProducedFramesPerSecond,
    double ConsumedFramesPerSecond,
    double OutputFramesPerSecond,
    double ConsumptionRatio,
    long PeakPendingFrames,
    long PeakPendingBytes,
    ProcessingBackend QualityBackend,
    EncoderBackend EncoderBackend,
    PerformancePreflightRating Rating,
    DateTimeOffset CreatedAt);
