using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Mmod.App.ViewModels;
using Wpf.Ui.Controls;

namespace Mmod.App.Converters;

/// <summary>
/// 任务呈现态 → 视觉资源键。所有颜色都走 Fluent 主题资源，不硬编码十六进制。
/// ConverterParameter: border（卡片描边）/ text（状态文字）
/// </summary>
public sealed class TaskStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var role = parameter as string ?? "text";
        var state = value as TaskPresentationState? ?? TaskPresentationState.Idle;
        var key = state switch
        {
            TaskPresentationState.Running => role == "border"
                ? "AccentControlElevationBorderBrush"
                : "AccentTextFillColorPrimaryBrush",
            TaskPresentationState.Paused => "SystemFillColorCautionBrush",
            TaskPresentationState.Success => "SystemFillColorSuccessBrush",
            TaskPresentationState.Danger => "SystemFillColorCriticalBrush",
            TaskPresentationState.Consuming => "SystemFillColorSuccessBrush",
            _ => role == "border" ? "ControlStrokeColorDefaultBrush" : "TextFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>进度条填充色：运行态主色、暂停态琥珀、其余灰。</summary>
public sealed class TaskProgressBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value as TaskPresentationState? ?? TaskPresentationState.Idle;
        var key = state switch
        {
            TaskPresentationState.Running => "AccentFillColorDefaultBrush",
            TaskPresentationState.Paused => "SystemFillColorCautionBrush",
            TaskPresentationState.Success => "SystemFillColorSuccessBrush",
            TaskPresentationState.Danger => "SystemFillColorCriticalBrush",
            _ => "ControlStrongStrokeColorDefaultBrush",
        };
        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool 取反后转 Visibility。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 布尔选中态 → ui:Button 的 ControlAppearance。
/// WPF-UI 4.3.0 没有分段器（Segmented Control），用 ui:Button 的 Appearance 表达选中态。
///
/// 默认（无 ConverterParameter）：true → Primary（强调色填充），false → Secondary（中性）。
/// ConverterParameter = "titlebar"：true → Secondary（受控填充 + 描边），false → Transparent（无填充、保留悬停反馈）。
/// 标题栏顶层的模式开关走这一支：选中态由强调色文字 + 受控填充表达，
/// 不占用「每屏仅一个 Primary」的主操作层级。
/// </summary>
public sealed class BooleanToControlAppearanceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var mode = parameter as string;
        if (string.Equals(mode, "titlebar", StringComparison.Ordinal))
            return value is true ? ControlAppearance.Secondary : ControlAppearance.Transparent;

        return value is true ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 布尔 → 主题画刷。ConverterParameter 形如 "TrueKey|FalseKey"，
/// 两个资源键都通过 <see cref="Application.TryFindResource"/> 解析，颜色不硬编码。
/// 键不存在时返回 <see cref="DependencyProperty.UnsetValue"/>，绑定会回落到继承值。
/// </summary>
public sealed class BooleanToThemeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var spec = parameter as string ?? string.Empty;
        var separator = spec.IndexOf('|');
        if (separator <= 0)
            return DependencyProperty.UnsetValue;

        var key = value is true ? spec[..separator] : spec[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(key))
            return DependencyProperty.UnsetValue;

        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// OBS 批量项状态文字 → ui:InfoBadge 的 InfoBadgeSeverity。
/// 与合成服务真实写入的 Status 文案一一对应（待处理 / 合成中… / 合成中 n/m /
/// 完成：… / 完成（无文件？） / 已取消 / 失败：…）。
/// WPF-UI 4.3.0 的 InfoBadgeSeverity 无 Neutral：排队等中性态用 Informational。
/// </summary>
public sealed class BatchStatusToSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string ?? string.Empty;
        if (status.StartsWith("失败", StringComparison.Ordinal))
            return InfoBadgeSeverity.Critical;
        if (status.StartsWith("已取消", StringComparison.Ordinal))
            return InfoBadgeSeverity.Caution;
        if (status.StartsWith("完成", StringComparison.Ordinal))
            return InfoBadgeSeverity.Success;
        if (status.StartsWith("合成中", StringComparison.Ordinal))
            return InfoBadgeSeverity.Attention;
        return InfoBadgeSeverity.Informational;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
