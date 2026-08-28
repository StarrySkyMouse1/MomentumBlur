using Mmod.Core.Models;
using Mmod.Core.Services;

namespace Mmod.SmokeTest;

/// <summary>
/// Deterministic recording-chain state machine tests using fakes (plan §16/§17).
/// No real game, NetCon, TGA writer or encoder is required.
/// </summary>
public sealed class RecordingStateTests
{
    private readonly RecordingTimeoutPolicy _timeouts = new()
    {
        TgaQuiescenceQuietWindow = TimeSpan.FromMilliseconds(20),
        TgaQuiescenceHardTimeout = TimeSpan.FromSeconds(2),
        ProgressSampleInterval = TimeSpan.FromMilliseconds(10),
        PlaybackEvidenceRequiredConsecutive = 3,
        EvidenceBlockSize = 16,
        EvidenceChangedBlockRatioThreshold = 0.08,
        EvidenceMeanLumaDeltaThreshold = 4.0,
    };

    public async Task RunAllAsync(List<string> failures)
    {
        await ProbeTests(failures);
        await RecorderTests(failures);
        await DiskSafetyTests(failures);
        await WatcherTests(failures);
        await PolicyTests(failures);
        MediaProbeTests(failures);
        TelemetryTests(failures);
        WatcherTelemetryTests(failures);
    }

    // ---- 17.5 / 17.6 / 17.7: visual playback evidence probe ----

    private Task ProbeTests(List<string> failures)
    {
        var probe = new VisualPlaybackEvidenceProbe(_timeouts);
        const int w = 64, h = 64;

        byte[] Frame(byte v)
        {
            var buf = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                buf[i * 4 + 0] = v; buf[i * 4 + 1] = v; buf[i * 4 + 2] = v; buf[i * 4 + 3] = 255;
            }
            return buf;
        }

        byte[] CornerDigitChanged(byte baseV)
        {
            var buf = Frame(baseV);
            // small corner region changes only
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                {
                    var i = (y * w + x) * 4;
                    buf[i] = buf[i + 1] = buf[i + 2] = (byte)(baseV ^ 0xFF);
                }
            return buf;
        }

