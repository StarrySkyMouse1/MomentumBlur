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
                _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                await _writer.WriteLineAsync(password);
                return;
            }
            catch (Exception ex) { last = ex; _client?.Dispose(); await Task.Delay(500, token); }
        }
        throw new TimeoutException("无法连接 Momentum Mod NetCon。", last);
    }

    public async Task ExecuteAsync(string command, TimeSpan timeout, CancellationToken token)
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
                if (line.Contains(marker, StringComparison.Ordinal)) return;
            }
        }
        finally { _commands.Release(); }
    }

    public ValueTask DisposeAsync() { _writer?.Dispose(); _reader?.Dispose(); _client?.Dispose(); _commands.Dispose(); return ValueTask.CompletedTask; }
}
