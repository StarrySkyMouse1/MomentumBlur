namespace Mmod.App.Services;

/// <summary>
/// 极简的服务定位器，用于把主窗口的对话框宿主暴露给各 ViewModel。
/// 项目规模不需要引入完整 DI 容器，这里保持最小实现。
/// </summary>
public static class DialogServiceLocator
{
    private static IDialogService? _current;

    public static IDialogService Current => _current ??= new DialogService();

    /// <summary>
    /// 在测试或替换实现时使用。
    /// </summary>
    public static void Override(IDialogService service) => _current = service;
}