        byte[] MovingScene(byte phase)
        {
            var buf = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var v = (byte)((x * 3 + y * 5 + phase * 37) % 255);
                    var i = (y * w + x) * 4;
                    buf[i] = buf[i + 1] = buf[i + 2] = v; buf[i + 3] = 255;
                }
            return buf;
        }

        // Baseline still frame.
        probe.SetBaseline(Frame(100), w, h);

        // 17.5: HUD-only tiny change must NOT establish the anchor.
        probe.Sample(CornerDigitChanged(100), w, h);
        probe.Sample(CornerDigitChanged(101), w, h);
        probe.Sample(CornerDigitChanged(102), w, h);
        if (probe.IsPlaybackStarted)
            failures.Add("17.5 HUD-only change must not establish playback evidence");

        // 17.6: scene-wide motion over consecutive frames must establish it.
        probe.Reset();
        probe.SetBaseline(Frame(100), w, h);
        for (var i = 1; i <= 3; i++)
        {
            probe.Sample(MovingScene((byte)i), w, h);
        }
        if (!probe.IsPlaybackStarted)
            failures.Add("17.6 scene-wide consecutive motion must establish playback evidence");

        // 17.7: single-frame spike must not establish it (hysteresis).
        probe.Reset();
        probe.SetBaseline(Frame(100), w, h);
        probe.Sample(MovingScene(9), w, h);   // big spike once
        probe.Sample(Frame(101), w, h);       // back to still
        probe.Sample(Frame(102), w, h);
        if (probe.IsPlaybackStarted)
            failures.Add("17.7 single spike must not establish playback evidence");

        return Task.CompletedTask;
    }

    // ---- recorder scenarios (17.1 / 17.8 / 17.9 / 17.13 / 17.14 / 17.15 / 17.16) ----

    private async Task RecorderTests(List<string> failures)
    {
        // 17.1 happy path
        {
            var (net, pipe, health) = Fakes.ForHappyPath(_timeouts);
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, _timeouts, _ => { });
            if (result.FinishSucceeded != true || pipe.FinalizeCount != 1)
                failures.Add("17.1 happy path did not complete finalize");
        }

        // 17.8 pipeline fault mid-recording → recorder exits fast, no 2-minute wait
        {
            var (net, pipe, health) = Fakes.ForFaultyPipeline(_timeouts);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 10.0,
                    health, null, null, CancellationToken.None, _timeouts, _ => { });
                failures.Add("17.8 pipeline fault did not fail the recorder");
            }
            catch (PipelineFaultException)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    failures.Add("17.8 pipeline fault took too long to propagate");
            }
        }

        // 17.9 native finish fault → not completed (pipeline.FinalizeAsync throws)
        {
            var (net, pipe, health) = Fakes.ForFinalizeFault(_timeouts);
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, _timeouts, _ => { });
                failures.Add("17.9 finish fault must fail the attempt");
            }
            catch (RecordingStageException)
            {
                // expected
            }
            catch (Exception)
            {
                // any failure is acceptable, just not success
            }
        }

        // 17.13 endmovie NetCon timeout → CaptureStopUnconfirmed
        {
            var (net, pipe, health) = Fakes.ForStopTimeout(_timeouts);
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, _timeouts, _ => { });
                failures.Add("17.13 endmovie timeout must fail");
            }
            catch (CaptureStopUnconfirmedException)
            {
                // expected
            }
        }

        // 17.15 game exit during capture → GameExitedException fast
        {
            var (net, pipe, health) = Fakes.ForGameExit(_timeouts);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 10.0,
                    health, null, null, CancellationToken.None, _timeouts, _ => { });
                failures.Add("17.15 game exit must fail the recorder");
            }
            catch (GameExitedException)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    failures.Add("17.15 game exit detection too slow");
            }
        }

        // 17.16 user cancel during capture → OperationCanceledException
        {
            var (net, pipe, health) = Fakes.ForCancel(_timeouts);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1 }, "replays/x.mtv", 10.0,
                    health, null, null, cts.Token, _timeouts, _ => { });
                failures.Add("17.16 cancel must throw OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    // ---- S2: percentage disk safety at runtime (A-S2-03 .. A-S2-10) ----

    private async Task DiskSafetyTests(List<string> failures)
    {
        const long gib = 1024L * 1024 * 1024;

        RecordingTimeoutPolicy DiskTimeouts() => new()
        {
            ProgressSampleInterval = TimeSpan.FromMilliseconds(10),
            DiskHealthSampleInterval = TimeSpan.FromMilliseconds(30),
            DiskHealthUnavailableMaxConsecutiveSamples = 3,
            TgaQuiescenceQuietWindow = TimeSpan.FromMilliseconds(20),
            TgaQuiescenceHardTimeout = TimeSpan.FromSeconds(2),
        };

        // A-S2-03: 100 GiB volume with 9 GiB free at 10% must trigger DiskPressure
        // even though 9 GiB >> old 2 GiB floor.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            var snap = DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 9 * gib, 10, DateTimeOffset.UtcNow);
            health.Snapshots.Add(snap);
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("A-S2-03 critical at 9/100 GiB (10%) must throw DiskPressure");
            }
            catch (DiskPressureException ex)
            {
                if (!ex.Message.Contains("9.0%", StringComparison.Ordinal) || !ex.Message.Contains("R:\\", StringComparison.Ordinal))
                    failures.Add($"A-S2-03 DiskPressure message must carry FreePercent/DriveRoot: {ex.Message}");
            }
        }

        // A-S2-04: 8 GiB volume with 1 GiB free (12.5%) at 10% must NOT stop,
        // even though 1 GiB < old 2 GiB floor.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForHappyPath(t);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 8 * gib, gib, 10, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-04 1 GiB free on 8 GiB volume (12.5%) must not stop");
        }

        // A-S2-05: 0% protection off — 0 bytes free must not stop.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForHappyPath(t);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 8 * gib, 0, 0, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 0 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-05 0% with 0 bytes free must not stop");
        }

        // A-S2-05b: 0% protection off — Unavailable samples must not fail either.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            for (var i = 0; i < 6; i++)
                health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 0 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-05b 0% with Unavailable inputs must not stop");
        }

        // A-S2-06: Warning (12% free at 10% safety, 15% warning line) must not stop.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForHappyPath(t);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 12 * gib, 10, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-06 Warning (12%) must not stop");
        }

        // A-S2-07: consecutive Unavailable reaching the configured max throws
        // DiskHealthUnavailableException (max=3; the immediate + 2 resamples hit it).
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            for (var i = 0; i < 5; i++)
                health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("A-S2-07 consecutive Unavailable must throw DiskHealthUnavailableException");
            }
            catch (DiskHealthUnavailableException)
            {
                // expected
            }
        }

        // A-S2-08: recovery resets the consecutive count:
        // U, U, Normal, U, U with max=3 must NOT fail (only 2 consecutive at the end).
        // The trailing Normal also proves Normal resets the count: without the
        // reset, the 4th sample (U after N) would already hit 3.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));                              // 1
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));                              // 2
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 90 * gib, 10, DateTimeOffset.UtcNow));               // Normal → reset
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));                              // 1
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 0, 0, 10, DateTimeOffset.UtcNow));                              // 2
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 90 * gib, 10, DateTimeOffset.UtcNow));               // Normal → reset (repeats)
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-08 recovery must reset the consecutive Unavailable count");
        }

        // A-S2-09: disk sampling is time-throttled — a stall loop that never
        // reaches safeEnd must not sample per iteration.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 90 * gib, 10, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            var callCount = health.SampleCount;
            if (result.FinishSucceeded != true)
                failures.Add("A-S2-09 throttled sample run must complete");
            // 2000ms stall / 30ms interval ⇒ ~66 samples; anything near
            // iterations-per-ms would mean per-loop sampling, so >200 fails.
            if (callCount > 200)
                failures.Add($"A-S2-09 disk sampling not time-throttled: {callCount} samples in a 2s stall");
            if (callCount < 10)
                failures.Add($"A-S2-09 disk sampling too sparse (interval too large or clock broken): {callCount}");
        }
    }

    private async Task WatcherTests(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmod_rec_watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 17.18 / 17.19: prefix isolation + same-index different prefix.
            File.WriteAllText(Path.Combine(dir, "frame0001.tga"), "x");
            File.WriteAllText(Path.Combine(dir, "oldSession_0001.tga"), "x");
            File.WriteAllText(Path.Combine(dir, "other_0002.tga"), "x");
            File.WriteAllText(Path.Combine(dir, "mmod_test_0001.tga"), "x");
            var watcher = new TgaDirectoryWatcher(dir, "mmod_test_");
            watcher.Start(acceptPreSessionFiles: true);
            await Task.Delay(300);
            if (watcher.SessionFileCount != 1)
                failures.Add($"17.18 prefix isolation failed: SessionFileCount={watcher.SessionFileCount}");
            watcher.Dispose();

            // 17.10: last TGA slow write — quiescence waits for stable candidate.
            var slowDir = Path.Combine(Path.GetTempPath(), "mmod_rec_slow_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(slowDir);
            var slow = new TgaDirectoryWatcher(slowDir, "slow_");
            slow.Start(acceptPreSessionFiles: false);
            var path = Path.Combine(slowDir, "slow_0001.tga");
            File.WriteAllText(path, "half");
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                File.WriteAllText(path, "half-complete-write");
                await Task.Delay(300);
                File.WriteAllBytes(path, BuildTga(4, 4)); // complete TGA
            });
            try
            {
                await slow.WaitForQuiescenceAsync(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3), CancellationToken.None);
                // quiescence succeeded → the slow writer settled
            }
            catch (TimeoutException)
            {
                failures.Add("17.10 slow final TGA write should settle within quiescence window");
            }
            slow.Dispose();
            try { Directory.Delete(slowDir, true); } catch { }

            // 17.12: quiescence hard timeout when a file keeps being touched.
            var busyDir = Path.Combine(Path.GetTempPath(), "mmod_rec_busy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(busyDir);
            var busyPath = Path.Combine(busyDir, "busy_0001.tga");
            File.WriteAllText(busyPath, "seed"); // pre-create so the first scan tracks it
            var busy = new TgaDirectoryWatcher(busyDir, "busy_");
            busy.Start(acceptPreSessionFiles: false);
            var keepTouching = true;
            var touchTask = Task.Run(async () =>
            {
                while (keepTouching)
                {
                    try { File.AppendAllText(busyPath, "x"); } catch { }
                    await Task.Delay(30);
                }
            });
            try
            {
                await busy.WaitForQuiescenceAsync(TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(1.2), CancellationToken.None);
                failures.Add($"17.12 quiescence hard timeout must throw (candidate={busy.CandidateCount} pending={busy.PendingCount} lastWrite={busy.LastPhysicalFileWriteUtc?.ToString("HH:mm:ss.fff") ?? "null"})");
            }
            catch (TimeoutException)
            {
                // expected
            }
            keepTouching = false;
            await touchTask;
            busy.Dispose();
            try { Directory.Delete(busyDir, true); } catch { }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- retry policy (17.20 / 17.21 / §12) ----

    private Task PolicyTests(List<string> failures)
    {
        // permanent kinds never auto-retry
        foreach (var kind in new[] { RecordingFailureKind.InvalidInput, RecordingFailureKind.UnsupportedReplay, RecordingFailureKind.MapUnavailable, RecordingFailureKind.DiskPressure, RecordingFailureKind.UserCanceled, RecordingFailureKind.DiskHealthUnavailable })
        {
            var d = RecordingRetryPolicy.Decide(kind, 1, 3, cleanupSucceeded: true);
            if (d.Action != RetryAction.NoRetryNeedsUser)
                failures.Add($"retry policy: {kind} must not auto-retry");
        }

        // A-S2-10: both DiskPressure and DiskHealthUnavailable are permanent and
        // classified independently (never reuse DiskPressure for sample failure).
        {
            var dp = RecordingRetryPolicy.Decide(RecordingFailureKind.DiskPressure, 1, 3, cleanupSucceeded: true);
            var du = RecordingRetryPolicy.Decide(RecordingFailureKind.DiskHealthUnavailable, 1, 3, cleanupSucceeded: true);
            if (dp.Action != RetryAction.NoRetryNeedsUser || du.Action != RetryAction.NoRetryNeedsUser)
                failures.Add("A-S2-10 DiskPressure/DiskHealthUnavailable must both be NoRetryNeedsUser");
            if (RecordingFailureClassifier.Classify(new DiskHealthUnavailableException("x")) != RecordingFailureKind.DiskHealthUnavailable)
                failures.Add("A-S2-10 DiskHealthUnavailableException must classify as DiskHealthUnavailable");
            if (RecordingFailureClassifier.Classify(new DiskPressureException("x")) != RecordingFailureKind.DiskPressure)
                failures.Add("A-S2-10 DiskPressureException must classify as DiskPressure");
        }

        // 17.20: stop unconfirmed → restart game, and never same-session
        {
            var d = RecordingRetryPolicy.Decide(RecordingFailureKind.CaptureStopUnconfirmed, 1, 3, cleanupSucceeded: false);
            if (d.Action != RetryAction.RestartGameRetry)
                failures.Add("17.20 stop-unconfirmed must force restart game");
            var d2 = RecordingRetryPolicy.Decide(RecordingFailureKind.CaptureStopUnconfirmed, 1, 3, cleanupSucceeded: true);
            if (d2.Action != RetryAction.RestartGameRetry)
                failures.Add("17.20 stop-unconfirmed must never same-session retry");
        }

        // 17.21: playback evidence timeout with clean cleanup → reload map
        {
            var d = RecordingRetryPolicy.Decide(RecordingFailureKind.PlaybackEvidenceTimeout, 1, 3, cleanupSucceeded: true);
            if (d.Action != RetryAction.ReloadMapRetry)
                failures.Add("17.21 playback-evidence-timeout must reload map");
        }

        // dirty cleanup escalates to restart regardless of kind
        {
            var d = RecordingRetryPolicy.Decide(RecordingFailureKind.CaptureStartFailed, 1, 3, cleanupSucceeded: false);
            if (d.Action != RetryAction.RestartGameRetry)
                failures.Add("dirty cleanup must escalate to restart game");
        }

        // max attempts exhausted
        {
            var d = RecordingRetryPolicy.Decide(RecordingFailureKind.TgaWriteStalled, 3, 3, cleanupSucceeded: true);
            if (d.Action != RetryAction.NoRetryNeedsUser)
                failures.Add("max attempts must stop retrying");
        }

        // state machine guard: illegal transition rejected
        try
        {
            RecordingStateMachine.AssertTransition(NodeExecutionStage.StartingReplay, NodeExecutionStage.Completed);
            failures.Add("state machine must reject StartingReplay → Completed");
        }
        catch (InvalidOperationException) { }
        RecordingStateMachine.AssertTransition(NodeExecutionStage.StartingReplay, NodeExecutionStage.WaitingPlaybackEvidence); // legal

        return Task.CompletedTask;
    }

    // ---- media probe (17.22 / 17.23) ----

    private void MediaProbeTests(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmod_mp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var valid = Path.Combine(dir, "valid.mp4");
            File.WriteAllBytes(valid, TestMp4.Build(width: 1920, height: 1080, timescale: 1000, duration: 5000, samples: 300));

            var probe = new MediaProbe().Probe(valid, expectedWidth: 1920, expectedHeight: 1080, expectedFps: 60);
            if (!probe.IsValid)
                failures.Add($"17.22 valid mp4 rejected: {probe.Error}");
            if (probe.IsValid && Math.Abs(probe.FrameCount - 300) > 1)
                failures.Add($"17.22 frame count mismatch: {probe.FrameCount}");

            // truncated: no moov
            var truncated = Path.Combine(dir, "truncated.mp4");
            File.WriteAllBytes(truncated, TestMp4.Build(1920, 1080, 1000, 5000, 300, withMoov: false));
            var t = new MediaProbe().Probe(truncated);
            if (t.IsValid)
                failures.Add("17.22 truncated mp4 must fail validation");

            // wrong fps: 100 samples over 5s = 20fps
            var wrongFps = Path.Combine(dir, "wrongfps.mp4");
            File.WriteAllBytes(wrongFps, TestMp4.Build(1920, 1080, 1000, 5000, 100));
            var w = new MediaProbe().Probe(wrongFps, expectedFps: 60);
            if (w.IsValid)
                failures.Add("17.23 wrong fps must fail validation");

            // wrong resolution
            var wrongRes = Path.Combine(dir, "wrongres.mp4");
            File.WriteAllBytes(wrongRes, TestMp4.Build(640, 360, 1000, 5000, 300));
            var r = new MediaProbe().Probe(wrongRes, expectedWidth: 1920, expectedHeight: 1080);
            if (r.IsValid)
                failures.Add("17.23 wrong resolution must fail validation");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- M3: rolling rate window + performance tracker (pure fakes) ----

    private void TelemetryTests(List<string> failures)
    {
        // 1. Window not ready: no samples → null rate, and the snapshot never
        //    contains NaN/Infinity or a bogus ratio.
        {
            var rw = new RateWindow(TimeSpan.FromSeconds(10));
            if (rw.GetRatePerSecond() is not null)
                failures.Add("M3 rate window must be null before any sample");
            var tracker = new CapturePerformanceTracker();
            var s = tracker.BuildSnapshot(ProcessingBackend.Unknown, EncoderBackend.Unknown, 0, 0);
            if (s.ProducedFramesPerSecond != 0 || s.ConsumedFramesPerSecond != 0 || s.OutputFramesPerSecond != 0)
                failures.Add("M3 empty snapshot must have zero rates");
            if (!double.IsFinite(s.ConsumptionRatio) || double.IsNaN(s.ConsumptionRatio) || double.IsInfinity(s.ConsumptionRatio))
                failures.Add("M3 empty snapshot ratio must be finite");
            if (s.BacklogTrend != BacklogTrend.Unknown || s.CatchUpSeconds is not null)
                failures.Add("M3 empty snapshot must be Unknown trend with no catch-up");
        }

        // 2. Monotonic growth: samples every 10ms with counts scaled by the
        //    measured elapsed time so the rate converges to the intended
        //    100/50/50 fps regardless of OS timer resolution.
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 1; i <= 30; i++)
            {
                var elapsed = sw.Elapsed.TotalSeconds;
                tracker.AddSample(new CapturePerformanceTracker.Sample(
                    Produced: elapsed * 100, Consumed: elapsed * 50, Output: elapsed * 50,
                    PendingFrames: 0, PendingBytes: 0));
                Thread.Sleep(10);
            }

            var s = tracker.BuildSnapshot(ProcessingBackend.Disabled, EncoderBackend.Software, 0, 0);
            if (!(s.ProducedFramesPerSecond > 0))
                failures.Add("M3 growth must produce a positive produced rate");
            if (s.ProducedFramesPerSecond < 80 || s.ProducedFramesPerSecond > 120)
                failures.Add($"M3 produced rate out of range: {s.ProducedFramesPerSecond}");
            if (s.ConsumedFramesPerSecond < 40 || s.ConsumedFramesPerSecond > 60)
                failures.Add($"M3 consumed rate out of range: {s.ConsumedFramesPerSecond}");
            if (Math.Abs(s.ConsumptionRatio - 0.5) > 0.15)
                failures.Add($"M3 consumption ratio must be ~0.5: {s.ConsumptionRatio}");
        }

        // 3. Counter reset must not produce negative/NaN rates.
        {
            var rw = new RateWindow(TimeSpan.FromSeconds(10));
            rw.AddSample(100);
            rw.AddSample(200);
            rw.AddSample(10); // reset to a new session
            rw.AddSample(30);
            var rate = rw.GetRatePerSecond();
            if (rate is null || rate < 0 || double.IsNaN(rate.Value) || double.IsInfinity(rate.Value))
                failures.Add($"M3 counter reset must yield a safe rate, got {rate}");
        }

        // 4. Zero interval (same instant) must not divide by zero.
        {
            var rw = new RateWindow(TimeSpan.FromSeconds(10));
            rw.AddSample(100);
            rw.AddSample(100); // zero delta
            if (rw.GetRatePerSecond() != 0)
                failures.Add("M3 zero delta must yield 0 rate");
        }

        // 5. Window expiry: old samples fall out of the window.
        {
            var rw = new RateWindow(TimeSpan.FromMilliseconds(10));
            rw.AddSample(0);
            Thread.Sleep(30); // > 10ms window
            rw.AddSample(100);
            rw.AddSample(200); // the second sample establishes a new baseline
            var rate = rw.GetRatePerSecond();
            if (rate is null || rate <= 0)
                failures.Add("M3 window expiry must keep only recent samples");
        }

        // 6. Trend dead zone: small deltas are Stable, big deltas are
        //    Growing/Shrinking, no production rate is Unknown.
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10), deadZoneRatio: 0.05);
            if (tracker.EvaluateTrend(0, 100) != BacklogTrend.Unknown)
                failures.Add("M3 trend must be Unknown with no production rate");
            if (tracker.EvaluateTrend(100, 100) != BacklogTrend.Stable)
                failures.Add("M3 equal rates must be Stable");
            if (tracker.EvaluateTrend(100, 103) != BacklogTrend.Stable)
                failures.Add("M3 3% delta must stay inside the 5% dead zone");
            if (tracker.EvaluateTrend(100, 80) != BacklogTrend.Growing)
                failures.Add("M3 consumption below production must be Growing");
            if (tracker.EvaluateTrend(100, 120) != BacklogTrend.Shrinking)
                failures.Add("M3 consumption above production must be Shrinking");
        }

        // 7. Catch-up time: only when a backlog exists and consumption clearly
        //    outpaces production. Must never be computed as a task ETA.
        {
            // Growing backlog: produced 100, consumed 20 → catch-up is null.
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            var swGrow = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 1; i <= 20; i++)
            {
                var elapsed = swGrow.Elapsed.TotalSeconds;
                tracker.AddSample(new CapturePerformanceTracker.Sample(
                    Produced: elapsed * 100, Consumed: elapsed * 20, Output: elapsed * 20,
                    PendingFrames: 100, PendingBytes: 0));
                Thread.Sleep(10);
            }

            var growing = tracker.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 100, 0);
            if (growing.CatchUpSeconds is not null)
                failures.Add("M3 catch-up must be null when production outpaces consumption");

            // Shrinking: consumed 120 vs produced 100 → pending / 20.
            var tracker2 = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            var swShrink = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 1; i <= 20; i++)
            {
                var elapsed = swShrink.Elapsed.TotalSeconds;
                tracker2.AddSample(new CapturePerformanceTracker.Sample(
                    Produced: elapsed * 100, Consumed: elapsed * 120, Output: elapsed * 120,
                    PendingFrames: 100, PendingBytes: 0));
                Thread.Sleep(10);
            }

            var shrinking = tracker2.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 100, 0);
            if (shrinking.CatchUpSeconds is null || Math.Abs(shrinking.CatchUpSeconds.Value - 5.0) > 2.0)
                failures.Add($"M3 catch-up must be ~5s for 100 pending at 20fps drain: {shrinking.CatchUpSeconds}");

            // No backlog → no catch-up even with fast consumption.
            var noBacklog = tracker2.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 0, 0);
            if (noBacklog.CatchUpSeconds is not null)
                failures.Add("M3 catch-up must be null with no backlog");
        }

        // 8. Peak backlog tracking (frames and bytes, long).
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            tracker.AddSample(new CapturePerformanceTracker.Sample(0, 0, 0, PendingFrames: 5, PendingBytes: 5000));
            tracker.AddSample(new CapturePerformanceTracker.Sample(0, 0, 0, PendingFrames: 8, PendingBytes: 8000));
            tracker.AddSample(new CapturePerformanceTracker.Sample(0, 0, 0, PendingFrames: 3, PendingBytes: 3000));
            var s = tracker.BuildSnapshot(ProcessingBackend.Disabled, EncoderBackend.Software, 3, 3000);
            if (s.Backlog.PeakPendingFrames != 8 || s.Backlog.PeakPendingBytes != 8000)
                failures.Add($"M3 peak backlog must track the maximum: {s.Backlog}");
        }

        // 9. Ratio is Unknown when there is no valid production rate, and
        //    never NaN/Infinity.
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            tracker.AddSample(new CapturePerformanceTracker.Sample(Produced: 0, Consumed: 50, Output: 50, PendingFrames: 0, PendingBytes: 0));
            var s = tracker.BuildSnapshot(ProcessingBackend.Unknown, EncoderBackend.Unknown, 0, 0);
            if (s.ConsumptionRatio != 0 || !double.IsFinite(s.ConsumptionRatio))
                failures.Add("M3 ratio must be 0 (unknown) without a production rate");
        }
    }

    // ---- M3: watcher backlog/produced/read-failure telemetry ----

    private void WatcherTelemetryTests(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmod_tele_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Produced counts only stable frames; duplicate events cannot
            // inflate it; candidates never count as produced.
            var path1 = Path.Combine(dir, "tel_0001.tga");
            var path2 = Path.Combine(dir, "tel_0002.tga");
            File.WriteAllBytes(path1, BuildTga(4, 4));
            File.WriteAllBytes(path2, BuildTga(4, 4));
            var watcher = new TgaDirectoryWatcher(dir, "tel_");
            watcher.Start(acceptPreSessionFiles: true);
            Thread.Sleep(700); // let candidates stabilize
            watcher.ForceFullScan();
            if (watcher.ProducedCount != 2)
                failures.Add($"M3 produced must count 2 stable frames, got {watcher.ProducedCount}");
            if (watcher.CandidateCount != 0)
                failures.Add($"M3 candidates must drain after stabilization, got {watcher.CandidateCount}");
            if (watcher.PendingBytes <= 0)
                failures.Add($"M3 pending bytes must be positive, got {watcher.PendingBytes}");
            if (watcher.PeakPendingFrames < 2 || watcher.PeakPendingBytes <= 0)
                failures.Add("M3 peak backlog must be tracked by the watcher");

            // Consumed: remove frames one at a time; bytes shrink accordingly.
            watcher.TryTake(1, out _);
            if (watcher.PendingCount != 1 || watcher.PendingBytes <= 0)
                failures.Add("M3 pending bytes must shrink after take");
            watcher.TryTake(2, out _);
            if (watcher.PendingCount != 0 || watcher.PendingBytes != 0)
                failures.Add($"M3 pending must be empty with 0 bytes after drain, got {watcher.PendingBytes}");

            // File disappears → read failure flagged, no exception.
            File.WriteAllBytes(path1, BuildTga(4, 4));
            watcher.ForceFullScan();
            Thread.Sleep(700);
            watcher.ForceFullScan();
            File.Delete(path1); // vanishes while pending
            watcher.ForceFullScan();
            Thread.Sleep(100);
            watcher.ForceFullScan();
            if (!watcher.HasPendingReadFailure)
                failures.Add("M3 vanished pending file must set the read-failure flag");
            watcher.Dispose();

            // Duplicate FS events for the same index must not re-count.
            var dupDir = Path.Combine(Path.GetTempPath(), "mmod_dup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dupDir);
            var dup = new TgaDirectoryWatcher(dupDir, "dup_");
            dup.Start(acceptPreSessionFiles: true);
            File.WriteAllBytes(Path.Combine(dupDir, "dup_0001.tga"), BuildTga(4, 4));
            File.WriteAllBytes(Path.Combine(dupDir, "dup_0002.tga"), BuildTga(4, 4));
            Thread.Sleep(700);
            dup.ForceFullScan();
            Thread.Sleep(100);
            dup.ForceFullScan();
            if (dup.ProducedCount != 2)
                failures.Add($"M3 duplicate events must not inflate produced: {dup.ProducedCount}");
            dup.Dispose();
            try { Directory.Delete(dupDir, true); } catch { }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static byte[] BuildTga(int w, int h)
    {
        var header = new byte[18];
        header[2] = 2;
        header[12] = (byte)(w & 0xFF);
        header[13] = (byte)(w >> 8);
        header[14] = (byte)(h & 0xFF);
        header[15] = (byte)(h >> 8);
        header[16] = 24;
        var pixels = new byte[w * h * 3];
        return [.. header, .. pixels];
    }
}

/// <summary>Minimal structurally-valid MP4 builder for MediaProbe tests.</summary>
internal static class TestMp4
{
    public static byte[] Build(int width, int height, uint timescale, uint duration, uint samples, bool withMoov = true)
    {
        var boxes = new List<byte[]>();
        boxes.Add(Box("ftyp", Concat(Ascii("isom"), U32(0), Ascii("isom"))));
        if (withMoov)
        {
            var mvhd = Box("mvhd", Concat(U32(0), U32(0), U32(0), U32(timescale), U32(duration), U32(0x00010000)));
            var tkhd = Box("tkhd", Concat(U32(7), U32(0), U32(0), U32(1), U32(0), U32(duration),
                U64(0), U16(0), U16(0), U16(0x0100), U16(0), new byte[36],
                U32((uint)(width << 16)), U32((uint)(height << 16))));
            var mdhd = Box("mdhd", Concat(U32(0), U32(0), U32(0), U32(timescale), U32(duration), U16(0x55C4), U16(0)));
            var stsd = Box("stsd", Concat(U32(0), U32(1), new byte[8]));
            var stts = Box("stts", Concat(U32(0), U32(1), U32(samples), U32(Math.Max(1, duration / Math.Max(1, samples)))));
            var stbl = Box("stbl", Concat(stsd, stts));
            var minf = Box("minf", stbl);
            var mdia = Box("mdia", Concat(mdhd, minf));
            var trak = Box("trak", Concat(tkhd, mdia));
            boxes.Add(Box("moov", Concat(mvhd, trak)));
        }
        return Concat(boxes.ToArray());
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var size = 8 + payload.Length;
        return Concat(U32((uint)size), Ascii(type), payload);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }
        return result;
    }

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
    private static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] U64(ulong v) => [.. U32((uint)(v >> 32)), .. U32((uint)v)];
}

