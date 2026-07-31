namespace Litos.Agent.Providers;

public sealed record ModelInfo(string Id, string DisplayName, bool IsDefault, string? Description = null, int? ContextLength = null);
