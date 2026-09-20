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

    /// <summary>
    /// ui:NumberBox.Value 是可空 double：清空输入框后失焦，控件会把 Value 置为 null
    /// （见 NumberBox.ValidateInput 的 SetCurrentValue(ValueProperty, null)）。
    /// 本页数值绑定源都是不可空 int/double，null 回写会失败，控件便停在空白、源保持旧值，
    /// 两边永久不一致 —— 表现就是「数字输不进去、框一直是空的」。
    /// 控件自身的 NumberBoxValidationMode 在 WPF-UI 4.3.0 里只注册了属性、没有实现，
    /// 因此这里在出现空白值时重新从绑定源拉取值，把输入框恢复成有效值。
    /// </summary>
    private void OnNumberBoxValueChanged(object sender, Wpf.Ui.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (e.NewValue is not null)
            return;

        if (sender is Wpf.Ui.Controls.NumberBox box)
            box.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateTarget();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            DataContext = main.ViewModel.Settings;
    }
}
