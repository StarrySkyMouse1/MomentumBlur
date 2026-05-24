using System.ComponentModel;
using System.Windows;

namespace mmod_record.Views.Controls;

/// <summary>
/// 设置页顶层分组容器（标题 + 说明 + 卡片内容区）。
/// </summary>
public class SettingsGroup : TitledContentControl
{
    public static new readonly DependencyProperty TitleProperty =
        TitledContentControl.TitleProperty.AddOwner(typeof(SettingsGroup));

    public static new readonly DependencyProperty DescriptionProperty =
        TitledContentControl.DescriptionProperty.AddOwner(typeof(SettingsGroup));

    [Bindable(true)]
    [Category("Appearance")]
    public new string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    [Bindable(true)]
    [Category("Appearance")]
    public new string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    static SettingsGroup()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsGroup),
            new FrameworkPropertyMetadata(typeof(SettingsGroup)));
    }
}
