using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Mmod.App.Services;

/// <summary>
/// 对话框与通知的抽象。ViewModel 只依赖本接口，不直接引用任何 UI 控件，
/// 便于替换实现与单元测试。
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// 展示确认弹层。返回 true 表示用户选择了主按钮。
    /// </summary>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "确定",
        string closeButtonText = "取消",
        bool danger = false);

    /// <summary>
    /// 展示信息弹层。
    /// </summary>
    Task ShowInfoAsync(string title, string message, string closeButtonText = "知道了");

    /// <summary>
    /// 展示就地通知（右下角 Snackbar），不打断当前操作。
    /// </summary>
    void Notify(string message, string? title = null);
}

/// <summary>
/// 基于 WPF-UI 4.3.0 的实现：确认/信息走 <see cref="ContentDialog"/> + <see cref="ContentDialogHost"/>，
/// 轻量反馈走 <see cref="SnackbarPresenter"/>。
/// </summary>
public sealed class DialogService : IDialogService
{
    private ContentDialogHost? _dialogHost;
    private SnackbarPresenter? _snackbarHost;

    /// <summary>
    /// 由主窗口在加载后注入两个宿主容器。
    /// </summary>
    public void Attach(ContentDialogHost dialogHost, SnackbarPresenter snackbarHost)
    {
        _dialogHost = dialogHost;
        _snackbarHost = snackbarHost;
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "确定",
        string closeButtonText = "取消",
        bool danger = false)
    {
        // 主窗口加载时会 Attach 宿主；此处兜底构造一个就地宿主，
        // 保证不出现原生 MessageBox 弹层。
        var host = EnsureHost();

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = new ContentPresenter
            {
                Content = BuildMessage(message)
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowInfoAsync(string title, string message, string closeButtonText = "知道了")
    {
        var host = EnsureHost();

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = new ContentPresenter
            {
                Content = BuildMessage(message)
            },
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    public void Notify(string message, string? title = null)
    {
        if (_snackbarHost is null)
            return;

        var snackbar = new Snackbar(_snackbarHost)
        {
            Title = title ?? string.Empty,
            Content = message,
            Timeout = TimeSpan.FromSeconds(3)
        };
        snackbar.Show();
    }

    /// <summary>
    /// 弹层内容一律用 <see cref="ui:TextBlock"/>（WPF-UI 组件），
    /// 走 FontTypography 语义层级，不硬编码字号。
    /// </summary>
    private static UIElement BuildMessage(string message) => new Wpf.Ui.Controls.TextBlock
    {
        Text = message,
        FontTypography = FontTypography.Body,
        Appearance = TextColor.Primary,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>
    /// 宿主未注入时的兜底：直接在活动窗口上挂一个宿主，
    /// 避免退化到原生 MessageBox。
    /// </summary>
    private ContentDialogHost EnsureHost()
    {
        if (_dialogHost is not null)
            return _dialogHost;

        var host = new ContentDialogHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (Application.Current?.MainWindow is Window owner)
        {
            if (owner.Content is Panel panel)
            {
                panel.Children.Add(host);
            }
            else if (owner.Content is Grid grid)
            {
                grid.Children.Add(host);
            }
        }

        _dialogHost = host;
        return host;
    }
}
