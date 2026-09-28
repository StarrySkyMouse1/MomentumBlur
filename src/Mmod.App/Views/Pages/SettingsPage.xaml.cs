using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>
    /// WPF 右键不会改变 ListView 选中项；右键菜单命令取自 VM 的选中项，
    /// 因此先选中所点的行，保证播放 / 删除作用在被点的影片上。
    /// </summary>
    private void OnQualityPreviewArtifactRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
            return;
        DependencyObject current = e.OriginalSource as DependencyObject ?? listView;
        while (current is not null and not ListViewItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is ListViewItem container)
            listView.SelectedItem = listView.ItemContainerGenerator.ItemFromContainer(container);
    }
}
