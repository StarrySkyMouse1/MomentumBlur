using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using mmod_record.Services;
using mmod_record.ViewModels;
using mmod_record.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace mmod_record;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        SystemThemeWatcher.Watch(this);
        InitializeComponent();
        LoadWindowIcon();
        _viewModel = new MainViewModel(new DialogService(this));
        DataContext = _viewModel;
        RootNavigation.Navigated += OnNavigated;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        RootNavigation.Navigate(typeof(RecordComposePage));

    private void OnNavigated(NavigationView sender, NavigatedEventArgs e)
    {
        switch (e.Page)
        {
            case RecordComposePage record:
                record.DataContext = _viewModel;
                break;
            case SettingsPage settings:
                settings.DataContext = _viewModel.Settings;
                break;
        }
    }

    private void LoadWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "MomentumBlur.ico");
        if (File.Exists(iconPath))
            Icon = BitmapFrame.Create(new global::System.Uri(iconPath, global::System.UriKind.Absolute));
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.DisposeAsync();
        base.OnClosed(e);
    }
}
