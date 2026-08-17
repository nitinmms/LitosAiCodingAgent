using System.Buffers.Binary;
using System.IO.Compression;

namespace Litos.Console;

/// <summary>
/// Minimal, dependency-free RGBA-to-PNG encoder — exists solely so Windows clipboard image paste
/// (ClipboardImageReader/Win32Clipboard) can hand the model real PNG bytes without pulling in
/// System.Drawing/WinForms (Windows-only, and its "write" path needs an STA thread/HWND — see
/// Win32Clipboard's own remarks) or a general-purpose imaging library the rest of this codebase
/// has never needed. Deliberately not a general PNG writer: no palette/interlacing/ancillary
/// chunks, 8-bit RGBA only, one IDAT chunk. Uses System.IO.Compression.ZLibStream for the actual
/// DEFLATE compression (BCL, no extra dependency) — only the PNG container framing (signature,
/// IHDR/IDAT/IEND chunks, per-scanline filter-type-0 prefix, big-endian length/CRC32) is
/// hand-rolled here.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="pixels">Top-to-bottom, left-to-right RGBA bytes, 4 bytes per pixel, width*height*4 total.</param>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes for a {width}x{height} RGBA image, got {pixels.Length}.", nameof(pixels));

        using var output = new MemoryStream();
        output.Write(Signature);

        WriteChunk(output, "IHDR", BuildIhdr(width, height));
        WriteChunk(output, "IDAT", DeflateScanlines(width, height, pixels));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression method (only value PNG defines)
        ihdr[11] = 0; // filter method (only value PNG defines)
        ihdr[12] = 0; // interlace: none
        return ihdr;
    }

    /// <summary>
    /// Each scanline is prefixed with filter-type 0 ("None" — store raw bytes unmodified) per the
    /// PNG spec, then the whole (height * (1 + width*4))-byte buffer is zlib-compressed as the
    /// single IDAT chunk's payload.
    /// </summary>
    private static byte[] DeflateScanlines(int width, int height, ReadOnlySpan<byte> pixels)
    {
        var stride = width * 4;
        var raw = new byte[height * (1 + stride)];
        for (var row = 0; row < height; row++)
        {
            var rawOffset = row * (1 + stride);
            raw[rawOffset] = 0; // filter type: None
            pixels.Slice(row * stride, stride).CopyTo(raw.AsSpan(rawOffset + 1, stride));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        output.Write(lengthBytes);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>Standard PNG/zlib CRC-32 (polynomial 0xEDB88320), computed over the chunk type + data.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in type)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
