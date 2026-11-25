namespace Litos.Hosting.Files;

/// <summary>Copy of Litos.Api's Files/SharedFileMeta.cs — see SharedFileStore's own note.</summary>
public sealed record SharedFileMeta(string Owner, string FileName, string? ContentType, DateTimeOffset ExpiresAt);

public sealed record SharedFileToken(string Token, DateTimeOffset ExpiresAt);
