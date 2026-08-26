using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Kernel;

/// <summary>
/// Flat, newline-delimited System.Text.Json records exchanged over the kernel subprocess's
/// stdio — no JSON-RPC library, no polymorphic envelope, matching how ContentBlock/TranscriptEntry
/// already do plain System.Text.Json records elsewhere in this codebase
/// (ReadMe_PTCPersistentKernel.md §7 "Wire protocol shape"). Each message is exactly one line.
/// </summary>
public static class KernelProtocol
{
    /// <summary>Bumped whenever a message shape below changes incompatibly. Exchanged once at subprocess startup.</summary>
    public const int CurrentVersion = 1;
}

public sealed record Handshake(int ProtocolVersion);

public sealed record HandshakeAck(int ProtocolVersion, bool Accepted, string? Reason);

/// <summary>
/// Sent once per subprocess lifetime, immediately after the handshake, before any EvalRequest —
/// carries the bridged-tool schema list and the scratch path so the host can pre-inject
/// SCRATCH_DIR and generate per-tool wrapper functions into the Roslyn globals before the first
/// script runs (§4.1, §4.5).
/// </summary>
public sealed record InitRequest(string ScratchDirectory, IReadOnlyList<BridgedToolSchema> BridgedTools);

public sealed record InitAck(bool Success, string? Error);

public sealed record BridgedToolSchema(string Name, string Description, JsonElement ParameterSchema);

public sealed record EvalRequest(string RequestId, string Code);

public sealed record EvalResult(
    string RequestId,
    string? Output,
    string? ReturnValueText,
    bool IsError,
    bool Truncated,
    string? ArtifactPath,
    string? StateDelta);

/// <summary>Interpreter -> host: a bridged tool call the running script made. Blocks until the matching ToolCallResponse arrives.</summary>
public sealed record ToolCallRequest(string RequestId, string ToolName, JsonElement Arguments);

public sealed record ToolCallResponse(string RequestId, string Text, bool IsError);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Handshake))]
[JsonSerializable(typeof(HandshakeAck))]
[JsonSerializable(typeof(InitRequest))]
[JsonSerializable(typeof(InitAck))]
[JsonSerializable(typeof(EvalRequest))]
[JsonSerializable(typeof(EvalResult))]
[JsonSerializable(typeof(ToolCallRequest))]
[JsonSerializable(typeof(ToolCallResponse))]
[JsonSerializable(typeof(KernelWireMessage))]
public sealed partial class KernelProtocolJsonContext : JsonSerializerContext;

/// <summary>
/// Every line on the wire is one of these, tagged by "kind" — a flat envelope rather than
/// JsonPolymorphic/[JsonDerivedType] since only one side ever needs to interpret any given kind
/// (§7's "no JSON-RPC library, no [JsonPolymorphic] envelope" — this is the minimal tagging that
/// still lets a single ReadLine loop dispatch by shape). Exactly one of the payload properties is
/// non-null for a given Kind.
/// </summary>
public sealed record KernelWireMessage(
    string Kind,
    Handshake? Handshake = null,
    HandshakeAck? HandshakeAck = null,
    InitRequest? InitRequest = null,
    InitAck? InitAck = null,
    EvalRequest? EvalRequest = null,
    EvalResult? EvalResult = null,
    ToolCallRequest? ToolCallRequest = null,
    ToolCallResponse? ToolCallResponse = null)
{
    public const string KindHandshake = "handshake";
    public const string KindHandshakeAck = "handshake_ack";
    public const string KindInitRequest = "init_request";
    public const string KindInitAck = "init_ack";
    public const string KindEvalRequest = "eval_request";
    public const string KindEvalResult = "eval_result";
    public const string KindToolCallRequest = "tool_call_request";
    public const string KindToolCallResponse = "tool_call_response";

    public static KernelWireMessage Of(Handshake m) => new(KindHandshake, Handshake: m);
    public static KernelWireMessage Of(HandshakeAck m) => new(KindHandshakeAck, HandshakeAck: m);
    public static KernelWireMessage Of(InitRequest m) => new(KindInitRequest, InitRequest: m);
    public static KernelWireMessage Of(InitAck m) => new(KindInitAck, InitAck: m);
    public static KernelWireMessage Of(EvalRequest m) => new(KindEvalRequest, EvalRequest: m);
    public static KernelWireMessage Of(EvalResult m) => new(KindEvalResult, EvalResult: m);
    public static KernelWireMessage Of(ToolCallRequest m) => new(KindToolCallRequest, ToolCallRequest: m);
    public static KernelWireMessage Of(ToolCallResponse m) => new(KindToolCallResponse, ToolCallResponse: m);
}
