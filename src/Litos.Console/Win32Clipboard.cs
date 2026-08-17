using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Litos.Console;

/// <summary>
/// Windows clipboard image read via direct Win32 P/Invoke — OpenClipboard/GetClipboardData(
/// CF_DIBV5)/CloseClipboard — bypassing Terminal.Gui's string-only IClipboard entirely (see
/// ClipboardImageReader's own remarks). Reading (unlike writing) needs no HWND and no STA thread:
/// MSDN documents OpenClipboard(NULL) as valid for a read-only caller, and GetClipboardData's
/// returned handle is a plain global memory block, not a window-message round-trip the way
/// SetClipboardData's ownership/render-on-demand contract requires. This is why no WinForms/
/// System.Drawing dependency is needed here either.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32Clipboard
{
    private const uint CF_DIBV5 = 17;

    /// <summary>Returns PNG bytes decoded from whatever bitmap is on the clipboard, or null if there is none (never throws).</summary>
    public static byte[]? TryReadDibAsPng()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CF_DIBV5);
            if (handle == IntPtr.Zero)
                return null;

            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
                return null;

            try
            {
                var size = (int)GlobalSize(handle);
                if (size <= 0)
                    return null;

                var buffer = new byte[size];
                Marshal.Copy(ptr, buffer, 0, size);
                return DecodeBitmapV5ToPng(buffer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// BITMAPV5HEADER (Win32 CF_DIBV5 layout) is a 124-byte header — width/height (Int32, LE),
    /// bit count (UInt16), compression, then the pixel array. Only the common cases a screenshot/
    /// paint-style copy actually produces are handled: 32bpp BI_RGB/BI_BITFIELDS (already
    /// effectively BGRA — the vast majority of what Windows puts on the clipboard for "copy
    /// image") and 24bpp BI_RGB. Anything else (paletted, RLE-compressed, 16bpp) returns null
    /// rather than guessing at a wrong decode — falls through to text paste same as "no image."
    /// Internal (not private) so it's directly unit-testable against hand-built DIB byte buffers
    /// without a real Windows clipboard.
    /// </summary>
    internal static byte[]? DecodeBitmapV5ToPng(byte[] dib)
    {
        const int HeaderSize = 124;
        if (dib.Length < HeaderSize)
            return null;

        var headerSizeField = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4));
        if (headerSizeField != HeaderSize)
            return null; // Not a BITMAPV5HEADER (e.g. an older BITMAPINFOHEADER-shaped CF_DIB) — unsupported.

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4));
        var heightRaw = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4));
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16, 4));

        // A positive height means the DIB is stored bottom-up (the overwhelmingly common case);
        // negative means top-down. Either way width must be positive and non-degenerate.
        if (width <= 0 || heightRaw == 0)
            return null;
        var height = Math.Abs(heightRaw);
        var isTopDown = heightRaw < 0;

        const int BI_RGB = 0;
        const int BI_BITFIELDS = 3;
        if (compression != BI_RGB && compression != BI_BITFIELDS)
            return null;
        if (bitCount is not (24 or 32))
            return null;

        var pixelDataOffset = HeaderSize; // BITMAPV5HEADER has no color table for 24/32bpp BI_RGB/BI_BITFIELDS.
        var bytesPerPixel = bitCount / 8;
        var stride = ((width * bytesPerPixel + 3) / 4) * 4; // DIB rows are padded to a 4-byte boundary.

        if (pixelDataOffset + (long)stride * height > dib.Length)
            return null;

        var rgba = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            // DIB storage order (bottom-up unless isTopDown) versus PNG's required top-to-bottom
            // scanline order: sourceRow walks the DIB buffer in the order that produces
            // top-to-bottom output rows regardless of which way the DIB itself is stored.
            var sourceRow = isTopDown ? row : height - 1 - row;
            var rowStart = pixelDataOffset + sourceRow * stride;

            for (var col = 0; col < width; col++)
            {
                var srcOffset = rowStart + col * bytesPerPixel;
                var dstOffset = (row * width + col) * 4;

                // DIB pixel byte order is BGR(A); PNG needs RGBA.
                rgba[dstOffset + 0] = dib[srcOffset + 2]; // R
                rgba[dstOffset + 1] = dib[srcOffset + 1]; // G
                rgba[dstOffset + 2] = dib[srcOffset + 0]; // B
                rgba[dstOffset + 3] = bytesPerPixel == 4 ? dib[srcOffset + 3] : (byte)255;
            }
        }

        return PngEncoder.EncodeRgba(width, height, rgba);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
