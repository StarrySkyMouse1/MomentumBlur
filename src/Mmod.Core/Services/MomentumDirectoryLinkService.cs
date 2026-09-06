using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Mmod.Core.Services;

/// <summary>
/// 将游戏根目录下的 <c>momentum</c> 与 RAM 盘上的副本通过目录链接（junction）关联。
/// </summary>
public static class MomentumDirectoryLinkService
{
    public const string MomentumFolderName = "momentum";
    public const string BackupFolderName = "_momentum";

    private static readonly EnumerationOptions CopyEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public sealed class LinkPaths
    {
        public required string GameRoot { get; init; }
        public required string LinkPath { get; init; }
        public required string BackupPath { get; init; }
        public required string RamMomentumPath { get; init; }
    }

    public static LinkPaths ResolvePaths(string gameRoot, string ramWatchDirectory)
    {
        var root = PathSanitizer.GetFullPath(gameRoot);
        var ramRoot = PathSanitizer.GetFullPath(ramWatchDirectory);
        return new LinkPaths
        {
            GameRoot = root,
            LinkPath = Path.Combine(root, MomentumFolderName),
            BackupPath = Path.Combine(root, BackupFolderName),
            RamMomentumPath = Path.Combine(ramRoot, MomentumFolderName),
        };
    }

