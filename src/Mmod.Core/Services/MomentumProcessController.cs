using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Owns (or attaches to) the Momentum Mod process and its NetCon channel.
/// Provides strict owned shutdown: graceful `quit` → bounded wait → kill
/// fallback (only when OwnsProcess and process identity matches). DisposeAsync
/// never carries business shutdown semantics.
/// 启动时优先「复用已打开的游戏」：同路径 momentum.exe 已在运行则直接连接（凭据
/// 优先取进程命令行解析出的 -netconport/-netconpassword，其次固定默认凭据），
/// OwnsProcess=false（不负责关闭、崩溃恢复不清理）；只有没有游戏在运行时才自启
/// 新进程（固定默认凭据）。有进程但连不上 NetCon 时严格报错，拒绝重复启动。
/// </summary>
public sealed class MomentumProcessController : IGameProcessController, IAsyncDisposable
{
    /// <summary>
    /// 固定 NetCon 端口/密码：自启动与「复用已打开游戏」共用同一组凭据，
    /// 这样暂停后继续、任务完成后再次开始都能直接连回仍在运行的游戏。
    /// </summary>
    public const int DefaultNetConPort = 29071;
    public const string DefaultNetConPassword = "mmod_record_local";

    /// <summary>复用探测时每个候选凭据等待 NetCon 连接的最长时间（避免无 NetCon 服务的实例拖慢启动）。</summary>
    private static readonly TimeSpan AttachConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>复用探测结果。</summary>
    public enum ReuseProbeResult
    {
        /// <summary>没有匹配的游戏进程在运行，需要自启新进程。</summary>
        NoProcessFound,
        /// <summary>已附加到运行中的游戏（OwnsProcess=false）。</summary>
        Connected,
        /// <summary>游戏进程在运行但 NetCon 连接失败（已抛出 InvalidOperationException）。</summary>
        ConnectFailed,
    }

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
        var reuse = await TryReuseExistingAsync(gameRoot, DefaultNetConPort, DefaultNetConPassword, token);
        if (reuse == ReuseProbeResult.Connected)
            return;
        if (reuse == ReuseProbeResult.ConnectFailed)
            return; // 已在 TryReuseExistingAsync 内抛出详细错误

        var exe = Path.Combine(gameRoot, "bin", "win64", "momentum.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 Momentum Mod 可执行文件。", exe);
        var startInfo = new ProcessStartInfo(
            exe,
            $"-console -novid -netconport {DefaultNetConPort} -netconpassword {DefaultNetConPassword}")
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
        var connect = NetCon.ConnectAsync(DefaultNetConPort, DefaultNetConPassword, TimeSpan.FromMinutes(2), connectCts.Token);
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

    /// <summary>
    /// 复用已打开的游戏：匹配同路径 momentum.exe 的进程，并按序尝试候选凭据——
    /// ① 进程命令行解析出的 <c>-netconport/-netconpassword</c>（用户或上次 Runner
    /// 带参启动的实例）；② 固定默认凭据（本 Runner 启动的实例）。任一成功 → 附加
    /// （OwnsProcess=false，不负责关闭、崩溃恢复不清理）；进程存在但全部连不上 →
    /// 抛错（严格拒绝重复启动）；进程已退出/不存在 → NoProcessFound。
    /// </summary>
    public async Task<ReuseProbeResult> TryReuseExistingAsync(string gameRoot, int fallbackPort, string fallbackPassword, CancellationToken token)
    {
        var match = FindRunningGameProcess(gameRoot);
        if (match is null)
            return ReuseProbeResult.NoProcessFound;

        var candidates = new List<(int Port, string Password)>();
        try
        {
            if (TryGetNetConArguments(match.Id) is { } parsed)
                candidates.Add(parsed);
        }
        catch
        {
            // 命令行解析失败不影响后续候选
        }
        candidates.Add((fallbackPort, fallbackPassword));

        string? lastError = null;
        foreach (var (port, password) in candidates)
        {
            try
            {
                await NetCon.ConnectAsync(port, password, AttachConnectTimeout, token);
                return AttachTo(match);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (token.IsCancellationRequested)
                    throw;
            }
        }

        // 全部候选失败：进程可能已被清掉（例如崩溃恢复刚杀掉的实例）→ 视为无进程；
        // 仍活着 → 用户已打开的游戏没有可用的 NetCon，严格拒绝重复启动。
        if (!IsProcessAlive(match))
        {
            match.Dispose();
            return ReuseProbeResult.NoProcessFound;
        }

        var exePath = match.MainModule?.FileName ?? Path.Combine(gameRoot, "bin", "win64", "momentum.exe");
        match.Dispose();
        var tried = string.Join("、", candidates.Select(c => c.Port.ToString()));
        throw new InvalidOperationException(
            $"检测到运行中的 Momentum Mod（{exePath}），但无法通过 NetCon 连接（已尝试端口：{tried}，最后错误：{lastError}）。" +
            $"为避免重复启动游戏实例，请先关闭该实例后重试；或使用参数 -netconport/-netconpassword 启动游戏以便直接复用。");
    }

