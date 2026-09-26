using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>Captures a short replay sample as an unblended 60 fps slow-motion master.</summary>
public sealed class ReplayQualityPreviewCaptureService
{
    public const int PreviewSupersamplingMultiplier = 60;
    private readonly RecordingTimeoutPolicy _timeouts = RecordingTimeoutPolicy.Default;

    public async Task<string> CaptureAsync(
        ReplayRecord replay,
        UserSettings settings,
        IProgress<QualityPreviewService.PreviewProgress>? progress,
        CancellationToken token)
    {
        if (!replay.IsCompatible)
            throw new NotSupportedException(replay.CompatibilityIssue);
        if (string.IsNullOrWhiteSpace(settings.GameRootPath) || !Directory.Exists(settings.GameRootPath))
            throw new DirectoryNotFoundException("游戏根目录未配置或不存在。");

        WatchDirectoryHelper.EnsureDerivedPaths(settings, settings.GameRootPath);
        var watchDirectory = WatchDirectoryHelper.ResolveEffectiveWatchDirectory(settings, settings.GameRootPath);
        MomentumDirectoryLinkService.EnsureCaptureTargetOnRam(
            settings.GameRootPath,
            settings.RamDiskWatchDirectory ?? string.Empty,
            watchDirectory);

        var previewDirectory = settings.VideoOutputDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previewDirectory))
            throw new DirectoryNotFoundException("请先配置成片输出目录，阶段 1 底片会直接保存在该目录中。");
        Directory.CreateDirectory(previewDirectory);
        var replayName = SanitizeFileName($"{replay.MapName}-{replay.PlayerName}");
        var output = Path.Combine(
            previewDirectory,
            $"quality-preview-stage1-{replayName}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");
        var session = CaptureSessionInfo.Create("preview", 0, 1);
        var captureSettings = CloneForRawCapture(settings);
        captureSettings.MovieSequenceName = session.SequencePrefix;

        await using var game = new MomentumProcessController();
        var cleanup = new CaptureCleanupCoordinator();
        TgaPipelineOrchestrator? pipeline = null;
        var environment = new CaptureConVarScope(game.NetCon, (_, message) =>
            progress?.Report(new QualityPreviewService.PreviewProgress(message ?? string.Empty)));

        try
        {
            progress?.Report(new QualityPreviewService.PreviewProgress("正在复用或启动 Momentum Mod…"));
            await game.StartAsync(settings.GameRootPath, token);
            await MomentumReplaySession.EnsurePrivateSoloLobbyAsync(game.NetCon, null, token);
            progress?.Report(new QualityPreviewService.PreviewProgress($"正在载入地图 {replay.MapName}…"));
            await MomentumReplaySession.ChangeMapAsync(game.NetCon, replay.MapName, null, token, timeouts: _timeouts);

            var captureFps = PreviewSupersamplingMultiplier * ProjectConstants.FinalOutputFramerate;
            var foregroundLimit = SettingsMigration.NormalizeForegroundCaptureFpsLimit(settings.ForegroundCaptureFpsLimit);
            await environment.ApplyAsync(captureFps, foregroundLimit == 0 ? null : foregroundLimit, token);
            await game.NetCon.ExecuteAsync(settings.HideHudInCfg ? "cl_drawhud 0" : "cl_drawhud 1", TimeSpan.FromSeconds(10), token);

            // blend=1 preserves every high-time-sampling TGA. Encoding at 60 fps
            // deliberately creates an N-times slow-motion master for the second pass.
            pipeline = new TgaPipelineOrchestrator(_timeouts, blendOverride: 1);
            await pipeline.StartAsync(captureSettings, output, session, acceptPreSessionFiles: false);
            var relativeReplay = MomentumReplaySession.BuildGameRelativeReplayPath(settings.GameRootPath, replay.FilePath);
            var health = new GameSessionHealthMonitor(game, watchDirectory);
            progress?.Report(new QualityPreviewService.PreviewProgress(
                $"正在抓取未合成慢放素材（{captureFps} 时间采样/秒）…"));
            await CaptureEnvelopeRecorder.RecordAsync(
                game.NetCon,
                pipeline,
                captureSettings,
                relativeReplay,
                runTimeSeconds: Math.Min(6, Math.Max(0.5, replay.RunTimeSeconds)),
                health,
                phase: text => progress?.Report(new QualityPreviewService.PreviewProgress(text)),
                structuredLog: null,
                token,
                _timeouts,
                telemetry: (disk, performance) => progress?.Report(
                    new QualityPreviewService.PreviewProgress(
                        "正在抓取 60× 慢放底片…",
                        Disk: disk,
                        Performance: performance)));
            pipeline = null;
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            try { await environment.RestoreAsync(); } catch { }
            try { await game.NetCon.ExecuteAsync("cl_drawhud 1", TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
            if (pipeline is not null)
            {
                using var cleanupCts = new CancellationTokenSource(_timeouts.CleanupHardLimit);
                try { await cleanup.CleanupAsync(pipeline, game, CleanupReason.Failed, cleanupCts.Token); } catch { }
            }
        }
    }

    private static UserSettings CloneForRawCapture(UserSettings source) => new()
    {
        CaptureMode = CaptureMode.Tga,
        SupersamplingMultiplier = PreviewSupersamplingMultiplier,
        Exposure = source.Exposure,
        VideoOutputDirectory = source.VideoOutputDirectory,
        RamDiskWatchDirectory = source.RamDiskWatchDirectory,
        GameRootPath = source.GameRootPath,
        HideHudInCfg = source.HideHudInCfg,
        DiskSafetyFreePercent = source.DiskSafetyFreePercent,
        ForegroundCaptureFpsLimit = source.ForegroundCaptureFpsLimit,
        IntermediateTargetBitrate = source.IntermediateTargetBitrate,
        MotionBlurWeightMode = source.MotionBlurWeightMode,
        ShutterAngle = source.ShutterAngle,
        VideoProcessing = new VideoProcessingSettings(),
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "replay" : sanitized;
    }
}
