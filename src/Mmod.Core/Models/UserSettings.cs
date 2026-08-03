namespace Mmod.Core.Models;

public sealed class UserSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Obs;
    public string VideoOutputDirectory { get; set; } = string.Empty;
    public string RamDiskWatchDirectory { get; set; } = string.Empty;
    public string? RamDiskDriveLetter { get; set; } = "R:\\";
    public string? StartmoviePathPrefix { get; set; }
    public string? GameRootPath { get; set; }
    public string? CfgDirectory { get; set; }

    public int SupersamplingMultiplier { get; set; } = 10;
    public double Exposure { get; set; } = 0.5;
    public int ObsCaptureFramerate { get; set; } = 120;
    public string MovieSequenceName { get; set; } = "frame";
    public string StartMovieHotkey { get; set; } = "[";
    public string EndMovieHotkey { get; set; } = "]";
    public bool HideHudInCfg { get; set; } = false;

    public int MaxParallelJobs { get; set; } = 2;
    public int PendingTgaWarningCount { get; set; } = 30;
}
