using System.Diagnostics;
using System.Text.Json;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Queue-level task runner. Owns queue/task lifecycle, game-session
/// compatibility, pause/stop semantics, crash recovery, and delegates each
/// node attempt to NodeExecutionCoordinator (attempt state machine). Per-node
/// cleanup always runs through CaptureCleanupCoordinator with an independent
/// bounded token — the user's cancellation token never gates cleanup.
/// </summary>
public sealed class RenderTaskRunner : IAsyncDisposable
{
    private readonly RenderTaskRepository _repository;
    private readonly RecordingTimeoutPolicy _timeouts = RecordingTimeoutPolicy.Default;
    private readonly IMediaProbe _mediaProbe = new MediaProbe();
    private readonly CaptureCleanupCoordinator _cleanup = new();
    private readonly NodeExecutionCoordinator _coordinator = new();
    private CancellationTokenSource? _cts;
    private bool _pauseAfterNode;
    private MomentumProcessController? _verifyGame;
    public bool IsRunning { get; private set; }
    public bool IsVerifying { get; private set; }
    public bool IsPreflighting { get; private set; }
    public string Status { get; private set; } = "空闲";
    public CaptureRuntimeSnapshot RuntimeSnapshot { get; private set; } = CaptureRuntimeSnapshot.Empty;
    public event Action? Changed;

    public RenderTaskRunner(RenderTaskRepository repository) { _repository = repository; }

