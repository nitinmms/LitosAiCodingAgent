using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Infrastructure.GitHub;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>
/// GitHub with the factory-wide token as it is set now (m3-architecture.md §2): a token set,
/// replaced or cleared in the app applies from the next call, with no restart.
/// </summary>
public sealed class GitHubFromSettings(FactorySettings settings) : IGitHub
{
    private readonly Lock _lock = new();
    private (string Token, GitHubClient Client)? _current;

    public bool IsConfigured => settings.IsSet(SecretNames.GitHub);

    public Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct) =>
        Client().CreateOrUpdateDraftPullRequestAsync(draft, ct);

    public Task<PullRequestState> GetPullRequestStateAsync(string owner, string repository, int number, CancellationToken ct) =>
        Client().GetPullRequestStateAsync(owner, repository, number, ct);

    /// <summary>A client for the token set now; the same one while the token does not change.</summary>
    private GitHubClient Client()
    {
        var token = settings.Secret(SecretNames.GitHub)
            ?? throw new GitHubException("No GitHub token is set. An Admin sets it under Settings, Providers.");
        lock (_lock)
        {
            if (_current is not { } current || current.Token != token)
                _current = current = (token, new GitHubClient(GitHubClient.CreateHttpClient(token)));
            return current.Client;
        }
    }
}
