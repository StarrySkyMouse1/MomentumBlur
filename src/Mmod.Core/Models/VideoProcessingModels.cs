namespace Mmod.Core.Models;

/// <summary>
/// Motion blur weight window mode. LegacyGaussianExposure preserves the
/// historical Gaussian-sigma behaviour driven by <c>Exposure</c>; ShutterAngle
/// uses a centered box window proportional to a physical shutter angle.
/// </summary>
public enum MotionBlurWeightMode
{
    LegacyGaussianExposure = 0,
    ShutterAngle = 1,
}

public enum VideoProcessingParameterKind
{
    Number = 0,
    Boolean = 1,
    Choice = 2,
}

/// <summary>
/// Logical processing stage. The native pipeline runs stages in this fixed
/// order; <see cref="VideoProcessingModuleConfig.Order"/> only sorts modules
/// within the same stage.
/// </summary>
public enum VideoProcessingStage
{
    /// <summary>After temporal accumulation, before spatial passes.</summary>
    Temporal = 0,
    /// <summary>Motion-aware spatial processors (need motion mask).</summary>
    MotionAwareSpatial = 1,
    /// <summary>Global spatial processors.</summary>
    GlobalSpatial = 2,
}

public sealed record VideoProcessingParameterDefinition(
    string Key,
    string DisplayName,
    VideoProcessingParameterKind Kind,
    double DefaultValue,
    double Min,
    double Max,
    double Step,
    string? Unit = null,
    string? Description = null);

public sealed record VideoProcessingModuleDefinition(
    string Id,
    string DisplayName,
    string Description,
    string RiskDescription,
    VideoProcessingStage Stage,
    int DefaultOrder,
    IReadOnlyList<VideoProcessingParameterDefinition> Parameters);

/// <summary>Per-module persisted configuration. Id refers to the Catalog.</summary>
public sealed class VideoProcessingModuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Order { get; set; }
    public Dictionary<string, double> Parameters { get; set; } = new();
}

/// <summary>
/// Complete quality-processing configuration. Persisted in UserSettings and in
/// every RenderSettingsSnapshot so created tasks are reproducible.
/// </summary>
public sealed class VideoProcessingSettings
{
    public string PresetId { get; set; } = "off";
    public List<VideoProcessingModuleConfig> Modules { get; set; } = new();

    public VideoProcessingSettings Clone()
    {
        var copy = new VideoProcessingSettings
        {
            PresetId = PresetId,
            Modules = new List<VideoProcessingModuleConfig>(Modules.Count),
        };
        foreach (var module in Modules)
        {
            copy.Modules.Add(new VideoProcessingModuleConfig
            {
                Id = module.Id,
                Enabled = module.Enabled,
                Order = module.Order,
                Parameters = new Dictionary<string, double>(module.Parameters),
            });
        }
        return copy;
    }

    public static VideoProcessingSettings CreateEmpty() => new()
    {
        PresetId = VideoProcessingPresetIds.Off,
        Modules = new List<VideoProcessingModuleConfig>(),
    };
}

public static class VideoProcessingPresetIds
{
    public const string Off = "off";
    public const string BilibiliLowBitrate = "bilibili-low-bitrate";
    public const string Custom = "custom";
}

public sealed record VideoProcessingPresetDefinition(
    string Id,
    string DisplayName,
    string Description);
