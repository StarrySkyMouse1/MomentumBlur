using System.Runtime.InteropServices;

namespace Mmod.Core.Native;

internal static class MmodNativeInterop
{
    private const string DllName = "mmod_native";
    internal const int AutomaticEncoder = 0;

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
