namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Fake-able boundaries of the recording state machine. Production classes
/// (MomentumNetConClient, TgaDirectoryWatcher, TgaPipelineOrchestrator, ...)
/// implement these so the state machine can be tested deterministically without
/// starting Momentum or writing real TGA files.
/// </summary>

/// <summary>Command channel to the Momentum mod console.</summary>
public interface INetConClient : IAsyncDisposable
{
    event Action<string>? OutputReceived;
    bool IsConnected { get; }

    Task ConnectAsync(int port, string password, TimeSpan timeout, CancellationToken token);

    Task SendAsync(string command, CancellationToken token);

    /// <summary>Strict checked command: ACK marker with failure-pattern detection.</summary>
    Task<NetConCommandResult> ExecuteStrictAsync(
        string command,
        TimeSpan timeout,
        IReadOnlyList<string> failurePatterns,
        CancellationToken token);

    /// <summary>Legacy unchecked execute (probe/diagnostic only).</summary>
    Task ExecuteAsync(string command, TimeSpan timeout, CancellationToken token);
}

/// <summary>Session-scoped TGA directory observation.</summary>
public interface ITgaCaptureWatcher : IDisposable
{
    int PendingCount { get; }
    int CandidateCount { get; }
    DateTime? LastPhysicalFileWriteUtc { get; }
    DateTime? LastStableFrameUtc { get; }
    int? LastAcceptedFrameIndex { get; }
    int MaxObservedFrameIndex { get; }
    int SessionFileCount { get; }
    bool HasUnstableFiles { get; }
    bool IsFrozen { get; }
    string SequencePrefix { get; }

    /// <summary>Full directory scan for the current prefix.</summary>
    void ForceFullScan();

    /// <summary>
    /// Waits until physical quiescence: no new/changed files for the session
    /// prefix, no unstable candidates, and at least one final full scan kept
    /// quiet. Throws on hard timeout (never silently succeeds).
    /// </summary>
    Task WaitForQuiescenceAsync(TimeSpan quietWindow, TimeSpan hardTimeout, CancellationToken token);

    /// <summary>Freezes the watcher; no new files accepted after this point.</summary>
    void Freeze();

    /// <summary>Removes a pending frame in deterministic (lowest index) order.</summary>
    bool TryTake(int frameIndex, out string filePath);

    bool TryGetMinPendingFrameIndex(out int frameIndex);

    /// <summary>Deletes only files belonging to this session prefix.</summary>
    void CleanupSessionFiles();
}

/// <summary>Capture pipeline (TGA read → Native submit → encoder).</summary>
public interface ICapturePipeline : IAsyncDisposable
{
    long FedCount { get; }
    PipelineState State { get; }
    Exception? Fault { get; }
    bool IsFaulted { get; }
    string? OutputPath { get; }
    string? CaptureSessionId { get; }
    ITgaCaptureWatcher Watcher { get; }

    /// <summary>
    /// Immutable runtime capture-performance snapshot (M3). All rates come
    /// from real counters; never NaN/Infinity.
    /// </summary>
    PerformanceSnapshot Performance { get; }

    /// <summary>Task that completes when the processing loop exits (faulted on error).</summary>
    Task Completion { get; }

    /// <summary>Throws the pipeline fault if one occurred.</summary>
    void ThrowIfFaulted();

    Task WaitUntilFedAsync(int minimumFed, TimeSpan timeout, CancellationToken token);
    Task WaitUntilActivityAsync(TimeSpan timeout, CancellationToken token);
    void ResetActivityTracking();

    /// <summary>True once playback evidence was established (robust probe).</summary>
    bool HasVisualChange { get; }

    /// <summary>FedCount of the first frame with playback evidence.</summary>
    int? ActivityAnchorFrame { get; }

    int? LastVisualChangeFrame { get; }

    /// <summary>
    /// Deterministic shutdown: request loop stop, await completion, run
    /// final full scan + quiescence, freeze watcher, drain pending frames,
    /// finish the native session. Any fault/timeout propagates.
    /// </summary>
    Task<PipelineFinalizeResult> FinalizeAsync(RecordingTimeoutPolicy timeouts, CancellationToken token);
}

/// <summary>Positive map-readiness probe.</summary>
public interface IMapReadinessProbe
{
    Task<MapReadinessResult> ProbeAsync(
        INetConClient netCon,
        string expectedMap,
        RecordingTimeoutPolicy timeouts,
        Action<string>? log,
        CancellationToken token);
}

/// <summary>Robust visual playback evidence probe (block-grid based).</summary>
public interface IPlaybackEvidenceProbe
{
    /// <summary>Establishes the pre-replay baseline frame.</summary>
    void SetBaseline(ReadOnlySpan<byte> bgra, int width, int height);

    PlaybackEvidenceSample Sample(ReadOnlySpan<byte> bgra, int width, int height);

    /// <summary>True when recent consecutive samples were significant.</summary>
    bool IsPlaybackStarted { get; }

    int ConsecutiveSignificantCount { get; }

    int RequiredConsecutive { get; }
}

/// <summary>Media-level MP4 validation.</summary>
public interface IMediaProbe
{
    MediaProbeResult Probe(string path, int? expectedWidth = null, int? expectedHeight = null, double? expectedFps = null);
}

/// <summary>Deterministic clock injection for tests.</summary>
public interface IRecordingClock
{
    DateTime UtcNow { get; }
}

/// <summary>Atomic file commit abstraction.</summary>
public interface IFileCommitter
{
    void Commit(string tempPath, string destinationPath);
}

/// <summary>
/// Monitors owned game-session health during capture: process exit, optional
/// NetCon heartbeat, watch-drive disk health. Lets the recorder fail in
/// seconds when the game dies instead of waiting out a progress timeout.
/// </summary>
public interface IGameSessionHealthMonitor
{
    /// <summary>Task completing (possibly faulted) when the owned game exits.</summary>
    Task GameExitedTask { get; }

    bool IsGameRunning { get; }

    /// <summary>
    /// Samples the TGA watch directory's volume and returns a full frozen
    /// disk-health snapshot. Implementations must never return null; failures
    /// to sample the drive surface as an Unavailable snapshot. 0% protection
    /// is reported as Disabled without requiring a drive read.
    /// </summary>
    DiskHealthSnapshot GetWatchDiskHealth(int safetyPercent);
}

/// <summary>Fake-able game process controller for the node coordinator.</summary>
public interface IGameProcessController : IAsyncDisposable
{
    INetConClient NetCon { get; }
    bool OwnsProcess { get; }
    bool IsGameRunning { get; }
    string? GameSessionId { get; }
    int? ProcessId { get; }
    DateTime? ProcessStartTimeUtc { get; }
    string? ExePath { get; }
    Task ExitTask { get; }

    Task StartAsync(string gameRoot, CancellationToken token);

    /// <summary>Strict owned shutdown: graceful quit → bounded wait → kill fallback.</summary>
    Task ShutdownOwnedProcessAsync(RecordingTimeoutPolicy timeouts, CancellationToken cleanupToken);

    GameSessionCompatibilityKey BuildCompatibilityKey(string gameRoot, string watchDirectory);
}
