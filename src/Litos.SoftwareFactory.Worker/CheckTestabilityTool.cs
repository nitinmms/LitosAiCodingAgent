using System.Net.Http.Json;
using System.Text.Json;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The Make-testable run's one way to run the app (testability-architecture.md §6.3): the host
/// starts the working copy's app under the draft <c>litos.test.yaml</c> on a free port, runs the
/// Testability Gate in a browser, and returns each step's result. The agent never starts the app
/// itself; this tool is given only to Make-testable runs.
/// </summary>
public sealed class CheckTestabilityTool(FactoryHostClient host, string sessionId) : ITool
{
    public const string ToolName = "check_testability";

    public string Name => ToolName;

    public string Description =>
        "Run the factory's Testability Gate on your draft litos.test.yaml: the host builds and starts the app as the contract says, "
        + "resets its data, signs in as each role, visits every route in a browser, checks access control, that /__test/* is gone "
        + "in Production, and that every control has an accessible name. Returns each step's result. Takes a minute or two. "
        + "Call it after writing or changing the contract or the app's start-up, and again until it passes.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            steps = new { type = "string", description = "Which Gate steps, e.g. \"1-8\" (the default) or \"1,2,4\" while you work on start-up." },
        },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        try
        {
            var response = await host.CheckTestabilityAsync(new CheckTestabilityRequest(sessionId, arguments.GetStringOrNull("steps")), ct);
            return response.IsError ? ToolResult.Error(response.Text) : ToolResult.Ok(response.Text);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"The factory host could not run the check: {ex.Message}");
        }
    }
}
