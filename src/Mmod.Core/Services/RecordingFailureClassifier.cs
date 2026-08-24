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

/// <summary>Watch drive free space fell below the safety floor.</summary>
public sealed class DiskPressureException : RecordingStageException
{
    public DiskPressureException(string message) : base(RecordingFailureKind.DiskPressure, message) { }
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
