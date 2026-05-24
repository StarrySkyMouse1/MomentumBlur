using mmod_record.Services;
using Xunit;

namespace MomentumBlur.Tests;

public sealed class SynthesisTimingTests
{
    [Theory]
    [InlineData(20, 120, 20)]
    [InlineData(2, 120, 2)]
    [InlineData(1, 120, 2)]
    [InlineData(10, 600, 10)]
    [InlineData(20, 1200, 20)]
    public void SynthesisBlendFrames_FromConfig(int n, int obsFps, int expectedBlend)
    {
        Assert.Equal(expectedBlend, SynthesisTiming.GetSynthesisBlendFrames(n, obsFps));
    }

    [Theory]
    [InlineData(20, 120, 0.1)]
    [InlineData(2, 120, 1.0)]
    [InlineData(1, 120, 1.0)]
    [InlineData(20, 1200, 1.0)]
    public void PlaybackSpeedScale_FromConfig(int n, int obsFps, double expectedScale)
    {
        Assert.Equal(expectedScale, SynthesisTiming.GetPlaybackSpeedScale(n, obsFps), precision: 6);
    }

    [Fact]
    public void OutputDuration_120fps_N20_RestoresSlowMotionRecording()
    {
        var probe = new VideoProbeResult(1920, 1080, 6879, 120);
        var output = SynthesisTiming.EstimateOutputDurationSeconds(probe, 20, 120);
        Assert.InRange(output, 5.6, 5.8);
    }

    [Fact]
    public void Plan_120_N20_UsesConfiguredNAndCompressesRecording()
    {
        var probe = new VideoProbeResult(1920, 1080, 6879, 120);
        var plan = SynthesisTiming.BuildPlan(probe, 20, 120);
        Assert.Equal(20, plan.BlendFrames);
        Assert.Equal(20, plan.ConfiguredN);
        Assert.True(plan.UsesFullSupersamplingBlend);
        Assert.Equal(0.1, plan.PlaybackSpeedScale, precision: 6);
        Assert.InRange(plan.OutputDurationSeconds, 5.6, 5.8);
    }

    [Fact]
    public void Plan_1200_N20_UsesFullNWithoutPlaybackCompression()
    {
        var probe = new VideoProbeResult(1920, 1080, 72000, 1200);
        var plan = SynthesisTiming.BuildPlan(probe, 20, 1200);
        Assert.Equal(20, plan.BlendFrames);
        Assert.True(plan.UsesFullSupersamplingBlend);
        Assert.Equal(1.0, plan.PlaybackSpeedScale, precision: 6);
    }

    [Fact]
    public void VideoFilterSuffix_AddsSetptsWhenRestoringSlowMotionRecording()
    {
        Assert.Equal(",fps=60", SynthesisTiming.BuildVideoFilterSuffix());
        Assert.Equal(",setpts=PTS*0.1,fps=60", SynthesisTiming.BuildVideoFilterSuffix(0.1));
    }

    [Fact]
    public void GameCommand_UsesHostTimescaleInsteadOfHostFramerate()
    {
        var command = GameSlowMotionCommandBuilder.BuildEnableSlowMotionBlock(120, 20);
        Assert.Contains("host_timescale 0.1;", command);
        Assert.Contains("sv_cheats 1;", command);
        Assert.DoesNotContain("host_framerate 1200", command);
        Assert.DoesNotContain("cl_drawhud", command);
    }

    [Fact]
    public void GameCommand_OptionallyIncludesHudCommandsWithSemicolons()
    {
        var enable = GameSlowMotionCommandBuilder.BuildEnableSlowMotionBlock(120, 20, hideHud: true);
        var restore = GameSlowMotionCommandBuilder.BuildRestoreBlock(hideHud: true);

        Assert.Contains("cl_drawhud 0;", enable);
        Assert.Contains("cl_drawhud 1;", restore);
    }
}
