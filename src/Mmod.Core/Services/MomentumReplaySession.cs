using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Shared Momentum TV replay NetCon steps used by Capture-first recording and
/// verification. Map readiness uses a positive probe (status output when
/// available, degraded engine-responsiveness otherwise); startmovie/endmovie
/// use strict typed commands with failure-pattern detection.
/// </summary>
public static class MomentumReplaySession
{
    public static string Quote(string text) => $"\"{text.Replace("\"", string.Empty)}\"";

    public static string BuildGameRelativeReplayPath(string gameRoot, string replayPath)
    {
        var contentRoot = Path.Combine(Path.GetFullPath(gameRoot), "momentum");
        var relative = Path.GetRelativePath(contentRoot, Path.GetFullPath(replayPath));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("回放文件不在游戏 momentum 目录中。");
        return relative.Replace('\\', '/').Replace("\"", string.Empty);
    }

    public static string BuildManualConsoleScript(string mapName, string gameRelativeReplayPath)
    {
        var replay = Quote(gameRelativeReplayPath);
        var sb = new StringBuilder();
        sb.AppendLine("mom_lobby_leave");
        sb.AppendLine("mom_lobby_type 0");
        sb.AppendLine("mom_lobby_create");
        sb.AppendLine($"map {Quote(mapName)}");
        sb.AppendLine("startmovie frame tga");
        sb.AppendLine($"mom_tv_replay_watch {replay}");
        sb.AppendLine("endmovie");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Leaves any inherited lobby, then creates an invite-only Steam lobby for
    /// this game session. This preserves the signed-in Steam identity/avatar
    /// while preventing public lobby players and chat from entering capture.
    /// </summary>
    public static async Task EnsurePrivateSoloLobbyAsync(
        INetConClient netCon,
        Action<string>? log,
        CancellationToken token)
    {
        log?.Invoke("PrivateSoloLobby：离开当前大厅…");
        await netCon.ExecuteAsync("mom_lobby_leave", TimeSpan.FromSeconds(10), token);
        log?.Invoke("PrivateSoloLobby：设置为仅邀请并创建私人大厅…");
        await netCon.ExecuteAsync("mom_lobby_type 0", TimeSpan.FromSeconds(10), token);
        await netCon.ExecuteAsync("mom_lobby_create", TimeSpan.FromSeconds(10), token);
        log?.Invoke("PrivateSoloLobby：私人单人大厅已初始化（保留 Steam 登录）");
    }

    /// <summary>
    /// Reads a numeric Source convar value from the console transcript returned
    /// by a strict query. Source branches vary between quoted and plain output,
    /// so accept both forms but require the requested convar name on the line.
    /// </summary>
    public static bool TryReadNumericConVar(
        NetConCommandResult result,
        string conVarName,
        out double value)
    {
        value = 0;
        var escapedName = Regex.Escape(conVarName);
        var patterns = new[]
        {
            $"\\\"?{escapedName}\\\"?\\s*=\\s*\\\"?(?<value>[-+]?[0-9]+(?:\\.[0-9]+)?)",
            $"\\b{escapedName}\\b\\s*:\\s*\\\"?(?<value>[-+]?[0-9]+(?:\\.[0-9]+)?)",
        };

        foreach (var line in result.CapturedConsoleLines)
        {
            foreach (var pattern in patterns)
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success &&
                    double.TryParse(
                        match.Groups["value"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Unverified-on-real-machine failure patterns (must be confirmed with a
    /// real Momentum console before being treated as authoritative; they only
    /// ever fail an attempt faster, never prove success).
    /// </summary>
    public static readonly string[] StartMovieFailurePatterns =
    [
        "already recording",
        "cannot start movie",
    ];

    public static readonly string[] ReplayWatchFailurePatterns =
    [
        "Failed to load replay",
        "Failed to open replay",
        "Invalid replay file",
        "Unknown command \"mom_tv_replay_watch\"",
    ];

    /// <summary>
    /// Changes map, then positively confirms readiness via IMapReadinessProbe.
    /// No fixed sleep is treated as proof; sleep only paces the probe.
    /// </summary>
    public static async Task ChangeMapAsync(
        INetConClient netCon,
        string mapName,
        Action<string>? log,
        CancellationToken token,
        IMapReadinessProbe? probe = null,
        RecordingTimeoutPolicy? timeouts = null)
    {
        timeouts ??= RecordingTimeoutPolicy.Default;
        probe ??= new NetConMapReadinessProbe();

        log?.Invoke($"ChangingMap：map {Quote(mapName)}");
        // map must be its own line — a trailing "; echo …" ACK is discarded
        // while the level loads. Failure is detected by the probe below.
        await netCon.SendAsync($"map {Quote(mapName)}", token);

        log?.Invoke("WaitingMapReady：正向确认当前地图…");
        var result = await probe.ProbeAsync(netCon, mapName, timeouts, log, token);

        if (!result.IsReady)
        {
            throw new TimeoutException(
                $"地图就绪超时（期望 {mapName}；当前 {result.CurrentMap ?? "未知"}；degraded={result.IsDegradedFallback}）。");
        }

        if (result.IsDegradedFallback)
        {
            log?.Invoke("WaitingMapReady：⚠ degraded 模式（未能从 status 正向读取当前地图名，仅确认引擎响应）。");
        }

        await Task.Delay(timeouts.MapSettleDelay, token);
    }

    /// <summary>Strict startmovie with failure-pattern detection.</summary>
    public static async Task<NetConCommandResult> ExecuteStartMovieAsync(
        INetConClient netCon,
        string sequenceName,
        RecordingTimeoutPolicy timeouts,
        Action<string>? log,
        CancellationToken token)
    {
        var cmd = WatchDirectoryHelper.BuildGameStartmovieCommand(sequenceName);
        log?.Invoke($"StartMovie：{cmd}");
        var result = await netCon.ExecuteStrictAsync(cmd, timeouts.StartMovieTimeout, StartMovieFailurePatterns, token);
        if (result.MatchedFailurePattern is not null)
            throw new InvalidOperationException($"startmovie 失败：{result.MatchedFailurePattern}（{cmd}）");
        return result;
    }

    /// <summary>
    /// Strict endmovie. Only CommandAcked / KnownAlreadyStopped may proceed to
    /// TGA quiescence; the physical quiescence itself is the real stop proof.
    /// </summary>
    public static async Task<StopMovieResult> ExecuteEndMovieAsync(
        INetConClient netCon,
        RecordingTimeoutPolicy timeouts,
        Action<string>? log,
        CancellationToken token)
    {
        log?.Invoke("RequestingMovieStop：endmovie");
        try
        {
            var result = await netCon.ExecuteStrictAsync("endmovie", timeouts.StopMovieTimeout, [], token);
            if (!result.Succeeded)
                return StopMovieResult.CommandRejected;
            log?.Invoke("EndMovieAck");
            return StopMovieResult.CommandAcked;
        }
        catch (IOException)
        {
            return StopMovieResult.NetConLost;
        }
        catch (TimeoutException)
        {
            return StopMovieResult.TimedOut;
        }
        catch (InvalidOperationException)
        {
            return StopMovieResult.NetConLost;
        }
        catch (OperationCanceledException)
        {
            // cleanup token expired while stopping
            return StopMovieResult.TimedOut;
        }
    }

    /// <summary>
    /// Capture-first：发送 mom_tv_replay_watch（单独一行），返回 typed result。
    /// 画面是否开播由 TGA PlaybackEvidence 判定。
    /// </summary>
    public static async Task<NetConCommandResult> StartWatchAsync(
        INetConClient netCon,
        string gameRelativeReplayPath,
        Action<string>? log,
        CancellationToken token,
        RecordingTimeoutPolicy? timeouts = null)
    {
        timeouts ??= RecordingTimeoutPolicy.Default;
        var watchCmd = $"mom_tv_replay_watch {Quote(gameRelativeReplayPath)}";
        log?.Invoke($"StartingReplay：{watchCmd}");
        await netCon.SendAsync(watchCmd, token);
        await Task.Delay(timeouts.ReplayWatchSettle, token);

        log?.Invoke("StartingReplay：确认控制台可响应…");
        var result = await netCon.ExecuteStrictAsync(
            "echo MMOD_REPLAY_WATCH_SENT",
            timeouts.ReplayWatchTimeout,
            ReplayWatchFailurePatterns,
            token);
        if (result.MatchedFailurePattern is not null)
            throw new InvalidOperationException($"回放加载失败：{result.MatchedFailurePattern}");
        log?.Invoke("StartingReplay：watch 命令已受理");
        return result;
    }
}
