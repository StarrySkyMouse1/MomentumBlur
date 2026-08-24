namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Single source of truth for quality-processing module definitions, default
/// parameters, ranges, stage assignment and settings normalization.
/// UI, snapshot migration and the native mapper all read from here so a new
/// module is added in exactly one place.
/// </summary>
public static class VideoProcessorCatalog
{
    public const string MotionAdaptiveDetail = "motion-adaptive-detail";
    public const string MicroDetailLowPass = "micro-detail-lowpass";
    public const string DebandNoDither = "deband-no-dither";
    public const string TemporalShimmerReduction = "temporal-shimmer-reduction";

    public static IReadOnlyList<VideoProcessingModuleDefinition> Modules { get; } =
    [
        new VideoProcessingModuleDefinition(
            Id: MotionAdaptiveDetail,
            DisplayName: "Motion-Adaptive Detail Reduction",
            Description: "高速运动区域轻微减少难以压缩的细碎纹理，静态区域与主要边缘尽量保持清晰。对 Surf 最推荐。",
            RiskDescription: "运动掩码误判或 Strength 过高时，运动中的 HUD / 主要轮廓可能轻微发软。",
            Stage: VideoProcessingStage.MotionAwareSpatial,
            DefaultOrder: 0,
            Parameters:
            [
                new VideoProcessingParameterDefinition(
                    Key: "strength",
                    DisplayName: "Strength",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.35,
                    Min: 0.0,
                    Max: 1.0,
                    Step: 0.05,
                    Description: "运动区域向轻模糊混合的强度。0 = 关闭该效果。"),
                new VideoProcessingParameterDefinition(
                    Key: "motion-threshold",
                    DisplayName: "Motion Threshold",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.08,
                    Min: 0.0,
                    Max: 0.5,
                    Step: 0.01,
                    Description: "归一化 luma 帧差阈值；低于它的区域视为静态。"),
                new VideoProcessingParameterDefinition(
                    Key: "edge-protection",
                    DisplayName: "Edge Protection",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.6,
                    Min: 0.0,
                    Max: 1.0,
                    Step: 0.05,
                    Description: "强边缘的保留程度；越高边缘越不容易被抹掉。"),
            ]),
        new VideoProcessingModuleDefinition(
            Id: MicroDetailLowPass,
            DisplayName: "Micro Detail Low-Pass",
            Description: "非常轻微地抑制超高频纹理与亚像素细节，可减少低码率二压时的闪烁、ringing 和宏块。",
            RiskDescription: "强度过高会让画面明显发软；用于压缩优化时应保持非常轻。",
            Stage: VideoProcessingStage.GlobalSpatial,
            DefaultOrder: 1,
            Parameters:
            [
                new VideoProcessingParameterDefinition(
                    Key: "strength",
                    DisplayName: "Strength",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.25,
                    Min: 0.0,
                    Max: 1.0,
                    Step: 0.05,
                    Description: "向低通结果混合的强度。0 = 关闭该效果。"),
                new VideoProcessingParameterDefinition(
                    Key: "radius",
                    DisplayName: "Radius",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 1.0,
                    Min: 1.0,
                    Max: 2.0,
                    Step: 1.0,
                    Unit: "px",
                    Description: "低通半径，V1 限制为 1~2 像素，不允许破坏性的大半径。"),
            ]),
        new VideoProcessingModuleDefinition(
            Id: DebandNoDither,
            DisplayName: "Deband (No Dither)",
            Description: "平滑大面积渐变中的细小色阶断层，不添加随机抖动/胶片颗粒，以避免增加编码压力。",
            RiskDescription: "Threshold 过高会在强边缘附近产生轻微糊化；本模块绝不添加噪点。",
            Stage: VideoProcessingStage.GlobalSpatial,
            DefaultOrder: 2,
            Parameters:
            [
                new VideoProcessingParameterDefinition(
                    Key: "strength",
                    DisplayName: "Strength",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.3,
                    Min: 0.0,
                    Max: 1.0,
                    Step: 0.05,
                    Description: "渐变区域向均值平滑的强度。0 = 关闭该效果。"),
                new VideoProcessingParameterDefinition(
                    Key: "threshold",
                    DisplayName: "Threshold",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.06,
                    Min: 0.0,
                    Max: 0.25,
                    Step: 0.01,
                    Description: "判定“平滑渐变区域”的归一化局部差异阈值。"),
            ]),
        new VideoProcessingModuleDefinition(
            Id: TemporalShimmerReduction,
            DisplayName: "Temporal Shimmer Reduction（实验性）",
            Description: "抑制远处细线和高频纹理的帧间闪烁。只对“细节较多且时间差很小”的像素做少量稳定。",
            RiskDescription: "实验性：Strength 过高在高速运动中可能产生拖影，建议保持很低或关闭。",
            Stage: VideoProcessingStage.Temporal,
            DefaultOrder: 0,
            Parameters:
            [
                new VideoProcessingParameterDefinition(
                    Key: "strength",
                    DisplayName: "Strength",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.15,
                    Min: 0.0,
                    Max: 0.5,
                    Step: 0.05,
                    Description: "向上一帧已处理结果稳定混合的强度。0 = 关闭该效果。"),
                new VideoProcessingParameterDefinition(
                    Key: "temporal-threshold",
                    DisplayName: "Temporal Threshold",
                    Kind: VideoProcessingParameterKind.Number,
                    DefaultValue: 0.05,
                    Min: 0.0,
                    Max: 0.3,
                    Step: 0.01,
                    Description: "归一化时间差阈值；超过它的像素视为大运动直接旁路。"),
            ]),
    ];

