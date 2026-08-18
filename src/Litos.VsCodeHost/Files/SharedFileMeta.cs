namespace Litos.VsCodeHost.Files;

/// <summary>Local copy of Litos.Api's Files/SharedFileMeta.cs — see SharedFileStore's own note on why each face keeps its own copy.</summary>
public sealed record SharedFileMeta(string Owner, string FileName, string? ContentType, DateTimeOffset ExpiresAt);

public sealed record SharedFileToken(string Token, DateTimeOffset ExpiresAt);
