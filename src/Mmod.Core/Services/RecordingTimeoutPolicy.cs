namespace Mmod.Core.Services;

/// <summary>
/// Single source of truth for every timeout / threshold / retry policy used by
/// the recording state machine. No magic numbers scattered through the code.
/// </summary>
public sealed record RecordingTimeoutPolicy
{
    public static RecordingTimeoutPolicy Default { get; } = new();

    public TimeSpan GameLaunchTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan NetConConnectTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan NetConReconnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MapLoadTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan MapProbeInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan MapSettleDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan StartMovieTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CaptureReadyTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReplayWatchTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PlaybackEvidenceTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReplayWatchSettle { get; init; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan NoPhysicalTgaProgressTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan NoPipelineProgressTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ProgressSampleInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan StopMovieTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan TgaQuiescenceQuietWindow { get; init; } = TimeSpan.FromMilliseconds(900);
    public TimeSpan TgaQuiescenceHardTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan EncoderFinalizeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan OwnedGameGracefulQuitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan OwnedGameKillWait { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CleanupHardLimit { get; init; } = TimeSpan.FromSeconds(45);

    public int CaptureReadyMinFrames { get; init; } = 3;
    public int MaxAttempts { get; init; } = 3; // 1st attempt + 2 retries
    public int PlaybackEvidenceRequiredConsecutive { get; init; } = 3;

    /// <summary>Minimum interval between watch-drive health samples (time-throttled, not per frame).</summary>
    public TimeSpan DiskHealthSampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive Unavailable samples before recording fails with DiskHealthUnavailable.</summary>
    public int DiskHealthUnavailableMaxConsecutiveSamples { get; init; } = 5;

    /// <summary>Playback evidence thresholds.</summary>
    public double EvidenceChangedBlockRatioThreshold { get; init; } = 0.08;
    public double EvidenceMeanLumaDeltaThreshold { get; init; } = 4.0;
    public int EvidenceBlockSize { get; init; } = 16;
}
