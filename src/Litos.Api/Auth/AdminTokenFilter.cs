using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Litos.Api.Auth;

/// <summary>
/// Shared-admin-token check for Minimal API endpoints (HeadlessServiceTool.md §5.5). Compares
/// an `Authorization: Bearer &lt;ADMIN_TOKEN&gt;` header using constant-time comparison — avoids
/// leaking the token's correctness via response-time timing.
/// </summary>
public sealed class AdminTokenFilter(AdminTokenProvider tokenProvider) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers[HeaderNames.Authorization].ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return ValueTask.FromResult<object?>(Results.Unauthorized());

        var presented = header[prefix.Length..];
        if (!tokenProvider.IsValid(presented))
            return ValueTask.FromResult<object?>(Results.Unauthorized());

        return next(context);
    }
}

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
