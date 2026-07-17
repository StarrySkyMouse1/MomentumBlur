using System.Runtime.InteropServices;
using Mmod.Core.Models;

namespace Mmod.Core.Native;

public sealed class NativeBlendSession : IDisposable
{
    private IntPtr _handle;
    private IntPtr _outputPathNative;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int BlendFrames { get; }

    private NativeBlendSession(IntPtr handle, IntPtr outputPathNative, int width, int height, int blendFrames)
    {
        _handle = handle;
        _outputPathNative = outputPathNative;
        Width = width;
        Height = height;
        BlendFrames = blendFrames;
    }

    public static NativeBlendSession Create(
        int width,
        int height,
        int blendFrames,
        float exposure,
        int outputFps,
        EncoderPreference encoder,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var pathPtr = Marshal.StringToHGlobalUni(outputPath);
        var desc = new MmodNativeInterop.SessionDesc
        {
            Width = width,
            Height = height,
            BlendFrames = Math.Max(1, blendFrames),
            Exposure = exposure,
            OutputFps = outputFps <= 0 ? 60 : outputFps,
            Encoder = MmodNativeInterop.ToNativeEncoder(encoder),
            OutputPath = pathPtr
        };

        var handle = MmodNativeInterop.mmod_session_create(ref desc, out var error);
        if (handle == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pathPtr);
            throw new InvalidOperationException(
                $"创建 Native Session 失败: {MmodNativeInterop.GetErrorString(error)}");
        }

        return new NativeBlendSession(handle, pathPtr, width, height, desc.BlendFrames);
    }

    public void SubmitBgra(byte[] bgra, int stride)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(bgra);
        var code = MmodNativeInterop.mmod_session_submit_bgra(_handle, bgra, stride);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"提交帧失败: {MmodNativeInterop.GetErrorString(code)}");
        }
    }

    public void Finish()
    {
        EnsureNotDisposed();
        var code = MmodNativeInterop.mmod_session_finish(_handle);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"结束 Session 失败: {MmodNativeInterop.GetErrorString(code)}");
        }
    }

    public (int Done, int Total) GetProgress()
    {
        EnsureNotDisposed();
        var code = MmodNativeInterop.mmod_session_get_progress(_handle, out var done, out var total);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"读取进度失败: {MmodNativeInterop.GetErrorString(code)}");
        }

        return (done, total);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle != IntPtr.Zero)
        {
            MmodNativeInterop.mmod_session_destroy(_handle);
            _handle = IntPtr.Zero;
        }

        if (_outputPathNative != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_outputPathNative);
            _outputPathNative = IntPtr.Zero;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
