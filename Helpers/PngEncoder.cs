using System.IO.Compression;

namespace TurfTime2.Helpers;

/// <summary>
/// Minimal PNG encoder for BGRA32 pixel buffers (e.g. ZXing QR output).
/// Avoids pulling SixLabors.ImageSharp for a single encode path.
/// </summary>
internal static class PngEncoder
{
    public static byte[] EncodeBgra32(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than width*height*4.", nameof(bgra));

        // PNG scanlines: filter byte 0 + RGBA per pixel
        var raw = new byte[height * (1 + width * 4)];
        var dest = 0;
        var src = 0;
        for (var y = 0; y < height; y++)
        {
            raw[dest++] = 0; // filter None
            for (var x = 0; x < width; x++)
            {
                // BGRA → RGBA
                raw[dest++] = bgra[src + 2];
                raw[dest++] = bgra[src + 1];
                raw[dest++] = bgra[src];
                raw[dest++] = bgra[src + 3];
                src += 4;
            }
        }

        using var ms = new MemoryStream();
        // Signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(ms, "IHDR"u8, ihdr);

        // IDAT (zlib-wrapped deflate)
        var compressed = ZlibCompress(raw);
        WriteChunk(ms, "IDAT"u8, compressed);

        // IEND
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);

        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        // zlib header: CMF/FLG (default compression, no dict)
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);

        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        // Adler-32 of uncompressed data
        var adler = Adler32(data);
        Span<byte> checksum = stackalloc byte[4];
        WriteUInt32BigEndian(checksum, 0, adler);
        ms.Write(checksum);

        return ms.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteUInt32BigEndian(len, 0, (uint)data.Length);
        stream.Write(len);
        stream.Write(type);
        stream.Write(data);

        var crc = Crc32.Compute(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteUInt32BigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteUInt32BigEndian(Span<byte> dest, int offset, uint value)
    {
        dest[offset] = (byte)(value >> 24);
        dest[offset + 1] = (byte)(value >> 16);
        dest[offset + 2] = (byte)(value >> 8);
        dest[offset + 3] = (byte)value;
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in type)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
