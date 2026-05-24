using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mmod_record.Models;
using mmod_record.Services;

namespace mmod_record.ViewModels;

public partial class RecordComposeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly IDialogService _dialogs;
    private readonly SystemCpuSampler _cpuSampler = new();
    private readonly DispatcherTimer _cpuTimer;
    private readonly ConcurrentBag<VideoSynthesisOrchestrator> _activeOrchestrators = [];
    private CancellationTokenSource? _batchCts;
    private Task? _batchTask;
    private int _batchFinishedCount;
    private int _batchActiveCount;

    [ObservableProperty]
    private string _pipelineConfigSummaryText = string.Empty;

    [ObservableProperty]
    private string _pipelinePhaseText = "状态：空闲";

    [ObservableProperty]
    private string _pipelineMetricsText = string.Empty;

    [ObservableProperty]
    private double _synthesisProgressPercent;

    [ObservableProperty]
    private string _synthesisProgressText = "合成进度：-";

    [ObservableProperty]
    private string _pipelineMessageText = "添加或拖入视频文件，勾选后点击「开始批量合成」。";

    [ObservableProperty]
    private Brush _pipelineMessageBrush = Brushes.Gray;

    [ObservableProperty]
    private bool _isStartEnabled = true;

    [ObservableProperty]
    private bool _isCancelEnabled;

    [ObservableProperty]
    private bool _isQueueEditEnabled = true;

    [ObservableProperty]
    private double _cpuUsagePercent;

    [ObservableProperty]
    private string _cpuUsageText = "—";

    [ObservableProperty]
    private string _batchQueueSummaryText = "队列：0 个文件，已选 0 个";

    public ObservableCollection<BatchVideoItem> BatchItems { get; } = [];

    public RecordComposeViewModel(SettingsViewModel settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _cpuTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _cpuTimer.Tick += (_, _) => RefreshCpuUsage();
        _cpuTimer.Start();

        _settings.PropertyChanged += OnSettingsPropertyChanged;
        BatchItems.CollectionChanged += (_, _) => UpdateBatchQueueSummary();
        RefreshPipelineConfigSummary();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
            return;

        if (e.PropertyName is nameof(SettingsViewModel.ObsCaptureFramerate) or
            nameof(SettingsViewModel.SupersamplingMultiplier) or
            nameof(SettingsViewModel.VideoOutputDirectory) or
            nameof(SettingsViewModel.VideoEncoder) or
            nameof(SettingsViewModel.CompositionBackend) or
            nameof(SettingsViewModel.MaxParallelGpuJobs) or
            nameof(SettingsViewModel.CrfText) or
            nameof(SettingsViewModel.ExposureText) or
            nameof(SettingsViewModel.FfmpegPath))
        {
            Application.Current.Dispatcher.Invoke(RefreshPipelineConfigSummary);
        }
    }

    partial void OnCpuUsagePercentChanged(double value) =>
        CpuUsageText = $"{value:F0}%";

    public void AddVideoPaths(IEnumerable<string> paths, bool selectNew = true) =>
        AddPathsToQueue(paths, selectNew);

    [RelayCommand]
    private void AddVideoFiles()
    {
        var settings = _settings.ToSettings();
        var initial = ObsOutputPathHelper.ResolveOutputDirectory(settings);
        var first = BatchItems.FirstOrDefault()?.FullPath;
        if (!string.IsNullOrWhiteSpace(first))
            initial = Path.GetDirectoryName(first);

        if (!_dialogs.TryPickVideoFiles("选择一个或多个视频文件", initial, out var paths))
            return;

        AddPathsToQueue(paths, selectNew: true);
    }

    [RelayCommand]
    private async Task StartSynthesisAsync()
    {
        var queue = BatchItems.Where(i => i.IsSelected).ToList();
        if (queue.Count == 0)
        {
            _dialogs.ShowWarning("请至少勾选一个要合成的视频文件。", "无法开始");
            return;
        }

        foreach (var item in queue)
            item.ResetForQueue();

        _settings.Save();
        var settings = _settings.ToSettings();
        var preset = _settings.ToRenderPreset();

        _batchCts = new CancellationTokenSource();
        IsStartEnabled = false;
        IsCancelEnabled = true;
        IsQueueEditEnabled = false;
        var parallel = Math.Clamp(settings.MaxParallelGpuJobs, 1, 4);
        GpuSynthesisConcurrency.Configure(parallel);
        PipelineMessageText = $"批量合成：共 {queue.Count} 个文件，最多 {parallel} 路并行…";
        PipelineMessageBrush = Brushes.DodgerBlue;

        _batchTask = RunBatchParallelAsync(settings, preset, queue, parallel, _batchCts.Token);
        try
        {
            await _batchTask;
        }
        catch (OperationCanceledException)
        {
            PipelineMessageText = "批量合成已取消。";
            PipelineMessageBrush = Brushes.Gray;
        }
        finally
        {
            IsStartEnabled = true;
            IsCancelEnabled = false;
            IsQueueEditEnabled = true;
            _batchCts?.Dispose();
            _batchCts = null;
            _batchTask = null;
            UpdateBatchQueueSummary();
        }
    }

    private async Task RunBatchParallelAsync(
        UserSettings settings,
        RenderPreset preset,
        IReadOnlyList<BatchVideoItem> queue,
        int parallel,
        CancellationToken cancellationToken)
    {
        var total = queue.Count;
        var succeeded = 0;
        var failed = 0;
        _batchFinishedCount = 0;
        _batchActiveCount = 0;

        using var slotSemaphore = new SemaphoreSlim(parallel, parallel);
        var tasks = queue.Select(item => ProcessQueueItemAsync(
            item,
            settings,
            preset,
            total,
            slotSemaphore,
            () => Interlocked.Increment(ref succeeded),
            () => Interlocked.Increment(ref failed),
            cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var item in queue)
            {
                if (item.Status == BatchItemStatus.Running)
                {
                    item.Status = BatchItemStatus.Cancelled;
                    item.StatusText = "已取消";
                }
            }
        }

        PipelinePhaseText = "状态：批量完成";
        PipelineMessageText =
            $"批量完成：成功 {succeeded}，失败 {failed}，共 {total}（并行 {parallel} 路）。\n" +
            $"成片目录：{ObsOutputPathHelper.ResolveOutputDirectory(settings)}";
        PipelineMessageBrush = failed > 0 ? Brushes.DarkOrange : Brushes.ForestGreen;
        SynthesisProgressPercent = 100;
        SynthesisProgressText = $"批量：{succeeded}/{total} 成功";
    }

    private async Task ProcessQueueItemAsync(
        BatchVideoItem item,
        UserSettings settings,
        RenderPreset preset,
        int total,
        SemaphoreSlim slotSemaphore,
        Action onSuccess,
        Action onFailure,
        CancellationToken cancellationToken)
    {
        await slotSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var orchestrator = new VideoSynthesisOrchestrator();
        _activeOrchestrators.Add(orchestrator);
        orchestrator.StatusChanged += status => OnItemPipelineStatusChanged(item, status);

        Interlocked.Increment(ref _batchActiveCount);
        UpdateBatchProgressHeader(total);

        item.Status = BatchItemStatus.Running;
        item.StatusText = "排队中…";
        item.ProgressPercent = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.StatusText = "处理中…";

            var result = await orchestrator.RunOneAsync(
                settings,
                preset,
                item.FullPath,
                0,
                total,
                cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                item.Status = BatchItemStatus.Cancelled;
                item.StatusText = "已取消";
                return;
            }

            if (result.Success)
            {
                onSuccess();
                item.Status = BatchItemStatus.Done;
                item.ProgressPercent = 100;
                item.StatusText = string.IsNullOrWhiteSpace(result.ValidationSummary)
                    ? "完成"
                    : $"完成 · {result.ValidationSummary}";
                item.OutputPath = result.FinalVideoPath;
            }
            else
            {
                onFailure();
                item.Status = BatchItemStatus.Failed;
                item.StatusText = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "失败" : result.ErrorMessage;
            }
        }
        catch (OperationCanceledException)
        {
            item.Status = BatchItemStatus.Cancelled;
            item.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            onFailure();
            item.Status = BatchItemStatus.Failed;
            item.StatusText = ex.Message;
        }
        finally
        {
            Interlocked.Increment(ref _batchFinishedCount);
            Interlocked.Decrement(ref _batchActiveCount);
            slotSemaphore.Release();
            Application.Current.Dispatcher.Invoke(() => UpdateBatchProgressHeader(total));
        }
    }

    private void UpdateBatchProgressHeader(int total)
    {
        var done = Volatile.Read(ref _batchFinishedCount);
        var active = Volatile.Read(ref _batchActiveCount);
        PipelinePhaseText = $"状态：批量进行中（并行 {active}，已完成 {done}/{total}）";

        var running = BatchItems.Where(i => i.Status == BatchItemStatus.Running).ToList();
        if (running.Count > 0)
            SynthesisProgressPercent = running.Average(i => i.ProgressPercent);
    }

    private void OnItemPipelineStatusChanged(BatchVideoItem item, SynthesisStatus status)
    {
        if (!string.Equals(status.InputVideoPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (status.Phase == PipelinePhase.Synthesizing)
            {
                item.ProgressPercent = status.SynthesisProgressPercent;
                if (status.SynthesisTotalFrames > 0)
                {
                    item.StatusText =
                        $"合成 {status.EncodedFrameCount}/{status.SynthesisTotalFrames} ({status.SynthesisProgressPercent:F0}%)";
                }
            }
            else if (status.Phase == PipelinePhase.ValidatingInput)
            {
                item.StatusText = "校验输入…";
            }
            else if (status.Phase == PipelinePhase.MuxingAudio)
            {
                item.StatusText = "封装音轨…";
            }
        });
    }

    [RelayCommand]
    private async Task CancelSynthesisAsync()
    {
        IsCancelEnabled = false;
        PipelineMessageText = "正在取消…";
        _batchCts?.Cancel();
        foreach (var orchestrator in _activeOrchestrators)
        {
            try
            {
                await orchestrator.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var settings = _settings.ToSettings();
        var dir = ObsOutputPathHelper.ResolveOutputDirectory(settings);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    private void AddPathsToQueue(IEnumerable<string> paths, bool selectNew)
    {
        var known = new HashSet<string>(BatchItems.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            if (!ObsOutputPathHelper.IsSupportedVideoExtension(Path.GetExtension(path)))
                continue;

            var full = Path.GetFullPath(path);
            if (!known.Add(full))
                continue;

            var item = new BatchVideoItem(full) { IsSelected = selectNew };
            item.PropertyChanged += OnBatchItemPropertyChanged;
            BatchItems.Add(item);
            added++;
        }

        if (added > 0)
        {
            PipelineMessageText = $"已加入 {added} 个视频。";
            PipelineMessageBrush = Brushes.ForestGreen;
        }

        UpdateBatchQueueSummary();
    }

    private void OnBatchItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchVideoItem.IsSelected))
            UpdateBatchQueueSummary();
    }

    private void UpdateBatchQueueSummary()
    {
        var selected = BatchItems.Count(i => i.IsSelected);
        BatchQueueSummaryText = $"队列：{BatchItems.Count} 个文件，已选 {selected} 个";
    }

    private void RefreshCpuUsage()
    {
        var percent = _cpuSampler.SamplePercent();
        if (percent is null)
            return;

        CpuUsagePercent = Math.Clamp(percent.Value, 0, 100);
    }

    private void RefreshPipelineConfigSummary()
    {
        var settings = _settings.ToSettings();
        var fps = ObsOutputPathHelper.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
        var backendId = CompositionBackendCatalog.Normalize(settings.CompositionBackend);
        var backendLabel = CompositionBackendCatalog.Options
            .FirstOrDefault(o => o.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? backendId;
        var finalEncLabel = VideoEncoderCatalog.Options
            .FirstOrDefault(o => o.Id.Equals(settings.VideoEncoder, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? settings.VideoEncoder;
        var crf = settings.Crf is > 0 and <= 51 ? settings.Crf : 18;
        var exposure = settings.Exposure is > 0 and <= 1 ? settings.Exposure : 0.5;
        var outputDir = ObsOutputPathHelper.ResolveOutputDirectory(settings);
        var parallel = Math.Clamp(settings.MaxParallelGpuJobs, 1, 4);
        var n = Math.Clamp(settings.SupersamplingMultiplier, 1, 120);
        var hostTimescale = SynthesisTiming.FormatMultiplier(
            GameSlowMotionCommandBuilder.ResolveSlowMotionScale(fps, n));

        PipelineConfigSummaryText =
            "模式：离线帧混合 → 60fps 成片（支持批量并行）\n" +
            $"源帧率：{fps} fps · 超采样 N={n} · 游戏运行速率 host_timescale {hostTimescale} · 并行 {parallel} 路\n" +
            $"混合：{backendLabel} · 编码 {finalEncLabel} · CRF {crf} · 曝光 {exposure.ToString("0.##", CultureInfo.InvariantCulture)}\n" +
            $"成片目录：{outputDir}";
    }

    public async ValueTask DisposeAsync()
    {
        _cpuTimer.Stop();
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _batchCts?.Cancel();
        if (_batchTask is not null)
        {
            try
            {
                await _batchTask;
            }
            catch
            {
                // ignored
            }
        }

        foreach (var orchestrator in _activeOrchestrators)
        {
            try
            {
                await orchestrator.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }
}

