using System.Text.Json.Serialization;
namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>Which of a run's turns may search the web.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSearchAccess
{
    Off,

    /// <summary>Implement, repair and rework turns: those that change code.</summary>
    WorkTurns,

    /// <summary>Every turn, the read-only review, spec, scan and chat turns too.</summary>
    AllTurns,
}

/// <summary>
/// The Tools tab (blueprint §8.3, m3-architecture.md §6). Its defaults keep M2's PTC and the
/// engine's five-minute shell limit, and turn web search on for the turns that change code: it
/// still searches nothing until an Admin sets its key.
/// </summary>
public sealed record ToolSettings
{
    /// <summary>Whether a new thread starts with Programmatic Tool Calling on.</summary>
    public bool PtcByDefault { get; init; } = true;

    /// <summary>Whether a Member may create a thread with PTC other than the default. Admins always may.</summary>
    public bool MembersMayChoosePtc { get; init; } = true;

    /// <summary>How long one shell command may run before the agent's shell kills it.</summary>
    public int ShellTimeoutSeconds { get; init; } = 300;

    /// <summary>Whether the agent may search the web, on by default; its key is a secret (SecretNames.WebSearch).</summary>
    public bool WebSearchEnabled { get; init; } = true;

    /// <summary>Whether read-only turns may search the web too, not only the turns that change code.</summary>
    public bool WebSearchOnReadOnlyTurns { get; init; }

    public const int MinShellTimeoutSeconds = 30;
    public const int MaxShellTimeoutSeconds = 3_600;

    /// <summary>What a run may do, given these settings and whether a key is set.</summary>
    public WebSearchAccess WebSearch(bool keySet) =>
        !WebSearchEnabled || !keySet ? WebSearchAccess.Off
        : WebSearchOnReadOnlyTurns ? WebSearchAccess.AllTurns
        : WebSearchAccess.WorkTurns;

    /// <summary>
    /// The PTC a new thread gets: its creator's choice, or the default. Null with a reason when a
    /// Member asks for what only an Admin may choose.
    /// </summary>
    public bool? ChoosePtc(bool? requested, bool isAdmin, out string? refusal)
    {
        refusal = null;
        if (requested is not { } choice || choice == PtcByDefault)
            return PtcByDefault;
        if (isAdmin || MembersMayChoosePtc)
            return choice;

        refusal = $"Programmatic Tool Calling is {(PtcByDefault ? "on" : "off")} for every new thread; only an Admin can change that.";
        return null;
    }

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ShellTimeoutSeconds is < MinShellTimeoutSeconds or > MaxShellTimeoutSeconds)
            errors.Add($"The shell command time limit must be between {MinShellTimeoutSeconds} and {MaxShellTimeoutSeconds} seconds.");
        if (WebSearchOnReadOnlyTurns && !WebSearchEnabled)
            errors.Add("Web search on read-only turns needs web search to be on.");
        return errors;
    }
}
