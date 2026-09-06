using System.IO;
using System.Text;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public static class WatchDirectoryHelper
{
    public static void EnsureDerivedPaths(UserSettings settings, string? gameRoot = null)
    {
        var name = SanitizeSequenceName(settings.MovieSequenceName);
        settings.MovieSequenceName = name;

        var watch = settings.RamDiskWatchDirectory?.Trim();
        if (!string.IsNullOrEmpty(watch))
        {
            var dir = Path.GetFullPath(watch);
            settings.RamDiskWatchDirectory = dir;
            settings.StartmoviePathPrefix = BuildWatchStartmoviePrefix(dir, name, gameRoot);
            var root = Path.GetPathRoot(dir);
            if (!string.IsNullOrEmpty(root))
                settings.RamDiskDriveLetter = root;
            return;
        }

        var drive = NormalizeDriveLetter(settings.RamDiskDriveLetter);
        settings.RamDiskDriveLetter = drive;
        settings.RamDiskWatchDirectory = drive.TrimEnd('\\');
        settings.StartmoviePathPrefix = BuildWatchStartmoviePrefix(settings.RamDiskWatchDirectory, name, gameRoot);
    }

    public static string BuildGameStartmovieCommand(string sequenceName) =>
        $"startmovie {SanitizeSequenceName(sequenceName)} tga";

    public static string SanitizeSequenceName(string? name)
    {
        var n = (name ?? "frame").Trim();
        if (string.IsNullOrWhiteSpace(n))
            n = "frame";
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        return n;
    }

    public static string NormalizeDriveLetter(string? drive)
    {
        var d = (drive ?? "R:\\").Trim();
        if (d.Length == 1)
            d += ":\\";
        else if (d.Length == 2 && d[1] == ':')
            d += "\\";
        return d;
    }

    private static string BuildWatchStartmoviePrefix(string watchDirectory, string sequenceName, string? gameRoot)
    {
        var dir = watchDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (UsesMomentumSubfolder(gameRoot, dir))
            dir = Path.Combine(dir, MomentumDirectoryLinkService.MomentumFolderName);
        return Path.Combine(dir, sequenceName + "_");
    }

    private static bool UsesMomentumSubfolder(string? gameRoot, string watchDirectory)
    {
        var momentumDir = Path.Combine(watchDirectory, MomentumDirectoryLinkService.MomentumFolderName);
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var linkPath = Path.Combine(Path.GetFullPath(gameRoot), MomentumDirectoryLinkService.MomentumFolderName);
            if (MomentumDirectoryLinkService.IsDirectoryJunction(linkPath))
                return true;
        }

        return Directory.Exists(momentumDir);
    }

    public static string FormatDerivedPathsSummary(UserSettings settings, string? gameRoot = null)
    {
        EnsureDerivedPaths(settings, gameRoot);
        var gameCmd = BuildGameStartmovieCommand(settings.MovieSequenceName);
        return $"游戏内：{gameCmd}\n监视目录：{ResolveEffectiveWatchDirectory(settings, gameRoot)}\n文件前缀：{settings.StartmoviePathPrefix}";
    }

    /// <summary>
    /// TGA 实际落在游戏工作目录（junction 后为 RAM 盘下的 momentum），
    /// 与设置里的盘符根目录可能不同。
    /// </summary>
    public static string ResolveEffectiveWatchDirectory(UserSettings settings, string? gameRoot = null)
    {
        EnsureDerivedPaths(settings, gameRoot ?? settings.GameRootPath);
        var prefix = settings.StartmoviePathPrefix;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(prefix + "0.tga"));
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        return Path.GetFullPath(settings.RamDiskWatchDirectory);
    }
}
