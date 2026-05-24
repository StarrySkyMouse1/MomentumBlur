using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace mmod_record.Views.Controls;

/// <summary>
/// 带标题与说明的 <see cref="ContentControl"/> 基类。
/// </summary>
public class TitledContentControl : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(TitledContentControl),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(TitledContentControl),
            new PropertyMetadata(string.Empty));

    [Bindable(true)]
    [Category("Appearance")]
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    [Bindable(true)]
    [Category("Appearance")]
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
