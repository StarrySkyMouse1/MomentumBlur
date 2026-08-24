namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>Builds short human-readable summaries of a processing config.</summary>
public static class VideoProcessingSummary
{
    public static string Build(VideoProcessingSettings? settings)
    {
        if (settings is null)
            return "画质处理 0 项";

        var enabled = settings.Modules
            .Where(m => m.Enabled)
            .Select(m => VideoProcessorCatalog.Modules.FirstOrDefault(d => string.Equals(d.Id, m.Id, StringComparison.Ordinal)))
            .Where(d => d is not null)
            .Select(d => d!.DisplayName)
            .ToList();

        if (enabled.Count == 0)
            return "画质处理 0 项";

        return $"画质处理 {enabled.Count} 项（{string.Join("、", enabled)}）";
    }

    public static string BuildPresetLabel(string presetId)
    {
        foreach (var p in VideoProcessorCatalog.Presets)
        {
            if (string.Equals(p.Id, presetId, StringComparison.Ordinal))
                return p.DisplayName;
        }
        return "自定义";
    }
}