    public static IReadOnlyList<VideoProcessingPresetDefinition> Presets { get; } =
    [
        new VideoProcessingPresetDefinition(
            VideoProcessingPresetIds.Off,
            "关闭 / 原始",
            "所有新增质量模块关闭，保持旧版输出行为。"),
        new VideoProcessingPresetDefinition(
            VideoProcessingPresetIds.BilibiliLowBitrate,
            "B站低码率推荐",
            "开启 Motion-Adaptive Detail Reduction 与轻量 Micro Detail Low-Pass；Deband 关闭；Temporal Shimmer 关闭。"),
        new VideoProcessingPresetDefinition(
            VideoProcessingPresetIds.Custom,
            "自定义",
            "手动勾选模块或修改参数后自动进入此状态。"),
    ];

    public static VideoProcessingModuleDefinition GetDefinition(string id)
    {
        foreach (var def in Modules)
        {
            if (string.Equals(def.Id, id, StringComparison.Ordinal))
                return def;
        }
        throw new KeyNotFoundException($"未知画质处理模块：{id}");
    }

    public static bool IsKnown(string id) =>
        Modules.Any(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Normalize a persisted config so it is safe to run:
    /// missing known module → appended disabled; missing parameter → default;
    /// out-of-range → clamped; unknown module → kept but ignored by the mapper.
    /// </summary>
    public static VideoProcessingSettings Normalize(VideoProcessingSettings? settings)
    {
        var result = settings is null ? VideoProcessingSettings.CreateEmpty() : settings.Clone();

        if (result.Modules is null)
            result.Modules = new List<VideoProcessingModuleConfig>();

        // Ensure every known module exists.
        foreach (var def in Modules)
        {
            var config = result.Modules.FirstOrDefault(m => string.Equals(m.Id, def.Id, StringComparison.Ordinal));
            if (config is null)
            {
                result.Modules.Add(BuildDefaultConfig(def));
            }
            else
            {
                NormalizeConfig(config, def);
            }
        }

        // Normalize preset id: only known presets survive; anything else = custom.
        if (!Presets.Any(p => string.Equals(p.Id, result.PresetId, StringComparison.Ordinal)))
            result.PresetId = VideoProcessingPresetIds.Custom;

        // Stable ordering: known modules first sorted by stage/order, unknown modules kept at the end.
        result.Modules = result.Modules
            .OrderBy(m => IsKnown(m.Id) ? StageRank(GetDefinition(m.Id).Stage) : 999)
            .ThenBy(m => m.Order)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        return result;
    }

    public static VideoProcessingModuleConfig BuildDefaultConfig(VideoProcessingModuleDefinition def)
    {
        var config = new VideoProcessingModuleConfig
        {
            Id = def.Id,
            Enabled = false,
            Order = def.DefaultOrder,
            Parameters = new Dictionary<string, double>(),
        };
        foreach (var p in def.Parameters)
            config.Parameters[p.Key] = p.DefaultValue;
        return config;
    }

    private static void NormalizeConfig(VideoProcessingModuleConfig config, VideoProcessingModuleDefinition def)
    {
        config.Parameters ??= new Dictionary<string, double>();
        foreach (var p in def.Parameters)
        {
            if (!config.Parameters.TryGetValue(p.Key, out var value) || double.IsNaN(value))
            {
                config.Parameters[p.Key] = p.DefaultValue;
            }
            else
            {
                config.Parameters[p.Key] = Math.Clamp(value, p.Min, p.Max);
            }
        }
    }

    private static int StageRank(VideoProcessingStage stage) => (int)stage;

    /// <summary>True when the normalized settings have no enabled module.</summary>
    public static bool HasNoEnabledModules(VideoProcessingSettings? settings) =>
        settings is null || settings.Modules.All(m => !m.Enabled);
}
