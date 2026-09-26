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
    private const double MaxTargetBitrateMbps = 120.0;
    private const int KsfMaximumBlurSupersampling = 60;
    private const double KsfMaximumBlurShutterAngle = 360.0;

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
    [ObservableProperty] private int foregroundCaptureFpsLimit = ProjectConstants.DefaultForegroundCaptureFpsLimit;
    [ObservableProperty] private string diskSafetySummary = string.Empty;

    // ---- Quality pipeline ----
    [ObservableProperty] private MotionBlurWeightMode motionBlurWeightMode;
    [ObservableProperty] private double shutterAngle = 270;
    [ObservableProperty] private double intermediateTargetBitrateMbps;
    [ObservableProperty] private bool enableDaVinci4KWorkflowGuide;
    [ObservableProperty] private string selectedPresetId = VideoProcessingPresetIds.Off;
    [ObservableProperty] private string processingSummary = string.Empty;
    [ObservableProperty] private bool isQualityPreviewRunning;
    [ObservableProperty] private int selectedQualityPreviewStageIndex;
    [ObservableProperty] private string qualityPreviewSearchText = string.Empty;
    [ObservableProperty] private ReplayPreviewItem? selectedQualityPreviewReplay;
    [ObservableProperty] private string qualityPreviewSlowMotionSourcePath = string.Empty;
    [ObservableProperty] private string qualityPreviewProcessedPath = string.Empty;
    [ObservableProperty] private string qualityPreviewPath = string.Empty;
    [ObservableProperty] private string qualityPreviewStatus = "搜索游戏回放，选择一条后获取 6 秒真实高采样预览。";
    [ObservableProperty] private string previewBacklogValueText = "—";
    [ObservableProperty] private string previewBacklogSubText = "等待采样";
    [ObservableProperty] private string previewEncoderRateText = "—";
    [ObservableProperty] private string previewEncoderRateSubText = "等待采样";
    [ObservableProperty] private string previewConsumptionValueText = "—";
    [ObservableProperty] private string previewConsumptionSubText = "等待采样";
    [ObservableProperty] private string previewDiskValueText = "—";
    [ObservableProperty] private string previewDiskSubText = "等待采样";
    [ObservableProperty] private string previewEtaValueText = "—";
    [ObservableProperty] private string previewEtaSubText = "开始抓取后估算";
    [ObservableProperty] private string davinciGuideText = string.Empty;

    public IReadOnlyList<CaptureMode> CaptureModeOptions { get; } = [CaptureMode.Tga, CaptureMode.Obs];
    public IReadOnlyList<int> ObsCaptureFramerateOptions { get; } =
        ProjectConstants.SupportedObsCaptureFramerates;
    public IReadOnlyList<MotionBlurWeightMode> MotionBlurModeOptions { get; } =
        [MotionBlurWeightMode.LegacyGaussianExposure, MotionBlurWeightMode.ShutterAngle];
    public IReadOnlyList<VideoProcessingPresetDefinition> PresetOptions { get; } =
        VideoProcessorCatalog.Presets;
    public ObservableCollection<QualityModuleViewModel> QualityModules { get; } = [];
    public ObservableCollection<PreviewReplayTreeNode> QualityPreviewReplayTree { get; } = [];
    public event Action<string>? QualityPreviewPlaybackRequested;

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
    public string BitrateSummaryText => IntermediateTargetBitrateMbps > 0
        ? $"目标 {IntermediateTargetBitrateMbps:0.#} Mbps（数值越大，目标画质越高）"
        : "自动（估算 ≈ 42 Mbps）";

    public string PreviewCaptureSettingsText =>
        $"生成上限 {(ForegroundCaptureFpsLimit == 0 ? "不覆盖" : $"{ForegroundCaptureFpsLimit} fps")} · " +
        $"编码码率 {(IntermediateTargetBitrateMbps > 0 ? $"{IntermediateTargetBitrateMbps:0.#} Mbps" : "自动")} · " +
        $"阶段2并行 {Math.Clamp(MaxParallelJobs, 1, 6)} 路 · 时间采样 3600 fps";

    /// <summary>「成片目录 …—— 可从合成页直接打开」。</summary>
    public string OutputDirectoryText => string.IsNullOrWhiteSpace(VideoOutputDirectory)
        ? "成片目录未配置 —— 请先在「捕获与合成」中设置"
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

    /// <summary>
    /// Applies an explicit Source Video Render-inspired offline capture profile:
    /// 60 output fps × 60 samples and a full-frame shutter. KSF publishes videos
    /// rendered with SVR, but does not publish one canonical channel profile, so
    /// this is deliberately labelled "style" instead of claiming exact parity.
    /// Extra quality filters stay off because SVR itself does not add effects.
    /// </summary>
    [RelayCommand]
    private void ApplyKsfMaximumMotionBlur()
    {
        if (!IsTgaMode)
        {
            StatusText = "KSF 极致运动模糊仅适用于 TGA 离线录制；请先切换到 TGA 模式。";
            return;
        }

        _suppressPersist = true;
        try
        {
            SupersamplingMultiplier = KsfMaximumBlurSupersampling;
            Exposure = 1.0;
            MotionBlurWeightMode = MotionBlurWeightMode.ShutterAngle;
            ShutterAngle = KsfMaximumBlurShutterAngle;
            IntermediateTargetBitrateMbps = MaxTargetBitrateMbps;

            var processing = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.Off);
            RebuildQualityModules(processing);
            RefreshQualityState(processing);
        }
        finally
        {
            _suppressPersist = false;
        }

        Persist();
        StatusText = "已应用 KSF 风格·极致运动模糊：60×、360°、60 fps、120 Mbps；额外画质滤镜关闭。";
    }

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
        if (!_loading && !HasQualityPreviewSlowMotionSource)
            RestoreCompletedQualityPreview(_settings);
    }
    partial void OnRamDiskWatchDirectoryChanged(string value) => Persist();
    partial void OnGameRootPathChanged(string value) => Persist();
    partial void OnMovieSequenceNameChanged(string value) => Persist();
    partial void OnStartMovieHotkeyChanged(string value) => Persist();
    partial void OnEndMovieHotkeyChanged(string value) => Persist();
    partial void OnHideHudInCfgChanged(bool value) => Persist();
    partial void OnMaxParallelJobsChanged(int value)
    {
        OnPropertyChanged(nameof(PreviewCaptureSettingsText));
        Persist();
    }

    partial void OnForegroundCaptureFpsLimitChanged(int value)
    {
        var normalized = SettingsMigration.NormalizeForegroundCaptureFpsLimit(value);
        if (normalized != value)
        {
            ForegroundCaptureFpsLimit = normalized;
            return;
        }
        OnPropertyChanged(nameof(PreviewCaptureSettingsText));
        Persist();
    }

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

    partial void OnIntermediateTargetBitrateMbpsChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, MaxTargetBitrateMbps);
        var normalized = clamped > 0 && clamped < 1
            ? 1
            : Math.Round(clamped, 1);
        if (Math.Abs(normalized - value) > 0.0001)
        {
            IntermediateTargetBitrateMbps = normalized;
            return;
        }
        OnPropertyChanged(nameof(BitrateSummaryText));
        OnPropertyChanged(nameof(PreviewCaptureSettingsText));
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
            ForegroundCaptureFpsLimit = SettingsMigration.NormalizeForegroundCaptureFpsLimit(s.ForegroundCaptureFpsLimit);

            MotionBlurWeightMode = s.MotionBlurWeightMode;
            ShutterAngle = SettingsMigration.NormalizeShutterAngle(s.ShutterAngle);
            IntermediateTargetBitrateMbps = Math.Clamp(
                s.IntermediateTargetBitrate / 1_000_000.0,
                0,
                MaxTargetBitrateMbps);
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
        RestoreCompletedQualityPreview(s);
    }

    private void RestoreCompletedQualityPreview(UserSettings settings)
    {
        var slowMotionSource = ExistingFileOrEmpty(settings.LastQualityPreviewSlowMotionSourcePath);
        if (slowMotionSource.Length == 0)
            slowMotionSource = FindLatestCompletedStageOnePreview(settings.VideoOutputDirectory);

        var processed = ExistingFileOrEmpty(settings.LastQualityPreviewProcessedPath);
        QualityPreviewSlowMotionSourcePath = slowMotionSource;
        QualityPreviewProcessedPath = processed;
        QualityPreviewPath = slowMotionSource.Length > 0 ? slowMotionSource : processed;

        if (QualityPreviewPath.Length == 0)
            return;

        QualityPreviewStatus = slowMotionSource.Length > 0
            ? $"已恢复最近完成的阶段 1 底片：{Path.GetFileName(slowMotionSource)}"
            : $"已恢复最近完成的阶段 2 预览：{Path.GetFileName(processed)}";

        // Old settings did not persist preview paths. Once a completed stage-1
        // file is recovered by its dedicated filename, retain that exact path.
        if (!string.Equals(settings.LastQualityPreviewSlowMotionSourcePath, slowMotionSource, StringComparison.OrdinalIgnoreCase))
        {
            settings.LastQualityPreviewSlowMotionSourcePath = slowMotionSource;
            _store.Save(settings);
        }
    }

    private static string ExistingFileOrEmpty(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : string.Empty;

    private static string FindLatestCompletedStageOnePreview(string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            return string.Empty;

        try
        {
            return Directory.EnumerateFiles(outputDirectory, "quality-preview-stage1-*.mp4", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault() ?? string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
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

    public bool HasQualityPreview => !string.IsNullOrWhiteSpace(QualityPreviewPath) && File.Exists(QualityPreviewPath);
    public bool HasNoQualityPreview => !HasQualityPreview;
    public bool HasQualityPreviewSlowMotionSource =>
        !string.IsNullOrWhiteSpace(QualityPreviewSlowMotionSourcePath) && File.Exists(QualityPreviewSlowMotionSourcePath);

    private bool CanCreateQualityPreview() =>
        !IsQualityPreviewRunning && SelectedQualityPreviewReplay is not null;

    partial void OnIsQualityPreviewRunningChanged(bool value)
    {
        CreateQualityPreviewCommand.NotifyCanExecuteChanged();
        UpdateQualityPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedQualityPreviewReplayChanged(ReplayPreviewItem? value) =>
        CreateQualityPreviewCommand.NotifyCanExecuteChanged();

    partial void OnQualityPreviewSlowMotionSourcePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasQualityPreviewSlowMotionSource));
        UpdateQualityPreviewCommand.NotifyCanExecuteChanged();
        ShowQualityPreviewSourceCommand.NotifyCanExecuteChanged();
    }

    partial void OnQualityPreviewProcessedPathChanged(string value) =>
        ShowQualityPreviewResultCommand.NotifyCanExecuteChanged();

    partial void OnQualityPreviewPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasQualityPreview));
        OnPropertyChanged(nameof(HasNoQualityPreview));
        OpenQualityPreviewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SearchQualityPreviewReplaysAsync()
    {
        if (IsQualityPreviewRunning)
            return;

        QualityPreviewStatus = "正在扫描 Momentum 回放…";
        try
        {
            var gameRoot = GameRootPath?.Trim() ?? string.Empty;
            var query = QualityPreviewSearchText?.Trim() ?? string.Empty;
            var result = await Task.Run(() => new ReplayCatalogService().Scan(gameRoot));
            var matches = result.Records
                .Where(record => record.IsCompatible)
                .Where(record => query.Length == 0
                    || record.MapName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || record.PlayerName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || Path.GetFileName(record.FilePath).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(record => record.RecordedAt)
                .Take(100)
                .ToList();

            QualityPreviewReplayTree.Clear();
            foreach (var item in BuildQualityPreviewReplayTree(matches))
                QualityPreviewReplayTree.Add(item);
            SelectedQualityPreviewReplay = null;
            QualityPreviewStatus = matches.Count == 0
                ? $"没有找到匹配的可用回放；扫描问题 {result.Issues.Count} 条。"
                : $"找到 {matches.Count} 条回放，已默认选择最新一条。";
        }
        catch (Exception ex)
        {
            QualityPreviewStatus = $"搜索回放失败：{ex.Message}";
        }
    }

    private static IReadOnlyList<PreviewReplayTreeNode> BuildQualityPreviewReplayTree(
        IReadOnlyList<ReplayRecord> records)
    {
        var result = new List<PreviewReplayTreeNode>();
        foreach (var mapGroup in records.GroupBy(x => x.MapName, StringComparer.OrdinalIgnoreCase))
        {
            var map = new PreviewReplayTreeNode(mapGroup.Key, "Folder24") { IsExpanded = true };
            foreach (var playerGroup in mapGroup.GroupBy(x => x.PlayerName, StringComparer.CurrentCultureIgnoreCase))
            {
                var player = new PreviewReplayTreeNode(playerGroup.Key, "Person24") { IsExpanded = true };
                foreach (var trackGroup in playerGroup.GroupBy(x => x.TrackNumber).OrderBy(x => x.Key))
                {
                    var staged = trackGroup.Any(x => x.StageNumber > 1);
                    foreach (var stageGroup in trackGroup.GroupBy(x => staged ? x.StageNumber : 0).OrderBy(x => x.Key))
                    {
                        var trackLabel = trackGroup.Key == 1 ? "主赛道" : $"Bonus {trackGroup.Key - 1}";
                        var track = new PreviewReplayTreeNode(
                            staged ? $"{trackLabel} · 阶段 {stageGroup.Key}" : trackLabel,
                            "Map24") { IsExpanded = true };
                        foreach (var record in stageGroup.OrderBy(x => x.RunTimeSeconds).ThenByDescending(x => x.RecordedAt))
                        {
                            track.Children.Add(new PreviewReplayTreeNode(
                                $"{record.RunTimeSeconds:0.0}s · {record.RecordedAt.LocalDateTime:MM-dd HH:mm}",
                                "Document24",
                                new ReplayPreviewItem(record)));
                        }
                        player.Children.Add(track);
                    }
                }
                map.Children.Add(player);
            }
            result.Add(map);
        }
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanCreateQualityPreview))]
    private async Task CreateQualityPreviewAsync()
    {
        SelectedQualityPreviewStageIndex = 0;
        var blocker = CaptureModeSwitchBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            QualityPreviewStatus = $"暂时不能获取预览：{blocker}";
            return;
        }

        IsQualityPreviewRunning = true;
        QualityPreviewStatus = "正在准备预览…";
        ResetQualityPreviewTelemetry();
        try
        {
            var replay = SelectedQualityPreviewReplay?.Record
                ?? throw new InvalidOperationException("请先搜索并选择一条回放。");
            var progress = new Progress<QualityPreviewService.PreviewProgress>(p =>
            {
                QualityPreviewStatus = p.Total > 0
                    ? $"{p.Stage} {p.Done}/{p.Total}"
                    : p.Stage;
                UpdateQualityPreviewTelemetry(p, replay.RunTimeSeconds);
            });
            var rawSource = await new ReplayQualityPreviewCaptureService().CaptureAsync(
                replay,
                Snapshot(),
                progress,
                CancellationToken.None);
            QualityPreviewSlowMotionSourcePath = rawSource;
            ReloadQualityPreview(rawSource);
            Persist();
            QualityPreviewStatus =
                $"阶段 1 完成：60× 慢放、60fps 底片已保存到 {rawSource}。可直接预览，或执行阶段 2 并行合成。";
        }
        catch (Exception ex)
        {
            QualityPreviewStatus = $"预览失败：{ex.Message}";
        }
        finally
        {
            IsQualityPreviewRunning = false;
        }
    }

    private void ResetQualityPreviewTelemetry()
    {
        PreviewBacklogValueText = "—";
        PreviewBacklogSubText = "等待采样";
        PreviewEncoderRateText = "—";
        PreviewEncoderRateSubText = "等待采样";
        PreviewConsumptionValueText = "—";
        PreviewConsumptionSubText = "等待采样";
        PreviewDiskValueText = "—";
        PreviewDiskSubText = "等待采样";
        PreviewEtaValueText = "—";
        PreviewEtaSubText = "开始抓取后估算";
    }

    private void UpdateQualityPreviewTelemetry(
        QualityPreviewService.PreviewProgress progress,
        double replayDurationSeconds)
    {
        if (progress.Performance is not { } performance)
            return;

        PreviewBacklogValueText = $"{performance.Backlog.PendingFrames} 帧";
        PreviewBacklogSubText =
            $"{performance.Backlog.PendingBytes / 1024d / 1024d:0.0} MiB · {performance.BacklogTrend}";
        PreviewEncoderRateText = $"{performance.OutputFramesPerSecond:0.0} fps";
        PreviewEncoderRateSubText = $"{performance.QualityBackend} · {performance.EncoderBackend}";
        PreviewConsumptionValueText = $"{performance.ConsumptionRatio:0.00}";
        PreviewConsumptionSubText =
            $"生产 {performance.ProducedFramesPerSecond:0.0} / 消费 {performance.ConsumedFramesPerSecond:0.0} fps";

        if (progress.Disk is { } disk)
        {
            PreviewDiskValueText = $"{disk.FreePercent:0}%";
            PreviewDiskSubText = $"安全 {disk.SafetyPercent}% · {disk.State}";
        }

        var targetFrames = CaptureEnvelopeRecorder.ComputeEnvelopeFrameCount(
            Math.Min(6, Math.Max(0.5, replayDurationSeconds)),
            ReplayQualityPreviewCaptureService.PreviewSupersamplingMultiplier);
        var remainingFrames = Math.Max(0, targetFrames - performance.ConsumedFrames);
        if (performance.ConsumedFramesPerSecond >= 1)
        {
            var remaining = TimeSpan.FromSeconds(remainingFrames / performance.ConsumedFramesPerSecond);
            PreviewEtaValueText = remaining.TotalMinutes >= 1
                ? $"约 {(int)remaining.TotalMinutes}分 {remaining.Seconds:D2}秒"
                : $"约 {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}秒";
            PreviewEtaSubText = $"剩余 {remainingFrames:N0} 帧";
        }
        else
        {
            PreviewEtaValueText = "采样中";
            PreviewEtaSubText = "等待稳定消费速度";
        }
    }

    private bool CanUpdateQualityPreview() =>
        !IsQualityPreviewRunning && HasQualityPreviewSlowMotionSource;

    [RelayCommand(CanExecute = nameof(CanUpdateQualityPreview))]
    private async Task UpdateQualityPreviewAsync()
    {
        SelectedQualityPreviewStageIndex = 1;
        IsQualityPreviewRunning = true;
        try
        {
            var progress = new Progress<QualityPreviewService.PreviewProgress>(p =>
            {
                QualityPreviewStatus = p.Total > 0
                    ? $"{p.Stage} {p.Done}/{p.Total}"
                    : p.Stage;
            });
            await SynthesizeQualityPreviewAsync(progress, SelectedQualityPreviewReplay?.Record.MapName ?? "缓存素材");
        }
        catch (Exception ex)
        {
            QualityPreviewStatus = $"更新预览失败：{ex.Message}";
        }
        finally
        {
            IsQualityPreviewRunning = false;
        }
    }

    private async Task SynthesizeQualityPreviewAsync(
        IProgress<QualityPreviewService.PreviewProgress> progress,
        string sourceLabel)
    {
        if (!HasQualityPreviewSlowMotionSource)
            throw new FileNotFoundException("尚未从游戏获取 60× 慢放预览素材。");

        var settings = Snapshot();
        var output = await new QualityPreviewService().CreateFromSlowMotionSourceAsync(
            QualityPreviewSlowMotionSourcePath,
            settings,
            progress,
            CancellationToken.None);
        QualityPreviewProcessedPath = output;
        ReloadQualityPreview(output);
        Persist();
        QualityPreviewStatus =
            $"预览已更新：{sourceLabel} · 60×慢放底片 · 当前 N={settings.SupersamplingMultiplier} · " +
            (settings.MotionBlurWeightMode == MotionBlurWeightMode.ShutterAngle
                ? $"快门 {settings.ShutterAngle:0}°"
                : $"Exposure {settings.Exposure:0.##}");
    }

    [RelayCommand]
    private void ShowQualityPreviewSource()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择阶段 1 预览视频",
            Filter = "视频|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|所有文件|*.*",
            Multiselect = false,
        };
        var currentDirectory = Path.GetDirectoryName(QualityPreviewSlowMotionSourcePath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            dialog.InitialDirectory = currentDirectory;
        else if (Directory.Exists(VideoOutputDirectory))
            dialog.InitialDirectory = VideoOutputDirectory;

        if (dialog.ShowDialog() != true)
            return;

        var selected = Path.GetFullPath(dialog.FileName);
        if (!string.Equals(QualityPreviewSlowMotionSourcePath, selected, StringComparison.OrdinalIgnoreCase))
            QualityPreviewProcessedPath = string.Empty;
        QualityPreviewSlowMotionSourcePath = selected;
        ReloadQualityPreview(selected);
        Persist();
        QualityPreviewStatus = $"正在预览阶段 1：{Path.GetFileName(selected)}";
    }

    private bool CanShowQualityPreviewResult() =>
        !string.IsNullOrWhiteSpace(QualityPreviewProcessedPath) && File.Exists(QualityPreviewProcessedPath);

    [RelayCommand(CanExecute = nameof(CanShowQualityPreviewResult))]
    private void ShowQualityPreviewResult()
    {
        ReloadQualityPreview(QualityPreviewProcessedPath);
        QualityPreviewStatus = "正在预览阶段 2：按当前参数生成的60fps结果。";
    }

    private void ReloadQualityPreview(string path)
    {
        QualityPreviewPath = path;
        QualityPreviewPlaybackRequested?.Invoke(path);
    }

    private bool CanOpenQualityPreview() => HasQualityPreview;

    [RelayCommand(CanExecute = nameof(CanOpenQualityPreview))]
    private void OpenQualityPreview()
    {
        var path = Path.GetFullPath(QualityPreviewPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            ArgumentList = { "/select,", path },
        });
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
            LastQualityPreviewSlowMotionSourcePath = QualityPreviewSlowMotionSourcePath,
            LastQualityPreviewProcessedPath = QualityPreviewProcessedPath,
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
            ForegroundCaptureFpsLimit = SettingsMigration.NormalizeForegroundCaptureFpsLimit(ForegroundCaptureFpsLimit),
            MotionBlurWeightMode = MotionBlurWeightMode,
            ShutterAngle = SettingsMigration.NormalizeShutterAngle(ShutterAngle),
            IntermediateTargetBitrate = (int)Math.Round(
                Math.Clamp(IntermediateTargetBitrateMbps, 0, MaxTargetBitrateMbps) * 1_000_000.0,
                MidpointRounding.AwayFromZero),
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

public sealed class ReplayPreviewItem
{
    public ReplayPreviewItem(ReplayRecord record) => Record = record;
    public ReplayRecord Record { get; }
    public string DisplayText =>
        $"{Record.MapName} · {Record.PlayerName} · {Record.TrackLabel}" +
        (Record.StageNumber > 0 ? $" 阶段 {Record.StageNumber}" : string.Empty) +
        $" · {Record.RunTimeSeconds:0.0}s · {Record.RecordedAt.LocalDateTime:MM-dd HH:mm}";
}

public sealed class PreviewReplayTreeNode
{
    public PreviewReplayTreeNode(string label, string icon, ReplayPreviewItem? replay = null)
    {
        Label = label;
        Icon = icon;
        Replay = replay;
    }

    public string Label { get; }
    public string Icon { get; }
    public ReplayPreviewItem? Replay { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<PreviewReplayTreeNode> Children { get; } = [];
}
