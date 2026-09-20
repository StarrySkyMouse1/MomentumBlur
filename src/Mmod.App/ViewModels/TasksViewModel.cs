using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;
using System.Windows.Threading;

namespace Mmod.App.ViewModels;

public partial class TasksViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly ReplayCatalogService _catalog = new();
    private readonly RenderTaskRepository _repository = new();
    private readonly RenderTaskRunner _runner;
    private readonly DispatcherTimer _runnerUiTimer;
    private int _runnerUiTicks;

    public ObservableCollection<ReplayTreeNode> Catalog { get; } = [];
    public ObservableCollection<ReplayTreeNode> CatalogView { get; } = [];
    public ObservableCollection<TaskListItem> Queue { get; } = [];
    public ObservableCollection<TaskListItem> History { get; } = [];
    public ObservableCollection<TaskNodeItem> DetailNodes { get; } = [];
    public ObservableCollection<TaskLogItem> DetailLogs { get; } = [];
    [ObservableProperty] private string statusText = "请刷新回放记录并勾选需要执行的记录。";
    /// <summary>
    /// 任务页常驻状态条（ui:InfoBar）的语义等级。
    /// 与合成页一致：只把已有状态文案分类，不伪造状态。
    /// </summary>
    [ObservableProperty] private Wpf.Ui.Controls.InfoBarSeverity statusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
    [ObservableProperty] private TaskListItem? selectedTask;
    [ObservableProperty] private string selectedTaskDetail = "选择任务后查看节点和日志。";
    [ObservableProperty] private string runtimeText = "当前没有正在录制的节点。";

    // ---- 设计稿呈现层 ----
    [ObservableProperty] private string catalogFilter = string.Empty;
    [ObservableProperty] private string catalogCountText = "可执行 0 · 旧版 0";
    [ObservableProperty] private string selectionCountText = "尚未勾选";
    [ObservableProperty] private bool isQueueTabSelected = true;
    [ObservableProperty] private string detailStateText = "未选择任务";
    [ObservableProperty] private TaskPresentationState detailState = TaskPresentationState.Idle;
    [ObservableProperty] private string frozenComposeText = "选择任务后显示冻结的合成参数。";
    [ObservableProperty] private string frozenQualityText = string.Empty;

    // ---- 空态可见性（画板 04 · ③）----
    /// <summary>回放树为空（刷新后无可执行记录）。</summary>
    [ObservableProperty] private bool isCatalogEmpty = true;
    /// <summary>执行队列为空。</summary>
    [ObservableProperty] private bool isQueueEmpty = true;
    /// <summary>历史为空。</summary>
    [ObservableProperty] private bool isHistoryEmpty = true;
    /// <summary>未选择任务时详情页显示占位。</summary>
    [ObservableProperty] private bool isDetailEmpty = true;

    // ---- 页签计数（画板 03「执行队列 ③」）----
    /// <summary>执行队列中的任务数，驱动页签徽标。</summary>
    [ObservableProperty] private int queueCount;

    // ---- 遥测指标块 ----
    [ObservableProperty] private string backlogValueText = "—";
    [ObservableProperty] private string backlogSubText = "等待采样";
    [ObservableProperty] private string consumptionRatioText = "—";
    [ObservableProperty] private string consumptionSubText = "等待采样";
    [ObservableProperty] private TaskPresentationState consumptionRatioSeverity = TaskPresentationState.Idle;
    [ObservableProperty] private string diskFreeText = "—";
    [ObservableProperty] private string diskSubText = "等待采样";
    // 编码速率：真实来源是原生 frames_output 计数（OutputFramesPerSecond）。
    [ObservableProperty] private string encoderRateText = "—";
    [ObservableProperty] private string encoderRateSubText = "等待采样";

    public bool IsHistoryTabSelected
    {
        get => !IsQueueTabSelected;
        set { if (value) IsQueueTabSelected = false; }
    }

    partial void OnIsQueueTabSelectedChanged(bool value) => OnPropertyChanged(nameof(IsHistoryTabSelected));

    /// <summary>任务详情的状态徽标语义等级（ui:InfoBadge 的 Severity）。</summary>
    public Wpf.Ui.Controls.InfoBadgeSeverity DetailSeverity => DetailState switch
    {
        TaskPresentationState.Running => Wpf.Ui.Controls.InfoBadgeSeverity.Attention,
        TaskPresentationState.Paused => Wpf.Ui.Controls.InfoBadgeSeverity.Caution,
        TaskPresentationState.Success => Wpf.Ui.Controls.InfoBadgeSeverity.Success,
        TaskPresentationState.Danger => Wpf.Ui.Controls.InfoBadgeSeverity.Critical,
        TaskPresentationState.Consuming => Wpf.Ui.Controls.InfoBadgeSeverity.Success,
        _ => Wpf.Ui.Controls.InfoBadgeSeverity.Informational,
    };

    partial void OnDetailStateChanged(TaskPresentationState value) => OnPropertyChanged(nameof(DetailSeverity));

    [RelayCommand] private void ShowQueueTab() => IsQueueTabSelected = true;
    [RelayCommand] private void ShowHistoryTab() => IsQueueTabSelected = false;

    partial void OnCatalogFilterChanged(string value) => ApplyCatalogFilter();

    private void ApplyCatalogFilter()
    {
        CatalogView.Clear();
        var query = CatalogFilter?.Trim() ?? string.Empty;
        foreach (var map in Catalog)
        {
            if (query.Length == 0)
            {
                CatalogView.Add(map);
                continue;
            }
            if (map.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                CatalogView.Add(map);
                continue;
            }
            var mapClone = map.CloneFiltered(query);
            if (mapClone is not null)
                CatalogView.Add(mapClone);
        }
        IsCatalogEmpty = CatalogView.Count == 0;
        CatalogEmptyHint = query.Length == 0
            ? "启动合成后，完成的片段会出现在这里"
            : $"没有与「{query}」匹配的回放记录";
    }

    /// <summary>空态说明文字：区分「还没有记录」与「筛选无结果」。</summary>
    [ObservableProperty] private string catalogEmptyHint = "启动合成后，完成的片段会出现在这里";

    public TasksViewModel(SettingsViewModel settings)
    {
        _settings = settings; _runner = new RenderTaskRunner(_repository);
        ReplayTreeNode.SelectionChanged += UpdateSelectionCount;
        _runnerUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _runnerUiTimer.Tick += (_, _) => UpdateRunnerProjection();
        _runner.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_runnerUiTimer.IsEnabled)
                _runnerUiTimer.Start();
        });
        ReloadTasks();
    }

    [RelayCommand]
    private void RefreshCatalog()
    {
        Catalog.Clear();
        var gameRoot = _settings.GameRootPath?.Trim() ?? string.Empty;
        var result = _catalog.Scan(gameRoot);
        // Only current-version (compatible) replays are shown; legacy formats are
        // counted and kept out of the tree so it stays clean and selectable-only.
        var usable = result.Records.Where(x => x.IsCompatible).ToList();
        foreach (var mapGroup in usable.GroupBy(x => x.MapName, StringComparer.OrdinalIgnoreCase))
        {
            // 地图与玩家默认展开，让「有下级」和记录行勾选框一刷新就可见；
            // 赛道 · 阶段层保持折叠，避免一次铺开全部记录行。
            var map = new ReplayTreeNode(mapGroup.Key, level: ReplayNodeLevel.Map) { IsExpanded = true };
            foreach (var playerGroup in mapGroup.GroupBy(x => x.PlayerName, StringComparer.CurrentCultureIgnoreCase))
            {
                var player = new ReplayTreeNode(playerGroup.Key, level: ReplayNodeLevel.Player) { IsExpanded = true };
                foreach (var trackGroup in playerGroup.GroupBy(x => x.TrackNumber).OrderBy(x => x.Key))
                {
                    var staged = trackGroup.Any(x => x.StageNumber > 1);
                    foreach (var stageGroup in trackGroup.GroupBy(x => staged ? x.StageNumber : 0).OrderBy(x => x.Key))
                    {
                        var label = staged ? $"{(trackGroup.Key == 1 ? "主赛道" : $"Bonus {trackGroup.Key - 1}")} · 阶段 {stageGroup.Key}" : (trackGroup.Key == 1 ? "完整地图" : $"Bonus {trackGroup.Key - 1}");
                        var stage = new ReplayTreeNode(label, level: ReplayNodeLevel.Track);
                        foreach (var record in stageGroup.OrderBy(x => x.RunTimeSeconds).ThenByDescending(x => x.RecordedAt))
                            stage.Children.Add(new ReplayTreeNode($"{FormatDuration(record.RunTimeSeconds)} · {record.RecordedAt.LocalDateTime:MM-dd HH:mm}", record, stage, ReplayNodeLevel.Record));
                        player.Children.Add(stage);
                    }
                }
                map.Children.Add(player);
            }
            Catalog.Add(map);
        }
        var incompatible = result.Records.Count - usable.Count;
        CatalogCountText = $"可执行 {usable.Count} · 旧版 {incompatible}";
        ApplyCatalogFilter();
        UpdateSelectionCount();
        SetStatus($"已解析 {result.Records.Count} 条回放；可执行 {usable.Count} 条；旧版不兼容 {incompatible} 条（已隐藏）；无法解析 {result.Issues.Count} 条。",
            Wpf.Ui.Controls.InfoBarSeverity.Informational);
    }

    private void UpdateSelectionCount()
    {
        var selected = Catalog.SelectMany(Flatten).Count(x => x.Record is not null && x.IsSelected);
        SelectionCountText = selected == 0 ? "尚未勾选" : $"已选 {selected} 条";
    }

    /// <summary>
    /// 写入状态文案并同步语义等级。
    /// 任务页此前只有 StatusText、没有任何可视出口，失败信息会静默丢失
    /// （「点创建任务没有反应」的成因）。所有状态写入统一走这里。
    /// severity 为 null 时按文案分级；异常等不可预测文本由调用方显式指定。
    /// </summary>
    private void SetStatus(string text, Wpf.Ui.Controls.InfoBarSeverity? severity = null)
    {
        StatusSeverity = severity ?? ClassifyStatus(text);
        StatusText = text;
    }

    /// <summary>由真实状态文案投影 InfoBar 的 Severity，不伪造状态。</summary>
    private static Wpf.Ui.Controls.InfoBarSeverity ClassifyStatus(string status)
    {
        if (status.Contains("失败", StringComparison.Ordinal)
            || status.Contains("错误", StringComparison.Ordinal)
            || status.Contains("无法", StringComparison.Ordinal)
            || status.Contains("不存在", StringComparison.Ordinal)
            || status.Contains("未配置", StringComparison.Ordinal))
            return Wpf.Ui.Controls.InfoBarSeverity.Error;

        if (status.Contains("请先", StringComparison.Ordinal)
            || status.Contains("请至少", StringComparison.Ordinal)
            || status.Contains("已取消", StringComparison.Ordinal)
            || status.Contains("只有", StringComparison.Ordinal)
            || status.Contains("需要先", StringComparison.Ordinal))
            return Wpf.Ui.Controls.InfoBarSeverity.Warning;

        if (status.Contains("已创建", StringComparison.Ordinal)
            || status.Contains("已删除", StringComparison.Ordinal)
            || status.Contains("已刷新", StringComparison.Ordinal)
            || status.Contains("已完成", StringComparison.Ordinal))
            return Wpf.Ui.Controls.InfoBarSeverity.Success;

        return Wpf.Ui.Controls.InfoBarSeverity.Informational;
    }

    [RelayCommand]
    private async Task CreateTasks()
    {
        try
        {
            var settings = _settings.Snapshot();
            ValidateTaskSettings(settings);
            var selected = Catalog.SelectMany(Flatten).Where(x => x.Record is not null && x.IsSelected).Select(x => x.Record!).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请至少勾选一条回放记录。");
            var incompatible = selected.FirstOrDefault(x => !x.IsCompatible);
            if (incompatible is not null)
                throw new InvalidOperationException($"回放与当前游戏不兼容：{Path.GetFileName(incompatible.FilePath)}（{incompatible.CompatibilityIssue}）。");
            var count = 0;
            foreach (var group in selected.GroupBy(x => new { x.MapName, x.PlayerName, x.TrackNumber }))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var output = Path.Combine(settings.VideoOutputDirectory, Safe($"{group.Key.MapName}_{group.Key.PlayerName}_{stamp}.mp4"));
                var nodes = group.OrderBy(x => x.StageNumber).Select((x, i) => new NewRenderNode(x.FilePath, x.StageNumber, i, x.RunTimeSeconds, x.TickCount)).ToList();
                // 冻结配置：这里是全局设置写入任务的唯一时机。此后执行 / 暂停 / 恢复 /
                // 中断重启都只从 render_tasks.settings_json 反序列化，绝不重新读取全局设置
                // （RenderTaskRunner 甚至不持有 SettingsViewModel/UserSettings 依赖）。
                // 用户要求「创建时是什么配置就一直是什么配置」，因此曾经用于事后覆盖快照的
                // 「刷新快照」按钮与其仓储方法 UpdatePendingTaskSettings 已一并删除，不要加回。
                var snapshot = new RenderSettingsSnapshot(
                    settings.SupersamplingMultiplier,
                    settings.Exposure,
                    settings.RamDiskWatchDirectory,
                    settings.VideoOutputDirectory,
                    settings.GameRootPath!,
                    settings.HideHudInCfg,
                    ProjectConstants.FinalOutputFramerate,
                    settings.IntermediateTargetBitrate,
                    settings.MotionBlurWeightMode,
                    settings.ShutterAngle,
                    settings.VideoProcessing?.Clone(),
                    DiskSafetyFreePercent: settings.DiskSafetyFreePercent);
                _repository.CreateTask(new NewRenderTask(group.Key.MapName, group.Key.PlayerName, group.Key.TrackNumber, output, snapshot, nodes));
                count++;
            }
            ReloadTasks();
            // 创建结果在「执行队列」页签里，切过去让用户直接看到
            IsQueueTabSelected = true;
            SetStatus($"已创建 {count} 个任务并追加到队列。", Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            // 失败原因必须可见：过去只写 StatusText，而任务页没有它的出口，
            // 表现就是「点创建任务没有任何反应」。
            SetStatus(ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            await Services.DialogServiceLocator.Current.ShowInfoAsync("创建任务失败", ex.Message);
        }
    }

    [RelayCommand] private void ReloadTasks()
    {
        var selectedId = SelectedTask?.Record.Id;
        Queue.Clear(); History.Clear();
        foreach (var task in _repository.GetTasks())
        {
            var item = new TaskListItem(task, _repository.GetNodes(task.Id));
            if (task.Status is RenderTaskStatus.Completed or RenderTaskStatus.Canceled or RenderTaskStatus.ClipsReadyNeedsManualMerge) History.Add(item); else Queue.Add(item);
        }
        QueueCount = Queue.Count;
        IsQueueEmpty = Queue.Count == 0;
        IsHistoryEmpty = History.Count == 0;
        if (selectedId is not null)
            SelectedTask = Queue.Concat(History).FirstOrDefault(x => x.Record.Id == selectedId);
    }

    [RelayCommand]
    private async Task StartQueue()
    {
        await _runner.StartAsync();
        SetStatus(_runner.Status);
    }

    [RelayCommand] private void PauseAfterNode() => _runner.PauseAfterCurrentNode();
    [RelayCommand] private void StopNow() => _runner.StopImmediately();

    /// <summary>
    /// 顶层模式切换的只读安全门：无人值守任务执行中时拒绝切换。
    /// 只读取 <see cref="RenderTaskRunner"/> 既有运行态，不改动任务状态机、Attempt 或清理契约。
    /// </summary>
    public string? DescribeCaptureModeSwitchBlock()
    {
        if (_runner.IsRunning)
            return "无人值守任务正在执行，请先「当前节点后暂停」或「立即停止」，并等待受控清理与收尾完成后"
                 + "再切换工作模式。";

        return null;
    }

    [RelayCommand] private void MoveUp() => Move(-1);
    [RelayCommand] private void MoveDown() => Move(1);
    private void Move(int delta)
    {
        if (SelectedTask?.Record.Status != RenderTaskStatus.Pending) return;
        var index = Queue.IndexOf(SelectedTask);
        if (index < 0) return;
        _repository.MovePendingTask(SelectedTask.Record.Id, index + delta);
        ReloadTasks();
    }

    [RelayCommand] private void DeleteTask()
    {
        if (SelectedTask is null) { SetStatus("请先在左侧列表选中要删除的任务。"); return; }
        var title = SelectedTask.Title;
        var status = SelectedTask.Record.Status;
        if (status is RenderTaskStatus.Running or RenderTaskStatus.Starting or RenderTaskStatus.Merging)
        {
            SetStatus("任务正在执行中，无法删除。请先「当前节点后暂停」或「立即停止」。",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }
        _repository.DeleteTaskRecord(SelectedTask.Record.Id);
        ReloadTasks();
        SetStatus($"已删除任务：{title}");
    }

    partial void OnSelectedTaskChanged(TaskListItem? value)
    {
        DetailNodes.Clear();
        DetailLogs.Clear();

        if (value is null)
        {
            SelectedTaskDetail = "选择任务后查看节点和日志。";
            DetailStateText = "未选择任务";
            DetailState = TaskPresentationState.Idle;
            FrozenComposeText = "选择任务后显示冻结的合成参数。";
            FrozenQualityText = string.Empty;
            IsDetailEmpty = true;
            return;
        }

        IsDetailEmpty = false;

        DetailState = value.State;
        DetailStateText = value.StateLabel;
        if (value.Record.Status == RenderTaskStatus.Pending) IsQueueTabSelected = true;

        var nodes = _repository.GetNodes(value.Record.Id).ToList();
        foreach (var node in nodes.OrderBy(x => x.Sequence))
        {
            var state = node.Status switch
            {
                RenderNodeStatus.Completed => TaskPresentationState.Success,
                RenderNodeStatus.Recording or RenderNodeStatus.Synthesizing => TaskPresentationState.Running,
                RenderNodeStatus.Failed => TaskPresentationState.Danger,
                _ => TaskPresentationState.Idle,
            };
            var statusText = node.Status switch
            {
                RenderNodeStatus.Completed => $"已完成 {TimeSpan.FromSeconds(node.ElapsedSeconds):mm\\:ss\\.f}",
                RenderNodeStatus.Recording => "录制中",
                RenderNodeStatus.Synthesizing => "合成中",
                RenderNodeStatus.Failed => $"失败 重试 {node.RetryCount}/2",
                RenderNodeStatus.Skipped => "已跳过",
                _ => "待执行",
            };
            DetailNodes.Add(new TaskNodeItem(state, $"节点 {node.Sequence + 1} · 阶段 {node.StageNumber}", statusText));
        }

        foreach (var log in _repository.GetLogs(value.Record.Id).TakeLast(20))
            DetailLogs.Add(new TaskLogItem($"{log.Timestamp.LocalDateTime:HH:mm:ss}  [{log.Level}] {log.Message}"));

        var configLines = new List<string>();
        FrozenComposeText = "（快照不可解析）";
        FrozenQualityText = string.Empty;
        try
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<RenderSettingsSnapshot>(value.Record.SettingsJson);
            if (snapshot is not null)
            {
                var blur = snapshot.MotionBlurMode == MotionBlurWeightMode.ShutterAngle
                    ? $"Shutter {snapshot.ShutterAngle:0}°"
                    : $"Legacy Exposure {snapshot.Exposure:0.##}";
                var bitrate = snapshot.TargetBitrate > 0 ? $" · 码率 {snapshot.TargetBitrate / 1_000_000.0:0.#} Mbps" : " · 码率 自动";
                FrozenComposeText = $"合成：N={snapshot.SupersamplingMultiplier} · {blur} · {snapshot.OutputFramerate}fps{bitrate}";
                FrozenQualityText = VideoProcessingSummary.Build(snapshot.VideoProcessing);
            }
        }
        catch
        {
            // old SettingsJson without new fields: keep legacy display
        }

        var nodeLines = nodes.Select(x => $"节点 {x.Sequence + 1} / 阶段 {x.StageNumber}：{x.Status}，重试 {x.RetryCount}/2\n{x.ReplayPath}");
        var logLines = _repository.GetLogs(value.Record.Id).TakeLast(30).Select(x => $"{x.Timestamp.LocalDateTime:MM-dd HH:mm:ss} [{x.Level}] {x.Message}");
        SelectedTaskDetail = string.Join("\n", nodeLines.Concat(configLines).Concat(["", "最近日志："]).Concat(logLines));
    }

    [RelayCommand] private void OpenOutput()
    {
        if (SelectedTask is null) return;
        var path = File.Exists(SelectedTask.Record.OutputPath) ? SelectedTask.Record.OutputPath : Path.GetDirectoryName(SelectedTask.Record.OutputPath);
        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path))) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{SelectedTask.Record.OutputPath}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task DeleteOutput()
    {
        if (SelectedTask is null || !File.Exists(SelectedTask.Record.OutputPath)) return;
        var confirmed = await Services.DialogServiceLocator.Current.ConfirmAsync(
            "删除输出",
            "确定删除该任务的最终输出文件？回放源文件和阶段片段不会删除。",
            primaryButtonText: "删除",
            closeButtonText: "取消",
            danger: true);
        if (!confirmed) return;
        File.Delete(SelectedTask.Record.OutputPath); SetStatus("最终输出文件已删除。");
    }

    private static IEnumerable<ReplayTreeNode> Flatten(ReplayTreeNode root) { yield return root; foreach (var child in root.Children.SelectMany(Flatten)) yield return child; }
    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static void ValidateTaskSettings(UserSettings s)
    {
        if (s.CaptureMode != CaptureMode.Tga) throw new InvalidOperationException("任务仅在 TGA 模式下可用。");
        if (string.IsNullOrWhiteSpace(s.GameRootPath) || !Directory.Exists(s.GameRootPath)) throw new InvalidOperationException("游戏根目录不存在。");
        if (string.IsNullOrWhiteSpace(s.RamDiskWatchDirectory) || !Directory.Exists(s.RamDiskWatchDirectory)) throw new InvalidOperationException("TGA 监视目录未配置或不存在。");
        if (string.IsNullOrWhiteSpace(s.VideoOutputDirectory)) throw new InvalidOperationException("请配置成片输出目录。");
        Directory.CreateDirectory(s.VideoOutputDirectory);
    }

    private void UpdateRunnerProjection()
    {
        SetStatus(_runner.Status);
        RuntimeText = FormatRuntime(_runner.RuntimeSnapshot);
        UpdateTelemetry(_runner.RuntimeSnapshot);
        // 运行中也要刷新节点投影；录制管线的高频遥测仍只更新指标，
        // 数据库列表固定每秒读取一次，避免 UI 4 Hz 查询影响捕获线程。
        if (++_runnerUiTicks % 4 == 0)
            ReloadTasks();
        if (!_runner.IsRunning)
        {
            ReloadTasks();
            _runnerUiTimer.Stop();
        }
    }

    private void UpdateTelemetry(CaptureRuntimeSnapshot snapshot)
    {
        if (snapshot.SampledAt == DateTimeOffset.MinValue)
        {
            BacklogValueText = "—";
            BacklogSubText = "等待采样";
            ConsumptionRatioText = "—";
            ConsumptionSubText = "等待采样";
            ConsumptionRatioSeverity = TaskPresentationState.Idle;
            DiskFreeText = "—";
            DiskSubText = "等待采样";
            EncoderRateText = "—";
            EncoderRateSubText = "等待采样";
            return;
        }

        var p = snapshot.Performance;
        BacklogValueText = $"{p.Backlog.PendingFrames} 帧";
        BacklogSubText = $"{p.Backlog.PendingBytes / 1024d / 1024d:0.0} MiB · 趋势 {p.BacklogTrend}";
        ConsumptionRatioText = $"{p.ConsumptionRatio:0.00}";
        ConsumptionSubText = $"生产 {p.ProducedFramesPerSecond:0.0} / 消费 {p.ConsumedFramesPerSecond:0.0} fps";
        ConsumptionRatioSeverity = p.ConsumptionRatio switch
        {
            >= 0.95 => TaskPresentationState.Consuming,
            >= 0.9 => TaskPresentationState.Paused,
            _ => TaskPresentationState.Danger,
        };

        // 编码速率来自原生 frames_output 计数，不是推导值。
        EncoderRateText = $"{p.OutputFramesPerSecond:0.0} fps";
        EncoderRateSubText = $"{p.QualityBackend} 画质 · {p.EncoderBackend} 编码";

        if (snapshot.DiskHealth is { } disk)
        {
            DiskFreeText = $"{disk.FreePercent:0}%";
            DiskSubText = $"安全线 {disk.SafetyPercent}% · 预警线 {disk.WarningPercent}% · {disk.State}";
        }
        else
        {
            DiskFreeText = "—";
            DiskSubText = "等待采样";
        }
    }

    private static string FormatRuntime(CaptureRuntimeSnapshot snapshot)
    {
        if (snapshot.SampledAt == DateTimeOffset.MinValue)
            return "当前没有正在录制的节点。";
        var p = snapshot.Performance;
        var disk = snapshot.DiskHealth;
        var diskText = disk is null
            ? "监视盘：等待采样"
            : $"监视盘 {disk.DriveRoot}：{disk.FreePercent:0.0}% / {disk.FreeBytes / 1024d / 1024d / 1024d:0.0} GiB（安全线 {disk.SafetyPercent}% · 预警线 {disk.WarningPercent}% · {disk.State}）";
        return $"{diskText}\n积压 {p.Backlog.PendingFrames} 帧 / {p.Backlog.PendingBytes / 1024d / 1024d:0.0} MiB · 趋势 {p.BacklogTrend}\n画质后端 {p.QualityBackend} · 编码后端 {p.EncoderBackend}";
    }
}

