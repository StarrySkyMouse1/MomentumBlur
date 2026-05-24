using System.Runtime.InteropServices;

namespace mmod_record.Services;

/// <summary>
/// 通过 GetSystemTimes 采样系统整体 CPU 占用（无需 PerformanceCounter 包）。
/// </summary>
public sealed class SystemCpuSampler
{
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrevious;

    /// <summary>
    /// 返回 0–100 的占用百分比；首次调用仅建立基线，返回 null。
    /// </summary>
    public double? SamplePercent()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return null;

        var idle = FileTimeToUInt64(idleFt);
        var kernel = FileTimeToUInt64(kernelFt);
        var user = FileTimeToUInt64(userFt);

        if (!_hasPrevious)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasPrevious = true;
            return null;
        }

        var idleDelta = idle - _prevIdle;
        var kernelDelta = kernel - _prevKernel;
        var userDelta = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        var total = kernelDelta + userDelta;
        if (total == 0)
            return 0;

        var busy = (long)total - (long)idleDelta;
        if (busy < 0)
            busy = 0;

        return busy * 100.0 / total;
    }

    private static ulong FileTimeToUInt64(FileTime ft) =>
        ((ulong)ft.High << 32) | ft.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        public readonly uint Low;
        public readonly uint High;
    }
}
