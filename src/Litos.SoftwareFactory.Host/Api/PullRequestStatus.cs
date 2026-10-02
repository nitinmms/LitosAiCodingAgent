using System.Collections.Concurrent;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// Where a task's pull request stands on GitHub. The factory opens the draft and then people
/// take over there — marking it ready, merging, closing — so the answer is asked of GitHub, not
/// kept in the factory's own records. Answers are remembered briefly so that every open browser
/// tab does not turn into a GitHub request, and a failure is remembered too, so a GitHub outage
/// is not hammered.
/// </summary>
public sealed class PullRequestStatus(IClock clock, ILogger<PullRequestStatus> logger, IGitHub? gitHub = null)
{
    private readonly ConcurrentDictionary<string, (PullRequestState? State, DateTimeOffset At)> _known = new();

    public TimeSpan Freshness { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The pull request's state, or null when it cannot be learned: the host has no
    /// GitHub token, or GitHub did not answer.</summary>
    public async Task<PullRequestState?> GetAsync(string owner, string repository, int number, CancellationToken ct)
    {
        if (gitHub is null)
            return null;

        var key = $"{owner}/{repository}#{number}";
        if (_known.TryGetValue(key, out var known) && clock.UtcNow - known.At < Freshness)
            return known.State;

        PullRequestState? state;
        try
        {
            state = await gitHub.GetPullRequestStateAsync(owner, repository, number, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message is GitHub's own and never carries the token.
            logger.LogWarning("The state of pull request {PullRequest} could not be read: {Message}", key, ex.Message);
            state = null;
        }

        _known[key] = (state, clock.UtcNow);
        return state;
    }
}
