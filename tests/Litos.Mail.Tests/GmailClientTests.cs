using System.Net;
using System.Text;
using System.Text.Json;
using Litos.Mail;

namespace Litos.Mail.Tests;

/// <summary>Answers requests from a script, so the Gmail client can be exercised without a mailbox.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Method, string Url, string? Body, string? Authorization)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body,
            request.Headers.Authorization?.Parameter));
        return respond(request, request.RequestUri!.ToString());
    }
}

internal static class Responses
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

public class GmailClientTests
{
    private static GmailClient Client(ScriptedHandler handler, string? token = "access-token") =>
        new(new HttpClient(handler), _ => Task.FromResult(token));

    /// <summary>A message in Gmail's own wire shape: headers, a nested payload, and an attachment.</summary>
    private const string MessageJson = """
        {
          "id": "18f0a",
          "threadId": "18f0a",
          "internalDate": "1772446800000",
          "payload": {
            "mimeType": "multipart/mixed",
            "headers": [
              {"name": "From", "value": "Priya <priya@example.com>"},
              {"name": "To", "value": "you@example.com, cc@example.com"},
              {"name": "Subject", "value": "Invoice 2026-114"},
              {"name": "Message-ID", "value": "<abc@example.com>"}
            ],
            "parts": [
              {
                "mimeType": "multipart/alternative",
                "parts": [
                  {"mimeType": "text/plain", "body": {"data": "UGxlYXNlIGZpbmQgdGhlIGludm9pY2UgYXR0YWNoZWQu"}}
                ]
              },
              {
                "partId": "1",
                "mimeType": "application/pdf",
                "filename": "invoice.pdf",
                "body": {"attachmentId": "ANGjdJ8", "size": 48210}
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task Attachment_IdIsStableAcrossReads_EvenThoughGmailsAttachmentIdIsNot()
    {
        // Gmail hands back a fresh attachmentId on every messages.get. The id the model sees must not
        // move, and the fetch must use the newest attachmentId.
        var reads = 0;
        var handler = new ScriptedHandler((_, _) =>
            Responses.Json(MessageJson.Replace("ANGjdJ8", $"ANGjdJ8-read{++reads}")));
        var client = Client(handler);

        var first = (await client.GetAsync("18f0a", default))!.Attachments![0];
        var second = (await client.GetAsync("18f0a", default))!.Attachments![0];

        Assert.Equal("part-1", first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.FetchHandle, second.FetchHandle);
        Assert.Equal("ANGjdJ8-read2", second.FetchHandle);
    }

    [Fact]
    public async Task Get_ReadsHeadersBodyAndAttachments()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json(MessageJson));

        var message = await Client(handler).GetAsync("18f0a", default);

        Assert.NotNull(message);
        Assert.Equal("18f0a", message!.Id);
        Assert.Equal("Priya <priya@example.com>", message.From);
        Assert.Equal(["you@example.com", "cc@example.com"], message.To);
        Assert.Equal("Invoice 2026-114", message.Subject);
        Assert.Equal("<abc@example.com>", message.InternetMessageId);
        Assert.Equal("Please find the invoice attached.", message.Body);
        var attachment = Assert.Single(message.Attachments!);
        Assert.Equal("part-1", attachment.Id);
        Assert.Equal("ANGjdJ8", attachment.FetchHandle);
        Assert.Equal("invoice.pdf", attachment.FileName);
        Assert.Equal(48210, attachment.SizeBytes);
    }

    [Fact]
    public async Task Get_SendsTheBearerToken_AndAsksForTheFullFormat()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json(MessageJson));

        await Client(handler).GetAsync("18f0a", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("access-token", request.Authorization);
        Assert.Contains("format=full", request.Url);
        Assert.Contains("/users/me/messages/18f0a", request.Url);
    }

    [Fact]
    public async Task Get_AMissingMessage_IsNullRatherThanAnError()
    {
        var handler = new ScriptedHandler((_, _) => Responses.NotFound());

        Assert.Null(await Client(handler).GetAsync("gone", default));
    }

    [Fact]
    public async Task Get_WithoutAToken_SaysTheMailboxIsNotConnected()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json(MessageJson));

        var ex = await Assert.ThrowsAsync<MailAccessException>(() => Client(handler, token: null).GetAsync("18f0a", default));

        Assert.Contains("isn't connected", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_ListsIdsThenFetchesEachMessage()
    {
        var handler = new ScriptedHandler((_, url) => url.Contains("/messages?")
            ? Responses.Json("""{"messages":[{"id":"a"},{"id":"b"}]}""")
            : Responses.Json(MessageJson));

        var found = await Client(handler).SearchAsync(new MailSearch("from:priya", 5), default);

        Assert.Equal(2, found.Count);
        Assert.Contains("q=from%3Apriya", handler.Requests[0].Url);
        Assert.Contains("maxResults=5", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Search_NoResults_IsAnEmptyList()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"resultSizeEstimate":0}"""));

        Assert.Empty(await Client(handler).SearchAsync(new MailSearch("nothing"), default));
    }

    [Fact]
    public async Task Search_CapsThePageSizeWhateverTheModelAsked()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"messages":[]}"""));

        await Client(handler).SearchAsync(new MailSearch("x", MaxResults: 500), default);

        Assert.Contains("maxResults=50", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Attachment_DecodesBase64UrlBytes()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"data":"aGVsbG8"}"""));

        var bytes = await Client(handler).GetAttachmentAsync("18f0a", "ANGjdJ8", default);

        Assert.NotNull(bytes);
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes!));
        Assert.Contains("/attachments/ANGjdJ8", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Draft_PostsRawMimeThatThreadsIntoTheConversation()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"id":"draft-9"}"""));

        var id = await Client(handler).CreateDraftAsync(
            new MailDraft("18f0a", ["priya@example.com"], "Re: Invoice 2026-114", "Paying Friday.", "<abc@example.com>"),
            default);

        Assert.Equal("draft-9", id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Contains("/drafts", request.Url);

        // The base64url "raw" field decodes to a real message with the threading headers.
        using var document = JsonDocument.Parse(request.Body!);
        var raw = Base64Url.Decode(document.RootElement.GetProperty("message").GetProperty("raw").GetString()!);
        var mime = Encoding.UTF8.GetString(raw);
        Assert.Contains("To: priya@example.com", mime);
        Assert.Contains("Subject: Re: Invoice 2026-114", mime);
        Assert.Contains("In-Reply-To: <abc@example.com>", mime);
        Assert.Contains("References: <abc@example.com>", mime);
        Assert.Contains("Content-Transfer-Encoding: base64", mime);
    }

    [Fact]
    public async Task Draft_WithoutAnOriginal_StillCarriesRecipientsAndSubject()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"id":"d"}"""));

        await Client(handler).CreateDraftAsync(new MailDraft(null, ["a@b.example"], "Hello", "Body"), default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var mime = Encoding.UTF8.GetString(Base64Url.Decode(
            document.RootElement.GetProperty("message").GetProperty("raw").GetString()!));
        Assert.Contains("To: a@b.example", mime);
        Assert.DoesNotContain("In-Reply-To", mime);
    }

    [Fact]
    public async Task Draft_NonAsciiBody_SurvivesTheBase64BodyEncoding()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"id":"d"}"""));

        await Client(handler).CreateDraftAsync(
            new MailDraft(null, ["a@b.example"], "भुगतान", "₹42,180.00 — thanks!"), default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var mime = Encoding.UTF8.GetString(Base64Url.Decode(
            document.RootElement.GetProperty("message").GetProperty("raw").GetString()!));
        var bodyB64 = mime[(mime.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].Replace("\r\n", "");
        Assert.Contains("₹42,180.00", Encoding.UTF8.GetString(Convert.FromBase64String(bodyB64)));
    }

    [Fact]
    public async Task Draft_ThreadsIntoTheRightGmailThread_NotJustTheHeaders()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"id":"d"}"""));

        await Client(handler).CreateDraftAsync(
            new MailDraft("18f0a", ["a@b.example"], "Re: x", "Body", "<abc@example.com>"), default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var message = document.RootElement.GetProperty("message");
        Assert.Equal("18f0a", message.GetProperty("threadId").GetString());
        Assert.True(message.TryGetProperty("raw", out _));
    }

    [Fact]
    public async Task Draft_WithoutAnOriginal_SendsNoThreadId()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"id":"d"}"""));

        await Client(handler).CreateDraftAsync(new MailDraft(null, ["a@b.example"], "Hello", "Body"), default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(document.RootElement.GetProperty("message").TryGetProperty("threadId", out _));
    }

    [Fact]
    public async Task AnExpiredPermission_SaysToConnectAgainRatherThanRelaying401()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json("""{"error":{"message":"Invalid credentials"}}""",
            HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<MailAccessException>(() => Client(handler).GetAsync("18f0a", default));

        Assert.Contains("Connect Gmail again", ex.Message);
    }

    [Fact]
    public async Task ABadRequest_ShowsGooglesOwnMessage()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json(
            """{"error":{"message":"Requested entity was not found."}}""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<MailAccessException>(() => Client(handler).GetAsync("18f0a", default));

        Assert.Contains("Requested entity was not found", ex.Message);
    }

    [Fact]
    public async Task HtmlOnlyBodies_AreTurnedIntoReadableText()
    {
        const string htmlBody = "PGI+SGVsbG8gPC9iPjxicj5Xb3JsZDxici8+PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==";
        var handler = new ScriptedHandler((_, _) => Responses.Json($$"""
            {
              "id": "x",
              "payload": {
                "mimeType": "text/html",
                "headers": [{"name": "Subject", "value": "Hi"}],
                "body": {"data": "{{htmlBody}}"}
              }
            }
            """));

        var message = await Client(handler).GetAsync("x", default);

        Assert.Contains("Hello", message!.Body);
        Assert.Contains("World", message.Body);
        Assert.DoesNotContain("<b>", message.Body);
        Assert.DoesNotContain("alert(1)", message.Body);
    }
}

public class HtmlTextTests
{
    [Fact]
    public void DropsScriptAndStyleBlocksWholesale()
    {
        var text = HtmlText.ToText("<style>body{color:red}</style><p>Real text</p><script>evil()</script>");

        Assert.Contains("Real text", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("evil", text);
    }

    [Fact]
    public void BlockTagsBecomeNewlines()
    {
        var text = HtmlText.ToText("<p>One</p><p>Two</p>");

        Assert.Contains("One", text);
        Assert.Contains("Two", text);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void EntitiesAreDecoded()
    {
        var text = HtmlText.ToText("a &amp; b &lt;c&gt; &nbsp;d");

        Assert.Contains("a & b <c>", text);
    }
}
