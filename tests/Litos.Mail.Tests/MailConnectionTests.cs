using System.Net;
using Litos.Agent.Tools;
using Litos.Mail;

namespace Litos.Mail.Tests;

/// <summary>An in-memory <see cref="ICredentialStore"/>, standing in for DPAPI in a test.</summary>
internal sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public string? Read(string name) => _secrets.GetValueOrDefault(name);

    public void Write(string name, string secret) => _secrets[name] = secret;

    public void Delete(string name) => _secrets.Remove(name);

    public bool Has(string name) => _secrets.ContainsKey(name);
}

/// <summary>
/// The connector as a face uses it (Implement_Buzzies.md, Phase 4): what is connected, which tools a
/// Buzzy is offered, and that "disconnect" really disconnects.
/// </summary>
public class MailConnectionTests
{
    private static (MailConnection Mail, MailSettingsStore Settings, MemoryCredentialStore Store) Build(TempDir temp)
    {
        var settings = new MailSettingsStore(Path.Combine(temp.Path, "gmail.json"));
        var store = new MemoryCredentialStore();
        var tokens = new MailTokenProvider(store, settings, new HttpClient());
        return (new MailConnection(settings, tokens, new HttpClient()), settings, store);
    }

    [Fact]
    public void WithNothingConnected_ThereAreNoToolsAtAll()
    {
        using var temp = new TempDir();
        var (mail, _, _) = Build(temp);

        Assert.False(mail.IsConnected);
        Assert.Null(mail.Info());
        Assert.Empty(mail.Tools(new FakeApproval()));
        Assert.Empty(mail.ReadOnlyTools());
    }

    [Fact]
    public void TheSampleInbox_IsAUsableMailbox_AndSaysSo()
    {
        using var temp = new TempDir();
        var (mail, settings, _) = Build(temp);
        settings.UseSampleData(settings.Load(), true);

        Assert.True(mail.IsConnected);
        Assert.True(mail.UsingFixtureData);
        Assert.Equal(5, mail.Tools(new FakeApproval()).Count);
        Assert.Contains("sample", mail.Info()!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlyTools_AreTheThreeThatChangeNothing()
    {
        using var temp = new TempDir();
        var (mail, settings, _) = Build(temp);
        settings.UseSampleData(settings.Load(), true);

        var names = mail.ReadOnlyTools().Select(t => t.Name).Order().ToArray();

        Assert.Equal(["mail_read", "mail_search", "mail_summarise"], names);
    }

    [Fact]
    public void AConnectedAccount_IsReadOnlyUntilComposeWasGranted()
    {
        using var temp = new TempDir();
        var (mail, settings, _) = Build(temp);

        settings.Connect(settings.Load(), new MailAccount("me@example.com", [MailOAuth.ReadOnlyScope], DateTimeOffset.Now));
        Assert.False(mail.Info()!.CanDraft);
        Assert.Contains("read only", mail.Info()!.Description);

        settings.Connect(settings.Load(),
            new MailAccount("me@example.com", [MailOAuth.ReadOnlyScope, MailOAuth.ComposeScope], DateTimeOffset.Now));
        Assert.True(mail.Info()!.CanDraft);
        Assert.Contains("read and draft", mail.Info()!.Description);
    }

    [Fact]
    public void Disconnect_ForgetsTheAccount()
    {
        using var temp = new TempDir();
        var (mail, settings, _) = Build(temp);
        settings.Connect(settings.Load(), new MailAccount("me@example.com", [MailOAuth.ReadOnlyScope], DateTimeOffset.Now));

        settings.Disconnect(settings.Load());

        Assert.False(mail.IsConnected);
        Assert.Null(mail.Info());
    }

    [Fact]
    public void TheSampleInboxWins_EvenWhenAnAccountIsConnected()
    {
        // A demo must never quietly reach a real mailbox because a connection was left over.
        using var temp = new TempDir();
        var (mail, settings, _) = Build(temp);
        settings.Connect(settings.Load(), new MailAccount("me@example.com", [MailOAuth.ReadOnlyScope], DateTimeOffset.Now));

        settings.UseSampleData(settings.Load(), true);

        Assert.True(mail.UsingFixtureData);
        Assert.Contains("sample", mail.Describe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATokenInTheStore_IsUsedWithoutAskingTheUser()
    {
        using var temp = new TempDir();
        var settings = new MailSettingsStore(Path.Combine(temp.Path, "gmail.json"));
        var store = new MemoryCredentialStore();
        var handler = new ScriptedHandler((_, _) => Responses.Json(
            """{"access_token":"fresh","expires_in":3600,"scope":"https://www.googleapis.com/auth/gmail.readonly"}"""));
        var tokens = new MailTokenProvider(store, settings, new HttpClient(handler));
        store.Write(MailTokenProvider.RefreshTokenName, "refresh-1");
        settings.Save(settings.Load() with { App = new MailOAuthClient("id", "secret") });

        var token = await tokens.AccessTokenAsync(default);

        Assert.Equal("fresh", token);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("grant_type=refresh_token", request.Body);
        Assert.Contains("refresh_token=refresh-1", request.Body);
    }

    [Fact]
    public async Task WithNoStoredRefreshToken_ThereIsNoToken()
    {
        using var temp = new TempDir();
        var settings = new MailSettingsStore(Path.Combine(temp.Path, "gmail.json"));
        var tokens = new MailTokenProvider(new MemoryCredentialStore(), settings, new HttpClient());

        Assert.Null(await tokens.AccessTokenAsync(default));
    }

    [Fact]
    public void Clear_ReallyClearsTheStoredToken()
    {
        using var temp = new TempDir();
        var settings = new MailSettingsStore(Path.Combine(temp.Path, "gmail.json"));
        var store = new MemoryCredentialStore();
        var tokens = new MailTokenProvider(store, settings, new HttpClient());
        tokens.Save(new MailTokens("a", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), []));

        Assert.True(store.Has(MailTokenProvider.RefreshTokenName));
        tokens.Clear();
        Assert.False(store.Has(MailTokenProvider.RefreshTokenName));
    }

    [Fact]
    public void Save_KeepsNoTokensInTheSettingsFile()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "gmail.json");
        var settings = new MailSettingsStore(path);
        var store = new MemoryCredentialStore();
        var tokens = new MailTokenProvider(store, settings, new HttpClient());

        tokens.Save(new MailTokens("access-should-not-be-stored", "refresh-should-not-be-stored",
            DateTimeOffset.UtcNow.AddHours(1), []));
        settings.Connect(settings.Load(), new MailAccount("me@example.com", [MailOAuth.ReadOnlyScope], DateTimeOffset.Now));

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("refresh-should-not-be-stored", text);
        Assert.DoesNotContain("access-should-not-be-stored", text);
        Assert.Contains("me@example.com", text);
    }

    [Fact]
    public void ADamagedSettingsFile_IsTreatedAsAbsent()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "gmail.json");
        File.WriteAllText(path, "{ not json");

