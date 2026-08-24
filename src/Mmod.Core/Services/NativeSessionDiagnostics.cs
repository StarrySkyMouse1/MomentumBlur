namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>One-line session diagnostics for logs (plan M: record what a session uses).</summary>
public static class NativeSessionDiagnostics
{
    public static string Describe(UserSettings settings, int width, int height, int blend, int outputFps)
    {
        var mode = settings.MotionBlurWeightMode == MotionBlurWeightMode.ShutterAngle
            ? $"Shutter {settings.ShutterAngle:0}°"
            : $"Legacy Exposure {settings.Exposure:0.##}";
        var enabledEffects = settings.VideoProcessing?.Modules
            .Where(m => m.Enabled)
            .Select(m => m.Id)
            .ToList() ?? [];

        var sb = new System.Text.StringBuilder(
            $"Session: {width}x{height}@{outputFps}fps blend={blend} motionBlur={mode}");
        if (settings.IntermediateTargetBitrate > 0)
            sb.Append($" targetBitrate={settings.IntermediateTargetBitrate}");
        if (enabledEffects.Count > 0)
            sb.Append($" effects=[{string.Join(",", enabledEffects)}]");
        else
            sb.Append(" effects=[]");
        return sb.ToString();
    }
}
