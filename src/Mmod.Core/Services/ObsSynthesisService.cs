using System.IO;
using System.Runtime.InteropServices;
using Mmod.Core.Models;
using Mmod.Core.Native;

namespace Mmod.Core.Services;

public sealed class ObsSynthesisService
{
    public sealed record Progress(int Done, int Total);

    public Task RunAsync(
        string inputPath,
        string outputPath,
        UserSettings settings,
        IProgress<Progress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blend = SynthesisTiming.GetSynthesisBlendFrames(
                settings.SupersamplingMultiplier,
                settings.ObsCaptureFramerate);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var pathPtr = Marshal.StringToHGlobalUni(outputPath);
            var desc = new MmodNativeInterop.SessionDesc
            {
                Width = 0,
                Height = 0,
                BlendFrames = blend,
                Exposure = (float)settings.Exposure,
                OutputFps = ProjectConstants.FinalOutputFramerate,
                Encoder = MmodNativeInterop.ToNativeEncoder(settings.Encoder),
                OutputPath = pathPtr
            };

            MmodNativeInterop.ProgressFn? cb = null;
            if (progress is not null)
            {
                cb = (_, done, total) =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        progress.Report(new Progress(done, Math.Max(done, total)));
                };
            }

            try
            {
                var code = MmodNativeInterop.mmod_process_video_file(
                    inputPath,
                    ref desc,
                    cb,
                    IntPtr.Zero,
                    out var error);

                GC.KeepAlive(cb);

                if (code != 0)
                {
                    throw new InvalidOperationException(
                        $"OBS 合成失败: {MmodNativeInterop.GetErrorString(error != 0 ? error : code)}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }, cancellationToken);
    }
}
