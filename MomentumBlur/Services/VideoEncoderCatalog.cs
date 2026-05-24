using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>FFmpeg 视频编码器选项（设置页下拉）。</summary>
public static class VideoEncoderCatalog
{
    public static IReadOnlyList<VideoEncoderOption> Options { get; } =
    [
        new()
        {
            Id = "libx264",
            DisplayName = "H.264 - CPU (libx264)",
            Hint = "CPU 软件编码，兼容性完美，同画质下文件体积最小；速度完全取决于 CPU 性能。",
        },
        new()
        {
            Id = "h264_nvenc",
            DisplayName = "H.264 - NVIDIA NVENC",
            Hint = "NVIDIA 显卡硬件编码，速度极快且兼容性好；适合直播或快速导出，推荐搭配 GPU 后端。",
        },
        new()
        {
            Id = "hevc_nvenc",
            DisplayName = "HEVC - NVIDIA NVENC",
            Hint = "NVIDIA 显卡硬件 HEVC(H.265) 编码；相比 NVENC H.264 压缩率更高、体积更小，需播放端支持。",
        },
        new()
        {
            Id = "h264_amf",
            DisplayName = "H.264 - AMD AMF",
            Hint = "AMD 显卡硬件编码，速度快；适合 AMD 显卡用户快速导出，需 FFmpeg 及驱动支持。",
        },
        new()
        {
            Id = "hevc_amf",
            DisplayName = "HEVC - AMD AMF",
            Hint = "AMD 显卡硬件 HEVC(H.265) 编码；相比 AMF H.264 压缩率更高、体积更小，需播放端支持。",
        },
    ];

    private static readonly HashSet<string> KnownIds =
        Options.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? encoder)
    {
        var id = encoder?.Trim();
        if (string.IsNullOrEmpty(id))
            return "libx264";

        if (KnownIds.Contains(id))
            return Options.First(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Id;

        return "libx264";
    }

    public static string GetHint(string? encoder)
    {
        var id = Normalize(encoder);
        return Options.First(o => o.Id == id).Hint;
    }
}
