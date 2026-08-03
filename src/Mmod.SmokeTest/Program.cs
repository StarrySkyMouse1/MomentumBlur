using Mmod.Core.Models;
using Mmod.Core.Native;
using Mmod.Core.Services;

if (args.Length >= 2 && args[0] == "obs")
{
    var input = args[1];
    var output = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"obs_out_{DateTime.Now:HHmmss}.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var settings = new UserSettings
    {
        SupersamplingMultiplier = 2,
        ObsCaptureFramerate = 60,
        Exposure = 0.5
    };
    Console.WriteLine($"OBS in={input}");
    await new ObsSynthesisService().RunAsync(
        input,
        output,
        settings,
        new Progress<ObsSynthesisService.Progress>(p => Console.WriteLine($"progress {p.Done}/{p.Total}")),
        CancellationToken.None);
    var info = new FileInfo(output);
    Console.WriteLine(info.Exists ? $"OBS_OK size={info.Length}" : "OBS_FAIL");
    return info.Exists && info.Length > 500 ? 0 : 2;
}

var outDir = Path.Combine(Path.GetTempPath(), "mmod_smoke");
Directory.CreateDirectory(outDir);
var outputPath = Path.Combine(outDir, $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

const int w = 320;
const int h = 180;
const int blend = 4;
const int outputFrames = 30;

Console.WriteLine($"Output: {outputPath}");
using var session = NativeBlendSession.Create(
    w, h, blend, 0.5f, 60, outputPath);

var frame = new byte[w * h * 4];
for (var of = 0; of < outputFrames * blend; of++)
{
    var shade = (byte)((of * 7) % 255);
    for (var i = 0; i < w * h; i++)
    {
        frame[i * 4 + 0] = shade;
        frame[i * 4 + 1] = (byte)(255 - shade);
        frame[i * 4 + 2] = 128;
        frame[i * 4 + 3] = 255;
    }

    session.SubmitBgra(frame, w * 4);
}

session.Finish();
var smokeInfo = new FileInfo(outputPath);
Console.WriteLine(smokeInfo.Exists
    ? $"OK size={smokeInfo.Length} bytes progress={session.GetProgress()}"
    : "FAIL: output missing");
return smokeInfo.Exists && smokeInfo.Length > 1000 ? 0 : 1;
