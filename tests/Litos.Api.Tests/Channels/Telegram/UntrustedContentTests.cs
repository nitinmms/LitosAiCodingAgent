using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class UntrustedContentTests
{
    [Fact]
    public void Wrap_ProducesExactBoundaryMarkerFormat()
    {
        var result = UntrustedContent.Wrap("telegram_attachment:abc123", "some file content");

        Assert.Equal(
            "<<<EXTERNAL_UNTRUSTED_CONTENT source=\"telegram_attachment:abc123\">>>\n" +
            "some file content\n" +
            "<<<END_EXTERNAL_UNTRUSTED_CONTENT>>>",
            result);
    }

    [Fact]
    public void Wrap_PreservesMultilineContentVerbatim()
    {
        var content = "line one\nline two\nline three";

        var result = UntrustedContent.Wrap("web_search:query", content);

        Assert.Contains(content, result);
    }
}
