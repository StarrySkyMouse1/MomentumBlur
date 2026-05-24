using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>
/// 使用 FFmpeg tmix 对 OBS 录制视频做离线运动模糊合成。
/// </summary>
public static partial class FfmpegSynthesisService
{
    [GeneratedRegex(@"frame=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameProgressRegex();

    public static string DescribeSynthesis(
        RenderPreset preset,
        int synthesisBlendFrames,
        double playbackSpeedScale,
        double exposure,
        string compositionBackend) =>
        $"源 {preset.ObsCaptureFramerate}fps · 配置 N={preset.BlendFrames} · 成片 {ProjectConstants.FinalOutputFramerate}fps\n" +
        $"时长：{SynthesisTiming.BuildTimingHint(preset.BlendFrames, preset.ObsCaptureFramerate)}\n" +
        $"后端：{CompositionBackendCatalog.GetHint(compositionBackend)}\n" +
        $"滤镜：{BuildSynthesisVideoFilter(synthesisBlendFrames, ToTmixWeights(synthesisBlendFrames, exposure), playbackSpeedScale)}";

    public static async Task RunAsync(
        string ffmpegPath,
        RenderPreset preset,
        int synthesisBlendFrames,
        double playbackSpeedScale,
        double exposure,
        string inputVideoPath,
        string synthesizedVideoPath,
        string videoEncoder,
        int crf,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        exposure = Math.Clamp(exposure, 0.05, 1.0);
        crf = Math.Clamp(crf, 0, 51);
        var encoder = string.IsNullOrWhiteSpace(videoEncoder) ? "libx264" : videoEncoder.Trim();
        var blendFrames = Math.Max(1, synthesisBlendFrames);
        var weights = ToTmixWeights(blendFrames, exposure);
        var vf = BuildSynthesisVideoFilter(blendFrames, weights, playbackSpeedScale);
        var enc = BuildEncoderArguments(encoder, crf);

        var args =
            $"-y -hide_banner -loglevel warning -nostats -progress pipe:2 {FfmpegThreadingOptions.DecodeArgs()} {FfmpegThreadingOptions.FilterArgs(stateful: true)} " +
            $"-i \"{inputVideoPath}\" -vf \"{vf}\" {enc} \"{synthesizedVideoPath}\"";

        PipelineLogger.Info($"ffmpeg 合成 {args}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 FFmpeg 合成进程。");

        using var stderrCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stderrTask = PumpStderrAsync(process.StandardError, onProgress, stderrCts.Token);

        await ProcessCancellation.WaitForExitOrKillAsync(
            process,
            cancellationToken,
            TimeSpan.FromHours(2));
        stderrCts.Cancel();

        try
        {
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // ignored
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 合成退出码 {process.ExitCode}");
    }

    private static string BuildSynthesisVideoFilter(int blendFrames, string weights, double playbackSpeedScale)
    {
        if (blendFrames <= 1)
            return playbackSpeedScale < 0.999999
                ? $"setpts=PTS*{SynthesisTiming.FormatMultiplier(playbackSpeedScale)},fps=60"
                : "fps=60";

        return $"tmix=frames={blendFrames}:weights='{weights}'{SynthesisTiming.BuildVideoFilterSuffix(playbackSpeedScale)}";
    }

    private static string BuildEncoderArguments(string videoEncoder, int crf)
    {
        var enc = videoEncoder.Trim();
        crf = Math.Clamp(crf, 0, 51);

        if (enc.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -preset p7 -tune hq -rc:v constqp -qp {crf} -pix_fmt yuv420p";
        }

        if (enc.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -usage ultralowlatency -quality speed -rc cqp -qp {crf} -pix_fmt yuv420p";
        }

        var x264 = enc.Equals("libx264", StringComparison.OrdinalIgnoreCase) ? "libx264" : enc;
        return $"-c:v {x264} {FfmpegThreadingOptions.EncoderArgs(x264)} -crf {crf} -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
               "-x264-params nal-hrd=none:ref=1:bframes=0:me=dia:subme=0:trellis=0:8x8dct=0:weightp=0:aq-mode=0";
    }

    private static string ToTmixWeights(int blendFrames, double exposure)
    {
        if (blendFrames <= 1)
            return "1";

        exposure = Math.Clamp(exposure, 0.05, 1.0);
        var weights = new double[blendFrames];
        var center = (blendFrames - 1) / 2.0;
        var sigma = Math.Max(0.5, blendFrames * exposure * 0.5);

        for (var i = 0; i < blendFrames; i++)
        {
            var d = i - center;
            weights[i] = Math.Exp(-(d * d) / (2 * sigma * sigma));
        }

        var sum = weights.Sum();
        return string.Join(" ", weights.Select(w =>
            (w / sum).ToString("0.######", CultureInfo.InvariantCulture)));
    }

    private static async Task PumpStderrAsync(
        StreamReader reader,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                if (!string.IsNullOrWhiteSpace(line))
                    PipelineLogger.Warn($"ffmpeg 合成: {line}");

                var match = FrameProgressRegex().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var n))
                    onProgress?.Invoke(n);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
