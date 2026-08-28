using System.Runtime.InteropServices;

namespace Mmod.Core.Native;

internal static class MmodNativeInterop
{
    private const string DllName = "mmod_native";
    internal const int AutomaticEncoder = 0;

    /// <summary>Must match MmodMotionBlurMode in mmod_native.h.</summary>
    internal const int MotionBlurModeLegacy = 0;
    internal const int MotionBlurModeShutterAngle = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EffectDescV1
    {
        public int EffectType;
        public int Enabled;
        public int Order;
        public int Reserved;
        public float P0;
        public float P1;
        public float P2;
        public float P3;
        public float P4;
        public float P5;
        public float P6;
        public float P7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionDesc
    {
        public int Width;
        public int Height;
        public int BlendFrames;
        public float Exposure;
        public int OutputFps;
        public int Encoder;
        public IntPtr OutputPath;
        // ---- new fields (appended to keep old offsets stable) ----
        public IntPtr Effects;
        public int EffectCount;
        public int TargetBitrate;
        public int MotionBlurMode;
        public float ShutterAngle;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ProgressFn(IntPtr user, int done, int total);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr mmod_session_create(ref SessionDesc desc, out int outError);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmod_session_submit_bgra(IntPtr session, byte[] bgra, int stride);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmod_session_finish(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mmod_session_destroy(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmod_session_get_progress(IntPtr session, out int outDone, out int outTotal);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmod_session_get_processing_status(
        IntPtr session, out int outEffectsEnabled, out int outUsingCpuFallback);

    /// <summary>Must match MMOD_BACKENDS_ABI_VERSION in mmod_native.h.</summary>
    internal const int BackendsAbiVersion = 1;

    /// <summary>Must match MmodProcessingBackend in mmod_native.h.</summary>
    internal const int ProcessingBackendDisabled = 1;
    internal const int ProcessingBackendGpu = 2;
    internal const int ProcessingBackendCpuFallback = 3;

    /// <summary>Must match MmodEncoderBackend in mmod_native.h.</summary>
    internal const int EncoderBackendSoftware = 2;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmod_session_get_backends(
        IntPtr session, out int outAbiVersion, out int outProcessing, out int outEncoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int mmod_process_video_file(
        string inputPath,
        ref SessionDesc desc,
        ProgressFn? progress,
        IntPtr progressUser,
        out int outError);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int mmod_concat_video_files(string inputPaths, string outputPath, out int outError);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mmod_error_string(int error);

    internal static string GetErrorString(int error)
    {
        var ptr = mmod_error_string(error);
        return ptr == IntPtr.Zero ? $"error {error}" : Marshal.PtrToStringAnsi(ptr) ?? $"error {error}";
    }
}
