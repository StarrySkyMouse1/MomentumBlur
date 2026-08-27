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

    // ---- Disk safety (percentage contract, S1) ----

    /// <summary>
    /// Watch-drive free-space safety floor as a percentage of total capacity.
    /// 0 = disk-space protection off; 1..50 = safety line. Missing in old
    /// settings.json defaults to 10. Frozen into RenderSettingsSnapshot at task
    /// creation / snapshot refresh.
    /// </summary>
    public int DiskSafetyFreePercent { get; set; } = 10;

    // ---- Quality pipeline (new in the Bilibili-quality plan) ----

    /// <summary>
    /// Complete quality-processing config. Null / missing in old settings.json
    /// means all new modules off and legacy motion-blur behaviour.
    /// </summary>
    public VideoProcessingSettings? VideoProcessing { get; set; }

    /// <summary>LegacyGaussianExposure keeps old Exposure semantics; ShutterAngle is the new recommended mode.</summary>
    public MotionBlurWeightMode MotionBlurWeightMode { get; set; } = MotionBlurWeightMode.LegacyGaussianExposure;

    /// <summary>Used only when MotionBlurWeightMode == ShutterAngle (180°~360°).</summary>
    public double ShutterAngle { get; set; } = 270;

    /// <summary>
    /// Intermediate-master target bitrate for the Media Foundation H.264 writer.
    /// 0 = auto (old width*height*fps estimate capped at 120 Mbps).
    /// </summary>
    public int IntermediateTargetBitrate { get; set; }

    /// <summary>Show the DaVinci 4K AI post-processing guide section in Settings UI.</summary>
    public bool EnableDaVinci4KWorkflowGuide { get; set; }
}
