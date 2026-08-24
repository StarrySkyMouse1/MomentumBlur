namespace Mmod.Core.Native;

using Mmod.Core.Models;
using Mmod.Core.Services;

/// <summary>
/// Central mapping between catalog module IDs and the native effect descriptor
/// layout (effect type + p0..p7). UI / orchestrators never need to know the
/// native parameter layout; only this mapper and the C++ effect implementations
/// must stay in sync.
/// </summary>
public static class NativeProcessingMapper
{
    /// <summary>Stable native effect type ids. Must match MmodEffectType in mmod_native.h.</summary>
    public enum NativeEffectType
    {
        MotionAdaptiveDetail = 1,
        MicroDetailLowPass = 2,
        DebandNoDither = 3,
        TemporalShimmerReduction = 4,
    }

    public sealed record NativeEffectDesc(
        int EffectType,
        int Order,
        float P0, float P1, float P2, float P3,
        float P4, float P5, float P6, float P7);

    public static IReadOnlyList<NativeEffectDesc> Map(VideoProcessingSettings? settings)
    {
        if (settings is null || settings.Modules is null)
            return [];

        var result = new List<NativeEffectDesc>();
        foreach (var config in settings.Modules)
        {
            if (!config.Enabled)
                continue;

            var type = ToNativeEffectType(config.Id);
            if (type is null)
            {
                // Unknown module: safe ignore; the native side also ignores it.
                continue;
            }

            var def = VideoProcessorCatalog.GetDefinition(config.Id);
            var p = new float[8];
            for (var i = 0; i < def.Parameters.Count && i < 8; i++)
            {
                var parameter = def.Parameters[i];
                p[i] = config.Parameters.TryGetValue(parameter.Key, out var value)
                    ? (float)Math.Clamp(value, parameter.Min, parameter.Max)
                    : (float)parameter.DefaultValue;
            }

            result.Add(new NativeEffectDesc(
                (int)type.Value,
                config.Order,
                p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]));
        }

        return result;
    }

    /// <summary>
    /// Stable p0..p7 parameter mapping per effect, in definition order.
    /// Order must match the native effect implementations.
    /// </summary>
    public static IReadOnlyList<string> ParameterOrder(string moduleId) =>
        VideoProcessorCatalog.GetDefinition(moduleId).Parameters
            .Select(p => p.Key)
            .ToList();

    public static string EffectTypeToModuleId(int nativeEffectType) => nativeEffectType switch
    {
        (int)NativeEffectType.MotionAdaptiveDetail => VideoProcessorCatalog.MotionAdaptiveDetail,
        (int)NativeEffectType.MicroDetailLowPass => VideoProcessorCatalog.MicroDetailLowPass,
        (int)NativeEffectType.DebandNoDither => VideoProcessorCatalog.DebandNoDither,
        (int)NativeEffectType.TemporalShimmerReduction => VideoProcessorCatalog.TemporalShimmerReduction,
        _ => string.Empty,
    };

    private static NativeEffectType? ToNativeEffectType(string moduleId) => moduleId switch
    {
        VideoProcessorCatalog.MotionAdaptiveDetail => NativeEffectType.MotionAdaptiveDetail,
        VideoProcessorCatalog.MicroDetailLowPass => NativeEffectType.MicroDetailLowPass,
        VideoProcessorCatalog.DebandNoDither => NativeEffectType.DebandNoDither,
        VideoProcessorCatalog.TemporalShimmerReduction => NativeEffectType.TemporalShimmerReduction,
        _ => null,
    };
}
