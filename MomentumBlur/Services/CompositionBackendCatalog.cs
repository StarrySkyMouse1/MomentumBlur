using mmod_record.Models;

namespace mmod_record.Services;

public static class CompositionBackendCatalog
{
    public const string GpuResidentOpenClId = "gpu-resident-opencl";
    public const string GpuId = "gpu";
    public const string FfmpegId = "ffmpeg";

    public static IReadOnlyList<VideoEncoderOption> Options { get; } =
    [
        new()
        {
            Id = GpuResidentOpenClId,
            DisplayName = "GPU Resident - OpenCL/NVENC",
            Hint = "极速路线。通过 FFmpeg OpenCL 在显存内直接进行多帧混合，推荐搭配 NVENC 编码器；对显存和驱动要求较高，若遇到画面异常，请切回 DirectX 12 路线。",
        },
        new()
        {
            Id = GpuId,
            DisplayName = "GPU 混合 - DirectX 12 (ComputeSharp)",
            Hint = "慢速的",
        },
        new()
        {
            Id = FfmpegId,
            DisplayName = "FFmpeg tmix - CPU",
            Hint = "备用路线。完全依赖 CPU 进行 FFmpeg tmix 帧混合，运行最稳定但速度最慢；仅推荐在 GPU 后端不可用时作为兜底使用。",
        }
    ];
    public static string Normalize(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
            return GpuResidentOpenClId;

        var value = backend.Trim().ToLowerInvariant();
        return value switch
        {
            GpuResidentOpenClId => GpuResidentOpenClId,
            FfmpegId => FfmpegId,
            GpuId => GpuId,
            _ => GpuResidentOpenClId,
        };
    }

    public static string GetHint(string? backend)
    {
        var id = Normalize(backend);
        return Options.FirstOrDefault(o => o.Id == id)?.Hint ?? Options[0].Hint;
    }
}
