namespace Mmod.Core.Models;

public static class ProjectConstants
{
    public const string ApplicationDisplayName = "Momentum 运动模糊合成";
    public const string SettingsFileName = "settings.json";
    public const string AppDataFolderName = "mmod_record_next";

    public const int FinalOutputFramerate = 60;
    public const int ObsFramerateStep = 60;
    public const int MaxObsCaptureFramerate = 480;

    public static readonly int[] SupportedObsCaptureFramerates =
        Enumerable.Range(1, MaxObsCaptureFramerate / ObsFramerateStep)
            .Select(i => i * ObsFramerateStep)
            .ToArray();

    public static int NormalizeObsCaptureFramerate(int fps)
    {
        if (fps < ObsFramerateStep)
            return ObsFramerateStep;

        var steps = (int)Math.Round(fps / (double)ObsFramerateStep, MidpointRounding.AwayFromZero);
        steps = Math.Clamp(steps, 1, MaxObsCaptureFramerate / ObsFramerateStep);
        return steps * ObsFramerateStep;
    }
}
