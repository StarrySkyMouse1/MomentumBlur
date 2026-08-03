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

    public ObservableCollection<ReplayTreeNode> Catalog { get; } = [];
    public ObservableCollection<TaskListItem> Queue { get; } = [];
    public ObservableCollection<TaskListItem> History { get; } = [];
    [ObservableProperty] private string statusText = "请刷新回放记录并勾选需要执行的记录。";
    [ObservableProperty] private TaskListItem? selectedTask;

    public TasksViewModel(SettingsViewModel settings) { _settings = settings; ReloadTasks(); }

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
                            stage.Children.Add(new ReplayTreeNode($"{FormatDuration(record.RunTimeSeconds)} · {record.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}", record, stage));
                        player.Children.Add(stage);
                    }
                }
                map.Children.Add(player);
            }
            Catalog.Add(map);
        }
        StatusText = $"已解析 {result.Records.Count} 条回放；无法解析 {result.Issues.Count} 条。";
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
            var count = 0;
            foreach (var group in selected.GroupBy(x => new { x.MapName, x.PlayerName, x.TrackNumber }))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var output = Path.Combine(settings.VideoOutputDirectory, Safe($"{group.Key.MapName}_{group.Key.PlayerName}_{stamp}.mp4"));
                var nodes = group.OrderBy(x => x.StageNumber).Select((x, i) => new NewRenderNode(x.FilePath, x.StageNumber, i, x.RunTimeSeconds)).ToList();
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
    public ReplayTreeNode(string label, ReplayRecord? record = null, ReplayTreeNode? selectionGroup = null) { Label = label; Record = record; SelectionGroup = selectionGroup; }
    partial void OnIsSelectedChanged(bool value)
    {
        if (!value || SelectionGroup is null) return;
        foreach (var sibling in SelectionGroup.Children.Where(x => x != this && x.IsSelected)) sibling.IsSelected = false;
    }
}

public sealed record TaskListItem(RenderTaskRecord Record, int NodeCount)
{
    public string Title => $"{Record.MapName} · {Record.PlayerName}";
    public string Detail => $"{NodeCount} 个节点 · {Record.Status} · 耗时 {TimeSpan.FromSeconds(Record.ElapsedSeconds):hh\\:mm\\:ss}";
}
