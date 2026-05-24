namespace mmod_record.Models;

public sealed record VideoSynthesisResult(
    bool Success,
    string InputVideoPath,
    string? FinalVideoPath,
    string? ErrorMessage,
    string? ValidationSummary = null);
