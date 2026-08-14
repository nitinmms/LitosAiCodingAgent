namespace Litos.Api.Data;

/// <summary>
/// Stores a hash of the refresh token, never the token itself — same rationale as PasswordHash
/// on User: a leaked database row shouldn't hand out a usable credential.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required string TokenHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
