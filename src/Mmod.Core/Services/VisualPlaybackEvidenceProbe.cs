namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Block-grid based visual playback evidence probe. Computes per-block average
/// luma on a low-resolution grid (subsampled) and classifies a sample as
/// significant only when both the changed-block ratio and the mean luma delta
/// clear their thresholds. Requires N consecutive significant samples
/// (hysteresis) before reporting playback started, so a single noise spike or a
/// small HUD-only change can never establish the anchor.
/// </summary>
public sealed class VisualPlaybackEvidenceProbe : IPlaybackEvidenceProbe
{
    private readonly int _blockSize;
    private readonly double _ratioThreshold;
    private readonly double _meanDeltaThreshold;
    private readonly double _changedBlockDelta;
    private readonly int _requiredConsecutive;
    private readonly int _maxHistory;
    private float[]? _previousBlocks;
    private int _gridWidth;
    private int _gridHeight;
    private int _consecutive;

    public VisualPlaybackEvidenceProbe(RecordingTimeoutPolicy policy)
    {
        _blockSize = Math.Max(4, policy.EvidenceBlockSize);
        _ratioThreshold = policy.EvidenceChangedBlockRatioThreshold;
        _meanDeltaThreshold = policy.EvidenceMeanLumaDeltaThreshold;
        _changedBlockDelta = _meanDeltaThreshold * 1.5;
        _requiredConsecutive = Math.Max(1, policy.PlaybackEvidenceRequiredConsecutive);
        _maxHistory = _requiredConsecutive;
    }

    public int RequiredConsecutive => _requiredConsecutive;
    public int ConsecutiveSignificantCount => _consecutive;
    public bool IsPlaybackStarted => _consecutive >= _requiredConsecutive;

    /// <summary>Clears the baseline and history (used before replay watch).</summary>
    public void Reset()
    {
        _previousBlocks = null;
        _consecutive = 0;
        _gridWidth = 0;
        _gridHeight = 0;
    }

    public void SetBaseline(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return;
        _gridWidth = (width + _blockSize - 1) / _blockSize;
        _gridHeight = (height + _blockSize - 1) / _blockSize;
        _previousBlocks = ComputeBlockLuma(bgra, width, height);
        _consecutive = 0;
    }

    public PlaybackEvidenceSample Sample(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return new PlaybackEvidenceSample(0, 0, false, 0, 0);

        var gridW = (width + _blockSize - 1) / _blockSize;
        var gridH = (height + _blockSize - 1) / _blockSize;

        // First sample after baseline: compare against the baseline frame.
        if (_previousBlocks is null || _previousBlocks.Length != gridW * gridH)
        {
            SetBaseline(bgra, width, height);
            return new PlaybackEvidenceSample(0, 0, false, 0, gridW * gridH);
        }

        var current = ComputeBlockLuma(bgra, width, height);
        var total = gridW * gridH;
        var changed = 0;
        double sumDelta = 0;

        for (var i = 0; i < total; i++)
        {
            var delta = Math.Abs(current[i] - _previousBlocks[i]);
            sumDelta += delta;
            if (delta >= _changedBlockDelta)
                changed++;
        }

        var ratio = total == 0 ? 0 : changed / (double)total;
        var meanDelta = total == 0 ? 0 : sumDelta / total;
        var significant = ratio >= _ratioThreshold && meanDelta >= _meanDeltaThreshold;

        _consecutive = significant ? Math.Min(_consecutive + 1, _maxHistory) : 0;
        _previousBlocks = current;

        return new PlaybackEvidenceSample(ratio, meanDelta, significant, changed, total);
    }

    /// <summary>
    /// Computes average luma per block, subsampling pixels 2x2 inside each
    /// block to keep the probe cheap on full 1080p frames.
    /// </summary>
    private float[] ComputeBlockLuma(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var blocks = new float[_gridWidth * _gridHeight];
        var counts = new int[_gridWidth * _gridHeight];

        for (var y = 0; y < height; y += 2)
        {
            var rowBase = y * width * 4;
            var blockY = y / _blockSize;
            for (var x = 0; x < width; x += 2)
            {
                var blockX = x / _blockSize;
                var pi = rowBase + x * 4;
                // BGRA: luma from BGR channels.
                var luma =
                    0.114f * bgra[pi] +
                    0.587f * bgra[pi + 1] +
                    0.299f * bgra[pi + 2];
                var bi = blockY * _gridWidth + blockX;
                blocks[bi] += luma;
                counts[bi]++;
            }
        }

        for (var i = 0; i < blocks.Length; i++)
        {
            if (counts[i] > 0)
                blocks[i] /= counts[i];
        }

        return blocks;
    }
}
