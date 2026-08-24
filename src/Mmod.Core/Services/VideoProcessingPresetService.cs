namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Presets are pure config writers: they only set module enabled flags and
/// parameters. They never introduce a second processing path in Native.
/// </summary>
public static class VideoProcessingPresetService
{
    public static VideoProcessingSettings Apply(string presetId)
    {
        var normalizedPresetId = NormalizePresetId(presetId);
        var settings = VideoProcessingSettings.CreateEmpty();
        settings.PresetId = normalizedPresetId;

        foreach (var def in VideoProcessorCatalog.Modules)
            settings.Modules.Add(VideoProcessorCatalog.BuildDefaultConfig(def));

        switch (normalizedPresetId)
        {
            case VideoProcessingPresetIds.BilibiliLowBitrate:
                Enable(settings, VideoProcessorCatalog.MotionAdaptiveDetail,
                    ("strength", 0.35), ("motion-threshold", 0.08), ("edge-protection", 0.6));
                Enable(settings, VideoProcessorCatalog.MicroDetailLowPass,
                    ("strength", 0.25), ("radius", 1.0));
                // Deband / Temporal Shimmer stay off in V1 recommended preset.
                break;

            case VideoProcessingPresetIds.Off:
            default:
                // Everything stays disabled.
                break;
        }

        return VideoProcessorCatalog.Normalize(settings);
    }

    /// <summary>
    /// Detect whether the current module config exactly matches a known preset.
    /// If it does, return that preset id; otherwise return "custom".
    /// </summary>
    public static string DetectPresetId(VideoProcessingSettings? settings)
    {
        if (settings is null || VideoProcessorCatalog.HasNoEnabledModules(settings))
            return VideoProcessingPresetIds.Off;

        var recommended = Apply(VideoProcessingPresetIds.BilibiliLowBitrate);
        if (SettingsEqual(recommended, settings))
            return VideoProcessingPresetIds.BilibiliLowBitrate;

        return VideoProcessingPresetIds.Custom;
    }

    public static string NormalizePresetId(string presetId) =>
        VideoProcessorCatalog.Presets.Any(p => string.Equals(p.Id, presetId, StringComparison.Ordinal))
            ? presetId
            : VideoProcessingPresetIds.Custom;

    private static void Enable(VideoProcessingSettings settings, string moduleId, params (string Key, double Value)[] parameters)
    {
        var config = settings.Modules.FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.Ordinal));
        if (config is null)
            return;
        config.Enabled = true;
        foreach (var (key, value) in parameters)
            config.Parameters[key] = value;
    }

    private static bool SettingsEqual(VideoProcessingSettings a, VideoProcessingSettings b)
    {
        if (a.Modules.Count != b.Modules.Count)
            return false;
        foreach (var am in a.Modules)
        {
            var bm = b.Modules.FirstOrDefault(m => string.Equals(m.Id, am.Id, StringComparison.Ordinal));
            if (bm is null || bm.Enabled != am.Enabled)
                return false;
            if (am.Parameters.Count != bm.Parameters.Count)
                return false;
            foreach (var (key, value) in am.Parameters)
            {
                if (!bm.Parameters.TryGetValue(key, out var bv) || Math.Abs(bv - value) > 1e-9)
                    return false;
            }
        }
        return true;
    }
}
