using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// A run of a project whose profile uses a virtual environment (m3-architecture.md §8): every
/// verification gets the project's venv, and the worker has it first on its PATH, so the agent's
/// shell runs the same pytest as the verifier. Other projects' workers get none.
/// </summary>
public sealed class PresetRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<(Guid Project, FakeWorker Worker)> HandedOffAsync(string preset, string repository)
    {
        var project = await _host.PostAsync("api/projects", new { gitHubUrl = $"https://github.com/acme/{repository}", preset }, System.Net.HttpStatusCode.Created);
        var projectId = project.GetProperty("id").GetGuid();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        return (projectId, _host.Workers.Workers.Single());
    }

    [SkippableFact]
    public async Task APythonProjectsRun_GivesItsVenv_ToEveryVerification_AndToItsWorker()
    {
        Skip.If(Infrastructure.Verification.ProfileExecutables.Find("python", Infrastructure.Verification.ProfileExecutables.HostPath()) is null,
            "python is not on this machine's PATH, so a Python project cannot be registered.");

        var (projectId, worker) = await HandedOffAsync("python", "pyapp");

        var venv = ProfileVariables.VirtualEnvironment(_host.Options.DataDirectory, projectId);
        Assert.Equal(venv, worker.Launch.VirtualEnvironment);
        Assert.NotEmpty(_host.Verifier.Requests);
        Assert.All(_host.Verifier.Requests, r => Assert.Equal(venv, r.Variables![ProfileVariables.Venv]));
    }

    [Fact]
    public async Task AnotherProjectsWorker_HasNoVenv_ThoughItsVerificationsStillKnowWhereOneWouldBe()
    {
        var (projectId, worker) = await HandedOffAsync("dotnet", "netapp");

        Assert.Null(worker.Launch.VirtualEnvironment);
        Assert.All(_host.Verifier.Requests, r => Assert.Equal(
            ProfileVariables.VirtualEnvironment(_host.Options.DataDirectory, projectId), r.Variables![ProfileVariables.Venv]));
    }
}
