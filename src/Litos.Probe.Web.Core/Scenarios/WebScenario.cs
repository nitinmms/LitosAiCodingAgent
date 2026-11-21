using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Probe.Web.Core.Scenarios;

/// <summary>
/// A fixed-step browser scenario (README_Litos.Probe.Web.md §6.2, W3.5): one role, an ordered list
/// of actions and assertions, and the acceptance criterion it checks. No model is called to run it.
/// A step whose target finds nothing is a TestDefect: Probe.Web never guesses another element.
/// </summary>
public sealed record WebScenario
{
    /// <summary>Stable across revisions: <c>cancel-upcoming-appointment</c>. The report's check is <c>scenario:&lt;id&gt;</c>.</summary>
    public string Id { get; init; } = "";

    public string? Title { get; init; }

    public int Revision { get; init; } = 1;

    /// <summary>One of the contract's roles, or null for a signed-out visitor.</summary>
    public string? Role { get; init; }

    /// <summary>The acceptance criterion's text, as the spec states it.</summary>
    public string? Criterion { get; init; }

    /// <summary>The route the scenario opens first.</summary>
    public string Start { get; init; } = "/";

    public IReadOnlyList<ScenarioStep> Steps { get; init; } = [];

    /// <summary>
    /// One scenario, or an array of them. A misspelt field is refused, as in a contract.
    /// </summary>
    /// <exception cref="JsonException">The text is not a scenario or a list of scenarios.</exception>
    public static IReadOnlyList<WebScenario> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<WebScenario>>(ProbeWebJson.Options) ?? []
            : [document.RootElement.Deserialize<WebScenario>(ProbeWebJson.Options) ?? throw new JsonException("Empty scenario")];
    }

    public string ToJson() => JsonSerializer.Serialize(this, ProbeWebJson.Indented);
}

public enum StepType
{
    // Actions
    [JsonStringEnumMemberName("navigate")] Navigate,
    [JsonStringEnumMemberName("click")] Click,
    [JsonStringEnumMemberName("fill")] Fill,
    [JsonStringEnumMemberName("select")] Select,
    [JsonStringEnumMemberName("check")] Check,
    [JsonStringEnumMemberName("uncheck")] Uncheck,
    [JsonStringEnumMemberName("waitFor")] WaitFor,
    [JsonStringEnumMemberName("screenshot")] Screenshot,

    // Assertions: each waits up to its timeout for the condition to hold.
    [JsonStringEnumMemberName("expectVisible")] ExpectVisible,
    [JsonStringEnumMemberName("expectHidden")] ExpectHidden,
    [JsonStringEnumMemberName("expectText")] ExpectText,
    [JsonStringEnumMemberName("expectUrl")] ExpectUrl,
    [JsonStringEnumMemberName("expectState")] ExpectState,
    [JsonStringEnumMemberName("expectError")] ExpectError,
    [JsonStringEnumMemberName("expectResponse")] ExpectResponse,
}

public enum ElementState
{
    [JsonStringEnumMemberName("enabled")] Enabled,
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("checked")] Checked,
    [JsonStringEnumMemberName("unchecked")] Unchecked,
}

/// <summary>One action or assertion. Which fields it needs depends on <see cref="Type"/> (<see cref="ScenarioValidator"/>).</summary>
public sealed record ScenarioStep
{
    [JsonRequired]
    public StepType Type { get; init; }

    public Target? Target { get; init; }

    /// <summary><c>navigate</c>: the route. <c>expectResponse</c>: the request path, a prefix when it ends with <c>*</c>.</summary>
    public string? Path { get; init; }

    /// <summary><c>fill</c> and <c>select</c>. May hold <c>{run}</c> (unique per run) and <c>{secret:NAME}</c>.</summary>
    public string? Value { get; init; }

    /// <summary><c>expectText</c> and <c>expectError</c>: text the element contains.</summary>
    public string? Text { get; init; }

    /// <summary><c>expectUrl</c>: text the page's path and query contain.</summary>
    public string? Url { get; init; }

    /// <summary><c>expectState</c>.</summary>
    public ElementState? State { get; init; }

    /// <summary><c>expectResponse</c>: the HTTP method, any when null.</summary>
    public string? Method { get; init; }

    /// <summary><c>expectResponse</c>: the status the request answered.</summary>
    public int? Status { get; init; }

    /// <summary><c>screenshot</c>: a name for the file.</summary>
    public string? Name { get; init; }

    /// <summary>How long the step waits for its element or condition. Default 10 seconds, at most 60.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// How a step finds its element (README §6.2): by role and accessible name, by label or by
/// <c>data-testid</c> first; by visible text; by CSS only as a fallback; never by coordinates.
/// Exactly one of <see cref="Role"/>, <see cref="Label"/>, <see cref="TestId"/>, <see cref="Text"/>
/// and <see cref="Css"/> is set. A target that matches several elements needs
/// <see cref="Within"/> (a unique container, such as a table row) or, as a last resort, <see cref="Nth"/>.
/// </summary>
public sealed record Target
{
    /// <summary>An ARIA role: <c>button</c>, <c>link</c>, <c>textbox</c>, <c>row</c>…</summary>
    public string? Role { get; init; }

    /// <summary>With <see cref="Role"/>: the exact accessible name.</summary>
    public string? Name { get; init; }

    public string? Label { get; init; }
    public string? TestId { get; init; }
    public string? Text { get; init; }
    public string? Css { get; init; }

    public Target? Within { get; init; }

    /// <summary>0-based, among the matches.</summary>
    public int? Nth { get; init; }

    /// <summary>"button \"Add note\"", "the field labelled \"Email\"", "[data-testid=notes]"…, for messages.</summary>
    public override string ToString()
    {
        var self = Role is not null ? (Name is null ? $"a {Role}" : $"{Role} \"{Name}\"")
            : Label is not null ? $"the field labelled \"{Label}\""
            : TestId is not null ? $"[data-testid={TestId}]"
            : Text is not null ? $"the text \"{Text}\""
            : Css is not null ? $"`{Css}`"
            : "(no target)";
        if (Nth is { } n)
            self += $" (match {n + 1})";
        return Within is null ? self : $"{self} in {Within}";
    }
}
