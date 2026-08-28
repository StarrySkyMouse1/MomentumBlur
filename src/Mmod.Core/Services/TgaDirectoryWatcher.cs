using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace Mmod.Core.Services;

/// <summary>
/// Session-scoped TGA directory watcher. Only accepts files matching the exact
/// session sequence prefix (<c>{prefix}{index}.tga</c>), so stale TGA from a
/// previous attempt, other startmovie sessions, or manual recordings can never
/// contaminate the current capture session. Exposes physical-write metrics for
/// positive quiescence proof.
/// </summary>
public sealed partial class TgaDirectoryWatcher : ITgaCaptureWatcher
{
    private const int MinTgaHeaderSize = 18;
    private const int ActiveFileStableIdleMs = 120;
    private const int BacklogFileStableIdleMs = 40;
    private const int BaseFullScanIntervalMs = 750;
    private const int BackloggedFullScanIntervalMs = 2000;

    private readonly string _directory;
    private readonly string _sequencePrefix;
    private readonly int _pollIntervalMs;
    private readonly ConcurrentDictionary<int, string> _pending = new();
    private readonly ConcurrentDictionary<int, CandidateFile> _candidates = new();
    private readonly object _scanLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private DateTime _sessionStartedUtc;
    private DateTime _lastFullScanUtc = DateTime.MinValue;
    private bool _acceptPreSessionFiles;
    private bool _frozen;
    private bool _disposed;
    private int _scanTickRunning;

    private DateTime _lastPhysicalWriteUtc = DateTime.MinValue;
    private DateTime _lastStableFrameUtc = DateTime.MinValue;
    private int? _lastAcceptedFrameIndex;
    private int _maxObservedFrameIndex = -1;
    private int _sessionFileCount;
    private long _pendingBytes;
    private long _producedCount;
    private long _peakPendingFrames;
    private long _peakPendingBytes;
    private bool _hasReadFailure;

    public TgaDirectoryWatcher(string directory, string sequencePrefix, int pollIntervalMs = 50)
    {
        _directory = directory;
        _sequencePrefix = string.IsNullOrWhiteSpace(sequencePrefix) ? string.Empty : sequencePrefix.Trim();
        _pollIntervalMs = Math.Max(20, pollIntervalMs);
    }

    public event Action? PendingChanged;

    public int PendingCount => _pending.Count;
    public int CandidateCount => _candidates.Count;
    public DateTime? LastPhysicalFileWriteUtc => _lastPhysicalWriteUtc == DateTime.MinValue ? null : _lastPhysicalWriteUtc;
    public DateTime? LastStableFrameUtc => _lastStableFrameUtc == DateTime.MinValue ? null : _lastStableFrameUtc;
    public int? LastAcceptedFrameIndex => _lastAcceptedFrameIndex;
    public int MaxObservedFrameIndex => _maxObservedFrameIndex;
    public int SessionFileCount => _sessionFileCount;
    public bool HasUnstableFiles => !_candidates.IsEmpty;
    public bool IsFrozen => _frozen;
    public string SequencePrefix => _sequencePrefix;

    /// <summary>Total bytes of the current-session pending files (long; file-length reads are best-effort).</summary>
    public long PendingBytes => _pendingBytes;

    /// <summary>
    /// Number of stable frames accepted into pending for the current session.
    /// A file system event for an index that is already pending is never
    /// counted again, so duplicate events cannot inflate production.
    /// </summary>
    public long ProducedCount => _producedCount;

    public long PeakPendingFrames => _peakPendingFrames;
    public long PeakPendingBytes => _peakPendingBytes;

    /// <summary>
    /// True when a pending file disappeared or its length could not be read
    /// while computing backlog bytes. Telemetry degrades gracefully; it never
    /// throws into the capture path.
    /// </summary>
    public bool HasPendingReadFailure => _hasReadFailure;

    private Regex BuildPrefixRegex() => new(
        "^" + Regex.Escape(_sequencePrefix) + @"(\d+)\.tga$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses {prefix}{index}.tga for the exact session prefix. When the prefix
    /// is empty (legacy manual mode) any numeric-tail .tga is accepted.
    /// </summary>
    public static bool TryParseFrameIndex(string filePath, string sequencePrefix, out int frameIndex)
    {
        frameIndex = -1;
        var name = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(sequencePrefix))
        {
            var legacy = FrameIndexLegacyRegex().Match(name);
            if (!legacy.Success)
                return false;
            return int.TryParse(legacy.Groups[1].Value, out frameIndex);
        }

        var match = ExactPrefixRegex(sequencePrefix).Match(name);
        if (!match.Success)
            return false;
        return int.TryParse(match.Groups[1].Value, out frameIndex);
    }

