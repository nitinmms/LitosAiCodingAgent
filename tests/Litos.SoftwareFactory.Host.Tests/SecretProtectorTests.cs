using Litos.SoftwareFactory.Host.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Secrets are kept protected, with a key ring in the data directory (m3-architecture.md §3.1).</summary>
public sealed class SecretProtectorTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("litos-factory-secrets-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ServiceProvider Host(string dataDirectory) =>
        new ServiceCollection().AddFactorySecretProtection(dataDirectory).BuildServiceProvider();

    [Fact]
    public void ASecret_RoundTrips_AndIsNotStoredInTheClear()
    {
        using var host = Host(_data);
        var protector = host.GetRequiredService<ISecretProtector>();

        var stored = protector.Protect("sk-or-v1-secret-key");

        Assert.DoesNotContain("sk-or-v1", stored);
        Assert.Equal("sk-or-v1-secret-key", protector.Unprotect(stored));
    }

    /// <summary>A restarted host, or one started by another account, reads what the last one stored.</summary>
    [Fact]
    public void TheKeyRing_IsInTheDataDirectory_SoAnotherHostOnItReadsTheSecret()
    {
        string stored;
        using (var first = Host(_data))
            stored = first.GetRequiredService<ISecretProtector>().Protect("ghp_token");

        Assert.NotEmpty(Directory.GetFiles(SecretProtection.KeysDirectory(_data), "key-*.xml"));
        using var second = Host(_data);
        Assert.Equal("ghp_token", second.GetRequiredService<ISecretProtector>().Unprotect(stored));
    }

    [Fact]
    public void Without_ItsKeyRing_ASecretCannotBeRead_AndTheErrorSaysWhatToRestore()
    {
        string stored;
        using (var first = Host(_data))
            stored = first.GetRequiredService<ISecretProtector>().Protect("ghp_token");
        var elsewhere = Directory.CreateTempSubdirectory("litos-factory-other-").FullName;

        try
        {
            using var other = Host(elsewhere);
            var error = Assert.Throws<SecretUnreadableException>(() => other.GetRequiredService<ISecretProtector>().Unprotect(stored));
            Assert.Contains("keys folder", error.Message);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>The sign-in cookies share the key ring; a cookie can never be read as a secret.</summary>
    [Fact]
    public void WhatTheHostProtectsForAnotherPurpose_IsNotASecret()
    {
        using var host = Host(_data);
        var cookie = host.GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Authentication.Cookies").Protect("session");

        Assert.Throws<SecretUnreadableException>(() => host.GetRequiredService<ISecretProtector>().Unprotect(cookie));
    }
}
