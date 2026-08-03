using System.IO;
using System.Text;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class CfgGeneratorResult
{
    public required string CfgRootDirectory { get; init; }
    public required string CfgFilePath { get; init; }
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];
    public string InstallHint { get; init; } = string.Empty;
}

public static class CfgGeneratorService
{
    public const string CfgFileName = "mmod_record.cfg";
    public const string GameExecCommand = "exec mmod_record";
    public const string LegacySubfolderName = "mmod_record";

    public static string? ResolveCfgRootFromGameRoot(string gameRoot)
    {
        gameRoot = PathSanitizer.Clean(gameRoot);
        if (string.IsNullOrWhiteSpace(gameRoot))
            return null;

        gameRoot = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(gameRoot))
            return null;

        foreach (var path in new[]
                 {
                     Path.Combine(gameRoot, "momentum", "cfg"),
                     Path.Combine(gameRoot, "hl2", "cfg"),
                     Path.Combine(gameRoot, "cfg"),
                 })
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);
        }

        var fallback = Path.Combine(gameRoot, "momentum", "cfg");
        try
        {
            Directory.CreateDirectory(fallback);
            return Path.GetFullPath(fallback);
        }
        catch
        {
            return null;
        }
    }

    public static CfgGeneratorResult Generate(UserSettings settings, string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(settings.MovieSequenceName))
            throw new InvalidOperationException("请填写 startmovie 序列名。");
        if (string.IsNullOrWhiteSpace(settings.StartMovieHotkey) || string.IsNullOrWhiteSpace(settings.EndMovieHotkey))
            throw new InvalidOperationException("请配置 startmovie / endmovie 快捷键。");

        WatchDirectoryHelper.EnsureDerivedPaths(settings, gameRoot);
        var cfgRoot = ResolveCfgRootFromGameRoot(gameRoot)
            ?? throw new InvalidOperationException("无法解析游戏 cfg 目录。");

        var legacy = Path.Combine(cfgRoot, LegacySubfolderName);
        if (Directory.Exists(legacy))
        {
            try { Directory.Delete(legacy, true); } catch { /* ignore */ }
        }

        var cfgFilePath = Path.Combine(cfgRoot, CfgFileName);
        File.WriteAllText(cfgFilePath, BuildCfgContent(settings), Encoding.UTF8);

        var multiplier = Math.Clamp(settings.SupersamplingMultiplier, 1, 120);
        var hostFr = multiplier * ProjectConstants.FinalOutputFramerate;
        var startMovieCmd = WatchDirectoryHelper.BuildGameStartmovieCommand(settings.MovieSequenceName);
        var hint =
            $"已生成到：{cfgFilePath}\n\n" +
            $"超采样 {multiplier}x（host_framerate {hostFr} → 成片 60fps）\n" +
            $"游戏内：{startMovieCmd}\n" +
            $"进图后控制台执行一次：{GameExecCommand}\n" +
            $"快捷键：{settings.StartMovieHotkey.Trim()} = startmovie，{settings.EndMovieHotkey.Trim()} = 停止录制并还原设置";

        return new CfgGeneratorResult
        {
            CfgRootDirectory = cfgRoot,
            CfgFilePath = cfgFilePath,
            WrittenFiles = [CfgFileName],
            InstallHint = hint,
        };
    }

    public static string BuildCfgContent(UserSettings settings)
    {
        var multiplier = Math.Clamp(settings.SupersamplingMultiplier, 1, 120);
        var hostFr = multiplier * ProjectConstants.FinalOutputFramerate;
        var sequenceName = WatchDirectoryHelper.SanitizeSequenceName(settings.MovieSequenceName);
        var startKey = settings.StartMovieHotkey.Trim();
        var endKey = settings.EndMovieHotkey.Trim();
        var startMovieCmd = WatchDirectoryHelper.BuildGameStartmovieCommand(sequenceName);

        var sb = new StringBuilder();
        sb.AppendLine("// mmod_record_next 自动生成");
        sb.AppendLine($"// 超采样 {multiplier}x → host_framerate {hostFr}");
        sb.AppendLine($"// 进图后：{GameExecCommand}");
        sb.AppendLine();
        sb.AppendLine("sv_cheats 1");
        if (settings.HideHudInCfg)
            sb.AppendLine("cl_drawhud 0");
        sb.AppendLine($"host_framerate {hostFr}");
        sb.AppendLine($"bind {startKey} \"{startMovieCmd}\"");
        var restoreCommands = new List<string>
        {
            "endmovie",
            "host_timescale 1",
            "host_framerate 0",
        };
        if (settings.HideHudInCfg)
            restoreCommands.Add("cl_drawhud 1");
        restoreCommands.Add("sv_cheats 0");
        sb.AppendLine($"bind {endKey} \"{string.Join("; ", restoreCommands)}\"");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
