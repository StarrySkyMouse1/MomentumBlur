using System.IO;
using mmod_record.Models;

namespace mmod_record.Services;

public static class ObsOutputPathHelper
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".mov", ".avi", ".flv", ".webm"];

    public static int NormalizeObsCaptureFramerate(int fps) =>
        SynthesisTiming.NormalizeObsCaptureFramerate(fps);

    public static int GetRecommendedBaselineN(int obsCaptureFramerate) =>
        SynthesisTiming.GetRealtimeBaselineN(obsCaptureFramerate);

    public static string ResolveOutputDirectory(UserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VideoOutputDirectory))
            return Path.GetFullPath(settings.VideoOutputDirectory.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "MomentumBlur");
    }

    public static string BuildFinalFileName(string inputVideoPath, RenderPreset preset, string session)
    {
        var stem = Path.GetFileNameWithoutExtension(inputVideoPath);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "video";

        return $"{stem}_{preset.Id}_60fps_{session}.mp4";
    }

    public static bool IsSupportedVideoExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) &&
        VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
