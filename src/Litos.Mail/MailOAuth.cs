using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Litos.Mail;

/// <summary>
/// Google OAuth for the Gmail connector (Implement_Buzzies.md, Phase 4): the installed-app flow with
/// PKCE, a loopback redirect and the narrowest scopes that do the job — <c>gmail.readonly</c> first,
/// and <c>gmail.compose</c> only when the user wants drafted replies. Note that Google's
/// <c>gmail.compose</c> also permits sending (its consent line is "Manage drafts and send emails"):
/// that Litos never sends is enforced by <see cref="IMailClient"/> having no send method, not by
/// the scope.
///
/// The redirect is a loopback listener on an ephemeral 127.0.0.1 port, which is what Google's
/// "Desktop app" client type expects and needs no inbound port opening to the network. Its single
/// HTTP request is answered by hand on a TcpListener rather than through HttpListener, which on
/// Windows needs an administrative URL ACL reservation for anything but a pre-registered prefix.
///
/// Only the authorization URL is handed back to the UI: the verifier, the state and the response all
/// stay inside this class, and the tokens go straight to <see cref="ICredentialStore"/>.
/// </summary>
public static class MailOAuth
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";
    public const string ComposeScope = "https://www.googleapis.com/auth/gmail.compose";

    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    // Long enough for a first-time user to read Google's unverified-app warning and the consent
    // screen; five minutes proved too short in the first live sign-in.
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Starts an authorization: opens the loopback listener, builds the consent URL, and returns it
    /// together with a task that completes when the browser comes back. Cancelling (or the timeout)
    /// gives a <see cref="MailAccessException"/> saying the user didn't finish, which is a normal
    /// outcome rather than a fault.
    /// </summary>
    public static async Task<MailAuthorization> BeginAsync(
        MailOAuthClient app, IEnumerable<string> scopes, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        var wanted = scopes.Distinct(StringComparer.Ordinal).ToList();
        if (!wanted.Contains(ReadOnlyScope))
            wanted.Insert(0, ReadOnlyScope);

        var (listener, redirectUri) = LoopbackRedirect.Start();
        var verifier = Pkce.Verifier();
        var state = Pkce.Verifier();

        var url = new UriBuilder(AuthorizeEndpoint)
        {
            Query = string.Join('&',
            [
                "client_id=" + Uri.EscapeDataString(app.ClientId),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "response_type=code",
                "scope=" + Uri.EscapeDataString(string.Join(' ', wanted)),
                // offline + consent: without both of these Google hands back no refresh token on a
                // repeat authorization, and a connector that silently stops working tomorrow is
                // worse than one that asks once.
                "access_type=offline",
                "prompt=consent",
                // No include_granted_scopes: with it Google folds every scope granted before into
                // the new token, so a user who unticked drafting and reconnected still got
                // gmail.compose back (first live test, 2026-11-27). Litos asks for exactly the set it
                // wants each time, and revokes first when that set shrinks (see RevokeAsync).
                "state=" + Uri.EscapeDataString(state),
                "code_challenge=" + Pkce.Challenge(verifier),
                "code_challenge_method=S256",
            ]),
        }.Uri;

        var completion = CompleteAsync();
        return new MailAuthorization(url.ToString(), completion);

        async Task<MailTokens> CompleteAsync()
        {
            // Created here, not in BeginAsync: a linked source disposed when BeginAsync returns
            // stops observing the caller's token, so the browser wait could never be cancelled and
            // the authorization timeout would never fire. Its lifetime has to be this function's.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AuthorizationTimeout);
            try
            {
                var code = await LoopbackRedirect.WaitForCodeAsync(listener, state, timeout.Token);
                return await ExchangeAsync(app, code, verifier, redirectUri, http, timeout.Token);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>Trades the one-time code for tokens. A refusal here (an unverified app in Testing
    /// mode, a wrong client secret, a scope the user unticked) surfaces as the provider's own
    /// sentence, since the user has to act on it.</summary>
    internal static async Task<MailTokens> ExchangeAsync(
        MailOAuthClient app, string code, string verifier, string redirectUri, HttpClient http, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(app.ClientSecret))
            form["client_secret"] = app.ClientSecret;

        return await PostAsync(form, http, ct);
    }

    /// <summary>A fresh access token from the stored refresh token. A revoked or expired refresh
    /// token (Google lets the user revoke access from their account page) fails with a sentence
    /// naming the fix, since nothing the app does can recover from it.</summary>
    public static async Task<MailTokens> RefreshAsync(
        MailOAuthClient app, string refreshToken, HttpClient http, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = app.ClientId,
        };
        if (!string.IsNullOrEmpty(app.ClientSecret))
            form["client_secret"] = app.ClientSecret;

        MailTokens tokens;
        try
        {
            tokens = await PostAsync(form, http, ct);
        }
        catch (MailAccessException ex) when (ex.Data[OAuthErrorKey] is "invalid_grant")
        {
            // Google's own sentence here ("Token has been expired or revoked.") doesn't say what to
            // do. In Testing mode this happens every 7 days, so it has to read as routine.
            throw new MailAccessException(SignedOutSentence, ex);
        }
        // Google omits the refresh token on a refresh: keep the one we have.
        return tokens.RefreshToken is null ? tokens with { RefreshToken = refreshToken } : tokens;
    }

    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Asks Google to withdraw Litos's access (the whole grant, not just this token), so
    /// Disconnect also removes Litos from the user's Google Account and a reconnect with fewer
    /// scopes really ends up with fewer. Returns whether Google confirmed it; an already-revoked or
    /// expired token counts as done. Never throws for a network failure: the caller still forgets
    /// the token locally, and the user can remove access from their Google Account.</summary>
    public static async Task<bool> RevokeAsync(string token, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await http.PostAsync(RevokeEndpoint, content, ct);
            if (response.IsSuccessStatusCode)
                return true;
            // Google answers 400 invalid_token for a token that is already revoked or expired.
            return ErrorCode(await response.Content.ReadAsStringAsync(ct)) == "invalid_token";
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<MailTokens> PostAsync(Dictionary<string, string> form, HttpClient http, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(TokenEndpoint, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MailAccessException($"Couldn't reach Google to sign in: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var failure = new MailAccessException(ErrorSentence(text, response.StatusCode));
                failure.Data[OAuthErrorKey] = ErrorCode(text);
                throw failure;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var access = Field(root, "access_token")
                ?? throw new MailAccessException("Google's reply had no access token, so the connection can't be used.");
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
            var scopes = Field(root, "scope") is { Length: > 0 } granted
                ? granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];

            return new MailTokens(
                access,
                Field(root, "refresh_token"),
                DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                scopes);
        }
    }

    /// <summary>Where a token-endpoint failure records Google's error code (e.g. "invalid_grant").</summary>
    internal const string OAuthErrorKey = "oauth_error";

    /// <summary>What a stored sign-in that Google no longer accepts tells the user and the model.</summary>
    public const string SignedOutSentence =
        "Google has signed Litos out of Gmail. This happens every 7 days while Litos is in Google's testing stage, "
        + "or when access is removed from your Google Account. Open Gmail in the Litos sidebar and press Reconnect.";

    internal static string? ErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Field(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Google's error JSON is {"error": "...", "error_description": "..."}; the description
    /// is written for the user ("This app is blocked"), so it is passed through rather than
    /// replaced by a generic message.</summary>
    internal static string ErrorSentence(string json, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var error = Field(document.RootElement, "error") ?? status.ToString();
            var description = Field(document.RootElement, "error_description");
            return string.IsNullOrWhiteSpace(description)
                ? $"Google refused the sign-in ({error})."
                : $"Google refused the sign-in ({error}): {description}";
        }
        catch (JsonException)
        {
            return $"Google refused the sign-in ({(int)status} {status}).";
        }
    }

    private static string? Field(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The OAuth app the connector signs in as: Google's "Desktop app" client id, and the
/// secret Google issues with it (not a client secret in the usual sense — it ships in the binary —
/// so it is not treated as one).</summary>
public sealed record MailOAuthClient(string ClientId, string? ClientSecret)
{
    /// <summary>Set from the environment first, so a developer can try the connector without
    /// writing anything into a file, then from the saved connector settings.</summary>
    public static MailOAuthClient? FromEnvironment() =>
        Environment.GetEnvironmentVariable("LITOS_GMAIL_CLIENT_ID") is { Length: > 0 } id
            ? new MailOAuthClient(id, Environment.GetEnvironmentVariable("LITOS_GMAIL_CLIENT_SECRET"))
            : null;
}

/// <summary>A consent URL to open in the browser, plus the task that finishes the sign-in.</summary>
public sealed record MailAuthorization(string Url, Task<MailTokens> Completion);

internal static class Pkce
{
    /// <summary>RFC 7636 verifier: 43 characters of base64url from 32 random bytes.</summary>
    public static string Verifier() =>
        Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The loopback redirect. It keeps accepting connections until one is Google's genuine callback —
/// a GET of <see cref="CallbackPath"/> carrying the expected <c>state</c> — answers it with a small
/// page telling the user to go back to Litos, and hands the <c>code</c> back.
///
/// Anything else that reaches the port is answered and ignored, never allowed to end the sign-in:
/// browsers open speculative connections, try <c>https://</c> first, ask for <c>/favicon.ico</c>,
/// and another process on this machine can connect too. An earlier version accepted exactly one
/// connection and stopped listening if it wasn't the callback, so the real redirect then met
/// "connection refused" (first live sign-in, 2026-11-27). Each connection is handled on its own,
/// so an idle one can't hold up the real one. The <c>state</c> check also means a stray request
/// can't feed us someone else's authorization code.
/// </summary>
internal static class LoopbackRedirect
{
    public const string CallbackPath = "/oauth2/callback";

    /// <summary>Binds 127.0.0.1 on an OS-assigned port and returns the listener with Google's
    /// redirect URI for it.</summary>
    public static (TcpListener Listener, string RedirectUri) Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 16);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, $"http://127.0.0.1:{port}{CallbackPath}");
    }

    /// <summary>Waits for the consent page to come back and extracts the code. A refusal
    /// (<c>error=access_denied</c>) is reported as what happened rather than as a timeout.
    /// Returns once the callback has been answered; the caller stops the listener.</summary>
    public static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var accepting = AcceptLoopAsync(listener, expectedState, outcome, stop.Token);
        try
        {
            return await outcome.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (!outcome.Task.IsCompleted)
        {
            throw new MailAccessException(
                "The Google sign-in was cancelled or not finished in time, so Gmail is not connected. "
                + "Press Connect Gmail again and finish in the new browser tab.");
        }
        finally
        {
            stop.Cancel();
            await accepting;
        }
    }

    private static async Task AcceptLoopAsync(
        TcpListener listener, string expectedState, TaskCompletionSource<string> outcome, CancellationToken ct)
    {
        var handlers = new List<Task>();
        while (!ct.IsCancellationRequested && !outcome.Task.IsCompleted)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            handlers.Add(HandleAsync(client, expectedState, outcome, ct));
        }
        await Task.WhenAll(handlers);
    }

    /// <summary>One connection. Never throws: whatever a stray connection does, the sign-in keeps
    /// waiting for the real callback.</summary>
    private static async Task HandleAsync(
        TcpClient client, string expectedState, TaskCompletionSource<string> outcome, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, ct);
                if (target is null)
                    return; // not HTTP at all (a TLS probe, an empty pre-connection): just close it

                var queryStart = target.IndexOf('?');
                var path = queryStart < 0 ? target : target[..queryStart];
                if (path != CallbackPath)
                {
                    await WriteResponseAsync(stream, "404 Not Found", "", ct);
                    return;
                }

                var query = QueryOf(target);
                if (!query.TryGetValue("state", out var state) || state != expectedState)
                {
                    // Not this sign-in's callback (an old tab, or a forgery): say so, keep waiting.
                    await WriteResponseAsync(stream, "400 Bad Request", Page(
                        "This sign-in link is out of date.",
                        "Go back to Litos and use the browser tab that its latest Connect Gmail opened."), ct);
                    return;
                }

                if (query.TryGetValue("error", out var error))
                {
                    await WriteResponseAsync(stream, "200 OK", Page("Gmail sign-in didn't finish.", "Go back to Litos to try again."), ct);
                    outcome.TrySetException(new MailAccessException(error == "access_denied"
                        ? "The Google sign-in was cancelled, so Gmail is not connected."
                        : $"Google reported \"{error}\" during the sign-in, so Gmail is not connected."));
                    return;
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteResponseAsync(stream, "200 OK", Page("Gmail sign-in didn't finish.", "Go back to Litos to try again."), ct);
                    outcome.TrySetException(new MailAccessException("The Google sign-in came back without a code, so Gmail is not connected."));
                    return;
                }

                await WriteResponseAsync(stream, "200 OK", Page("Litos is connected to Gmail.", "You can close this tab and go back to Litos."), ct);
                outcome.TrySetResult(code);
            }
            catch (Exception)
            {
                // A connection that breaks, stalls until the sign-in ends, or sends garbage is
                // simply dropped.
            }
        }
    }

    /// <summary>The request target of a GET ("/oauth2/callback?code=…"), or null when the
    /// connection isn't a plain-HTTP GET. Reads until the end of the request line, since TCP can
    /// deliver it in pieces.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n"u8) >= 0)
                break;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (end < 0)
            return null;
        // "GET /oauth2/callback?code=...&state=... HTTP/1.1"
        var parts = text[..end].Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static Dictionary<string, string> QueryOf(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = target.IndexOf('?');
        if (start < 0)
            return result;

        foreach (var pair in target[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            // '+' means space in a query string; it has to be decoded before %2B becomes a real '+'.
            result[Uri.UnescapeDataString(pair[..eq].Replace('+', ' '))] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static string Page(string heading, string text) =>
        $"<html><body style=\"font-family:sans-serif\"><h2>{heading}</h2><p>{text}</p></body></html>";

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}
