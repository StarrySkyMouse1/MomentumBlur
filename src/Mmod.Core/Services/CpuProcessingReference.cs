namespace Mmod.Core.Services;

/// <summary>
/// C# reference implementation of the V1 quality effects and motion-blur
/// weight builders. It mirrors the native math in frame_processing.cpp /
/// session.cpp so SmokeTest can verify behaviour without loading the DLL.
/// </summary>
public static class CpuProcessingReference
{
    public static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    public static float SmoothStep(float e0, float e1, float x)
    {
        if (e1 <= e0) return 0f;
        var t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    public static float Luma(ReadOnlySpan<float> rgb) =>
        0.2126f * rgb[0] + 0.7152f * rgb[1] + 0.0722f * rgb[2];

    public static float[] BoxBlur(Span<float> img, int width, int height, int x, int y, int radius)
    {
        var acc = new float[3];
        var span = 2 * radius + 1;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var cx = Math.Clamp(x + dx, 0, width - 1);
                var cy = Math.Clamp(y + dy, 0, height - 1);
                var pi = (cy * width + cx) * 3;
                acc[0] += img[pi];
                acc[1] += img[pi + 1];
                acc[2] += img[pi + 2];
            }
        }
        var inv = 1f / (span * span);
        return [acc[0] * inv, acc[1] * inv, acc[2] * inv];
    }

    /// <summary>p0=strength p1=motion-threshold p2=edge-protection</summary>
    public static void ApplyMotionAdaptiveDetail(Span<float> cur, Span<float> prevPrequality, Span<float> dst, int width, int height, params float[] p)
    {
        var strength = p.Length > 0 ? p[0] : 0f;
        var threshold = Math.Max(0.001f, p.Length > 1 ? p[1] : 0f);
        var edgeProtection = Clamp01(p.Length > 2 ? p[2] : 0f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pi = (y * width + x) * 3;
                var yc = Luma(cur.Slice(pi, 3));
                var yp = Luma(prevPrequality.Slice(pi, 3));
                var motion = Math.Abs(yc - yp) / 255f;
                var mask = SmoothStep(threshold, threshold * 2.5f, motion);

                var blur = BoxBlur(cur, width, height, x, y, 1);

                var lx0 = Luma(BoxBlur(cur, width, height, x - 1, y, 0));
                var lx1 = Luma(BoxBlur(cur, width, height, x + 1, y, 0));
                var ly0 = Luma(BoxBlur(cur, width, height, x, y - 1, 0));
                var ly1 = Luma(BoxBlur(cur, width, height, x, y + 1, 0));
                var gx = Math.Abs(lx1 - lx0);
                var gy = Math.Abs(ly1 - ly0);
                var edge = MathF.Sqrt(gx * gx + gy * gy) / 255f;
                var edgeMask = Clamp01(edge / 0.6f);
                var protect = 1f - edgeMask * edgeProtection;

                var t = mask * strength * protect;
                for (var ch = 0; ch < 3; ch++)
                    dst[pi + ch] = cur[pi + ch] + (blur[ch] - cur[pi + ch]) * t;
            }
        }
    }

    /// <summary>p0=strength p1=radius (1..2)</summary>
    public static void ApplyMicroDetailLowPass(Span<float> cur, Span<float> dst, int width, int height, params float[] p)
    {
        var strength = Clamp01(p.Length > 0 ? p[0] : 0f);
        var radius = (p.Length > 1 && p[1] >= 1.5f) ? 2 : 1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pi = (y * width + x) * 3;
                var blur = BoxBlur(cur, width, height, x, y, radius);
                for (var ch = 0; ch < 3; ch++)
                    dst[pi + ch] = cur[pi + ch] + (blur[ch] - cur[pi + ch]) * strength;
            }
        }
    }

    /// <summary>p0=strength p1=threshold</summary>
    public static void ApplyDebandNoDither(Span<float> cur, Span<float> dst, int width, int height, params float[] p)
    {
        var strength = Clamp01(p.Length > 0 ? p[0] : 0f);
        var threshold = Math.Max(0.001f, p.Length > 1 ? p[1] : 0f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pi = (y * width + x) * 3;
                var mean = BoxBlur(cur, width, height, x, y, 2);

                var centerDiff = 0f;
                for (var ch = 0; ch < 3; ch++)
                    centerDiff = Math.Max(centerDiff, Math.Abs(cur[pi + ch] - mean[ch]) / 255f);
                var flat = 1f - SmoothStep(threshold, threshold * 3f, centerDiff);
                var t = strength * flat;

                for (var ch = 0; ch < 3; ch++)
                    dst[pi + ch] = cur[pi + ch] + (mean[ch] - cur[pi + ch]) * t;
            }
        }
    }

    /// <summary>p0=strength p1=temporal-threshold</summary>
    public static void ApplyTemporalShimmerReduction(Span<float> cur, Span<float> prevPreprocessed, Span<float> dst, int width, int height, params float[] p)
    {
        var strength = Clamp01(p.Length > 0 ? p[0] : 0f);
        var threshold = Math.Max(0.001f, p.Length > 1 ? p[1] : 0f);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pi = (y * width + x) * 3;
                var diff = 0f;
                for (var ch = 0; ch < 3; ch++)
                    diff = Math.Max(diff, Math.Abs(cur[pi + ch] - prevPreprocessed[pi + ch]) / 255f);
                var k = 1f - SmoothStep(threshold * 0.5f, threshold * 2f, diff);

                var blur = BoxBlur(cur, width, height, x, y, 1);
                var hf = Math.Abs(Luma(cur.Slice(pi, 3)) - Luma(blur)) / 255f;
                var hfMask = SmoothStep(0.02f, 0.10f, hf);

                var t = strength * k * hfMask;
                for (var ch = 0; ch < 3; ch++)
                    dst[pi + ch] = cur[pi + ch] + (prevPreprocessed[pi + ch] - cur[pi + ch]) * t;
            }
        }
    }

    /// <summary>
    /// Gaussian legacy weights, mirroring the historical BuildWeights().
    /// sigma = max(0.05, exposure) * N * 0.5, centered, normalized.
    /// </summary>
    public static float[] BuildGaussianWeights(int blendFrames, double exposure)
    {
        var n = Math.Max(1, blendFrames);
        var weights = new float[n];
        if (n <= 1)
        {
            weights[0] = 1f;
            return weights;
        }
        var sigma = (float)Math.Max(0.05, exposure) * n * 0.5f;
        var center = (n - 1) * 0.5f;
        var sum = 0f;
        for (var i = 0; i < n; i++)
        {
            var x = i - center;
            weights[i] = MathF.Exp(-(x * x) / (2f * sigma * sigma));
            sum += weights[i];
        }
        if (sum > 0f)
            for (var i = 0; i < n; i++)
                weights[i] /= sum;
        return weights;
    }

    /// <summary>Centered box window for a physical shutter angle (180..360).</summary>
    public static float[] BuildShutterWeights(int blendFrames, double shutterAngle)
    {
        var n = Math.Max(1, blendFrames);
        var weights = new float[n];
        var angle = Math.Clamp(shutterAngle, 180.0, 360.0);
        var active = Math.Clamp((int)Math.Round(n * angle / 360.0, MidpointRounding.AwayFromZero), 1, n);
        var start = (n - active) / 2;
        var w = 1f / active;
        for (var i = 0; i < active; i++)
            weights[start + i] = w;
        return weights;
    }
}