/// <summary>任务在 UI 上的语义呈现态：驱动卡片底色、描边、进度条与状态文字颜色。</summary>
public enum TaskPresentationState
{
    Idle,
    Running,
    Paused,
    Success,
    Danger,
    Consuming,
}

/// <summary>回放树层级。</summary>
public enum ReplayNodeLevel
{
    Map,
    Player,
    Track,
    Record,
}

/// <summary>任务详情「节点进度」一行。</summary>
public sealed record TaskNodeItem(TaskPresentationState State, string Label, string Value)
{
    /// <summary>节点徽标语义等级，直接绑定 ui:InfoBadge 的 Severity。</summary>
    public Wpf.Ui.Controls.InfoBadgeSeverity Severity => State switch
    {
        TaskPresentationState.Running => Wpf.Ui.Controls.InfoBadgeSeverity.Attention,
        TaskPresentationState.Paused => Wpf.Ui.Controls.InfoBadgeSeverity.Caution,
        TaskPresentationState.Success => Wpf.Ui.Controls.InfoBadgeSeverity.Success,
        TaskPresentationState.Danger => Wpf.Ui.Controls.InfoBadgeSeverity.Critical,
        _ => Wpf.Ui.Controls.InfoBadgeSeverity.Informational,
    };

    public double ProgressRatio => State == TaskPresentationState.Success ? 1 : 0;
    public bool IsRunning => State == TaskPresentationState.Running;
}

