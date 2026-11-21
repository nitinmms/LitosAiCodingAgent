using System.Text.Json;
using Litos.Probe.Web.Core.Scenarios;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>A project's stored scenarios as Probe.Web runs them (README_Litos.Probe.Web.md W3.6).</summary>
public static class ProjectScenarios
{
    /// <summary>The scenario a row holds, or null if it no longer reads (a format change since it was saved).</summary>
    public static WebScenario? Parse(ProjectScenario row)
    {
        try
        {
            return WebScenario.Parse(row.ScenarioJson) is [var one] ? one : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What tasks and main's baseline run: the approved revision of each scenario.</summary>
    public static async Task<IReadOnlyList<WebScenario>> ApprovedAsync(IFactoryStore store, Guid projectId, CancellationToken ct) =>
        [.. (await store.ApprovedScenariosAsync(projectId, ct)).Select(Parse).OfType<WebScenario>()];
}
