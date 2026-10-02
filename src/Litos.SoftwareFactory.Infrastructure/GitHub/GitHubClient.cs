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
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("litos-software-factory", "1.0"));
        return client;
    }

    public async Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct)
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

    public async Task<PullRequestState> GetPullRequestStateAsync(string owner, string repository, int number, CancellationToken ct)
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
