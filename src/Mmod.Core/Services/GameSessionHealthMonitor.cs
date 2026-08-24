using System.IO;

namespace Mmod.Core.Services;

/// <summary>
/// Health monitor over an owned MomentumProcessController plus the capture
/// watch directory. Game exit is surfaced through ExitTask so the recording
/// loop can fail within seconds rather than waiting for a progress timeout.
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

    public long? WatchDriveFreeBytes
    {
        get
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_watchDirectory))
                    return null;
                var root = Path.GetPathRoot(Path.GetFullPath(_watchDirectory));
                if (string.IsNullOrWhiteSpace(root))
                    return null;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }
    }
}
