using System.Text;
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

    public static void Validate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1024) throw new InvalidDataException("MP4 文件不存在或过小。");
        using var stream = File.OpenRead(path);
        var probeSize = (int)Math.Min(1024 * 1024, info.Length);
        var head = new byte[probeSize];
        stream.ReadExactly(head);
        stream.Position = Math.Max(0, info.Length - probeSize);
        var tail = new byte[probeSize];
        stream.ReadExactly(tail);
        var headText = Encoding.ASCII.GetString(head);
        var tailText = Encoding.ASCII.GetString(tail);
        if (!headText.Contains("ftyp", StringComparison.Ordinal) || (!headText.Contains("moov", StringComparison.Ordinal) && !tailText.Contains("moov", StringComparison.Ordinal)))
            throw new InvalidDataException("MP4 容器校验失败（缺少 ftyp/moov）。");
    }
}
