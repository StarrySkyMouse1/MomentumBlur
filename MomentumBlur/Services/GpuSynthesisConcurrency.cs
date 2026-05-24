namespace mmod_record.Services;

/// <summary>
/// 限制同时进行 GPU 帧混合的并发数（ComputeSharp 共享默认显卡设备）。
/// </summary>
public static class GpuSynthesisConcurrency
{
    private static readonly object Gate = new();
    private static int _limit = 2;
    private static int _inUse;

    public static int CurrentLimit
    {
        get
        {
            lock (Gate)
                return _limit;
        }
    }

    public static void Configure(int maxParallel)
    {
        lock (Gate)
            _limit = Math.Clamp(maxParallel, 1, 4);
    }

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (_inUse < _limit)
                {
                    _inUse++;
                    return new ReleaseHandle();
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ReleaseHandle : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            lock (Gate)
                _inUse = Math.Max(0, _inUse - 1);
        }
    }
}
