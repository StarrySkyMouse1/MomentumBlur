using System.Windows;
using System.Windows.Controls;
using Mmod.App.ViewModels;

namespace Mmod.App.Views.Pages;

public partial class ComposePage : Page
{
    public ComposePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            DataContext = main.ViewModel.Compose;
            main.ViewModel.Compose.RefreshModeSummary();
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not ComposeViewModel vm)
            return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        foreach (var file in files)
            vm.AddVideoPath(file);
    }
}
