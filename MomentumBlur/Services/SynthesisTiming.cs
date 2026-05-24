using System.Globalization;
using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>
/// Builds the timing plan for OBS recordings. When N is higher than the realtime
/// capture baseline, the game is expected to be recorded in slow motion and the
/// synthesized output is compressed back to original speed.
/// </summary>
public static class SynthesisTiming
{
    public static int GetRealtimeBaselineN(int obsCaptureFramerate) =>
        Math.Max(1, ResolveObsFramerateForGate(obsCaptureFramerate) / ProjectConstants.FinalOutputFramerate);

    public static int ResolveHostFramerate(int supersamplingN) =>
        Math.Clamp(supersamplingN, 1, 120) * ProjectConstants.FinalOutputFramerate;

    public static int NormalizeObsCaptureFramerate(int fps) =>
        ProjectConstants.NormalizeObsCaptureFramerate(fps);

    /// <summary>Rounds to a multiple of 60 for timing gates, without clamping to the UI max.</summary>
    public static int ResolveObsFramerateForGate(int obsCaptureFramerate)
    {
        if (obsCaptureFramerate < ProjectConstants.ObsFramerateStep)
            return ProjectConstants.ObsFramerateStep;

        var steps = (int)Math.Round(
            obsCaptureFramerate / (double)ProjectConstants.ObsFramerateStep,
            MidpointRounding.AwayFromZero);
        return Math.Max(ProjectConstants.ObsFramerateStep, steps * ProjectConstants.ObsFramerateStep);
    }

    public static int ResolveEffectiveObsCaptureFramerate(VideoProbeResult probe, int settingsObsFps)
    {
        var gateSettings = ResolveObsFramerateForGate(settingsObsFps);
        if (probe.FrameRate is null or <= 0)
            return gateSettings;

        var gateProbe = ResolveObsFramerateForGate((int)Math.Round(probe.FrameRate.Value));
        var normalizedSettings = NormalizeObsCaptureFramerate(settingsObsFps);
        if (!VideoProbeService.IsFrameRateMismatch(probe.FrameRate, normalizedSettings))
            return gateSettings;

        return gateProbe;
    }

    /// <summary>
    /// Number of input frames consumed for each 60fps output frame.
    /// For slow-motion OBS recording, this is the configured N. For realtime
    /// high-fps sources, it is at least the source-fps-to-60fps baseline.
    /// </summary>
    public static int GetSynthesisBlendFrames(int supersamplingN, int obsCaptureFramerate)
    {
        var n = Math.Clamp(supersamplingN, 1, 120);
        var baseline = GetRealtimeBaselineN(obsCaptureFramerate);
        return Math.Max(n, baseline);
    }

    /// <summary>
    /// Output duration as a fraction of the OBS recording duration.
    /// 1 means realtime source; values below 1 restore a slow-motion recording.
    /// </summary>
    public static double GetPlaybackSpeedScale(int supersamplingN, int obsCaptureFramerate)
    {
        var baseline = GetRealtimeBaselineN(obsCaptureFramerate);
        var blend = GetSynthesisBlendFrames(supersamplingN, obsCaptureFramerate);
        return Math.Clamp(baseline / (double)blend, 0.000001, 1.0);
    }

    public static bool UsesFullSupersamplingBlend(int supersamplingN, int obsCaptureFramerate) =>
        GetSynthesisBlendFrames(supersamplingN, obsCaptureFramerate) ==
        Math.Clamp(supersamplingN, 1, 120);

    public static double EstimateSourceDurationSeconds(VideoProbeResult probe)
    {
        if (probe.FrameRate is > 0)
            return probe.FrameCount / probe.FrameRate.Value;

        return probe.FrameCount / (double)ProjectConstants.FinalOutputFramerate;
    }

    public static double EstimateOutputDurationSeconds(
        VideoProbeResult probe,
        int supersamplingN,
        int obsCaptureFramerate)
    {
        var blend = GetSynthesisBlendFrames(supersamplingN, obsCaptureFramerate);
        return probe.FrameCount / (double)blend / ProjectConstants.FinalOutputFramerate;
    }

