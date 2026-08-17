namespace Litos.Api.Files;

/// <summary>Persisted alongside the copied file as meta.json — the record SharedFileStore.TryGetAsync reads back to serve a download.</summary>
public sealed record SharedFileMeta(string Owner, string FileName, string? ContentType, DateTimeOffset ExpiresAt);

/// <summary>Returned by SharedFileStore.ShareAsync — just enough for ShareFileTool to build its response.</summary>
public sealed record SharedFileToken(string Token, DateTimeOffset ExpiresAt);
