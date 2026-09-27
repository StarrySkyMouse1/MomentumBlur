namespace Mmod.App.ViewModels;

public sealed class QualityPreviewArtifactItem
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string CreatedText { get; init; }
    public required string ParametersText { get; init; }
    public string SourceText { get; init; } = string.Empty;
}