        var settings = new MailSettingsStore(path).Load();

        Assert.Null(settings.App);
        Assert.Null(settings.Connection);
        Assert.False(settings.UseFixtureData);
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void FileStore_RoundTripsASecret()
    {
        using var temp = new TempDir();
        var store = new FileCredentialStore(temp.Path);

        store.Write("k", "s3cret-refresh-token");

        Assert.Equal("s3cret-refresh-token", new FileCredentialStore(temp.Path).Read("k"));
    }

    [Fact]
    public void FileStore_WritesCiphertextNotPlaintext()
    {
        using var temp = new TempDir();
        new FileCredentialStore(temp.Path).Write("k", "s3cret-refresh-token");

        var blob = File.ReadAllBytes(Path.Combine(temp.Path, "k.bin"));

        Assert.DoesNotContain("s3cret", System.Text.Encoding.UTF8.GetString(blob));
    }

    [Fact]
    public void FileStore_AMissingSecretIsNull()
    {
        using var temp = new TempDir();

        Assert.Null(new FileCredentialStore(temp.Path).Read("nothing"));
    }

    [Fact]
    public void FileStore_DeleteRemovesIt()
    {
        using var temp = new TempDir();
        var store = new FileCredentialStore(temp.Path);
        store.Write("k", "v");

        store.Delete("k");

        Assert.Null(store.Read("k"));
    }

    [Fact]
    public void FileStore_ADifferentKeySaysNullRatherThanGarbage()
    {
        // A copied blob with a different local key: report "absent" so the user is asked to connect
        // again, rather than handing a model a decryption error.
        using var temp = new TempDir();
        new FileCredentialStore(temp.Path).Write("k", "v");
        var other = System.IO.Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(other);

        Assert.Null(new FileCredentialStore(other).Read("k"));
    }

    [Fact]
    public void DpapiStore_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // DPAPI is Windows-only; FileCredentialStore is the fallback elsewhere.

        using var temp = new TempDir();
        var store = new DpapiCredentialStore(temp.Path);

        store.Write("k", "refresh-token");
        Assert.Equal("refresh-token", new DpapiCredentialStore(temp.Path).Read("k"));

        store.Delete("k");
        Assert.Null(store.Read("k"));
    }

    [Fact]
    public void TheMachineGetsAStoreOnEveryPlatform()
    {
        Assert.NotNull(CredentialStores.ForThisMachine());
        Assert.Equal(OperatingSystem.IsWindows(),
            CredentialStores.ForThisMachine() is DpapiCredentialStore);
    }
}
