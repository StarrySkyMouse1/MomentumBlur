using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class ReplayCatalogService
{
    public ReplayCatalogResult Scan(string gameRootPath)
    {
        var records = new List<ReplayRecord>();
        var issues = new List<ReplayCatalogIssue>();
        if (string.IsNullOrWhiteSpace(gameRootPath))
            return new ReplayCatalogResult(records, [new ReplayCatalogIssue(string.Empty, "未配置游戏根目录。")]);

        var root = Path.Combine(Path.GetFullPath(gameRootPath), "momentum", "momtv");
        if (!Directory.Exists(root))
            return new ReplayCatalogResult(records, [new ReplayCatalogIssue(root, "未找到 Momentum 回放目录。")]);

        foreach (var file in Directory.EnumerateFiles(root, "*.mtv", SearchOption.AllDirectories))
        {
            try { records.Add(MtvReplayParser.Parse(file)); }
            catch (Exception ex) { issues.Add(new ReplayCatalogIssue(file, ex.Message)); }
        }

        var ordered = records
            .OrderBy(r => r.MapName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.TrackNumber)
            .ThenBy(r => r.StageNumber)
            .ThenBy(r => r.RunTimeSeconds)
            .ThenByDescending(r => r.RecordedAt)
            .ToList();
        return new ReplayCatalogResult(ordered, issues);
    }
}
