using Litos.Console;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for PngEncoder — the hand-rolled, dependency-free RGBA-to-PNG encoder backing Windows
/// clipboard image paste. Verifies the encoder produces bytes any standard PNG decoder recognizes
/// by round-tripping through System.Drawing... deliberately NOT done, since this project has no
/// System.Drawing dependency and shouldn't gain a test-only one either. Instead these tests verify
/// the PNG container framing directly against the PNG spec: signature bytes, IHDR field layout,
/// per-chunk CRC-32, and a hand-computed CRC for a known IHDR payload as an external check that
/// the CRC table/algorithm itself is correct (not just internally self-consistent).
/// </summary>
public class PngEncoderTests
{
    [Fact]
    public void EncodeRgba_StartsWithThePngSignature()
    {
        var png = PngEncoder.EncodeRgba(1, 1, new byte[4]);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
    }

    [Fact]
    public void EncodeRgba_MismatchedPixelBufferLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.EncodeRgba(2, 2, new byte[4]));
    }

    [Fact]
    public void EncodeRgba_IhdrChunk_EncodesWidthHeightAndRgbaColorType()
    {
        var png = PngEncoder.EncodeRgba(3, 5, new byte[3 * 5 * 4]);

        // Chunk layout: [4-byte length][4-byte type][data][4-byte CRC]. IHDR is always the first
        // chunk, immediately after the 8-byte signature.
        var ihdrLength = ReadUInt32BigEndian(png, 8);
        var ihdrType = System.Text.Encoding.ASCII.GetString(png, 12, 4);
        Assert.Equal(13u, ihdrLength);
        Assert.Equal("IHDR", ihdrType);

        var ihdrData = png.AsSpan(16, 13);
        var width = ReadUInt32BigEndian(ihdrData, 0);
        var height = ReadUInt32BigEndian(ihdrData, 4);
        var bitDepth = ihdrData[8];
        var colorType = ihdrData[9];

        Assert.Equal(3u, width);
        Assert.Equal(5u, height);
        Assert.Equal(8, bitDepth);
        Assert.Equal(6, colorType); // RGBA
    }

    [Fact]
    public void EncodeRgba_EndsWithIendChunk_ZeroLengthPayload()
    {
        var png = PngEncoder.EncodeRgba(1, 1, new byte[4]);

        // IEND is always exactly 12 bytes (4 length + 4 type + 0 data + 4 CRC) at the very end.
        var iendStart = png.Length - 12;
        var iendLength = ReadUInt32BigEndian(png, iendStart);
        var iendType = System.Text.Encoding.ASCII.GetString(png, iendStart + 4, 4);

        Assert.Equal(0u, iendLength);
        Assert.Equal("IEND", iendType);
    }

    [Fact]
    public void EncodeRgba_IhdrCrc_MatchesIndependentlyComputedCrc32()
    {
        var png = PngEncoder.EncodeRgba(1, 1, new byte[4]);

        // type(4) + data(13) immediately follow the length field at offset 8.
        var typeAndData = png.AsSpan(12, 4 + 13).ToArray();
        var expectedCrc = Crc32Reference(typeAndData);

        var actualCrc = ReadUInt32BigEndian(png, 12 + 4 + 13);
        Assert.Equal(expectedCrc, actualCrc);
    }

    [Fact]
    public void EncodeRgba_ContainsExactlyOneIdatChunk()
    {
        var png = PngEncoder.EncodeRgba(2, 2, new byte[2 * 2 * 4]);

        var idatCount = 0;
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)ReadUInt32BigEndian(png, offset);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
                idatCount++;
            offset += 4 + 4 + length + 4;
        }

        Assert.Equal(1, idatCount);
    }

    [Fact]
    public void EncodeRgba_IdatPayload_DecompressesToPixelDataWithFilterBytePrefix()
    {
        const int width = 2;
        const int height = 2;
        byte[] pixels =
        [
            255, 0, 0, 255,    0, 255, 0, 255,
            0, 0, 255, 255,    255, 255, 0, 255,
        ];

        var png = PngEncoder.EncodeRgba(width, height, pixels);
        var idatData = ExtractChunkData(png, "IDAT");

        using var compressedStream = new MemoryStream(idatData);
        using var zlib = new System.IO.Compression.ZLibStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        var raw = decompressed.ToArray();

        var stride = width * 4;
        Assert.Equal(height * (1 + stride), raw.Length);

        for (var row = 0; row < height; row++)
        {
            var rowStart = row * (1 + stride);
            Assert.Equal(0, raw[rowStart]); // filter type: None
            Assert.Equal(pixels.AsSpan(row * stride, stride).ToArray(), raw.AsSpan(rowStart + 1, stride).ToArray());
        }
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static byte[] ExtractChunkData(byte[] png, string chunkType)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)ReadUInt32BigEndian(png, offset);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == chunkType)
                return png.AsSpan(offset + 8, length).ToArray();
            offset += 4 + 4 + length + 4;
        }

        throw new InvalidOperationException($"Chunk '{chunkType}' not found.");
    }

    /// <summary>Independent reference CRC-32 (same IEEE 802.3 / zlib polynomial PNG mandates), used to check PngEncoder's own table-driven implementation isn't internally-consistent-but-wrong.</summary>
    private static uint Crc32Reference(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
