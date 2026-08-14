using System.Security.Cryptography;
using System.Text;

namespace Litos.Api.Auth;

/// <summary>
/// Resolves ADMIN_TOKEN from the environment directly — no file-backed config for this value
/// (HeadlessServiceTool.md §5.5 treats it as a deploy-time secret, not user-editable state).
/// </summary>
public sealed class AdminTokenProvider
{
    private readonly byte[]? _tokenBytes;

    public AdminTokenProvider(IConfiguration configuration)
    {
        var token = Environment.GetEnvironmentVariable("ADMIN_TOKEN") ?? configuration["ADMIN_TOKEN"];
        _tokenBytes = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    }

    public bool IsConfigured => _tokenBytes is not null;

    public bool IsValid(string presented)
    {
        if (_tokenBytes is null)
            return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        // Lengths must match for FixedTimeEquals; a length mismatch is itself safe to
        // short-circuit on since it leaks no information about the token's content.
        return presentedBytes.Length == _tokenBytes.Length && CryptographicOperations.FixedTimeEquals(presentedBytes, _tokenBytes);
    }
}
