using System.Reflection;

namespace Litos.Mail;

/// <summary>
/// The Google OAuth client every released copy of Litos signs in to Gmail as, so a user never has
/// to create a Google Cloud project: "Connect Gmail…" opens Google's own sign-in straight away, the
/// way Claude Code's /login does. It is the "Desktop app" client in the Litos Google Cloud project
/// (publisher: litosai.dev), flagged as used by an AI agent.
///
/// The client id is public by design and lives here. The client secret is supplied at build time
/// (MSBuild property <c>LitosGmailClientSecret</c>, which MSBuild also reads from an environment
/// variable of that name; release workflows pass it from a repository secret) and baked into
/// this assembly's metadata, so the public repository never holds it. Google doesn't treat an
/// installed app's secret as confidential — the sign-in is protected by PKCE and the loopback
/// redirect (see <see cref="MailOAuth"/>) — but public-repository secret scanners do, and a
/// flagged secret can get the client disabled. A build without it has no built-in client: the
/// Gmail window says sign-in isn't set up, and a developer uses their own client through
/// ~/.litos/gmail.json or LITOS_GMAIL_CLIENT_ID / LITOS_GMAIL_CLIENT_SECRET, which take precedence.
///
/// Until Google's verification of the Litos app is complete the client is in Testing mode: only
/// accounts listed as test users in the Google Cloud console can sign in, and Google signs them
/// out every 7 days.
/// </summary>
public static class BuiltInGmailClient
{
    public const string ClientId = "131380048259-ntervqet4vq46fev8vvpusbsj3njhb39.apps.googleusercontent.com";

    internal const string SecretMetadataKey = "LitosGmailClientSecret";

    /// <summary>The built-in client, or null when this build was made without its secret.</summary>
    public static MailOAuthClient? Client { get; } = Create(
        typeof(BuiltInGmailClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == SecretMetadataKey)?.Value);

    internal static MailOAuthClient? Create(string? secret) =>
        string.IsNullOrWhiteSpace(secret) ? null : new MailOAuthClient(ClientId, secret.Trim());
}
