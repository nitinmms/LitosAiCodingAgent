using QRCoder;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Wraps QRCoder's byte-array PNG generator — deliberately PngByteQRCode, not the
/// System.Drawing-backed QRCode class, since this runs inside a Linux container
/// (HeadlessServiceTool.md §5.1) where System.Drawing.Common isn't a safe dependency.
/// </summary>
public static class QrCodeGenerator
{
    public static byte[] GeneratePng(string content, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