/// <summary>任务详情「最近日志」一行。</summary>
public sealed record TaskLogItem(string Text);

public partial class ReplayTreeNode : ObservableObject
{
    public string Label { get; }
    public ReplayRecord? Record { get; }
    public ReplayTreeNode? SelectionGroup { get; }
    public ObservableCollection<ReplayTreeNode> Children { get; } = [];
    [ObservableProperty] private bool isSelected;
    /// <summary>展开态。TreeViewItem 经 Header 双向绑定到这里。</summary>
    [ObservableProperty] private bool isExpanded;
    public bool IsRecord => Record is not null;
    public bool IsSelectable => Record?.IsCompatible == true;
    public string? DisabledReason => Record?.CompatibilityIssue;
    public ReplayNodeLevel Level => _level ?? (Record is not null
        ? ReplayNodeLevel.Record
        : Children.Count > 0 ? ReplayNodeLevel.Map : ReplayNodeLevel.Track);

    public string Icon => Level switch
    {
        ReplayNodeLevel.Map => "Folder24",
        ReplayNodeLevel.Player => "Person24",
        ReplayNodeLevel.Track => "Map24",
        _ => "Document24",
    };

    public Visibility IconVisibility => Record is not null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckVisibility => Record is not null ? Visibility.Visible : Visibility.Collapsed;