/// <summary>Deterministic fakes for the recorder scenarios.</summary>
internal static class Fakes
{
    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForHappyPath(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 1000, ActivityAnchorFrame = 3, HasVisualChange = true, AutoGrow = true };
        pipe.FinalizeResult = new PipelineFinalizeResult(1000, 1000, 0, 999, "out.mp4", true);
        return (net, pipe, new FakeHealth());
    }

    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForFaultyPipeline(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 10, ActivityAnchorFrame = 3, HasVisualChange = true };
        pipe.CompletionSrc.SetException(new PipelineFaultException("injected", new Exception("boom")));
        return (net, pipe, new FakeHealth());
    }

    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForFinalizeFault(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 1000, ActivityAnchorFrame = 3, HasVisualChange = true, AutoGrow = true };
        pipe.OnFinalize = (_, _) => throw new InvalidOperationException("native finish failed");
        return (net, pipe, new FakeHealth());
    }

    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForStopTimeout(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        net.OnStrict = (cmd) =>
        {
            if (cmd == "endmovie")
                throw new TimeoutException("endmovie timeout");
            return Task.FromResult(new NetConCommandResult(cmd, "m", DateTime.UtcNow, DateTime.UtcNow, [], null));
        };
        var pipe = new FakePipeline(timeouts) { FedCount = 1000, ActivityAnchorFrame = 3, HasVisualChange = true, AutoGrow = true };
        pipe.FinalizeResult = new PipelineFinalizeResult(1000, 1000, 0, 999, "out.mp4", true);
        return (net, pipe, new FakeHealth());
    }

    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForGameExit(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 10, ActivityAnchorFrame = 3, HasVisualChange = true };
        var health = new FakeHealth();
        health.ExitSrc.SetResult();
        return (net, pipe, health);
    }

    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForCancel(RecordingTimeoutPolicy timeouts)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 10, ActivityAnchorFrame = 3, HasVisualChange = true };
        return (net, pipe, new FakeHealth());
    }

    /// <summary>Recording that stalls below safeEnd: the Capturing loop spins
    /// (sampling disk per interval) until it is released or fails.</summary>
    public static (FakeNetCon Net, FakePipeline Pipe, FakeHealth Health) ForStall(RecordingTimeoutPolicy timeouts, int stall)
    {
        var net = new FakeNetCon();
        var pipe = new FakePipeline(timeouts) { FedCount = 10, ActivityAnchorFrame = 3, HasVisualChange = true };
        _ = Task.Run(async () =>
        {
            await Task.Delay(stall);
            pipe.FedCount = 5000;
            pipe.CompletionSrc.TrySetResult();
        });
        return (net, pipe, new FakeHealth());
    }
}

