using System.IO;
using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

public sealed class TgaPipelineOrchestrator : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TgaDirectoryWatcher? _watcher;
    private NativeBlendSession? _session;
    private int _nextFrame;
    private int _fed;
    private ulong? _firstFrameSignature;
    private ulong? _previousFrameSignature;

    public bool IsRunning => _loop is { IsCompleted: false };

    public int PendingCount => _watcher?.PendingCount ?? 0;
    public int FedCount => _fed;
    /// <summary>True once any frame differed from the first sampled signature (PlaybackStartLowerBound reached).</summary>
    public bool HasVisualChange { get; private set; }
    /// <summary>FedCount of the first frame that differed from the opening still; null until then.</summary>
    public int? ActivityAnchorFrame { get; private set; }
    /// <summary>FedCount of the most recent frame that differed from the previous frame.</summary>
    public int? LastVisualChangeFrame { get; private set; }
    public string? OutputPath { get; private set; }
    public string? WatchDirectory { get; private set; }
    public string Status { get; private set; } = "空闲";

    public event Action? Changed;

    public Task StartAsync(UserSettings settings) => StartAsync(settings, null, true);

    public async Task StartAsync(UserSettings settings, string? outputPath, bool acceptPreSessionFiles = false)
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
        _fed = 0;
        _firstFrameSignature = null;
        _previousFrameSignature = null;
        HasVisualChange = false;
        ActivityAnchorFrame = null;
        LastVisualChangeFrame = null;
        _nextFrame = 0;
        _cts = new CancellationTokenSource();
        _watcher = new TgaDirectoryWatcher(watchDir);
        _watcher.PendingChanged += () => Changed?.Invoke();
        _watcher.Start(acceptPreSessionFiles: acceptPreSessionFiles);

        Status = $"监视中：{watchDir}";
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
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待 TGA 写帧超时：需要至少 {minimumFed} 帧，当前 {FedCount}。");
            if (!string.IsNullOrWhiteSpace(Status) && Status.StartsWith("错误：", StringComparison.Ordinal))
                throw new InvalidOperationException(Status);
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    public async Task WaitUntilActivityAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ActivityAnchorFrame is null)
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待回放画面运动超时（{timeout.TotalSeconds:0}s 内无 VisualActivity）。");
            if (!string.IsNullOrWhiteSpace(Status) && Status.StartsWith("错误：", StringComparison.Ordinal))
                throw new InvalidOperationException(Status);
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears ActivityAnchor / signatures so CaptureReady still frames cannot count as PlaybackActivity.
    /// Call after CaptureReady, immediately before mom_tv_replay_watch.
    /// </summary>
    public void ResetActivityTracking()
    {
        _firstFrameSignature = null;
        _previousFrameSignature = null;
        HasVisualChange = false;
        ActivityAnchorFrame = null;
        LastVisualChangeFrame = null;
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

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

        _watcher?.Stop();
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            _session?.Finish();
            Status = File.Exists(OutputPath) ? $"完成：{OutputPath}" : "已停止（无输出帧）";
        }
        catch (Exception ex)
        {
            Status = $"收尾失败：{ex.Message}";
        }
        finally
        {
            _session?.Dispose();
            _session = null;
            _cts.Dispose();
            _cts = null;
            _loop = null;
            Changed?.Invoke();
        }
    }

    private void RunLoop(UserSettings settings, int blend, CancellationToken token)
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

            try
            {
                _session ??= NativeBlendSession.Create(
                    width,
                    height,
                    blend,
                    (float)settings.Exposure,
                    ProjectConstants.FinalOutputFramerate,
                    OutputPath!);

                _session.SubmitBgra(bgra, width * 4);
                var changed = TrackVisualChange(bgra);
                _fed++;
                if (changed)
                {
                    LastVisualChangeFrame = _fed;
                    ActivityAnchorFrame ??= _fed;
                    HasVisualChange = true;
                }

                Status = $"合成中：已喂入 {_fed} 帧，待处理 {_watcher.PendingCount}";
                Changed?.Invoke();

                try { File.Delete(path); } catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                Status = $"错误：{ex.Message}";
                Changed?.Invoke();
                break;
            }
        }

        var drainDeadline = DateTime.UtcNow.AddSeconds(15);
        while (_watcher is not null && DateTime.UtcNow < drainDeadline)
        {
            if (!_watcher.TryGetMinPendingFrameIndex(out var min))
                break;
            if (!_watcher.TryTake(min, out var path))
                break;
            if (!TgaFrameReader.TryReadBgra(path, out var width, out var height, out var bgra))
            {
                try { File.Delete(path); } catch { }
                continue;
            }

            try
            {
                _session ??= NativeBlendSession.Create(
                    width,
                    height,
                    blend,
                    (float)settings.Exposure,
                    ProjectConstants.FinalOutputFramerate,
                    OutputPath!);
                _session.SubmitBgra(bgra, width * 4);
                _fed++;
                try { File.Delete(path); } catch { }
            }
            catch
            {
                break;
            }
        }
    }

    /// <returns>True when this frame's signature differs from the previous frame.</returns>
    private bool TrackVisualChange(ReadOnlySpan<byte> bgra)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var stride = Math.Max(4, bgra.Length / 4096);
        stride -= stride % 4;
        for (var i = 0; i < bgra.Length; i += stride)
        {
            hash ^= bgra[i];
            hash *= prime;
        }

        _firstFrameSignature ??= hash;
        var changed = _previousFrameSignature is not null && _previousFrameSignature.Value != hash;
        _previousFrameSignature = hash;
        return changed;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync().ConfigureAwait(false);
    }
}
