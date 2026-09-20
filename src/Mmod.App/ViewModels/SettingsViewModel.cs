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
    /// <summary>设置页页签索引：仅 OBS 模式可见的页签。</summary>
    private const int ObsOnlySectionIndex = 4;

    /// <summary>设置页页签索引：仅 TGA 模式可见的页签。</summary>
    private const int TgaOnlySectionIndex = 3;

    private readonly UserSettingsStore _store;
    private UserSettings _settings;
    private bool _loading;
    private bool _suppressPersist;

    public SettingsViewModel(UserSettingsStore store)
    {
        _store = store;
        _settings = _store.Load();
        ApplyFrom(_settings);
        NormalizeSettingsSectionForMode();
    }

    /// <summary>
    /// 由壳层注入的只读阻塞原因提供者：返回非空字符串表示当前存在不可安全切换的
    /// 运行/收尾状态（OBS 批处理、TGA 手工管线、无人值守任务）。返回 null 表示可切换。
    /// 这里不修改任何录制状态机，只做只读投影。
    /// </summary>
    public Func<string?>? CaptureModeSwitchBlocker { get; set; }

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
    [ObservableProperty] private int diskSafetyFreePercent = 10;
    [ObservableProperty] private string diskSafetySummary = string.Empty;

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

    // ---- 设计稿呈现层 ----
    [ObservableProperty] private bool isCaptureSectionSelected = true;
    [ObservableProperty] private bool isQualitySectionSelected;
    [ObservableProperty] private bool isPostSectionSelected;
    [ObservableProperty] private bool isTgaSectionSelected;
    [ObservableProperty] private bool isObsSectionSelected;
    [ObservableProperty] private bool isDiskSectionSelected;

    /// <summary>
    /// 设置页当前页签索引（对应 <c>ui:TabView.SelectedIndex</c>）。
    /// 模式切换后若停留在另一模式的专属页签，会被强制回到公共页签，
    /// 否则折叠的页签会让内容区变空。该值只是 UI 位置，不持久化。
    /// </summary>
    [ObservableProperty] private int selectedSettingsSectionIndex;

    /// <summary>滑块刻度中间值「当前 300°」。</summary>
    public string ShutterCurrentText => $"当前 {ShutterAngle:0}°";

    /// <summary>「自动（估算 ≈ 42 Mbps）」/「自定义 12 Mbps」。</summary>
    public string BitrateSummaryText => IntermediateTargetBitrate > 0
        ? $"自定义 {IntermediateTargetBitrate / 1_000_000.0:0.#} Mbps"
        : "自动（估算 ≈ 42 Mbps）";

    /// <summary>「成片目录 …—— 可从合成页直接打开」。</summary>
    public string OutputDirectoryText => string.IsNullOrWhiteSpace(VideoOutputDirectory)
        ? "成片目录未配置 —— 请先在「游戏 TGA」分组中设置"
        : $"成片目录 {VideoOutputDirectory} —— 可从合成页直接打开";

    /// <summary>
    /// 顶层工作模式的唯一写入口（标题栏双态开关与设置页共用）。
    /// 成功 / 同模式点击 / 运行中拒绝 / 保存失败四条路径都要有明确结果，绝不静默改变模式。
    /// </summary>
    [RelayCommand] private void SetTgaMode() => TrySwitchCaptureMode(CaptureMode.Tga);

    /// <inheritdoc cref="SetTgaMode"/>
    [RelayCommand] private void SetObsMode() => TrySwitchCaptureMode(CaptureMode.Obs);

    private void TrySwitchCaptureMode(CaptureMode target)
    {
        // 同模式点击：不重复持久化，也不触发导航/菜单重建。
        if (CaptureMode == target)
            return;

        var blockReason = CaptureModeSwitchBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            _ = Services.DialogServiceLocator.Current.ShowInfoAsync("无法切换工作模式", blockReason);
            return;
        }

        var previous = CaptureMode;
        try
        {
            // 沿用既有保存逻辑：OnCaptureModeChanged → Persist → UserSettingsStore.Save。
            CaptureMode = target;
        }
        catch (Exception ex)
        {
            _suppressPersist = true;
            try
            {
                CaptureMode = previous;
                _settings.CaptureMode = previous;
            }
            finally
            {
                _suppressPersist = false;
            }

            // 尽力把原模式写回磁盘，避免重启后落到一次并未真正生效的切换。
            string persisted;
            try
            {
                _store.Save(_settings);
                persisted = "settings.json 已保持原有模式。";
            }
            catch (Exception saveEx)
            {
                persisted = $"settings.json 可能仍记录了未生效的模式，请检查写入权限：{saveEx.Message}";
            }

            _ = Services.DialogServiceLocator.Current.ShowInfoAsync(
                "切换工作模式失败",
                $"模式未能保存：{ex.Message}\n界面已保持原有模式「{DescribeMode(previous)}」。{persisted}");
        }
    }

    private static string DescribeMode(CaptureMode mode) =>
        mode == CaptureMode.Obs ? "OBS" : "TGA";

    private void NormalizeSettingsSectionForMode()
    {
        var hiddenSectionIndex = CaptureMode == CaptureMode.Obs ? TgaOnlySectionIndex : ObsOnlySectionIndex;
        if (SelectedSettingsSectionIndex == hiddenSectionIndex)
            SelectedSettingsSectionIndex = 0;
    }

    [RelayCommand] private void SetShutterMode() => MotionBlurWeightMode = MotionBlurWeightMode.ShutterAngle;
    [RelayCommand] private void SetLegacyMode() => MotionBlurWeightMode = MotionBlurWeightMode.LegacyGaussianExposure;

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        OnPropertyChanged(nameof(IsObsMode));
        OnPropertyChanged(nameof(IsTgaMode));
        NormalizeSettingsSectionForMode();
        Persist();
    }

    partial void OnSupersamplingMultiplierChanged(int value) => Persist();
    partial void OnObsCaptureFramerateChanged(int value) => Persist();
    partial void OnVideoOutputDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(OutputDirectoryText));
        Persist();
    }
    partial void OnRamDiskWatchDirectoryChanged(string value) => Persist();
    partial void OnGameRootPathChanged(string value) => Persist();
    partial void OnMovieSequenceNameChanged(string value) => Persist();
    partial void OnStartMovieHotkeyChanged(string value) => Persist();
    partial void OnEndMovieHotkeyChanged(string value) => Persist();
    partial void OnHideHudInCfgChanged(bool value) => Persist();
    partial void OnMaxParallelJobsChanged(int value) => Persist();

    partial void OnDiskSafetyFreePercentChanged(int value)
    {
        var normalized = DiskSafetyPolicy.NormalizeSafetyPercent(value);
        if (normalized != value)
        {
            DiskSafetyFreePercent = normalized;
            return;
        }
        RefreshDiskSafetySummary();
        Persist();
    }

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
        OnPropertyChanged(nameof(ShutterCurrentText));
        Persist();
    }

    partial void OnIntermediateTargetBitrateChanged(int value)
    {
        IntermediateTargetBitrate = Math.Clamp(value, 0, 120_000_000);
        OnPropertyChanged(nameof(BitrateSummaryText));
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
            DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(s.DiskSafetyFreePercent);

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
        RefreshDiskSafetySummary();
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
            DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(DiskSafetyFreePercent),
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
        if (_loading || _suppressPersist)
            return;

        _settings = Snapshot();
        _store.Save(_settings);
        RefreshDerived();
    }

    private void RefreshDiskSafetySummary()
    {
        var safety = DiskSafetyPolicy.NormalizeSafetyPercent(DiskSafetyFreePercent);
        DiskSafetySummary = safety == 0
            ? "磁盘空间保护已关闭（不推荐用于无人值守任务）。"
            : $"安全下限 {safety}%；预警线 {DiskSafetyPolicy.CalculateWarningPercent(safety)}%。达到安全下限时受控停止并保留已验证 partial。";
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
