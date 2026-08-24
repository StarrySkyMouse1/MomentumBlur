using System.Text;

namespace Mmod.Core.Services;

using Mmod.Core.Models;

/// <summary>
/// Minimal MP4/ISO-BMFF media probe: validates container readability, video
/// stream presence, resolution, frame rate, frame count and duration without
/// any external dependency. Used as the gate before a clip may be committed.
/// </summary>
public sealed class MediaProbe : IMediaProbe
{
    public MediaProbeResult Probe(string path, int? expectedWidth = null, int? expectedHeight = null, double? expectedFps = null)
    {
        try
        {
            if (!File.Exists(path))
                return MediaProbeResult.Fail("文件不存在。");

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 16)
                return MediaProbeResult.Fail("文件过小，缺少容器头。");

            Span<byte> head = stackalloc byte[16];
            if (fs.Read(head) < 16)
                return MediaProbeResult.Fail("无法读取容器头。");
            if (Encoding.ASCII.GetString(head.Slice(4, 4)) != "ftyp")
                return MediaProbeResult.Fail("缺少 ftyp box（不是有效 MP4）。");

            fs.Position = 0;
            var moov = FindBox(fs, "moov", fs.Length);
            if (moov is null)
                return MediaProbeResult.Fail("缺少 moov box（未正确 finalize）。");

            // Movie header: timescale + duration.
            var mvhd = FindChildBox(fs, moov.Value, "mvhd");
            var movieDuration = ReadMvhdDuration(fs, mvhd);
            if (movieDuration is null || movieDuration.Value.DurationSeconds <= 0)
                return MediaProbeResult.Fail("mvhd 时长无效。");

            // First video track: tkhd dimensions + mdhd track duration + stts sample count.
            var track = FindFirstTrack(fs, moov.Value);
            if (track is null)
                return MediaProbeResult.Fail("没有视频轨。");

            var dims = ReadTkhdDimensions(fs, track.Value);
            var trackDuration = ReadMdhdDuration(fs, track.Value);
            var sampleCount = ReadSttsSampleCount(fs, track.Value);

            if (sampleCount <= 0)
                return MediaProbeResult.Fail("stts 样本数无效（0 帧）。");

            var durationSeconds = trackDuration?.DurationSeconds ?? movieDuration.Value.DurationSeconds;
            if (durationSeconds <= 0)
                return MediaProbeResult.Fail("轨道时长无效。");

            var fps = sampleCount / durationSeconds;

            if (expectedWidth is not null && (dims is null || dims.Value.Width != expectedWidth))
                return MediaProbeResult.Fail($"分辨率不符：实际 {dims?.Width}x{dims?.Height}，期望 {expectedWidth}x{expectedHeight}。");
            if (expectedHeight is not null && (dims is null || dims.Value.Height != expectedHeight))
                return MediaProbeResult.Fail($"分辨率不符：实际 {dims?.Width}x{dims?.Height}，期望 {expectedWidth}x{expectedHeight}。");
            if (expectedFps is not null && Math.Abs(fps - expectedFps.Value) > 1.0)
                return MediaProbeResult.Fail($"帧率不符：实际 {fps:0.###} fps，期望 ~{expectedFps.Value:0.###}。");
            if (dims is null || dims.Value.Width <= 0 || dims.Value.Height <= 0)
                return MediaProbeResult.Fail("视频轨分辨率无效。");

            return new MediaProbeResult(
                IsValid: true,
                Width: dims.Value.Width,
                Height: dims.Value.Height,
                Fps: fps,
                FrameCount: sampleCount,
                DurationSeconds: durationSeconds,
                Error: null);
        }
        catch (Exception ex)
        {
            return MediaProbeResult.Fail($"MediaProbe 异常：{ex.Message}");
        }
    }

    private static (long Offset, long Size)? FindBox(Stream fs, string type, long limit)
    {
        var hdrBuf = new byte[8];
        var extBuf = new byte[8];
        long pos = 0;
        while (pos + 8 <= limit)
        {
            fs.Position = pos;
            var header = ReadBoxHeader(fs, hdrBuf, extBuf);
            if (header is null)
                return null;
            var (size, headerSize, boxType) = header.Value;
            if (size == 0)
                size = limit - pos;

            if (boxType == type)
                return (pos + headerSize, size - headerSize);
            if (size <= 0)
                return null;
            pos += size;
        }
        return null;
    }

    private static (long Offset, long Size)? FindChildBox(Stream fs, (long Offset, long Size) parent, string type)
    {
        var hdrBuf = new byte[8];
        var extBuf = new byte[8];
        var end = parent.Offset + parent.Size;
        var pos = parent.Offset;
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            var header = ReadBoxHeader(fs, hdrBuf, extBuf);
            if (header is null)
                return null;
            var (size, headerSize, boxType) = header.Value;
            if (size == 0)
                size = end - pos;

            if (boxType == type)
                return (pos + headerSize, size - headerSize);
            if (size <= 0)
                return null;
            pos += size;
        }
        return null;
    }

    private static (long Offset, long Size)? FindFirstTrack(Stream fs, (long Offset, long Size) moov)
    {
        var hdrBuf = new byte[8];
        var extBuf = new byte[8];
        var moovEnd = moov.Offset + moov.Size;
        var pos = moov.Offset;
        while (pos + 8 <= moovEnd)
        {
            fs.Position = pos;
            var header = ReadBoxHeader(fs, hdrBuf, extBuf);
            if (header is null)
                return null;
            var (size, headerSize, boxType) = header.Value;
            if (size == 0)
                size = moovEnd - pos;

            if (boxType == "trak")
                return (pos + headerSize, size - headerSize);
            if (size <= 0)
                return null;
            pos += size;
        }
        return null;
    }

    private static (long Size, long HeaderSize, string Type)? ReadBoxHeader(Stream fs, byte[] hdrBuf, byte[] extBuf)
    {
        if (fs.Read(hdrBuf, 0, 8) < 8)
            return null;
        long size = ReadU32(hdrBuf, 0);
        var boxType = Encoding.ASCII.GetString(hdrBuf, 4, 4);
        var headerSize = 8L;
        if (size == 1)
        {
            if (fs.Read(extBuf, 0, 8) < 8)
                return null;
            size = (long)ReadU64(extBuf, 0);
            headerSize = 16;
        }
        return (size, headerSize, boxType);
    }

    private static (double DurationSeconds, uint Timescale)? ReadMvhdDuration(Stream fs, (long Offset, long Size)? mvhd)
    {
        if (mvhd is null || mvhd.Value.Size < 20)
            return null;
        fs.Position = mvhd.Value.Offset;
        Span<byte> buf = stackalloc byte[20];
        if (fs.Read(buf) < 20)
            return null;
        var version = buf[0];
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            // version 1: 8-byte creation/modification, 4-byte timescale, 8-byte duration
            fs.Position = mvhd.Value.Offset + 16;
            Span<byte> v1 = stackalloc byte[12];
            if (fs.Read(v1) < 12)
                return null;
            timescale = ReadU32(v1, 0);
            duration = ReadU64(v1, 4);
        }
        else
        {
            timescale = ReadU32(buf, 12);
            duration = ReadU32(buf, 16);
        }
        if (timescale == 0)
            return null;
        return ((double)duration / timescale, timescale);
    }

    private static (int Width, int Height)? ReadTkhdDimensions(Stream fs, (long Offset, long Size) trak)
    {
        var tkhd = FindChildBox(fs, trak, "tkhd");
        if (tkhd is null || tkhd.Value.Size < 84)
            return null;
        fs.Position = tkhd.Value.Offset;
        Span<byte> buf = stackalloc byte[96];
        var read = fs.Read(buf);
        if (read < 84)
            return null;
        var version = buf[0];
        // tkhd: header(4) + creation/modification/ID/reserved/duration (v0=20, v1=32)
        //       + reserved(8) + layer/group/volume/reserved(8) + matrix(36) → width/height.
        var widthOffset = version == 1 ? 88 : 76;
        var width = ReadU32(buf, widthOffset) >> 16;
        var height = ReadU32(buf, widthOffset + 4) >> 16;
        return ((int)width, (int)height);
    }

    private static (double DurationSeconds, uint Timescale)? ReadMdhdDuration(Stream fs, (long Offset, long Size) trak)
    {
        var mdia = FindChildBox(fs, trak, "mdia");
        if (mdia is null)
            return null;
        var mdhd = FindChildBox(fs, mdia.Value, "mdhd");
        if (mdhd is null || mdhd.Value.Size < 20)
            return null;
        fs.Position = mdhd.Value.Offset;
        Span<byte> buf = stackalloc byte[20];
        if (fs.Read(buf) < 20)
            return null;
        var version = buf[0];
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            fs.Position = mdhd.Value.Offset + 16;
            Span<byte> v1 = stackalloc byte[12];
            if (fs.Read(v1) < 12)
                return null;
            timescale = ReadU32(v1, 0);
            duration = ReadU64(v1, 4);
        }
        else
        {
            timescale = ReadU32(buf, 12);
            duration = ReadU32(buf, 16);
        }
        if (timescale == 0)
            return null;
        return ((double)duration / timescale, timescale);
    }

    private static long ReadSttsSampleCount(Stream fs, (long Offset, long Size) trak)
    {
        var mdia = FindChildBox(fs, trak, "mdia");
        if (mdia is null)
            return 0;
        var minf = FindChildBox(fs, mdia.Value, "minf");
        if (minf is null)
            return 0;
        var stbl = FindChildBox(fs, minf.Value, "stbl");
        if (stbl is null)
            return 0;
        var stts = FindChildBox(fs, stbl.Value, "stts");
        if (stts is null || stts.Value.Size < 16)
            return 0;
        fs.Position = stts.Value.Offset;
        Span<byte> buf = stackalloc byte[8];
        if (fs.Read(buf) < 8)
            return 0;
        var entryCount = ReadU32(buf, 4);
        long total = 0;
        fs.Position = stts.Value.Offset + 8;
        Span<byte> entry = stackalloc byte[8];
        for (uint i = 0; i < entryCount; i++)
        {
            if (fs.Read(entry) < 8)
                break;
            total += ReadU32(entry, 0); // sample_count per entry
        }
        return total;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ulong ReadU64(ReadOnlySpan<byte> b, int o) =>
        ((ulong)ReadU32(b, o) << 32) | ReadU32(b, o + 4);
}
