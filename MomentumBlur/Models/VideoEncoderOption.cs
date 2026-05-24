namespace mmod_record.Models;

/// <summary>设置页视频编码器下拉项。</summary>
public sealed class VideoEncoderOption
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Hint { get; init; }
}
