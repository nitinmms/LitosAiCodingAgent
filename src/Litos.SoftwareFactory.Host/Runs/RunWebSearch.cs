using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Host.Settings;
using Litos.Tools.Web;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// A run's web searches (m3-architecture.md §6, blueprint §8): the host searches with the key it
/// holds, so no worker ever has it, and writes every query, with the URLs it found or why it found
/// none, to the run's <c>web-search.log</c>. The settings are read again on every search, so web
/// search turned off, or its key cleared, stops a run that is already searching.
/// </summary>
public sealed class RunWebSearch(FactorySettings settings, TavilySearchClient tavily, FactoryOptions options, IClock clock, ILogger<RunWebSearch> logger)
{
    /// <summary>A query longer than this is cut: Tavily refuses very long ones, and no useful search needs more.</summary>
    public const int MaxQueryLength = 400;

    public const int MaxResults = 10;

    private readonly Lock _logLock = new();

    public static string LogPath(string dataDirectory, Guid runId) => Path.Combine(dataDirectory, "runs", runId.ToString("N"), "web-search.log");

    public async Task<WebSearchResponse> SearchAsync(ActiveRun active, WebSearchRequest request, CancellationToken ct)
    {
        var query = request.Query.Trim();
        if (query.Length > MaxQueryLength)
            query = query[..MaxQueryLength];
        var turn = active.TurnKind;

        if (Refusal(turn) is { } refusal)
        {
            Record(active.RunId, turn, query, urls: null, refusal);
            return new WebSearchResponse(true, refusal);
        }

        try
        {
            var results = await tavily.SearchAsync(settings.Secret(SecretNames.WebSearch)!, query, Math.Clamp(request.MaxResults, 1, MaxResults), ct);
            Record(active.RunId, turn, query, [.. results.Select(r => r.Url)], error: null);
            return new WebSearchResponse(false, TavilySearchClient.Format(results));
        }
        catch (WebSearchException ex)
        {
            Record(active.RunId, turn, query, urls: null, ex.Message);
            return new WebSearchResponse(true, ex.Message);
        }
    }

    /// <summary>Why this turn may not search now; null when it may.</summary>
    private string? Refusal(TurnKind? turn)
    {
        var access = settings.WebSearch;
        if (access == WebSearchAccess.Off)
            return settings.Tools.WebSearchEnabled
                ? "Web search has no key set. An Admin sets it under Settings, Tools."
                : "Web search is turned off for this factory.";

        var changesCode = turn is TurnKind.Implement or TurnKind.Repair or TurnKind.Rework;
        return !changesCode && access != WebSearchAccess.AllTurns
            ? "Web search is not available on a turn that only reads. Use what is in the repository."
            : null;
    }

    private void Record(Guid runId, TurnKind? turn, string query, IReadOnlyList<string>? urls, string? error)
    {
        var line = JsonSerializer.Serialize(new { At = clock.UtcNow, Turn = turn?.ToString(), Query = query, Urls = urls, Error = error }, FactoryWire.Json);
        var path = LogPath(options.DataDirectory, runId);
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException ex)
        {
            // The search itself is not refused for want of its log line.
            logger.LogWarning(ex, "A web search of run {RunId} could not be written to {Path}.", runId, path);
        }
    }
}
