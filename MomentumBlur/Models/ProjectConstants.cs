namespace mmod_record.Models;

public static class ProjectConstants
{
    public const string ApplicationDisplayName = "mmod运动模糊合成";

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

