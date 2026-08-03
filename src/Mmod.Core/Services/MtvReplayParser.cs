using System.Buffers.Binary;
using System.Text;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public static class MtvReplayParser
{
    private const int MinimumHeaderSize = 0xB5;
    private const int MapOffset = 0x10;
    private const int MapLength = 64;
    private const int PlayerIdOffset = 0x50;
    private const int PlayerIdLength = 41;
    private const int PlayerNameOffset = 0x87;
    private const int PlayerNameLength = 32;
    private const int TrackOffset = 0xA7;
    private const int StageOffset = 0xA8;
    private const int RunTimeOffset = 0xA9;
    private const int TickCountOffset = 0xB1;

    public static ReplayRecord Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[MinimumHeaderSize];
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual("MMTV"u8))
            throw new InvalidDataException("不是有效的 MMTV 回放文件。");

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        if (version is < 1 or > 2)
            throw new NotSupportedException($"暂不支持 MMTV 版本 {version}。");

        var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(header[8..16]);
        var map = ReadFixedUtf8(header.Slice(MapOffset, MapLength));
        var playerId = ReadFixedUtf8(header.Slice(PlayerIdOffset, PlayerIdLength));
        var player = ReadFixedUtf8(header.Slice(PlayerNameOffset, PlayerNameLength));
        var track = header[TrackOffset];
        var stage = header[StageOffset];
        var runTime = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.Slice(RunTimeOffset, 8)));
        var ticks = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(TickCountOffset, 4));

        if (string.IsNullOrWhiteSpace(map))
            throw new InvalidDataException("回放缺少地图名。");
        if (track <= 0 || stage <= 0)
            throw new InvalidDataException("回放缺少有效赛道或阶段编号。");
        if (!double.IsFinite(runTime) || runTime <= 0)
            throw new InvalidDataException("回放时长无效。");

        DateTimeOffset recordedAt;
        try { recordedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs); }
        catch (ArgumentOutOfRangeException) { recordedAt = File.GetLastWriteTimeUtc(filePath); }

        var normalized = Path.GetFullPath(filePath);
        var onlineSegment = $"{Path.DirectorySeparatorChar}online{Path.DirectorySeparatorChar}";
        var source = normalized.Contains(onlineSegment, StringComparison.OrdinalIgnoreCase)
            ? ReplaySourceKind.Online
            : ReplaySourceKind.Local;

        return new ReplayRecord(
            normalized,
            version,
            map,
            string.IsNullOrWhiteSpace(player) ? "未知玩家" : player,
            playerId,
            track,
            stage,
            runTime,
            Math.Max(0, ticks),
            recordedAt,
            source);
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> bytes)
    {
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0)
            bytes = bytes[..zero];
        return Encoding.UTF8.GetString(bytes).Trim();
    }
}
