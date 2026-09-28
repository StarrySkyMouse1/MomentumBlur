using System.Runtime.InteropServices;

namespace Mmod.Core.Services;

/// <summary>
/// 整机 CPU 利用率采样：GetSystemTimes 差分，不依赖性能计数器。
/// 首次采样无差分基准，返回 -1。
/// </summary>
internal sealed class MachineCpuSampler
{
    private long _lastIdleTicks = -1;
    private long _lastTotalTicks = -1;

    /// <summary>返回 0-100 的整机 CPU 利用率；首次或采样失败返回 -1。</summary>
    public double NextSamplePercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return -1;
        // kernel 时间已包含 idle；总时长 = kernel + user。
        var idleTicks = ToTicks(idle);
        var totalTicks = ToTicks(kernel) + ToTicks(user);
        if (_lastTotalTicks < 0)
        {
            _lastIdleTicks = idleTicks;
            _lastTotalTicks = totalTicks;
            return -1;
        }
        var totalDelta = totalTicks - _lastTotalTicks;
        var idleDelta = idleTicks - _lastIdleTicks;
        _lastIdleTicks = idleTicks;
        _lastTotalTicks = totalTicks;
        if (totalDelta <= 0)
            return -1;
        return Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0.0, 100.0);
    }

    private static long ToTicks(FILETIME value) =>
        ((long)value.dwHighDateTime << 32) | value.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
