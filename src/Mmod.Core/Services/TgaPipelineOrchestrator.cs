using System.IO;
using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

/// <summary>
/// Capture pipeline: session-scoped TGA watcher → Native blend session →
/// encoder. UI Status is only a projection; correctness lives in the strong
/// lifecycle (State / Completion / Fault) and FinalizeAsync, which never
/// swallows Native faults and never reports success without proof.
/// </summary>
public sealed class TgaPipelineOrchestrator : ICapturePipeline, IAsyncDisposable
{
    private readonly VisualPlaybackEvidenceProbe _evidenceProbe;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TgaDirectoryWatcher? _watcher;
    private NativeBlendSession? _session;
    private int _nextFrame;
    private long _fed;
    private long _submittedInputFrames;
    private int _firstFrameWidth;
    private int _firstFrameHeight;
    private Exception? _fault;
    private PipelineState _state = PipelineState.Created;
    private string? _sessionDiagnostics;

    public TgaPipelineOrchestrator(RecordingTimeoutPolicy? timeouts = null)
    {
        Timeouts = timeouts ?? RecordingTimeoutPolicy.Default;
        _evidenceProbe = new VisualPlaybackEvidenceProbe(Timeouts);
    }

    public RecordingTimeoutPolicy Timeouts { get; }

    public event Action? Changed;

    public bool IsRunning => _loop is { IsCompleted: false };
    public long FedCount => _fed;
    public int PendingCount => _watcher?.PendingCount ?? 0;
    public int CandidateCount => _watcher?.CandidateCount ?? 0;
    public PipelineState State => _state;
    public Exception? Fault => _fault;
    public bool IsFaulted => _fault is not null;
    public Task Completion => _completion.Task;
    public string? OutputPath { get; private set; }
    public string? WatchDirectory { get; private set; }
    public string? SessionDiagnostics => _sessionDiagnostics;
    public string Status { get; private set; } = "空闲";
    public ITgaCaptureWatcher Watcher => _watcher ?? throw new InvalidOperationException("Watcher 尚未启动。");
    public string? CaptureSessionId { get; private set; }
    public string? SequencePrefix { get; private set; }

    /// <summary>True once any frame differed from the baseline (evidence probe).</summary>
    public bool HasVisualChange { get; private set; }
    /// <summary>FedCount of the first frame that established playback evidence.</summary>
    public int? ActivityAnchorFrame { get; private set; }
    /// <summary>FedCount of the most recent frame with significant scene activity.</summary>
    public int? LastVisualChangeFrame { get; private set; }

    public Task StartAsync(UserSettings settings) => StartAsync(settings, null, null, true);

    public Task StartAsync(UserSettings settings, string? outputPath, bool acceptPreSessionFiles = false)
        => StartAsync(settings, outputPath, null, acceptPreSessionFiles);

    public async Task StartAsync(
        UserSettings settings,
        string? outputPath,
        CaptureSessionInfo? session,
        bool acceptPreSessionFiles = false)
    {
        if (IsRunning)
            throw new InvalidOperationException("管线已在运行");

        WatchDirectoryHelper.EnsureDerivedPaths(settings, settings.GameRootPath);
        var watchDir = WatchDirectoryHelper.ResolveEffectiveWatchDirectory(settings, settings.GameRootPath);
        if (string.IsNullOrWhiteSpace(watchDir))
            throw new InvalidOperationException("请先设置有效的 TGA 监视目录");

        Directory.CreateDirectory(watchDir);
        if (!Directory.Exists(watchDir))
            throw new InvalidOperationException($"TGA 监视目录不存在：{watchDir}");

        var outputDir = string.IsNullOrWhiteSpace(settings.VideoOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "mmod_record_next")
            : settings.VideoOutputDirectory;
        Directory.CreateDirectory(outputDir);

        OutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(outputDir, $"tga_{DateTime.Now:yyyyMMdd_HHmmss}.mp4")
            : Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        WatchDirectory = watchDir;

        // Session-scoped prefix: watcher only consumes files of this session.
        var effectiveSession = session ?? CaptureSessionInfo.Create("manual", 0, 0);
        CaptureSessionId = effectiveSession.CaptureSessionId;
        SequencePrefix = effectiveSession.SequencePrefix;
        // startmovie name = the exact prefix (trailing underscore included) so
        // produced files are {prefix}{index}.tga, matching the watcher regex.
        settings.MovieSequenceName = effectiveSession.SequencePrefix;

        _fed = 0;
        _submittedInputFrames = 0;
        HasVisualChange = false;
        ActivityAnchorFrame = null;
        LastVisualChangeFrame = null;
        _fault = null;
        _nextFrame = 0;
        _state = PipelineState.Watching;
        _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();

        _watcher = new TgaDirectoryWatcher(watchDir, effectiveSession.SequencePrefix);
        _watcher.PendingChanged += () => Changed?.Invoke();
        _watcher.Start(acceptPreSessionFiles: acceptPreSessionFiles);

        Status = $"监视中：{watchDir}（prefix={effectiveSession.SequencePrefix}）";
        Changed?.Invoke();

        var blend = Math.Max(1, settings.SupersamplingMultiplier);
        var token = _cts.Token;
        _loop = Task.Run(() => RunLoop(settings, blend, token), token);
        await Task.CompletedTask;
    }

