namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Load-time normalization so old settings.json / old task SettingsJson remain
/// runnable: missing quality fields default to off, values are clamped, and the
/// legacy motion-blur semantics are preserved. The disk-safety percentage
/// default (10) is carried by the model defaults; Normalize only clamps to [0, 50].
/// </summary>
public static class SettingsMigration
{
    public static void Normalize(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.VideoProcessing = VideoProcessorCatalog.Normalize(settings.VideoProcessing);

        settings.SupersamplingMultiplier = Math.Clamp(settings.SupersamplingMultiplier, 1, 64);
        settings.Exposure = Math.Clamp(settings.Exposure, 0.05, 1.0);
        settings.ShutterAngle = Math.Clamp(settings.ShutterAngle, 180.0, 360.0);
        settings.IntermediateTargetBitrate = NormalizeTargetBitrate(settings.IntermediateTargetBitrate);
        settings.ObsCaptureFramerate = ProjectConstants.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
        settings.DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(settings.DiskSafetyFreePercent);
        settings.ForegroundCaptureFpsLimit = NormalizeForegroundCaptureFpsLimit(settings.ForegroundCaptureFpsLimit);
    }

    /// <summary>
    /// Normalize a deserialized task snapshot. Old JSON snapshots miss the new
    /// fields; defaulting to Legacy + all-off preserves their output, and a
    /// missing disk-safety percentage stays at the model default of 10.
    /// </summary>
    public static RenderSettingsSnapshot NormalizeSnapshot(RenderSettingsSnapshot snapshot)
    {
        var processing = snapshot.VideoProcessing is null
            ? VideoProcessorCatalog.Normalize(null)
            : VideoProcessorCatalog.Normalize(snapshot.VideoProcessing);
        return snapshot with
        {
            SupersamplingMultiplier = Math.Clamp(snapshot.SupersamplingMultiplier, 1, 64),
            Exposure = Math.Clamp(snapshot.Exposure, 0.05, 1.0),
            ShutterAngle = Math.Clamp(snapshot.ShutterAngle, 180.0, 360.0),
            TargetBitrate = NormalizeTargetBitrate(snapshot.TargetBitrate),
            DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(snapshot.DiskSafetyFreePercent),
            ForegroundCaptureFpsLimit = NormalizeForegroundCaptureFpsLimit(snapshot.ForegroundCaptureFpsLimit),
            VideoProcessing = processing,
        };
    }

    public static double NormalizeShutterAngle(double angle) => Math.Clamp(angle, 180.0, 360.0);

    public static int NormalizeForegroundCaptureFpsLimit(int value) =>
        Math.Clamp(value, 0, ProjectConstants.MaxForegroundCaptureFpsLimit);

    /// <summary>
    /// Bitrate is persisted as bits per second. A short-lived UI version exposed
    /// the raw field while users reasonably entered Mbps values such as 100,
    /// producing a 100 bps snapshot that Native later clamped to only 1 Mbps.
    /// Values 1..120 cannot be useful raw bitrates, so migrate them as Mbps.
    /// Other positive sub-1-Mbps values are raised to the encoder's real floor.
    /// </summary>
    public static int NormalizeTargetBitrate(int value)
    {
        if (value <= 0)
            return 0;
        if (value <= 120)
            return value * 1_000_000;
        return Math.Clamp(value, 1_000_000, 120_000_000);
    }
}
