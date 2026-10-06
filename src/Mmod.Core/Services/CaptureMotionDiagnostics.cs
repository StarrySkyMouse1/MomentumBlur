namespace Mmod.Core.Services;

/// <summary>Samples source TGA cadence at output boundaries without changing capture behavior.</summary>
public sealed class CaptureMotionDiagnostics
{
    private const int GridColumns = 20;
    private const int GridRows = 12;
    private const double NearStaticThreshold = 0.08;
    private const double JumpThreshold = 2.0;
    private float[]? _previous;
    private int _nearStaticRun;
    private int _longestNearStaticRun;
    private int _holdThenJumpCount;
    private int _sampleCount;
    private double _deltaSum;
    private double _maximumDelta;
    private long _lastSourceFrameIndex = -1;
    private int _outputFps = 60;
    private readonly List<string> _holdThenJumpLocations = [];
    private readonly object _gate = new();

    public void Sample(ReadOnlySpan<byte> bgra, int width, int height,
        long sourceFrameIndex = -1, int outputFps = 60)
    {
        lock (_gate)
            SampleCore(bgra, width, height, sourceFrameIndex, outputFps);
    }

    private void SampleCore(ReadOnlySpan<byte> bgra, int width, int height,
        long sourceFrameIndex, int outputFps)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return;

        var current = ComputeGrid(bgra, width, height);
        _lastSourceFrameIndex = sourceFrameIndex;
        _outputFps = Math.Max(1, outputFps);
        if (_previous is not null)
        {
            double sum = 0;
            for (var i = 0; i < current.Length; i++)
                sum += Math.Abs(current[i] - _previous[i]);

            var delta = sum / current.Length;
            _sampleCount++;
            _deltaSum += delta;
            _maximumDelta = Math.Max(_maximumDelta, delta);
            if (delta <= NearStaticThreshold)
            {
                _nearStaticRun++;
                _longestNearStaticRun = Math.Max(_longestNearStaticRun, _nearStaticRun);
            }
            else
            {
                if (_nearStaticRun >= 2 && delta >= JumpThreshold)
                {
                    _holdThenJumpCount++;
                    // Bounded diagnostics: no per-input-frame database writes.
                    // Indices are zero-based output-boundary samples, not proof
                    // of dropped frames (intro/outro stillness is expected too).
                    if (_holdThenJumpLocations.Count < 16)
                        _holdThenJumpLocations.Add(
                            $"output={_sampleCount - _nearStaticRun - 1}..{_sampleCount}" +
                            $" time={(_sampleCount - _nearStaticRun - 1) / (double)_outputFps:0.###}..{_sampleCount / (double)_outputFps:0.###}s" +
                            $" source={sourceFrameIndex} delta={delta:0.###}");
                }
                _nearStaticRun = 0;
            }
        }

        _previous = current;
    }

    public string BuildSummary()
    {
        lock (_gate)
        {
            return $"TGA运动节奏：输出边界样本={_sampleCount + (_previous is null ? 0 : 1)} " +
                $"平均块亮度差={(_sampleCount == 0 ? 0 : _deltaSum / _sampleCount):0.###} " +
                $"最大差={_maximumDelta:0.###} 近静止最长={_longestNearStaticRun}帧 " +
                $"停后突跳={_holdThenJumpCount}次 尾部近静止={_nearStaticRun}帧 " +
                $"末次源帧={_lastSourceFrameIndex} 区间=[{string.Join("; ", _holdThenJumpLocations)}] " +
                "（0基输出采样位置；含片头/片尾，仅源TGA诊断，不等同编码掉帧）";
        }
    }

    private static float[] ComputeGrid(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var values = new float[GridColumns * GridRows];
        for (var gy = 0; gy < GridRows; gy++)
        {
            var y = Math.Min(height - 1, (gy * 2 + 1) * height / (GridRows * 2));
            for (var gx = 0; gx < GridColumns; gx++)
            {
                var x = Math.Min(width - 1, (gx * 2 + 1) * width / (GridColumns * 2));
                var offset = (y * width + x) * 4;
                values[gy * GridColumns + gx] =
                    0.114f * bgra[offset] + 0.587f * bgra[offset + 1] + 0.299f * bgra[offset + 2];
            }
        }

        return values;
    }
}