    public async Task WaitUntilFedAsync(int minimumFed, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (FedCount < minimumFed)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待 TGA 写帧超时：需要至少 {minimumFed} 帧，当前 {FedCount}。");
            if (!string.IsNullOrWhiteSpace(Status) && Status.StartsWith("错误：", StringComparison.Ordinal))
                throw new InvalidOperationException(Status);
            await Task.WhenAny(Task.Delay(100, token), Completion).ConfigureAwait(false);
        }
    }

    public async Task WaitUntilActivityAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ActivityAnchorFrame is null)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待回放画面运动超时（{timeout.TotalSeconds:0}s 内无 PlaybackEvidence）。");
            if (!string.IsNullOrWhiteSpace(Status) && Status.StartsWith("错误：", StringComparison.Ordinal))
                throw new InvalidOperationException(Status);
            await Task.WhenAny(Task.Delay(100, token), Completion).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears ActivityAnchor / evidence baseline so CaptureReady still frames
    /// cannot count as PlaybackActivity. Call after CaptureReady, immediately
    /// before mom_tv_replay_watch.
    /// </summary>
    public void ResetActivityTracking()
    {
        HasVisualChange = false;
        ActivityAnchorFrame = null;
        LastVisualChangeFrame = null;
        _evidenceProbe.Reset(); // force re-baseline on next frame
        Changed?.Invoke();
    }

    public void ThrowIfFaulted()
    {
        if (_fault is not null)
            throw new PipelineFaultException(_fault.Message, _fault);
    }

    /// <summary>
    /// Deterministic shutdown. Order: request loop stop → await completion
    /// (fault propagates) → final full scan → physical quiescence → freeze →
    /// drain pending in order → assert empty → native Finish. Any step failure
    /// throws; success is only reported after every boundary is proven.
    /// </summary>
    public async Task<PipelineFinalizeResult> FinalizeAsync(RecordingTimeoutPolicy timeouts, CancellationToken token)
    {
        if (_cts is null)
            throw new InvalidOperationException("管线尚未启动。");

        _state = PipelineState.FreezeRequested;
        Status = "停止中，排空并收尾…";
        Changed?.Invoke();

        _cts.Cancel();
        try
        {
            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        ThrowIfFaulted(); // P0-05: pipeline background fault must fail the attempt

        var watcher = _watcher ?? throw new InvalidOperationException("Watcher 不存在。");
        if (_session is null)
            throw new InvalidOperationException("无已喂入帧，无法 finalize。");

        _state = PipelineState.Draining;

        // Final scan + positive physical quiescence proof.
        watcher.ForceFullScan();
        await watcher.WaitForQuiescenceAsync(
            timeouts.TgaQuiescenceQuietWindow,
            timeouts.TgaQuiescenceHardTimeout,
            token);

        watcher.Freeze();

        // Deterministic drain: lowest index first.
        var drainDeadline = DateTime.UtcNow + timeouts.DrainTimeout;
        while (watcher.TryGetMinPendingFrameIndex(out var min))
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= drainDeadline)
                throw new TimeoutException($"TGA drain 超时（{timeouts.DrainTimeout.TotalSeconds:0}s），剩余 {watcher.PendingCount} 帧。");
            if (!watcher.TryTake(min, out var path))
                continue;
            SubmitFile(path);
        }

        // Assert nothing is left unmanaged (P0-07 fail-safe).
        if (watcher.PendingCount != 0 || watcher.CandidateCount != 0 || watcher.HasUnstableFiles)
            throw new InvalidDataException(
                $"排空后仍有未管理 TGA：pending={watcher.PendingCount} candidate={watcher.CandidateCount} unstable={watcher.HasUnstableFiles}");

        _state = PipelineState.Finalizing;
        var progress = _session.GetProgress();
        _session.Finish(); // P0-06: Finish fault must propagate

        _state = PipelineState.Finalized;
        Status = File.Exists(OutputPath) ? $"完成：{OutputPath}" : "已停止（无输出文件）";
        Changed?.Invoke();

        return new PipelineFinalizeResult(
            SubmittedFrames: _submittedInputFrames,
            ProducedFrames: progress.Done,
            FirstFrameIndex: 0,
            LastFrameIndex: _nextFrame - 1,
            OutputPath: OutputPath ?? string.Empty,
            FinishSucceeded: true,
            FirstFrameWidth: _firstFrameWidth,
            FirstFrameHeight: _firstFrameHeight);
    }

    public async Task StopAsync()
    {
        // Legacy convenience: finalize with defaults; used by manual TGA mode.
        await FinalizeAsync(Timeouts, CancellationToken.None);
    }

    private void RunLoop(UserSettings settings, int blend, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_watcher is null)
                    break;

                if (!_watcher.TryGetMinPendingFrameIndex(out var frameIndex))
                {
                    Thread.Sleep(30);
                    continue;
                }

                if (!_watcher.TryTake(frameIndex, out var path))
                {
                    Thread.Sleep(20);
                    continue;
                }

                _nextFrame = frameIndex + 1;

                if (!File.Exists(path) || !TgaFrameReader.TryReadBgra(path, out var width, out var height, out var bgra))
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                    Status = $"合成中：已喂入 {_fed} 帧，待处理 {_watcher.PendingCount}";
                    Changed?.Invoke();
                    continue;
                }

                _session ??= CreateSession(settings, width, height, blend);
                if (_firstFrameWidth == 0)
                {
                    _firstFrameWidth = width;
                    _firstFrameHeight = height;
                }
                _session.SubmitBgra(bgra, width * 4);
                _submittedInputFrames++;
                _fed++;
                TrackPlaybackEvidence(bgra, width, height);

                Status = $"合成中：已喂入 {_fed} 帧，待处理 {_watcher.PendingCount}";
                Changed?.Invoke();

                try { File.Delete(path); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _fault = ex;
            _state = PipelineState.Faulted;
            Status = $"错误：{ex.Message}";
            Changed?.Invoke();
            _completion.TrySetException(ex);
            return;
        }

        if (_fault is null)
            _completion.TrySetResult();
    }

    private NativeBlendSession CreateSession(UserSettings settings, int width, int height, int blend)
    {
        var session = NativeBlendSession.Create(
            width,
            height,
            blend,
            (float)settings.Exposure,
            ProjectConstants.FinalOutputFramerate,
            OutputPath!,
            NativeSessionFactory.BuildOptions(settings));
        _sessionDiagnostics = NativeSessionDiagnostics.Describe(
            settings, width, height, blend, ProjectConstants.FinalOutputFramerate);
        return session;
    }

    private void SubmitFile(string path)
    {
        // Drain path: the session must already exist (frames were fed during
        // capture). A corrupt pending frame here is a hard error, not a skip.
        if (_session is null)
            throw new InvalidOperationException("Finalize 排空阶段缺少 Native Session。");

        if (!TgaFrameReader.TryReadBgra(path, out var width, out var height, out var bgra))
        {
            throw new InvalidDataException($"drain 阶段无法读取 TGA：{path}");
        }

        _session.SubmitBgra(bgra, width * 4);
        _submittedInputFrames++;
        _fed++;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private void TrackPlaybackEvidence(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var sample = _evidenceProbe.Sample(bgra, width, height);
        if (_evidenceProbe.IsPlaybackStarted)
        {
            ActivityAnchorFrame ??= (int)_fed;
            LastVisualChangeFrame = (int)_fed;
            HasVisualChange = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            // Best-effort stop for disposal; real cleanup is FinalizeAsync's job.
            try
            {
                _cts?.Cancel();
                if (_loop is not null)
                    await _loop.ConfigureAwait(false);
            }
            catch
            {
                // secondary; do not throw from Dispose
            }
        }

        try
        {
            _session?.Finish();
        }
        catch
        {
            // secondary cleanup error
        }
        finally
        {
            _session?.Dispose();
            _session = null;
            _watcher?.Stop();
            _watcher?.Dispose();
            _watcher = null;
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            _state = PipelineState.Disposed;
            Changed?.Invoke();
        }
    }
}

/// <summary>Strongly-typed pipeline fault (plan P0-05).</summary>
public sealed class PipelineFaultException : Exception
{
    public PipelineFaultException(string message, Exception inner) : base(message, inner) { }
}
