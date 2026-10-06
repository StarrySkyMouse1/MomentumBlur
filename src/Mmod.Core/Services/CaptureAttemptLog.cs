using System.Text;

namespace Mmod.Core.Services;

/// <summary>Attempt-local, flushed diagnostic transcript; SQLite remains authoritative.</summary>
internal sealed class CaptureAttemptLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly INetConClient _netCon;
    private readonly Action<string> _console;
    private readonly Action<string> _reportFailure;
    private readonly object _gate = new();
    private bool _failed;

    public CaptureAttemptLog(string path, INetConClient netCon, Action<string> reportFailure)
    {
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _reportFailure = reportFailure;
        _netCon = netCon;
        _console = line => Write("NetConOutput", line);
        _netCon.OutputReceived += _console;
    }

    public void Write(string kind, string? message)
    {
        lock (_gate)
        {
            if (_failed)
                return;
            try { _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{kind}] {message}"); }
            catch (Exception ex)
            {
                _failed = true;
                _reportFailure($"Attempt 文本日志写入失败，继续保留数据库日志：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _netCon.OutputReceived -= _console;
        lock (_gate)
        {
            try { _writer.Dispose(); }
            catch (Exception ex) { _reportFailure($"Attempt 文本日志关闭失败：{ex.Message}"); }
        }
    }
}
