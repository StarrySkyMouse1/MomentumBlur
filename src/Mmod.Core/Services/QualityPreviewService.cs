using System.Diagnostics;
using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

/// <summary>Builds a short 60 fps preview for the currently selected quality modules.</summary>
public sealed class QualityPreviewService
{
    public sealed record PreviewProgress(
        string Stage,
        int Done = 0,
        int Total = 0,
        DiskHealthSnapshot? Disk = null,
        PerformanceSnapshot? Performance = null);

    public async Task<string> CreateAsync(
        string sourcePath,
        UserSettings settings,
        IProgress<PreviewProgress>? progress,
        CancellationToken token)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("预览源视频不存在。", sourcePath);

        var ffmpeg = FindFfmpeg()
            ?? throw new FileNotFoundException("未找到 ffmpeg。请将 ffmpeg.exe 加入 PATH，或安装到 HLAE FFMPEG 目录。");
        var previewDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProjectConstants.AppDataFolderName,
            "quality-preview");
        Directory.CreateDirectory(previewDirectory);

        var id = Guid.NewGuid().ToString("N");
        var normalized = Path.Combine(previewDirectory, $"source-{id}.mp4");
        var temporaryOutput = Path.Combine(previewDirectory, $"preview-{id}.mp4");
        var finalOutput = Path.Combine(
            previewDirectory,
            $"quality-preview-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..6]}.mp4");

        try
        {
            progress?.Report(new PreviewProgress("正在截取 6 秒预览源…"));
            await RunFfmpegAsync(ffmpeg, sourcePath, normalized, token);

            // A 60 fps normalized source with blend=1 isolates the quality
            // modules. Applying the TGA supersampling value to an ordinary
            // video would change its duration and produce a misleading preview.
            var previewSettings = CloneForQualityPreview(settings);
            var synthesisProgress = new Progress<ObsSynthesisService.Progress>(p =>
                progress?.Report(new PreviewProgress("正在应用当前画质设置…", p.Done, p.Total)));
            await new ObsSynthesisService().RunAsync(
                normalized,
                temporaryOutput,
                previewSettings,
                synthesisProgress,
                token);

            token.ThrowIfCancellationRequested();
            File.Move(temporaryOutput, finalOutput, overwrite: true);
            progress?.Report(new PreviewProgress("预览已生成"));
            return finalOutput;
        }
        finally
        {
            TryDelete(normalized);
            TryDelete(temporaryOutput);
        }
    }

    public async Task<string> CreateFromSlowMotionSourceAsync(
        string sourcePath,
        UserSettings settings,
        IProgress<PreviewProgress>? progress,
        CancellationToken token)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("慢放预览源不存在。", sourcePath);

        var previewDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProjectConstants.AppDataFolderName,
            "quality-preview");
        Directory.CreateDirectory(previewDirectory);

        var id = Guid.NewGuid().ToString("N");
        var finalOutput = Path.Combine(
            previewDirectory,
            $"quality-preview-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..6]}.mp4");
        try
        {
            var previewSettings = CloneForReplayPreview(settings);
            var ffmpeg = FindFfmpeg()
                ?? throw new FileNotFoundException("未找到 ffmpeg，无法切分预览。请将 ffmpeg.exe 加入 PATH。");
            var blend = Math.Clamp(previewSettings.SupersamplingMultiplier, 1, 60);
            const int outputSeconds = 6;
            var parallelism = Math.Clamp(settings.MaxParallelJobs, 1, outputSeconds);
            var chunkDirectory = Path.Combine(previewDirectory, $"chunks-{id}");
            Directory.CreateDirectory(chunkDirectory);
            var completed = 0;
            var outputs = Enumerable.Range(0, outputSeconds)
                .Select(index => Path.Combine(chunkDirectory, $"out-{index:D2}.mp4"))
                .ToArray();
            using var gate = new SemaphoreSlim(parallelism);
            var tasks = Enumerable.Range(0, outputSeconds).Select(async index =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var inputChunk = Path.Combine(chunkDirectory, $"in-{index:D2}.mp4");
                    // Each chunk contains exactly one second of output: N whole
                    // 60 fps input windows. No blur window crosses a chunk edge.
                    var sourceStartSeconds = (2 + index) * blend;
                    await ExtractChunkAsync(ffmpeg, sourcePath, inputChunk, sourceStartSeconds, blend, token);
                    var chunkProgress = new Progress<ObsSynthesisService.Progress>(_ => { });
                    await new ObsSynthesisService().RunAsync(
                        inputChunk,
                        outputs[index],
                        previewSettings,
                        chunkProgress,
                        token);
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new PreviewProgress(
                        $"阶段 2 并行合成（{parallelism} 路）…", done, outputSeconds));
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();
            progress?.Report(new PreviewProgress("正在无损拼接阶段 2 片段…", outputSeconds, outputSeconds));
            NativeMp4Concatenator.Concatenate(outputs, finalOutput);
            TryDeleteDirectory(chunkDirectory);
            return finalOutput;
        }
        finally
        {
            foreach (var directory in Directory.EnumerateDirectories(previewDirectory, $"chunks-{id}"))
                TryDeleteDirectory(directory);
        }
    }

    private static UserSettings CloneForQualityPreview(UserSettings source) => new()
    {
        SupersamplingMultiplier = 1,
        ObsCaptureFramerate = ProjectConstants.FinalOutputFramerate,
        Exposure = source.Exposure,
        MotionBlurWeightMode = source.MotionBlurWeightMode,
        ShutterAngle = source.ShutterAngle,
        IntermediateTargetBitrate = source.IntermediateTargetBitrate,
        VideoProcessing = VideoProcessorCatalog.Normalize(source.VideoProcessing),
    };

    private static UserSettings CloneForReplayPreview(UserSettings source) => new()
    {
        SupersamplingMultiplier = Math.Clamp(source.SupersamplingMultiplier, 1, 64),
        ObsCaptureFramerate = ProjectConstants.FinalOutputFramerate,
        Exposure = source.Exposure,
        MotionBlurWeightMode = source.MotionBlurWeightMode,
        ShutterAngle = source.ShutterAngle,
        IntermediateTargetBitrate = source.IntermediateTargetBitrate,
        VideoProcessing = VideoProcessorCatalog.Normalize(source.VideoProcessing),
    };

    private static async Task ExtractChunkAsync(
        string ffmpeg,
        string sourcePath,
        string outputPath,
        int startSeconds,
        int durationSeconds,
        CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-ss", startSeconds.ToString(), "-t", durationSeconds.ToString(),
            "-i", sourcePath, "-an", "-c:v", "libx264", "-preset", "ultrafast",
            "-crf", "8", "-pix_fmt", "yuv420p", outputPath,
        })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"切分预览底片失败（ffmpeg {process.ExitCode}）：{error.Trim()}");
    }

    private static async Task RunFfmpegAsync(
        string ffmpeg,
        string sourcePath,
        string outputPath,
        CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("6");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add("fps=60");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("ultrafast");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("8");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"截取预览源失败（ffmpeg {process.ExitCode}）：{error.Trim()}");
    }

    private static string? FindFfmpeg()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            @"D:\Software\Videos\HLAE FFMPEG\ffmpeg\bin\ffmpeg.exe",
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "ffmpeg.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
