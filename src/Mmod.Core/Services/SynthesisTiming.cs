using System.Globalization;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public static class SynthesisTiming
{
    public static int GetRealtimeBaselineN(int obsCaptureFramerate) =>
        Math.Max(1, ResolveObsFramerateForGate(obsCaptureFramerate) / ProjectConstants.FinalOutputFramerate);

    public static int ResolveObsFramerateForGate(int obsCaptureFramerate)
    {
        if (obsCaptureFramerate < ProjectConstants.ObsFramerateStep)
            return ProjectConstants.ObsFramerateStep;

        var steps = (int)Math.Round(
            obsCaptureFramerate / (double)ProjectConstants.ObsFramerateStep,
            MidpointRounding.AwayFromZero);
        return Math.Max(ProjectConstants.ObsFramerateStep, steps * ProjectConstants.ObsFramerateStep);
    }

    public static int GetSynthesisBlendFrames(int supersamplingN, int obsCaptureFramerate)
    {
        var n = Math.Clamp(supersamplingN, 1, 120);
        var baseline = GetRealtimeBaselineN(obsCaptureFramerate);
        return Math.Max(n, baseline);
    }

    public static double GetPlaybackSpeedScale(int supersamplingN, int obsCaptureFramerate)
    {
        var baseline = GetRealtimeBaselineN(obsCaptureFramerate);
        var blend = GetSynthesisBlendFrames(supersamplingN, obsCaptureFramerate);
        return Math.Clamp(baseline / (double)blend, 0.000001, 1.0);
    }

    public static string FormatMultiplier(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string BuildTimingHint(int supersamplingN, int obsCaptureFramerate)
    {
        var obs = ProjectConstants.NormalizeObsCaptureFramerate(obsCaptureFramerate);
        var n = Math.Clamp(supersamplingN, 1, 120);
        var blend = GetSynthesisBlendFrames(n, obs);
        var playbackScale = GetPlaybackSpeedScale(n, obs);
        if (playbackScale < 0.999999)
        {
            return
                $"OBS {obs}fps + N={n}: 录制用 host_timescale {FormatMultiplier(playbackScale)}；" +
                $"合成每输出帧混合 {blend} 帧并还原时长。";
        }

        return $"OBS {obs}fps 实时：合成每输出帧混合 {blend} 帧。";
    }
}
