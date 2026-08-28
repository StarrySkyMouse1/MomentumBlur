using Mmod.Core.Models;

namespace Mmod.Core.Services;

/// <summary>Deterministic interpretation of one real 10-second capture sample.</summary>
public static class PerformancePreflightEvaluator
{
    public const double PassRatio = 0.98;
    public const double MarginalRatio = 0.90;

    public static PerformancePreflightResult Evaluate(
        PerformanceSnapshot snapshot,
        bool hasSufficientWindow,
        bool hasPendingReadFailure = false)
    {
        var rating = PerformancePreflightRating.Unknown;
        var backendKnown = snapshot.QualityBackend != ProcessingBackend.Unknown
            && snapshot.EncoderBackend != EncoderBackend.Unknown;
        var ratesValid = IsFiniteNonNegative(snapshot.ProducedFramesPerSecond)
            && IsFiniteNonNegative(snapshot.ConsumedFramesPerSecond)
            && IsFiniteNonNegative(snapshot.OutputFramesPerSecond)
            && IsFiniteNonNegative(snapshot.ConsumptionRatio)
            && snapshot.ProducedFramesPerSecond > 0;

        if (hasSufficientWindow && !hasPendingReadFailure && backendKnown && ratesValid)
        {
            if (snapshot.BacklogTrend == BacklogTrend.Growing || snapshot.ConsumptionRatio < MarginalRatio)
                rating = PerformancePreflightRating.Fail;
            else if (snapshot.ConsumptionRatio < PassRatio || snapshot.BacklogTrend == BacklogTrend.Unknown)
                rating = PerformancePreflightRating.Marginal;
            else
                rating = PerformancePreflightRating.Pass;
        }

        return new PerformancePreflightResult(
            snapshot.ProducedFramesPerSecond,
            snapshot.ConsumedFramesPerSecond,
            snapshot.OutputFramesPerSecond,
            snapshot.ConsumptionRatio,
            snapshot.Backlog.PeakPendingFrames,
            snapshot.Backlog.PeakPendingBytes,
            snapshot.QualityBackend,
            snapshot.EncoderBackend,
            rating,
            DateTimeOffset.UtcNow);
    }

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0;
}
