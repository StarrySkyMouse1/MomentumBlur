namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>Typed failure with a stable classification (plan P1-07).</summary>
public class RecordingStageException : Exception
{
    public RecordingStageException(RecordingFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        FailureKind = kind;
    }

    public RecordingFailureKind FailureKind { get; }
}

/// <summary>endmovie could not be positively confirmed.</summary>
public sealed class CaptureStopUnconfirmedException : RecordingStageException
{
    public CaptureStopUnconfirmedException(string message, Exception? inner = null)
        : base(RecordingFailureKind.CaptureStopUnconfirmed, message, inner) { }
}

/// <summary>The owned game process exited during capture.</summary>
public sealed class GameExitedException : RecordingStageException
{
    public GameExitedException(string message) : base(RecordingFailureKind.GameExited, message) { }
}

/// <summary>Watch drive free space fell below the percentage safety floor.</summary>
public sealed class DiskPressureException : RecordingStageException
{
    public DiskPressureException(string message, DiskHealthSnapshot? snapshot = null, ControlledStopResult? controlledStop = null)
        : base(RecordingFailureKind.DiskPressure, message)
    {
        Snapshot = snapshot;
        ControlledStop = controlledStop;
    }

    public DiskHealthSnapshot? Snapshot { get; }

    /// <summary>
    /// Non-null only when the controlled stop sequence (strict endmovie →
    /// quiescence → freeze/drain → Native Finish) proved successful. The
    /// caller then runs media validation + atomic commit + persistence; a
    /// null value means no partial may be recorded.
    /// </summary>
    public ControlledStopResult? ControlledStop { get; }
}

/// <summary>
/// Watch-drive health could not be sampled (consecutive Unavailable samples).
/// Distinct from DiskPressure: diagnostics and recovery semantics differ.
/// </summary>
public sealed class DiskHealthUnavailableException : RecordingStageException
{
    public DiskHealthUnavailableException(string message, DiskHealthSnapshot? snapshot = null)
        : base(RecordingFailureKind.DiskHealthUnavailable, message)
    {
        Snapshot = snapshot;
    }

    public DiskHealthSnapshot? Snapshot { get; }
}

/// <summary>Maps exceptions to stable failure kinds for the retry policy.</summary>
public static class RecordingFailureClassifier
{
    public static RecordingFailureKind Classify(Exception ex)
    {
        if (ex is OperationCanceledException) return RecordingFailureKind.UserCanceled;
        if (ex is RecordingStageException stage) return stage.FailureKind;
        if (ex is PipelineFaultException) return RecordingFailureKind.PipelineFault;
        if (ex is TimeoutException) return RecordingFailureKind.TgaWriteStalled;
        if (ex is IOException) return RecordingFailureKind.NetConLost;
        return RecordingFailureKind.Unknown;
    }
}
