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
    private int _nextFrame = 0;
    private int _fed;

    public bool IsRunning => _loop is { IsCompleted: false };

    public int PendingCount => _watcher?.PendingCount ?? 0;
    public int FedCount => _fed;
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

        OutputPath = string.IsNullOrWhiteSpace(outputPath) ? Path.Combine(outputDir, $"tga_{DateTime.Now:yyyyMMdd_HHmmss}.mp4") : Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        WatchDirectory = watchDir;
        _fed = 0;
        _nextFrame = 0;
        _cts = new CancellationTokenSource();
        _watcher = new TgaDirectoryWatcher(watchDir);
        _watcher.PendingChanged += () => Changed?.Invoke();
        // 接受目录中已有 TGA：允许「先录制再开监视」；新写入的帧仍照常接入
        _watcher.Start(acceptPreSessionFiles: acceptPreSessionFiles);

        Status = $"监视中：{watchDir}（含已有 TGA）";
        Changed?.Invoke();

        var blend = Math.Max(1, settings.SupersamplingMultiplier);
        var token = _cts.Token;
        _loop = Task.Run(() => RunLoop(settings, blend, token), token);
        await Task.CompletedTask;
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

            // 始终取最小待处理帧，避免 nextFrame 超前后卡死（重入队/删文件后残留）
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
                _fed++;
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

        // drain briefly after cancel
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

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync().ConfigureAwait(false);
    }
}
