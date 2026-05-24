namespace mmod_record.Services;

/// <summary>
/// Builds Momentum / Source console commands for OBS recording. OBS captures in
/// realtime, so supersampling above the capture baseline is achieved with game
/// slow motion instead of forcing host_framerate.
/// </summary>
public static class GameSlowMotionCommandBuilder
{
    public const string EnableCheatsCommand = "sv_cheats 1";

    public const string RestoreTimescaleCommand = "host_timescale 1";

    public const string RestoreFramerateCommand = "host_framerate 0";

    public const string HideHudCommand = "cl_drawhud 0";

    public const string ShowHudCommand = "cl_drawhud 1";

    public static double ResolveSlowMotionScale(int sourceCaptureFramerate, int supersamplingMultiplier)
    {
        var fps = SynthesisTiming.ResolveObsFramerateForGate(sourceCaptureFramerate);
        return SynthesisTiming.GetPlaybackSpeedScale(supersamplingMultiplier, fps);
    }

    public static string BuildSlowMotionCommand(int sourceCaptureFramerate, int supersamplingMultiplier) =>
        FormatCommand(
            $"host_timescale {SynthesisTiming.FormatMultiplier(ResolveSlowMotionScale(sourceCaptureFramerate, supersamplingMultiplier))}");

    public static string BuildEnableSlowMotionBlock(
        int sourceCaptureFramerate,
        int supersamplingMultiplier,
        bool hideHud = false)
    {
        var lines = new List<string> { EnableCheatsCommand };
        if (hideHud)
            lines.Add(HideHudCommand);

        lines.Add(RestoreFramerateCommand);
        lines.Add(
            $"host_timescale {SynthesisTiming.FormatMultiplier(ResolveSlowMotionScale(sourceCaptureFramerate, supersamplingMultiplier))}");
        return JoinCommands(lines);
    }

    public static string BuildRestoreBlock(bool hideHud = false)
    {
        var lines = new List<string>
        {
            RestoreTimescaleCommand,
            RestoreFramerateCommand,
        };

        if (hideHud)
            lines.Add(ShowHudCommand);

        return JoinCommands(lines);
    }

    private static string FormatCommand(string command) =>
        command.EndsWith(';') ? command : $"{command};";

    private static string JoinCommands(IEnumerable<string> commands) =>
        string.Join(Environment.NewLine, commands.Select(FormatCommand));
}
