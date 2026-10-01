using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// What kind of agent turn the host is starting. The worker turns it into a tool set (a review
/// turn gets read-only tools); the host uses it to pick the brief.
/// </summary>
public enum TurnKind
{
    Chat,
    Spec,
    Implement,
    Repair,
    Review,
    Rework,
    Nudge,
}

/// <summary>Names both sides must agree on: the secret header, the worker's environment
/// variables and the paths of the worker's callbacks into the host.</summary>
public static class FactoryWire
{
    /// <summary>Carried on every host→worker and worker→host request.</summary>
    public const string SecretHeader = "X-Factory-Secret";

    public const string WorkerSecretVariable = "FACTORY_WORKER_SECRET";
    public const string HostUrlVariable = "FACTORY_HOST_URL";
    public const string RunIdVariable = "FACTORY_RUN_ID";

    public static string SubmissionsPath(string runId) => $"/internal/runs/{Uri.EscapeDataString(runId)}/submissions";

    public static string GatewayPath(string runId) => $"/internal/runs/{Uri.EscapeDataString(runId)}/gateway";

    public static string ReadyPath(string runId) => $"/internal/runs/{Uri.EscapeDataString(runId)}/ready";

    /// <summary>The worker's own extra endpoints, beside Litos.Hosting's turn endpoints.</summary>
    public static string CompactPath(string sessionId) => $"/sessions/{Uri.EscapeDataString(sessionId)}/compact";

    public const string ShutdownPath = "/shutdown";

    /// <summary>
    /// The one JSON shape for everything on the factory wire. Enums travel as names, so a
    /// transcript of the wire is readable and adding an enum member can't silently renumber
    /// the ones after it.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>The worker's "started" callback: its port, and whether its MCP servers have
/// settled — the host waits for this, not just the port handshake, before the first turn.</summary>
public sealed record WorkerReady(int Port, bool McpReady);

/// <summary>Body of the worker's compact endpoint. The instruction tells the summarizer what
/// must survive (acceptance criteria, decisions, changed files, outstanding failures).</summary>
public sealed record CompactRequest(string? Instruction = null);

public sealed record CompactResponse(bool Compacted);
