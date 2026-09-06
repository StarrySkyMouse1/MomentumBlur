using System.Diagnostics;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>
/// Turns raw capture counters (stable TGA produced, native submits, native
/// frames_output, watcher backlog) into an immutable PerformanceSnapshot.
/// Rates come from the 10-second rolling RateWindow; the consumption ratio is
/// only meaningful while a valid production rate exists (otherwise Unknown /
/// NaN-free); the trend has a noise dead-zone; catch-up time is only computed
/// when consumption clearly outpaces production and a backlog exists, and is
/// explicitly a backlog-drain estimate, never a task ETA.
///
/// Thread safety: AddSample, Reset and BuildSnapshot are mutually exclusive
/// under one lock, so every snapshot's rates, ratio, trend, catch-up and
/// peaks come from one logical sampling state. The lock is only held for
/// in-memory operations; no disk, native, UI, database or long-running work
/// happens inside it.
/// </summary>
public sealed class CapturePerformanceTracker
{
    public const double DefaultDeadZoneRatio = 0.05;
    public const double DefaultCatchUpMargin = 1.1;

    private readonly object _gate = new();
    private readonly RateWindow _producedWindow;
    private readonly RateWindow _consumedWindow;
    private readonly RateWindow _outputWindow;
    private readonly double _deadZoneRatio;
    private readonly double _catchUpMargin;
    private long _peakPendingFrames;
    private long _peakPendingBytes;

    public CapturePerformanceTracker(
        TimeSpan? window = null,
        double deadZoneRatio = DefaultDeadZoneRatio,
        double catchUpMargin = DefaultCatchUpMargin)
    {
        _producedWindow = new RateWindow(window ?? TimeSpan.FromSeconds(10));
        _consumedWindow = new RateWindow(window ?? TimeSpan.FromSeconds(10));
        _outputWindow = new RateWindow(window ?? TimeSpan.FromSeconds(10));
        _deadZoneRatio = Math.Clamp(deadZoneRatio, 0, 0.5);
        _catchUpMargin = Math.Max(1.0, catchUpMargin);
    }

    /// <summary>Counters observed since the last sample; zero by default.</summary>
    public sealed record Sample(
        double Produced = 0,
        double Consumed = 0,
        double Output = 0,
        long PendingFrames = 0,
        long PendingBytes = 0);

    /// <summary>
    /// Records one observation. All values are cumulative counters except
    /// PendingFrames/PendingBytes, which are instantaneous backlog levels.
    /// </summary>
    public void AddSample(Sample sample) => AddSample(Stopwatch.GetTimestamp(), sample);

    /// <summary>
    /// Records one observation with an explicit monotonic timestamp (same
    /// unit as <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).
    /// Tests can drive the window deterministically without sleeping; the
    /// default overload keeps using Stopwatch for production callers.
    /// </summary>
    public void AddSample(long monotonicTicks, Sample sample)
    {
        lock (_gate)
        {
            _producedWindow.AddSample(monotonicTicks, sample.Produced);
            _consumedWindow.AddSample(monotonicTicks, sample.Consumed);
            _outputWindow.AddSample(monotonicTicks, sample.Output);

            if (sample.PendingFrames > _peakPendingFrames)
                _peakPendingFrames = sample.PendingFrames;
            if (sample.PendingBytes > _peakPendingBytes)
                _peakPendingBytes = sample.PendingBytes;
        }
    }

    /// <summary>Resets every window and the session peaks (new session).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _producedWindow.Reset();
            _consumedWindow.Reset();
            _outputWindow.Reset();
            _peakPendingFrames = 0;
            _peakPendingBytes = 0;
        }
    }

    public PerformanceSnapshot BuildSnapshot(
        ProcessingBackend qualityBackend,
        EncoderBackend encoderBackend,
        long pendingFrames,
        long pendingBytes,
        DateTimeOffset? sampledAt = null)
    {
        lock (_gate)
        {
            var produced = _producedWindow.GetRatePerSecond() ?? 0;
            var consumed = _consumedWindow.GetRatePerSecond() ?? 0;
            var output = _outputWindow.GetRatePerSecond() ?? 0;

            // Ratio is Unknown (NaN-free) when production has no valid rate.
            var hasProducedRate = _producedWindow.GetRatePerSecond() is not null;
            var ratio = hasProducedRate && produced > 0 ? consumed / produced : 0;

            var trend = EvaluateTrendCore(produced, consumed);

            // Catch-up only when a real backlog exists AND consumption clearly
            // outpaces production: pending / (consumed - produced).
            double? catchUpSeconds = null;
            var backlogPending = pendingFrames > 0;
            var consumptionFaster = consumed > produced * _catchUpMargin;
            if (backlogPending && consumptionFaster && consumed > 0)
            {
                var drainRate = consumed - produced;
                if (drainRate > 0 && double.IsFinite(drainRate))
                    catchUpSeconds = pendingFrames / drainRate;
            }

            return new PerformanceSnapshot(
                ProducedFramesPerSecond: produced,
                ConsumedFramesPerSecond: consumed,
                OutputFramesPerSecond: output,
                ConsumptionRatio: ratio,
                Backlog: new BacklogSnapshot(
                    PendingFrames: pendingFrames,
                    PendingBytes: pendingBytes,
                    PeakPendingFrames: _peakPendingFrames,
                    PeakPendingBytes: _peakPendingBytes),
                BacklogTrend: trend,
                CatchUpSeconds: catchUpSeconds,
                QualityBackend: qualityBackend,
                EncoderBackend: encoderBackend,
                SampledAt: sampledAt ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Central, testable trend classification with a noise dead-zone. Needs a
    /// valid production rate; otherwise Unknown.
    /// </summary>
    public BacklogTrend EvaluateTrend(double producedFps, double consumedFps)
    {
        lock (_gate)
        {
            return EvaluateTrendCore(producedFps, consumedFps);
        }
    }

    private BacklogTrend EvaluateTrendCore(double producedFps, double consumedFps)
    {
        if (producedFps <= 0)
            return BacklogTrend.Unknown;

        var delta = consumedFps - producedFps;
        var deadZone = producedFps * _deadZoneRatio;
        if (Math.Abs(delta) <= deadZone)
            return BacklogTrend.Stable;
        return delta < 0 ? BacklogTrend.Growing : BacklogTrend.Shrinking;
    }
}
