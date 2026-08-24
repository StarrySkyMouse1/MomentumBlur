using System.Net.Sockets;
using System.Text;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class MomentumNetConClient : INetConClient
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _commands = new(1, 1);
    public event Action<string>? OutputReceived;

    public bool IsConnected => _client?.Connected == true;

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
            catch (Exception ex)
            {
                last = ex;
                try { _client?.Dispose(); } catch { }
                await Task.Delay(500, token);
            }
        }
        throw new TimeoutException("无法连接 Momentum Mod NetCon。", last);
    }

    public Task ExecuteAsync(string command, TimeSpan timeout, CancellationToken token)
        => ExecuteCheckedAsync(command, timeout, null, token);

    public async Task ExecuteCheckedAsync(string command, TimeSpan timeout, Func<string, bool>? failure, CancellationToken token)
    {
        await ExecuteStrictCoreAsync(command, timeout, [], failure, token);
    }

    /// <summary>
    /// Strict typed command: appends an ACK marker, captures console lines,
    /// detects failure patterns, and returns a transcript. Throws only on
    /// connection loss / timeout — a matched failure pattern is returned in the
    /// result so the caller can classify it.
    /// </summary>
    public Task<NetConCommandResult> ExecuteStrictAsync(
        string command,
        TimeSpan timeout,
        IReadOnlyList<string> failurePatterns,
        CancellationToken token)
        => ExecuteStrictCoreAsync(command, timeout, failurePatterns, null, token);

    private async Task<NetConCommandResult> ExecuteStrictCoreAsync(
        string command,
        TimeSpan timeout,
        IReadOnlyList<string> failurePatterns,
        Func<string, bool>? failure,
        CancellationToken token)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("NetCon 尚未连接。");
        await _commands.WaitAsync(token);
        try
        {
            var marker = "MMOD_ACK_" + Guid.NewGuid().ToString("N");
            var sentAt = DateTime.UtcNow;
            var captured = new List<string>();
            await _writer.WriteLineAsync($"{command}; echo {marker}");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeoutCts.Token) ?? throw new IOException("NetCon 连接已关闭。");
                OutputReceived?.Invoke(line);
                captured.Add(line);
                if (failure?.Invoke(line) == true)
                    throw new InvalidOperationException(line.Trim());
                if (failurePatterns is not null)
                {
                    foreach (var pattern in failurePatterns)
                    {
                        if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            return new NetConCommandResult(command, marker, sentAt, DateTime.UtcNow, captured, pattern);
                        }
                    }
                }
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    return new NetConCommandResult(command, marker, sentAt, DateTime.UtcNow, captured, null);
                }
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
