using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

public static class Mp4MergeService
{
    public static void MergeAtomically(IReadOnlyList<string> clips, string outputPath)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var temp = Path.Combine(Path.GetDirectoryName(fullOutput)!, Path.GetFileNameWithoutExtension(fullOutput) + ".merging.mp4");
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            NativeMp4Concatenator.Concatenate(clips, temp);
            Validate(temp);
            File.Move(temp, fullOutput, true);
            Validate(fullOutput);
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
}
