using System.Text;

namespace Mmod.Core.Services;

/// <summary>
/// Shared Momentum TV replay NetCon steps used by Capture-first recording and verification.
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
        sb.AppendLine($"map {Quote(mapName)}");
        sb.AppendLine("startmovie frame tga");
        sb.AppendLine($"mom_tv_replay_watch {replay}");
        sb.AppendLine("endmovie");
        return sb.ToString().TrimEnd();
    }

    public static async Task ChangeMapAsync(
        MomentumNetConClient netCon,
        string mapName,
        Action<string>? log,
        CancellationToken token)
    {
        log?.Invoke($"WaitingMapReady：map {Quote(mapName)}");
        // map must be its own line — a trailing "; echo …" ACK is discarded while the level loads.
        await netCon.SendAsync($"map {Quote(mapName)}", token);
        await Task.Delay(2000, token);
        log?.Invoke("WaitingMapReady：等待地图加载完成…");
        await netCon.ExecuteCheckedAsync(
            "echo MMOD_MAP_READY",
            TimeSpan.FromMinutes(3),
            line => line.Contains("Unknown command \"map\"", StringComparison.OrdinalIgnoreCase),
            token);
        log?.Invoke("WaitingMapReady：地图已就绪");
        await Task.Delay(1500, token);
    }

    /// <summary>
    /// Capture-first：仅发送 mom_tv_replay_watch（单独一行）。画面是否开播由 TGA VisualActivity 判定。
    /// </summary>
    public static async Task StartWatchAsync(
        MomentumNetConClient netCon,
        string gameRelativeReplayPath,
        Action<string>? log,
        CancellationToken token)
    {
        var watchCmd = $"mom_tv_replay_watch {Quote(gameRelativeReplayPath)}";
        log?.Invoke($"StartingReplay：{watchCmd}");
        await netCon.SendAsync(watchCmd, token);
        await Task.Delay(800, token);

        log?.Invoke("StartingReplay：确认控制台可响应…");
        await netCon.ExecuteCheckedAsync(
            "echo MMOD_REPLAY_WATCH_SENT",
            TimeSpan.FromMinutes(2),
            line => line.Contains("Failed to load replay", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed to open replay", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Invalid replay file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Unknown command \"mom_tv_replay_watch\"", StringComparison.OrdinalIgnoreCase),
            token);
        log?.Invoke("StartingReplay：watch 命令已受理");
    }
}
