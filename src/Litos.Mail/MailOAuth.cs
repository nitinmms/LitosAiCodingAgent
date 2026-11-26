using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Litos.Mail;

/// <summary>
/// Google OAuth for the Gmail connector (Implement_Buzzies.md, Phase 4): the installed-app flow with
/// PKCE, a loopback redirect and the narrowest scopes that do the job — <c>gmail.readonly</c> first,
/// and <c>gmail.compose</c> only when the user wants drafted replies. Nothing here ever sends mail:
/// <c>gmail.compose</c> creates drafts, and <c>gmail.send</c> is deliberately never requested.
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

    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);

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
                "include_granted_scopes=true",
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

        var tokens = await PostAsync(form, http, ct);
        // Google omits the refresh token on a refresh: keep the one we have.
        return tokens.RefreshToken is null ? tokens with { RefreshToken = refreshToken } : tokens;
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
                throw new MailAccessException(ErrorSentence(text, response.StatusCode));

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
/// The one-shot loopback redirect. It accepts a single connection, reads the request line, answers
/// with a small page telling the user to go back to Litos, and hands the <c>code</c> back. The
/// <c>state</c> is checked before the code is trusted, so a stray request from another process on
/// this machine cannot feed us someone else's authorization code.
/// </summary>
internal static class LoopbackRedirect
{
    /// <summary>Binds 127.0.0.1 on an OS-assigned port and returns the listener with Google's
    /// redirect URI for it.</summary>
    public static (TcpListener Listener, string RedirectUri) Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 4);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, $"http://127.0.0.1:{port}/oauth2/callback");
    }

    /// <summary>Waits for the consent page to come back and extracts the code. A refusal
    /// (<c>error=access_denied</c>) is reported as what happened rather than as a timeout.</summary>
    public static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        using var client = await AcceptAsync(listener, ct);
        var request = await ReadRequestLineAsync(client, ct);
        var query = QueryOf(request);

        await WritePageAsync(client, query.ContainsKey("code"), ct);

        if (query.TryGetValue("error", out var error))
            throw new MailAccessException(error == "access_denied"
                ? "The Google sign-in was cancelled, so Gmail is not connected."
                : $"Google reported \"{error}\" during the sign-in, so Gmail is not connected.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new MailAccessException("The Google sign-in came back without a code, so Gmail is not connected.");
        if (!query.TryGetValue("state", out var state) || state != expectedState)
            throw new MailAccessException("The Google sign-in came back with an unexpected state, so it was ignored as a possible forgery.");

        return code;
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw new MailAccessException(
                ct.IsCancellationRequested
                    ? "The Google sign-in was cancelled, so Gmail is not connected."
                    : "The Google sign-in was not completed in time. Try connecting Gmail again.");
        }
    }

    private static async Task<string> ReadRequestLineAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, ct);
        var request = Encoding.ASCII.GetString(buffer, 0, read);
        // "GET /oauth2/callback?code=...&state=... HTTP/1.1"
        var line = request.Split("\r\n", StringSplitOptions.None)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    private static Dictionary<string, string> QueryOf(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = path.IndexOf('?');
        if (start < 0)
            return result;

        foreach (var pair in path[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static async Task WritePageAsync(TcpClient client, bool ok, CancellationToken ct)
    {
        var body = ok
            ? "<html><body style=\"font-family:sans-serif\"><h2>Litos is connected to Gmail.</h2><p>You can close this tab and go back to Litos.</p></body></html>"
            : "<html><body style=\"font-family:sans-serif\"><h2>Gmail sign-in didn't finish.</h2><p>Go back to Litos to try again.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}
