using System.IO;
using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>
/// 对输入视频执行离线帧混合并输出 60fps 成片。
/// </summary>
public sealed class VideoSynthesisOrchestrator : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private string? _inputVideoPath;
    private string? _tempVideoPath;
    private string? _finalVideoPath;
    private string? _lastError;
    private int _synthesisEncodedFrames;
    private int _synthesisTotalFrames;
    private int _batchIndex;
    private int _batchTotal;

    public event Action<SynthesisStatus>? StatusChanged;

    public bool IsRunning { get; private set; }

    public void Start(
        UserSettings settings,
        RenderPreset preset,
        string inputVideoPath,
        int batchIndex = 0,
        int batchTotal = 0)
    {
        lock (_gate)
        {
            if (IsRunning)
                throw new InvalidOperationException("合成任务已在运行。");

            _batchIndex = batchIndex;
            _batchTotal = batchTotal;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _runTask = Task.Run(async () =>
            {
                try
                {
                    await ExecuteJobAsync(settings, preset, inputVideoPath, _cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Publish(PipelinePhase.Idle, "合成已取消。");
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
                finally
                {
                    if (IsRunning)
                        CleanupRunning();
                }
            });
        }
    }

    public async Task<VideoSynthesisResult> RunOneAsync(
        UserSettings settings,
        RenderPreset preset,
        string inputVideoPath,
        int batchIndex = 0,
        int batchTotal = 0,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (IsRunning)
                throw new InvalidOperationException("合成任务已在运行。");

            _batchIndex = batchIndex;
            _batchTotal = batchTotal;
            IsRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            return await ExecuteJobAsync(settings, preset, inputVideoPath, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Publish(PipelinePhase.Idle, "合成已取消。");
            return new VideoSynthesisResult(false, inputVideoPath, null, "已取消", null);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return new VideoSynthesisResult(false, inputVideoPath, null, ex.Message, null);
        }
        finally
        {
            CleanupRunning();
        }
    }

    public async Task CancelAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
        }

        cts?.Cancel();
        var task = _runTask;
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch
            {
                // fault already published
            }
        }

        CleanupRunning();
        Publish(PipelinePhase.Idle, "已取消合成。");
    }

    private async Task<VideoSynthesisResult> ExecuteJobAsync(
        UserSettings settings,
        RenderPreset preset,
        string inputVideoPath,
        CancellationToken cancellationToken)
    {
        inputVideoPath = Path.GetFullPath(inputVideoPath.Trim());
        if (!File.Exists(inputVideoPath))
            throw new FileNotFoundException("找不到输入视频文件。", inputVideoPath);

        if (!FfmpegLocator.TryResolve(settings, out _, out var ffmpegError))
            throw new InvalidOperationException(ffmpegError);

        _inputVideoPath = inputVideoPath;
        _lastError = null;
        _synthesisEncodedFrames = 0;
        _synthesisTotalFrames = 0;

        var outputDir = ObsOutputPathHelper.ResolveOutputDirectory(settings);
        Directory.CreateDirectory(outputDir);
        var session = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _tempVideoPath = Path.Combine(outputDir, $"synth_{session}.mp4");
        _finalVideoPath = Path.Combine(
            outputDir,
            ObsOutputPathHelper.BuildFinalFileName(inputVideoPath, preset, session));

        Publish(PipelinePhase.ValidatingInput, "正在校验输入视频…");
        PipelineLogger.Info(
            $"合成启动 input={inputVideoPath} preset={preset.Id} final={_finalVideoPath}");

        var ffmpeg = FfmpegLocator.Resolve(settings);
        var probe = await VideoProbeService.ProbeAsync(ffmpeg, inputVideoPath, cancellationToken);
        var settingsObsFps = ObsOutputPathHelper.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
        var effectiveObsFps = SynthesisTiming.ResolveEffectiveObsCaptureFramerate(probe, settingsObsFps);
        if (VideoProbeService.IsFrameRateMismatch(probe.FrameRate, settingsObsFps))
        {
            PipelineLogger.Warn(
                $"输入帧率 {probe.FrameRate:F2} 与设置 {settingsObsFps}fps 不一致，时长计算改用 {effectiveObsFps}fps。");
        }

        if (new FileInfo(inputVideoPath).Length < 1024)
            throw new InvalidOperationException("输入视频过小或无效。");

        var plan = SynthesisTiming.BuildPlan(probe, preset.BlendFrames, settingsObsFps);
        if (!plan.UsesFullSupersamplingBlend)
        {
            PipelineLogger.Warn(
                $"配置 N={plan.ConfiguredN}，源 {plan.ObsCaptureFramerate}fps 低于 N×60={plan.ConfiguredN * ProjectConstants.FinalOutputFramerate}；" +
                $"实际混合 {plan.BlendFrames} 帧/输出（成片与源等长）。若需 N 帧混合请提高 OBS 帧率。");
        }

        PipelineLogger.Info($"合成计划: {plan.Description}");

        _synthesisTotalFrames = Math.Max(1, probe.FrameCount / Math.Max(1, plan.BlendFrames));
        var backend = CompositionBackendCatalog.Normalize(settings.CompositionBackend);
        var backendName = backend switch
        {
            CompositionBackendCatalog.GpuId => "GPU",
            CompositionBackendCatalog.GpuResidentOpenClId => "GPU resident OpenCL",
            _ => "FFmpeg tmix",
        };
        PipelineLogger.Info($"Synthesis backend: settings={settings.CompositionBackend}, resolved={backend}, name={backendName}");
        Publish(PipelinePhase.Synthesizing, $"运动模糊合成（{backendName}）…");
        PipelineLogger.Info($"合成开始: {inputVideoPath} → {_tempVideoPath}");

        if (backend == CompositionBackendCatalog.GpuId)
        {
            _synthesisEncodedFrames = await GpuMotionBlurSynthesisService.RunAsync(
                ffmpeg,
                preset,
                plan.BlendFrames,
                settings.Exposure,
                inputVideoPath,
                _tempVideoPath,
                settings.VideoEncoder,
                settings.Crf,
                probe.FrameCount,
                (n, total) =>
                {
                    _synthesisEncodedFrames = n;
                    _synthesisTotalFrames = total;
                    PublishPhase(PipelinePhase.Synthesizing);
                },
                cancellationToken);
        }
        else if (backend == CompositionBackendCatalog.GpuResidentOpenClId)
        {
            await FfmpegOpenClSynthesisService.RunAsync(
                ffmpeg,
                preset,
                plan.BlendFrames,
                settings.Exposure,
                inputVideoPath,
                _tempVideoPath,
                settings.VideoEncoder,
                settings.Crf,
                n =>
                {
                    _synthesisEncodedFrames = n;
                    PublishPhase(PipelinePhase.Synthesizing);
                },
                cancellationToken);
        }
        else
        {
            await FfmpegSynthesisService.RunAsync(
                ffmpeg,
                preset,
                plan.BlendFrames,
                plan.PlaybackSpeedScale,
                settings.Exposure,
                inputVideoPath,
                _tempVideoPath,
                settings.VideoEncoder,
                settings.Crf,
                n =>
                {
                    _synthesisEncodedFrames = n;
                    PublishPhase(PipelinePhase.Synthesizing);
                },
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_tempVideoPath) || new FileInfo(_tempVideoPath).Length < 1024)
            throw new InvalidOperationException("未生成有效合成视频。");

        Publish(PipelinePhase.MuxingAudio, "正在封装音轨…");
        await FfmpegEncodingService.MuxFromSourceVideoAsync(
            ffmpeg,
            _tempVideoPath,
            inputVideoPath,
            _finalVideoPath,
            audioTempo: null,
            cancellationToken);

        TryDeleteFile(_tempVideoPath);

        var validation = await VideoOutputValidator.ValidateAsync(
            ffmpeg,
            _finalVideoPath,
            ProjectConstants.FinalOutputFramerate,
            cancellationToken);

        var doneMessage = $"完成: {_finalVideoPath}";
        if (!string.IsNullOrWhiteSpace(validation.Message))
        {
            if (validation.Success)
                doneMessage += $"\n{validation.Message}";
            else
            {
                PipelineLogger.Warn(validation.Message);
                doneMessage += $"\n校验警告：{validation.Message}";
            }
        }

        Publish(PipelinePhase.Done, doneMessage);
        PipelineLogger.Info($"成片: {_finalVideoPath}");
        return new VideoSynthesisResult(
            true,
            inputVideoPath,
            _finalVideoPath,
            null,
            validation.Message);
    }

    private void Fail(string message)
    {
        _lastError = message;
        PipelineLogger.Error(message);
        Publish(PipelinePhase.Faulted, message);
    }

    private void PublishPhase(PipelinePhase phase) => Publish(phase, null);

    private void Publish(PipelinePhase phase, string? message)
    {
        StatusChanged?.Invoke(new SynthesisStatus
        {
            Phase = phase,
            EncodedFrameCount = _synthesisEncodedFrames,
            SynthesisTotalFrames = _synthesisTotalFrames,
            InputVideoPath = _inputVideoPath,
            Message = message,
            LastError = _lastError,
            TempVideoPath = _tempVideoPath,
            FinalVideoPath = _finalVideoPath,
            BatchIndex = _batchIndex,
            BatchTotal = _batchTotal,
        });
    }

    private void CleanupRunning()
    {
        lock (_gate)
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    public async ValueTask DisposeAsync() => await CancelAsync();
}
