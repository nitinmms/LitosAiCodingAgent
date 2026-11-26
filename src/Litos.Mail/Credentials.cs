using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Mail;

/// <summary>
/// OAuth tokens for one connector (Implement_Buzzies.md, Phase 4). Held in memory only as long as a
/// call needs them, and persisted through <see cref="ICredentialStore"/> — never written into a
/// prompt, a transcript, a routine or a Buzzy's folder, which is what "tokens live in the OS
/// credential store" has to mean concretely.
/// </summary>
/// <param name="Scopes">What the user actually consented to, as granted (which can be narrower than
/// what was asked for). The tools consult this rather than assuming: drafting is only possible when
/// <c>gmail.compose</c> is among them.</param>
public sealed record MailTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes)
{
    /// <summary>Treats a token as expired slightly early, so a call never starts on a token that
    /// dies in flight.</summary>
    public bool IsExpired(DateTimeOffset now) => now > ExpiresAt - TimeSpan.FromMinutes(2);

    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
}

/// <summary>
/// Where a secret is kept: the operating system's own protected store, or the closest thing to it on
/// this platform (see <see cref="CredentialStores"/>). Only <see cref="Litos.Mail"/>'s own OAuth code
/// and the settings window use it; no tool, prompt or transcript can reach it.
/// </summary>
public interface ICredentialStore
{
    string? Read(string name);
    void Write(string name, string secret);
    void Delete(string name);
}

public static class CredentialStores
{
    /// <summary>The store for this machine. Windows uses DPAPI (the same protection a browser's
    /// password store uses); elsewhere a user-only file encrypted with AES-GCM and a random local
    /// key, which is the strongest thing available here without a native keychain binding.</summary>
    public static ICredentialStore ForThisMachine() =>
        OperatingSystem.IsWindows() ? new DpapiCredentialStore() : new FileCredentialStore();

    internal static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "secrets");
}

/// <summary>
/// Per-user DPAPI: the ciphertext can only be read by the same Windows account on this machine, so a
/// copied file is useless elsewhere and a copy on another machine is useless here.
/// </summary>
public sealed class DpapiCredentialStore(string? directory = null) : ICredentialStore
{
    private readonly string _directory = directory ?? CredentialStores.DefaultDirectory;

    public string? Read(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or NotSupportedException)
        {
            // A blob from another account or a corrupted file: treat it as absent, so the user is
            // asked to connect again rather than shown a crash.
            return null;
        }
    }

    public void Write(string name, string secret)
    {
        Directory.CreateDirectory(_directory);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), optionalEntropy: null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var path = PathOf(name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathOf(string name) => Path.Combine(_directory, name + ".dpapi");
}

/// <summary>
/// The non-Windows fallback: AES-GCM with a random 256-bit key in a file only this user can read
/// (0600 on Unix). This is honest about what it is — a local secret file, better than plaintext JSON
/// but not a native keychain. A later revision should bind to the macOS Keychain.
/// </summary>
public sealed class FileCredentialStore(string? directory = null) : ICredentialStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _directory = directory ?? CredentialStores.DefaultDirectory;

    public string? Read(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
            return null;
        try
        {
            var blob = File.ReadAllBytes(path);
            if (blob.Length <= NonceSize + TagSize)
                return null;

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new System.Security.Cryptography.AesGcm(Key(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string name, string secret)
    {
        Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        using (var aes = new System.Security.Cryptography.AesGcm(Key(), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        var path = PathOf(name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, blob);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathOf(string name) => Path.Combine(_directory, name + ".bin");

    /// <summary>Reads (or creates once) the local key file, restricted to this user.</summary>
    private byte[] Key()
    {
        var path = Path.Combine(_directory, ".key");
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        Directory.CreateDirectory(_directory);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
