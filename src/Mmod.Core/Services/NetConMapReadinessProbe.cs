namespace Mmod.Core.Services;

using System.Text.RegularExpressions;
using Mmod.Core.Models;

/// <summary>
/// Positive map-readiness probe. First tries to read the actual current map via
/// the Source console `status` command; if the real console output format is not
/// recognizable (unverified on real Momentum), it degrades to engine
/// responsiveness + no failure output and explicitly marks IsDegradedFallback so
/// callers know the map identity was NOT positively proven.
/// </summary>
public sealed partial class NetConMapReadinessProbe : IMapReadinessProbe
{
    [GeneratedRegex(@"map\s*:\s*(?<map>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusMapRegex();

    [GeneratedRegex(@"^map\s+(?<map>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapLineRegex();

    /// <summary>Failure patterns considered fatal for the map command itself.</summary>
    public static readonly string[] MapCommandFailurePatterns =
    [
        "Unknown command \"map\"",
        "Map load failed",
        "Failed to load map",
        "map file is missing",
    ];

    public async Task<MapReadinessResult> ProbeAsync(
        INetConClient netCon,
        string expectedMap,
        RecordingTimeoutPolicy timeouts,
        Action<string>? log,
        CancellationToken token)
    {
        var expected = NormalizeMapName(expectedMap);
        var consoleTail = new List<string>();
        var deadline = DateTime.UtcNow + timeouts.MapLoadTimeout;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            // Try positive proof: read the current map from `status` output.
            string? parsedMap = null;
            try
            {
                var result = await netCon.ExecuteStrictAsync(
                    "status", timeouts.NetConReconnectTimeout, [], token);
                foreach (var line in result.CapturedConsoleLines)
                {
                    consoleTail.Add(line);
                    var m = StatusMapRegex().Match(line);
                    if (m.Success)
                    {
                        parsedMap = NormalizeMapName(m.Groups["map"].Value);
                        break;
                    }
                    var m2 = MapLineRegex().Match(line);
                    if (m2.Success)
                    {
                        parsedMap = NormalizeMapName(m2.Groups["map"].Value);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Engine unresponsive or console closed → not ready.
                return new MapReadinessResult(null, expected, false, false, true, consoleTail.TakeLast(20).ToList());
            }

            if (parsedMap is not null)
            {
                if (string.Equals(parsedMap, expected, StringComparison.Ordinal))
                {
                    log?.Invoke($"MapReady：正向确认当前地图 {parsedMap} == {expected}");
                    return new MapReadinessResult(parsedMap, expected, true, true, false, consoleTail.TakeLast(20).ToList());
                }

                // Engine responsive but on a different map — keep probing.
                log?.Invoke($"WaitingMapReady：当前地图 {parsedMap}（期望 {expected}）…");
                await Task.Delay(timeouts.MapProbeInterval, token);
                continue;
            }

            // Degraded fallback: engine echoes but map identity is unreadable.
            log?.Invoke("WaitingMapReady：无法解析 status 输出的当前地图，使用 degraded 模式（仅引擎响应）…");
            return new MapReadinessResult(null, expected, true, true, true, consoleTail.TakeLast(20).ToList());
        }

        return new MapReadinessResult(null, expected, false, true, true, consoleTail.TakeLast(20).ToList());
    }

    public static string NormalizeMapName(string map)
    {
        var name = map.Trim().Trim('"');
        // Strip workshop id prefixes like "workshop/123456789/".
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        // Strip map extension if present.
        if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }
}
