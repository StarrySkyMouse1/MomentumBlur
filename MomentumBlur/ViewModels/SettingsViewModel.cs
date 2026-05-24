using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mmod_record.Models;
using mmod_record.Services;

namespace mmod_record.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private bool _isLoading;
    private CancellationTokenSource? _autoSaveCts;

    [ObservableProperty]
    private int _obsCaptureFramerate = 120;

    [ObservableProperty]
    private int _supersamplingMultiplier = 2;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    private string _videoEncoder = "libx264";

    public IReadOnlyList<VideoEncoderOption> VideoEncoderOptions => VideoEncoderCatalog.Options;

    public string VideoEncoderHint => VideoEncoderCatalog.GetHint(VideoEncoder);

    [ObservableProperty]
    private string _compositionBackend = "gpu";

    public IReadOnlyList<VideoEncoderOption> CompositionBackendOptions => CompositionBackendCatalog.Options;

    public string CompositionBackendHint => CompositionBackendCatalog.GetHint(CompositionBackend);

    [ObservableProperty]
    private string _crfText = "18";

    [ObservableProperty]
    private string _exposureText = "0.5";

    [ObservableProperty]
    private string _ffmpegEncodingPreview = string.Empty;

    [ObservableProperty]
    private string _videoOutputDirectory = string.Empty;

    [ObservableProperty]
    private string _ffmpegPathStatusText = string.Empty;

    [ObservableProperty]
    private Brush _ffmpegPathStatusBrush = Brushes.Gray;

    [ObservableProperty]
    private string _settingsPathHint = string.Empty;

    [ObservableProperty]
    private string _supersamplingHint = string.Empty;

    [ObservableProperty]
    private int _maxParallelGpuJobs = 2;

    [ObservableProperty]
    private string _gameCommandEnableSlowMotionBlockText = string.Empty;

    [ObservableProperty]
    private string _gameCommandRestoreBlockText = string.Empty;

    [ObservableProperty]
    private bool _hideHudInGameCommands;

    public IReadOnlyList<int> ObsCaptureFramerateOptions => ProjectConstants.SupportedObsCaptureFramerates;

    public IReadOnlyList<int> MaxParallelGpuJobsOptions { get; } = [1, 2, 3, 4];

    public SettingsViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Load();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(ObsCaptureFramerate) or nameof(SupersamplingMultiplier) or nameof(HideHudInGameCommands))
        {
            if (e.PropertyName is not nameof(HideHudInGameCommands))
            {
                UpdateSupersamplingHint();
                UpdateFfmpegEncodingPreview();
            }

            UpdateGameSlowMotionCommands();
        }

        if (e.PropertyName is nameof(VideoEncoder) or nameof(CompositionBackend) or nameof(CrfText) or nameof(ExposureText))
        {
            if (e.PropertyName is nameof(VideoEncoder))
                OnPropertyChanged(nameof(VideoEncoderHint));
            if (e.PropertyName is nameof(CompositionBackend))
                OnPropertyChanged(nameof(CompositionBackendHint));
            UpdateFfmpegEncodingPreview();
        }

        if (_isLoading || string.IsNullOrEmpty(e.PropertyName))
            return;

        if (IsPersistedSettingProperty(e.PropertyName))
            ScheduleAutoSave();

        if (e.PropertyName is nameof(VideoOutputDirectory))
            RefreshPathsStatus();
    }

    public void Load()
    {
        _isLoading = true;
        var settings = UserSettingsStore.Load();
        var resetInvalidPaths = UserSettingsStore.LastLoadResetInvalidPaths ||
                                UserSettingsStore.ResetInvalidPathValues(settings);

        ObsCaptureFramerate = ObsOutputPathHelper.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
        SupersamplingMultiplier = settings.SupersamplingMultiplier > 0 ? settings.SupersamplingMultiplier : 2;
        FfmpegPath = settings.FfmpegPath ?? FfmpegLocator.BundledFfmpegPath ?? string.Empty;
        VideoEncoder = VideoEncoderCatalog.Normalize(settings.VideoEncoder);
        CompositionBackend = CompositionBackendCatalog.Normalize(settings.CompositionBackend);
        CrfText = (settings.Crf is > 0 and <= 51 ? settings.Crf : 18).ToString(CultureInfo.InvariantCulture);
        ExposureText = (settings.Exposure is > 0 and <= 1 ? settings.Exposure : 0.5)
            .ToString("0.##", CultureInfo.InvariantCulture);
        VideoOutputDirectory = settings.VideoOutputDirectory ?? string.Empty;
        MaxParallelGpuJobs = Math.Clamp(settings.MaxParallelGpuJobs, 1, 4);
        HideHudInGameCommands = settings.HideHudInGameCommands;
        SettingsPathHint = $"工具配置：{UserSettingsStore.SettingsFilePath}";

        UpdateSupersamplingHint();
        UpdateGameSlowMotionCommands();
        UpdateFfmpegEncodingPreview();
        RefreshPathsStatus();

        if (resetInvalidPaths)
            UserSettingsStore.Save(settings);

        _isLoading = false;
    }

    public UserSettings ToSettings()
    {
        var settings = new UserSettings
        {
            FfmpegPath = NullIfEmpty(FfmpegPath),
            ObsCaptureFramerate = ObsCaptureFramerate,
            SupersamplingMultiplier = Math.Clamp(SupersamplingMultiplier, 1, 120),
            VideoOutputDirectory = NullIfEmpty(VideoOutputDirectory),
            VideoEncoder = VideoEncoderCatalog.Normalize(VideoEncoder),
            CompositionBackend = CompositionBackendCatalog.Normalize(CompositionBackend),
            Crf = ParseCrf(CrfText),
            Exposure = ParseExposure(ExposureText),
            MaxParallelGpuJobs = Math.Clamp(MaxParallelGpuJobs, 1, 4),
            HideHudInGameCommands = HideHudInGameCommands,
        };

        UserSettingsStore.Normalize(settings);
        UserSettingsStore.ResetInvalidPathValues(settings);
        return settings;
    }

    public void Save() => UserSettingsStore.Save(ToSettings());

    public RenderPreset ToRenderPreset() =>
        RenderPreset.FromObsCapture(ObsCaptureFramerate, SupersamplingMultiplier);

    [RelayCommand]
    private void CopyGameCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning($"无法复制到剪贴板：{ex.Message}", "复制");
        }
    }

    private void ScheduleAutoSave()
    {
        if (_isLoading)
            return;

        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;
        _ = RunAutoSaveDebouncedAsync(token);
    }

    private async Task RunAutoSaveDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(450, token);
            if (token.IsCancellationRequested)
                return;

            PersistNow();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void PersistNow()
    {
        if (_isLoading)
            return;

        _autoSaveCts?.Cancel();
        Save();
        UpdateFfmpegEncodingPreview();
        RefreshPathsStatus();
    }

    [RelayCommand]
    private void SetObsCaptureFramerate(string? fpsText)
    {
        if (!int.TryParse(fpsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps))
            return;

        ObsCaptureFramerate = ObsOutputPathHelper.NormalizeObsCaptureFramerate(fps);
        PersistNow();
    }

    private void UpdateFfmpegEncodingPreview()
    {
        try
        {
            var preset = ToRenderPreset();
            var blend = SynthesisTiming.GetSynthesisBlendFrames(
                preset.BlendFrames,
                preset.ObsCaptureFramerate);
            var playbackScale = SynthesisTiming.GetPlaybackSpeedScale(
                preset.BlendFrames,
                preset.ObsCaptureFramerate);
            FfmpegEncodingPreview = CompositionBackendCatalog.Normalize(CompositionBackend) == CompositionBackendCatalog.GpuResidentOpenClId
                ? FfmpegOpenClSynthesisService.DescribeSynthesis(
                    preset,
                    blend,
                    ParseExposure(ExposureText),
                    CompositionBackend)
                : FfmpegSynthesisService.DescribeSynthesis(
                    preset,
                    blend,
                    playbackScale,
                    ParseExposure(ExposureText),
                    CompositionBackend);
        }
        catch
        {
            FfmpegEncodingPreview = "请填写有效的编码参数。";
        }
    }

    private void UpdateSupersamplingHint()
    {
        var n = Math.Clamp(SupersamplingMultiplier, 1, 120);
        var fps = ObsOutputPathHelper.NormalizeObsCaptureFramerate(ObsCaptureFramerate);
        SupersamplingHint = SynthesisTiming.BuildHostTimescaleObsHint(n, fps);
    }

    private void UpdateGameSlowMotionCommands()
    {
        var fps = ObsCaptureFramerate;
        var n = SupersamplingMultiplier;

        GameCommandEnableSlowMotionBlockText = GameSlowMotionCommandBuilder.BuildEnableSlowMotionBlock(
            fps, n, HideHudInGameCommands);
        GameCommandRestoreBlockText = GameSlowMotionCommandBuilder.BuildRestoreBlock(HideHudInGameCommands);
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        if (_dialogs.TryPickFfmpegExecutable(out var path) && path is not null)
        {
            FfmpegPath = path;
            PersistNow();
        }
    }

    [RelayCommand]
    private void BrowseVideoOutput()
    {
        if (_dialogs.TryPickFolder("选择成片输出目录", VideoOutputDirectory, out var folder) && folder is not null)
        {
            VideoOutputDirectory = folder;
            PersistNow();
        }
    }

    public void RefreshPathsStatus()
    {
        var settings = ToSettings();

        if (FfmpegLocator.TryResolve(settings, out var ff, out var ffError))
        {
            FfmpegPathStatusText = $"FFmpeg：{ff}";
            FfmpegPathStatusBrush = Brushes.ForestGreen;
        }
        else
        {
            FfmpegPathStatusText = ffError ?? "FFmpeg 未配置";
            FfmpegPathStatusBrush = Brushes.IndianRed;
        }
    }

    private static int ParseCrf(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var crf)
            ? Math.Clamp(crf, 0, 51)
            : 18;

    private static double ParseExposure(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exposure)
            ? Math.Clamp(exposure, 0.05, 1.0)
            : 0.5;

    private static bool IsPersistedSettingProperty(string propertyName) =>
        propertyName switch
        {
            nameof(ObsCaptureFramerate) or
            nameof(SupersamplingMultiplier) or
            nameof(FfmpegPath) or
            nameof(VideoEncoder) or
            nameof(CompositionBackend) or
            nameof(CrfText) or
            nameof(ExposureText) or
            nameof(VideoOutputDirectory) or
            nameof(MaxParallelGpuJobs) or
            nameof(HideHudInGameCommands) => true,
            _ => false,
        };

    private static string? NullIfEmpty(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
