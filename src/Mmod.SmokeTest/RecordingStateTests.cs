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
        PreflightTests(failures);
        WatcherTelemetryTests(failures);
        WatcherDedupTests(failures);
        await DiskPressureControlledStopTests(failures);
        await PartialPersistenceTests(failures);
        await PartialCrashRecoveryTests(failures);
    }

    private static void PreflightTests(List<string> failures)
    {
        static PerformanceSnapshot Snapshot(double ratio, BacklogTrend trend,
            ProcessingBackend quality = ProcessingBackend.Gpu,
            EncoderBackend encoder = EncoderBackend.Software) => new(
                100, 100 * ratio, 10, ratio,
                new BacklogSnapshot(2, 2048, 8, 8192), trend, null,
                quality, encoder, DateTimeOffset.UtcNow);

        if (PerformancePreflightEvaluator.Evaluate(Snapshot(0.98, BacklogTrend.Stable), true).Rating != PerformancePreflightRating.Pass)
            failures.Add("M5 preflight ratio == 0.98 must Pass when backlog is stable");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(0.90, BacklogTrend.Stable), true).Rating != PerformancePreflightRating.Marginal)
            failures.Add("M5 preflight ratio == 0.90 must be Marginal");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(0.899, BacklogTrend.Stable), true).Rating != PerformancePreflightRating.Fail)
            failures.Add("M5 preflight ratio below 0.90 must Fail");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(1.0, BacklogTrend.Growing), true).Rating != PerformancePreflightRating.Fail)
            failures.Add("M5 preflight growing backlog must Fail");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(1.0, BacklogTrend.Stable), false).Rating != PerformancePreflightRating.Unknown)
            failures.Add("M5 preflight insufficient window must be Unknown");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(1.0, BacklogTrend.Stable, ProcessingBackend.Unknown), true).Rating != PerformancePreflightRating.Unknown)
            failures.Add("M5 preflight unknown backend must be Unknown");
        if (PerformancePreflightEvaluator.Evaluate(Snapshot(1.0, BacklogTrend.Stable), true, hasPendingReadFailure: true).Rating != PerformancePreflightRating.Unknown)
            failures.Add("M5 preflight pending read failure must be Unknown");
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

        // A-S2-03: 100 GiB volume with 9 GiB free at 10% must trigger the
        // controlled stop (M4): endmovie → quiescence → drain → Finish, then
        // throw DiskPressure carrying the ControlledStop result.
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
                if (ex.ControlledStop is null)
                    failures.Add("A-S2-03 DiskPressure must carry a ControlledStop after the controlled sequence");
                else if (ex.ControlledStop.Finalize.FinishSucceeded != true || pipe.FinalizeCount < 1)
                    failures.Add("A-S2-03 controlled stop must have run finalize");
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

    // ---- M4: DiskPressure controlled stop + partial lifecycle ----

    private async Task DiskPressureControlledStopTests(List<string> failures)
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
        DiskHealthSnapshot CriticalSnap() =>
            DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 9 * gib, 10, DateTimeOffset.UtcNow);

        // M4-01: controlled-stop endmovie failure → CaptureStopUnconfirmed and
        //        no ControlledStop (no partial may be recorded).
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            net.OnStrict = (cmd) =>
            {
                if (cmd == "endmovie")
                    throw new TimeoutException("endmovie timeout");
                return Task.FromResult(new NetConCommandResult(cmd, "m", DateTime.UtcNow, DateTime.UtcNow, [], null));
            };
            health.Snapshots.Add(CriticalSnap());
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("M4-01 endmovie failure must throw");
            }
            catch (CaptureStopUnconfirmedException ex)
            {
                if (ex.FailureKind != RecordingFailureKind.CaptureStopUnconfirmed)
                    failures.Add("M4-01 must classify as CaptureStopUnconfirmed");
            }
            catch (Exception ex)
            {
                failures.Add($"M4-01 unexpected exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // M4-02: quiescence failure during the controlled stop → no
        //        ControlledStop (no partial may be recorded).
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            pipe.Watcher = new FakeWatcher { ThrowQuiescence = true };
            health.Snapshots.Add(CriticalSnap());
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("M4-02 quiescence failure must throw");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is DiskPressureException { ControlledStop: not null })
                    failures.Add("M4-02 quiescence failure must not produce a ControlledStop");
            }
        }

        // M4-03: finalize failure during the controlled stop → no ControlledStop.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            pipe.OnFinalize = (_, _) => throw new InvalidOperationException("native finish failed");
            health.Snapshots.Add(CriticalSnap());
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("M4-03 finalize failure must throw");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is DiskPressureException { ControlledStop: not null })
                    failures.Add("M4-03 finalize failure must not produce a ControlledStop");
            }
        }

        // M4-04: successful controlled stop carries real output facts.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForStall(t, stall: 2000);
            pipe.FinalizeResult = new PipelineFinalizeResult(1000, 800, 0, 999, "out.mp4", true, 1920, 1080);
            health.Snapshots.Add(CriticalSnap());
            try
            {
                await CaptureEnvelopeRecorder.RecordAsync(
                    net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                    health, null, null, CancellationToken.None, t, _ => { });
                failures.Add("M4-04 controlled stop must throw DiskPressure");
            }
            catch (DiskPressureException ex)
            {
                if (ex.ControlledStop is null)
                {
                    failures.Add("M4-04 controlled stop must carry the result");
                }
                else
                {
                    if (ex.ControlledStop.OutputFrames != 800 || ex.ControlledStop.SubmittedFrames != 1000)
                        failures.Add($"M4-04 controlled stop facts wrong: {ex.ControlledStop}");
                    if (ex.ControlledStop.Finalize.FirstFrameWidth != 1920)
                        failures.Add("M4-04 controlled stop must carry first-frame geometry");
                    if (pipe.FinalizeCount < 1)
                        failures.Add("M4-04 controlled stop must run finalize");
                }
            }
        }

        // M4-05: Warning and 0% Disabled never enter the partial flow.
        {
            var t = DiskTimeouts();
            var (net, pipe, health) = Fakes.ForHappyPath(t);
            health.Snapshots.Add(DiskSafetyPolicy.EvaluateSnapshot("R:\\", 100 * gib, 12 * gib, 10, DateTimeOffset.UtcNow));
            var result = await CaptureEnvelopeRecorder.RecordAsync(
                net, pipe, new UserSettings { SupersamplingMultiplier = 1, DiskSafetyFreePercent = 10 }, "replays/x.mtv", 1.0,
                health, null, null, CancellationToken.None, t, _ => { });
            if (result.FinishSucceeded != true)
                failures.Add("M4-05 Warning must not stop recording");
        }
    }

    // ---- M4: partial persistence + crash recovery (pure repository fakes) ----

    private async Task PartialPersistenceTests(List<string> failures)
    {
        var db = Path.Combine(Path.GetTempPath(), "mmod_m4_" + Guid.NewGuid().ToString("N") + ".db");
        var repo = new RenderTaskRepository(db);
        try
        {
            var settings = new RenderSettingsSnapshot(10, 0.5, @"R:\", @"C:\Videos", @"C:\Game", false, 60, 0);
            var taskId = repo.CreateTask(new NewRenderTask("map", "player", 1, @"C:\Videos\out.mp4", settings,
                [new NewRenderNode(@"C:\record.mtv", 1, 0, 12.3, 738)]));
            var nodeId = repo.GetNodes(taskId).Single().Id;
            var session = CaptureSessionInfo.Create(taskId, 0, 1);
            var attemptId = repo.CreateAttempt(new RenderAttemptRecord(
                Id: Guid.NewGuid().ToString("N"),
                SessionId: session.CaptureSessionId,
                TaskId: taskId,
                NodeId: nodeId,
                AttemptNumber: 1,
                Stage: NodeExecutionStage.DiskPressureRequested,
                SequencePrefix: session.SequencePrefix,
                TempClipPath: @"C:\work\attempt_1.encoding.mp4",
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                FinishedAt: null,
                LastError: null,
                FailureKind: RecordingFailureKind.DiskPressure,
                CleanupState: CaptureCleanupState.Clean,
                GameProcessId: null,
                GameProcessStartedUtc: null,
                NetConPort: null,
                ExpectedMap: "map",
                FedCount: 100,
                SubmittedFrameCount: 100,
                LastTgaIndex: 99));

            // Old-record equivalent: fresh attempt reads as "no partial".
            var fresh = repo.GetAttemptsForNode(taskId, nodeId).Single();
            if (fresh.PartialState != PartialState.None || fresh.PartialPath is not null || fresh.PartialOutputFrames is not null)
                failures.Add("M4 fresh attempt must read as no partial");

            // ---- M4-B-002: illegal transitions are rejected ----
            // None → Validated directly must throw (no guard bypass).
            try
            {
                repo.UpdateAttemptPartial(attemptId, @"C:\work\attempt_1.partial.mp4", DateTimeOffset.UtcNow, 800, "DiskPressure Critical");
                failures.Add("M4-B-002 None → Validated must be rejected");
            }
            catch (InvalidOperationException) { }

            // Wrong attempt ID must throw (0 rows).
            try
            {
                repo.MarkAttemptPartialPending("no-such-attempt", @"C:\work\x.partial.mp4", "DiskPressure Critical");
                failures.Add("M4-B-002 unknown attempt must throw");
            }
            catch (InvalidOperationException) { }

            // Pending → Clear resets all metadata to zero.
            repo.MarkAttemptPartialPending(attemptId, @"C:\work\attempt_1.partial.mp4", "DiskPressure Critical");
            var pending = repo.GetAttemptsForNode(taskId, nodeId).Single();
            if (pending.PartialState != PartialState.Pending || pending.PartialPath != @"C:\work\attempt_1.partial.mp4")
                failures.Add("M4-B-002 Pending intent must persist path");
            repo.ClearAttemptPartial(attemptId);
            var cleared = repo.GetAttemptsForNode(taskId, nodeId).Single();
            if (cleared.PartialState != PartialState.None || cleared.PartialPath is not null ||
                cleared.PartialValidatedAt is not null || cleared.PartialOutputFrames is not null || cleared.PartialReason is not null)
                failures.Add("M4-B-002 Pending → Clear must zero all partial metadata");

            // Validated → Clear must be rejected (Clear only touches Pending).
            repo.MarkAttemptPartialPending(attemptId, @"C:\work\attempt_1.partial.mp4", "DiskPressure Critical");
            var validatedAt = DateTimeOffset.UtcNow;
            repo.UpdateAttemptPartial(attemptId, @"C:\work\attempt_1.partial.mp4", validatedAt, 800, "DiskPressure Critical");
            try
            {
                repo.ClearAttemptPartial(attemptId);
                failures.Add("M4-B-002 Validated → Clear must be rejected");
            }
            catch (InvalidOperationException) { }

            // Repeated Validated (Validated → Validated) must throw.
            try
            {
                repo.UpdateAttemptPartial(attemptId, @"C:\work\attempt_1.partial.mp4", DateTimeOffset.UtcNow, 800, "DiskPressure Critical");
                failures.Add("M4-B-002 repeated Validated must be rejected");
            }
            catch (InvalidOperationException) { }

            var stored = repo.GetAttemptsForNode(taskId, nodeId).Single();
            if (stored.PartialState != PartialState.Validated)
                failures.Add($"M4 partial must persist as Validated, got {stored.PartialState}");
            if (stored.PartialPath != @"C:\work\attempt_1.partial.mp4")
                failures.Add("M4 partial path must round-trip");
            if (stored.PartialOutputFrames != 800)
                failures.Add($"M4 partial output frames must round-trip, got {stored.PartialOutputFrames}");
            if (stored.PartialValidatedAt is null)
                failures.Add("M4 partial validated-at must round-trip");
            if (stored.PartialReason is null || !stored.PartialReason.Contains("DiskPressure", StringComparison.Ordinal))
                failures.Add("M4 partial reason must round-trip");

            // Reopen the DB: idempotent migration, values survive.
            var reopened = new RenderTaskRepository(db);
            var reread = reopened.GetAttemptsForNode(taskId, nodeId).Single();
            if (reread.PartialState != PartialState.Validated || reread.PartialPath != @"C:\work\attempt_1.partial.mp4")
                failures.Add("M4 partial must survive DB reopen");
            if (reopened.GetAttemptsWithValidatedPartial().Count != 1)
                failures.Add("M4 GetAttemptsWithValidatedPartial must find the attempt");

            // Terminal attempt + validated partial: node stays not-completed,
            // ClipPath stays null (merge can never include partials).
            var node = reopened.GetNodes(taskId).Single();
            if (node.ClipPath is not null)
                failures.Add("M4 node ClipPath must stay null with a validated partial");
            if (node.Status == RenderNodeStatus.Completed)
                failures.Add("M4 node must never be Completed by a partial");
        }
        finally
        {
            try { File.Delete(db); } catch { }
            try { File.Delete(db + "-wal"); } catch { }
            try { File.Delete(db + "-shm"); } catch { }
        }
    }

    // ---- M4: crash matrix — pending cleanup, validated keep, no false deletes ----

    private async Task PartialCrashRecoveryTests(List<string> failures)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "mmod_m4crash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var db = Path.Combine(Path.GetTempPath(), "mmod_m4crash_" + Guid.NewGuid().ToString("N") + ".db");
        var repo = new RenderTaskRepository(db);

        RenderAttemptRecord MakeAttempt(string taskId, string nodeId, int attemptNumber, NodeExecutionStage stage, string? temp)
        {
            var session = CaptureSessionInfo.Create(taskId, 0, attemptNumber);
            return new RenderAttemptRecord(
                Guid.NewGuid().ToString("N"), session.CaptureSessionId, taskId, nodeId, attemptNumber,
                stage, session.SequencePrefix, temp,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
                null, null, CaptureCleanupState.NotRequired,
                null, null, null, "map", 0, 0, null);
        }

        try
        {
            var settings = new RenderSettingsSnapshot(10, 0.5, @"R:\", @"C:\Videos", @"C:\Game", false, 60, 0);
            var taskId = repo.CreateTask(new NewRenderTask("map", "player", 1, @"C:\Videos\out.mp4", settings,
                [new NewRenderNode(@"C:\record.mtv", 1, 0, 12.3, 738)]));
            var nodeId = repo.GetNodes(taskId).Single().Id;

            // Case 1: Pending + temp (Pending written, move not yet done).
            var case1Temp = Path.Combine(workDir, "c1.encoding.mp4");
            File.WriteAllText(case1Temp, "c1-temp");
            var c1 = MakeAttempt(taskId, nodeId, 1, NodeExecutionStage.Failed, case1Temp);
            repo.CreateAttempt(c1);
            repo.MarkAttemptPartialPending(c1.Id, Path.Combine(workDir, "c1.partial.mp4"), "DiskPressure Critical");

            // Case 2: Pending + partial (move done, Validated not yet written).
            var case2Partial = Path.Combine(workDir, "c2.partial.mp4");
            File.WriteAllText(case2Partial, "c2-partial");
            var c2 = MakeAttempt(taskId, nodeId, 2, NodeExecutionStage.ControlledStopFinalized, null);
            repo.CreateAttempt(c2);
            repo.MarkAttemptPartialPending(c2.Id, case2Partial, "DiskPressure Critical");

            // Case 3: Failed attempt + Pending + partial (terminal dirty record).
            var case3Partial = Path.Combine(workDir, "c3.partial.mp4");
            File.WriteAllText(case3Partial, "c3-partial");
            var c3 = MakeAttempt(taskId, nodeId, 3, NodeExecutionStage.Failed, null);
            repo.CreateAttempt(c3);
            repo.MarkAttemptPartialPending(c3.Id, case3Partial, "DiskPressure Critical");
            repo.UpdateAttemptFailure(c3.Id, RecordingFailureKind.DiskPressure, "DiskPressure", CaptureCleanupState.Clean);

            // Case 4: Validated + partial (must be kept).
            var case4Partial = Path.Combine(workDir, "c4.partial.mp4");
            File.WriteAllText(case4Partial, "c4-partial");
            var c4 = MakeAttempt(taskId, nodeId, 4, NodeExecutionStage.Failed, null);
            repo.CreateAttempt(c4);
            repo.MarkAttemptPartialPending(c4.Id, case4Partial, "DiskPressure Critical");
            repo.UpdateAttemptPartial(c4.Id, case4Partial, DateTimeOffset.UtcNow, 800, "DiskPressure Critical");

            // Case 5: unrelated user file in the work dir (must not be deleted).
            var unrelated = Path.Combine(workDir, "user-notes.txt");
            File.WriteAllText(unrelated, "user file");

            repo.SaveRunnerSession(new RunnerSessionRecord(
                ProcessId: null, NetConPort: null, NetConPassword: null, TaskId: taskId, NodeId: nodeId,
                ExePath: null, ProcessStartedAt: null, GameSessionId: null,
                CaptureSessionId: null, SequencePrefix: null, OwnershipToken: null,
                WatchDirectory: @"R:\"));

            var runner = new RenderTaskRunner(repo);
            await runner.RecoverFromCrashAsync(CancellationToken.None);

            // Cases 1-3: temp/partial deleted, Pending reset to None.
            if (File.Exists(case1Temp)) failures.Add("M4 crash: case1 temp must be deleted");
            if (File.Exists(case2Partial)) failures.Add("M4 crash: case2 partial must be deleted");
            if (File.Exists(case3Partial)) failures.Add("M4 crash: case3 partial must be deleted");
            var c1After = repo.GetAttemptsForNode(taskId, nodeId).Single(a => a.Id == c1.Id);
            var c2After = repo.GetAttemptsForNode(taskId, nodeId).Single(a => a.Id == c2.Id);
            var c3After = repo.GetAttemptsForNode(taskId, nodeId).Single(a => a.Id == c3.Id);
            foreach (var (label, attempt) in new[] { ("c1", c1After), ("c2", c2After), ("c3", c3After) })
            {
                if (attempt.PartialState != PartialState.None || attempt.PartialPath is not null ||
                    attempt.PartialValidatedAt is not null || attempt.PartialOutputFrames is not null ||
                    attempt.PartialReason is not null)
                    failures.Add($"M4 crash: {label} Pending must be reset to None with zero metadata");
            }

            // Case 4: validated partial kept, metadata intact.
            if (!File.Exists(case4Partial)) failures.Add("M4 crash: validated partial must be kept");
            var c4After = repo.GetAttemptsForNode(taskId, nodeId).Single(a => a.Id == c4.Id);
            if (c4After.PartialState != PartialState.Validated || c4After.PartialPath != case4Partial)
                failures.Add("M4 crash: validated metadata must survive recovery");

            // Case 5: unrelated user file untouched.
            if (!File.Exists(unrelated)) failures.Add("M4 crash: unrelated user file must not be deleted");

            // Node back to re-recordable state, ClipPath stays null.
            var node = repo.GetNodes(taskId).Single();
            if (node.Status != RenderNodeStatus.Pending)
                failures.Add($"M4 crash recovery must leave the node Pending, got {node.Status}");
            if (node.ClipPath is not null)
                failures.Add("M4 crash recovery must not adopt a crash-window file as ClipPath");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
            try { File.Delete(db); } catch { }
            try { File.Delete(db + "-wal"); } catch { }
            try { File.Delete(db + "-shm"); } catch { }
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
    // All time-driven cases use explicit monotonic timestamps so the window
    // behaviour is deterministic and no Thread.Sleep is needed.

    private void TelemetryTests(List<string> failures)
    {
        const long freq = 10_000_000; // simulated Stopwatch.Frequency
        const long windowTicks = 10L * freq; // 10s window

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

        // 2. Deterministic growth: one sample per simulated frame at 100 fps
        //    produces exactly the expected rates.
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            var frameTicks = freq / 100;
            for (var i = 1; i <= 300; i++)
            {
                tracker.AddSample(i * frameTicks, new CapturePerformanceTracker.Sample(
                    Produced: i * 1.0, Consumed: i * 0.5, Output: i * 0.5,
                    PendingFrames: 0, PendingBytes: 0));
            }

            var s = tracker.BuildSnapshot(ProcessingBackend.Disabled, EncoderBackend.Software, 0, 0);
            if (Math.Abs(s.ProducedFramesPerSecond - 100) > 1e-6)
                failures.Add($"M3 produced rate must be ~100: {s.ProducedFramesPerSecond}");
            if (Math.Abs(s.ConsumedFramesPerSecond - 50) > 1e-6)
                failures.Add($"M3 consumed rate must be ~50: {s.ConsumedFramesPerSecond}");
            if (Math.Abs(s.OutputFramesPerSecond - 50) > 1e-6)
                failures.Add($"M3 output rate must be ~50: {s.OutputFramesPerSecond}");
            if (Math.Abs(s.ConsumptionRatio - 0.5) > 1e-6)
                failures.Add($"M3 consumption ratio must be exactly 0.5: {s.ConsumptionRatio}");
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

        // 5. Window expiry: old samples fall out of the window; the rate then
        //    reflects only the in-window span.
        {
            var rw = new RateWindow(TimeSpan.FromSeconds(10));
            var t0 = 0L;
            rw.AddSample(t0, 0);
            rw.AddSample(t0 + windowTicks, 100); // exactly at the window edge
            var rateAtEdge = rw.GetRatePerSecond();
            if (rateAtEdge is null || Math.Abs(rateAtEdge.Value - 10) > 1e-6)
                failures.Add($"M3 rate at window edge must be ~10/s, got {rateAtEdge}");

            rw.AddSample(t0 + windowTicks + freq, 200); // first sample expired
            var rateAfter = rw.GetRatePerSecond();
            if (rateAfter is null || Math.Abs(rateAfter.Value - 100) > 1e-6)
                failures.Add($"M3 post-expiry rate must be ~100/s, got {rateAfter}");
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
            var frameTicks = freq / 100;
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            for (var i = 1; i <= 300; i++)
            {
                tracker.AddSample(i * frameTicks, new CapturePerformanceTracker.Sample(
                    Produced: i * 1.0, Consumed: i * 0.2, Output: i * 0.2,
                    PendingFrames: 100, PendingBytes: 0));
            }

            var growing = tracker.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 100, 0);
            if (growing.CatchUpSeconds is not null)
                failures.Add("M3 catch-up must be null when production outpaces consumption");

            var tracker2 = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            for (var i = 1; i <= 300; i++)
            {
                tracker2.AddSample(i * frameTicks, new CapturePerformanceTracker.Sample(
                    Produced: i * 1.0, Consumed: i * 1.2, Output: i * 1.2,
                    PendingFrames: 100, PendingBytes: 0));
            }

            var shrinking = tracker2.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 100, 0);
            // consumed 120/s - produced 100/s → 100 pending drains in 5s.
            if (shrinking.CatchUpSeconds is null || Math.Abs(shrinking.CatchUpSeconds.Value - 5.0) > 0.001)
                failures.Add($"M3 catch-up must be exactly ~5s, got {shrinking.CatchUpSeconds}");

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

        // 10. Same-timestamp samples: identical timestamps must never produce
        //     negative/NaN/Infinity even across a counter reset.
        {
            var rw = new RateWindow(TimeSpan.FromSeconds(10));
            rw.AddSample(1234, 100);
            rw.AddSample(1234, 150); // same instant, increasing counter
            if (rw.GetRatePerSecond() is not null)
                failures.Add("M3 same-timestamp samples must yield no rate (zero span)");
            rw.AddSample(1234, 50); // same instant, reset counter
            var rate = rw.GetRatePerSecond();
            if (rate is not null && (double.IsNaN(rate.Value) || double.IsInfinity(rate.Value) || rate < 0))
                failures.Add($"M3 same-timestamp reset must stay safe, got {rate}");
        }

        // 11. Concurrency stress: AddSample / BuildSnapshot / Reset running
        //     concurrently must never throw, produce NaN/Infinity, or break
        //     the peak constraints.
        {
            var tracker = new CapturePerformanceTracker(TimeSpan.FromSeconds(10));
            var stop = false;
            var errors = new List<string>();
            var counter = 0L;

            var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try
                {
                    var i = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        Interlocked.Increment(ref counter);
                        tracker.AddSample(new CapturePerformanceTracker.Sample(
                            Produced: i * 1.0, Consumed: i * 0.5, Output: i * 0.5,
                            PendingFrames: i % 13, PendingBytes: i % 17));
                        i++;
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"writer: {ex.Message}");
                }
            })).ToArray();

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        var s = tracker.BuildSnapshot(ProcessingBackend.Gpu, EncoderBackend.Software, 7, 7000);
                        if (!double.IsFinite(s.ProducedFramesPerSecond)
                            || !double.IsFinite(s.ConsumedFramesPerSecond)
                            || !double.IsFinite(s.OutputFramesPerSecond)
                            || !double.IsFinite(s.ConsumptionRatio))
                        {
                            lock (errors) errors.Add("non-finite snapshot value");
                        }

                        // Reset legally zeroes the peaks for a new session, so
                        // the only cross-concurrency invariants are: never
                        // negative, never NaN/Infinity, never thrown.
                        if (s.Backlog.PeakPendingFrames < 0 || s.Backlog.PeakPendingBytes < 0)
                            lock (errors) errors.Add("negative peak");
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"reader: {ex.Message}");
                }
            })).ToArray();

            var resets = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        tracker.Reset();
                        Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"reset: {ex.Message}");
                }
            });

            Thread.Sleep(500);
            Volatile.Write(ref stop, true);
            Task.WaitAll([.. writers, .. readers, resets]);

            foreach (var e in errors)
                failures.Add($"M3 concurrency stress: {e}");
            if (Volatile.Read(ref counter) == 0)
                failures.Add("M3 concurrency stress did no work");
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
            var atomic = watcher.GetBacklogSnapshot();
            if (atomic.PendingFrames != 0 || atomic.PendingBytes != 0 || atomic.PeakPendingFrames < 2 || atomic.PeakPendingBytes <= 0)
                failures.Add("M5 atomic watcher backlog snapshot did not preserve current/peak facts");
            watcher.Dispose();

            // File disappears while pending → read failure flagged, no
            // exception. Uses a fresh watcher because the previous session
            // permanently deduplicated the index (M3-B-002).
            var rfDir = Path.Combine(Path.GetTempPath(), "mmod_rf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rfDir);
            var rfPath = Path.Combine(rfDir, "rf_0001.tga");
            File.WriteAllBytes(rfPath, BuildTga(4, 4));
            var rf = new TgaDirectoryWatcher(rfDir, "rf_");
            rf.Start(acceptPreSessionFiles: true);
            Thread.Sleep(700);
            rf.ForceFullScan();
            if (rf.PendingCount != 1)
                failures.Add("M3 read-failure setup must accept the frame");
            File.Delete(rfPath); // vanishes while pending
            rf.ForceFullScan();
            Thread.Sleep(100);
            rf.ForceFullScan();
            if (!rf.HasPendingReadFailure)
                failures.Add("M3 vanished pending file must set the read-failure flag");
            rf.Dispose();
            try { Directory.Delete(rfDir, true); } catch { }

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

    // ---- M3-B-002: permanent per-session frame-index dedup ----

    private void WatcherDedupTests(List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mmod_dedup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1. Accept frame 1.
            var path1 = Path.Combine(dir, "dd_0001.tga");
            File.WriteAllBytes(path1, BuildTga(4, 4));
            var watcher = new TgaDirectoryWatcher(dir, "dd_");
            watcher.Start(acceptPreSessionFiles: true);
            Thread.Sleep(700);
            watcher.ForceFullScan();
            if (watcher.ProducedCount != 1 || watcher.PendingCount != 1)
                failures.Add($"M3-B-002 accept failed: produced={watcher.ProducedCount} pending={watcher.PendingCount}");

            // 2. Take frame 1 out of pending.
            if (!watcher.TryTake(1, out _))
                failures.Add("M3-B-002 TryTake must succeed");
            if (watcher.ProducedCount != 1 || watcher.PendingCount != 0)
                failures.Add("M3-B-002 take must not change produced");

            // 3. Recreate the same file (keep it present) and hammer scans.
            File.WriteAllBytes(path1, BuildTga(4, 4));
            for (var i = 0; i < 5; i++)
            {
                watcher.ForceFullScan();
                Thread.Sleep(120);
            }

            // 4. The index must not re-enter pending and must not re-count.
            if (watcher.ProducedCount != 1)
                failures.Add($"M3-B-002 recreated file must not re-count produced: {watcher.ProducedCount}");
            if (watcher.PendingCount != 0)
                failures.Add($"M3-B-002 recreated file must not re-enter pending: {watcher.PendingCount}");
            if (watcher.CandidateCount != 0)
                failures.Add($"M3-B-002 recreated file must not re-enter candidates: {watcher.CandidateCount}");
            watcher.Dispose();

            // 5. A fresh watcher instance on the same directory accepts the
            //    same index again (no cross-session pollution).
            var fresh = new TgaDirectoryWatcher(dir, "dd_");
            fresh.Start(acceptPreSessionFiles: true);
            Thread.Sleep(700);
            fresh.ForceFullScan();
            if (fresh.ProducedCount != 1 || fresh.PendingCount != 1)
                failures.Add($"M3-B-002 fresh watcher must accept the index: produced={fresh.ProducedCount} pending={fresh.PendingCount}");
            fresh.Dispose();
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
    public WatcherBacklogSnapshot GetBacklogSnapshot() => new(
        PendingCount, PendingBytes, PeakPendingFrames, PeakPendingBytes, false);
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
