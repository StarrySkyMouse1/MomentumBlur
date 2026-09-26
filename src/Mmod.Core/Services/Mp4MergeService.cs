using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

public static class Mp4MergeService
{
    public static void MergeAtomically(IReadOnlyList<string> clips, string outputPath)
    {
        if (clips.Count < 2)
            throw new ArgumentException("多阶段合并至少需要两个片段。", nameof(clips));

        var inputProbes = clips.Select(path =>
        {
            Validate(path, out var probe);
            return probe;
        }).ToArray();
        var expectedFrames = inputProbes.Sum(x => x.FrameCount);
        var expectedDuration = inputProbes.Sum(x => x.DurationSeconds);
        var fullOutput = Path.GetFullPath(outputPath);
        var temp = Path.Combine(Path.GetDirectoryName(fullOutput)!, Path.GetFileNameWithoutExtension(fullOutput) + ".merging.mp4");
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            NativeMp4Concatenator.Concatenate(clips, temp);
            ValidateMergedOutput(temp, expectedFrames, expectedDuration);
            File.Move(temp, fullOutput, true);
            ValidateMergedOutput(fullOutput, expectedFrames, expectedDuration);
        }
        catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
    }

    /// <summary>
    /// Media-level validation (container, stream, resolution, fps, duration,
    /// frame count). A file with ftyp/moov but broken duration no longer passes.
    /// </summary>
    public static void Validate(string path) =>
        Validate(path, out _);

    public static void Validate(string path, out MediaProbeResult probe)
    {
        probe = new MediaProbe().Probe(path);
        if (!probe.IsValid)
            throw new InvalidDataException($"MP4 校验失败：{probe.Error}");
    }

    private static void ValidateMergedOutput(string path, long expectedFrames, double expectedDuration)
    {
        Validate(path, out var probe);
        if (probe.FrameCount != expectedFrames)
        {
            throw new InvalidDataException(
                $"多阶段合并帧数不完整：输出 {probe.FrameCount} 帧，阶段合计 {expectedFrames} 帧。");
        }

        var durationTolerance = Math.Max(0.1, 1.0 / Math.Max(1.0, probe.Fps));
        if (Math.Abs(probe.DurationSeconds - expectedDuration) > durationTolerance)
        {
            throw new InvalidDataException(
                $"多阶段合并时长不完整：输出 {probe.DurationSeconds:0.###}s，" +
                $"阶段合计 {expectedDuration:0.###}s。");
        }
    }
}
