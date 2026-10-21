using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.SoftwareFactory.Tray;

public enum ReminderInterval
{
    Never,
    Every15Minutes,
    Hourly,
}

/// <summary>
/// The tray app's settings, kept per user as JSON beside the factory's own config (on Windows,
/// %APPDATA%\LitosSoftwareFactory\tray.json). The token is not here: it lives in the OS's secure
/// store (<see cref="ITokenStore"/>).
/// </summary>
public sealed record TraySettings
{
    /// <summary>The factory's address, as <see cref="FactoryAddress.Parse"/> accepted it; null until set.</summary>
    public string? Address { get; init; }

    public AlertSound Sound { get; init; } = AlertSound.Ding;

    public ReminderInterval Reminder { get; init; } = ReminderInterval.Never;

    /// <summary>No sound while Windows' Do not disturb (or a full-screen app) holds notifications back.</summary>
    public bool SilentWhenBusy { get; init; } = true;

    public bool StartAtSignIn { get; init; } = true;

    /// <summary>The person agreed to send the token unencrypted to a factory on another machine.</summary>
    public bool AllowPlainHttp { get; init; }

    /// <summary>Muted until this time; null when not muted for a while. <see cref="Muted"/> is the indefinite mute.</summary>
    public DateTimeOffset? MutedUntil { get; init; }

    public bool Muted { get; init; }

    public bool IsMuted(DateTimeOffset now) => Muted || MutedUntil > now;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings in the file, or the defaults when it is missing or cannot be read.</summary>
    public static TraySettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes the file whole, through a temporary file, so a crash never leaves half of it.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Whether a reminder is due: something still waits, and the last ring was an interval ago.</summary>
    public bool ReminderDue(DateTimeOffset lastRing, DateTimeOffset now, int awaiting) => awaiting > 0 && Reminder switch
    {
        ReminderInterval.Every15Minutes => now - lastRing >= TimeSpan.FromMinutes(15),
        ReminderInterval.Hourly => now - lastRing >= TimeSpan.FromHours(1),
        _ => false,
    };
}

/// <summary>Reading the factory address a person typed or pasted.</summary>
public static class FactoryAddress
{
    /// <summary>
    /// The address to connect to, or why it can't be used. A page address pasted from the browser
    /// works: the <c>#/board</c> part and any query are dropped, and a path behind a reverse proxy
    /// (<c>https://host/factory</c>) is kept. Plain HTTP to another machine would send the token
    /// unencrypted, so it needs <paramref name="allowPlainHttp"/>; to this machine it is fine.
    /// </summary>
    public static (Uri? Address, string? Error) Parse(string? input, bool allowPlainHttp)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            return (null, "Enter the factory's address, as the web app's Tray app page shows it.");
        if (!text.Contains("://", StringComparison.Ordinal))
            return (null, "Start the address with https:// or http://.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (null, "That is not a web address. It looks like https://factory.example.com or http://127.0.0.1:5180.");

        if (uri.Scheme == Uri.UriSchemeHttp && !IsThisMachine(uri) && !allowPlainHttp)
        {
            return (null, $"{uri.Host} is another machine, and plain http:// would send your token unencrypted. "
                + "Use its https:// address, or tick “Allow an unencrypted connection”.");
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return (new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri, null);
    }

    public static bool IsThisMachine(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>The web app's page for one task, which a click on its notification opens.</summary>
    public static Uri TaskPage(Uri address, Guid taskId) => new(address, $"#/threads/{taskId}");

    public static Uri Board(Uri address) => new(address, "#/board");

    /// <summary>The web app's page where tokens are made.</summary>
    public static Uri TrayPage(Uri address) => new(address, "#/tray");
}

/// <summary>Where the token is kept: the OS's secure store (Windows Credential Manager, the macOS
/// Keychain, the Linux Secret Service), one entry per factory address.</summary>
public interface ITokenStore
{
    string? Read(Uri address);

    void Write(Uri address, string token);

    void Delete(Uri address);
}
