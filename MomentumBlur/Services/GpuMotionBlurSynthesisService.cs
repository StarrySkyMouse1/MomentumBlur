using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComputeSharp;
using mmod_record.Models;

namespace mmod_record.Services;

public sealed class GpuMotionBlurSynthesisService
{
    private sealed record VideoProbe(int Width, int Height, int InputFrames);
    private const int MaxBatchedInputPixels = 200_000_000;

    public static async Task<int> RunAsync(
        string ffmpegPath,
        RenderPreset preset,
        int synthesisBlendFrames,
        double exposure,
        string inputVideoPath,
        string synthesizedVideoPath,
        string videoEncoder,
        int crf,
        int knownInputFrames,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        using var gpuSlot = await GpuSynthesisConcurrency.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);

        var probe = await ProbeVideoAsync(ffmpegPath, inputVideoPath, knownInputFrames, cancellationToken);
        var blendFrames = Math.Max(1, synthesisBlendFrames);
        var totalOutputFrames = Math.Max(1, probe.InputFrames / blendFrames);
        var framePixelCount = checked(probe.Width * probe.Height);
        var frameByteCount = checked(framePixelCount * 4);
        var weights = BuildWeights(blendFrames, exposure);

        PipelineLogger.Info(
            $"GPU 合成开始: {inputVideoPath} -> {synthesizedVideoPath}, " +
            $"{probe.Width}x{probe.Height}, input={probe.InputFrames}, output={totalOutputFrames}, blend={blendFrames}");

        using var decoder = StartDecoder(ffmpegPath, inputVideoPath, probe.Width, probe.Height);
        using var encoder = StartEncoder(
            ffmpegPath,
            synthesizedVideoPath,
            probe.Width,
            probe.Height,
            videoEncoder,
            crf);

        using var decoderStderrCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var encoderStderrCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decoderStderr = PumpProcessStderrAsync("ffmpeg GPU decode", decoder.StandardError, decoderStderrCts.Token);
        var encoderStderr = PumpProcessStderrAsync("ffmpeg GPU encode", encoder.StandardError, encoderStderrCts.Token);

        var device = GraphicsDevice.GetDefault();
        var groupPixelCount = checked(framePixelCount * blendFrames);
        var useBatchedShader = groupPixelCount <= MaxBatchedInputPixels;
        PipelineLogger.Info(
            $"GPU blend mode={(useBatchedShader ? "batched" : "streaming")}, " +
            $"framePixels={framePixelCount}, groupPixels={groupPixelCount}, maxBatchedPixels={MaxBatchedInputPixels}");
        using ReadWriteBuffer<uint> inputBuffer = device.AllocateReadWriteBuffer<uint>(
            useBatchedShader ? groupPixelCount : framePixelCount);
        using ReadWriteBuffer<float> weightsBuffer = device.AllocateReadWriteBuffer<float>(blendFrames);
        using ReadWriteBuffer<float4> accumulatorBuffer = useBatchedShader
            ? null!
            : device.AllocateReadWriteBuffer<float4>(framePixelCount);
        using ReadWriteBuffer<uint> outputBuffer = device.AllocateReadWriteBuffer<uint>(framePixelCount);
        weightsBuffer.CopyFrom(weights);

        var inputBytes = new byte[useBatchedShader ? checked(frameByteCount * blendFrames) : frameByteCount];
        var outputPixels = new uint[framePixelCount];
        var written = 0;

        await using var encoderInput = new BufferedStream(encoder.StandardInput.BaseStream, 8 * 1024 * 1024);
        await using var decoderOutput = new BufferedStream(decoder.StandardOutput.BaseStream, 8 * 1024 * 1024);

