namespace Mmod.Core.Native;

public static class NativeMp4Concatenator
{
    public static void Concatenate(IReadOnlyList<string> inputPaths, string outputPath)
    {
        if (inputPaths.Count == 0) throw new ArgumentException("没有可合并的片段。", nameof(inputPaths));
        foreach (var path in inputPaths) if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new FileNotFoundException("阶段片段不存在或为空。", path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var code = MmodNativeInterop.mmod_concat_video_files(string.Join('|', inputPaths), outputPath, out var error);
        if (code != 0) throw new InvalidOperationException("Media Foundation 无损合并失败：" + MmodNativeInterop.GetErrorString(error));
    }
}
