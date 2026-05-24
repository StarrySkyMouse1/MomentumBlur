using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace mmod_record.Services;

public sealed record VideoProbeResult(
    int Width,
    int Height,
    int FrameCount,
    double? FrameRate);

public static class VideoProbeService
{
    public static async Task<VideoProbeResult> ProbeAsync(
        string ffmpegPath,
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobePath = ResolveFfprobePath(ffmpegPath)
            ?? throw new InvalidOperationException("找不到 ffprobe，无法读取视频信息。");

        var args =
            "-v error -select_streams v:0 " +
            "-show_entries stream=width,height,nb_frames,r_frame_rate " +
            "-of json " +
            $"\"{videoPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 ffprobe。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await ProcessCancellation.WaitForExitOrKillAsync(process, cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 读取失败: {error}");

        try
        {
            using var metadata = JsonDocument.Parse(output);
            if (!metadata.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array ||
                streams.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("ffprobe 未返回视频流。");
            }

            var stream = streams[0];
            var width = ReadJsonInt(stream, "width");
            var height = ReadJsonInt(stream, "height");
            var frames = ReadJsonInt(stream, "nb_frames");
            var frameRate = ParseFrameRate(stream);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("ffprobe 未能读取有效的视频宽高。");

            if (frames <= 0)
                frames = 1;

            return new VideoProbeResult(width, height, frames, frameRate);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ffprobe 返回的元数据不是有效 JSON。", ex);
        }
    }

    public static bool IsFrameRateMismatch(double? probedFps, int expectedFps, double tolerance = 2.0)
    {
        if (probedFps is null or <= 0)
            return false;

        return Math.Abs(probedFps.Value - expectedFps) > tolerance;
    }

    private static double? ParseFrameRate(JsonElement stream)
    {
        if (!stream.TryGetProperty("r_frame_rate", out var value))
            return null;

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den > 0)
        {
            return num / den;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single)
            ? single
            : null;
    }

    private static int ReadJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string? ResolveFfprobePath(string ffmpegPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(ffmpegPath);
            var candidate = !string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(dir, "ffprobe.exe")
                : "ffprobe.exe";

            return File.Exists(candidate) ? candidate : "ffprobe.exe";
        }
        catch
        {
            return "ffprobe.exe";
        }
    }
}
