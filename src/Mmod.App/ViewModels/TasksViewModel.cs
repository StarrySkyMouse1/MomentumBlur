using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;

namespace Mmod.App.ViewModels;

public partial class TasksViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly ReplayCatalogService _catalog = new();
    private readonly RenderTaskRepository _repository = new();
    private readonly RenderTaskRunner _runner;

    public ObservableCollection<ReplayTreeNode> Catalog { get; } = [];
    public ObservableCollection<TaskListItem> Queue { get; } = [];
    public ObservableCollection<TaskListItem> History { get; } = [];
    [ObservableProperty] private string statusText = "请刷新回放记录并勾选需要执行的记录。";
    [ObservableProperty] private TaskListItem? selectedTask;
    [ObservableProperty] private string selectedTaskDetail = "选择任务后查看节点和日志。";

    public TasksViewModel(SettingsViewModel settings)
    {
        _settings = settings; _runner = new RenderTaskRunner(_repository);
        _runner.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { StatusText = _runner.Status; ReloadTasks(); });
        ReloadTasks();
    }

    [RelayCommand]
    private void RefreshCatalog()
    {
        Catalog.Clear();
        var gameRoot = _settings.GameRootPath?.Trim() ?? string.Empty;
        var result = _catalog.Scan(gameRoot);
        foreach (var mapGroup in result.Records.GroupBy(x => x.MapName, StringComparer.OrdinalIgnoreCase))
        {
            var map = new ReplayTreeNode(mapGroup.Key);
            foreach (var playerGroup in mapGroup.GroupBy(x => x.PlayerName, StringComparer.CurrentCultureIgnoreCase))
            {
                var player = new ReplayTreeNode(playerGroup.Key);
                foreach (var trackGroup in playerGroup.GroupBy(x => x.TrackNumber).OrderBy(x => x.Key))
                {
                    var staged = trackGroup.Any(x => x.StageNumber > 1);
                    foreach (var stageGroup in trackGroup.GroupBy(x => staged ? x.StageNumber : 0).OrderBy(x => x.Key))
                    {
                        var label = staged ? $"{(trackGroup.Key == 1 ? "主赛道" : $"Bonus {trackGroup.Key - 1}")} · 阶段 {stageGroup.Key}" : (trackGroup.Key == 1 ? "完整地图" : $"Bonus {trackGroup.Key - 1}");
                        var stage = new ReplayTreeNode(label);
                        foreach (var record in stageGroup.OrderBy(x => x.RunTimeSeconds).ThenByDescending(x => x.RecordedAt))
                        {
                            var version = record.IsCompatible ? string.Empty : $" · 不兼容：MMTV v{record.FormatVersion}";
                            stage.Children.Add(new ReplayTreeNode($"{FormatDuration(record.RunTimeSeconds)} · {record.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}{version}", record, stage));
                        }
                        player.Children.Add(stage);
                    }
                }
                map.Children.Add(player);
            }
            Catalog.Add(map);
        }
        var compatible = result.Records.Count(x => x.IsCompatible);
        var incompatible = result.Records.Count - compatible;
        StatusText = $"已解析 {result.Records.Count} 条回放；可执行 {compatible} 条；旧版不兼容 {incompatible} 条；无法解析 {result.Issues.Count} 条。";
    }

    [RelayCommand]
    private void CreateTasks()
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
                var snapshot = new RenderSettingsSnapshot(settings.SupersamplingMultiplier, settings.Exposure, settings.RamDiskWatchDirectory, settings.VideoOutputDirectory, settings.GameRootPath!, settings.HideHudInCfg, ProjectConstants.FinalOutputFramerate, 140_000_000);
                _repository.CreateTask(new NewRenderTask(group.Key.MapName, group.Key.PlayerName, group.Key.TrackNumber, output, snapshot, nodes));
                count++;
            }
            ReloadTasks();
            StatusText = $"已创建 {count} 个任务并追加到队列。";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand] private void ReloadTasks()
    {
        Queue.Clear(); History.Clear();
        foreach (var task in _repository.GetTasks())
        {
            var item = new TaskListItem(task, _repository.GetNodes(task.Id).Count);
            if (task.Status is RenderTaskStatus.Completed or RenderTaskStatus.Canceled or RenderTaskStatus.ClipsReadyNeedsManualMerge) History.Add(item); else Queue.Add(item);
        }
    }

    [RelayCommand] private async Task StartQueue() { await _runner.StartAsync(); StatusText = _runner.Status; }
    [RelayCommand] private void PauseAfterNode() => _runner.PauseAfterCurrentNode();
    [RelayCommand] private void StopNow() => _runner.StopImmediately();

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
        if (SelectedTask is null) return;
        _repository.DeleteTaskRecord(SelectedTask.Record.Id);
        ReloadTasks();
    }

    partial void OnSelectedTaskChanged(TaskListItem? value)
    {
        if (value is null) { SelectedTaskDetail = "选择任务后查看节点和日志。"; return; }
        var nodes = _repository.GetNodes(value.Record.Id).Select(x => $"节点 {x.Sequence + 1} / 阶段 {x.StageNumber}：{x.Status}，重试 {x.RetryCount}/2\n{x.ReplayPath}");
        var logs = _repository.GetLogs(value.Record.Id).TakeLast(30).Select(x => $"{x.Timestamp.LocalDateTime:MM-dd HH:mm:ss} [{x.Level}] {x.Message}");
        SelectedTaskDetail = string.Join("\n", nodes.Concat(["", "最近日志："]).Concat(logs));
    }

    [RelayCommand] private void RefreshSnapshot()
    {
        if (SelectedTask?.Record.Status != RenderTaskStatus.Pending) { StatusText = "只有待执行任务可以刷新设置快照。"; return; }
        try
        {
            var s = _settings.Snapshot(); ValidateTaskSettings(s);
            _repository.UpdatePendingTaskSettings(SelectedTask.Record.Id, new RenderSettingsSnapshot(s.SupersamplingMultiplier, s.Exposure, s.RamDiskWatchDirectory, s.VideoOutputDirectory, s.GameRootPath!, s.HideHudInCfg, ProjectConstants.FinalOutputFramerate, 140_000_000));
            StatusText = "任务设置快照已刷新。"; ReloadTasks();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand] private void OpenOutput()
    {
        if (SelectedTask is null) return;
        var path = File.Exists(SelectedTask.Record.OutputPath) ? SelectedTask.Record.OutputPath : Path.GetDirectoryName(SelectedTask.Record.OutputPath);
        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path))) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{SelectedTask.Record.OutputPath}\"") { UseShellExecute = true });
    }

    [RelayCommand] private void DeleteOutput()
    {
        if (SelectedTask is null || !File.Exists(SelectedTask.Record.OutputPath)) return;
        if (System.Windows.MessageBox.Show("确定删除该任务的最终输出文件？回放源文件和阶段片段不会删除。", "删除输出", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        File.Delete(SelectedTask.Record.OutputPath); StatusText = "最终输出文件已删除。";
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
}

public partial class ReplayTreeNode : ObservableObject
{
    public string Label { get; }
    public ReplayRecord? Record { get; }
    public ReplayTreeNode? SelectionGroup { get; }
    public ObservableCollection<ReplayTreeNode> Children { get; } = [];
    [ObservableProperty] private bool isSelected;
    public bool IsRecord => Record is not null;
    public bool IsSelectable => Record?.IsCompatible == true;
    public string? DisabledReason => Record?.CompatibilityIssue;
    public ReplayTreeNode(string label, ReplayRecord? record = null, ReplayTreeNode? selectionGroup = null) { Label = label; Record = record; SelectionGroup = selectionGroup; }
    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsSelectable) { IsSelected = false; return; }
        if (!value || SelectionGroup is null) return;
        foreach (var sibling in SelectionGroup.Children.Where(x => x != this && x.IsSelected)) sibling.IsSelected = false;
    }
}

public sealed record TaskListItem(RenderTaskRecord Record, int NodeCount)
{
    public string Title => $"{Record.MapName} · {Record.PlayerName}";
    public string Detail => $"{NodeCount} 个节点 · {Record.Status} · 耗时 {TimeSpan.FromSeconds(Record.ElapsedSeconds):hh\\:mm\\:ss}";
}
