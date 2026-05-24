namespace mmod_record.Services;

public static class FfmpegThreadingOptions
{
    public static int LogicalProcessorCount => Math.Max(1, Environment.ProcessorCount);

    public static int DecodeThreads => Math.Clamp(LogicalProcessorCount / 2, 1, 8);

    public static int SoftwareEncodeThreads => Math.Clamp(LogicalProcessorCount - 2, 2, 16);

    public static int HardwareEncodeThreads => 1;

    public static int StatefulFilterThreads => 1;

    public static int StatelessFilterThreads => Math.Clamp(LogicalProcessorCount / 2, 1, 8);

    public static bool IsHardwareEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder))
            return false;

        return encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
               encoder.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
               encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase);
    }

    public static int EncodeThreadsFor(string? encoder) =>
        IsHardwareEncoder(encoder) ? HardwareEncodeThreads : SoftwareEncodeThreads;

    public static string DecodeArgs() => $"-threads {DecodeThreads}";

    public static string EncoderArgs(string? encoder) => $"-threads {EncodeThreadsFor(encoder)}";

    public static string FilterArgs(bool stateful) =>
        $"-filter_threads {(stateful ? StatefulFilterThreads : StatelessFilterThreads)}";

    public static string Describe()
    {
        return
            $"decode_threads={DecodeThreads}, " +
            $"software_encode_threads={SoftwareEncodeThreads}, " +
            $"hardware_encode_threads={HardwareEncodeThreads}, " +
            $"stateful_filter_threads={StatefulFilterThreads}, " +
            $"stateless_filter_threads={StatelessFilterThreads}";
    }
}
