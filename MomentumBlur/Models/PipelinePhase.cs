namespace mmod_record.Models;

public enum PipelinePhase
{
    Idle,
    ValidatingInput,
    Synthesizing,
    MuxingAudio,
    Done,
    Faulted,
}
