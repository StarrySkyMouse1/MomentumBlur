namespace mmod_record.Services;

/// <summary>
/// 定义消息框与文件/文件夹选择对话框的抽象接口。
/// </summary>
public interface IDialogService
{
    void ShowInfo(string message, string title = "提示");

    void ShowWarning(string message, string title = "提示");

    bool Confirm(string message, string title = "确认");

    bool TryPickFfmpegExecutable(out string? path);

    bool TryPickFolder(string title, string? initialDirectory, out string? folder);

    bool TryPickVideoFile(string title, string? initialDirectory, out string? path);

    bool TryPickVideoFiles(string title, string? initialDirectory, out IReadOnlyList<string> paths);
}