        try
        {
            for (var outputIndex = 0; outputIndex < totalOutputFrames; outputIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (useBatchedShader)
                {
                    if (!await TryReadExactAsync(decoderOutput, inputBytes, cancellationToken))
                        goto Finish;

                    var pixels = MemoryMarshal.Cast<byte, uint>(inputBytes);
                    inputBuffer.CopyFrom(pixels);
                    device.For(
                        framePixelCount,
                        new BlendBatchedBgraShader(
                            inputBuffer,
                            weightsBuffer,
                            outputBuffer,
                            framePixelCount,
                            blendFrames));
                }
                else
                {
                    device.For(framePixelCount, new ClearAccumulatorShader(accumulatorBuffer));

                    for (var i = 0; i < blendFrames; i++)
                    {
                        if (!await TryReadExactAsync(decoderOutput, inputBytes, cancellationToken))
                            goto Finish;

                        var pixels = MemoryMarshal.Cast<byte, uint>(inputBytes);
                        inputBuffer.CopyFrom(pixels);
                        device.For(framePixelCount, new AccumulateBgraShader(inputBuffer, accumulatorBuffer, weights[i]));
                    }

                    device.For(framePixelCount, new PackBgraShader(accumulatorBuffer, outputBuffer));
                }

                outputBuffer.CopyTo(outputPixels);
                var outputBytes = MemoryMarshal.AsBytes(outputPixels.AsSpan());
                encoderInput.Write(outputBytes);

                written++;
                onProgress?.Invoke(written, totalOutputFrames);
            }
        }
        catch (OperationCanceledException)
        {
            ProcessCancellation.KillTree(decoder);
            ProcessCancellation.KillTree(encoder);
            throw;
        }

    Finish:
        try
        {
            await encoderInput.FlushAsync(cancellationToken);
            encoder.StandardInput.Close();

            decoderStderrCts.Cancel();
            encoderStderrCts.Cancel();

            await ProcessCancellation.WaitForExitOrKillAsync(
                encoder,
                cancellationToken,
                TimeSpan.FromHours(2));
        }
        catch (OperationCanceledException)
        {
            ProcessCancellation.KillTree(decoder);
            ProcessCancellation.KillTree(encoder);
            throw;
        }

        if (!decoder.HasExited)
        {
            try
            {
                decoder.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            await Task.WhenAll(decoderStderr, encoderStderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // ignored
        }

        if (encoder.ExitCode != 0)
            throw new InvalidOperationException($"GPU 合成编码 FFmpeg 退出码 {encoder.ExitCode}");

        if (written == 0)
            throw new InvalidOperationException("GPU 合成未输出任何帧。");

        return written;
    }

    private static Process StartDecoder(string ffmpegPath, string inputVideoPath, int width, int height)
    {
        var args =
            $"-hide_banner -loglevel warning {FfmpegThreadingOptions.DecodeArgs()} -i \"{inputVideoPath}\" " +
            $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} pipe:1";

        return StartProcess(ffmpegPath, args, redirectStdin: false, redirectStdout: true);
    }

    private static Process StartEncoder(
        string ffmpegPath,
        string synthesizedVideoPath,
        int width,
        int height,
        string videoEncoder,
        int crf)
    {
        var encoder = BuildEncoderArguments(videoEncoder, crf);
        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} " +
            $"-framerate {ProjectConstants.FinalOutputFramerate} -i pipe:0 {encoder} \"{synthesizedVideoPath}\"";

