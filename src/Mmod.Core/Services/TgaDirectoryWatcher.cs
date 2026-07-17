using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace Mmod.Core.Services;

public sealed partial class TgaDirectoryWatcher : IDisposable
{
    private const int MinTgaHeaderSize = 18;
    private const int ActiveFileStableIdleMs = 120;
    private const int BacklogFileStableIdleMs = 40;
    private const int BaseFullScanIntervalMs = 750;
    private const int BackloggedFullScanIntervalMs = 2000;

    private readonly string _directory;
    private readonly int _pollIntervalMs;
    private readonly ConcurrentDictionary<int, string> _pending = new();
    private readonly ConcurrentDictionary<int, CandidateFile> _candidates = new();
    private readonly object _scanLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private DateTime _sessionStartedUtc;
    private DateTime _lastFullScanUtc = DateTime.MinValue;
    private bool _acceptPreSessionFiles;
    private bool _disposed;
    private int _scanTickRunning;

    [GeneratedRegex(@"(\d+)\.tga$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameIndexRegex();

    public TgaDirectoryWatcher(string directory, int pollIntervalMs = 50)
    {
        _directory = directory;
        _pollIntervalMs = Math.Max(20, pollIntervalMs);
    }

    public event Action? PendingChanged;

    public int PendingCount => _pending.Count;

    public static bool TryParseFrameIndex(string filePath, out int frameIndex)
    {
        frameIndex = -1;
        var name = Path.GetFileName(filePath);
        var match = FrameIndexRegex().Match(name);
        if (!match.Success)
            return false;
        return int.TryParse(match.Groups[1].Value, out frameIndex);
    }

    public static bool IsValidTgaFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < MinTgaHeaderSize)
                return false;
            Span<byte> header = stackalloc byte[MinTgaHeaderSize];
            if (stream.Read(header) < MinTgaHeaderSize)
                return false;
            var width = header[12] | (header[13] << 8);
            var height = header[14] | (header[15] << 8);
            return width > 0 && height > 0 && width <= 7680 && height <= 4320;
        }
        catch
        {
            return false;
        }
    }

    public void Start(DateTime? sessionStartedUtc = null, bool acceptPreSessionFiles = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sessionStartedUtc = sessionStartedUtc ?? DateTime.UtcNow;
        _acceptPreSessionFiles = acceptPreSessionFiles;
        _watcher = new FileSystemWatcher(_directory, "*.tga")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnFsEvent;
        _watcher.Changed += OnFsEvent;
        _watcher.Renamed += (_, e) => OnFsEvent(null!, e);
        ScanDirectory();
        _pollTimer = new Timer(_ => ProcessTick(), null, _pollIntervalMs, _pollIntervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public bool TryTake(int frameIndex, out string filePath)
    {
        if (_pending.TryRemove(frameIndex, out filePath!))
        {
            PendingChanged?.Invoke();
            return true;
        }

        filePath = string.Empty;
        return false;
    }

    public bool TryGetMinPendingFrameIndex(out int frameIndex)
    {
        frameIndex = -1;
        if (_pending.IsEmpty)
            return false;
        frameIndex = _pending.Keys.Min();
        return true;
    }

    public void ScanDirectory()
    {
        if (_disposed)
            return;
        lock (_scanLock)
        {
            try
            {
                _lastFullScanUtc = DateTime.UtcNow;
                foreach (var path in Directory.EnumerateFiles(_directory, "*.tga"))
                {
                    if (TryParseFrameIndex(path, out var index))
                        TrackCandidate(index, path);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
            return;
        if (TryParseFrameIndex(e.FullPath, out var index))
            TrackCandidate(index, e.FullPath);
    }

    private void ProcessTick()
    {
        if (_disposed)
            return;
        if (Interlocked.Exchange(ref _scanTickRunning, 1) == 1)
            return;
        try
        {
            var now = DateTime.UtcNow;
            var fullScanInterval = PendingCount > 500 ? BackloggedFullScanIntervalMs : BaseFullScanIntervalMs;
            if ((now - _lastFullScanUtc).TotalMilliseconds >= fullScanInterval)
                ScanDirectory();
            ProcessCandidates(now);
        }
        finally
        {
            Interlocked.Exchange(ref _scanTickRunning, 0);
        }
    }

    private void ProcessCandidates(DateTime nowUtc)
    {
        foreach (var (index, candidate) in _candidates)
        {
            if (_pending.ContainsKey(index))
            {
                _candidates.TryRemove(index, out _);
                continue;
            }

            try
            {
                if (!File.Exists(candidate.Path))
                {
                    _candidates.TryRemove(index, out _);
                    continue;
                }

                var info = new FileInfo(candidate.Path);
                if (!_acceptPreSessionFiles && info.LastWriteTimeUtc < _sessionStartedUtc.AddSeconds(-3))
                {
                    _candidates.TryRemove(index, out _);
                    continue;
                }

                if (info.Length < MinTgaHeaderSize)
                {
                    candidate.Update(info.Length, info.LastWriteTimeUtc);
                    continue;
                }

                var stable = candidate.IsSameObservation(info.Length, info.LastWriteTimeUtc);
                candidate.Update(info.Length, info.LastWriteTimeUtc);
                var idleMs = (nowUtc - info.LastWriteTimeUtc).TotalMilliseconds;
                var requiredIdleMs = PendingCount > 500 ? BacklogFileStableIdleMs : ActiveFileStableIdleMs;
                if (!stable || idleMs < requiredIdleMs)
                    continue;
                if (!IsValidTgaFile(candidate.Path))
                    continue;
                if (_pending.TryAdd(index, candidate.Path))
                {
                    _candidates.TryRemove(index, out _);
                    PendingChanged?.Invoke();
                }
            }
            catch
            {
                // retry next tick
            }
        }
    }

    private void TrackCandidate(int index, string path)
    {
        if (_pending.ContainsKey(index))
            return;
        if (!ShouldTrackFile(path))
            return;
        _candidates.AddOrUpdate(
            index,
            _ => new CandidateFile(path),
            (_, existing) =>
            {
                existing.Path = path;
                return existing;
            });
    }

    private bool ShouldTrackFile(string path)
    {
        if (_acceptPreSessionFiles)
            return true;
        try
        {
            return new FileInfo(path).LastWriteTimeUtc >= _sessionStartedUtc.AddSeconds(-3);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private sealed class CandidateFile(string path)
    {
        public string Path { get; set; } = path;
        public long LastLength { get; private set; } = -1;
        public DateTime LastWriteUtc { get; private set; } = DateTime.MinValue;

        public bool IsSameObservation(long length, DateTime lastWriteUtc) =>
            length > 0 && length == LastLength && lastWriteUtc == LastWriteUtc;

        public void Update(long length, DateTime lastWriteUtc)
        {
            LastLength = length;
            LastWriteUtc = lastWriteUtc;
        }
    }
}
