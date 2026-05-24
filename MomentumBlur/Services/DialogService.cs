using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace mmod_record.Services;

/// <summary>
/// 基于 WPF 的对话框服务实现。
/// </summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public void ShowInfo(string message, string title = "提示") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title = "提示") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message, string title = "确认") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) ==
        MessageBoxResult.Yes;

    public bool TryPickFfmpegExecutable(out string? path)
    {
        path = null;
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe|可执行文件 (*.exe)|*.exe",
            FileName = "ffmpeg.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(owner) != true)
            return false;

        path = dialog.FileName;
        return true;
    }

    public bool TryPickFolder(string title, string? initialDirectory, out string? folder)
    {
        folder = null;
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog(owner) != true)
            return false;

        folder = dialog.FolderName;
        return true;
    }

    public bool TryPickVideoFile(string title, string? initialDirectory, out string? path)
    {
        path = null;
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "视频文件|*.mkv;*.mp4;*.mov;*.avi;*.flv;*.webm|所有文件|*.*",
            CheckFileExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            var dir = Directory.Exists(initialDirectory) ? initialDirectory : Path.GetDirectoryName(initialDirectory);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog(owner) != true)
            return false;

        path = dialog.FileName;
        return true;
    }

    public bool TryPickVideoFiles(string title, string? initialDirectory, out IReadOnlyList<string> paths)
    {
        paths = [];
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "视频文件|*.mkv;*.mp4;*.mov;*.avi;*.flv;*.webm|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            var dir = Directory.Exists(initialDirectory) ? initialDirectory : Path.GetDirectoryName(initialDirectory);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog(owner) != true || dialog.FileNames.Length == 0)
            return false;

        paths = dialog.FileNames;
        return true;
    }
}
