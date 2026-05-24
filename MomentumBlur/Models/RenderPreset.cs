namespace mmod_record.Models;

public sealed record RenderPreset(
    string Id,
    int ObsCaptureFramerate,
    int BlendFrames)
{
    /// <summary>游戏慢放 <c>host_framerate</c>（N×60）。</summary>
    public int HostFramerate => BlendFrames * ProjectConstants.FinalOutputFramerate;

    public static RenderPreset FromObsCapture(int obsCaptureFramerate, int multiplier)
    {
        var fps = ProjectConstants.NormalizeObsCaptureFramerate(obsCaptureFramerate);
        multiplier = Math.Clamp(multiplier, 1, 120);
        return new RenderPreset(
            $"x{multiplier}@{fps}",
            fps,
            multiplier);
    }
}
