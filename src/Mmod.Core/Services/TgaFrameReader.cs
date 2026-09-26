using System.Buffers;
using System.IO;

namespace Mmod.Core.Services;

public static class TgaFrameReader
{
    private const int HeaderSize = 18;
    private static readonly ParallelOptions ConversionParallelism = new()
    {
        // Conversion is the only CPU-parallel portion of the ordered pipeline.
        // Keep capacity for the game, UI and Media Foundation encoder instead
        // of saturating every logical processor.
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)),
    };

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
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length < HeaderSize)
                return false;

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);

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

            var dstStride = checked(width * 4);
            bgra = GC.AllocateUninitializedArray<byte>(checked(dstStride * height));

            if (bpp == 32)
            {
                // Source startmovie commonly produces BGRA32. Read it directly
                // into the final buffer, reversing whole rows only when the TGA
                // origin is at the bottom. This is byte-for-byte equivalent to
                // the old per-pixel copy without a second full-frame allocation.
                for (var sourceRow = 0; sourceRow < height; sourceRow++)
                {
                    var destinationRow = topOrigin ? sourceRow : height - 1 - sourceRow;
                    stream.ReadExactly(bgra.AsSpan(destinationRow * dstStride, dstStride));
                }
            }
            else
            {
                // Source startmovie currently emits RGB24. Keep its source
                // frame in the shared pool and expand independent rows in
                // parallel. Each worker writes a disjoint destination span, so
                // ordering and pixel values are identical to the serial path.
                var sourceBuffer = ArrayPool<byte>.Shared.Rent(expected);
                try
                {
                    stream.ReadExactly(sourceBuffer.AsSpan(0, expected));
                    var frameWidth = width;
                    var frameHeight = height;
                    var destinationBuffer = bgra;
                    Parallel.For(0, frameHeight, ConversionParallelism, sourceRow =>
                    {
                        var destinationRow = topOrigin ? sourceRow : frameHeight - 1 - sourceRow;
                        var sourceOffset = sourceRow * srcStride;
                        var destinationOffset = destinationRow * dstStride;
                        for (var x = 0; x < frameWidth; x++)
                        {
                            var sourcePixel = sourceOffset + x * 3;
                            var destinationPixel = destinationOffset + x * 4;
                            destinationBuffer[destinationPixel] = sourceBuffer[sourcePixel];
                            destinationBuffer[destinationPixel + 1] = sourceBuffer[sourcePixel + 1];
                            destinationBuffer[destinationPixel + 2] = sourceBuffer[sourcePixel + 2];
                            destinationBuffer[destinationPixel + 3] = 255;
                        }
                    });
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(sourceBuffer);
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
