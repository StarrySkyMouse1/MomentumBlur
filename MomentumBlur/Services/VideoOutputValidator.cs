namespace mmod_record.Services;

public sealed record VideoOutputValidation(
    bool Success,
    string? Message,
    double? FrameRate,
    double? DurationSeconds,
    bool HasAudio);

/// <summary>
/// 合成完成后用 ffprobe 校验成片。
/// </summary>
public static class VideoOutputValidator
{
    public static async Task<VideoOutputValidation> ValidateAsync(
        string ffmpegPath,
        string outputPath,
        int expectedFps = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = await VideoProbeService.ProbeAsync(ffmpegPath, outputPath, cancellationToken);
            var hasAudio = HasAudioStream(ffmpegPath, outputPath);
            var fpsOk = probe.FrameRate is null or <= 0 ||
                        Math.Abs(probe.FrameRate.Value - expectedFps) <= 3.0;
            var duration = probe.FrameCount > 0 && probe.FrameRate is > 0
                ? probe.FrameCount / probe.FrameRate.Value
                : (double?)null;

            if (!fpsOk)
            {
                return new VideoOutputValidation(
                    false,
                    $"成片帧率 {probe.FrameRate:F2} 与期望 {expectedFps}fps 偏差较大",
                    probe.FrameRate,
                    duration,
                    hasAudio);
            }

            return new VideoOutputValidation(
                true,
                $"校验通过：{probe.Width}x{probe.Height}，约 {probe.FrameRate:F1}fps" +
                (duration is > 0 ? $"，时长 {duration:F1}s" : string.Empty) +
                (hasAudio ? "，含音轨" : "，无音轨"),
                probe.FrameRate,
                duration,
                hasAudio);
        }
        catch (Exception ex)
        {
            return new VideoOutputValidation(false, $"成片校验失败：{ex.Message}", null, null, false);
        }
    }

    private static bool HasAudioStream(string ffmpegPath, string videoPath) =>
        FfmpegEncodingService.HasAudioStream(ffmpegPath, videoPath);
}
