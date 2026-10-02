using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Infrastructure.GitHub;

public sealed class GitHubException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed partial record GitHubRepository(string Owner, string Name)
{
    [GeneratedRegex(@"^(?:https://github\.com/|git@github\.com:)([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9._-]+?)(?:\.git)?/?$")]
    private static partial Regex GitHubUrl();

    /// <summary>Accepts the forms an Admin is likely to paste: https://github.com/owner/repo,
    /// with or without .git or a trailing slash, and git@github.com:owner/repo.git.</summary>
    public static bool TryParse(string url, out GitHubRepository? repository)
    {
        var match = GitHubUrl().Match(url.Trim());
        repository = match.Success ? new GitHubRepository(match.Groups[1].Value, match.Groups[2].Value) : null;
        return match.Success;
    }

    public string CloneUrl => $"https://github.com/{Owner}/{Name}.git";

    public string WebUrl => $"https://github.com/{Owner}/{Name}";
}

/// <summary>
/// Creates and updates draft pull requests through the GitHub REST API. The token comes from
/// host configuration only and lives on this client's HttpClient; it is never given to a worker.
/// </summary>
public sealed class GitHubClient(HttpClient http) : IGitHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>An HttpClient set up for api.github.com with the given token.</summary>
    public static HttpClient CreateHttpClient(string token, HttpMessageHandler? handler = null)
    {
        // GitHub closes a connection that has been idle for about a minute. A client that keeps
        // one for longer sends its next request into a closed socket and fails with "An error
        // occurred while sending the request" — which is what every pull request update did in
        // the first real runs, because the host polls a pull request's state once a minute and
        // so always had a connection of just that age. Dropping idle connections sooner than
        // GitHub does avoids the race.
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("litos-software-factory", "1.0"));
        return client;
    }

    /// <summary>How many times a request that failed in transit is tried in all.</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>The pause before a retry, multiplied by the attempt number; tests shorten it.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Runs one whole operation again when it failed in transit — the request never got an
    /// answer. Both operations here are safe to repeat from the start: reading state changes
    /// nothing, and create-or-update looks the pull request up first, so a create that did reach
    /// GitHub is found and updated rather than created twice. A refusal from GitHub (a status
    /// code) is an answer, and is never retried.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException) when (attempt < Attempts)
            {
                await Task.Delay(RetryDelay * attempt, ct);
            }
        }
    }

    public Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct) =>
        WithRetryAsync(() => CreateOrUpdateOnceAsync(draft, ct), ct);

    private async Task<PullRequestRef> CreateOrUpdateOnceAsync(PullRequestDraft draft, CancellationToken ct)
    {
        var repository = $"repos/{Uri.EscapeDataString(draft.Owner)}/{Uri.EscapeDataString(draft.Repository)}";

        // A rework run pushes to a branch that already has its PR: update that one rather than
        // failing on GitHub's "a pull request already exists" error.
        var head = Uri.EscapeDataString($"{draft.Owner}:{draft.Head}");
        using var listResponse = await http.GetAsync($"{repository}/pulls?state=open&head={head}", ct);
        await EnsureSuccessAsync(listResponse, "look up the pull request", ct);
        var open = await listResponse.Content.ReadFromJsonAsync<List<PullRequestResponse>>(JsonOptions, ct) ?? [];

        if (open.FirstOrDefault() is { } existing)
        {
            using var patchResponse = await http.PatchAsJsonAsync(
                $"{repository}/pulls/{existing.Number}", new UpdatePullRequest(draft.Title, draft.Body), JsonOptions, ct);
            await EnsureSuccessAsync(patchResponse, "update the pull request", ct);
            return ToRef(await patchResponse.Content.ReadFromJsonAsync<PullRequestResponse>(JsonOptions, ct));
        }

        using var createResponse = await http.PostAsJsonAsync(
            $"{repository}/pulls", new CreatePullRequest(draft.Title, draft.Body, draft.Head, draft.Base, Draft: true), JsonOptions, ct);
        await EnsureSuccessAsync(createResponse, "create the draft pull request", ct);
        return ToRef(await createResponse.Content.ReadFromJsonAsync<PullRequestResponse>(JsonOptions, ct));
    }

    public Task<PullRequestState> GetPullRequestStateAsync(string owner, string repository, int number, CancellationToken ct) =>
        WithRetryAsync(() => GetPullRequestStateOnceAsync(owner, repository, number, ct), ct);

    private async Task<PullRequestState> GetPullRequestStateOnceAsync(string owner, string repository, int number, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pulls/{number}", ct);
        await EnsureSuccessAsync(response, "read the pull request", ct);
        var pull = await response.Content.ReadFromJsonAsync<PullRequestResponse>(JsonOptions, ct)
            ?? throw new GitHubException("GitHub's response did not describe a pull request.");

        // GitHub's "state" is only open or closed; merged and draft are separate flags.
        if (pull.Merged == true)
            return PullRequestState.Merged;
        if (pull.State == "closed")
            return PullRequestState.Closed;
        return pull.Draft == true ? PullRequestState.Draft : PullRequestState.Open;
    }

    private static PullRequestRef ToRef(PullRequestResponse? response) =>
        response is { Number: > 0, HtmlUrl: { Length: > 0 } url }
            ? new PullRequestRef(response.Number, url)
            : throw new GitHubException("GitHub's response did not describe a pull request.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        // GitHub's error body carries a human-readable "message"; it never echoes the token.
        string? detail = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, ct);
            detail = error?.Message;
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        throw new GitHubException(
            detail is null ? $"GitHub refused to {action} (HTTP {status})." : $"GitHub refused to {action} (HTTP {status}): {detail}", status);
    }

    private sealed record CreatePullRequest(string Title, string Body, string Head, string Base, bool Draft);

    private sealed record UpdatePullRequest(string Title, string Body);

    private sealed record PullRequestResponse(int Number, string? HtmlUrl, string? State = null, bool? Draft = null, bool? Merged = null);

    private sealed record ErrorResponse(string? Message);
}
