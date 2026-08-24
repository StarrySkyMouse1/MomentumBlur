namespace Mmod.Core.Services;

/// <summary>
/// Generates the DaVinci Resolve Studio 4K AI post-processing guide text.
/// The tool never performs AI upscaling; this service only produces
/// copyable instructions for the manual DaVinci step.
/// </summary>
public static class DaVinciWorkflowGuideService
{
    public static string BuildGuideText(int sourceFps = 60, int supersamplingMultiplier = 10)
    {
        var fps = sourceFps > 0 ? sourceFps : 60;
        return $"""
        DaVinci 4K AI 后处理指引（1080p60 中间母版 → Super Scale 2x → 4K60）

        本工具不会执行 AI 放大。工具输出 1920×1080 / {fps}fps 高码率中间母版后，请按以下步骤在
        DaVinci Resolve Studio 中处理（Enhanced Super Scale 属于 Studio 功能；没有 Studio 时该指引不保证可用）。

        1. 项目 / Timeline：
           3840 × 2160 Ultra HD（UHD），{fps} fps，Progressive，Square Pixel。

        2. 素材放大：
           选中 1080p 素材 → Inspector / Clip Attributes → Super Scale → 2x Enhanced
           （Resolve 20 的 Enhanced 模式可进一步调节 Sharpness / Noise Reduction）。

        3. Sharpness / Noise Reduction：保守。
           判断标准不是“暂停截图最锐”，而是运动画面中没有 halo / ringing / 虚假 micro-texture。
           如果工具端已启用 Motion-Adaptive Detail / Low-Pass / Deband，DaVinci 侧不要叠加强 NR 或锐化，避免双重平滑。

        4. 不要添加：
           Film Grain、强 Sharpen、强 Texture / Clarity / Midtone Detail。
           这些会重新制造大量 micro detail，抵消工具端为低码率压缩做的优化。

        5. 检查重点：
           斜坡边缘、HUD 文字与数字、远处细线，确认 AI 放大没有产生 halo / ringing。

        6. 导出（高质量上传源，不是平台最终播放码率）：
           分辨率 3840×2160，{fps} fps，Progressive，Square Pixel。
           色彩默认 SDR：Rec.709 / Gamma 2.4 / Data Levels Auto（除非项目实际为 HDR）。
           H.264 约 60~100 Mbps 起步；H.265 约 40~80 Mbps 起步。最终以实际 AB 测试为准。
           Bilibili 会再次转码，上传源不要提前压到平台档位的码率。

        7. 音频（如需要）：48 kHz AAC，较高码率（例如 320 kbps）。
           MomentumBlur 本身仍只负责画面。
        """.Trim();
    }

    public static string BuildBilibiliExportSuggestions()
    {
        return """
        Bilibili 上传导出建议（低码率二压优化）

        1. 上传源使用高质量 4K60 母版，不要提前压到平台播放档码率。
           H.264 约 60~100 Mbps 起步；H.265 约 40~80 Mbps 起步（以实际 AB 测试为准）。
        2. 编码器偏好：H.265/HEVC（若平台接受）或高码率 H.264；10-bit 可选但不是必须。
        3. 关键帧间隔建议设小（如 1~2 秒），方便平台二压与拖动。
        4. 不要添加 Film Grain / 随机噪点 / 重抖动 —— 它们会直接消耗 Bilibili 低码率档的码率。
        5. 平台会生成 1080p60 / 1080p30 等播放档；请在这些档位（而不是本地 4K 暂停截图）上检查
           高速运动观感、HUD 清晰度、斜坡边缘与远处纹理。
        6. 音频：48 kHz AAC，约 320 kbps。
        """.Trim();
    }
}
