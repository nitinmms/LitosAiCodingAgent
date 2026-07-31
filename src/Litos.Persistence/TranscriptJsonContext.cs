using System.Text.Json.Serialization;
using Litos.Agent.Session;

namespace Litos.Persistence;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TranscriptEntry))]
internal sealed partial class TranscriptJsonContext : JsonSerializerContext;
