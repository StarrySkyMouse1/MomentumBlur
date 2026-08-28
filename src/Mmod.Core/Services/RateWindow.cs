using System.Diagnostics;

namespace Mmod.Core.Services;

/// <summary>
/// Monotonic rolling-window rate estimator over a fixed time span. Uses
/// Stopwatch ticks (monotonic) instead of wall-clock time so clock adjustments
/// cannot produce negative or absurd rates. Handles three degenerate cases:
/// an empty window (no rate yet), a zero elapsed interval (no rate), and
/// counter resets (a later sample with a smaller counter than the earliest
/// kept sample — rate falls back to 0 instead of going negative).
/// </summary>
public sealed class RateWindow
{
    private readonly long _windowTicks;
    private readonly double _ticksPerSecond;
    private readonly List<(long Ticks, double Value)> _samples = [];

    /// <param name="window">Rolling window span; clamped to at least 1 ms.</param>
    public RateWindow(TimeSpan window)
    {
        var effective = window <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : window;
        _windowTicks = (long)(effective.TotalSeconds * Stopwatch.Frequency);
        _ticksPerSecond = Stopwatch.Frequency;
    }

    /// <summary>Records a cumulative counter observation at the current time.</summary>
    public void AddSample(double cumulativeValue)
    {
        var now = Stopwatch.GetTimestamp();

        // Counter reset (e.g. new session): drop everything older than the
        // reset so the rate never goes negative.
        if (_samples.Count > 0 && _samples[^1].Value > cumulativeValue)
            _samples.Clear();

        _samples.Add((now, cumulativeValue));

        // Expire samples older than the window (keep at least the newest).
        while (_samples.Count > 1 && now - _samples[0].Ticks > _windowTicks)
            _samples.RemoveAt(0);
    }

    /// <summary>
    /// Rate per second over the rolling window (first-to-last sample within
    /// the window). Returns null when the window is not ready (no samples),
    /// the elapsed span is zero, or the counter reset (values went backwards)
    /// — never negative, NaN or Infinity.
    /// </summary>
    public double? GetRatePerSecond()
    {
        if (_samples.Count == 0)
            return null;

        var first = _samples[0];
        var last = _samples[^1];
        var elapsedTicks = last.Ticks - first.Ticks;
        if (elapsedTicks <= 0)
            return null;

        var delta = last.Value - first.Value;
        if (delta < 0)
            return null;
        if (delta == 0)
            return 0;

        var rate = delta * _ticksPerSecond / elapsedTicks;
        return double.IsFinite(rate) ? rate : null;
    }

    /// <summary>Number of observations currently in the window.</summary>
    public int SampleCount => _samples.Count;

    /// <summary>Resets all state (new session).</summary>
    public void Reset()
    {
        _samples.Clear();
    }
}
