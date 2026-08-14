namespace Litos.Api.Data;

public sealed class User
{
    public Guid Id { get; init; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public bool IsAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Soft-disable — set to revoke access without deleting session history tied to this user's SessionOwner.</summary>
    public DateTimeOffset? DisabledAt { get; set; }
}
