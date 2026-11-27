using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Mail;

/// <summary>
/// What a user has connected, as it is safe to store in a plain file (Implement_Buzzies.md, Phase 4):
/// the address they signed in as, which scopes were granted, and when. The tokens themselves are
/// not here — they are in <see cref="ICredentialStore"/>, which is the whole reason this record
/// exists apart from <see cref="MailTokens"/>.
/// </summary>
public sealed record MailAccount(
    string Address,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt,
    bool UseFixtureData = false)
{
    /// <summary>True when drafting is possible: the compose scope was actually granted, not merely
    /// asked for. Google's consent screen lets a user untick any individual permission.</summary>
    public bool CanDraft => Scopes.Contains(MailOAuth.ComposeScope, StringComparer.Ordinal);

    /// <summary>What the settings window and the tools say about this connection.</summary>
    public string Describe => UseFixtureData
        ? "the sample inbox (fixture data, not a real mailbox)"
        : $"{Address} (Gmail, {(CanDraft ? "read and draft" : "read only")})";
}

/// <summary>
/// The saved Gmail connector settings: the OAuth client the app signs in as, and what the user has
/// connected. Kept in <c>~/.litos/gmail.json</c> — beside config.json, and separate from every Buzzy's
/// folder, so connecting Gmail once serves every Buzzy the user grants mail tools to, and a Buzzy's
/// own files never hold a credential.
/// </summary>
public sealed class MailSettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "gmail.json");

    /// <summary>Everything on disk, or an empty record. A damaged file is treated as absent rather
    /// than failing the app: the user connects Gmail again.</summary>
    public MailSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new MailSettings();
            return JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new MailSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new MailSettings();
        }
    }

    public void Save(MailSettings settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Records a connection and saves, returning what was written.</summary>
    public MailSettings Connect(MailSettings settings, MailAccount account) =>
        SaveAndReturn(settings with { Connection = account, UseFixtureData = false });

    /// <summary>Switches the sample inbox on or off, keeping any real connection's details.</summary>
    public MailSettings UseSampleData(MailSettings settings, bool on) =>
        SaveAndReturn(settings with { UseFixtureData = on });

    /// <summary>Forgets the connection. The tokens are deleted from the credential store separately,
    /// so a failure here can't leave them behind thinking they were removed.</summary>
    public MailSettings Disconnect(MailSettings settings) =>
        SaveAndReturn(settings with { Connection = null, UseFixtureData = false });

    private MailSettings SaveAndReturn(MailSettings settings)
    {
        Save(settings);
        return settings;
    }
}

/// <summary>The whole of gmail.json. <see cref="App"/> is the OAuth client (public by design for a
/// desktop app); <see cref="UseFixtureData"/> says the tools should answer from sample mail — the
/// fallback while Google's verification is pending.</summary>
public sealed record MailSettings(
    MailOAuthClient? App = null,
    MailAccount? Connection = null,
    bool UseFixtureData = false)
{
    /// <summary>The OAuth client in force: the environment's, else the one saved in gmail.json,
    /// else the one built into this copy of Litos (<see cref="BuiltInGmailClient"/>).</summary>
    public MailOAuthClient? EffectiveApp => MailOAuthClient.FromEnvironment() ?? App ?? BuiltInGmailClient.Client;
}