    [GeneratedRegex(@"(\d+)\.tga$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameIndexLegacyRegex();

    private static Regex ExactPrefixRegex(string prefix) => new(
        "^" + Regex.Escape(prefix) + @"(\d+)\.tga$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    private static bool LooksLikeCompleteTga(FileInfo info)
    {
        try
        {
            using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < MinTgaHeaderSize)
                return false;
            Span<byte> header = stackalloc byte[MinTgaHeaderSize];
            if (stream.Read(header) < MinTgaHeaderSize)
                return false;
            if (header[2] != 2)
                return false;
            var width = header[12] | (header[13] << 8);
            var height = header[14] | (header[15] << 8);
            var bpp = header[16];
            if (width is <= 0 or > 7680 || height is <= 0 or > 4320)
                return false;
            if (bpp is not (24 or 32))
                return false;
            var expected = (long)width * height * (bpp / 8) + MinTgaHeaderSize;
            // 允许极小尾部差异，拒绝明显半截文件
            return stream.Length >= expected && stream.Length <= expected + 64;
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

    public void Freeze()
    {
        _frozen = true;
    }

    public bool TryTake(int frameIndex, out string filePath)
    {
        if (_pending.TryRemove(frameIndex, out filePath!))
        {
            OnPendingRemoved();
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

    public void ForceFullScan() => ScanDirectory();

    public async Task WaitForQuiescenceAsync(TimeSpan quietWindow, TimeSpan hardTimeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + hardTimeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"TGA 物理静默超时（{hardTimeout.TotalSeconds:0}s）：候选={CandidateCount} 待处理={PendingCount} " +
                    $"最后物理写入={LastPhysicalFileWriteUtc?.ToString("O") ?? "无"} 未稳定文件={HasUnstableFiles}");
            }

            ForceFullScan();

            var now = DateTime.UtcNow;
            var quietEnough =
                (LastPhysicalFileWriteUtc is null || now - LastPhysicalFileWriteUtc.Value >= quietWindow)
                && CandidateCount == 0
                && !HasUnstableFiles;

            if (quietEnough)
            {
                // Final scan must stay quiet for a full quiet window to prove the writer stopped.
                var afterScan = DateTime.UtcNow;
                if (afterScan - now >= quietWindow
                    || (LastPhysicalFileWriteUtc is null || afterScan - LastPhysicalFileWriteUtc.Value >= quietWindow))
                {
                    return;
                }
            }

            await Task.Delay(100, token);
        }
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
                var sessionFiles = 0;
                foreach (var path in Directory.EnumerateFiles(_directory, "*.tga"))
                {
                    if (TryParseFrameIndex(path, _sequencePrefix, out var index))
                    {
                        sessionFiles++;
                        if (index > _maxObservedFrameIndex)
                            _maxObservedFrameIndex = index;
                        TrackCandidate(index, path);
                    }
                }
                _sessionFileCount = sessionFiles;
            }
            catch
            {
                // ignored; retried next tick
            }
        }
    }

    public void CleanupSessionFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.tga"))
            {
                if (!TryParseFrameIndex(path, _sequencePrefix, out _))
                    continue;
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void PruneMissingPending()
    {
        var removed = false;
        foreach (var (index, path) in _pending)
        {
            try
            {
                if (File.Exists(path))
                    continue;
            }
            catch
            {
                // treat as missing
            }

            _hasReadFailure = true; // file vanished; telemetry degrades, capture path unaffected
            if (_pending.TryRemove(index, out _))
                removed = true;
        }

        if (removed)
        {
            OnPendingRemoved();
            PendingChanged?.Invoke();
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed || _frozen)
            return;
        if (TryParseFrameIndex(e.FullPath, _sequencePrefix, out var index))
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
            PruneMissingPending();
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
            if (_frozen)
                break;
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

                if (info.LastWriteTimeUtc > _lastPhysicalWriteUtc)
                    _lastPhysicalWriteUtc = info.LastWriteTimeUtc;

                if (info.Length < MinTgaHeaderSize)
                {
                    candidate.Update(info.Length, info.LastWriteTimeUtc);
                    continue;
                }

                // 已收尾的完整帧：文件大小应接近声明分辨率，避免误收半截写入
                if (!LooksLikeCompleteTga(info))
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
                    _lastAcceptedFrameIndex = index;
                    _lastStableFrameUtc = DateTime.UtcNow;
                    _producedCount++;
                    OnPendingAdded(candidate.Path);
                    PendingChanged?.Invoke();
                }
            }
            catch
            {
                // retry next tick
            }
        }
    }

    /// <summary>Best-effort length of one pending file; never throws.</summary>
    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private void OnPendingAdded(string path)
    {
        var length = TryGetFileLength(path);
        if (length < 0)
        {
            _hasReadFailure = true; // telemetry-only; never breaks the capture path
            length = 0;
        }

        _pendingBytes += length;
        if (_pendingBytes > _peakPendingBytes)
            _peakPendingBytes = _pendingBytes;
        var frames = (long)_pending.Count;
        if (frames > _peakPendingFrames)
            _peakPendingFrames = frames;
    }

    /// <summary>Recomputes pending bytes from live file lengths (file-level, long).</summary>
    private void OnPendingRemoved()
    {
        long total = 0;
        foreach (var path in _pending.Values)
        {
            var length = TryGetFileLength(path);
            if (length < 0)
            {
                _hasReadFailure = true;
                length = 0;
            }

            total += length;
        }

        _pendingBytes = total;
        if (_pendingBytes > _peakPendingBytes)
            _peakPendingBytes = _pendingBytes;
        var frames = (long)_pending.Count;
        if (frames > _peakPendingFrames)
            _peakPendingFrames = frames;
    }

    private void TrackCandidate(int index, string path)
    {
        if (_frozen)
            return;
        if (_pending.ContainsKey(index))
            return;
        if (!ShouldTrackFile(path))
            return;
        try
        {
            var writeTime = new FileInfo(path).LastWriteTimeUtc;
            if (writeTime > _lastPhysicalWriteUtc)
                _lastPhysicalWriteUtc = writeTime;
        }
        catch
        {
            // ignore
        }
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
