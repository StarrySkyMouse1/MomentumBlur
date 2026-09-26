using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mmod.App.ViewModels;
using Mmod.Core.Services;

namespace Mmod.App.Views.Pages;

public partial class SettingsPage : Page
{
    private static readonly TimeSpan StageOnePreviewStart = TimeSpan.FromSeconds(
        2 * ReplayQualityPreviewCaptureService.PreviewSupersamplingMultiplier);
    private readonly DispatcherTimer _previewPositionTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private bool _updatingPreviewPosition;
    private SettingsViewModel? _previewViewModel;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _previewPositionTimer.Tick += OnPreviewPositionTimerTick;
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

        if (!ReferenceEquals(_previewViewModel, DataContext))
        {
            if (_previewViewModel is not null)
                _previewViewModel.QualityPreviewPlaybackRequested -= OnQualityPreviewPlaybackRequested;
            _previewViewModel = DataContext as SettingsViewModel;
            if (_previewViewModel is not null)
                _previewViewModel.QualityPreviewPlaybackRequested += OnQualityPreviewPlaybackRequested;
        }

        if (_previewViewModel?.HasQualityPreview == true)
            LoadQualityPreview(_previewViewModel.QualityPreviewPath);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _previewPositionTimer.Stop();
        if (_previewViewModel is not null)
        {
            _previewViewModel.QualityPreviewPlaybackRequested -= OnQualityPreviewPlaybackRequested;
            _previewViewModel = null;
        }
    }

    private void OnQualityPreviewPlaybackRequested(string path) => LoadQualityPreview(path);

    private void LoadQualityPreview(string path)
    {
        _previewPositionTimer.Stop();
        QualityPreviewPlayer.Stop();
        QualityPreviewPlayer.Source = null;
        QualityPreviewPositionSlider.IsEnabled = false;
        QualityPreviewPositionText.Text = "00:00 / 00:00";
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            QualityPreviewPlayer.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            QualityPreviewPlayer.Play();
        }
    }

    private void OnQualityPreviewMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player)
            return;

        player.Position = GetPreviewStart(player);
        player.Play();
    }

    private void OnQualityPreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement player)
        {
            player.Position = GetPreviewStart(player);
            InitializePreviewPosition(player);
            player.Play();
        }
    }

    private void OnQualityPreviewMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _previewPositionTimer.Stop();
        QualityPreviewPositionSlider.IsEnabled = false;
        QualityPreviewPositionText.Text = "00:00 / 00:00";
        if (DataContext is SettingsViewModel vm)
            vm.QualityPreviewStatus = $"内嵌播放失败：{e.ErrorException?.Message ?? "未知媒体错误"}；可在资源管理器中定位文件。";
    }

    private void OnQualityPreviewPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPreviewPosition
            || sender is not Slider slider
            || !slider.IsEnabled
            || !QualityPreviewPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var seconds = Math.Clamp(slider.Value, 0, QualityPreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds);
        QualityPreviewPlayer.Position = TimeSpan.FromSeconds(seconds);
        UpdatePreviewPositionDisplay(QualityPreviewPlayer.Position, QualityPreviewPlayer.NaturalDuration.TimeSpan);
    }

    private void OnPreviewPositionTimerTick(object? sender, EventArgs e)
    {
        if (!QualityPreviewPlayer.NaturalDuration.HasTimeSpan)
            return;

        UpdatePreviewPositionControls(
            QualityPreviewPlayer.Position,
            QualityPreviewPlayer.NaturalDuration.TimeSpan);
    }

    private void InitializePreviewPosition(MediaElement player)
    {
        if (!player.NaturalDuration.HasTimeSpan)
            return;

        var duration = player.NaturalDuration.TimeSpan;
        QualityPreviewPositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        QualityPreviewPositionSlider.IsEnabled = duration > TimeSpan.Zero;
        UpdatePreviewPositionControls(player.Position, duration);
        _previewPositionTimer.Start();
    }

    private void UpdatePreviewPositionControls(TimeSpan position, TimeSpan duration)
    {
        _updatingPreviewPosition = true;
        try
        {
            QualityPreviewPositionSlider.Value = Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
            UpdatePreviewPositionDisplay(position, duration);
        }
        finally
        {
            _updatingPreviewPosition = false;
        }
    }

    private void UpdatePreviewPositionDisplay(TimeSpan position, TimeSpan duration) =>
        QualityPreviewPositionText.Text = $"{FormatMediaTime(position)} / {FormatMediaTime(duration)}";

    private static string FormatMediaTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";

    private void OnQualityPreviewViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border viewport && e.NewSize.Width > 0)
            viewport.Height = e.NewSize.Width * 9.0 / 16.0;
    }

    private void OnQualityPreviewReplaySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        vm.SelectedQualityPreviewReplay = (e.NewValue as PreviewReplayTreeNode)?.Replay;
        if (vm.SelectedQualityPreviewReplay is not null)
            vm.QualityPreviewStatus = $"已选择：{vm.SelectedQualityPreviewReplay.DisplayText}";
    }

    private TimeSpan GetPreviewStart(MediaElement player)
    {
        if (DataContext is not SettingsViewModel vm
            || string.IsNullOrWhiteSpace(vm.QualityPreviewSlowMotionSourcePath)
            || string.IsNullOrWhiteSpace(vm.QualityPreviewPath)
            || !string.Equals(
                Path.GetFullPath(vm.QualityPreviewPath),
                Path.GetFullPath(vm.QualityPreviewSlowMotionSourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        if (player.NaturalDuration.HasTimeSpan
            && player.NaturalDuration.TimeSpan <= StageOnePreviewStart)
        {
            return TimeSpan.Zero;
        }

        return StageOnePreviewStart;
    }
}
