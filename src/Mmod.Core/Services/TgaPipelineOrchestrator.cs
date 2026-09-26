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
    private const int DecodeWorkerLimit = 3;
    private const int DecodeLookAheadLimit = 8;
    private readonly VisualPlaybackEvidenceProbe _evidenceProbe;
    private readonly CapturePerformanceTracker _performanceTracker = new();
    private readonly CaptureMotionDiagnostics _motionDiagnostics = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TaskCompletionSource _loopReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TgaDirectoryWatcher? _watcher;
    private NativeBlendSession? _session;
    private int _nextFrame;
    private long _fed;
    private long _submittedInputFrames;
    private long _outputFrames;
    private int _lastVisualChangeFrame = -1;
    private ProcessingBackend _processingBackend = ProcessingBackend.Unknown;
    private EncoderBackend _encoderBackend = EncoderBackend.Unknown;
    private int _firstFrameWidth;
    private int _firstFrameHeight;
    private Exception? _fault;
    private PipelineState _state = PipelineState.Created;
    private string? _sessionDiagnostics;
    private bool _finishSucceeded;
    private readonly int? _blendOverride;

    public TgaPipelineOrchestrator(RecordingTimeoutPolicy? timeouts = null, int? blendOverride = null)
    {
        Timeouts = timeouts ?? RecordingTimeoutPolicy.Default;
        _blendOverride = blendOverride is > 0 ? blendOverride : null;
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
    public string MotionDiagnosticsSummary => _motionDiagnostics.BuildSummary();
    public string Status { get; private set; } = "空闲";

    /// <summary>
    /// Immutable runtime capture-performance snapshot. Produced comes from the
    /// watcher's stable-frame counter, Consumed from successful native
    /// submits, Output from the native frames_output counter.
    /// </summary>
    public PerformanceSnapshot Performance
    {
        get
        {
            var backlog = _watcher?.GetBacklogSnapshot()
                ?? new WatcherBacklogSnapshot(0, 0, 0, 0, false);
            return _performanceTracker.BuildSnapshot(
                _processingBackend, _encoderBackend,
                backlog.PendingFrames, backlog.PendingBytes);
        }
    }

    public ITgaCaptureWatcher Watcher => _watcher ?? throw new InvalidOperationException("Watcher 尚未启动。");
    public string? CaptureSessionId { get; private set; }
    public string? SequencePrefix { get; private set; }

    /// <summary>True once any frame differed from the baseline (evidence probe).</summary>
    public bool HasVisualChange { get; private set; }
    /// <summary>FedCount of the first frame that established playback evidence.</summary>
    public int? ActivityAnchorFrame { get; private set; }
    /// <summary>FedCount of the most recent frame with significant scene activity.</summary>
    public int? LastVisualChangeFrame
    {
        get
        {
            var frame = Volatile.Read(ref _lastVisualChangeFrame);
            return frame < 0 ? null : frame;
        }
    }

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

        // 严格闸门：链接目录必须已创建且指向内存盘；未链接时禁止在磁盘路径上监视/录制
        MomentumDirectoryLinkService.EnsureCaptureTargetOnRam(
            settings.GameRootPath,
            settings.RamDiskWatchDirectory ?? string.Empty,
            watchDir);

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
        _outputFrames = 0;
        _processingBackend = ProcessingBackend.Unknown;
        _encoderBackend = EncoderBackend.Unknown;
        _finishSucceeded = false;
        _performanceTracker.Reset();
        HasVisualChange = false;
        ActivityAnchorFrame = null;
        Volatile.Write(ref _lastVisualChangeFrame, -1);
        _fault = null;
        _nextFrame = 0;
        _state = PipelineState.Watching;
        _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();

        _watcher = new TgaDirectoryWatcher(watchDir, effectiveSession.SequencePrefix);
        _watcher.PendingChanged += () => Changed?.Invoke();
        _watcher.Start(acceptPreSessionFiles: acceptPreSessionFiles);

        Status = $"监视中：{watchDir}（prefix={effectiveSession.SequencePrefix}）";
        Changed?.Invoke();

        var blend = _blendOverride ?? Math.Max(1, settings.SupersamplingMultiplier);
        var token = _cts.Token;
        _loop = Task.Run(() => RunLoopAsync(settings, blend, token), token);
        // Do not let startmovie begin until the consumer thread has actually
        // entered its loop. At high supersampling the producer can fill a RAM
        // disk quickly, so merely scheduling Task.Run is not a readiness proof.
        await _loopReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        Volatile.Write(ref _lastVisualChangeFrame, -1);
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
            if (min != _nextFrame)
                throw new InvalidDataException(
                    $"TGA 帧序列不连续：期望 {_nextFrame}，实际最小待处理帧为 {min}。为避免乱序或画面抖动，已停止合成。");
            SubmitFile(path);
            _nextFrame = min + 1;
        }

        // Assert nothing is left unmanaged (P0-07 fail-safe).
        if (watcher.PendingCount != 0 || watcher.CandidateCount != 0 || watcher.HasUnstableFiles)
            throw new InvalidDataException(
                $"排空后仍有未管理 TGA：pending={watcher.PendingCount} candidate={watcher.CandidateCount} unstable={watcher.HasUnstableFiles}");

        _state = PipelineState.Finalizing;
        var progress = _session.GetProgress();
        _session.Finish(); // P0-06: Finish fault must propagate
        _finishSucceeded = true;

        _outputFrames = progress.Done; // real native frames_output, never submitted/N
        SamplePerformance();

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

    private async Task RunLoopAsync(UserSettings settings, int blend, CancellationToken token)
    {
        var inFlight = new SortedDictionary<int, PreparedFrameWork>();
        using var decodeSlots = new SemaphoreSlim(
            Math.Max(1, Math.Min(DecodeWorkerLimit, Environment.ProcessorCount - 1)));
        try
        {
            _loopReady.TrySetResult();
            var nextToAcquire = _nextFrame;
            while (!token.IsCancellationRequested || inFlight.Count > 0)
            {
                if (_watcher is null)
                    break;

                // Reading and TGA expansion are independent per frame, so keep
                // a small bounded look-ahead window busy. The watcher retains
                // ownership and backlog accounting until exact-order commit.
                while (!token.IsCancellationRequested
                    && inFlight.Count < DecodeLookAheadLimit
                    && _watcher.TryGetPendingPath(nextToAcquire, out var path))
                {
                    var work = new PreparedFrameWork(
                        nextToAcquire,
                        path,
                        DecodeFrameAsync(nextToAcquire, path, decodeSlots));
                    inFlight.Add(nextToAcquire, work);
                    nextToAcquire++;
                }

                if (inFlight.Count == 0)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    continue;
                }

                var first = inFlight.First();
                if (first.Key != _nextFrame)
                    throw new InvalidDataException(
                        $"TGA 提交顺序异常：期望 {_nextFrame}，预解码队首为 {first.Key}。");

                var prepared = await first.Value.DecodeTask.ConfigureAwait(false);
                if (!_watcher.TryTake(first.Key, out var committedPath)
                    || !string.Equals(committedPath, first.Value.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"TGA 帧 {first.Key} 在预解码后失去 watcher 所有权，已停止以避免重复或乱序提交。");
                }
                SubmitPreparedFrame(prepared, settings, blend);
                inFlight.Remove(first.Key);
                _nextFrame = first.Key + 1;

                // Source deletion is the commit marker. Never delete a frame
                // merely because decoding finished; Native submit must first
                // have succeeded.
                try { File.Delete(committedPath); } catch { /* cleanup barrier owns leftovers */ }
            }
        }
        catch (Exception ex)
        {
            // Decode work is deliberately non-cancellable once a file lease is
            // taken. Observe every task before exposing the fault so cleanup
            // cannot race a worker still reading the attempt's files.
            try
            {
                await Task.WhenAll(inFlight.Values.Select(static work => work.DecodeTask)).ConfigureAwait(false);
            }
            catch
            {
                // The primary exception below remains authoritative.
            }
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

    private static async Task<PreparedFrame> DecodeFrameAsync(
        int frameIndex,
        string path,
        SemaphoreSlim decodeSlots)
    {
        await decodeSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(path)
                    || !TgaFrameReader.TryReadBgra(path, out var width, out var height, out var bgra))
                {
                    throw new InvalidDataException($"无法读取完整 TGA 帧 {frameIndex}：{path}");
                }

                return new PreparedFrame(frameIndex, width, height, bgra);
            }).ConfigureAwait(false);
        }
        finally
        {
            decodeSlots.Release();
        }
    }

    private void SubmitPreparedFrame(PreparedFrame frame, UserSettings settings, int blend)
    {
        _session ??= CreateSession(settings, frame.Width, frame.Height, blend);
        if (_firstFrameWidth == 0)
        {
            _firstFrameWidth = frame.Width;
            _firstFrameHeight = frame.Height;
        }
        else if (frame.Width != _firstFrameWidth || frame.Height != _firstFrameHeight)
        {
            throw new InvalidDataException(
                $"TGA 分辨率在序列中发生变化：期望 {_firstFrameWidth}x{_firstFrameHeight}，" +
                $"帧 {frame.FrameIndex} 为 {frame.Width}x{frame.Height}。");
        }

        _session.SubmitBgra(frame.Bgra, frame.Width * 4);
        _submittedInputFrames++;
        _fed++;
        _outputFrames = _session.GetProgress().Done;
        if (_submittedInputFrames % Math.Max(1, blend) == 0)
            _motionDiagnostics.Sample(frame.Bgra, frame.Width, frame.Height);
        TrackPlaybackEvidence(frame.Bgra, frame.Width, frame.Height);
        SamplePerformance();

        Status = $"合成中：已喂入 {_fed} 帧，待处理 {_watcher?.PendingCount ?? 0}";
        Changed?.Invoke();
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
        (_processingBackend, _encoderBackend) = session.GetBackends();
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
        _outputFrames = _session.GetProgress().Done;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private sealed record PreparedFrameWork(int FrameIndex, string Path, Task<PreparedFrame> DecodeTask);

    private sealed record PreparedFrame(int FrameIndex, int Width, int Height, byte[] Bgra);

    /// <summary>
    /// Samples the rolling performance windows from real counters. Produced is
    /// the watcher's stable-frame count (duplicate events cannot inflate it),
    /// Consumed is successful native submits, Output is the native
    /// frames_output counter, and the backlog is the current session's
    /// stable pending frames/bytes (candidates keep their own semantics).
    /// </summary>
    private void SamplePerformance()
    {
        var backlog = _watcher?.GetBacklogSnapshot()
            ?? new WatcherBacklogSnapshot(0, 0, 0, 0, false);
        _performanceTracker.AddSample(new CapturePerformanceTracker.Sample(
            Produced: _watcher?.ProducedCount ?? 0,
            Consumed: _submittedInputFrames,
            Output: _outputFrames,
            PendingFrames: backlog.PendingFrames,
            PendingBytes: backlog.PendingBytes));
    }

    private void TrackPlaybackEvidence(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var sample = _evidenceProbe.Sample(bgra, width, height);
        if (_evidenceProbe.IsPlaybackStarted)
        {
            ActivityAnchorFrame ??= (int)_fed;
            HasVisualChange = true;
        }

        // Once playback has been established, remember every frame with an
        // actual block-level visual change. The recorder uses the distance
        // from this frame as positive replay-end evidence when subsequent
        // frames stay visually identical for a bounded window.
        if (HasVisualChange &&
            (sample.ChangedBlockCount > 0 || sample.MeanLumaDelta > 0.01))
        {
            Volatile.Write(ref _lastVisualChangeFrame, (int)_fed);
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
            if (!_finishSucceeded)
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