        return StartProcess(ffmpegPath, args, redirectStdin: true, redirectStdout: false);
    }

    private static Process StartProcess(string fileName, string args, bool redirectStdin, bool redirectStdout)
    {
        PipelineLogger.Info($"ffmpeg GPU stage {args}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = redirectStdin,
                RedirectStandardOutput = redirectStdout,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 FFmpeg GPU 合成进程。");

        return process;
    }

    private static async Task<VideoProbe> ProbeVideoAsync(
        string ffmpegPath,
        string inputVideoPath,
        int knownInputFrames,
        CancellationToken cancellationToken)
    {
        var ffprobePath = ResolveFfprobePath(ffmpegPath);
        if (ffprobePath is null)
            throw new InvalidOperationException("找不到 ffprobe，GPU 合成需要 ffprobe 读取输入视频分辨率。");

        var args =
            "-v error -select_streams v:0 " +
            "-show_entries stream=width,height,nb_frames " +
            "-of json " +
            $"\"{inputVideoPath}\"";

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
            throw new InvalidOperationException($"ffprobe 读取输入视频失败: {error}");

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
            var probedFrames = ReadJsonInt(stream, "nb_frames");
            var frames = knownInputFrames > 0 ? knownInputFrames : probedFrames;

            if (width <= 0 || height <= 0 || frames <= 0)
                throw new InvalidOperationException("ffprobe 未能读取有效的视频宽高或帧数。");

            return new VideoProbe(width, height, frames);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ffprobe 返回的元数据不是有效 JSON。", ex);
        }
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

            if (File.Exists(candidate))
                return candidate;

            return "ffprobe.exe";
        }
        catch
        {
            return "ffprobe.exe";
        }
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    private static float[] BuildWeights(int blendFrames, double exposure)
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
        return weights.Select(w => (float)(w / sum)).ToArray();
    }

    private static string BuildEncoderArguments(string videoEncoder, int crf)
    {
        var enc = string.IsNullOrWhiteSpace(videoEncoder) ? "libx264" : videoEncoder.Trim();
        crf = Math.Clamp(crf, 0, 51);

        if (enc.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -preset p7 -tune hq -rc:v constqp -qp {crf} -pix_fmt yuv420p";

        if (enc.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -usage ultralowlatency -quality speed -rc cqp -qp {crf} -pix_fmt yuv420p";

        return $"-c:v {enc} {FfmpegThreadingOptions.EncoderArgs(enc)} -crf {crf} -preset ultrafast -tune zerolatency -pix_fmt yuv420p " +
               "-x264-params nal-hrd=none:ref=1:bframes=0:me=dia:subme=0:trellis=0:8x8dct=0:weightp=0:aq-mode=0";
    }

    private static async Task PumpProcessStderrAsync(
        string prefix,
        StreamReader reader,
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
                    PipelineLogger.Warn($"{prefix}: {line}");
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ClearAccumulatorShader(ReadWriteBuffer<float4> accumulator) : IComputeShader
{
    public void Execute()
    {
        accumulator[ThreadIds.X] = new float4(0, 0, 0, 0);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct AccumulateBgraShader(
    ReadWriteBuffer<uint> input,
    ReadWriteBuffer<float4> accumulator,
    float weight) : IComputeShader
{
    public void Execute()
    {
        uint pixel = input[ThreadIds.X];
        float b = pixel & 255;
        float g = (pixel >> 8) & 255;
        float r = (pixel >> 16) & 255;
        float a = (pixel >> 24) & 255;
        accumulator[ThreadIds.X] += new float4(b, g, r, a) * weight;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BlendBatchedBgraShader(
    ReadWriteBuffer<uint> inputFrames,
    ReadWriteBuffer<float> weights,
    ReadWriteBuffer<uint> output,
    int framePixelCount,
    int blendFrames) : IComputeShader
{
    public void Execute()
    {
        int pixelIndex = ThreadIds.X;
        float4 color = new(0, 0, 0, 0);

        for (int frame = 0; frame < blendFrames; frame++)
        {
            uint pixel = inputFrames[frame * framePixelCount + pixelIndex];
            float weight = weights[frame];
            color.X += (pixel & 255) * weight;
            color.Y += ((pixel >> 8) & 255) * weight;
            color.Z += ((pixel >> 16) & 255) * weight;
            color.W += ((pixel >> 24) & 255) * weight;
        }

        uint b = (uint)Hlsl.Clamp(color.X + 0.5f, 0, 255);
        uint g = (uint)Hlsl.Clamp(color.Y + 0.5f, 0, 255);
        uint r = (uint)Hlsl.Clamp(color.Z + 0.5f, 0, 255);
        uint a = (uint)Hlsl.Clamp(color.W + 0.5f, 0, 255);
        output[pixelIndex] = b | (g << 8) | (r << 16) | (a << 24);
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PackBgraShader(
    ReadWriteBuffer<float4> accumulator,
    ReadWriteBuffer<uint> output) : IComputeShader
{
    public void Execute()
    {
        float4 color = accumulator[ThreadIds.X];
        uint b = (uint)Hlsl.Clamp(color.X + 0.5f, 0, 255);
        uint g = (uint)Hlsl.Clamp(color.Y + 0.5f, 0, 255);
        uint r = (uint)Hlsl.Clamp(color.Z + 0.5f, 0, 255);
        uint a = (uint)Hlsl.Clamp(color.W + 0.5f, 0, 255);
        output[ThreadIds.X] = b | (g << 8) | (r << 16) | (a << 24);
    }
}
