using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mmod.App.ViewModels;

namespace Mmod.App.Views.Pages;

public partial class SettingsPage : Page
{
    private Window? _hostWindow;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateViewport();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            DataContext = main.ViewModel.Settings;

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseWheel -= OnHostPreviewMouseWheel;
            _hostWindow.PreviewMouseWheel += OnHostPreviewMouseWheel;
            _hostWindow.SizeChanged -= OnHostSizeChanged;
            _hostWindow.SizeChanged += OnHostSizeChanged;
        }

        UpdateViewport();
        Dispatcher.BeginInvoke(UpdateViewport, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(UpdateViewport, DispatcherPriority.ContextIdle);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseWheel -= OnHostPreviewMouseWheel;
            _hostWindow.SizeChanged -= OnHostSizeChanged;
            _hostWindow = null;
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        var height = ResolveViewportHeight();
        if (height <= 0)
            return;

        RootScroll.Height = height;
        RootScroll.MaxHeight = height;
    }

    private double ResolveViewportHeight()
    {
        for (var d = VisualTreeHelper.GetParent(this) as DependencyObject;
             d is not null;
             d = VisualTreeHelper.GetParent(d))
        {
            if (d is Frame frame && frame.ActualHeight > 32)
                return Math.Max(0, frame.ActualHeight - 8);

            if (d is FrameworkElement fe &&
                fe.ActualHeight > 32 &&
                fe.GetType().Name.Contains("NavigationViewContent", StringComparison.Ordinal))
                return Math.Max(0, fe.ActualHeight - 8);
        }

        if (_hostWindow is { ActualHeight: > 160 })
            return Math.Max(200, _hostWindow.ActualHeight - 140);

        return 0;
    }

    private void OnHostPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsVisible || !IsLoaded || e.Delta == 0)
            return;

        if (!IsMouseOverPage())
            return;

        if (RootScroll.ScrollableHeight <= 0)
            UpdateViewport();

        if (RootScroll.ScrollableHeight <= 0)
            return;

        RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private bool IsMouseOverPage()
    {
        if (IsMouseOver || RootScroll.IsMouseOver)
            return true;

        if (_hostWindow is null)
            return false;

        var pos = Mouse.GetPosition(RootScroll);
        return pos.X >= 0 && pos.Y >= 0 &&
               pos.X <= RootScroll.ActualWidth &&
               pos.Y <= RootScroll.ActualHeight;
    }
}
