using System.IO;

namespace Mmod.Core.Services;

public static class TgaFrameReader
{
    private const int HeaderSize = 18;

    public static bool TryReadBgra(
        string path,
        out int width,
        out int height,
        out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = [];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < HeaderSize)
                return false;

            Span<byte> header = stackalloc byte[HeaderSize];
            if (stream.Read(header) < HeaderSize)
                return false;

            if (header[2] != 2)
                return false;

            width = header[12] | (header[13] << 8);
            height = header[14] | (header[15] << 8);
            var bpp = header[16];
            var descriptor = header[17];
            var topOrigin = (descriptor & 0x20) != 0;

            if (width is <= 0 or > 7680 || height is <= 0 or > 4320)
                return false;
            if (bpp is not (24 or 32))
                return false;

            var srcStride = width * (bpp / 8);
            var expected = srcStride * height;
            if (stream.Length < HeaderSize + expected)
                return false;

            var raw = new byte[expected];
            if (stream.Read(raw, 0, raw.Length) != raw.Length)
                return false;

            bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var srcY = topOrigin ? y : height - 1 - y;
                var srcRow = srcY * srcStride;
                var dstRow = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var si = srcRow + x * (bpp / 8);
                    var di = dstRow + x * 4;
                    bgra[di + 0] = raw[si + 0];
                    bgra[di + 1] = raw[si + 1];
                    bgra[di + 2] = raw[si + 2];
                    bgra[di + 3] = bpp == 32 ? raw[si + 3] : (byte)255;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
