using System.ComponentModel;
using System.Windows;
using Mmod.App.Services;
using Mmod.App.ViewModels;
using Mmod.Core.Models;
using Wpf.Ui.Controls;

namespace Mmod.App;

public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// 当前实际显示的页面类型。由 <see cref="NavigationView.Navigated"/> 权威更新，
    /// 用于判断模式切换后是否需要跳转到目标模式的默认页。
    /// </summary>
    private Type? _currentPageType;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += OnLoaded;
        RootNavigation.Navigated += OnNavigated;
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 主窗口是 RootContentDialog / SnackbarPresenter 的唯一宿主，
        // 窗口一加载就绑定，保证任何 ConfirmAsync / ShowInfoAsync / Notify
        // 都走框架弹层，不会退化成原生 MessageBox。
        if (DialogServiceLocator.Current is DialogService dialogService)
            dialogService.Attach(RootContentDialog, SnackbarPresenter);

        ViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;

        // 先按已保存模式投影有效主导航，再进入该模式的默认页（不再固定合成页）。
        ApplyCaptureModeNavigation();
        NavigateTo(DefaultPageFor(ViewModel.Settings.CaptureMode));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(SettingsViewModel.CaptureMode))
            return;

        ApplyCaptureModeNavigation();

        // 公共设置页留驻：在设置页切换模式时留在设置页，
        // 专属页签由设置页自身的模式绑定替换，公共字段值保持不变。
        if (_currentPageType == typeof(Views.Pages.SettingsPage))
            return;

        NavigateTo(DefaultPageFor(ViewModel.Settings.CaptureMode));
    }

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args) =>
        _currentPageType = args.Page.GetType();

    private static Type DefaultPageFor(CaptureMode mode) =>
        mode == CaptureMode.Obs ? typeof(Views.Pages.ComposePage) : typeof(Views.Pages.TasksPage);

    /// <summary>
    /// 按当前模式原地重建有效主导航：
    /// OBS = 录制与处理 + 设置；TGA = 任务 + 设置。
    ///
    /// 只增删这一批常驻实例，不新建 <see cref="NavigationViewItem"/>——
    /// 框架按 <c>TargetPageType</c> 登记的字典不会为同名新实例刷新。
    /// 只用 Remove/Insert 的增量通知（不用 Clear 触发的 Reset），
    /// 避免容器被整体重建后出现重复项或失效选中态。
    /// </summary>
    private void ApplyCaptureModeNavigation()
    {
        object[] effective = ViewModel.Settings.CaptureMode == CaptureMode.Obs
            ? [ComposeNavigationItem, SettingsNavigationItem]
            : [TasksNavigationItem, SettingsNavigationItem];

        var menu = RootNavigation.MenuItems;

        // 1) 移除不再属于当前模式的项。
        for (var index = menu.Count - 1; index >= 0; index--)
        {
            if (!effective.Contains(menu[index]))
                menu.RemoveAt(index);
        }

        // 2) 补齐缺失项并校正顺序。
        for (var index = 0; index < effective.Length; index++)
        {
            var item = effective[index];
            var currentIndex = menu.IndexOf(item);
            if (currentIndex < 0)
                menu.Insert(index, item);
            else if (currentIndex != index)
            {
                menu.RemoveAt(currentIndex);
                menu.Insert(index, item);
            }
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (_currentPageType == pageType)
            return;

        // 只由 Navigated 事件记录当前页：导航失败时不留下错误的“已到达”状态。
        RootNavigation.Navigate(pageType);
    }
}
