using System.Windows;
using System.Windows.Controls;
using Mmod.App.ViewModels;

namespace Mmod.App.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnNumberBoxValueChanged(object sender, Wpf.Ui.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (e.NewValue is null && sender is Wpf.Ui.Controls.NumberBox box)
            box.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateTarget();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            DataContext = main.ViewModel.Settings;
    }

    private void OnQualityPreviewReplaySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        vm.SelectedQualityPreviewReplay = (e.NewValue as PreviewReplayTreeNode)?.Replay;
        if (vm.SelectedQualityPreviewReplay is not null)
            vm.QualityPreviewStatus = $"已选择：{vm.SelectedQualityPreviewReplay.DisplayText}";
    }
}
