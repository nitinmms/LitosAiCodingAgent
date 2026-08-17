using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Litos.Console;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for Win32Clipboard.DecodeBitmapV5ToPng against hand-built BITMAPV5HEADER byte buffers —
/// no real Windows clipboard needed. Verifies row order (DIBs are bottom-up unless height is
/// negative), the BGR(A)-to-RGBA channel swap, 24bpp alpha defaulting to opaque, row-stride
/// padding, and that unsupported formats degrade to null (falls through to text paste) rather
/// than guessing at a wrong decode. The logic under test is pure byte-array manipulation with no
/// live Win32 clipboard call, so it would run identically on any OS — [SupportedOSPlatform] here
/// only silences CA1416 (Win32Clipboard's own attribute), matching this repo's Windows dev/CI
/// environment; it isn't a statement that the logic itself is Windows-specific.
/// </summary>
[SupportedOSPlatform("windows")]
public class Win32ClipboardTests
{
    private const int HeaderSize = 124;

    private static byte[] BuildBitmapV5Header(int width, int height, ushort bitCount, int compression = 0)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), compression);
        return header;
    }

    private static byte[] Build32bppDib(int width, int height, bool topDown, byte[,][] bgraPixels)
    {
        // bgraPixels is indexed [row][col] in TOP-TO-BOTTOM visual order regardless of storage
        // direction — this helper handles writing rows in the DIB's actual on-disk order.
        var header = BuildBitmapV5Header(width, height, 32, compression: 0);
        if (topDown)
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), -height);

        var stride = width * 4;
        var pixelData = new byte[stride * height];
        for (var visualRow = 0; visualRow < height; visualRow++)
        {
            var storageRow = topDown ? visualRow : height - 1 - visualRow;
            for (var col = 0; col < width; col++)
            {
                var px = bgraPixels[visualRow, col];
                var offset = storageRow * stride + col * 4;
                Array.Copy(px, 0, pixelData, offset, 4);
            }
        }

        return [.. header, .. pixelData];
    }

    [Fact]
    public void DecodeBitmapV5ToPng_TooShortBuffer_ReturnsNull()
    {
        var result = Win32Clipboard.DecodeBitmapV5ToPng(new byte[10]);

        Assert.Null(result);
    }

    [Fact]
    public void DecodeBitmapV5ToPng_WrongHeaderSizeField_ReturnsNull()
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 40); // BITMAPINFOHEADER size, not V5

        var result = Win32Clipboard.DecodeBitmapV5ToPng(header);

        Assert.Null(result);
    }

    [Fact]
    public void DecodeBitmapV5ToPng_ZeroWidth_ReturnsNull()
    {
        var dib = BuildBitmapV5Header(0, 1, 32);

        Assert.Null(Win32Clipboard.DecodeBitmapV5ToPng(dib));
    }

    [Fact]
    public void DecodeBitmapV5ToPng_ZeroHeight_ReturnsNull()
    {
        var dib = BuildBitmapV5Header(1, 0, 32);

        Assert.Null(Win32Clipboard.DecodeBitmapV5ToPng(dib));
    }

    [Fact]
    public void DecodeBitmapV5ToPng_UnsupportedBitCount_ReturnsNull()
    {
        var dib = BuildBitmapV5Header(1, 1, 16);

        Assert.Null(Win32Clipboard.DecodeBitmapV5ToPng(dib));
    }

    [Fact]
    public void DecodeBitmapV5ToPng_RleCompression_ReturnsNull()
    {
        const int BI_RLE8 = 1;
        var dib = BuildBitmapV5Header(1, 1, 32, compression: BI_RLE8);

        Assert.Null(Win32Clipboard.DecodeBitmapV5ToPng(dib));
    }

    [Fact]
    public void DecodeBitmapV5ToPng_TruncatedPixelData_ReturnsNull()
    {
        // Header claims 10x10 32bpp but supplies zero pixel bytes.
        var dib = BuildBitmapV5Header(10, 10, 32);

        Assert.Null(Win32Clipboard.DecodeBitmapV5ToPng(dib));
    }

    [Fact]
    public void DecodeBitmapV5ToPng_ValidBottomUp32bpp_ProducesWellFormedPng()
    {
        byte[,][] pixels = new byte[1, 2][];
        pixels[0, 0] = [0, 0, 255, 255];   // BGRA: pure red, opaque
        pixels[0, 1] = [0, 255, 0, 128];   // BGRA: pure green, half alpha

        var dib = Build32bppDib(2, 1, topDown: false, pixels);

        var png = Win32Clipboard.DecodeBitmapV5ToPng(dib);

        Assert.NotNull(png);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png![..8]);
    }

    [Fact]
    public void DecodeBitmapV5ToPng_BgraToRgba_ChannelsAreSwappedCorrectly()
    {
        byte[,][] pixels = new byte[1, 1][];
        pixels[0, 0] = [10, 20, 30, 40]; // B=10 G=20 R=30 A=40

        var dib = Build32bppDib(1, 1, topDown: true, pixels);
        var png = Win32Clipboard.DecodeBitmapV5ToPng(dib)!;

        var rgba = DecodePngToRgba(png, 1, 1);

        Assert.Equal(30, rgba[0]); // R
        Assert.Equal(20, rgba[1]); // G
        Assert.Equal(10, rgba[2]); // B
        Assert.Equal(40, rgba[3]); // A
    }

    [Fact]
    public void DecodeBitmapV5ToPng_24bpp_AlphaDefaultsToOpaque()
    {
        var header = BuildBitmapV5Header(1, 1, 24, compression: 0);
        var stride = ((1 * 3 + 3) / 4) * 4; // padded to 4 bytes
        var pixelData = new byte[stride];
        pixelData[0] = 5;   // B
        pixelData[1] = 6;   // G
        pixelData[2] = 7;   // R
        var dib = header.Concat(pixelData).ToArray();

        var png = Win32Clipboard.DecodeBitmapV5ToPng(dib)!;
        var rgba = DecodePngToRgba(png, 1, 1);

        Assert.Equal(7, rgba[0]);
        Assert.Equal(6, rgba[1]);
        Assert.Equal(5, rgba[2]);
        Assert.Equal(255, rgba[3]); // no alpha channel in 24bpp -> fully opaque
    }

    [Fact]
    public void DecodeBitmapV5ToPng_BottomUpStorage_ProducesTopToBottomVisualOrder()
    {
        // Two rows, visually red-on-top / blue-on-bottom. Bottom-up DIB storage means blue is
        // physically written FIRST in the buffer, red second — the decoder must un-flip this.
        byte[,][] pixels = new byte[2, 1][];
        pixels[0, 0] = [0, 0, 255, 255]; // visual row 0 (top): BGRA red
        pixels[1, 0] = [255, 0, 0, 255]; // visual row 1 (bottom): BGRA blue

        var dib = Build32bppDib(1, 2, topDown: false, pixels);
        var png = Win32Clipboard.DecodeBitmapV5ToPng(dib)!;
        var rgba = DecodePngToRgba(png, 1, 2);

        // Row 0 (first in PNG's required top-to-bottom order) must be the visually-top red pixel.
        Assert.Equal((255, 0, 0), (rgba[0], rgba[1], rgba[2]));
        // Row 1 must be the visually-bottom blue pixel.
        Assert.Equal((0, 0, 255), (rgba[4], rgba[5], rgba[6]));
    }

    /// <summary>Minimal PNG reader for test verification only — decompresses the single IDAT chunk and strips per-row filter-type-0 bytes. Assumes filter type 0 throughout, matching PngEncoder's own output exactly.</summary>
    private static byte[] DecodePngToRgba(byte[] png, int width, int height)
    {
        var offset = 8;
        byte[]? idatData = null;
        while (offset < png.Length)
        {
            var length = (int)((png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3]);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
                idatData = png.AsSpan(offset + 8, length).ToArray();
            offset += 4 + 4 + length + 4;
        }

        Assert.NotNull(idatData);

        using var compressedStream = new MemoryStream(idatData!);
        using var zlib = new System.IO.Compression.ZLibStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        var raw = decompressed.ToArray();

        var stride = width * 4;
        var rgba = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Array.Copy(raw, row * (1 + stride) + 1, rgba, row * stride, stride);

        return rgba;
    }
}