    public System.Windows.Media.Brush LabelForeground => Record is not null
        ? (IsSelected
            ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentTextFillColorPrimaryBrush")
            : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextFillColorSecondaryBrush"))
        : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextFillColorPrimaryBrush");

    public ReplayTreeNode(string label, ReplayRecord? record = null, ReplayTreeNode? selectionGroup = null, ReplayNodeLevel? level = null)
    {
        Label = label;
        Record = record;
        SelectionGroup = selectionGroup;
        _level = level;
    }

    private readonly ReplayNodeLevel? _level;

    /// <summary>
    /// 按查询串过滤出仅含匹配记录的子树；无匹配时返回 null。
    /// 命中的节点直接复用原实例（而不是拷贝），否则过滤状态下勾选/取消会写进
    /// 与 Catalog 脱钩的副本，「已选 N 条」和创建任务都取不到。
    /// </summary>
    public ReplayTreeNode? CloneFiltered(string query)
    {
        if (Matches(query))
            return this;
        var kept = Children.Select(c => c.CloneFiltered(query)).Where(c => c is not null).Select(c => c!).ToList();
        if (kept.Count == 0)
            return null;
        // 过滤路径上的分组节点强制展开，命中的记录行不会藏在折叠里。
        var clone = new ReplayTreeNode(Label, Record, SelectionGroup, _level) { IsExpanded = true };
        foreach (var child in kept)
            clone.Children.Add(child);
        return clone;
    }

    private bool Matches(string query) => Label.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsSelectable) { IsSelected = false; return; }
        OnPropertyChanged(nameof(LabelForeground));
        SelectionChanged?.Invoke();
        if (!value || SelectionGroup is null) return;
        foreach (var sibling in SelectionGroup.Children.Where(x => x != this && x.IsSelected)) sibling.IsSelected = false;
    }

    /// <summary>任何节点勾选状态变化时回调宿主 VM 刷新「已选 N 条」。</summary>
    public static event Action? SelectionChanged;
}

