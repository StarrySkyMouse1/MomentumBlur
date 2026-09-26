namespace Mmod.Core.Models;

public enum ReplaySourceKind
{
    Local,
    Online,
}

public sealed record ReplayRecord(
    string FilePath,
    int FormatVersion,
    string MapName,
    string PlayerName,
    string PlayerId,
    int TrackNumber,
    int StageNumber,
    double RunTimeSeconds,
    int TickCount,
    DateTimeOffset RecordedAt,
    ReplaySourceKind Source)
{
    public const int MinimumSupportedFormatVersion = 1;
    public const int CurrentFormatVersion = 2;
    public bool IsCompatible => FormatVersion is >= MinimumSupportedFormatVersion and <= CurrentFormatVersion;
    public string? CompatibilityIssue => IsCompatible
        ? null
        : $"不支持 MMTV v{FormatVersion}，当前支持 v{MinimumSupportedFormatVersion}–v{CurrentFormatVersion}";
    public bool IsMainTrack => TrackNumber == 1;
    public int BonusNumber => Math.Max(0, TrackNumber - 1);
    public string TrackLabel => IsMainTrack ? "主赛道" : $"Bonus {BonusNumber}";
}

public sealed record ReplayCatalogIssue(string FilePath, string Message);

public sealed record ReplayCatalogResult(
    IReadOnlyList<ReplayRecord> Records,
    IReadOnlyList<ReplayCatalogIssue> Issues);
