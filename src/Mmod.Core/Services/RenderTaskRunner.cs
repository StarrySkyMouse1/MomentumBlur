using System.Diagnostics;
using System.Text.Json;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class RenderTaskRunner : IAsyncDisposable
{
    private readonly RenderTaskRepository _repository;
    private CancellationTokenSource? _cts;
    private bool _pauseAfterNode;
    private MomentumProcessController? _verifyGame;
    public bool IsRunning { get; private set; }
    public bool IsVerifying { get; private set; }
    public string Status { get; private set; } = "空闲";
    public event Action? Changed;

    public RenderTaskRunner(RenderTaskRepository repository) { _repository = repository; }

    public Task StartAsync()
    {
        if (IsRunning || IsVerifying) return Task.CompletedTask;
        _pauseAfterNode = false; _cts = new CancellationTokenSource(); IsRunning = true;
        _ = RunQueueAsync(_cts.Token);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mini Capture Envelope：map Ready → TEMP startmovie → CaptureReady → watch → Activity → endmovie.
    /// </summary>
    public async Task VerifyReplayAsync(UserSettings settings, string mapName, string replayFilePath)
    {
        if (IsRunning || IsVerifying)
            throw new InvalidOperationException("已有任务或验证在进行中，请先点「立即停止」。");

        _cts = new CancellationTokenSource();
        IsVerifying = true;
        Status = "正在验证回放（Capture Envelope）…";
        Changed?.Invoke();
        try
        {
            await RunVerifyAsync(settings, mapName, replayFilePath, _cts.Token);
        }
        finally
        {
            IsVerifying = false;
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    public void PauseAfterCurrentNode() { _pauseAfterNode = true; Status = "将在当前节点完成后暂停"; Changed?.Invoke(); }
    public void StopImmediately() { Status = "正在立即停止"; _cts?.Cancel(); Changed?.Invoke(); }

    private async Task RunVerifyAsync(UserSettings settings, string mapName, string replayFilePath, CancellationToken token)
    {
        var logLines = new List<string>();
        void Log(string line)
        {
            logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            Status = string.Join("\n", logLines.TakeLast(40));
            Changed?.Invoke();
        }

        var gameRoot = settings.GameRootPath?.Trim() ?? string.Empty;
        var tempClip = Path.Combine(
            Path.GetTempPath(),
            "mmod_record_verify",
            $"verify_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(tempClip)!);

        try
        {
            if (!Directory.Exists(gameRoot))
                throw new DirectoryNotFoundException("游戏根目录不存在。");
            if (!File.Exists(replayFilePath))
                throw new FileNotFoundException("回放文件不存在。", replayFilePath);

            var metadata = MtvReplayParser.Parse(replayFilePath);
            if (!metadata.IsCompatible)
                throw new NotSupportedException($"回放格式不兼容：{metadata.CompatibilityIssue}");

            var relative = MomentumReplaySession.BuildGameRelativeReplayPath(gameRoot, replayFilePath);
            Log($"地图={mapName}");
            Log($"文件={Path.GetFileName(replayFilePath)}");
            Log($"相对路径={relative}");

            if (_verifyGame is null || _verifyGame.Process is null || _verifyGame.Process.HasExited)
            {
                if (_verifyGame is not null)
                    await _verifyGame.DisposeAsync();
                _verifyGame = new MomentumProcessController();
                _verifyGame.NetCon.OutputReceived += line => Log($"« {line.Trim()}");
                Log("正在启动 Momentum Mod 并连接 NetCon…");
                await _verifyGame.StartAsync(gameRoot, token);
                Log("NetCon 已连接");
            }
            else
            {
                Log("复用已打开的游戏实例");
            }

            await MomentumReplaySession.ChangeMapAsync(_verifyGame.NetCon, mapName, Log, token);

            var verifySettings = CloneForVerify(settings);
            var hostFps = verifySettings.SupersamplingMultiplier * ProjectConstants.FinalOutputFramerate;
            Log($"配置 host_framerate {hostFps}");
            await _verifyGame.NetCon.ExecuteAsync(
                $"sv_cheats 1; host_framerate {hostFps}",
                TimeSpan.FromSeconds(30),
                token);

            await using var pipeline = new TgaPipelineOrchestrator();
            pipeline.Changed += () =>
            {
                Status = string.Join("\n", logLines.TakeLast(36).Append($"Fed={pipeline.FedCount} Anchor={pipeline.ActivityAnchorFrame}"));
                Changed?.Invoke();
            };
            await pipeline.StartAsync(verifySettings, tempClip, acceptPreSessionFiles: false);

            await CaptureEnvelopeRecorder.VerifyActivityAsync(
                _verifyGame.NetCon,
                pipeline,
                verifySettings,
                relative,
                Log,
                token);

            await pipeline.StopAsync();
            Log("验证成功：回放可被自动拉起（已检测到 VisualActivity）。");
            Status = string.Join("\n", logLines.TakeLast(40));
        }
        catch (OperationCanceledException)
        {
            Log("回放验证已取消");
            if (_verifyGame is not null)
            {
                await _verifyGame.DisposeAsync();
                _verifyGame = null;
            }
            throw;
        }
        catch (Exception ex)
        {
            Log("失败：" + ex.Message);
            if (_verifyGame is not null)
            {
                try { await CaptureEnvelopeRecorder.TryEndMovieAsync(_verifyGame.NetCon); } catch { }
                try { await _verifyGame.CloseOwnedAsync(CancellationToken.None); } catch { }
                await _verifyGame.DisposeAsync();
                _verifyGame = null;
            }
            throw new InvalidOperationException(string.Join("\n", logLines.TakeLast(20)), ex);
        }
        finally
        {
            TryDelete(tempClip);
            try
            {
                var dir = Path.GetDirectoryName(tempClip);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* ignore */ }
        }
    }

    private async Task RunQueueAsync(CancellationToken token)
    {
        MomentumProcessController? game = null;
        try
        {
            if (_verifyGame is not null)
            {
                game = _verifyGame;
                _verifyGame = null;
            }

            var runnable = _repository.GetTasks(false)
                .Where(x => x.Status is RenderTaskStatus.Pending or RenderTaskStatus.Paused or RenderTaskStatus.FailedNeedsAttention)
                .OrderBy(x => x.QueuePosition)
                .ToList();
            if (runnable.Count == 0) { Status = "没有待执行任务"; return; }
            var firstSettings = Deserialize(runnable[0]);
            _repository.UpdateTaskStatus(runnable[0].Id, RenderTaskStatus.Starting);

            if (game is null)
            {
                game = new MomentumProcessController();
                Status = "正在启动 Momentum Mod 并验证 NetCon"; Changed?.Invoke();
                await game.StartAsync(firstSettings.GameRootPath, token);
            }
            Status = "NetCon 已连接，准备执行任务"; Changed?.Invoke();

            foreach (var task in runnable)
            {
                token.ThrowIfCancellationRequested();
                var settings = Deserialize(task);
                Validate(settings);
                _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Starting);
                _repository.AddLog(task.Id, null, "Info", $"NetCon 已认证，正在切换地图 {task.MapName}。");
                Status = $"WaitingMapReady：{task.MapName}"; Changed?.Invoke();
                await MomentumReplaySession.ChangeMapAsync(
                    game.NetCon,
                    task.MapName,
                    line => { Status = line; _repository.AddLog(task.Id, null, "Info", line); Changed?.Invoke(); },
                    token);
                _repository.AddLog(task.Id, null, "Info", $"地图 {task.MapName} 已加载，开始配置录制。");
                await game.NetCon.ExecuteAsync(BuildSetup(settings), TimeSpan.FromSeconds(30), token);
                _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Running);
                var timer = Stopwatch.StartNew();
                foreach (var node in _repository.GetNodes(task.Id).Where(x => x.Status != RenderNodeStatus.Completed))
                {
                    await ExecuteNodeWithRetryAsync(game, task, node, settings, token);
                    _repository.UpdateTaskElapsed(task.Id, task.ElapsedSeconds + timer.Elapsed.TotalSeconds);
                    if (_pauseAfterNode)
                    {
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Paused, "按要求在当前节点完成后暂停。");
                        Status = "队列已暂停"; return;
                    }
                }
                var nodes = _repository.GetNodes(task.Id);
                if (nodes.Count == 1)
                {
                    Mp4MergeService.Validate(task.OutputPath);
                    _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Completed);
                }
                else
                {
                    _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Merging);
                    try
                    {
                        Mp4MergeService.MergeAtomically(nodes.OrderBy(x => x.Sequence).Select(x => x.ClipPath!).ToList(), task.OutputPath);
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Completed);
                    }
                    catch (Exception ex)
                    {
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.ClipsReadyNeedsManualMerge, ex.Message);
                        Status = $"{task.MapName} 无损合并失败，阶段片段已保留；继续下一任务。"; Changed?.Invoke();
                    }
                }
            }
            Status = "所有任务已完成";
            await game.CloseOwnedAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var active = _repository.GetTasks(false).FirstOrDefault(x => x.Status is RenderTaskStatus.Starting or RenderTaskStatus.Running or RenderTaskStatus.Merging);
            if (active is not null) _repository.UpdateTaskStatus(active.Id, RenderTaskStatus.Paused, "任务已立即中断；当前节点将在继续时从头执行。");
            Status = "队列已立即停止";
        }
        catch (Exception ex)
        {
            var active = _repository.GetTasks(false).FirstOrDefault(x => x.Status is RenderTaskStatus.Starting or RenderTaskStatus.Running or RenderTaskStatus.Merging);
            if (active is not null) _repository.UpdateTaskStatus(active.Id, RenderTaskStatus.FailedNeedsAttention, ex.Message);
            Status = "执行失败并暂停：" + ex.Message;
        }
        finally
        {
            if (game is not null) await game.DisposeAsync();
            IsRunning = false; _cts?.Dispose(); _cts = null; Changed?.Invoke();
        }
    }

    private async Task ExecuteNodeWithRetryAsync(
        MomentumProcessController game,
        RenderTaskRecord task,
        RenderNodeRecord original,
        RenderSettingsSnapshot settings,
        CancellationToken token)
    {
        var node = original;
        while (true)
        {
            var started = DateTimeOffset.UtcNow;
            var workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ProjectConstants.AppDataFolderName,
                ProjectConstants.TaskWorkFolderName,
                task.Id);
            Directory.CreateDirectory(workDir);
            var clip = _repository.GetNodes(task.Id).Count == 1
                ? task.OutputPath
                : Path.Combine(workDir, $"stage_{node.Sequence + 1:D3}.mp4");
            node = node with
            {
                Status = RenderNodeStatus.Recording,
                StartedAt = started,
                FinishedAt = null,
                ClipPath = clip,
                LastError = null,
            };
            _repository.UpdateNode(node);
            _repository.AddLog(task.Id, node.Id, "Info", $"开始节点（Capture-first）：{Path.GetFileName(node.ReplayPath)}");

            try
            {
                var replayMetadata = MtvReplayParser.Parse(node.ReplayPath);
                if (!replayMetadata.IsCompatible)
                    throw new NotSupportedException($"回放格式不兼容：{replayMetadata.CompatibilityIssue}。请使用当前游戏版本重新下载或生成回放。");

                var user = ToUserSettings(settings);
                var relative = MomentumReplaySession.BuildGameRelativeReplayPath(settings.GameRootPath, node.ReplayPath);
                _repository.AddLog(task.Id, node.Id, "Info", "可复制控制台命令：\n" + MomentumReplaySession.BuildManualConsoleScript(task.MapName, relative));
                _repository.AddLog(
                    task.Id,
                    node.Id,
                    "Info",
                    $"RunTime={replayMetadata.RunTimeSeconds:0.###}s → envelope frames ≈ {CaptureEnvelopeRecorder.ComputeEnvelopeFrameCount(replayMetadata.RunTimeSeconds, settings.SupersamplingMultiplier)} @ {settings.SupersamplingMultiplier * ProjectConstants.FinalOutputFramerate} fps");

                await using var pipeline = new TgaPipelineOrchestrator();
                pipeline.Changed += () =>
                {
                    Status = $"{task.MapName}：节点 {node.Sequence + 1} · Fed={pipeline.FedCount} Anchor={pipeline.ActivityAnchorFrame}";
                    Changed?.Invoke();
                };
                await pipeline.StartAsync(user, clip, acceptPreSessionFiles: false);

                void OnPhase(string phase)
                {
                    Status = $"{task.MapName}：{phase}";
                    if (!phase.StartsWith("Recording：", StringComparison.Ordinal)
                        || phase.Contains("SafeEndFrame", StringComparison.Ordinal))
                    {
                        _repository.AddLog(task.Id, node.Id, "Info", phase);
                    }

                    Changed?.Invoke();
                }

                await CaptureEnvelopeRecorder.RecordAsync(
                    game.NetCon,
                    pipeline,
                    user,
                    relative,
                    replayMetadata.RunTimeSeconds,
                    OnPhase,
                    token);

                await pipeline.StopAsync();
                if (!File.Exists(clip) || new FileInfo(clip).Length == 0)
                    throw new InvalidDataException("节点没有生成有效 MP4 文件。");

                node = node with
                {
                    Status = RenderNodeStatus.Completed,
                    FinishedAt = DateTimeOffset.UtcNow,
                    ElapsedSeconds = node.ElapsedSeconds + (DateTimeOffset.UtcNow - started).TotalSeconds,
                };
                _repository.UpdateNode(node);
                return;
            }
            catch (OperationCanceledException)
            {
                await CaptureEnvelopeRecorder.TryEndMovieAsync(game.NetCon);
                TryDelete(clip);
                _repository.UpdateNode(node with
                {
                    Status = RenderNodeStatus.Pending,
                    ClipPath = null,
                    LastError = "立即中断，继续时从头执行。",
                });
                throw;
            }
            catch (NotSupportedException ex)
            {
                await CaptureEnvelopeRecorder.TryEndMovieAsync(game.NetCon);
                TryDelete(clip);
                _repository.UpdateNode(node with { Status = RenderNodeStatus.Failed, LastError = ex.Message });
                throw;
            }
            catch (Exception ex)
            {
                await CaptureEnvelopeRecorder.TryEndMovieAsync(game.NetCon);
                TryDelete(clip);
                if (node.RetryCount >= 2)
                {
                    _repository.UpdateNode(node with { Status = RenderNodeStatus.Failed, LastError = ex.Message });
                    throw;
                }

                node = node with
                {
                    Status = RenderNodeStatus.Pending,
                    RetryCount = node.RetryCount + 1,
                    ClipPath = null,
                    LastError = ex.Message,
                };
                _repository.UpdateNode(node);
                _repository.AddLog(task.Id, node.Id, "Warning", $"节点失败，将重试（{node.RetryCount}/2）：{ex.Message}");
            }
        }
    }

    private static UserSettings CloneForVerify(UserSettings settings)
    {
        var s = new UserSettings
        {
            CaptureMode = CaptureMode.Tga,
            SupersamplingMultiplier = Math.Max(1, settings.SupersamplingMultiplier),
            Exposure = settings.Exposure,
            RamDiskWatchDirectory = settings.RamDiskWatchDirectory,
            RamDiskDriveLetter = settings.RamDiskDriveLetter,
            VideoOutputDirectory = settings.VideoOutputDirectory,
            GameRootPath = settings.GameRootPath,
            HideHudInCfg = settings.HideHudInCfg,
            MovieSequenceName = "mmod_verify",
        };
        WatchDirectoryHelper.EnsureDerivedPaths(s, s.GameRootPath);
        return s;
    }

    private static RenderSettingsSnapshot Deserialize(RenderTaskRecord task) =>
        JsonSerializer.Deserialize<RenderSettingsSnapshot>(task.SettingsJson)
        ?? throw new InvalidDataException("任务设置快照无效。");

    private static void Validate(RenderSettingsSnapshot s)
    {
        if (!Directory.Exists(s.GameRootPath)) throw new DirectoryNotFoundException("游戏根目录不存在。");
        if (!Directory.Exists(s.WatchDirectory)) throw new DirectoryNotFoundException("TGA 监视目录不存在。");
        Directory.CreateDirectory(s.OutputDirectory);
    }

    private static string BuildSetup(RenderSettingsSnapshot s) =>
        $"sv_cheats 1; {(s.HideHud ? "cl_drawhud 0; " : string.Empty)}host_framerate {s.SupersamplingMultiplier * ProjectConstants.FinalOutputFramerate}";

    private static UserSettings ToUserSettings(RenderSettingsSnapshot s) => new()
    {
        CaptureMode = CaptureMode.Tga,
        SupersamplingMultiplier = s.SupersamplingMultiplier,
        Exposure = s.Exposure,
        RamDiskWatchDirectory = s.WatchDirectory,
        VideoOutputDirectory = s.OutputDirectory,
        GameRootPath = s.GameRootPath,
        HideHudInCfg = s.HideHud,
        MovieSequenceName = "frame",
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning || IsVerifying)
        {
            StopImmediately();
            while (IsRunning || IsVerifying) await Task.Delay(50);
        }

        if (_verifyGame is not null)
        {
            await _verifyGame.DisposeAsync();
            _verifyGame = null;
        }
    }
}