internal sealed class FakeNetCon : INetConClient
{
    public bool IsConnected { get; set; } = true;
    public event Action<string>? OutputReceived { add { } remove { } }
    public List<string> Commands { get; } = [];
    public Func<string, Task<NetConCommandResult>>? OnStrict { get; set; }
    public Func<string, Task>? OnSend { get; set; }

    public Task ConnectAsync(int port, string password, TimeSpan timeout, CancellationToken token)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string command, CancellationToken token)
    {
        Commands.Add(command);
        return OnSend?.Invoke(command) ?? Task.CompletedTask;
    }

    public Task<NetConCommandResult> ExecuteStrictAsync(string command, TimeSpan timeout, IReadOnlyList<string> failurePatterns, CancellationToken token)
    {
        Commands.Add(command);
        if (OnStrict is not null)
            return OnStrict(command);
        return Task.FromResult(new NetConCommandResult(command, "m", DateTime.UtcNow, DateTime.UtcNow, [], null));
    }

    public Task ExecuteAsync(string command, TimeSpan timeout, CancellationToken token)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakePipeline : ICapturePipeline
{
    private readonly RecordingTimeoutPolicy _timeouts;
    public FakePipeline(RecordingTimeoutPolicy timeouts)
    {
        _timeouts = timeouts;
        Watcher = new FakeWatcher();
    }

    public long FedCount { get; set; }
    public PipelineState State { get; set; } = PipelineState.Processing;
    public Exception? Fault { get; set; }
    public bool IsFaulted => Fault is not null || CompletionSrc.Task.IsFaulted;
    public string? OutputPath { get; set; } = "out.mp4";
    public string? CaptureSessionId { get; set; } = "sess-fake";
    public ITgaCaptureWatcher Watcher { get; set; }
    public PerformanceSnapshot Performance { get; set; } = PerformanceSnapshot.Empty;
    public TaskCompletionSource CompletionSrc { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => CompletionSrc.Task;
    public bool HasVisualChange { get; set; }
    public int? ActivityAnchorFrame { get; set; }
    public int? LastVisualChangeFrame { get; set; }
    public PipelineFinalizeResult? FinalizeResult { get; set; }
    public Func<RecordingTimeoutPolicy, CancellationToken, Task<PipelineFinalizeResult>>? OnFinalize { get; set; }
    public int FinalizeCount { get; private set; }
    public bool SimulateEvidence { get; set; } = true;
    public bool AutoGrow { get; set; }
    private CancellationTokenSource? _growCts;

    private void EnsureAutoGrow()
    {
        if (!AutoGrow || _growCts is not null)
            return;
        _growCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_growCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(20, _growCts.Token);
                    FedCount += 1000;
                    CompletionSrc.TrySetResult();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public Task WaitUntilFedAsync(int minimumFed, TimeSpan timeout, CancellationToken token)
    {
        EnsureAutoGrow();
        return WaitUntilAsync(() => FedCount >= minimumFed, timeout, "fed timeout", token);
    }

    public async Task WaitUntilActivityAsync(TimeSpan timeout, CancellationToken token)
    {
        EnsureAutoGrow();
        var deadline = DateTime.UtcNow + timeout;
        while (ActivityAnchorFrame is null)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("activity timeout");
            // Simulate playback evidence arriving (like the real probe).
            if (SimulateEvidence)
            {
                ActivityAnchorFrame = (int)Math.Max(3, FedCount);
                HasVisualChange = true;
                return;
            }
            await Task.WhenAny(Task.Delay(10, token), Completion);
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(message);
            await Task.WhenAny(Task.Delay(10, token), Completion);
        }
    }

    public void ResetActivityTracking()
    {
        ActivityAnchorFrame = null;
        HasVisualChange = false;
        CompletionSrc.TrySetResult();
    }

    public void ThrowIfFaulted()
    {
        if (IsFaulted)
            throw new PipelineFaultException(Fault?.Message ?? "faulted", Fault ?? new Exception("faulted"));
    }

    public Task<PipelineFinalizeResult> FinalizeAsync(RecordingTimeoutPolicy timeouts, CancellationToken token)
    {
        FinalizeCount++;
        if (OnFinalize is not null)
            return OnFinalize(timeouts, token);
        return Task.FromResult(FinalizeResult ?? new PipelineFinalizeResult(FedCount, FedCount, 0, (int)FedCount - 1, "out.mp4", true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeWatcher : ITgaCaptureWatcher
{
    public int PendingCount { get; set; }
    public int CandidateCount { get; set; }
    public DateTime? LastPhysicalFileWriteUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastStableFrameUtc { get; set; }
    public int? LastAcceptedFrameIndex { get; set; }
    public int MaxObservedFrameIndex { get; set; }
    public int SessionFileCount { get; set; }
    public long PendingBytes { get; set; }
    public long ProducedCount { get; set; }
    public long PeakPendingFrames { get; set; }
    public long PeakPendingBytes { get; set; }
    public bool HasPendingReadFailure { get; set; }
    public bool HasUnstableFiles { get; set; }
    public bool IsFrozen { get; set; }
    public string SequencePrefix { get; set; } = "mmod_fake_";
    public bool ThrowQuiescence { get; set; }
    public Action? OnQuiescence { get; set; }

    public void ForceFullScan() { }

    public Task WaitForQuiescenceAsync(TimeSpan quietWindow, TimeSpan hardTimeout, CancellationToken token)
    {
        OnQuiescence?.Invoke();
        if (ThrowQuiescence)
            throw new TimeoutException("quiescence hard timeout");
        return Task.CompletedTask;
    }

    public void Freeze() => IsFrozen = true;
    public bool TryTake(int frameIndex, out string filePath) { filePath = string.Empty; return false; }
    public bool TryGetMinPendingFrameIndex(out int frameIndex) { frameIndex = -1; return false; }
    public void CleanupSessionFiles() { }
    public void Dispose() { }
}

internal sealed class FakeHealth : IGameSessionHealthMonitor
{
    public TaskCompletionSource ExitSrc { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task GameExitedTask => ExitSrc.Task;
    public bool IsGameRunning { get; set; } = true;

    /// <summary>Deterministic snapshots served in call order; last one repeats.</summary>
    public List<DiskHealthSnapshot> Snapshots { get; } = [];

    /// <summary>Number of GetWatchDiskHealth calls (throttling evidence).</summary>
    public int SampleCount { get; private set; }

    /// <summary>Last safetyPercent the recorder asked for.</summary>
    public int LastRequestedSafetyPercent { get; private set; }

    public DiskHealthSnapshot GetWatchDiskHealth(int safetyPercent)
    {
        SampleCount++;
        LastRequestedSafetyPercent = safetyPercent;
        if (Snapshots.Count == 0)
            return DiskSafetyPolicy.EvaluateSnapshot("C:\\", 1024L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, safetyPercent, DateTimeOffset.UtcNow);
        // When safety is off (0%), the production monitor never reads the drive
        // and always reports Disabled — mirror that semantics on the last entry.
        if (DiskSafetyPolicy.NormalizeSafetyPercent(safetyPercent) == 0)
            return DiskSafetyPolicy.EvaluateSnapshot("", 0, 0, 0, DateTimeOffset.UtcNow);
        var index = Math.Min(SampleCount - 1, Snapshots.Count - 1);
        return Snapshots[index];
    }
}
