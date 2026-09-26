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

    private void OnNumberBoxValueChanged(object sender, Wpf.Ui.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (e.NewValue is not null)
            return;
        if (sender is Wpf.Ui.Controls.NumberBox box)
            box.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateTarget();
    }
}
