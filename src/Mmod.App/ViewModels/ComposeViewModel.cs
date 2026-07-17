using System.Collections.ObjectModel;
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
        _tga.Changed += () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                StatusText = _tga.Status;
                TgaMetricsText = $"已喂入 {_tga.FedCount}，待处理 {_tga.PendingCount}";
                OnPropertyChanged(nameof(IsTgaRunning));
                OnPropertyChanged(nameof(CanStartTga));
                OnPropertyChanged(nameof(CanStopTga));
            });
        };
        RefreshModeSummary();
    }

    public ObservableCollection<BatchVideoItem> BatchItems { get; } = [];

    [ObservableProperty]
    private string modeSummary = string.Empty;

    [ObservableProperty]
    private string statusText = "就绪";

    [ObservableProperty]
    private string tgaMetricsText = "已喂入 0，待处理 0";

    [ObservableProperty]
    private string batchSummary = "队列：0";

    [ObservableProperty]
    private bool isObsBusy;

    public bool IsTgaMode => _settings.CaptureMode == CaptureMode.Tga;
    public bool IsObsMode => _settings.CaptureMode == CaptureMode.Obs;
    public bool IsTgaRunning => _tga.IsRunning;
    public bool CanStartTga => IsTgaMode && !_tga.IsRunning;
    public bool CanStopTga => IsTgaMode && _tga.IsRunning;
    public bool CanStartObs => IsObsMode && !IsObsBusy && BatchItems.Any(i => i.IsSelected);

    public void RefreshModeSummary()
    {
        var s = _settings.Snapshot();
        ModeSummary = s.CaptureMode == CaptureMode.Tga
            ? $"TGA · N={s.SupersamplingMultiplier} · {s.Exposure:0.##}"
            : $"OBS · {s.ObsCaptureFramerate}fps · N={s.SupersamplingMultiplier}";
        OnPropertyChanged(nameof(IsTgaMode));
        OnPropertyChanged(nameof(IsObsMode));
        OnPropertyChanged(nameof(CanStartTga));
        OnPropertyChanged(nameof(CanStopTga));
        OnPropertyChanged(nameof(CanStartObs));
        UpdateBatchSummary();
    }
    [RelayCommand]
    private async Task StartTgaAsync()
    {
        try
        {
            await _tga.StartAsync(_settings.Snapshot());
            StatusText = _tga.Status;
            RefreshModeSummary();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StopTgaAsync()
    {
        await _tga.StopAsync();
        StatusText = _tga.Status;
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
        _obsCts?.Cancel();
        await _tga.DisposeAsync();
    }
}
