using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Litos.Api.Auth;

/// <summary>
/// Resolves JWT_SIGNING_KEY from the environment directly — a deploy-time secret, same pattern
/// as AdminTokenProvider's ADMIN_TOKEN (no file-backed config, env var only). Required whenever
/// per-user accounts exist, since /auth/token can't issue anything without it.
/// </summary>
public sealed class JwtSigningKeyProvider
{
    public const string Issuer = "litos-api";
    public const string Audience = "litos-api-clients";

    public SymmetricSecurityKey Key { get; }

    public JwtSigningKeyProvider(IConfiguration configuration)
    {
        var key = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY") ?? configuration["JWT_SIGNING_KEY"];
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("JWT_SIGNING_KEY is required (env var) to issue per-user API tokens.");

        // HMAC-SHA256 needs >= 256 bits of key material — a short/weak key would silently produce
        // a forgeable signature rather than a clear startup error, so this is checked eagerly.
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("JWT_SIGNING_KEY must be at least 32 bytes (256 bits) for HMAC-SHA256.");

        Key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}
