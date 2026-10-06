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
        if (settings.MotionBlurWeightMode == MotionBlurWeightMode.LegacyGaussianExposure)
        {
            // Exposure controls Gaussian sigma, NOT SVR's box exposure ratio.
            // Report the equivalent equal-weight sample count, 1 / sum(w^2),
            // so a large supersampling N is not mistaken for full shutter coverage.
            var n = Math.Max(1, blend);
            var sigma = Math.Max(0.05, settings.Exposure) * n * 0.5;
            double sum = 0, sumSquared = 0;
            for (var i = 0; i < n; i++)
            {
                var x = i - (n - 1) * 0.5;
                var weight = Math.Exp(-x * x / (2 * sigma * sigma));
                sum += weight;
                sumSquared += weight * weight;
            }
            sb.Append($" effectiveSamples≈{sum * sum / sumSquared:0.##}/{n}");
            sb.Append($" shutterAngle={settings.ShutterAngle:0}°未启用（Legacy不是SVR矩形曝光）");
        }
        return sb.ToString();
    }
}
