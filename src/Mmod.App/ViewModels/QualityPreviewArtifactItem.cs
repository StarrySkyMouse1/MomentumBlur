namespace Mmod.App.ViewModels;

/// <summary>预览影片列表项：只展示可读命名与一行关键信息，长信息进悬浮提示。</summary>
public sealed class QualityPreviewArtifactItem
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string InfoText { get; init; }
    public string ToolTipText { get; init; } = string.Empty;
}
