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
        PerformanceSnapshot? Performance = null,
        double MachineCpuPercent = -1,
        int ExtractingChunks = 0,
        int SynthesizingChunks = 0,
        int ParallelismChunks = 0);

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

        var outputDirectory = settings.VideoOutputDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new DirectoryNotFoundException("请先配置成片输出目录。");
        var previewDirectory = Path.Combine(outputDirectory, "quality-preview", "stage2");
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
            // 预览覆盖阶段 1 慢放底片的全部时长（即选中回放的完整阶段），
            // 不再固定截取 6 秒。向下取整保证每段都是完整的 N 秒窗口，
            // 不会因末尾碎片段导致合成失败。
            var (outputSeconds, sourceDurationSeconds) = ResolveOutputSeconds(sourcePath, blend);
            // 预览合成与任务录制（磁盘背压）无关：按 CPU 逻辑核心数满载并行。
            // 每条分块串行经历 ffmpeg 切片（多线程吃 CPU）→ 原生解码 + GPU 混合 +
            // 系统软件编码（CPU），单块吃不满多核，靠块间并行占满 CPU。
            var parallelism = Math.Clamp(Environment.ProcessorCount, 1, outputSeconds);
            var chunkDirectory = Path.Combine(previewDirectory, $"chunks-{id}");
            Directory.CreateDirectory(chunkDirectory);
            var completed = 0;
            var outputs = Enumerable.Range(0, outputSeconds)
                .Select(index => Path.Combine(chunkDirectory, $"out-{index:D2}.mp4"))
                .ToArray();
            using var gate = new SemaphoreSlim(parallelism);
            using var cpuLog = new StageTwoCpuLog(
                previewDirectory, id, sourcePath, sourceDurationSeconds,
                blend, parallelism, outputSeconds, progress, token);
            var tasks = Enumerable.Range(0, outputSeconds).Select(async index =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var inputChunk = Path.Combine(chunkDirectory, $"in-{index:D2}.mp4");
                    // Each chunk contains exactly one second of output: N whole
                    // 60 fps input windows. Chunks tile the stage-1 master from
                    // its start, so the preview covers the complete replay stage.
                    var sourceStartSeconds = index * blend;
                    cpuLog.BeginExtract();
                    var extractClock = Stopwatch.StartNew();
                    try
                    {
                        await ExtractChunkAsync(ffmpeg, sourcePath, inputChunk, sourceStartSeconds, blend, token);
                    }
                    finally
                    {
                        cpuLog.EndExtract();
                    }
                    cpuLog.BeginSynthesize();
                    var synthClock = Stopwatch.StartNew();
                    var chunkProgress = new Progress<ObsSynthesisService.Progress>(_ => { });
                    await new ObsSynthesisService().RunAsync(
                        inputChunk,
                        outputs[index],
                        previewSettings,
                        chunkProgress,
                        token);
                    cpuLog.ChunkFinished(index, extractClock.ElapsedMilliseconds, synthClock.ElapsedMilliseconds);
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new PreviewProgress(
                        $"阶段 2 并行合成（{parallelism} 路）…", done, outputSeconds));
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks);
                cpuLog.Complete("ok");
            }
            catch (Exception ex)
            {
                cpuLog.Complete($"failed error={ex.Message}");
                throw;
            }
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

    /// <summary>
    /// 阶段 1 底片时长 ÷ N = 预览覆盖的输出秒数；探测失败时退回 6 秒样例。
    /// </summary>
    private static (int OutputSeconds, double SourceDurationSeconds) ResolveOutputSeconds(
        string sourcePath, int blend)
    {
        try
        {
            var probe = new MediaProbe().Probe(sourcePath);
            if (probe.IsValid && probe.DurationSeconds > 0)
            {
                var seconds = Math.Clamp((int)Math.Floor(probe.DurationSeconds / blend), 1, 600);
                return (seconds, probe.DurationSeconds);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 底片读取失败时退回固定样例时长，让阶段 2 继续可用。
        }
        return (6, 0);
    }

    /// <summary>
    /// 阶段2合成的 CPU 利用率日志：1 Hz 整机采样行 + 每分块耗时行，写在
    /// stage2/logs/ 下，供后续 AI 分析并行度与瓶颈。所有行即时落盘，
    /// 即使合成中途失败也保留已采集的数据。
    /// </summary>
    private sealed class StageTwoCpuLog : IDisposable
    {
        private readonly object _writeGate = new();
        private readonly StreamWriter _writer;
        private readonly MachineCpuSampler _sampler = new();
        private readonly IProgress<PreviewProgress>? _progress;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _loop;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _parallelism;
        private int _total;
        private int _extracting;
        private int _synthesizing;
        private int _done;
        private double _cpuPercent = -1;

        public StageTwoCpuLog(
            string stageTwoDirectory,
            string id,
            string sourcePath,
            double sourceDurationSeconds,
            int blend,
            int parallelism,
            int total,
            IProgress<PreviewProgress>? progress,
            CancellationToken token)
        {
            _parallelism = parallelism;
            _total = total;
            _progress = progress;
            var logDirectory = Path.Combine(stageTwoDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"stage2-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..6]}.csv");
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
            WriteLine($"# Momentum 阶段2合成 CPU 日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine("# s,elapsed_seconds,cpu_percent,extracting,synthesizing,done,total");
            WriteLine("# c,chunk_index,extract_ms,synth_ms");
            WriteLine(FormattableString.Invariant(
                $"# source={Path.GetFileName(sourcePath)} sourceDurationSeconds={sourceDurationSeconds:F1} blend={blend} parallelism={parallelism} total={total} logicalCores={Environment.ProcessorCount}"));
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _loop = Task.Run(SampleLoopAsync);
        }

        public void BeginExtract() => Interlocked.Increment(ref _extracting);

        public void EndExtract() => Interlocked.Decrement(ref _extracting);

        public void BeginSynthesize() => Interlocked.Increment(ref _synthesizing);

        public void ChunkFinished(int index, long extractMilliseconds, long synthMilliseconds)
        {
            Interlocked.Decrement(ref _synthesizing);
            var done = Interlocked.Increment(ref _done);
            WriteLine(FormattableString.Invariant($"c,{index},{extractMilliseconds},{synthMilliseconds}"));
            ReportProgress(done);
        }

        public void Complete(string result) =>
            WriteLine(FormattableString.Invariant($"# result={result} elapsed_seconds={_clock.Elapsed.TotalSeconds:F1}"));

        public void Dispose()
        {
            _cancellation.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _cancellation.Dispose();
            _writer.Dispose();
        }

        private async Task SampleLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                var cpu = _sampler.NextSamplePercent();
                if (cpu >= 0)
                    Volatile.Write(ref _cpuPercent, cpu);
                WriteLine(FormattableString.Invariant(
                    $"s,{_clock.Elapsed.TotalSeconds:F1},{Volatile.Read(ref _cpuPercent):F0},{Volatile.Read(ref _extracting)},{Volatile.Read(ref _synthesizing)},{Volatile.Read(ref _done)},{Volatile.Read(ref _total)}"));
                ReportProgress(Volatile.Read(ref _done));
            }
        }

        private void ReportProgress(int done) =>
            _progress?.Report(new PreviewProgress(
                string.Empty,
                done,
                _total,
                MachineCpuPercent: Volatile.Read(ref _cpuPercent),
                ExtractingChunks: Volatile.Read(ref _extracting),
                SynthesizingChunks: Volatile.Read(ref _synthesizing),
                ParallelismChunks: _parallelism));

        private void WriteLine(string line)
        {
            lock (_writeGate)
                _writer.WriteLine(line);
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
