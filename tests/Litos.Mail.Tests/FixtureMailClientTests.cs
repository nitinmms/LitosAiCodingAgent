using System.Net;
using System.Text;
using Litos.Mail;

namespace Litos.Mail.Tests;

/// <summary>
/// The fixture inbox (Implement_Buzzies.md, Phase 4: "a fixture inbox, clearly labelled as sample
/// data"). These pin that it behaves as a real <see cref="IMailClient"/> — so the same broker and
/// the same approvals run over it — and that it never claims to be a real mailbox.
/// </summary>
public class FixtureMailClientTests
{
    private readonly FixtureMailClient _client = new();

    [Fact]
    public void Describe_SaysItIsSampleDataEverywhere()
    {
        Assert.Contains("sample", _client.Describe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a real mailbox", _client.Describe);
    }

    [Fact]
    public void TheSampleInboxCoversWhatTheConnectorMustDemonstrate()
    {
        var messages = FixtureMailClient.Messages;

        // An invoice with an attachment, a message that tries to act as an instruction, and an
        // ordinary question — the three things a demo needs to show.
        Assert.Contains(messages, m => m.Attachments is { Count: > 0 } && m.Subject.Contains("Invoice"));
        Assert.Contains(messages, m => m.Body.Contains("ASSISTANT INSTRUCTION", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Attachments is { Count: 0 } or null);
        Assert.All(messages, m => Assert.StartsWith(FixtureMailClient.FixtureIdPrefix, m.Id));
    }

    [Fact]
    public async Task Search_MatchesOnSubjectSenderAndBody()
    {
        var bySubject = await _client.SearchAsync(new MailSearch("invoice"), default);
        var bySender = await _client.SearchAsync(new MailSearch("Priya"), default);
        var byBody = await _client.SearchAsync(new MailSearch("lunch"), default);

        Assert.NotEmpty(bySubject);
        Assert.NotEmpty(bySender);
        Assert.Single(byBody);
    }

    [Fact]
    public async Task Search_RespectsTheCap()
    {
        var found = await _client.SearchAsync(new MailSearch("a", MaxResults: 1), default);

        Assert.Single(found);
    }

    [Fact]
    public async Task Get_ReturnsTheMessageWithItsIncidentals()
    {
        var message = await _client.GetAsync(FixtureMailClient.FixtureIdPrefix + "1", default);

        Assert.NotNull(message);
        Assert.Equal("Invoice 2026-114 for March", message!.Subject);
        Assert.NotNull(message.InternetMessageId);
        Assert.Single(message.Attachments!);
    }

    [Fact]
    public async Task GetAttachment_ReturnsBytesThatSayTheyAreSampleData()
    {
        var message = await _client.GetAsync(FixtureMailClient.FixtureIdPrefix + "1", default);
        var attachment = message!.Attachments![0];

        var bytes = await _client.GetAttachmentAsync(message.Id, attachment.Id, default);

        Assert.NotNull(bytes);
        Assert.Contains("Sample data", Encoding.UTF8.GetString(bytes!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAttachment_UnknownAttachment_IsNull()
    {
        Assert.Null(await _client.GetAttachmentAsync(FixtureMailClient.FixtureIdPrefix + "1", "nope", default));
    }

    [Fact]
    public async Task CreateDraft_ReturnsAnIdAndNeverSends()
    {
        var id = await _client.CreateDraftAsync(
            new MailDraft(FixtureMailClient.FixtureIdPrefix + "1", ["a@b.example"], "Re: x", "body"), default);

        Assert.StartsWith(FixtureMailClient.FixtureIdPrefix, id);
    }
}

/// <summary>
/// The OAuth pieces that can be checked without a Google account: PKCE's shape (RFC 7636), the error
/// sentence a refused sign-in produces, and the fact that the authorize URL only ever asks for the
/// narrow scopes.
/// </summary>
public class MailOAuthTests
{
    [Fact]
    public void PkceVerifier_Is43To128CharactersOfUrlSafeBase64()
    {
        var verifier = Pkce.Verifier();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.All(verifier, c => Assert.True(char.IsLetterOrDigit(c) || c is '-' or '_', $"'{c}' is not URL-safe"));
    }

    [Fact]
    public void PkceChallenge_IsTheS256Hash_Base64Url_WithoutPadding()
    {
        // RFC 7636's own worked example (Appendix B).
        var challenge = Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void PkceVerifier_TwoCallsDiffer()
    {
        Assert.NotEqual(Pkce.Verifier(), Pkce.Verifier());
    }

    [Fact]
    public void ErrorSentence_PassesGooglesOwnWordingThrough()
    {
        var sentence = MailOAuth.ErrorSentence(
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""",
            HttpStatusCode.BadRequest);

        Assert.Contains("invalid_grant", sentence);
        Assert.Contains("expired or revoked", sentence);
    }

    [Fact]
    public async Task Refresh_ExpiredOrRevoked_SaysGoogleSignedOutAndToReconnect()
    {
        // Testing-mode Google clients expire refresh tokens after 7 days; the user must read that as
        // routine, with the fix, rather than as Google's bare "invalid_grant".
        var handler = new ScriptedHandler((_, _) => Responses.Json(
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""",
            HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<MailAccessException>(() => MailOAuth.RefreshAsync(
            new MailOAuthClient("id", "secret"), "old-refresh", new HttpClient(handler), default));

        Assert.Equal(MailOAuth.SignedOutSentence, ex.Message);
        Assert.Contains("Reconnect", ex.Message);
    }

    [Fact]
    public async Task Refresh_OtherFailures_KeepGooglesOwnSentence()
    {
        var handler = new ScriptedHandler((_, _) => Responses.Json(
            """{"error":"invalid_client","error_description":"The OAuth client was not found."}""",
            HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<MailAccessException>(() => MailOAuth.RefreshAsync(
            new MailOAuthClient("id", "secret"), "old-refresh", new HttpClient(handler), default));

        Assert.Contains("invalid_client", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void BuiltInClient_IsTheLitosDesktopClientId()
    {
        Assert.EndsWith(".apps.googleusercontent.com", BuiltInGmailClient.ClientId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuiltInClient_WithoutASecretAtBuildTime_IsAbsent(string? secret)
    {
        // A build from the public repo has no secret: no built-in client, rather than one that
        // fails at Google's token endpoint.
        Assert.Null(BuiltInGmailClient.Create(secret));
    }

    [Fact]
    public void BuiltInClient_WithASecret_UsesTheBuiltInIdAndTheTrimmedSecret()
    {
        var client = BuiltInGmailClient.Create("  s3cret \n");

        Assert.Equal(BuiltInGmailClient.ClientId, client!.ClientId);
        Assert.Equal("s3cret", client.ClientSecret);
    }

    [Fact]
    public void EffectiveApp_PrefersTheSavedClient_OverTheBuiltInOne()
    {
        var saved = new MailOAuthClient("developer-client", "developer-secret");

        Assert.Same(saved, new MailSettings(App: saved).EffectiveApp);
    }

    [Fact]
    public void ErrorSentence_HandlesAJsonBodyThatIsNotJson()
    {
        var sentence = MailOAuth.ErrorSentence("<html>nope</html>", HttpStatusCode.BadGateway);

        Assert.Contains("502", sentence);
    }

    [Fact]
    public async Task Begin_AsksForReadOnlyFirst_AndComposeOnlyWhenWanted()
    {
        // The listener is a real socket, so the authorization is cancelled rather than completed;
        // what is asserted is the URL it would have opened. Each completion is awaited, so no
        // listener outlives the test.
        var app = new MailOAuthClient("client-id", "client-secret");
        using var http = new HttpClient();

        using (var cancel = new CancellationTokenSource())
        {
            var readOnly = await MailOAuth.BeginAsync(app, [], http, cancel.Token);
            Assert.Contains(Uri.EscapeDataString(MailOAuth.ReadOnlyScope), readOnly.Url);
            Assert.DoesNotContain("gmail.compose", readOnly.Url);
            Assert.Contains("code_challenge_method=S256", readOnly.Url);
            Assert.Contains("access_type=offline", readOnly.Url);
            Assert.Contains("response_type=code", readOnly.Url);
            Assert.Contains("code_challenge=", readOnly.Url);
            Assert.Contains("state=", readOnly.Url);
            Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A", readOnly.Url);

            await cancel.CancelAsync();
            await Assert.ThrowsAsync<MailAccessException>(() => readOnly.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        using (var cancel = new CancellationTokenSource())
        {
            var withCompose = await MailOAuth.BeginAsync(app, [MailOAuth.ComposeScope], http, cancel.Token);
            Assert.Contains(Uri.EscapeDataString(MailOAuth.ComposeScope), withCompose.Url);
            Assert.Contains(Uri.EscapeDataString(MailOAuth.ReadOnlyScope), withCompose.Url);

            await cancel.CancelAsync();
            await Assert.ThrowsAsync<MailAccessException>(() => withCompose.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void TheScopesNeverIncludeSend()
    {
        Assert.DoesNotContain("gmail.send", MailOAuth.ReadOnlyScope);
        Assert.DoesNotContain("gmail.send", MailOAuth.ComposeScope);
        Assert.DoesNotContain("mail.google.com", MailOAuth.ReadOnlyScope);
    }

    [Fact]
    public void Tokens_AreTreatedAsExpiredSlightlyEarly()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var tokens = new MailTokens("a", "r", now.AddMinutes(1), []);

        Assert.True(tokens.IsExpired(now));
        Assert.False((tokens with { ExpiresAt = now.AddMinutes(30) }).IsExpired(now));
    }
}
