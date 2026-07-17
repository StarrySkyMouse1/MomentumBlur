using System.Windows;
using Mmod.App.ViewModels;
using Wpf.Ui.Controls;

namespace Mmod.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += OnLoaded;
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(Views.Pages.ComposePage));
    }
}
