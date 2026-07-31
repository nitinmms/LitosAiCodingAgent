using System.Text.Json;

namespace Litos.Agent.Tools;

public sealed record ToolSchema(string Name, string Description, JsonElement ParameterSchema);
