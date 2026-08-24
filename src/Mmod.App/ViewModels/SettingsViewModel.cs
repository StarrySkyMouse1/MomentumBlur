using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace Mmod.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsStore _store;
    private UserSettings _settings;
    private bool _loading;

    public SettingsViewModel(UserSettingsStore store)
    {
        _store = store;
        _settings = _store.Load();
        ApplyFrom(_settings);
    }

    [ObservableProperty] private CaptureMode captureMode;
    [ObservableProperty] private int supersamplingMultiplier;
    [ObservableProperty] private double exposure;
    [ObservableProperty] private int obsCaptureFramerate;
    [ObservableProperty] private string videoOutputDirectory = string.Empty;
    [ObservableProperty] private string ramDiskWatchDirectory = string.Empty;
    [ObservableProperty] private string gameRootPath = string.Empty;
    [ObservableProperty] private string movieSequenceName = "frame";
    [ObservableProperty] private string startMovieHotkey = "[";
    [ObservableProperty] private string endMovieHotkey = "]";
    [ObservableProperty] private bool hideHudInCfg;
    [ObservableProperty] private int maxParallelJobs = 2;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string slowMotionBlock = string.Empty;
    [ObservableProperty] private string restoreBlock = string.Empty;
    [ObservableProperty] private string junctionState = string.Empty;
    [ObservableProperty] private string cfgCommandBlock = string.Empty;
    [ObservableProperty] private string cfgRestoreCommandBlock = string.Empty;

    // ---- Quality pipeline ----
    [ObservableProperty] private MotionBlurWeightMode motionBlurWeightMode;
    [ObservableProperty] private double shutterAngle = 270;
    [ObservableProperty] private int intermediateTargetBitrate;
    [ObservableProperty] private bool enableDaVinci4KWorkflowGuide;
    [ObservableProperty] private string selectedPresetId = VideoProcessingPresetIds.Off;
    [ObservableProperty] private string processingSummary = string.Empty;
    [ObservableProperty] private string davinciGuideText = string.Empty;

    public IReadOnlyList<CaptureMode> CaptureModeOptions { get; } = [CaptureMode.Tga, CaptureMode.Obs];
    public IReadOnlyList<int> ObsCaptureFramerateOptions { get; } =
        ProjectConstants.SupportedObsCaptureFramerates;
    public IReadOnlyList<MotionBlurWeightMode> MotionBlurModeOptions { get; } =
        [MotionBlurWeightMode.LegacyGaussianExposure, MotionBlurWeightMode.ShutterAngle];
    public IReadOnlyList<VideoProcessingPresetDefinition> PresetOptions { get; } =
        VideoProcessorCatalog.Presets;
    public ObservableCollection<QualityModuleViewModel> QualityModules { get; } = [];

    public bool IsObsMode => CaptureMode == CaptureMode.Obs;
    public bool IsTgaMode => CaptureMode == CaptureMode.Tga;
    public bool IsShutterMode => MotionBlurWeightMode == MotionBlurWeightMode.ShutterAngle;
    public bool IsLegacyMode => MotionBlurWeightMode == MotionBlurWeightMode.LegacyGaussianExposure;
    public bool IsDaVinciGuideEnabled => EnableDaVinci4KWorkflowGuide;

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        OnPropertyChanged(nameof(IsObsMode));
        OnPropertyChanged(nameof(IsTgaMode));
        Persist();
    }

    partial void OnSupersamplingMultiplierChanged(int value) => Persist();
    partial void OnObsCaptureFramerateChanged(int value) => Persist();
    partial void OnVideoOutputDirectoryChanged(string value) => Persist();
    partial void OnRamDiskWatchDirectoryChanged(string value) => Persist();
    partial void OnGameRootPathChanged(string value) => Persist();
    partial void OnMovieSequenceNameChanged(string value) => Persist();
    partial void OnStartMovieHotkeyChanged(string value) => Persist();
    partial void OnEndMovieHotkeyChanged(string value) => Persist();
    partial void OnHideHudInCfgChanged(bool value) => Persist();
    partial void OnMaxParallelJobsChanged(int value) => Persist();

    partial void OnExposureChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.05, 1.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            Exposure = clamped;
            return;
        }

        Persist();
    }

    partial void OnMotionBlurWeightModeChanged(MotionBlurWeightMode value)
    {
        OnPropertyChanged(nameof(IsShutterMode));
        OnPropertyChanged(nameof(IsLegacyMode));
        Persist();
    }

    partial void OnShutterAngleChanged(double value)
    {
        var clamped = Math.Clamp(value, 180.0, 360.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            ShutterAngle = clamped;
            return;
        }
        Persist();
    }

    partial void OnIntermediateTargetBitrateChanged(int value)
    {
        IntermediateTargetBitrate = Math.Clamp(value, 0, 120_000_000);
        Persist();
    }

    partial void OnEnableDaVinci4KWorkflowGuideChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDaVinciGuideEnabled));
        RefreshDaVinciGuide();
        Persist();
    }

    partial void OnSelectedPresetIdChanged(string value)
    {
        if (_loading)
            return;
        if (string.Equals(value, VideoProcessingPresetIds.Custom, StringComparison.Ordinal))
            return; // custom is derived, not applied
        ApplyPreset(value);
    }

    private void ApplyFrom(UserSettings s)
    {
        _loading = true;
        try
        {
            CaptureMode = s.CaptureMode;
            SupersamplingMultiplier = s.SupersamplingMultiplier;
            Exposure = Math.Clamp(s.Exposure, 0.05, 1.0);
            ObsCaptureFramerate = s.ObsCaptureFramerate;
            VideoOutputDirectory = s.VideoOutputDirectory ?? string.Empty;
            RamDiskWatchDirectory = s.RamDiskWatchDirectory ?? string.Empty;
            GameRootPath = s.GameRootPath ?? string.Empty;
            MovieSequenceName = s.MovieSequenceName;
            StartMovieHotkey = s.StartMovieHotkey;
            EndMovieHotkey = s.EndMovieHotkey;
            HideHudInCfg = s.HideHudInCfg;
            MaxParallelJobs = Math.Max(1, s.MaxParallelJobs);

            MotionBlurWeightMode = s.MotionBlurWeightMode;
            ShutterAngle = SettingsMigration.NormalizeShutterAngle(s.ShutterAngle);
            IntermediateTargetBitrate = Math.Clamp(s.IntermediateTargetBitrate, 0, 120_000_000);
            EnableDaVinci4KWorkflowGuide = s.EnableDaVinci4KWorkflowGuide;
        }
        finally
        {
            _loading = false;
        }

        RebuildQualityModules(s.VideoProcessing);
        RefreshDaVinciGuide();
        RefreshDerived();
    }

    private void RebuildQualityModules(VideoProcessingSettings? processing)
    {
        var normalized = VideoProcessorCatalog.Normalize(processing);
        QualityModules.Clear();
        foreach (var def in VideoProcessorCatalog.Modules)
        {
            var config = normalized.Modules.First(m => string.Equals(m.Id, def.Id, StringComparison.Ordinal));
            QualityModules.Add(new QualityModuleViewModel(def, config, OnQualityModuleChanged));
        }
        RefreshQualityState(normalized);
    }

    private void OnQualityModuleChanged()
    {
        var snapshot = BuildProcessingSnapshot();
        RefreshQualityState(snapshot);
        Persist();
    }

    private VideoProcessingSettings BuildProcessingSnapshot()
    {
        var processing = VideoProcessorCatalog.Normalize(_settings.VideoProcessing);
        foreach (var vm in QualityModules)
        {
            var config = processing.Modules.First(m => string.Equals(m.Id, vm.Definition.Id, StringComparison.Ordinal));
            config.Enabled = vm.IsEnabled;
            foreach (var pvm in vm.Parameters)
                config.Parameters[pvm.Parameter.Key] = pvm.Value;
        }
        processing.PresetId = VideoProcessingPresetService.DetectPresetId(processing);
        return processing;
    }

    private void RefreshQualityState(VideoProcessingSettings processing)
    {
        _loading = true;
        try
        {
            SelectedPresetId = processing.PresetId;
        }
        finally
        {
            _loading = false;
        }
        ProcessingSummary = VideoProcessingSummary.Build(processing);
        OnPropertyChanged(nameof(ProcessingSummary));
    }

    private void ApplyPreset(string presetId)
    {
        var processing = VideoProcessingPresetService.Apply(presetId);
        RebuildQualityModules(processing);
        _settings.VideoProcessing = processing;
        _store.Save(_settings);
        RefreshDerived();
        StatusText = $"已应用画质处理预设：{VideoProcessingPresetService.DetectPresetId(processing)}";
    }

    [RelayCommand]
    private void RestoreAllQualityDefaults()
    {
        foreach (var vm in QualityModules)
            vm.RestoreDefaultsCommand.Execute(null);
    }

    [RelayCommand]
    private void CopyDaVinciSteps()
    {
        System.Windows.Clipboard.SetText(DaVinciWorkflowGuideService.BuildGuideText(
            ProjectConstants.FinalOutputFramerate, SupersamplingMultiplier));
        StatusText = "已复制 DaVinci 4K 操作步骤";
    }

    [RelayCommand]
    private void CopyBilibiliExport()
    {
        System.Windows.Clipboard.SetText(DaVinciWorkflowGuideService.BuildBilibiliExportSuggestions());
        StatusText = "已复制 Bilibili 导出建议";
    }

    private void RefreshDaVinciGuide()
    {
        DavinciGuideText = EnableDaVinci4KWorkflowGuide
            ? DaVinciWorkflowGuideService.BuildGuideText(ProjectConstants.FinalOutputFramerate, SupersamplingMultiplier)
            : string.Empty;
    }

    public UserSettings Snapshot()
    {
        var s = new UserSettings
        {
            CaptureMode = CaptureMode,
            SupersamplingMultiplier = Math.Clamp(SupersamplingMultiplier, 1, 64),
            Exposure = Math.Clamp(Exposure, 0.05, 1.0),
            ObsCaptureFramerate = ObsCaptureFramerate,
            VideoOutputDirectory = VideoOutputDirectory?.Trim() ?? string.Empty,
            RamDiskWatchDirectory = RamDiskWatchDirectory?.Trim() ?? string.Empty,
            GameRootPath = GameRootPath?.Trim(),
            MovieSequenceName = MovieSequenceName,
            StartMovieHotkey = StartMovieHotkey,
            EndMovieHotkey = EndMovieHotkey,
            HideHudInCfg = HideHudInCfg,
            MaxParallelJobs = Math.Max(1, MaxParallelJobs),
            RamDiskDriveLetter = _settings.RamDiskDriveLetter,
            StartmoviePathPrefix = _settings.StartmoviePathPrefix,
            PendingTgaWarningCount = _settings.PendingTgaWarningCount,
            MotionBlurWeightMode = MotionBlurWeightMode,
            ShutterAngle = SettingsMigration.NormalizeShutterAngle(ShutterAngle),
            IntermediateTargetBitrate = Math.Clamp(IntermediateTargetBitrate, 0, 120_000_000),
            EnableDaVinci4KWorkflowGuide = EnableDaVinci4KWorkflowGuide,
            VideoProcessing = BuildProcessingSnapshot(),
        };
        WatchDirectoryHelper.EnsureDerivedPaths(s, s.GameRootPath);
        return s;
    }

    private void Persist()
    {
        if (_loading)
            return;

        _settings = Snapshot();
        _store.Save(_settings);
        RefreshDerived();
    }

    public void RefreshDerived()
    {
        var s = Snapshot();
        SlowMotionBlock = GameSlowMotionCommandBuilder.BuildEnableSlowMotionBlock(
            s.ObsCaptureFramerate, s.SupersamplingMultiplier, s.HideHudInCfg);
        RestoreBlock = GameSlowMotionCommandBuilder.BuildRestoreBlock(s.HideHudInCfg);
        CfgCommandBlock = CfgGeneratorService.GameExecCommand;
        CfgRestoreCommandBlock = CfgGeneratorService.BuildRestoreCommand(s);

        if (string.IsNullOrWhiteSpace(s.GameRootPath) || string.IsNullOrWhiteSpace(s.RamDiskWatchDirectory))
        {
            JunctionState = string.Empty;
            return;
        }

        try
        {
            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath, s.RamDiskWatchDirectory);
            JunctionState = MomentumDirectoryLinkService.DescribeLinkState(paths);
        }
        catch (Exception ex)
        {
            JunctionState = ex.Message;
        }
    }

    [RelayCommand]
    private void BrowseVideoOutput() => PickFolder("选择成片输出目录", VideoOutputDirectory, v => VideoOutputDirectory = v);

    [RelayCommand]
    private void BrowseWatchDirectory() => PickFolder("选择 TGA 监视目录", RamDiskWatchDirectory, v => RamDiskWatchDirectory = v);

    [RelayCommand]
    private void BrowseGameRoot() => PickFolder("选择游戏根目录", GameRootPath, v => GameRootPath = v);

    private static void PickFolder(string title, string current, Action<string> apply)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            apply(dialog.FolderName);
    }

    [RelayCommand]
    private void CopyCfgCommand()
    {
        System.Windows.Clipboard.SetText(CfgGeneratorService.GameExecCommand);
        StatusText = "已复制 CFG 指令";
    }

    [RelayCommand]
    private void CopyCfgRestoreCommand()
    {
        System.Windows.Clipboard.SetText(CfgRestoreCommandBlock);
        StatusText = "已复制 CFG 还原指令";
    }

    [RelayCommand]
    private void CopySlowMotion()
    {
        System.Windows.Clipboard.SetText(SlowMotionBlock);
        StatusText = "已复制慢放指令";
    }

    [RelayCommand]
    private void CopyRestore()
    {
        System.Windows.Clipboard.SetText(RestoreBlock);
        StatusText = "已复制恢复指令";
    }

    [RelayCommand]
    private void CreateJunction()
    {
        try
        {
            var s = Snapshot();
            if (string.IsNullOrWhiteSpace(s.GameRootPath) || string.IsNullOrWhiteSpace(s.RamDiskWatchDirectory))
                throw new InvalidOperationException("需要游戏根目录与监视目录。");
            if (!Directory.Exists(s.GameRootPath) || !Directory.Exists(s.RamDiskWatchDirectory))
                throw new InvalidOperationException("游戏根目录或 TGA 监视目录不存在（请确认 ImDisk 已挂载）。");
            if (string.IsNullOrWhiteSpace(s.MovieSequenceName) ||
                string.IsNullOrWhiteSpace(s.StartMovieHotkey) ||
                string.IsNullOrWhiteSpace(s.EndMovieHotkey))
                throw new InvalidOperationException("请填写序列名与快捷键。");

            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath!, s.RamDiskWatchDirectory);
            MomentumDirectoryLinkService.CreateLink(paths, overwriteRamCopy: true);

            var result = CfgGeneratorService.Generate(s, s.GameRootPath!);
            _settings = s;
            _store.Save(_settings);

            System.Windows.Clipboard.SetText(CfgGeneratorService.GameExecCommand);
            StatusText = $"Junction 已创建，{Path.GetFileName(result.CfgFilePath)} 已生成，CFG 指令已复制";
            RefreshDerived();
        }
        catch (Exception ex)
        {
            StatusText = $"创建失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveJunction()
    {
        try
        {
            var s = Snapshot();
            if (string.IsNullOrWhiteSpace(s.GameRootPath))
                throw new InvalidOperationException("请先设置游戏根目录。");
            if (!Directory.Exists(s.GameRootPath))
                throw new InvalidOperationException($"游戏根目录不存在：{s.GameRootPath}");

            // 取消只需游戏根目录；RAM 盘未挂载时仍可删 junction 并还原 _momentum
            var watch = string.IsNullOrWhiteSpace(s.RamDiskWatchDirectory)
                ? (s.RamDiskDriveLetter ?? "R:\\")
                : s.RamDiskWatchDirectory;
            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath!, watch);
            var removed = MomentumDirectoryLinkService.RemoveLink(paths);
            StatusText = removed
                ? "Junction 已取消，_momentum 已还原为 momentum"
                : "无需取消（未发现 junction / _momentum）";
            RefreshDerived();
        }
        catch (Exception ex)
        {
            StatusText = $"取消 Junction 失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenImDisk()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ImDisk", "RamDiskUI.exe");
            if (!File.Exists(exe))
            {
                StatusText = "未找到 ImDisk：请确认 tools/ImDisk 已复制到输出目录。";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            StatusText = "已打开 ImDisk";
        }
        catch (Exception ex)
        {
            StatusText = $"打开 ImDisk 失败：{ex.Message}";
        }
    }
}
