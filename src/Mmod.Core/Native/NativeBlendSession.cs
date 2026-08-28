using System.Runtime.InteropServices;
using Mmod.Core.Models;

namespace Mmod.Core.Native;

/// <summary>Optional native session parameters (all default to legacy behaviour).</summary>
public sealed record NativeSessionOptions(
    MotionBlurWeightMode MotionBlurMode = MotionBlurWeightMode.LegacyGaussianExposure,
    double ShutterAngle = 270,
    IReadOnlyList<NativeProcessingMapper.NativeEffectDesc>? Effects = null,
    int TargetBitrate = 0);

public sealed class NativeBlendSession : IDisposable
{
    private IntPtr _handle;
    private IntPtr _outputPathNative;
    private IntPtr _effectsNative;
    private int _effectCount;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int BlendFrames { get; }

    private NativeBlendSession(IntPtr handle, IntPtr outputPathNative, IntPtr effectsNative, int effectCount, int width, int height, int blendFrames)
    {
        _handle = handle;
        _outputPathNative = outputPathNative;
        _effectsNative = effectsNative;
        _effectCount = effectCount;
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
        string outputPath)
        => Create(width, height, blendFrames, exposure, outputFps, outputPath, options: null);

    public static NativeBlendSession Create(
        int width,
        int height,
        int blendFrames,
        float exposure,
        int outputFps,
        string outputPath,
        NativeSessionOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new NativeSessionOptions();

        var pathPtr = Marshal.StringToHGlobalUni(outputPath);

        var effects = options.Effects ?? [];
        var effectBytes = effects.Count * Marshal.SizeOf<MmodNativeInterop.EffectDescV1>();
        var effectsPtr = effectBytes > 0 ? Marshal.AllocHGlobal(effectBytes) : IntPtr.Zero;
        if (effectsPtr != IntPtr.Zero)
        {
            var offset = effectsPtr;
            foreach (var effect in effects)
            {
                Marshal.StructureToPtr(new MmodNativeInterop.EffectDescV1
                {
                    EffectType = effect.EffectType,
                    Enabled = 1,
                    Order = effect.Order,
                    Reserved = 0,
                    P0 = effect.P0,
                    P1 = effect.P1,
                    P2 = effect.P2,
                    P3 = effect.P3,
                    P4 = effect.P4,
                    P5 = effect.P5,
                    P6 = effect.P6,
                    P7 = effect.P7,
                }, offset, fDeleteOld: false);
                offset = IntPtr.Add(offset, Marshal.SizeOf<MmodNativeInterop.EffectDescV1>());
            }
        }

        var desc = new MmodNativeInterop.SessionDesc
        {
            Width = width,
            Height = height,
            BlendFrames = Math.Max(1, blendFrames),
            Exposure = exposure,
            OutputFps = outputFps <= 0 ? 60 : outputFps,
            Encoder = MmodNativeInterop.AutomaticEncoder,
            OutputPath = pathPtr,
            Effects = effectsPtr,
            EffectCount = effects.Count,
            TargetBitrate = Math.Max(0, options.TargetBitrate),
            MotionBlurMode = options.MotionBlurMode == MotionBlurWeightMode.ShutterAngle
                ? MmodNativeInterop.MotionBlurModeShutterAngle
                : MmodNativeInterop.MotionBlurModeLegacy,
            ShutterAngle = (float)Math.Clamp(options.ShutterAngle, 180.0, 360.0),
        };

        var handle = MmodNativeInterop.mmod_session_create(ref desc, out var error);
        if (handle == IntPtr.Zero)
        {
            if (effectsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(effectsPtr);
            Marshal.FreeHGlobal(pathPtr);
            throw new InvalidOperationException(
                $"创建 Native Session 失败: {MmodNativeInterop.GetErrorString(error)}");
        }

        return new NativeBlendSession(handle, pathPtr, effectsPtr, effects.Count, width, height, desc.BlendFrames);
    }

    /// <summary>
    /// Reports whether the native session runs quality effects, and whether it
    /// fell back to the CPU processing path (GPU processing init failed).
    /// </summary>
    public (bool EffectsEnabled, bool UsingCpuFallback) GetProcessingStatus()
    {
        EnsureNotDisposed();
        var code = MmodNativeInterop.mmod_session_get_processing_status(
            _handle, out var enabled, out var cpuFallback);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"读取处理状态失败: {MmodNativeInterop.GetErrorString(code)}");
        }
        return (enabled != 0, cpuFallback != 0);
    }

    /// <summary>
    /// Versioned native backend diagnosis (M3). Returns the real processing
    /// path (Disabled / Gpu / CpuFallback) and the real encoder path
    /// (currently Software because the live capture path disables hardware
    /// MFTs). Query failure surfaces as Unknown, never as a fabricated value.
    /// </summary>
    public (ProcessingBackend Processing, EncoderBackend Encoder) GetBackends()
    {
        EnsureNotDisposed();
        var code = MmodNativeInterop.mmod_session_get_backends(
            _handle, out var abiVersion, out var processing, out var encoder);
        if (code != 0 || abiVersion != MmodNativeInterop.BackendsAbiVersion)
            return (ProcessingBackend.Unknown, EncoderBackend.Unknown);

        var backend = processing switch
        {
            MmodNativeInterop.ProcessingBackendDisabled => ProcessingBackend.Disabled,
            MmodNativeInterop.ProcessingBackendGpu => ProcessingBackend.Gpu,
            MmodNativeInterop.ProcessingBackendCpuFallback => ProcessingBackend.CpuFallback,
            _ => ProcessingBackend.Unknown,
        };
        var encoderBackend = encoder switch
        {
            MmodNativeInterop.EncoderBackendSoftware => EncoderBackend.Software,
            _ => EncoderBackend.Unknown,
        };
        return (backend, encoderBackend);
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

        if (_effectsNative != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_effectsNative);
            _effectsNative = IntPtr.Zero;
            _effectCount = 0;
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
