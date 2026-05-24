using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>
/// Experimental FFmpeg/OpenCL backend. It keeps the N-frame blend inside an
/// FFmpeg GPU filter graph instead of round-tripping every frame through C#.
/// </summary>
public static partial class FfmpegOpenClSynthesisService
{
    [GeneratedRegex(@"frame=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameProgressRegex();

    public static string DescribeSynthesis(
        RenderPreset preset,
        int synthesisBlendFrames,
        double exposure,
        string compositionBackend) =>
        $"源 {preset.ObsCaptureFramerate}fps · 配置 N={preset.BlendFrames} · 成片 {ProjectConstants.FinalOutputFramerate}fps\n" +
        $"时长：{SynthesisTiming.BuildTimingHint(preset.BlendFrames, preset.ObsCaptureFramerate)}\n" +
        $"后端：{CompositionBackendCatalog.GetHint(compositionBackend)}\n" +
        $"滤镜：FFmpeg OpenCL program_opencl inputs={Math.Max(1, synthesisBlendFrames)} + hwdownload + encoder";

    public static async Task RunAsync(
        string ffmpegPath,
        RenderPreset preset,
        int synthesisBlendFrames,
        double exposure,
        string inputVideoPath,
        string synthesizedVideoPath,
        string videoEncoder,
        int crf,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        var blendFrames = Math.Clamp(synthesisBlendFrames, 1, 120);
        var weights = BuildWeights(blendFrames, exposure);
        var kernelName = $"motion_blend_{blendFrames}";
        var kernelPath = WriteKernelFile(kernelName, weights);
        var relativeKernelPath = ToFfmpegRelativePath(kernelPath);
        var filterGraph = BuildFilterGraph(blendFrames, relativeKernelPath, kernelName);
        var encoder = BuildEncoderArguments(videoEncoder, crf);

        var args =
            $"-y -hide_banner -loglevel warning -nostats -progress pipe:2 " +
            "-init_hw_device opencl=ocl:0 -filter_hw_device ocl " +
            $"{FfmpegThreadingOptions.DecodeArgs()} " +
            $"-i \"{inputVideoPath}\" -filter_complex \"{filterGraph}\" -map \"[out]\" {encoder} \"{synthesizedVideoPath}\"";

        PipelineLogger.Info($"ffmpeg OpenCL resident synthesis {args}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 FFmpeg OpenCL 合成进程。");

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
        {
            throw new InvalidOperationException(
                $"FFmpeg OpenCL GPU resident 合成退出码 {process.ExitCode}。请确认 FFmpeg 支持 OpenCL、NVIDIA OpenCL 设备可用，并优先选择 h264_nvenc/hevc_nvenc。");
        }
    }

    private static string BuildFilterGraph(int blendFrames, string kernelPath, string kernelName)
    {
        var sb = new StringBuilder();

        sb.Append("[0:v]format=rgba,split=");
        sb.Append(blendFrames);
        for (var i = 0; i < blendFrames; i++)
            sb.Append(CultureInfo.InvariantCulture, $"[s{i}]");
        sb.Append(';');

        for (var i = 0; i < blendFrames; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[s{i}]");
            if (i > 0)
                sb.Append(CultureInfo.InvariantCulture, $"trim=start_frame={i},");

            sb.Append(CultureInfo.InvariantCulture,
                $"select='not(mod(n,{blendFrames}))',setpts=N/(60*TB),hwupload[o{i}];");
        }

        for (var i = 0; i < blendFrames; i++)
            sb.Append(CultureInfo.InvariantCulture, $"[o{i}]");

        sb.Append(CultureInfo.InvariantCulture,
            $"program_opencl=source={kernelPath}:kernel={kernelName}:inputs={blendFrames},");
        sb.Append("hwdownload,format=rgba,format=yuv420p[out]");

        return sb.ToString();
    }

    private static string WriteKernelFile(string kernelName, IReadOnlyList<double> weights)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "opencl_kernels");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{kernelName}.cl");
        File.WriteAllText(path, BuildKernelSource(kernelName, weights));
        return path;
    }

    private static string BuildKernelSource(string kernelName, IReadOnlyList<double> weights)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"__kernel void {kernelName}(");
        sb.AppendLine("    __write_only image2d_t dst,");
        sb.AppendLine("    unsigned int index,");
        for (var i = 0; i < weights.Count; i++)
        {
            var suffix = i == weights.Count - 1 ? ")" : ",";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    __read_only image2d_t src{i}{suffix}");
        }

        sb.AppendLine("{");
        sb.AppendLine("    const sampler_t sampler = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;");
        sb.AppendLine("    int2 p = (int2)(get_global_id(0), get_global_id(1));");
        sb.AppendLine("    float4 color = (float4)(0.0f, 0.0f, 0.0f, 0.0f);");
        for (var i = 0; i < weights.Count; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    color += read_imagef(src{i}, sampler, p) * {weights[i].ToString("0.########", CultureInfo.InvariantCulture)}f;");
        }

        sb.AppendLine("    write_imagef(dst, p, color);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ToFfmpegRelativePath(string path)
    {
        var relative = Path.GetRelativePath(AppContext.BaseDirectory, path)
            .Replace('\\', '/');
        return relative;
    }

    private static double[] BuildWeights(int blendFrames, double exposure)
    {
        if (blendFrames <= 1)
            return [1];

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
        for (var i = 0; i < weights.Length; i++)
            weights[i] /= sum;

        return weights;
    }

    private static string BuildEncoderArguments(string videoEncoder, int crf)
    {
        var enc = string.IsNullOrWhiteSpace(videoEncoder) ? "h264_nvenc" : videoEncoder.Trim();
        crf = Math.Clamp(crf, 0, 51);

        if (enc.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -preset p7 -tune hq -rc:v constqp -qp {crf} -pix_fmt yuv420p";

        if (enc.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -usage ultralowlatency -quality speed -rc cqp -qp {crf} -pix_fmt yuv420p";

        return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -crf {crf} -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
               "-x264-params nal-hrd=none:ref=1:bframes=0:me=dia:subme=0:trellis=0:8x8dct=0:weightp=0:aq-mode=0";
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
                    PipelineLogger.Warn($"ffmpeg OpenCL synthesis: {line}");

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
