using System.Globalization;

namespace Mmod.Core.Services;

/// <summary>
/// Attempt-scoped, reversible game configuration for deterministic replay capture.
/// Optional convars are only overridden after their numeric value is captured.
/// </summary>
public sealed class CaptureConVarScope
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly INetConClient _netCon;
    private readonly Action<string, string> _log;
    private readonly List<CapturedConVar> _captured = [];

    public CaptureConVarScope(INetConClient netCon, Action<string, string> log)
    {
        _netCon = netCon;
        _log = log;
    }

    public async Task ApplyAsync(int hostFramerate, int? foregroundFpsLimit, CancellationToken token)
    {
        await _netCon.ExecuteAsync("sv_cheats 1", CommandTimeout, token);

        await TryCaptureAndSetAsync("mat_queue_mode", 0, token);
        await TryCaptureAndSetAsync("engine_no_focus_sleep", 0, token);
        await TryCaptureAndSetAsync("cl_predict", 0, token);
        await TryCaptureAndSetAsync("cl_interp_ratio", 2, token);
        await TryCaptureAndSetAsync("cl_interp", 0.1, token);
        await TryCaptureAndSetAsync("cl_updaterate", 60, token);
        await TryCaptureAndSetAsync("sv_minupdaterate", 60, token);
        await TryCaptureAndSetAsync("sv_maxupdaterate", 60, token);

        if (foregroundFpsLimit is > 0)
            await TryCaptureAndSetAsync("fps_max", foregroundFpsLimit.Value, token);

        // This is the required temporal-supersampling contract. Source builds
        // that do not echo the query are restored to the established safe 0.
        await CaptureAndSetRequiredAsync("host_framerate", hostFramerate, 0, token);
    }

    public async Task RestoreAsync()
    {
        if (!_netCon.IsConnected)
            return;

        for (var i = _captured.Count - 1; i >= 0; i--)
        {
            var item = _captured[i];
            try
            {
                using var cts = new CancellationTokenSource(CommandTimeout);
                await _netCon.ExecuteAsync($"{item.Name} {Format(item.OriginalValue)}", CommandTimeout, cts.Token);
                _log("Info", $"录制环境：已恢复 {item.Name} {Format(item.OriginalValue)}");
            }
            catch (Exception ex)
            {
                _log("Warning", $"录制环境：恢复 {item.Name} 失败：{ex.Message}");
            }
        }

        _captured.Clear();
    }

    private async Task TryCaptureAndSetAsync(string name, double value, CancellationToken token)
    {
        var original = await TryReadAsync(name, token);
        if (original is null)
        {
            _log("Warning", $"录制环境：无法可靠读取 {name}，未覆盖该变量。");
            return;
        }

        await SetCapturedAsync(name, original.Value, value, token);
    }

    private async Task CaptureAndSetRequiredAsync(string name, double value, double restoreFallback, CancellationToken token)
    {
        var original = await TryReadAsync(name, token);
        if (original is null)
        {
            original = restoreFallback;
            _log("Warning", $"录制环境：无法读取 {name} 原值；结束后恢复安全值 {Format(restoreFallback)}。");
        }

        await SetCapturedAsync(name, original.Value, value, token);
    }

    private async Task<double?> TryReadAsync(string name, CancellationToken token)
    {
        try
        {
            var result = await _netCon.ExecuteStrictAsync(name, CommandTimeout, [], token);
            return MomentumReplaySession.TryReadNumericConVar(result, name, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            _log("Warning", $"录制环境：查询 {name} 失败：{ex.Message}");
            return null;
        }
    }

    private async Task SetCapturedAsync(string name, double originalValue, double value, CancellationToken token)
    {
        await _netCon.ExecuteAsync($"{name} {Format(value)}", CommandTimeout, token);
        _captured.Add(new CapturedConVar(name, originalValue));
        _log("Info", $"录制环境：{name} {Format(value)}（原值 {Format(originalValue)}）");
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private sealed record CapturedConVar(string Name, double OriginalValue);
}
