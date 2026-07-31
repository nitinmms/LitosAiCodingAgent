using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Agent.Messages;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
[JsonDerivedType(typeof(CompactionSummaryBlock), "compaction_summary")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ImageBlock(string MediaType, byte[] Data) : ContentBlock;

public sealed record ToolUseBlock(string CallId, string ToolName, JsonElement Arguments) : ContentBlock;

public sealed record ToolResultBlock(string CallId, string Text, bool IsError = false) : ContentBlock;

/// <summary>
/// Replaces a run of older messages after compaction. Rendered to providers as plain
/// text (a synthetic user-authored recap), but tagged distinctly so it round-trips
/// through the JSONL transcript as its own kind rather than looking like a real user message.
/// </summary>
public sealed record CompactionSummaryBlock(string Summary, int TokensBefore) : ContentBlock;
