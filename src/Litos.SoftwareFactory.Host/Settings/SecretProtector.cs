using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>Turns a secret into the form the store keeps, and back (m3-architecture.md §3.1).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <exception cref="SecretUnreadableException">The key that protected it is not in the key ring.</exception>
    string Unprotect(string ciphertext);
}

/// <summary>
/// A stored secret could not be read: the key ring it was protected with is gone, typically
/// because the data directory's <c>keys</c> folder was not restored with the database.
/// </summary>
public sealed class SecretUnreadableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// ASP.NET Data Protection, under a purpose of its own, so nothing else the host protects (its
/// sign-in cookies) can be unprotected as a secret, or the other way round.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    internal const string Purpose = "Litos.SoftwareFactory.Secrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext)
    {
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            throw new SecretUnreadableException(
                "A stored secret cannot be read with this host's key ring. Restore the data directory's keys folder, or set the secret again.", ex);
        }
    }
}

public static class SecretProtection
{
    /// <summary>
    /// The key ring lives in the data directory, beside the working copies, so the host reads its
    /// secrets whichever Windows account starts it, and a backup of the data directory carries the
    /// keys the database's secrets need. The keys are not encrypted at rest: anyone who can read
    /// the data directory can already read the working copies (blueprint §17).
    /// </summary>
    public static IServiceCollection AddFactorySecretProtection(this IServiceCollection services, string dataDirectory)
    {
        services.AddDataProtection()
            .SetApplicationName("Litos.SoftwareFactory")
            .PersistKeysToFileSystem(new DirectoryInfo(KeysDirectory(dataDirectory)));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }

    public static string KeysDirectory(string dataDirectory) => Path.Combine(dataDirectory, "keys");
}
