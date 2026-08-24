namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Load-time normalization so old settings.json / old task SettingsJson remain
/// runnable: missing quality fields default to off, values are clamped, and the
/// legacy motion-blur semantics are preserved.
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
        settings.IntermediateTargetBitrate = Math.Clamp(settings.IntermediateTargetBitrate, 0, 120_000_000);
        settings.ObsCaptureFramerate = ProjectConstants.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
    }

    /// <summary>
    /// Normalize a deserialized task snapshot. Old JSON snapshots miss the new
    /// fields; defaulting to Legacy + all-off preserves their output.
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
            TargetBitrate = Math.Clamp(snapshot.TargetBitrate, 0, 120_000_000),
            VideoProcessing = processing,
        };
    }

    public static double NormalizeShutterAngle(double angle) => Math.Clamp(angle, 180.0, 360.0);
}
