using System.Diagnostics;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Git;
using Litos.SoftwareFactory.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// One run with nothing faked between the host and the worker: a real
/// Litos.SoftwareFactory.Worker process is launched in a real git working copy, its agent loop
/// calls the host's model gateway over HTTP, its tools edit real files and call the host back,
/// and the host commits and pushes to a real (local) remote. Only the model's replies and the
/// verification result are scripted.
/// </summary>
public sealed class RealWorkerTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-factory-e2e-").FullName;
    private string _remote = "";
    private TestHost _host = null!;

    private sealed class LocalRemoteWorkspaces(FactoryOptions options, string remote) : IWorkspaceProvider
    {
        public IWorkspace For(Project project) =>
            new GitWorkspace(new GitWorkspaceOptions(Path.Combine(options.WorkspacesDirectory, project.Id.ToString("N")), remote));
    }

    public async Task InitializeAsync()
    {
        // A bare repository stands in for GitHub, with one commit on main.
        _remote = Path.Combine(_root, "remote.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "--initial-branch=main", _remote);
        Git(_root, "clone", _remote, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Sales app\n");
        Git(seed, "add", "--all");
        Git(seed, "-c", "user.name=Seeder", "-c", "user.email=seed@example.invalid", "commit", "--no-gpg-sign", "-m", "Initial commit");
        Git(seed, "push", "origin", "HEAD:refs/heads/main");

        _host = await TestHost.StartAsync(
            options => options.PtcEnabled = false, // the script below calls tools directly
            services =>
            {
                services.AddSingleton<IWorkspaceProvider>(sp => new LocalRemoteWorkspaces(sp.GetRequiredService<FactoryOptions>(), _remote));
                services.AddSingleton<IWorkerLauncher>(_ => new LocalProcessWorkerLauncher(WorkerLocator.Resolve(null)));
                services.AddSingleton<IWorkerClientFactory, HttpWorkerClientFactory>();
            });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    [Fact]
    public async Task ARealWorker_EditsTheWorkingCopy_SubmitsThroughTheHost_AndTheBranchIsPushed()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId, budgetCap: 400_000);
        var workingCopy = Path.Combine(_host.Options.WorkspacesDirectory, projectId.ToString("N"));
        var newFile = Path.Combine(workingCopy, "src", "Orders.cs");

        // The implement turn: write a file, submit the work. The call after each submission is
        // answered by the gateway, not the provider, so nothing is scripted for it.
        _host.Provider.EnqueueToolCall("write_file", new { path = newFile, content = "public class Orders { }\n" });
        _host.Provider.EnqueueToolCall("submit_work", new { summary = "Added the Orders class.", testsAdded = new[] { "OrdersTests" } });
        // The review turn: a clean review.
        _host.Provider.EnqueueToolCall("submit_review", new { findings = Array.Empty<object>() });

        await _host.DelegateAsync(threadId, "@factory Add an Orders class.");
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting, seconds: 120);

        // The model's calls all went through the host's gateway, with the right tools per turn.
        var requests = _host.Provider.Requests.ToArray();
        Assert.Equal(3, requests.Length); // write, submit_work, submit_review: nothing after a submission
        var implementTools = requests[0].Tools.Select(t => t.Name).ToArray();
        Assert.Contains("write_file", implementTools);
        Assert.Contains("submit_work", implementTools);
        Assert.Contains("request_decision", implementTools);
        Assert.DoesNotContain("submit_review", implementTools);
        var reviewTools = requests[2].Tools.Select(t => t.Name).ToArray();
        Assert.Equal(["read_file", "list_directory", "search_code", "submit_review"], reviewTools);
        Assert.All(requests, r => Assert.Equal("deepseek/deepseek-v4.1-flash", r.Model));
        Assert.All(requests, r => Assert.Equal(4_000, r.MaxOutputTokens));
        Assert.Contains("Add an Orders class.", requests[0].Messages[0].Content.OfType<Litos.Agent.Messages.TextBlock>().First().Text);

        // The review ran in its own session: it starts from its brief, not from the implement turn.
        Assert.NotEqual(requests[0].SessionId, requests[2].SessionId);
        Assert.Single(requests[2].Messages);

        // The worker's tool really wrote the file, and the host really committed and pushed it.
        Assert.Equal("public class Orders { }\n", File.ReadAllText(newFile).ReplaceLineEndings("\n"));
        var branch = details.Thread.Branch!;
        Assert.StartsWith("factory/", branch);
        Assert.Equal(details.LatestHandoff!.CommitSha, Git(_remote, "rev-parse", $"refs/heads/{branch}"));
        Assert.Equal("public class Orders { }", Git(_remote, "show", $"refs/heads/{branch}:src/Orders.cs"));
        Assert.Equal("Litos Software Factory", Git(_remote, "log", "-1", "--format=%an", $"refs/heads/{branch}"));
        Assert.Contains("Co-authored-by: Admin <admin@example.invalid>", Git(_remote, "log", "-1", "--format=%B", $"refs/heads/{branch}"));
        Assert.Equal(Git(_remote, "rev-parse", $"refs/heads/{branch}~1"), Git(_remote, "rev-parse", "refs/heads/main")); // main is untouched

        // The handoff uses what the agent submitted and what the host measured.
        var handoff = details.Messages.Single(m => m.Kind == MessageKind.Handoff);
        Assert.Contains("Ready for human testing.", handoff.Text);
        Assert.Contains("Added the Orders class.", handoff.PayloadJson);

        // Every provider call was charged to the task, and nothing is left reserved; the gateway's
        // own replies after a submission were neither reserved nor charged.
        var usage = await _host.Store.ListUsageAsync(threadId, default);
        Assert.Equal(3, usage.Count);
        Assert.All(usage, u => Assert.Equal(Core.Budget.UsageStatus.Settled, u.Status));
        Assert.Equal(usage.Sum(u => u.Charged), details.Thread.TokensUsed);
        Assert.Equal(0, details.Thread.TokensReserved);

        // The run's transcripts and worker log are in the factory data directory.
        var runDirectory = Path.Combine(_host.Options.DataDirectory, "runs", details.LatestRun!.Id.ToString("N"));
        Assert.True(File.Exists(Path.Combine(runDirectory, "worker.log")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(runDirectory, "sessions"), "*.jsonl", SearchOption.AllDirectories));
    }
}