public sealed class TaskListItem
{
    public TaskListItem(RenderTaskRecord record, IReadOnlyList<RenderNodeRecord> nodes)
    {
        Record = record;
        Nodes = nodes.OrderBy(x => x.Sequence).Select(CreateNodeItem).ToArray();
    }

    public RenderTaskRecord Record { get; }
    public IReadOnlyList<TaskNodeItem> Nodes { get; }
    public int NodeCount => Nodes.Count;
    public string Title => $"{Record.MapName} · {Record.PlayerName}";
    public string Detail => $"{NodeCount} 个节点 · {Record.Status} · 耗时 {TimeSpan.FromSeconds(Record.ElapsedSeconds):hh\\:mm\\:ss}";

    public TaskPresentationState State => Record.Status switch
    {
        RenderTaskStatus.Running or RenderTaskStatus.Starting or RenderTaskStatus.Merging => TaskPresentationState.Running,
        RenderTaskStatus.Paused => TaskPresentationState.Paused,
        RenderTaskStatus.Completed or RenderTaskStatus.ClipsReadyNeedsManualMerge => TaskPresentationState.Success,
        RenderTaskStatus.FailedNeedsAttention => TaskPresentationState.Danger,
        _ => TaskPresentationState.Idle,
    };

    public string StateLabel => Record.Status switch
    {
        RenderTaskStatus.Running => "录制中",
        RenderTaskStatus.Starting => "启动中",
        RenderTaskStatus.Merging => "合并中",
        RenderTaskStatus.Paused => "已暂停",
        RenderTaskStatus.Completed => "已完成",
        RenderTaskStatus.ClipsReadyNeedsManualMerge => "待手动合并",
        RenderTaskStatus.FailedNeedsAttention => "需要处理",
        RenderTaskStatus.Canceled => "已取消",
        _ => "执行队列",
    };

