using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Mmod.Core.Services;

public sealed class MomentumProcessController : IAsyncDisposable
{
    public Process? Process { get; private set; }
    public MomentumNetConClient NetCon { get; } = new();
    public bool OwnsProcess { get; private set; }

    public async Task StartAsync(string gameRoot, CancellationToken token)
    {
        var exe = Path.Combine(gameRoot, "bin", "win64", "momentum.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 Momentum Mod 可执行文件。", exe);
        var port = ReservePort();
        var password = Guid.NewGuid().ToString("N");
        Process = System.Diagnostics.Process.Start(new ProcessStartInfo(exe, $"-console -novid -netconport {port} -netconpassword {password}") { WorkingDirectory = gameRoot, UseShellExecute = true });
        if (Process is null) throw new InvalidOperationException("Momentum Mod 启动失败。");
        OwnsProcess = true;
        await NetCon.ConnectAsync(port, password, TimeSpan.FromMinutes(2), token);
    }

    public async Task CloseOwnedAsync(CancellationToken token)
    {
        if (!OwnsProcess || Process is null || Process.HasExited) return;
        try { await NetCon.ExecuteAsync("quit", TimeSpan.FromSeconds(10), token); } catch { }
        try { await Process.WaitForExitAsync(token); } catch { }
    }

    private static int ReservePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    public async ValueTask DisposeAsync() { await NetCon.DisposeAsync(); Process?.Dispose(); }
}
