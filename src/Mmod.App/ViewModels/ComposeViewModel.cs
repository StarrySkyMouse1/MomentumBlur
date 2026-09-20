using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;
using Microsoft.Win32;

namespace Mmod.App.ViewModels;

public partial class BatchVideoItem : ObservableObject
{
    public BatchVideoItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool isSelected = true;

    [ObservableProperty]
    private string status = "待处理";

    [ObservableProperty]
    private double progressPercent;
}

public partial class ComposeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly TgaPipelineOrchestrator _tga = new();
    private readonly ObsSynthesisService _obs = new();
    private CancellationTokenSource? _obsCts;

    public ComposeViewModel(SettingsViewModel settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _tga.Changed += () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshTgaUi();
            });
        };
        RefreshModeSummary();
        RefreshDiskSpace();
    }

    public ObservableCollection<BatchVideoItem> BatchItems { get; } = [];

    /// <summary>供 OBS 工作台复用设置页的真实指令复制命令（不复制第二份配置）。</summary>
    public SettingsViewModel Settings => _settings;

    [ObservableProperty]
    private string modeSummary = string.Empty;

    /// <summary>外部 OBS 录制步骤的说明文案，随真实 OBS 源帧率变化。</summary>
    [ObservableProperty]
    private string obsRecordingHint = string.Empty;

    /// <summary>OBS 工作台显示的成片输出目录（未配置时为默认目录）。</summary>
    [ObservableProperty]
    private string obsOutputDirectoryText = string.Empty;

    [ObservableProperty]
    private string statusText = "就绪";

    [ObservableProperty]
    private string tgaMetricsText = "未开始监视";

    [ObservableProperty]
    private string batchSummary = "队列：0";

    [ObservableProperty]
    private string diskSpaceText = "磁盘空间：正在读取…";

    [ObservableProperty]
    private bool isObsBusy;

    // ---- 结构化指标（供卡片内的指标块绑定，替代整段拼接文本） ----

    /// <summary>已喂入帧数。</summary>
    [ObservableProperty]
    private string fedCountText = "0";

    /// <summary>待处理积压帧数。</summary>
    [ObservableProperty]
    private string pendingCountText = "0";

    /// <summary>当前监视目录。</summary>
    [ObservableProperty]
    private string watchDirectoryText = "（未设置）";

    /// <summary>当前输出文件名。</summary>
    [ObservableProperty]
    private string outputNameText = "（尚未创建）";

    /// <summary>
    /// 本会话合成进度的分子：已喂入（= 已提交到原生会话）帧数。
    /// </summary>
    [ObservableProperty]
    private double composedFrames;

    /// <summary>
    /// 本会话合成进度的分母：已喂入 + 待处理。这是「已收帧总数」，不是最终总帧数，
    /// 所以进度条表达的是「当前积压被消化了多少」，而不是整项任务的百分比。
    /// </summary>
    [ObservableProperty]
    private double composedTotal;

    /// <summary>「已合成 84 / 128」文案。</summary>
    [ObservableProperty]
    private string composedProgressText = "已合成 0 / 0";

    /// <summary>合成进度百分比（0–100），无数据时为 0。</summary>
    [ObservableProperty]
    private double composedPercent;

    /// <summary>卡片主标题，随运行态变化。</summary>
    [ObservableProperty]
    private string monitorTitle = "游戏 TGA 监视";

    /// <summary>卡片状态徽标文字。</summary>
    [ObservableProperty]
    private string monitorStateText = "未开始";

    /// <summary>徽标语义等级，直接映射到 ui:InfoBadge 的 Severity。</summary>
    [ObservableProperty]
    private Wpf.Ui.Controls.InfoBadgeSeverity monitorSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Informational;

    /// <summary>监视盘可用空间百分比（0~100）。</summary>
    [ObservableProperty]
    private double diskUsedPercent;

    /// <summary>监视盘可用空间摘要。</summary>
    [ObservableProperty]
    private string diskDetailText = "正在读取…";

    /// <summary>磁盘状态徽标文字。</summary>
    [ObservableProperty]
    private string diskStateText = "读取中";

    /// <summary>磁盘状态徽标语义等级。</summary>
    [ObservableProperty]
    private Wpf.Ui.Controls.InfoBadgeSeverity diskSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Informational;

    /// <summary>状态栏提示语义等级，用于 ui:InfoBar 的 Severity。</summary>
    [ObservableProperty]
    private Wpf.Ui.Controls.InfoBarSeverity statusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;

    public bool IsTgaMode => _settings.CaptureMode == CaptureMode.Tga;
    public bool IsObsMode => _settings.CaptureMode == CaptureMode.Obs;
    public bool IsTgaRunning => _tga.IsRunning;
    public bool CanStartObs => IsObsMode && !IsObsBusy && BatchItems.Any(i => i.IsSelected);

    /// <summary>本页在 OBS 模式下是「录制与处理」工作台。</summary>
    public string PageTitleText => IsObsMode ? "录制与处理" : "合成";

    /// <summary>磁盘卡标题随模式变化：OBS 关注输出盘，TGA 关注 TGA 监视盘。</summary>
    public string DiskCardTitle => IsObsMode ? "输出盘空间" : "监视盘空间";

    /// <summary>
    /// 顶层模式切换的只读安全门：OBS 批处理或 TGA 手工管线处于运行/收尾时拒绝切换。
    /// 只读取既有运行态，不改动任何管道或状态机。
    /// </summary>
    public string? DescribeCaptureModeSwitchBlock()
    {
        if (IsObsBusy)
            return "OBS 批量合成正在运行，请等待当前队列结束或点击「取消」完成收尾后再切换工作模式。";

        if (_tga.IsRunning)
            return "游戏 TGA 监视正在运行，请先「停止并收尾」并等待写盘物理静默完成后再切换工作模式。";

        return null;
    }

    public void RefreshModeSummary()
    {
        var s = _settings.Snapshot();
        var blur = s.MotionBlurWeightMode == MotionBlurWeightMode.ShutterAngle
            ? $"Shutter {s.ShutterAngle:0}°"
            : $"Exposure {s.Exposure:0.##}";
        var processing = VideoProcessingSummary.Build(s.VideoProcessing);
        var davinci = s.EnableDaVinci4KWorkflowGuide ? " · 后续 4K AI" : string.Empty;
        ModeSummary = s.CaptureMode == CaptureMode.Tga
            ? $"TGA · N={s.SupersamplingMultiplier} · {blur} · 60fps · {processing}{davinci}"
            : $"OBS · {s.ObsCaptureFramerate}fps · N={s.SupersamplingMultiplier} · {blur} · {processing}{davinci}";
        OnPropertyChanged(nameof(IsTgaMode));
        OnPropertyChanged(nameof(IsObsMode));
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(DiskCardTitle));
        StartTgaCommand.NotifyCanExecuteChanged();
        StopTgaCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartObs));
        RefreshObsWorkbenchProjection();
        UpdateBatchSummary();
        if (!_tga.IsRunning)
        {
            TgaMetricsText = BuildIdleTgaMetrics(s);
            RefreshTgaMetricTiles();
        }
        UpdateStatusSeverity();
        RefreshDiskSpace();
    }

    /// <summary>
    /// OBS 工作台里所有随真实设置变化的派生文案。指令文本本身由设置页的现有
    /// 构建逻辑提供（<c>SlowMotionBlock</c> / <c>RestoreBlock</c>），这里不复制配置。
    /// </summary>
    private void RefreshObsWorkbenchProjection()
    {
        var s = _settings.Snapshot();
        ObsRecordingHint =
            $"在外部 OBS 中以 {s.ObsCaptureFramerate}fps 录制游戏画面；录制完成后导出 MP4 / MKV 文件，"
            + "再回到本页加入队列做运动模糊、画质与编码处理。本应用不会自动启动、控制或读取 OBS。";

        ObsOutputDirectoryText = string.IsNullOrWhiteSpace(s.VideoOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "mmod_record_next")
            : s.VideoOutputDirectory;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.CaptureMode))
        {
            // 模式切换后标题、磁盘语义、录制提示都要立刻跟上。
            RefreshModeSummary();
            return;
        }

        if (e.PropertyName is nameof(SettingsViewModel.ObsCaptureFramerate))
        {
            RefreshObsWorkbenchProjection();
            return;
        }

        if (e.PropertyName is nameof(SettingsViewModel.VideoOutputDirectory)
            or nameof(SettingsViewModel.RamDiskWatchDirectory)
            or nameof(SettingsViewModel.GameRootPath))
        {
            RefreshObsWorkbenchProjection();
            RefreshDiskSpace();
        }
    }

    private void RefreshDiskSpace()
    {
        try
        {
            var directory = ResolveDisplayedDiskDirectory();
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(driveRoot))
                throw new IOException("无法确定盘符。");

            var drive = new DriveInfo(driveRoot);
            var label = IsTgaMode ? "监视盘空间" : "输出盘空间";
            DiskSpaceText = $"{label}：可用 {FormatGiB(drive.AvailableFreeSpace)} / 共 {FormatGiB(drive.TotalSize)}（{drive.Name.TrimEnd(Path.DirectorySeparatorChar)}）";

            // 结构化填充：已用百分比 + 状态徽标语义。
            var used = drive.TotalSize > 0
                ? (drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize * 100d
                : 0d;
            DiskUsedPercent = Math.Round(used, 1);
            DiskDetailText = $"可用 {FormatGiB(drive.AvailableFreeSpace)} / 共 {FormatGiB(drive.TotalSize)}（{drive.Name.TrimEnd(Path.DirectorySeparatorChar)}）";

            var safety = _settings.DiskSafetyFreePercent;
            var freePercent = 100d - used;
            if (freePercent <= safety)
            {
                DiskStateText = "低于安全线";
                DiskSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Critical;
            }
            else if (freePercent <= safety + 5)
            {
                DiskStateText = "接近安全线";
                DiskSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Caution;
            }
            else
            {
                DiskStateText = "正常";
                DiskSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Success;
            }
        }
        catch
        {
            DiskSpaceText = IsTgaMode ? "监视盘空间：无法读取" : "输出盘空间：无法读取";
            DiskStateText = "无法读取";
            DiskSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Informational;
            DiskDetailText = "无法读取磁盘信息";
            DiskUsedPercent = 0;
        }
    }

    private string ResolveDisplayedDiskDirectory()
    {
        if (IsTgaMode)
        {
            if (_tga.IsRunning && !string.IsNullOrWhiteSpace(_tga.WatchDirectory))
                return _tga.WatchDirectory;

            var settings = _settings.Snapshot();
            return WatchDirectoryHelper.ResolveEffectiveWatchDirectory(settings, settings.GameRootPath);
        }

        return string.IsNullOrWhiteSpace(_settings.VideoOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "mmod_record_next")
            : _settings.VideoOutputDirectory;
    }

    private static string FormatGiB(long bytes) => $"{bytes / 1024d / 1024d / 1024d:N1} GB";

    private void RefreshTgaUi()
    {
        StatusText = _tga.Status;
        TgaMetricsText = BuildRunningTgaMetrics();
        RefreshTgaMetricTiles();
        RefreshDiskSpace();
        UpdateStatusSeverity();
        OnPropertyChanged(nameof(IsTgaRunning));
        StartTgaCommand.NotifyCanExecuteChanged();
        StopTgaCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 由真实状态文案投影 InfoBar 的 Severity：
    /// 错误/失败 → Critical，停止/排空/取消 → Caution，其余 → Informational。
    /// 不伪造状态，只把已有文本分类。
    /// </summary>
    private void UpdateStatusSeverity()
    {
        StatusSeverity = ClassifyStatus(StatusText, _tga.IsFaulted);
    }

    private static Wpf.Ui.Controls.InfoBarSeverity ClassifyStatus(string status, bool faulted)
    {
        if (faulted || status.StartsWith("错误", StringComparison.Ordinal)
                     || status.Contains("失败", StringComparison.Ordinal))
            return Wpf.Ui.Controls.InfoBarSeverity.Error;

        if (status.Contains("停止", StringComparison.Ordinal)
            || status.Contains("取消", StringComparison.Ordinal)
            || status.Contains("排空", StringComparison.Ordinal))
            return Wpf.Ui.Controls.InfoBarSeverity.Warning;

        return Wpf.Ui.Controls.InfoBarSeverity.Informational;
    }

    /// <summary>
    /// 把运行态拆成结构化指标 + 徽标语义，供卡片内的组件直接绑定。
    /// </summary>
    private void RefreshTgaMetricTiles()
    {
        if (_tga.IsRunning)
        {
            FedCountText = _tga.FedCount.ToString("N0");
            PendingCountText = _tga.PendingCount.ToString("N0");
            MonitorTitle = "游戏 TGA 监视中";
            MonitorStateText = "录制中";
            MonitorSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Critical;
            StatusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
        }
        else
        {
            MonitorTitle = "游戏 TGA 监视";
            MonitorStateText = "未开始";
            MonitorSeverity = Wpf.Ui.Controls.InfoBadgeSeverity.Informational;
        }

        // 合成进度：分子 = 已喂入，分母 = 已喂入 + 待处理（当前可见帧总量）。
        var fed = _tga.FedCount;
        var pending = _tga.PendingCount;
        ComposedFrames = fed;
        ComposedTotal = fed + pending;
        ComposedProgressText = $"已合成 {fed} / {fed + pending}";
        ComposedPercent = ComposedTotal > 0 ? Math.Round(100d * fed / ComposedTotal, 1) : 0d;

        WatchDirectoryText = string.IsNullOrWhiteSpace(_tga.WatchDirectory)
            ? string.IsNullOrWhiteSpace(_settings.RamDiskWatchDirectory) ? "（未设置）" : _settings.RamDiskWatchDirectory
            : _tga.WatchDirectory;

        OutputNameText = string.IsNullOrWhiteSpace(_tga.OutputPath)
            ? "（尚未创建）"
            : Path.GetFileName(_tga.OutputPath);
    }

    private string BuildRunningTgaMetrics()
    {
        var watch = string.IsNullOrWhiteSpace(_tga.WatchDirectory) ? "（未设置）" : _tga.WatchDirectory;
        var output = string.IsNullOrWhiteSpace(_tga.OutputPath)
            ? "（尚未创建）"
            : Path.GetFileName(_tga.OutputPath);
        var diag = string.IsNullOrWhiteSpace(_tga.SessionDiagnostics) ? string.Empty : $"\n{_tga.SessionDiagnostics}";
        return
            $"监视目录：{watch}\n" +
            $"已喂入 {_tga.FedCount} 帧，待处理 {_tga.PendingCount}\n" +
            $"输出：{output}{diag}";
    }

    private static string BuildIdleTgaMetrics(UserSettings s)
    {
        try
        {
            var watch = WatchDirectoryHelper.ResolveEffectiveWatchDirectory(s, s.GameRootPath);
            return $"将监视：{watch}\n已喂入 0 帧，待处理 0";
        }
        catch
        {
            return "请先在设置中配置 TGA 监视目录与游戏根目录";
        }
    }

    private bool CanStartTga() => IsTgaMode && !_tga.IsRunning;

    private bool CanStopTga() => IsTgaMode && _tga.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartTga))]
    private async Task StartTgaAsync()
    {
        try
        {
            await _tga.StartAsync(_settings.Snapshot());
            RefreshTgaUi();
            RefreshModeSummary();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Error;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopTga))]
    private async Task StopTgaAsync()
    {
        try
        {
            await _tga.StopAsync();
            StatusText = _tga.Status;
        }
        catch (Exception ex)
        {
            StatusText = $"收尾失败：{ex.Message}";
        }
        RefreshTgaUi();
        RefreshModeSummary();
    }

    [RelayCommand]
    private void AddVideos()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "视频|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
            return;
        foreach (var path in dialog.FileNames)
            AddVideoPath(path);
    }

    public void AddVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        if (BatchItems.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        BatchItems.Add(new BatchVideoItem(path));
        UpdateBatchSummary();
        OnPropertyChanged(nameof(CanStartObs));
    }

    [RelayCommand]
    private void ClearBatch()
    {
        if (IsObsBusy)
            return;
        BatchItems.Clear();
        UpdateBatchSummary();
        OnPropertyChanged(nameof(CanStartObs));
    }

    [RelayCommand]
    private async Task StartObsBatchAsync()
    {
        if (!CanStartObs)
            return;

        IsObsBusy = true;
        _obsCts = new CancellationTokenSource();
        var settings = _settings.Snapshot();
        var outputDir = string.IsNullOrWhiteSpace(settings.VideoOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "mmod_record_next")
            : settings.VideoOutputDirectory;
        Directory.CreateDirectory(outputDir);
        RefreshDiskSpace();

        try
        {
            var selected = BatchItems.Where(i => i.IsSelected).ToList();
            var parallel = Math.Clamp(settings.MaxParallelJobs, 1, 4);
            using var gate = new SemaphoreSlim(parallel, parallel);
            var tasks = selected.Select(async item =>
            {
                await gate.WaitAsync(_obsCts.Token).ConfigureAwait(false);
                try
                {
                    _obsCts.Token.ThrowIfCancellationRequested();
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.Status = "合成中…";
                        item.ProgressPercent = 0;
                    });

                    var output = Path.Combine(
                        outputDir,
                        $"{Path.GetFileNameWithoutExtension(item.Name)}_x{settings.SupersamplingMultiplier}_60fps_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.TickCount & 0xFFFF:x4}.mp4");

                    var progress = new Progress<ObsSynthesisService.Progress>(p =>
                    {
                        var total = Math.Max(1, p.Total);
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            item.ProgressPercent = Math.Clamp(100.0 * p.Done / total, 0, 100);
                            item.Status = $"合成中 {p.Done}/{total}";
                        });
                    });

                    try
                    {
                        await _obs.RunAsync(item.Path, output, settings, progress, _obsCts.Token).ConfigureAwait(false);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            item.Status = File.Exists(output) ? $"完成：{Path.GetFileName(output)}" : "完成（无文件？）";
                            item.ProgressPercent = 100;
                            RefreshDiskSpace();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => item.Status = "已取消");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => item.Status = $"失败：{ex.Message}");
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
            StatusText = "OBS 批量处理结束";
        }
        catch (OperationCanceledException)
        {
            StatusText = "OBS 批量已取消";
        }
        finally
        {
            IsObsBusy = false;
            _obsCts.Dispose();
            _obsCts = null;
            OnPropertyChanged(nameof(CanStartObs));
        }
    }

    [RelayCommand]
    private void CancelObsBatch()
    {
        _obsCts?.Cancel();
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = string.IsNullOrWhiteSpace(_settings.VideoOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "mmod_record_next")
            : _settings.VideoOutputDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void UpdateBatchSummary()
    {
        var selected = BatchItems.Count(i => i.IsSelected);
        BatchSummary = $"队列：{BatchItems.Count} 个文件，已选 {selected} 个";
    }

    public async ValueTask DisposeAsync()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _obsCts?.Cancel();
        await _tga.DisposeAsync();
    }
}
