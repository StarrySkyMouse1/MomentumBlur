using System.IO;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Health monitor over an owned MomentumProcessController plus the capture
/// watch directory. Game exit is surfaced through ExitTask so the recording
/// loop can fail within seconds rather than waiting for a progress timeout.
/// Disk health is sampled from the volume that actually contains the TGA watch
/// directory: the absolute watch path's root drive is read via DriveInfo and
/// evaluated by DiskSafetyPolicy into a full DiskHealthSnapshot. Sampling
/// failures never throw — they surface as an Unavailable snapshot.
/// </summary>
public sealed class GameSessionHealthMonitor : IGameSessionHealthMonitor
{
    private readonly MomentumProcessController _game;
    private readonly string? _watchDirectory;

    public GameSessionHealthMonitor(MomentumProcessController game, string? watchDirectory)
    {
        _game = game;
        _watchDirectory = watchDirectory;
    }

    public Task GameExitedTask => _game.ExitTask;
    public bool IsGameRunning => _game.IsGameRunning;

    public DiskHealthSnapshot GetWatchDiskHealth(int safetyPercent)
    {
        var sampledAt = DateTimeOffset.UtcNow;

        // 0% means protection off: keep the Disabled semantics even when the
        // path is invalid, and avoid a needless DriveInfo read entirely.
        if (DiskSafetyPolicy.NormalizeSafetyPercent(safetyPercent) == 0)
            return DiskSafetyPolicy.EvaluateSnapshot("", 0, 0, 0, sampledAt);

        var root = ResolveWatchDriveRoot();
        if (root is null)
            return UnavailableSnapshot(root, 0, 0, safetyPercent, sampledAt);

        try
        {
            var drive = new DriveInfo(root);
            return DiskSafetyPolicy.EvaluateSnapshot(
                root, drive.TotalSize, drive.AvailableFreeSpace, safetyPercent, sampledAt);
        }
        catch
        {
            return UnavailableSnapshot(root, 0, 0, safetyPercent, sampledAt);
        }
    }

    /// <summary>Absolute watch-path root drive, or null when unresolvable.</summary>
    private string? ResolveWatchDriveRoot()
    {
        if (string.IsNullOrWhiteSpace(_watchDirectory))
            return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_watchDirectory));
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch
        {
            return null;
        }
    }

    private static DiskHealthSnapshot UnavailableSnapshot(
        string? driveRoot, long totalBytes, long freeBytes, int safetyPercent, DateTimeOffset sampledAt) =>
        DiskSafetyPolicy.EvaluateSnapshot(driveRoot ?? string.Empty, totalBytes, freeBytes, safetyPercent, sampledAt);
}
