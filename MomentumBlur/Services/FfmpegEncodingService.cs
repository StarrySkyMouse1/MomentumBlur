using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace mmod_record.Services;

/// <summary>
/// 将合成视频与源视频音轨封装为最终 MP4 成片。
/// </summary>
public static class FfmpegEncodingService
{
    public static async Task ApplyPlaybackStretchAsync(
        string ffmpegPath,
        string inputVideoPath,
        string outputVideoPath,
        double setptsMultiplier,
        string videoEncoder,
        int crf,
        CancellationToken cancellationToken = default)
    {
        if (setptsMultiplier <= 1.001)
        {
            File.Copy(inputVideoPath, outputVideoPath, overwrite: true);
            return;
        }

        var scale = setptsMultiplier.ToString("0.######", CultureInfo.InvariantCulture);
        var enc = BuildStretchEncoderArguments(videoEncoder, crf);
        var args =
            $"-y -hide_banner -loglevel warning {FfmpegThreadingOptions.DecodeArgs()} {FfmpegThreadingOptions.FilterArgs(stateful: true)} " +
            $"-i \"{inputVideoPath}\" -vf \"setpts=PTS*{scale},fps=60\" -an {enc} \"{outputVideoPath}\"";

        PipelineLogger.Info($"ffmpeg 播放拉伸 setpts×{scale} {args}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await ProcessCancellation.WaitForExitOrKillAsync(process, cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"播放拉伸失败，退出码 {process.ExitCode}");
    }

    public static async Task<string> MuxFromSourceVideoAsync(
        string ffmpegPath,
        string synthesizedVideoPath,
        string sourceVideoPath,
        string finalOutputPath,
        double? audioTempo = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasAudioStream(ffmpegPath, sourceVideoPath))
        {
            File.Copy(synthesizedVideoPath, finalOutputPath, overwrite: true);
            PipelineLogger.Warn("源视频无音轨，仅输出无音频成片。");
            return finalOutputPath;
        }

        var audioFilter = BuildAudioTempoFilter(audioTempo);
        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-i \"{synthesizedVideoPath}\" -i \"{sourceVideoPath}\" " +
            $"-map 0:v:0 -map 1:a:0? -c:v copy {audioFilter}-c:a aac -b:a 192k -shortest \"{finalOutputPath}\"";

        PipelineLogger.Info($"ffmpeg mux {args}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await ProcessCancellation.WaitForExitOrKillAsync(process, cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"音频封装失败，退出码 {process.ExitCode}");

        return finalOutputPath;
    }

    public static bool HasAudioStream(string ffmpegPath, string videoPath)
    {
        var ffprobePath = ResolveFfprobePath(ffmpegPath);
        if (ffprobePath is null)
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 \"{videoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Trim().Equals("audio", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildStretchEncoderArguments(string videoEncoder, int crf)
    {
        var enc = string.IsNullOrWhiteSpace(videoEncoder) ? "libx264" : videoEncoder.Trim();
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
        return $"-c:v {x264} {FfmpegThreadingOptions.EncoderArgs(x264)} -crf {crf} -preset ultrafast -pix_fmt yuv420p";
    }

    private static string BuildAudioTempoFilter(double? tempo)
    {
        if (tempo is null || (tempo.Value >= 0.99 && tempo.Value <= 1.01))
            return string.Empty;

        var chain = BuildAtempoChain(tempo.Value);
        return $"-filter:a \"{chain}\" ";
    }

    private static string BuildAtempoChain(double tempo)
    {
        if (tempo <= 0)
            return "atempo=1";

        var filters = new List<string>();
        var remaining = tempo;

        while (remaining > 2.0 + 1e-6)
        {
            filters.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5 - 1e-6)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }

        filters.Add($"atempo={remaining.ToString("0.######", CultureInfo.InvariantCulture)}");
        return string.Join(',', filters);
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