    public Task StartAsync()
    {
        if (IsRunning || IsVerifying || IsPreflighting) return Task.CompletedTask;
        _pauseAfterNode = false;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = RunQueueAsync(_cts.Token);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mini Capture Envelope：map Ready → TEMP startmovie → CaptureReady → watch → Evidence → endmovie.
    /// </summary>
    public async Task VerifyReplayAsync(UserSettings settings, string mapName, string replayFilePath)
    {
        if (IsRunning || IsVerifying || IsPreflighting)
            throw new InvalidOperationException("已有任务或验证在进行中，请先点「立即停止」。");

        _cts = new CancellationTokenSource();
        IsVerifying = true;
        Status = "正在验证回放（Capture Envelope）…";
        Changed?.Invoke();
        try
        {
            await RunDiagnosticCaptureAsync(settings, mapName, replayFilePath, TimeSpan.Zero, _cts.Token);
        }
        finally
        {
            IsVerifying = false;
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    public async Task<PerformancePreflightResult> RunPerformancePreflightAsync(string taskId)
    {
        if (IsRunning || IsVerifying || IsPreflighting)
            throw new InvalidOperationException("已有任务、验证或性能预检正在进行。");

        var task = _repository.GetTasks().SingleOrDefault(x => x.Id == taskId)
            ?? throw new InvalidOperationException("任务不存在。");
        if (task.Status != RenderTaskStatus.Pending)
            throw new InvalidOperationException("只有待执行任务可以进行性能预检。");
        var node = _repository.GetNodes(task.Id)
            .Where(x => x.Status == RenderNodeStatus.Pending)
            .OrderBy(x => x.Sequence)
            .FirstOrDefault() ?? throw new InvalidOperationException("任务没有可预检的待执行节点。");
        var snapshot = Deserialize(task);
        Validate(snapshot);

        _cts = new CancellationTokenSource();
        IsPreflighting = true;
        Status = "正在进行真实性能预检…";
        Changed?.Invoke();
        try
        {
            var user = ToUserSettingsForAttempt(snapshot);
            var diagnostic = await RunDiagnosticCaptureAsync(
                user, task.MapName, node.ReplayPath, TimeSpan.FromSeconds(10), _cts.Token);
            var result = PerformancePreflightEvaluator.Evaluate(
                diagnostic.Performance,
                hasSufficientWindow: true,
                diagnostic.HasPendingReadFailure);
            Status = $"性能预检完成：{result.Rating}，消费比 {result.ConsumptionRatio:P1}";
            return result;
        }
        finally
        {
            IsPreflighting = false;
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    public void PauseAfterCurrentNode() { _pauseAfterNode = true; Status = "将在当前节点完成后暂停"; Changed?.Invoke(); }
    public void StopImmediately() { Status = "正在立即停止"; _cts?.Cancel(); Changed?.Invoke(); }

    private async Task<(PerformanceSnapshot Performance, bool HasPendingReadFailure)> RunDiagnosticCaptureAsync(
        UserSettings settings,
        string mapName,
        string replayFilePath,
        TimeSpan observationWindow,
        CancellationToken token)
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

            await using var pipeline = new TgaPipelineOrchestrator(_timeouts);
            pipeline.Changed += () =>
            {
                Status = string.Join("\n", logLines.TakeLast(36).Append($"Fed={pipeline.FedCount} Anchor={pipeline.ActivityAnchorFrame}"));
                Changed?.Invoke();
            };

            var session = CaptureSessionInfo.Create("verify", 0, 0);
            await pipeline.StartAsync(verifySettings, tempClip, session, acceptPreSessionFiles: false);

            var performance = await CaptureEnvelopeRecorder.VerifyActivityAsync(
                _verifyGame.NetCon,
                pipeline,
                verifySettings,
                relative,
                Log,
                token,
                observationWindow: observationWindow);

            Log("验证成功：回放可被自动拉起（已建立 PlaybackEvidence）。");
            Status = string.Join("\n", logLines.TakeLast(40));
            return (performance, pipeline.Watcher.GetBacklogSnapshot().HasReadFailure);
        }
        catch (OperationCanceledException)
        {
            Log("回放验证已取消");
            await ShutdownVerifyGameAsync();
            throw;
        }
        catch (Exception ex)
        {
            Log("失败：" + ex.Message);
            await ShutdownVerifyGameAsync();
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

    private async Task ShutdownVerifyGameAsync()
    {
        if (_verifyGame is null)
            return;
        try
        {
            if (_verifyGame.OwnsProcess)
                await _verifyGame.ShutdownOwnedProcessAsync(_timeouts, CancellationToken.None);
        }
        catch { /* secondary */ }
        await _verifyGame.DisposeAsync();
        _verifyGame = null;
    }

    private async Task RunQueueAsync(CancellationToken token)
    {
        MomentumProcessController? game = null;
        GameSessionCompatibilityKey? currentCompatibility = null;

        try
        {
            // Crash recovery: clean any stale owned process / session before work.
            await RecoverFromCrashAsync(token);

            var runnable = _repository.GetTasks(false)
                .Where(x => x.Status is RenderTaskStatus.Pending or RenderTaskStatus.Paused or RenderTaskStatus.FailedNeedsAttention)
                .OrderBy(x => x.QueuePosition)
                .ToList();
            if (runnable.Count == 0) { Status = "没有待执行任务"; return; }
            var firstSettings = Deserialize(runnable[0]);
            _repository.UpdateTaskStatus(runnable[0].Id, RenderTaskStatus.Starting);

            foreach (var task in runnable)
            {
                token.ThrowIfCancellationRequested();
                var settings = Deserialize(task);
                Validate(settings);

                // Game-session compatibility: different GameRoot/watch env → fresh session.
                var expectedKey = new GameSessionCompatibilityKey(
                    NormalizePath(settings.GameRootPath),
                    NormalizePath(settings.WatchDirectory));
                if (game is not null && currentCompatibility is not null && currentCompatibility != expectedKey)
                {
                    Status = $"任务环境变化（{task.MapName}），正在重建游戏会话…"; Changed?.Invoke();
                    await ShutdownOwnedGameAsync(game, token);
                    game = null;
                    currentCompatibility = null;
                }

                if (game is null)
                {
                    game = new MomentumProcessController();
                    Status = "正在启动 Momentum Mod 并验证 NetCon"; Changed?.Invoke();
                    await game.StartAsync(settings.GameRootPath, token);
                    currentCompatibility = expectedKey;
                }

                _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Starting);
                _repository.AddLog(task.Id, null, "Info", $"NetCon 已认证，正在切换地图 {task.MapName}。");
                Status = $"WaitingMapReady：{task.MapName}"; Changed?.Invoke();

                _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Running);
                var timer = Stopwatch.StartNew();
                foreach (var node in _repository.GetNodes(task.Id).Where(x => x.Status != RenderNodeStatus.Completed))
                {
                    await ExecuteNodeWithRetryAsync(game, task, node, settings, token);
                    _repository.UpdateTaskElapsed(task.Id, task.ElapsedSeconds + timer.Elapsed.TotalSeconds);
                    if (_pauseAfterNode)
                    {
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Paused, "按要求在当前节点完成后暂停。");
                        Status = "队列已暂停（游戏会话保持，capture 已停止并清理）"; return;
                    }
                }

                var nodes = _repository.GetNodes(task.Id);
                if (nodes.Count == 1)
                {
                    ValidateFinalOutput(task.OutputPath);
                    _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Completed);
                }
                else
                {
                    _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Merging);
                    try
                    {
                        Mp4MergeService.MergeAtomically(nodes.OrderBy(x => x.Sequence).Select(x => x.ClipPath!).ToList(), task.OutputPath);
                        ValidateFinalOutput(task.OutputPath);
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.Completed);
                    }
                    catch (Exception ex)
                    {
                        _repository.UpdateTaskStatus(task.Id, RenderTaskStatus.ClipsReadyNeedsManualMerge, ex.Message);
                        Status = $"{task.MapName} 无损合并失败，阶段片段已保留；继续下一任务。"; Changed?.Invoke();
                    }
                }

                _repository.ClearRunnerSession();
            }
            Status = "所有任务已完成";
            if (game is not null)
                await ShutdownOwnedGameAsync(game, token);
            _repository.ClearRunnerSession();
        }
        catch (OperationCanceledException)
        {
            await HandleQueueInterruptionAsync(game, RenderTaskStatus.Paused, "任务已立即中断；当前节点将在继续时从头执行。");
            Status = "队列已立即停止";
        }
        catch (Exception ex)
        {
            await HandleQueueInterruptionAsync(game, RenderTaskStatus.FailedNeedsAttention, ex.Message);
            Status = "执行失败并暂停：" + ex.Message;
        }
        finally
        {
            if (game is not null) await game.DisposeAsync();
            IsRunning = false;
            RuntimeSnapshot = CaptureRuntimeSnapshot.Empty;
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// StopNow / fatal interruption: unified cleanup barrier with an
    /// independent bounded token, then owned-process shutdown per policy.
    /// </summary>
    private async Task HandleQueueInterruptionAsync(MomentumProcessController? game, RenderTaskStatus taskStatus, string message)
    {
        var active = _repository.GetTasks(false)
            .FirstOrDefault(x => x.Status is RenderTaskStatus.Starting or RenderTaskStatus.Running or RenderTaskStatus.Merging);
        if (active is not null)
            _repository.UpdateTaskStatus(active.Id, taskStatus, message);

        using var cleanupCts = new CancellationTokenSource(_timeouts.CleanupHardLimit);
        try
        {
            if (game is not null)
            {
                var cleanup = await _cleanup.CleanupAsync(null, game, CleanupReason.UserCanceled, cleanupCts.Token);
                foreach (var secondary in cleanup.SecondaryErrors)
                    _repository.AddLog(active?.Id ?? string.Empty, null, "Warning", $"清理次级错误：{secondary}");
                if (game.OwnsProcess)
                    await game.ShutdownOwnedProcessAsync(_timeouts, cleanupCts.Token);
            }
        }
        catch (Exception ex)
        {
            _repository.AddLog(active?.Id ?? string.Empty, null, "Error", $"队列中断清理异常：{ex.Message}");
        }
        finally
        {
            _repository.ClearRunnerSession();
        }
    }

    private async Task ShutdownOwnedGameAsync(MomentumProcessController game, CancellationToken token)
    {
        if (!game.OwnsProcess)
            return;
        using var cleanupCts = new CancellationTokenSource(_timeouts.CleanupHardLimit);
        await _cleanup.CleanupAsync(null, game, CleanupReason.Completed, cleanupCts.Token);
        await game.ShutdownOwnedProcessAsync(_timeouts, cleanupCts.Token);
        _repository.ClearRunnerSession();
    }

    private async Task ExecuteNodeWithRetryAsync(
        MomentumProcessController game,
        RenderTaskRecord task,
        RenderNodeRecord node,
        RenderSettingsSnapshot settings,
        CancellationToken token)
    {
        var workDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProjectConstants.AppDataFolderName,
            ProjectConstants.TaskWorkFolderName,
            task.Id);
        Directory.CreateDirectory(workDir);
        var isSingle = _repository.GetNodes(task.Id).Count == 1;
        var stableClip = isSingle
            ? task.OutputPath
            : Path.Combine(workDir, $"stage_{node.Sequence + 1:D3}.mp4");

        var replay = MtvReplayParser.Parse(node.ReplayPath);
        if (!replay.IsCompatible)
            throw new NotSupportedException($"回放格式不兼容：{replay.CompatibilityIssue}。请使用当前游戏版本重新下载或生成回放。");

        node = node with
        {
            Status = RenderNodeStatus.Recording,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = null,
            ClipPath = null,
            LastError = null,
        };
        _repository.UpdateNode(node);
        _repository.AddLog(task.Id, node.Id, "Info", $"开始节点（Capture-first）：{Path.GetFileName(node.ReplayPath)}");
        _repository.AddLog(
            task.Id,
            node.Id,
            "Info",
            NativeSessionDiagnostics.Describe(
                ToUserSettingsForAttempt(settings),
                width: 1920,
                height: 1080,
                blend: Math.Max(1, settings.SupersamplingMultiplier),
                outputFps: ProjectConstants.FinalOutputFramerate));

        var ctx = new NodeExecutionContext(
            Task: task,
            Node: node,
            Settings: settings,
            Replay: replay,
            WorkDirectory: workDir,
            StableClipPath: stableClip,
            Game: game,
            Repository: _repository,
            MediaProbe: _mediaProbe,
            CleanupCoordinator: _cleanup,
            Timeouts: _timeouts,
            Log: (level, msg) =>
            {
                _repository.AddLog(task.Id, node.Id, level, msg ?? string.Empty);
                Status = $"{task.MapName}：节点 {node.Sequence + 1} · {msg}";
                Changed?.Invoke();
            },
            Phase: phase =>
            {
                Status = $"{task.MapName}：{phase}";
                if (!phase.StartsWith("Recording：", StringComparison.Ordinal)
                    || phase.Contains("SafeEndFrame", StringComparison.Ordinal))
                {
                    _repository.AddLog(task.Id, node.Id, "Info", phase);
                }
                Changed?.Invoke();
            },
            OnNodeStatusChanged: updated => _repository.UpdateNode(updated),
            Telemetry: (disk, performance) =>
            {
                RuntimeSnapshot = new CaptureRuntimeSnapshot(
                    task.Id, node.Id, disk, performance, DateTimeOffset.UtcNow);
                Changed?.Invoke();
            });

        try
        {
            var clip = await _coordinator.ExecuteNodeAsync(ctx, token);
            if (!File.Exists(clip) || new FileInfo(clip).Length == 0)
                throw new InvalidDataException("节点没有生成有效 MP4 文件。");

            node = node with
            {
                Status = RenderNodeStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                ClipPath = clip,
                LastError = null,
            };
            _repository.UpdateNode(node);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stableClip);
            _repository.UpdateNode(node with
            {
                Status = RenderNodeStatus.Pending,
                ClipPath = null,
                LastError = "立即中断，继续时从头执行。",
            });
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(stableClip);
            _repository.UpdateNode(node with
            {
                Status = RenderNodeStatus.Failed,
                ClipPath = null,
                LastError = ex.Message,
            });
            throw;
        }
    }

    /// <summary>
    /// App-start crash recovery (plan §7): prove identity of the stale owned
    /// process before killing it, clean only the recorded session TGA prefix,
    /// discard partial temp clips, reset node/task statuses, clear the session.
    /// </summary>
    public async Task RecoverFromCrashAsync(CancellationToken token)
    {
        var session = _repository.GetRunnerSession();
        if (session is null)
            return;

        Status = "正在恢复上次中断的运行现场…";
        Changed?.Invoke();

        // 1. Stale owned process: identity-checked shutdown (PID reuse guard).
        if (session.ProcessId is { } pid && session.ExePath is { Length: > 0 } exe && session.ProcessStartedAt is { } start)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var sameStart = proc.StartTime.ToUniversalTime() == start;
                var sameExe = string.Equals(proc.MainModule?.FileName ?? string.Empty, exe, StringComparison.OrdinalIgnoreCase);
                if (sameStart && sameExe)
                {
                    Status = $"发现上次运行遗留的 Momentum 进程（PID {pid}），正在停止并清理…"; Changed?.Invoke();
                    try
                    {
                        if (session.NetConPort is { } port && session.NetConPassword is { Length: > 0 } pwd)
                        {
                            await using var staleNetCon = new MomentumNetConClient();
                            await staleNetCon.ConnectAsync(port, pwd, _timeouts.NetConReconnectTimeout, token);
                            await MomentumReplaySession.ExecuteEndMovieAsync(staleNetCon, _timeouts, null, token);
                            try { await staleNetCon.ExecuteAsync("quit", TimeSpan.FromSeconds(5), token); } catch { }
                            await Task.Delay(1000, token);
                        }
                    }
                    catch
                    {
                        // fall through to kill
                    }
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // process already gone
            }
        }

        // 2. Clean only the recorded session TGA prefix.
        var watchDir = session.WatchDirectory;
        if (string.IsNullOrWhiteSpace(watchDir) && session.TaskId is { Length: > 0 } taskId)
        {
            try
            {
                var task = _repository.GetTasks(false).FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    var snap = Deserialize(task);
                    watchDir = snap.WatchDirectory;
                }
            }
            catch { /* ignore */ }
        }
        if (!string.IsNullOrWhiteSpace(watchDir) && !string.IsNullOrWhiteSpace(session.SequencePrefix) && Directory.Exists(watchDir))
        {
            try
            {
                var watcher = new TgaDirectoryWatcher(watchDir, session.SequencePrefix);
                watcher.CleanupSessionFiles();
                watcher.Dispose();
            }
            catch { /* ignore */ }
        }

        // 3. Partial-clip crash recovery (M4-B-001): every attempt carrying a
        //    Pending partial — including terminal ones (a crash can leave
        //    stage=Failed with partial_state=Pending) — is reset: its partial
        //    target file and its attempt temp are deleted and the Pending
        //    metadata returns to None. Persisted Validated partials are kept.
        //    Never adopt a crash-window file as a formal Clip; never delete
        //    other attempts' files, formal Clips or user files. Nodes are
        //    reset to Pending by NormalizeInterruptedWork, so the next run
        //    starts a new Attempt from the head of the node.
        foreach (var attempt in _repository.GetActiveAttempts())
        {
            if (!string.IsNullOrWhiteSpace(attempt.TempClipPath))
                TryDelete(attempt.TempClipPath);
        }
        foreach (var attempt in _repository.GetAttemptsWithPendingPartial())
        {
            // Pending semantics: the partial was never validated. Delete both
            // the target candidate (may exist if the move happened) and the
            // attempt temp (may still exist if the move did not), then reset
            // the metadata. Clear failure is logged, never fatal for recovery.
            if (!string.IsNullOrWhiteSpace(attempt.PartialPath))
                TryDelete(attempt.PartialPath);
            if (!string.IsNullOrWhiteSpace(attempt.TempClipPath))
                TryDelete(attempt.TempClipPath);
            try
            {
                _repository.ClearAttemptPartial(attempt.Id);
            }
            catch (Exception clearEx)
            {
                _repository.AddLog(attempt.TaskId, attempt.NodeId, "Error",
                    $"崩溃恢复清除 Pending partial 失败：{clearEx.Message}");
            }
        }
        foreach (var attempt in _repository.GetAttemptsWithValidatedPartial())
        {
            if (string.IsNullOrWhiteSpace(attempt.PartialPath) || !File.Exists(attempt.PartialPath))
                continue;
            _repository.AddLog(attempt.TaskId, attempt.NodeId, "Info",
                $"崩溃恢复保留已验证 partial：{attempt.PartialPath}（输出帧 {attempt.PartialOutputFrames ?? 0}，{attempt.PartialValidatedAt:O}）");
        }

        _repository.ClearRunnerSession();
        Status = "上次中断的运行现场已清理（节点已恢复为 Pending，可继续执行）。";
        Changed?.Invoke();
    }

    private void ValidateFinalOutput(string path)
    {
        var result = _mediaProbe.Probe(path, expectedFps: ProjectConstants.FinalOutputFramerate);
        if (!result.IsValid)
            throw new InvalidDataException($"最终输出媒体校验失败：{result.Error}");
    }

    private static UserSettings CloneForVerify(UserSettings settings) => new()
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
        MotionBlurWeightMode = settings.MotionBlurWeightMode,
        ShutterAngle = settings.ShutterAngle,
        IntermediateTargetBitrate = settings.IntermediateTargetBitrate,
        VideoProcessing = settings.VideoProcessing?.Clone(),
        DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(settings.DiskSafetyFreePercent),
    };

    private static RenderSettingsSnapshot Deserialize(RenderTaskRecord task) =>
        SettingsMigration.NormalizeSnapshot(
            JsonSerializer.Deserialize<RenderSettingsSnapshot>(task.SettingsJson)
            ?? throw new InvalidDataException("任务设置快照无效。"));

    private static void Validate(RenderSettingsSnapshot s)
    {
        if (!Directory.Exists(s.GameRootPath)) throw new DirectoryNotFoundException("游戏根目录不存在。");
        if (!Directory.Exists(s.WatchDirectory)) throw new DirectoryNotFoundException("TGA 监视目录不存在。");
        Directory.CreateDirectory(s.OutputDirectory);
    }

    /// <summary>
    /// Shared conversion used by the node coordinator (per-attempt). The frozen
    /// task percentage is copied explicitly (normalized) so the runtime never
    /// falls back to the UserSettings default of 10.
    /// </summary>
    public static UserSettings ToUserSettingsForAttempt(RenderSettingsSnapshot s) => new()
    {
        CaptureMode = CaptureMode.Tga,
        SupersamplingMultiplier = s.SupersamplingMultiplier,
        Exposure = s.Exposure,
        RamDiskWatchDirectory = s.WatchDirectory,
        VideoOutputDirectory = s.OutputDirectory,
        GameRootPath = s.GameRootPath,
        HideHudInCfg = s.HideHud,
        MovieSequenceName = "frame",
        MotionBlurWeightMode = s.MotionBlurMode,
        ShutterAngle = s.ShutterAngle,
        IntermediateTargetBitrate = s.TargetBitrate,
        DiskSafetyFreePercent = DiskSafetyPolicy.NormalizeSafetyPercent(s.DiskSafetyFreePercent),
        VideoProcessing = s.VideoProcessing?.Clone(),
    };

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return (path ?? string.Empty).TrimEnd('\\').ToLowerInvariant(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning || IsVerifying || IsPreflighting)
        {
            StopImmediately();
            while (IsRunning || IsVerifying || IsPreflighting) await Task.Delay(50);
        }

        await ShutdownVerifyGameAsync();
    }
}
