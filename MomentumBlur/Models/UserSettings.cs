namespace mmod_record.Models;

public sealed class UserSettings
{
    public string? FfmpegPath { get; set; }

    /// <summary>源视频帧率（混合参数），仅支持 60 或 120。</summary>
    public int ObsCaptureFramerate { get; set; } = 120;

    public int SupersamplingMultiplier { get; set; } = 2;

    public string? VideoOutputDirectory { get; set; }

    public string VideoEncoder { get; set; } = "libx264";

    /// <summary>帧混合后端；gpu 或 ffmpeg（tmix）。</summary>
    public string CompositionBackend { get; set; } = "gpu-resident-opencl";

    public int Crf { get; set; } = 18;

    public double Exposure { get; set; } = 0.5;

    /// <summary>批量合成时允许同时进行的 GPU 合成路数（1–4）。</summary>
    public int MaxParallelGpuJobs { get; set; } = 2;

    /// <summary>游戏慢放指令是否包含 <c>cl_drawhud</c>（隐藏/恢复 UI）。</summary>
    public bool HideHudInGameCommands { get; set; }
}