    public static bool IsDirectoryJunction(string path) =>
        Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static string? TryGetJunctionTarget(string junctionPath)
    {
        if (!IsDirectoryJunction(junctionPath))
            return null;

        var fromReparse = WindowsJunctionHelper.TryGetMountPointTarget(junctionPath);
        if (!string.IsNullOrWhiteSpace(fromReparse))
            return fromReparse;

        try
        {
            var linkTarget = new DirectoryInfo(junctionPath).LinkTarget;
            if (!string.IsNullOrWhiteSpace(linkTarget))
                return PathSanitizer.GetFullPath(linkTarget);
        }
        catch (IOException)
        {
            // ignore
        }

        try
        {
            var target = Directory.ResolveLinkTarget(junctionPath, returnFinalTarget: true);
            return target?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string DescribeLinkState(LinkPaths paths)
    {
        if (IsDirectoryJunction(paths.LinkPath))
        {
            var target = TryGetJunctionTarget(paths.LinkPath);
            if (string.IsNullOrWhiteSpace(target) && Directory.Exists(paths.RamMomentumPath))
                target = paths.RamMomentumPath;

            target ??= "（无法解析目标，链接可能仍有效）";
            return $"已链接：{paths.LinkPath} → {target}";
        }

        if (Directory.Exists(paths.BackupPath) && !Directory.Exists(paths.LinkPath))
            return "已备份 _momentum，但未创建链接（可重新「生成链接目录」）。";

        if (Directory.Exists(paths.LinkPath) && Directory.Exists(paths.RamMomentumPath))
            return "momentum 为实体目录；可执行「生成链接目录」迁移到 RAM 盘。";

        return "未建立 momentum 目录链接。";
    }

    /// <summary>
    /// 严格校验链接目录：必须已创建（junction）、目标可解析、目标存在、且指向
    /// 配置的 RAM 监视目录下的 <c>momentum</c>（内存盘）。任一项不满足即抛出
    /// <see cref="InvalidOperationException"/>。
    /// 这是「不允许没有链接目录直接向磁盘录制」的硬性闸门。
    /// </summary>
    public static void EnsureLinkedToRam(LinkPaths paths)
    {
        if (IsDirectoryJunction(paths.LinkPath))
        {
            var target = TryGetJunctionTarget(paths.LinkPath);
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException(
                    $"链接目录 {paths.LinkPath} 的目标无法解析（内存盘可能已卸载）。请先挂载 ImDisk RAM 盘，再重新「生成链接目录」。");

            if (!Directory.Exists(target))
                throw new InvalidOperationException(
                    $"链接目录 {paths.LinkPath} 指向的目标不存在（内存盘可能未挂载）：{target}。禁止直接向磁盘录制，请先挂载 ImDisk RAM 盘。");

            if (!PathsEqual(target, paths.RamMomentumPath))
                throw new InvalidOperationException(
                    $"链接目录 {paths.LinkPath} 未指向内存盘目录：当前目标 {target}，应为 {paths.RamMomentumPath}。请先「取消链接」，确认内存盘已挂载后重新「生成链接目录」。");

            return;
        }

        if (IsBrokenDirectoryJunction(paths.LinkPath))
            throw new InvalidOperationException(
                $"链接目录 {paths.LinkPath} 的目标不可达（内存盘可能未挂载）。请先挂载 ImDisk RAM 盘，再重新「生成链接目录」。");

        if (Directory.Exists(paths.LinkPath))
            throw new InvalidOperationException(
                $"未创建链接目录：{paths.LinkPath} 是实体目录而非目录链接。禁止直接向磁盘录制，请先执行「生成链接目录」迁移到内存盘。");

        throw new InvalidOperationException(
            $"未创建链接目录：{paths.LinkPath} 不存在。禁止直接向磁盘录制，请先挂载 ImDisk RAM 盘并在「设置」中执行「生成链接目录」。");
    }

    /// <summary>
    /// 捕获前的严格闸门（任务执行 / 回放验证 / 性能预检 / 手动录制共用）：
    /// 设置了游戏根目录时，必须满足——链接目录已创建且指向内存盘（RAM 监视目录下的
    /// <c>momentum</c>），并且实际监视目录就是该内存盘目标；否则抛出异常拒绝执行，
    /// 绝不回退到磁盘路径上监视或录制。
    /// </summary>
    public static void EnsureCaptureTargetOnRam(
        string? gameRoot,
        string ramWatchDirectory,
        string effectiveWatchDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            // 无游戏根目录（纯手动模式）：不存在链接概念，仅信任用户配置的监视目录。
            return;
        }

        if (string.IsNullOrWhiteSpace(ramWatchDirectory))
            throw new InvalidOperationException("未配置 TGA 监视目录（内存盘）。无法确认链接目标。");

        var paths = ResolvePaths(gameRoot, ramWatchDirectory);
        EnsureLinkedToRam(paths);

        if (!PathsEqual(effectiveWatchDirectory, paths.RamMomentumPath))
            throw new InvalidOperationException(
                $"TGA 监视目录未落在内存盘链接目录上：{effectiveWatchDirectory}（应为 {paths.RamMomentumPath}）。请确认已「生成链接目录」且内存盘已挂载。");
    }

    /// <summary>
    /// 将 <c>momentum</c> 重命名为 <c>_momentum</c>，复制到 RAM 盘并创建 junction。
    /// </summary>
    public static void CreateLink(LinkPaths paths, bool overwriteRamCopy)
    {
        if (!Directory.Exists(paths.GameRoot))
            throw new InvalidOperationException($"游戏根目录不存在：{paths.GameRoot}");

        var ramRoot = Path.GetDirectoryName(paths.RamMomentumPath)!;
        if (!Directory.Exists(ramRoot))
            throw new InvalidOperationException($"RAM 监视目录不存在：{ramRoot}（请确认 ImDisk 已挂载）");

        if (IsDirectoryJunction(paths.LinkPath))
            Directory.Delete(paths.LinkPath, recursive: false);

        EnsureBackupFolder(paths);

        if (Directory.Exists(paths.RamMomentumPath))
        {
            if (!overwriteRamCopy)
            {
                throw new InvalidOperationException(
                    $"RAM 盘上已存在 {paths.RamMomentumPath}。若需覆盖请先确认覆盖，或手动删除后重试。");
            }

            DeleteDirectoryTree(paths.RamMomentumPath);
        }

        CopyDirectory(paths.BackupPath, paths.RamMomentumPath);
        RemoveLinkPathIfExists(paths.LinkPath);
        CreateJunction(paths.LinkPath, paths.RamMomentumPath);
    }

    /// <summary>
    /// 是否存在可通过「取消链接」清理的状态（junction、<c>_momentum</c> 或 RAM 副本）。
    /// </summary>
    public static bool HasRemovableLinkArtifacts(LinkPaths paths) =>
        IsDirectoryJunction(paths.LinkPath) ||
        Directory.Exists(paths.BackupPath) ||
        Directory.Exists(paths.RamMomentumPath);

    /// <summary>
    /// 删除 junction、将 <c>_momentum</c> 还原为 <c>momentum</c>，并尽量删除 RAM 盘副本。
    /// RAM 盘未挂载时仍会完成 junction 删除与备份还原。
    /// </summary>
    /// <returns>若已执行清理为 <c>true</c>；若本就已无链接相关目录则为 <c>false</c>（幂等）。</returns>
    public static bool RemoveLink(LinkPaths paths)
    {
        if (!Directory.Exists(paths.GameRoot))
            throw new InvalidOperationException($"游戏根目录不存在：{paths.GameRoot}");

        var hadJunction = IsDirectoryJunction(paths.LinkPath) || IsBrokenDirectoryJunction(paths.LinkPath);
        var hadBackup = Directory.Exists(paths.BackupPath);
        var hadRamCopy = SafeDirectoryExists(paths.RamMomentumPath);

        if (!hadJunction && !hadBackup && !hadRamCopy)
            return false;

        // momentum 已是实体目录且没有待还原的 _momentum，说明链接取消已经完成。
        // RAM 盘可能只残留上次未清理掉的副本；清掉它后仍按幂等成功处理。
        if (!hadJunction && !hadBackup && Directory.Exists(paths.LinkPath))
        {
            if (hadRamCopy)
                TryDeleteRamCopy(paths.RamMomentumPath);
            return hadRamCopy;
        }

        if (hadJunction)
            RemoveLinkPathIfExists(paths.LinkPath);

        if (Directory.Exists(paths.LinkPath) && !IsDirectoryJunction(paths.LinkPath))
        {
            // 已有实体 momentum，同时还有 _momentum：优先删掉多余备份，避免双份残留
            if (hadBackup)
            {
                try { DeleteDirectoryTree(paths.BackupPath); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"已存在实体 {MomentumFolderName}，无法自动清理 {BackupFolderName}：{ex.Message}");
                }
            }

            TryDeleteRamCopy(paths.RamMomentumPath);
            return true;
        }

        if (hadBackup)
            RestoreBackupFolder(paths.BackupPath, paths.LinkPath);

        TryDeleteRamCopy(paths.RamMomentumPath);
        return true;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 目标盘未挂载时，Directory.Exists 可能为 false，但仍可能是损坏的 junction。
    /// </summary>
    private static bool IsBrokenDirectoryJunction(string path)
    {
        try
        {
            if (!Path.Exists(path))
                return false;
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Directory) != 0 &&
                   (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteRamCopy(string ramMomentumPath)
    {
        try
        {
            if (SafeDirectoryExists(ramMomentumPath))
                DeleteDirectoryTree(ramMomentumPath);
        }
        catch
        {
            // RAM 未挂载或清理失败不影响本地还原
        }
    }

    /// <summary>
    /// 将 <c>_momentum</c> 还原为 <c>momentum</c>；Move 失败时复制后删除备份（与创建链接时的策略一致）。
    /// </summary>
    private static void RestoreBackupFolder(string backupPath, string linkPath)
    {
        if (!Directory.Exists(backupPath))
            return;

        RemoveLinkPathIfExists(linkPath);

        if (Directory.Exists(linkPath))
        {
            throw new InvalidOperationException(
                $"无法还原：{linkPath} 仍存在。请手动删除该路径后重试。");
        }

        IOException? lastMoveError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(150 * attempt);

            try
            {
                Directory.Move(backupPath, linkPath);
                return;
            }
            catch (IOException ex)
            {
                lastMoveError = ex;
            }
        }

        try
        {
            CopyDirectory(backupPath, linkPath);
            DeleteDirectoryTree(backupPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法将 {BackupFolderName} 还原为 {MomentumFolderName}。请关闭游戏、资源管理器或杀毒软件对该目录的占用后重试。",
                lastMoveError is null ? ex : new AggregateException(lastMoveError, ex));
        }
    }

    private static void EnsureBackupFolder(LinkPaths paths)
    {
        if (Directory.Exists(paths.BackupPath))
        {
            RemoveLinkPathIfExists(paths.LinkPath);
            return;
        }

        if (Directory.Exists(paths.LinkPath))
        {
            if (IsDirectoryJunction(paths.LinkPath))
                Directory.Delete(paths.LinkPath, recursive: false);
            else
            {
                try
                {
                    Directory.Move(paths.LinkPath, paths.BackupPath);
                }
                catch (IOException)
                {
                    CopyDirectory(paths.LinkPath, paths.BackupPath);
                    DeleteDirectoryTree(paths.LinkPath);
                }

                return;
            }
        }

        if (Directory.Exists(paths.RamMomentumPath))
        {
            CopyDirectory(paths.RamMomentumPath, paths.BackupPath);
            return;
        }

        throw new InvalidOperationException(
            $"未找到 {MomentumFolderName}、{BackupFolderName} 或 RAM 盘副本，请确认游戏根路径与 TGA 监视目录正确。");
    }

    private static void RemoveLinkPathIfExists(string linkPath)
    {
        if (IsDirectoryJunction(linkPath) || IsBrokenDirectoryJunction(linkPath))
        {
            Directory.Delete(linkPath, recursive: false);
            return;
        }

        if (!Directory.Exists(linkPath))
            return;

        DeleteDirectoryTree(linkPath);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var target = PathSanitizer.GetFullPath(targetPath);
        if (!Directory.Exists(target))
            throw new InvalidOperationException($"链接目标不存在：{target}");

        var junction = PathSanitizer.GetFullPath(junctionPath);
        RemoveLinkPathIfExists(junction);

        if (Directory.Exists(junction))
            throw new InvalidOperationException($"无法创建链接，路径仍存在：{junction}");

        try
        {
            CreateJunctionViaMklink(junction, target);
        }
        catch (Exception mklinkEx)
        {
            try
            {
                WindowsJunctionHelper.Create(junction, target);
            }
            catch (IOException ioctlEx)
            {
                var win32 = ioctlEx.InnerException as Win32Exception;
                var detail = win32?.Message ?? ioctlEx.Message;
                throw new InvalidOperationException(
                    $"创建目录链接失败：{detail}（mklink：{mklinkEx.Message}）",
                    ioctlEx);
            }
        }
    }

    private static void CreateJunctionViaMklink(string junctionPath, string targetPath)
    {
        var arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("无法启动 cmd 执行 mklink。");

        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(msg) ? $"mklink 失败，退出码 {process.ExitCode}。" : msg);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        var source = PathSanitizer.GetFullPath(sourceDir);
        var dest = PathSanitizer.GetFullPath(destDir);
        Directory.CreateDirectory(dest);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", CopyEnumerationOptions))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(dest, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", CopyEnumerationOptions))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetFile = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private static void DeleteDirectoryTree(string path)
    {
        if (IsDirectoryJunction(path))
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            RunRobocopyPurge(path);
        }
    }

    private static void RunRobocopyPurge(string directory)
    {
        var empty = Path.Combine(Path.GetTempPath(), "mmod_record_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var arguments =
                $"\"{empty}\" \"{directory}\" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "robocopy",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("无法启动 robocopy 清理目录。");

            process.WaitForExit();
            if (process.ExitCode is > 7)
                throw new InvalidOperationException($"清理目录失败（robocopy {process.ExitCode}）：{directory}");

            Directory.Delete(directory, recursive: true);
        }
        finally
        {
            try
            {
                Directory.Delete(empty, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        var na = PathSanitizer.GetFullPath(a).TrimEnd('\\', '/');
        var nb = PathSanitizer.GetFullPath(b).TrimEnd('\\', '/');
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
