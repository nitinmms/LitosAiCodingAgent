using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class QrCodeGeneratorTests
{
    [Fact]
    public void GeneratePng_ReturnsNonEmptyPngBytes()
    {
        var bytes = QrCodeGenerator.GeneratePng("https://t.me/SomeBot?start=abc123");

        Assert.NotEmpty(bytes);
        // PNG magic number.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes.Take(4));
    }

    [Fact]
    public void GeneratePng_DifferentContent_ProducesDifferentBytes()
    {
        var a = QrCodeGenerator.GeneratePng("https://t.me/SomeBot?start=aaaa");
        var b = QrCodeGenerator.GeneratePng("https://t.me/SomeBot?start=bbbb");

        Assert.NotEqual(a, b);
    }
}
