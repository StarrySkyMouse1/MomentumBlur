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

            var options = NativeSessionFactory.BuildOptions(settings);
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
                Width = 0,
                Height = 0,
                BlendFrames = blend,
                Exposure = (float)settings.Exposure,
                OutputFps = ProjectConstants.FinalOutputFramerate,
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
                if (effectsPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(effectsPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }, cancellationToken);
    }
}
