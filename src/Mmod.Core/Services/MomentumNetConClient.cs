using System.Net.Sockets;
using System.Text;

namespace Mmod.Core.Services;

public sealed class MomentumNetConClient : IAsyncDisposable
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _commands = new(1, 1);
    public event Action<string>? OutputReceived;

    public async Task ConnectAsync(int port, string password, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync("127.0.0.1", port, token);
                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await _writer.WriteLineAsync($"PASS {password}");
                // Authentication has no dedicated success packet. An echoed
                // marker proves both that PASS was accepted and that commands
                // are actually being executed before the runner continues.
                await ExecuteAsync("echo MMOD_NETCON_AUTHENTICATED", TimeSpan.FromSeconds(10), token);
                return;
            }
            catch (Exception ex) { last = ex; _client?.Dispose(); await Task.Delay(500, token); }
        }
        throw new TimeoutException("无法连接 Momentum Mod NetCon。", last);
    }

    public async Task ExecuteAsync(string command, TimeSpan timeout, CancellationToken token)
        => await ExecuteCheckedAsync(command, timeout, null, token);

    public async Task ExecuteCheckedAsync(string command, TimeSpan timeout, Func<string, bool>? failure, CancellationToken token)
    {
        if (_writer is null || _reader is null) throw new InvalidOperationException("NetCon 尚未连接。");
        await _commands.WaitAsync(token);
        try
        {
            var marker = "MMOD_ACK_" + Guid.NewGuid().ToString("N");
            await _writer.WriteLineAsync($"{command}; echo {marker}");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeoutCts.Token) ?? throw new IOException("NetCon 连接已关闭。");
                OutputReceived?.Invoke(line);
                if (failure?.Invoke(line) == true) throw new InvalidOperationException(line.Trim());
                if (line.Contains(marker, StringComparison.Ordinal)) return;
            }
        }
        finally { _commands.Release(); }
    }

    public async Task SendAsync(string command, CancellationToken token)
    {
        if (_writer is null) throw new InvalidOperationException("NetCon 尚未连接。");
        await _commands.WaitAsync(token);
        try { await _writer.WriteLineAsync(command); }
        finally { _commands.Release(); }
    }

    public async Task<string> WaitForOutputAsync(
        Func<string, bool> success,
        Func<string, bool>? failure,
        TimeSpan timeout,
        CancellationToken token)
    {
        if (_reader is null) throw new InvalidOperationException("NetCon 尚未连接。");
        await _commands.WaitAsync(token);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeoutCts.Token) ?? throw new IOException("NetCon 连接已关闭。");
                OutputReceived?.Invoke(line);
                if (failure?.Invoke(line) == true) throw new InvalidOperationException(line.Trim());
                if (success(line)) return line;
            }
        }
        finally { _commands.Release(); }
    }

    public ValueTask DisposeAsync() { _writer?.Dispose(); _reader?.Dispose(); _client?.Dispose(); _commands.Dispose(); return ValueTask.CompletedTask; }
}
