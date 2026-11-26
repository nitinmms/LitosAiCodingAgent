using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Litos.Mail;

/// <summary>
/// Gmail through its own REST API (Implement_Buzzies.md, Phase 4), on the narrow scopes
/// <see cref="MailOAuth"/> asks for. Read, search, attachment fetch and draft creation only — this
/// class has no code path that sends, deletes, archives or marks a message, which is the whole point
/// of the broker sitting on top of it (see <see cref="IMailClient"/>): the capability isn't gated, it
/// doesn't exist.
///
/// Message bodies are decoded here from Gmail's payload tree rather than by a general MIME parser, so
/// there is no ambiguity about what "the body" is: the first <c>text/plain</c> part, or the first
/// <c>text/html</c> one with its tags taken out. Attachments are named from the tree and fetched
/// separately, behind the approval the tool asks for.
/// </summary>
public sealed class GmailClient(HttpClient http, Func<CancellationToken, Task<string?>> accessToken)
    : IMailClient
{
    private const string Base = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const string Plain = "text/plain";
    private const string Html = "text/html";

    public string Describe => "Gmail";

    public async Task<IReadOnlyList<MailMessage>> SearchAsync(MailSearch search, CancellationToken ct)
    {
        var max = Math.Clamp(search.MaxResults, 1, 50);
        var url = $"{Base}/messages?q={Uri.EscapeDataString(search.Query)}&maxResults={max}";
        using var document = await GetJsonAsync(url, ct);
        if (!document.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return [];

        var ids = messages.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();

        // Listing returns ids alone, so the summaries come from one fetch each. Bounded by max, and
        // the Gmail API's batch endpoint is deprecated, so this stays sequential but small.
        var found = new List<MailMessage>();
        foreach (var id in ids)
            if (await GetAsync(id, ct) is { } message)
                found.Add(message);
        return found;
    }

    public async Task<MailMessage?> GetAsync(string id, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync($"{Base}/messages/{Uri.EscapeDataString(id)}?format=full", HttpMethod.Get, ct);
        }
        catch (MailAccessException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            return ParseMessage(document.RootElement);
        }
    }

    public async Task<byte[]?> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(
                $"{Base}/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
                HttpMethod.Get, ct);
        }
        catch (MailAccessException)
        {
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            var data = document.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
            return data is null ? null : Base64Url.Decode(data);
        }
    }

    public async Task<string> CreateDraftAsync(MailDraft draft, CancellationToken ct)
    {
        var raw = RawMessage.Build(draft);
        // "threadId" as well as the In-Reply-To header: Gmail groups by its own thread id, so without
        // this a reply lands as a new conversation even though every header is right.
        var message = new Dictionary<string, string> { ["raw"] = raw };
        if (draft.ReplyToMessageId is { Length: > 0 } thread)
            message["threadId"] = thread;
        var payload = JsonSerializer.Serialize(new { message });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await SendAsync($"{Base}/drafts", HttpMethod.Post, ct, content);
        await EnsureSuccessAsync(response, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, HttpMethod.Get, ct);
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, HttpMethod method, CancellationToken ct, HttpContent? content = null)
    {
        var token = await accessToken(ct)
            ?? throw new MailAccessException("Gmail isn't connected. Connect it in Litos' Gmail settings, then try again.");

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MailAccessException($"Couldn't reach Gmail: {ex.Message}", ex);
        }
    }

    /// <summary>A 401 means the stored token was revoked or expired despite the refresh attempt, so
    /// the user has to reconnect — say that, rather than relaying a bare status code.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new MailAccessException(
                "Gmail refused the connection: the permission has expired or was revoked. Connect Gmail again in Litos' settings.");

        var detail = GoogleError(body) ?? response.ReasonPhrase ?? "no detail";
        throw new MailAccessException($"Gmail returned {(int)response.StatusCode} {detail}.");
    }

    private static string? GoogleError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Reading Gmail's payload tree ----------------------------------------------------------

    private static MailMessage ParseMessage(JsonElement root)
    {
        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        var headers = Headers(payload);

        var attachments = new List<MailAttachment>();
        var body = payload.ValueKind == JsonValueKind.Object
            ? BodyText(payload, attachments).Text
            : "";

        var received = root.TryGetProperty("internalDate", out var internalDate)
            && long.TryParse(internalDate.GetString(), out var millis)
                ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
                : DateTimeOffset.UtcNow;

        return new MailMessage(
            root.GetProperty("id").GetString() ?? "",
            headers.GetValueOrDefault("From", ""),
            SplitAddresses(headers.GetValueOrDefault("To", "")),
            headers.GetValueOrDefault("Subject", "(no subject)"),
            received,
            body,
            attachments,
            headers.GetValueOrDefault("Message-ID"));
    }

    private static Dictionary<string, string> Headers(JsonElement payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var header in headers.EnumerateArray())
        {
            var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = header.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (name is null || value is null || result.ContainsKey(name))
                continue;
            result[name] = value;
        }
        return result;
    }

    /// <summary>The message's readable text, preferring a plain part over an HTML one, while
    /// collecting every named attachment met on the way down. The mime type comes back with the text
    /// because a nested <c>multipart/alternative</c> part is not itself plain or HTML — the type that
    /// matters is the one of the part that actually carried the text.</summary>
    private static (string MimeType, string Text) BodyText(JsonElement part, List<MailAttachment> attachments)
    {
        var mimeType = Field(part, "mimeType") ?? "";
        var fileName = Field(part, "filename");
        var hasParts = part.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array;

        if (!string.IsNullOrEmpty(fileName) && part.TryGetProperty("body", out var attachmentBody)
            && attachmentBody.TryGetProperty("attachmentId", out var attachmentId))
        {
            attachments.Add(new MailAttachment(
                attachmentId.GetString() ?? "",
                fileName,
                mimeType,
                attachmentBody.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            return ("", "");
        }

        if (hasParts)
        {
            (string MimeType, string Text)? plain = null;
            (string MimeType, string Text)? html = null;
            foreach (var child in parts.EnumerateArray())
            {
                var found = BodyText(child, attachments);
                if (found.Text.Length == 0)
                    continue;
                if (plain is null && found.MimeType == Plain)
                    plain = found;
                else if (html is null && found.MimeType == Html)
                    html = found;
            }

            if (plain is { } p)
                return p;
            if (html is { } h)
                return h;
            return ("", "");
        }

        if (mimeType is Plain or Html && part.TryGetProperty("body", out var own)
            && own.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
        {
            var text = Encoding.UTF8.GetString(Base64Url.Decode(encoded));
            return (mimeType, mimeType == Html ? HtmlText.ToText(text) : text);
        }

        return ("", "");
    }

    private static string? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> SplitAddresses(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0)];
}

/// <summary>Gmail and Google's token endpoint both use base64url without padding.</summary>
internal static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The RFC 822 message a draft is built from: the headers that thread it into the conversation it
/// answers, and the body base64-encoded so non-ASCII text survives intact. Deliberately simple — a
/// single plain-text part — because a draft is something the user will read and edit in their own
/// mail client, not a newsletter.
/// </summary>
internal static class RawMessage
{
    public static string Build(MailDraft draft)
    {
        var headers = new StringBuilder()
            .Append("To: ").Append(string.Join(", ", draft.To)).Append("\r\n")
            .Append("Subject: ").Append(draft.Subject).Append("\r\n");

        if (draft.InReplyToHeader is { Length: > 0 } inReplyTo)
        {
            headers.Append("In-Reply-To: ").Append(inReplyTo).Append("\r\n")
                .Append("References: ").Append(inReplyTo).Append("\r\n");
        }

        headers.Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=\"UTF-8\"\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n");

        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(draft.Body));
        for (var i = 0; i < body.Length; i += 76)
            headers.Append(body, i, Math.Min(76, body.Length - i)).Append("\r\n");

        return Base64Url.Encode(Encoding.UTF8.GetBytes(headers.ToString()));
    }
}

/// <summary>
/// Enough HTML-to-text for a mail body: drops script/style blocks wholesale, turns block tags into
/// line breaks, removes every other tag and decodes the handful of entities that appear in mail.
/// Not a browser and not trying to be.
/// </summary>
internal static class HtmlText
{
    public static string ToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(
            text, "(?i)<br\\s*/?>|</p>|</div>|</tr>|</li>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "[ \\t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\n{3,}", "\n\n");
        return text.Trim();
    }
}
