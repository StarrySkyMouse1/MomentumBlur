namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Pure, side-effect-free disk-safety contract (S1: percentage-based watch-drive
/// free-space floor). No DriveInfo, no file system access, no WPF dependency.
///
/// Semantics:
/// - 0% disables the disk-space protection (state Disabled).
/// - 1..50% is the safety floor as a percentage of the watch drive's total capacity.
/// - The yellow warning line is safety + 5 percentage points, capped at 100%.
/// - All threshold boundaries are inclusive (freePercent &lt;= threshold counts).
///
/// This stage only defines the contract; nothing in the runtime samples or
/// enforces disk state from this policy yet.
/// </summary>
public static class DiskSafetyPolicy
{
    public const int DefaultSafetyPercent = 10;
    public const int MaxSafetyPercent = 50;
    public const int WarningOffsetPercent = 5;

    /// <summary>
    /// Clamp a raw percentage to the closed range [0, 50]:
    /// below 0 → 0 (protection on), above 50 → 50 (hard ceiling).
    /// </summary>
    public static int NormalizeSafetyPercent(int percent) => Math.Clamp(percent, 0, MaxSafetyPercent);

    /// <summary>
    /// Yellow warning line = safety + 5 percentage points, capped at 100%.
    /// The input is normalized to [0, 50] first.
    /// </summary>
    public static int CalculateWarningPercent(int safetyPercent)
    {
        var normalized = NormalizeSafetyPercent(safetyPercent);
        return Math.Min(100, normalized + WarningOffsetPercent);
    }

    /// <summary>
    /// Safety floor in bytes for a capacity and a percentage. Uses a controlled
    /// double intermediate so long × percent cannot overflow; the final long is
    /// explicitly clamped to long.MaxValue. percent is normalized to [0, 50] first,
    /// so with totalBytes &lt;= long.MaxValue the result is at most totalBytes / 2.
    /// </summary>
    public static long CalculateThresholdBytes(long totalBytes, int percent)
    {
        var normalized = NormalizeSafetyPercent(percent);
        if (totalBytes <= 0 || normalized <= 0)
            return 0;
        var threshold = totalBytes * (normalized / 100.0);
        return threshold >= long.MaxValue ? long.MaxValue : (long)threshold;
    }

    /// <summary>Free space as a percentage of total capacity; 0 when the total is invalid.</summary>
    public static double CalculateFreePercent(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0)
            return 0;
        return freeBytes * 100.0 / totalBytes;
    }

    /// <summary>
    /// Evaluate drive state from raw sample inputs. Precedence (all boundaries
    /// inclusive, per contract):
    /// safetyPercent == 0 → Disabled;
    /// totalBytes &lt;= 0 or freeBytes &lt; 0 → Unavailable;
    /// freePercent &lt;= safetyPercent → Critical;
    /// freePercent &lt;= warningPercent → Warning;
    /// otherwise → Normal.
    /// </summary>
    public static DiskSafetyEvaluation Evaluate(long totalBytes, long freeBytes, int safetyPercent)
    {
        var safety = NormalizeSafetyPercent(safetyPercent);
        var warningPercent = CalculateWarningPercent(safety);
        if (safety == 0)
            return new DiskSafetyEvaluation(DiskSafetyState.Disabled, 0, 0, 0, warningPercent, 0);
        if (totalBytes <= 0 || freeBytes < 0)
            return new DiskSafetyEvaluation(DiskSafetyState.Unavailable, CalculateFreePercent(totalBytes, freeBytes), safety, 0, warningPercent, 0);

        var freePercent = CalculateFreePercent(totalBytes, freeBytes);
        var safetyBytes = CalculateThresholdBytes(totalBytes, safety);
        var warningBytes = CalculateThresholdBytes(totalBytes, warningPercent);
        DiskSafetyState state;
        if (freePercent <= safety)
            state = DiskSafetyState.Critical;
        else if (freePercent <= warningPercent)
            state = DiskSafetyState.Warning;
        else
            state = DiskSafetyState.Normal;
        return new DiskSafetyEvaluation(state, freePercent, safety, safetyBytes, warningPercent, warningBytes);
    }

    /// <summary>
    /// Build the full pure-data disk-health snapshot model from raw sample inputs.
    /// The snapshot only carries computed values; no runtime sampling happens here.
    /// </summary>
    public static DiskHealthSnapshot EvaluateSnapshot(
        string driveRoot, long totalBytes, long freeBytes, int safetyPercent, DateTimeOffset sampledAt)
    {
        var evaluation = Evaluate(totalBytes, freeBytes, safetyPercent);
        var usedBytes = totalBytes > 0 && freeBytes >= 0 ? Math.Max(0, totalBytes - freeBytes) : 0;
        return new DiskHealthSnapshot(
            driveRoot,
            totalBytes,
            freeBytes,
            usedBytes,
            evaluation.FreePercent,
            evaluation.SafetyPercent,
            evaluation.SafetyBytes,
            evaluation.WarningPercent,
            evaluation.WarningBytes,
            evaluation.State,
            sampledAt);
    }
}