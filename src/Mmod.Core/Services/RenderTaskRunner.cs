using System.Diagnostics;
using System.Text.Json;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class RenderTaskRunner : IAsyncDisposable
{
    private readonly RenderTaskRepository _repository;
    private CancellationTokenSource? _cts;
    private bool _pauseAfterNode;
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "空闲";
    public event Action? Changed;

    public RenderTaskRunner(RenderTaskRepository repository) { _repository = repository; }

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        _pauseAfterNode = false; _cts = new CancellationTokenSource(); IsRunning = true;
        _ = RunQueueAsync(_cts.Token);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void PauseAfterCurrentNode() { _pauseAfterNode = true; Status = "将在当前节点完成后暂停"; Changed?.Invoke(); }
    public void StopImmediately() { Status = "正在立即停止"; _cts?.Cancel(); Changed?.Invoke(); }

    private async Task RunQueueAsync(CancellationToken token)
    {
        MomentumProcessController? game = null;
        try
        {
            var runnable = _repository.GetTasks(false).Where(x => x.Status is RenderTaskStatus.Pending or RenderTaskStatus.Paused or RenderTaskStatus.FailedNeedsAttention).OrderBy(x => x.QueuePosition).ToList();
            if (runnable.Count == 0) { Status = "没有待执行任务"; return; }
            var firstSettings = Deserialize(runnable[0]);
            _repository.UpdateTaskStatus(runnable[0].Id, RenderTaskStatus.Starting);
            game = new MomentumProcessController();
            Status = "正在启动 Momentum Mod 并验证 NetCon"; Changed?.Invoke();
            await game.StartAsync(firstSettings.GameRootPath, token);
            Status = "NetCon 已连接，准备执行任务"; Changed?.Invoke();

            foreach (var task in runnable)
            {
                token.ThrowIfCancellationRequested();
                var settings = Deserialize(task);
                Validate(settings);
                _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Starting);
                _repository.AddLog(task.Id, null, "Info", $"NetCon 已认证，正在切换地图 {task.MapName}。");
                Status = $"正在切换地图：{task.MapName}"; Changed?.Invoke();
                // `map` interrupts the current console command while the level
                // loads, so an ACK appended with a semicolon is discarded.
                // Send it as its own line, then use the next line's ACK as the
                // condition that the engine command loop is responsive again.
                await game.NetCon.SendAsync($"map {Quote(task.MapName)}", token);
                await game.NetCon.ExecuteAsync("echo MMOD_MAP_READY", TimeSpan.FromMinutes(3), token);
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

    private async Task ExecuteNodeWithRetryAsync(MomentumProcessController game, RenderTaskRecord task, RenderNodeRecord original, RenderSettingsSnapshot settings, CancellationToken token)
    {
        var node = original;
        while (true)
        {
            var started = DateTimeOffset.UtcNow;
            var workDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProjectConstants.AppDataFolderName, ProjectConstants.TaskWorkFolderName, task.Id);
            Directory.CreateDirectory(workDir);
            var clip = _repository.GetNodes(task.Id).Count == 1 ? task.OutputPath : Path.Combine(workDir, $"stage_{node.Sequence + 1:D3}.mp4");
            node = node with { Status = RenderNodeStatus.Recording, StartedAt = started, FinishedAt = null, ClipPath = clip, LastError = null };
            _repository.UpdateNode(node);
            _repository.AddLog(task.Id, node.Id, "Info", $"开始节点：{Path.GetFileName(node.ReplayPath)}");
            try
            {
                var replayMetadata = MtvReplayParser.Parse(node.ReplayPath);
                if (!replayMetadata.IsCompatible)
                    throw new NotSupportedException($"回放格式不兼容：{replayMetadata.CompatibilityIssue}。请使用当前游戏版本重新下载或生成回放。");
                var user = ToUserSettings(settings);
                await using var pipeline = new TgaPipelineOrchestrator();
                pipeline.Changed += () => { Status = $"{task.MapName}：节点 {node.Sequence + 1}，已处理 {pipeline.FedCount} 帧"; Changed?.Invoke(); };
                await pipeline.StartAsync(user, clip, false);
                var replay = BuildGameReplayPath(settings.GameRootPath, node.ReplayPath);
                await game.NetCon.SendAsync($"mom_tv_replay_watch {Quote(replay)}", token);
                // This Playtest build does not emit its internal `Loaded replay`
                // message to NetCon. The watch command itself is synchronous
                // enough to create the controlled replay; use the next command
                // line's ACK as readiness, while still rejecting explicit
                // loading errors seen before that ACK.
                await game.NetCon.ExecuteCheckedAsync(
                    "echo MMOD_REPLAY_COMMAND_COMPLETE", TimeSpan.FromMinutes(2),
                    line => line.Contains("Failed to load replay", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Failed to open replay", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Invalid replay file", StringComparison.OrdinalIgnoreCase),
                    token);
                // Watching starts playback. Once the following command ACKs,
                // pause, seek back to tick 0, then resume after startmovie is
                // active so no replay frames are missed.
                await game.NetCon.ExecuteAsync("mom_tv_replay_play_pause; mom_tv_replay_goto 0", TimeSpan.FromSeconds(30), token);
                _repository.AddLog(task.Id, node.Id, "Info", "回放已加载并定位到 tick 0，正在启动 TGA 后继续播放。");
                await game.NetCon.ExecuteAsync($"{WatchDirectoryHelper.BuildGameStartmovieCommand(user.MovieSequenceName)}; mom_tv_replay_play_pause", TimeSpan.FromSeconds(30), token);
                var expectedFrames = Math.Max(1, (int)Math.Ceiling(node.ExpectedDurationSeconds * settings.SupersamplingMultiplier * ProjectConstants.FinalOutputFramerate));
                var validationFrames = Math.Min(expectedFrames, Math.Max(5, settings.SupersamplingMultiplier * 2));
                var lastProgress = DateTime.UtcNow; var lastCount = -1;
                while (pipeline.FedCount < expectedFrames)
                {
                    token.ThrowIfCancellationRequested();
                    if (pipeline.FedCount != lastCount) { lastCount = pipeline.FedCount; lastProgress = DateTime.UtcNow; }
                    if (DateTime.UtcNow - lastProgress > TimeSpan.FromMinutes(2)) throw new TimeoutException("TGA 帧连续两分钟没有增长。");
                    if (pipeline.FedCount >= validationFrames && !pipeline.HasVisualChange)
                        throw new InvalidOperationException("回放未实际播放：已经生成 TGA 帧，但画面始终静止。请确认回放由当前游戏版本生成。");
                    await Task.Delay(250, token);
                }
                if (!pipeline.HasVisualChange)
                    throw new InvalidOperationException("回放未实际播放：录制完成时画面仍然完全静止。请确认回放由当前游戏版本生成。");
                await game.NetCon.ExecuteAsync("endmovie; host_framerate 0; host_timescale 1; sv_cheats 0", TimeSpan.FromSeconds(30), token);
                await pipeline.StopAsync();
                if (!File.Exists(clip) || new FileInfo(clip).Length == 0) throw new InvalidDataException("节点没有生成有效 MP4 文件。");
                node = node with { Status = RenderNodeStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, ElapsedSeconds = node.ElapsedSeconds + (DateTimeOffset.UtcNow - started).TotalSeconds };
                _repository.UpdateNode(node); return;
            }
            catch (OperationCanceledException)
            {
                try { await game.NetCon.ExecuteAsync("endmovie; host_framerate 0; host_timescale 1; sv_cheats 0", TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
                TryDelete(clip);
                _repository.UpdateNode(node with { Status = RenderNodeStatus.Pending, ClipPath = null, LastError = "立即中断，继续时从头执行。" });
                throw;
            }
            catch (NotSupportedException ex)
            {
                try { await game.NetCon.ExecuteAsync("endmovie; host_framerate 0; host_timescale 1; sv_cheats 0", TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
                TryDelete(clip);
                _repository.UpdateNode(node with { Status = RenderNodeStatus.Failed, LastError = ex.Message });
                throw;
            }
            catch (Exception ex)
            {
                try { await game.NetCon.ExecuteAsync("endmovie; host_framerate 0; host_timescale 1; sv_cheats 0", TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
                TryDelete(clip);
                if (node.RetryCount >= 2) { _repository.UpdateNode(node with { Status = RenderNodeStatus.Failed, LastError = ex.Message }); throw; }
                node = node with { Status = RenderNodeStatus.Pending, RetryCount = node.RetryCount + 1, ClipPath = null, LastError = ex.Message };
                _repository.UpdateNode(node); _repository.AddLog(task.Id, node.Id, "Warning", $"节点失败，将重试（{node.RetryCount}/2）：{ex.Message}");
            }
        }
    }

    private static RenderSettingsSnapshot Deserialize(RenderTaskRecord task) => JsonSerializer.Deserialize<RenderSettingsSnapshot>(task.SettingsJson) ?? throw new InvalidDataException("任务设置快照无效。");
    private static void Validate(RenderSettingsSnapshot s) { if (!Directory.Exists(s.GameRootPath)) throw new DirectoryNotFoundException("游戏根目录不存在。"); if (!Directory.Exists(s.WatchDirectory)) throw new DirectoryNotFoundException("TGA 监视目录不存在。"); Directory.CreateDirectory(s.OutputDirectory); }
    private static string BuildSetup(RenderSettingsSnapshot s) => $"sv_cheats 1; {(s.HideHud ? "cl_drawhud 0; " : string.Empty)}host_framerate {s.SupersamplingMultiplier * ProjectConstants.FinalOutputFramerate}";
    private static UserSettings ToUserSettings(RenderSettingsSnapshot s) => new() { CaptureMode = CaptureMode.Tga, SupersamplingMultiplier = s.SupersamplingMultiplier, Exposure = s.Exposure, RamDiskWatchDirectory = s.WatchDirectory, VideoOutputDirectory = s.OutputDirectory, GameRootPath = s.GameRootPath, HideHudInCfg = s.HideHud, MovieSequenceName = "frame" };
    private static string Quote(string text) => $"\"{text.Replace("\"", string.Empty)}\"";
    private static string BuildGameReplayPath(string gameRoot, string replayPath)
    {
        var contentRoot = Path.Combine(Path.GetFullPath(gameRoot), "momentum");
        var relative = Path.GetRelativePath(contentRoot, Path.GetFullPath(replayPath));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("回放文件不在游戏 momentum 目录中。");
        return relative.Replace('\\', '/').Replace("\"", string.Empty);
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public async ValueTask DisposeAsync() { if (IsRunning) { StopImmediately(); while (IsRunning) await Task.Delay(50); } }
}
