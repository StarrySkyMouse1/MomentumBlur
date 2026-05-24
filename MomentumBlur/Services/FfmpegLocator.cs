using System.Diagnostics;
using System.IO;
using mmod_record.Models;

namespace mmod_record.Services;

/// <summary>
/// Resolves and validates the FFmpeg executable used by synthesis and muxing.
/// </summary>
public static class FfmpegLocator
{
    private const string BundledRelativePath = @"Resources\ffmpeg\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe";

    public static string? BundledFfmpegPath => FindBundledFfmpegPath();

    public static bool TryResolve(UserSettings settings, out string resolvedPath, out string? errorMessage)
    {
        resolvedPath = string.Empty;
        errorMessage = null;

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
        {
            var configured = settings.FfmpegPath.Trim().Trim('"');
            if (!File.Exists(configured))
            {
                errorMessage = $"配置的 FFmpeg 不存在：{configured}";
                return false;
            }

            resolvedPath = Path.GetFullPath(configured);
            return true;
        }

        var bundled = BundledFfmpegPath;
        if (bundled is not null)
        {
            resolvedPath = bundled;
            return true;
        }

        var fromPath = FindOnSystemPath("ffmpeg.exe");
        if (fromPath is not null)
        {
            resolvedPath = fromPath;
            return true;
        }

        errorMessage = "未配置 FFmpeg 路径，项目内置 FFmpeg 不存在，且系统 PATH 中找不到 ffmpeg.exe。请在设置中指定 ffmpeg.exe。";
        return false;
    }

    public static string Resolve(UserSettings settings)
    {
        if (!TryResolve(settings, out var path, out var error))
            throw new InvalidOperationException(error);
        return path;
    }

    /// <summary>
    /// Runs ffmpeg -version to verify that the executable can start.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ValidateAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await ProcessCancellation.WaitForExitOrKillAsync(process, cancellationToken);

            if (process.ExitCode != 0)
                return (false, $"ffmpeg 退出码 {process.ExitCode}");

            var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(无输出)";
            return (true, firstLine.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? FindBundledFfmpegPath()
    {
        var bases = new List<string>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            bases.Add(current.FullName);
            current = current.Parent;
        }

        foreach (var basePath in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var direct = Path.Combine(basePath, BundledRelativePath);
            if (File.Exists(direct))
                return Path.GetFullPath(direct);

            var projectNested = Path.Combine(basePath, "MomentumBlur", BundledRelativePath);
            if (File.Exists(projectNested))
                return Path.GetFullPath(projectNested);

            var legacyProjectNested = Path.Combine(basePath, "mmod_record_obs", BundledRelativePath);
            if (File.Exists(legacyProjectNested))
                return Path.GetFullPath(legacyProjectNested);
        }

        return null;
    }

    private static string? FindOnSystemPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return null;
    }
}
