using Mmod.Core.Models;
using Mmod.Core.Native;
using Mmod.Core.Services;

if (args.Length >= 3 && args[0] == "netcon")
{
    await using var client = new MomentumNetConClient();
    await client.ConnectAsync(int.Parse(args[1]), args[2], TimeSpan.FromSeconds(10), CancellationToken.None);
    await client.ExecuteAsync("echo MMOD_NETCON_SMOKE_OK", TimeSpan.FromSeconds(10), CancellationToken.None);
    Console.WriteLine("NETCON_OK");
    return 0;
}

if (args.Length >= 3 && args[0] == "control")
{
    await using var game = new MomentumProcessController();
    game.NetCon.OutputReceived += line => Console.WriteLine("NETCON " + line);
    await game.StartAsync(args[1], CancellationToken.None);
    Console.WriteLine("CONTROL_AUTH_OK");
    await game.NetCon.SendAsync($"map \"{args[2].Replace("\"", string.Empty)}\"", CancellationToken.None);
    await game.NetCon.ExecuteAsync("echo MMOD_CONTROL_MAP_READY", TimeSpan.FromMinutes(3), CancellationToken.None);
    Console.WriteLine("CONTROL_MAP_OK");
    if (args.Length >= 4)
    {
        await game.NetCon.ExecuteAsync("help mom_tv_replay_play_pause; help mom_tv_replay_goto; mom_tv_replay_goto", TimeSpan.FromSeconds(30), CancellationToken.None);
        var replayRoot = Path.Combine(Path.GetFullPath(args[1]), "momentum");
        var replay = Path.GetRelativePath(replayRoot, Path.GetFullPath(args[3])).Replace("\\", "/").Replace("\"", string.Empty);
        await game.NetCon.SendAsync($"mom_tv_replay_watch \"{replay}\"", CancellationToken.None);
        await game.NetCon.ExecuteCheckedAsync(
            "echo MMOD_CONTROL_REPLAY_READY", TimeSpan.FromMinutes(2),
            line => line.Contains("Failed to load replay", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed to open replay", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Invalid replay file", StringComparison.OrdinalIgnoreCase),
            CancellationToken.None);
        Console.WriteLine("CONTROL_REPLAY_OK");
        await game.NetCon.ExecuteAsync("mom_tv_replay_play_pause; mom_tv_replay_goto 0", TimeSpan.FromSeconds(30), CancellationToken.None);
        Console.WriteLine("CONTROL_REPLAY_RESET_OK");
    }
    await game.CloseOwnedAsync(CancellationToken.None);
    return 0;
}

if (args.Length >= 2 && args[0] == "catalog")
{
    var result = new ReplayCatalogService().Scan(args[1]);
    var compatible = result.Records.Count(x => x.IsCompatible);
    var incompatible = result.Records.Count - compatible;
    Console.WriteLine($"CATALOG records={result.Records.Count} compatible={compatible} incompatible={incompatible} issues={result.Issues.Count}");
    foreach (var issue in result.Issues.Take(10)) Console.WriteLine($"ISSUE {issue.FilePath}: {issue.Message}");
    return result.Records.Count > 0 && result.Issues.Count == 0 ? 0 : 3;
}

if (args.Length >= 3 && args[0] == "concat")
{
    var output = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"concat_{DateTime.Now:HHmmss}.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    NativeMp4Concatenator.Concatenate([args[1], args[2]], output);
    Mp4MergeService.Validate(output);
    Console.WriteLine($"CONCAT_OK path={output} size={new FileInfo(output).Length}");
    return 0;
}

if (args.Length >= 1 && args[0] == "repository")
{
    var db = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"tasks_{Guid.NewGuid():N}.db");
    var repo = new RenderTaskRepository(db);
    var settings = new RenderSettingsSnapshot(10, 0.5, @"R:\", @"C:\Videos", @"C:\Game", false, 60, 140_000_000);
    var id = repo.CreateTask(new NewRenderTask("map", "player", 1, @"C:\Videos\out.mp4", settings, [new NewRenderNode(@"C:\record.mtv", 1, 0, 12.3, 738)]));
    var task = repo.GetTasks().Single(x => x.Id == id);
    var node = repo.GetNodes(id).Single();
    if (task.Status != RenderTaskStatus.Pending || node.ExpectedTickCount != 738) return 4;
    repo.UpdateTaskStatus(id, RenderTaskStatus.Running);
    var reopened = new RenderTaskRepository(db);
    if (reopened.GetTasks().Single().Status != RenderTaskStatus.Paused || reopened.GetNodes(id).Single().Status != RenderNodeStatus.Pending) return 5;
    Console.WriteLine("REPOSITORY_OK");
    return 0;
}

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