    private ReuseProbeResult AttachTo(Process match)
    {
        // 附加成功：不拥有进程，不负责关闭，崩溃恢复也不清理它
        string fileName;
        try { fileName = match.MainModule?.FileName ?? string.Empty; } catch { fileName = string.Empty; }
        Process = match;
        OwnsProcess = false;
        GameSessionId = Guid.NewGuid().ToString("N");
        ExePath = fileName;
        try { ProcessStartTimeUtc = match.StartTime.ToUniversalTime(); } catch { ProcessStartTimeUtc = null; }
        _exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = WatchExitAsync(match);
        return ReuseProbeResult.Connected;
    }

    /// <summary>
    /// 从运行中进程的命令行解析 <c>-netconport / -netconpassword</c> 参数。
    /// 覆盖用户手动带参启动或上次 Runner 启动后遗留的实例（随机端口/密码时仍可复用）。
    /// </summary>
    private static (int Port, string Password)? TryGetNetConArguments(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + processId);
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var commandLine = item["CommandLine"] as string;
                    var parsed = ParseNetConArguments(commandLine);
                    if (parsed is not null)
                        return parsed;
                }
            }
        }
        catch
        {
            // WMI 不可用/权限受限：返回 null，走固定凭据候选
        }
        return null;
    }

    private static (int Port, string Password)? ParseNetConArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var portMatch = Regex.Match(commandLine, @"-netconport\s+(\d+)", RegexOptions.IgnoreCase);
        if (!portMatch.Success || !int.TryParse(portMatch.Groups[1].Value, out var port))
            return null;

        var passwordMatch = Regex.Match(commandLine, @"-netconpassword\s+(\S+)", RegexOptions.IgnoreCase);
        return (port, passwordMatch.Success ? passwordMatch.Groups[1].Value : string.Empty);
    }

    private static Process? FindRunningGameProcess(string gameRoot)
    {
        var exe = Path.Combine(gameRoot, "bin", "win64", "momentum.exe");
        try
        {
            foreach (var candidate in Process.GetProcessesByName("momentum"))
            {
                var keep = false;
                try
                {
                    if (!candidate.HasExited)
                    {
                        var fileName = candidate.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(fileName) &&
                            string.Equals(fileName, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            keep = true;
                        }
                    }
                }
                catch
                {
                    // 访问被拒或进程已退出，视为不匹配
                }

                if (keep)
                    return candidate;
                candidate.Dispose();
            }
        }
        catch
        {
            // 进程快照失败：视为无匹配
        }
        return null;
    }

    private static bool IsProcessAlive(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
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

    public async ValueTask DisposeAsync()
    {
        await NetCon.DisposeAsync();
        Process?.Dispose();
    }
}