    public static SynthesisPlan BuildPlan(VideoProbeResult probe, int supersamplingN, int settingsObsFps)
    {
        var obs = ResolveEffectiveObsCaptureFramerate(probe, settingsObsFps);
        var n = Math.Clamp(supersamplingN, 1, 120);
        var blend = GetSynthesisBlendFrames(n, obs);
        var sourceSec = EstimateSourceDurationSeconds(probe);
        var outputSec = EstimateOutputDurationSeconds(probe, n, obs);
        var host = ResolveHostFramerate(n);
        var baseline = GetRealtimeBaselineN(obs);
        var playbackScale = GetPlaybackSpeedScale(n, obs);
        var requiredObs = n * ProjectConstants.FinalOutputFramerate;
        var gateObs = ResolveObsFramerateForGate(obs);
        var mode = playbackScale < 0.999999
            ? $"OBS slow-motion recording: source {gateObs}fps, N={n}, blend {blend}, output duration = recording x{FormatMultiplier(playbackScale)}."
            : $"Realtime recording: source {gateObs}fps, blend {blend}, output 60fps with source duration preserved.";

        return new SynthesisPlan(
            blend,
            outputSec,
            sourceSec,
            UsesFullSupersamplingBlend(n, obs),
            mode,
            host,
            obs,
            baseline,
            n,
            playbackScale,
            requiredObs);
    }

    /// <summary>设置页：说明 host_timescale 与 OBS 源帧率、N 的关系（中文）。</summary>
    public static string BuildHostTimescaleObsHint(int supersamplingN, int obsCaptureFramerate)
    {
        var obs = NormalizeObsCaptureFramerate(obsCaptureFramerate);
        var n = Math.Clamp(supersamplingN, 1, 120);
        var requiredObs = n * ProjectConstants.FinalOutputFramerate;
        var timescale = FormatMultiplier(GetPlaybackSpeedScale(n, obs));
        var blend = GetSynthesisBlendFrames(n, obs);

        if (GetPlaybackSpeedScale(n, obs) < 0.999999)
        {
            return
                $"host_timescale = 源fps÷(N×60)。OBS 按源帧率录，游戏慢放；合成 {blend} 帧/输出后还原原速。当前 {timescale}（{obs}÷{requiredObs}）。";
        }

        return
            $"源fps ≥ N×60（{requiredObs}），host_timescale = 1，无需慢放；合成 {blend} 帧/输出，成片与源等长。";
    }

    public static string BuildTimingHint(int supersamplingN, int obsCaptureFramerate)
    {
        var obs = NormalizeObsCaptureFramerate(obsCaptureFramerate);
        var n = Math.Clamp(supersamplingN, 1, 120);
        var blend = GetSynthesisBlendFrames(n, obs);
        var baseline = GetRealtimeBaselineN(obs);
        var playbackScale = GetPlaybackSpeedScale(n, obs);
        var requiredObs = n * ProjectConstants.FinalOutputFramerate;

        if (playbackScale < 0.999999)
        {
            return
                $"OBS {obs}fps + N={n}: record with host_timescale {FormatMultiplier(playbackScale)}; " +
                $"synthesis blends {blend} input frames per 60fps output frame and restores recording x{FormatMultiplier(playbackScale)} to original speed.";
        }

        return
            $"OBS {obs}fps realtime: baseline N={baseline}, configured N={n}, " +
            $"synthesis blends {blend} input frames per 60fps output frame. Required OBS for full N without slow motion: {requiredObs}fps.";
    }

    public static string BuildVideoFilterSuffix(double playbackSpeedScale = 1.0)
    {
        if (playbackSpeedScale < 0.999999)
            return $",setpts=PTS*{FormatMultiplier(playbackSpeedScale)},fps=60";

        return ",fps=60";
    }

    public static string FormatMultiplier(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Timing plan for one synthesis job.</summary>
public readonly record struct SynthesisPlan(
    int BlendFrames,
    double OutputDurationSeconds,
    double SourceDurationSeconds,
    bool UsesFullSupersamplingBlend,
    string Description,
    int HostFramerate,
    int ObsCaptureFramerate,
    int BaselineN,
    int ConfiguredN,
    double PlaybackSpeedScale,
    int RequiredObsFramerate);
