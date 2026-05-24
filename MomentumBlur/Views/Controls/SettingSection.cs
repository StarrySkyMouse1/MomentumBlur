using System.ComponentModel;
using System.Windows;

namespace mmod_record.Views.Controls;

/// <summary>
/// 设置页内层小节容器（标题 + 说明 + 圆角内容区）。
/// </summary>
public class SettingSection : TitledContentControl
{
    public static new readonly DependencyProperty TitleProperty =
        TitledContentControl.TitleProperty.AddOwner(typeof(SettingSection));

    public static new readonly DependencyProperty DescriptionProperty =
        TitledContentControl.DescriptionProperty.AddOwner(typeof(SettingSection));

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

    static SettingSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingSection),
            new FrameworkPropertyMetadata(typeof(SettingSection)));
    }
}
