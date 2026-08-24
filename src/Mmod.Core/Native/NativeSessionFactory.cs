namespace Mmod.Core.Native;

using Mmod.Core.Models;
using Mmod.Core.Services;

/// <summary>
/// Builds NativeSessionOptions from a UserSettings snapshot. Both the TGA
/// pipeline and the OBS synthesis path go through here so the quality config
/// reaches Native identically for every capture mode.
/// </summary>
public static class NativeSessionFactory
{
    public static NativeSessionOptions BuildOptions(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var processing = settings.VideoProcessing is null
            ? VideoProcessorCatalog.Normalize(null)
            : VideoProcessorCatalog.Normalize(settings.VideoProcessing);

        return new NativeSessionOptions(
            MotionBlurMode: settings.MotionBlurWeightMode,
            ShutterAngle: SettingsMigration.NormalizeShutterAngle(settings.ShutterAngle),
            Effects: NativeProcessingMapper.Map(processing),
            TargetBitrate: Math.Clamp(settings.IntermediateTargetBitrate, 0, 120_000_000));
    }

    /// <summary>
    /// Deep snapshot of the processing config. Used when a task is created so
    /// later global-settings edits cannot change an already-created task.
    /// </summary>
    public static VideoProcessingSettings? SnapshotProcessing(UserSettings settings)
    {
        if (settings.VideoProcessing is null)
            return VideoProcessorCatalog.Normalize(null);
        return VideoProcessorCatalog.Normalize(settings.VideoProcessing);
    }

    public static IReadOnlyList<NativeProcessingMapper.NativeEffectDesc> MapEffects(UserSettings settings) =>
        NativeProcessingMapper.Map(SnapshotProcessing(settings));
}
