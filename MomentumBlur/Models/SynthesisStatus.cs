namespace mmod_record.Models;

public sealed class SynthesisStatus
{
    public PipelinePhase Phase { get; init; } = PipelinePhase.Idle;

    public int EncodedFrameCount { get; init; }

    public int SynthesisTotalFrames { get; init; }

    public double SynthesisProgressPercent =>
        SynthesisTotalFrames <= 0
            ? 0
            : Math.Clamp(EncodedFrameCount * 100.0 / SynthesisTotalFrames, 0, 100);

    public string? InputVideoPath { get; init; }

    public string? Message { get; init; }

    public string? LastError { get; init; }

    public string? TempVideoPath { get; init; }

    public string? FinalVideoPath { get; init; }

    public int BatchIndex { get; init; }

    public int BatchTotal { get; init; }
}
