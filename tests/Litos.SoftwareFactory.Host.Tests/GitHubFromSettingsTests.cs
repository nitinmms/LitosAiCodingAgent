using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The factory-wide GitHub token, read from settings when used (m3-architecture.md §2).</summary>
public sealed class GitHubFromSettingsTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task ItIsConfigured_WhileATokenIsSet()
    {
        var gitHub = new GitHubFromSettings(_settings);
        if (_settings.IsSet(SecretNames.GitHub))
            await _settings.ClearSecretAsync(SecretNames.GitHub, await _host.AdminIdAsync(), default);
        Assert.False(gitHub.IsConfigured);

        await _settings.SetSecretAsync(SecretNames.GitHub, "ghp_new", null, default);

        Assert.True(gitHub.IsConfigured);
    }

    [Fact]
    public async Task WithNoToken_ACallFails_SayingWhatToSet()
    {
        if (_settings.IsSet(SecretNames.GitHub))
            await _settings.ClearSecretAsync(SecretNames.GitHub, await _host.AdminIdAsync(), default);

        var refused = await Assert.ThrowsAsync<GitHubException>(() =>
            new GitHubFromSettings(_settings).GetPullRequestStateAsync("acme", "salesapp", 1, default));

        Assert.Contains("No GitHub token is set", refused.Message);
    }

    [Fact]
    public async Task APullRequestsState_IsNotAsked_WithNoToken()
    {
        var gitHub = new FakeGitHub { IsConfigured = false };
        var status = new PullRequestStatus(new SystemClockForTests(), NullLogger<PullRequestStatus>.Instance, gitHub);

        Assert.Null(await status.GetAsync("acme", "salesapp", 212, default));
        Assert.Empty(gitHub.StateRequests);
    }

    private sealed class SystemClockForTests : Core.Ports.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