    public string StatusText => $"{StateLabel} · {Nodes.Count(x => x.State == TaskPresentationState.Success)}/{NodeCount}";

    /// <summary>队列/历史卡片的进度比（0–1）。运行态由耗时推进，其余按状态取值。</summary>
    public double ProgressRatio => NodeCount == 0 ? 0 : Nodes.Sum(x => x.ProgressRatio) / NodeCount;

    private static TaskNodeItem CreateNodeItem(RenderNodeRecord node)
    {
        var state = node.Status switch
        {
            RenderNodeStatus.Completed => TaskPresentationState.Success,
            RenderNodeStatus.Recording or RenderNodeStatus.Synthesizing => TaskPresentationState.Running,
            RenderNodeStatus.Failed => TaskPresentationState.Danger,
            _ => TaskPresentationState.Idle,
        };
        var value = node.Status switch
        {
            RenderNodeStatus.Completed => "已完成",
            RenderNodeStatus.Recording => "录制中",
            RenderNodeStatus.Synthesizing => "合成中",
            RenderNodeStatus.Failed => "失败",
            RenderNodeStatus.Skipped => "已跳过",
            _ => "等待中",
        };
        return new TaskNodeItem(state, $"节点 {node.Sequence + 1} · 阶段 {node.StageNumber}", value);
    }
}
