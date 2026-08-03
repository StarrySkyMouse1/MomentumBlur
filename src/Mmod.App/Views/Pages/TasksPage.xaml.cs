using System.Windows;
using System.Windows.Controls;

namespace Mmod.App.Views.Pages;

public partial class TasksPage : Page
{
    public TasksPage() { InitializeComponent(); Loaded += OnLoaded; }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) DataContext = main.ViewModel.Tasks;
    }
}
