using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Owns (or attaches to) the Momentum Mod process and its NetCon channel.
/// Provides strict owned shutdown: graceful `quit` → bounded wait → kill
/// fallback (only when OwnsProcess and process identity matches). DisposeAsync
/// never carries business shutdown semantics.
/// </summary>
public sealed class MomentumProcessController : IGameProcessController, IAsyncDisposable
{
    private TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Process? Process { get; private set; }
    public INetConClient NetCon { get; } = new MomentumNetConClient();
    public bool OwnsProcess { get; private set; }
    public string? GameSessionId { get; private set; }
    public int? ProcessId => Process is { HasExited: false } ? Process.Id : null;
    public DateTime? ProcessStartTimeUtc { get; private set; }
    public string? ExePath { get; private set; }

    /// <summary>Completes when the game process exits (faulted on WaitForExit failure).</summary>
    public Task ExitTask => _exit.Task;

    public bool IsGameRunning => Process is { HasExited: false };

    public async Task StartAsync(string gameRoot, CancellationToken token)
    {
        var exe = Path.Combine(gameRoot, "bin", "win64", "momentum.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 Momentum Mod 可执行文件。", exe);
        var port = ReservePort();
        var password = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(exe, $"-console -novid -netconport {port} -netconpassword {password}")
        {
            WorkingDirectory = gameRoot,
            UseShellExecute = true,
        };
        Process = System.Diagnostics.Process.Start(startInfo);
        if (Process is null) throw new InvalidOperationException("Momentum Mod 启动失败。");
        OwnsProcess = true;
        GameSessionId = Guid.NewGuid().ToString("N");
        ProcessStartTimeUtc = Process.StartTime.ToUniversalTime();
        ExePath = exe;
        _exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = WatchExitAsync(Process);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var connect = NetCon.ConnectAsync(port, password, TimeSpan.FromMinutes(2), connectCts.Token);
        var exited = _exit.Task;
        var completed = await Task.WhenAny(connect, exited);
        if (completed == exited)
        {
            connectCts.Cancel();
            try { await connect; } catch { }
            throw new InvalidOperationException($"Momentum Mod 在 NetCon 连接完成前退出（退出代码 {Process.ExitCode}）。");
        }
        await connect;
    }

    private async Task WatchExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _exit.TrySetException(ex);
            return;
        }
        _exit.TrySetResult();
    }

    /// <summary>Graceful quit for a normally-completed queue.</summary>
    public async Task CloseOwnedAsync(CancellationToken token)
    {
        if (!OwnsProcess || Process is null || Process.HasExited) return;
        try { await NetCon.ExecuteAsync("quit", TimeSpan.FromSeconds(10), token); } catch { }
        try { await Process.WaitForExitAsync(token); } catch { }
    }

    /// <summary>
    /// Strict owned-process shutdown policy: graceful quit → bounded wait →
    /// kill fallback only when identity matches. Used on fatal failure / StopNow.
    /// </summary>
    public async Task ShutdownOwnedProcessAsync(RecordingTimeoutPolicy timeouts, CancellationToken cleanupToken)
    {
        if (!OwnsProcess || Process is null || Process.HasExited)
        {
            return;
        }

        var pid = Process.Id;
        var startTime = ProcessStartTimeUtc;
        var exe = ExePath;

        try
        {
            await NetCon.ExecuteAsync("quit", timeouts.OwnedGameGracefulQuitTimeout, cleanupToken);
        }
        catch
        {
            // fall through to wait/kill
        }

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cleanupToken);
            waitCts.CancelAfter(timeouts.OwnedGameGracefulQuitTimeout);
            await Process.WaitForExitAsync(waitCts.Token);
            return;
        }
        catch (OperationCanceledException)
        {
            // graceful quit didn't work; kill fallback below
        }

        // Kill fallback: only for the exact process we started (PID reuse guard).
        try
        {
            using var candidate = Process.GetProcessById(pid);
            if (candidate.StartTime.ToUniversalTime() == startTime &&
                string.Equals(candidate.MainModule?.FileName ?? string.Empty, exe, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Kill(entireProcessTree: true);
                try { await candidate.WaitForExitAsync(); } catch { }
            }
        }
        catch
        {
            // process already gone
        }
    }

    public GameSessionCompatibilityKey BuildCompatibilityKey(string gameRoot, string watchDirectory) =>
        new(NormalizePath(gameRoot), NormalizePath(watchDirectory));

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return (path ?? string.Empty).TrimEnd('\\').ToLowerInvariant(); }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await NetCon.DisposeAsync();
        Process?.Dispose();
    }
}
