using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Litos.SoftwareFactory.Host.Tests;

// ---- Fakes for the ports ----

/// <summary>A model that answers from a script, and records what it was asked.</summary>
public sealed class ScriptedProvider : IChatProvider, IChatProviderFactory
{
    private readonly ConcurrentQueue<Func<ChatRequest, CancellationToken, IAsyncEnumerable<AgentEvent>>> _script = new();

    public string ProviderName => "openrouter";

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public List<string> ResolvedNames { get; } = [];

    public IChatProvider Resolve(string providerName)
    {
        ResolvedNames.Add(providerName);
        return this;
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    public void Enqueue(Func<ChatRequest, CancellationToken, IAsyncEnumerable<AgentEvent>> response) => _script.Enqueue(response);

    public void EnqueueReply(string text, UsageInfo? usage = null) =>
        Enqueue((_, _) => Events(new TextDelta(text), new MessageCompleted(ChatMessage.Assistant([new TextBlock(text)]), usage ?? new UsageInfo(1_000, 100))));

    public void EnqueueToolCall(string tool, object arguments, UsageInfo? usage = null)
    {
        var json = JsonSerializer.SerializeToElement(arguments);
        Enqueue((_, _) => Events(
            new ToolCallStarted("call-1", tool), new ToolCallCompleted("call-1", tool, json),
            new MessageCompleted(ChatMessage.Assistant([new ToolUseBlock("call-1", tool, json)]), usage ?? new UsageInfo(1_000, 100))));
    }

    public void EnqueueThrow(Exception exception) => Enqueue((_, _) => Throwing(exception));

    public IAsyncEnumerable<AgentEvent> StreamAsync(ChatRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return _script.TryDequeue(out var response)
            ? response(request, ct)
            : Events(new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1_000, 100)));
    }

    public static async IAsyncEnumerable<AgentEvent> Events(params AgentEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

#pragma warning disable CS1998, CS0162
    private static async IAsyncEnumerable<AgentEvent> Throwing(Exception exception)
    {
        throw exception;
        yield break;
    }
#pragma warning restore CS1998, CS0162
}

/// <summary>A working copy that exists only in memory. "Files" are a dictionary the test's fake
/// worker edits; the diff is whatever differs from the content at branch creation.</summary>
public sealed class FakeWorkspace : IWorkspace
{
    private Dictionary<string, string> _base = new();
    private Dictionary<string, string> _committed = new() { ["README.md"] = "readme\n" };

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"litos-fake-workspace-{Guid.NewGuid():n}");

    public Dictionary<string, string> Files { get; } = new() { ["README.md"] = "readme\n" };

    public List<string> Calls { get; } = [];

    public string Branch { get; private set; } = "main";

    public string Head { get; private set; } = "base000";

    public List<(string Message, CommitIdentity Author, string? CoAuthor)> Commits { get; } = [];

    public List<string> Pushed { get; } = [];

    public Exception? FailFetch { get; set; }

    /// <summary>Branches the remote does not have: a reading checkout of one fails.</summary>
    public HashSet<string> MissingBranches { get; } = [];

    public Queue<Exception> FailPush { get; } = new();

    /// <summary>Awaited before "clone", "fetch" and "push" with that operation's token, so a test
    /// can hold the operation open; a cancelled token stops it, as it stops git.</summary>
    public Func<string, CancellationToken, Task>? Hold { get; set; }

    private async Task HoldAsync(string operation, CancellationToken ct)
    {
        if (Hold is { } hold)
            await hold(operation, ct);
        ct.ThrowIfCancellationRequested();
    }

    public async Task EnsureClonedAsync(CancellationToken ct)
    {
        await HoldAsync("clone", ct);
        Calls.Add("clone");
    }

    public async Task FetchAsync(CancellationToken ct)
    {
        await HoldAsync("fetch", ct);
        Calls.Add("fetch");
        if (FailFetch is not null)
            throw FailFetch;
    }

    /// <summary>As the real working copy does: it will not switch branches over uncommitted edits.</summary>
    private void RequireClean()
    {
        if (Uncommitted.Count > 0)
            throw new WorkspaceException($"The working copy has uncommitted changes on '{Branch}' ({Uncommitted.Count} path(s), first: {Uncommitted[0]}).");
    }

    public Task<string> CreateTaskBranchAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        RequireClean();
        Calls.Add($"branch {branch} from {defaultBranch}");
        // A task branch always starts from the default branch's head, which no task ever moves.
        Branch = branch;
        Head = "base000";
        _base = new Dictionary<string, string>(Files);
        _committed = new Dictionary<string, string>(Files);
        return Task.FromResult(Head);
    }

    public Task CheckoutAsync(string branch, CancellationToken ct)
    {
        RequireClean();
        Calls.Add($"checkout {branch}");
        Branch = branch;
        return Task.CompletedTask;
    }

    /// <summary>As the real reading copy does: the remote's branch, detached, whatever was left over.</summary>
    public Task CheckoutForReadingAsync(string branch, CancellationToken ct)
    {
        if (MissingBranches.Contains(branch))
            throw new WorkspaceException($"'origin/{branch}' is not a branch the remote has.");
        Calls.Add($"read {branch}");
        Branch = branch;
        Uncommitted = [];
        return Task.CompletedTask;
    }

    /// <summary>What the working copy held at each commit, so a diff can be taken from any of them.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _snapshots = new();

    private List<string> Changed(string? since = null)
    {
        var from = since is not null && _snapshots.TryGetValue(since, out var snapshot) ? snapshot : _base;
        return [.. Files.Where(f => !from.TryGetValue(f.Key, out var original) || original != f.Value).Select(f => f.Key).Order()];
    }

    public Task<WorkspaceStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new WorkspaceStatus(Branch, Head, Uncommitted.Count == 0, Uncommitted));

    private List<string> Uncommitted { get; set; } = [];

    /// <summary>Moves the head without the factory: what a person committing by hand does.</summary>
    public void SetHead(string head) => Head = head;

    public Task<WorkspaceSnapshot> SnapshotAsync(CancellationToken ct) => Task.FromResult(new WorkspaceSnapshot(
        Branch, Head,
        Uncommitted.ToDictionary(p => p, p => Files.TryGetValue(p, out var content) ? content : WorkspaceSnapshot.Deleted)));

    /// <summary>What the fake worker calls to "edit a file".</summary>
    public void Write(string path, string content)
    {
        Files[path] = content;
        if (!Uncommitted.Contains(path))
            Uncommitted.Add(path);
    }

    public Task<WorkspaceDiff> DiffAsync(string baseCommit, CancellationToken ct)
    {
        var changed = Changed(baseCommit);
        var patch = string.Concat(changed.Select(f => $"+++ b/{f}\n+{Files[f]}"));
        return Task.FromResult(new WorkspaceDiff(
            baseCommit, patch, [.. changed.Select(f => new FileChange(f, [new LineRange(1, Math.Max(1, Files[f].Split('\n').Length - 1))]))]));
    }

    public Task<string?> CommitAllAsync(string message, CommitIdentity author, string? coAuthoredBy, CancellationToken ct)
    {
        if (Uncommitted.Count == 0)
            return Task.FromResult<string?>(null);

        Commits.Add((message, author, coAuthoredBy));
        _committed = new Dictionary<string, string>(Files);
        Uncommitted = [];
        Head = $"commit{Commits.Count:000}";
        _snapshots[Head] = new Dictionary<string, string>(Files);
        return Task.FromResult<string?>(Head);
    }

    public async Task PushAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        await HoldAsync("push", ct);
        if (FailPush.TryDequeue(out var failure))
            throw failure;
        Pushed.Add($"{branch}@{Head}");
    }

    public List<string> SetAside { get; } = [];

    public Task<bool> SetAsideUncommittedChangesAsync(string label, CommitIdentity identity, CancellationToken ct)
    {
        if (Uncommitted.Count == 0)
            return Task.FromResult(false);

        Calls.Add($"set aside on {Branch}");
        SetAside.Add(label);
        foreach (var path in Uncommitted)
        {
            if (_committed.TryGetValue(path, out var content))
                Files[path] = content;
            else
                Files.Remove(path, out _);
        }

        Uncommitted = [];
        return Task.FromResult(true);
    }

    public Exception? FailDiscard { get; set; }

    public Task DiscardUncommittedChangesAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        Calls.Add($"discard {branch}");
        if (FailDiscard is not null)
            return Task.FromException(FailDiscard);

        // Back to what the last commit held: here, whatever was there before the uncommitted edits.
        foreach (var path in Uncommitted)
        {
            if (_committed.TryGetValue(path, out var content))
                Files[path] = content;
            else
                Files.Remove(path, out _);
        }

        Uncommitted = [];
        return Task.CompletedTask;
    }
}

public sealed class FakeWorkspaceProvider : IWorkspaceProvider
{
    public ConcurrentDictionary<Guid, FakeWorkspace> Workspaces { get; } = new();

    public IWorkspace For(Project project) => Workspaces.GetOrAdd(project.Id, _ => new FakeWorkspace());

    public FakeWorkspace Of(Guid projectId) => Workspaces.GetOrAdd(projectId, _ => new FakeWorkspace());

    public ConcurrentDictionary<Guid, FakeWorkspace> ReadingCopies { get; } = new();

    public IWorkspace ReadingCopyFor(Project project) => ReadingOf(project.Id);

    public FakeWorkspace ReadingOf(Guid projectId) => ReadingCopies.GetOrAdd(projectId, _ => new FakeWorkspace());
}

/// <summary>Answers each verification from a queue; the default passes.</summary>
public sealed class ScriptedVerifier : IVerifier
{
    public Queue<VerificationOutcome> Outcomes { get; } = new();

    public List<VerificationRequest> Requests { get; } = [];

    public VerificationOutcome Baseline { get; set; } = Passing("Existing.Test");

    /// <summary>Awaited before answering, with the verification's token, so a test can hold a
    /// verification open; a cancelled token stops it, as it kills the real commands.</summary>
    public Func<VerificationRequest, CancellationToken, Task>? Hold { get; set; }

    public async Task<VerificationOutcome> VerifyAsync(VerificationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Hold is { } hold)
            await hold(request, ct);
        ct.ThrowIfCancellationRequested();
        if (request.ChangedFiles is null)
            return Baseline;
        return Outcomes.Count > 0 ? Outcomes.Dequeue() : Passing("Existing.Test", "New.Test");
    }

    public static VerificationOutcome Passing(params string[] tests) => new(
        BuildStatus.Passed, UnitTestStatus.Passed, CoverageStatus.Met,
        [.. tests.Select(t => new TestCaseResult(t, TestOutcome.Passed))], new ChangedLineCoverage(9, 10, []),
        [new CommandRun("dotnet", "unitTests", "dotnet test", 0, TimeSpan.FromSeconds(2), false)], []);

    public static VerificationOutcome Failing(params string[] failing) => new(
        BuildStatus.Passed, UnitTestStatus.Failed, CoverageStatus.NotMeasured,
        [new TestCaseResult("Existing.Test", TestOutcome.Passed), .. failing.Select(t => new TestCaseResult(t, TestOutcome.Failed, "boom"))], null, [], []);
}

public sealed class FakeGitHub : IGitHub
{
    public List<PullRequestDraft> Drafts { get; } = [];

    public Exception? Fail { get; set; }

    public Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct)
    {
        Drafts.Add(draft);
        return Fail is null
            ? Task.FromResult(new PullRequestRef(212, $"https://github.com/{draft.Owner}/{draft.Repository}/pull/212"))
            : Task.FromException<PullRequestRef>(Fail);
    }

    public PullRequestState State { get; set; } = PullRequestState.Draft;

    public Exception? FailState { get; set; }

    public List<string> StateRequests { get; } = [];

    public Task<PullRequestState> GetPullRequestStateAsync(string owner, string repository, int number, CancellationToken ct)
    {
        StateRequests.Add($"{owner}/{repository}#{number}");
        return FailState is null ? Task.FromResult(State) : Task.FromException<PullRequestState>(FailState);
    }
}

public sealed class FakeUserDirectory : IUserDirectory
{
    public Task<string?> CoAuthorAsync(Guid userId, CancellationToken ct) => Task.FromResult<string?>("Admin <admin@example.invalid>");
}

/// <summary>One turn the host asked the fake worker to run.</summary>
public sealed record TurnCall(FakeWorker Worker, string SessionId, TurnKind Kind, string Brief, CancellationToken Token)
{
    /// <summary>The tool-call limit the host started the turn with.</summary>
    public int MaxToolCalls { get; init; }
}

/// <summary>
/// Stands in for the worker process. Each turn is answered by the next scripted behaviour, which
/// can edit the fake working copy and report through the host's real callback endpoints, with
/// the run's real secret — exactly what the real worker's completion tools do.
/// </summary>
public sealed class FakeWorker(WorkerLaunch launch, FakeWorkerLauncher owner) : IWorkerHandle, IWorkerClient
{
    private readonly HttpClient _http = CreateClient(launch);

    public WorkerLaunch Launch => launch;

    public int ProcessId { get; } = Random.Shared.Next(100_000, 900_000);

    public DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;

    public Uri BaseAddress { get; } = new("http://127.0.0.1:1/");

    public bool HasExited { get; private set; }

    public bool ShutdownRequested { get; private set; }

    public List<string> Steered { get; } = [];

    public List<(string Session, string Instruction)> Compactions { get; } = [];

    private static HttpClient CreateClient(WorkerLaunch launch)
    {
        var http = new HttpClient { BaseAddress = new Uri(launch.HostUrl) };
        http.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, launch.Secret);
        return http;
    }

    public void Kill() => HasExited = true;

    public ValueTask DisposeAsync()
    {
        HasExited = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Posts a completion tool's payload to the host, as the real worker does.</summary>
    public async Task<SubmissionResponse> SubmitAsync(string sessionId, Submission submission)
    {
        using var response = await _http.PostAsJsonAsync(
            FactoryWire.SubmissionsPath(launch.RunId), new SubmissionRequest(sessionId, submission), FactoryWire.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SubmissionResponse>(FactoryWire.Json))!;
    }

    /// <summary>Makes one model call through the host's gateway and returns the events it streamed.</summary>
    public async Task<List<GatewayEvent>> CallModelAsync(string sessionId, string text = "hello")
    {
        var request = new GatewayRequest(Guid.NewGuid().ToString(), new ChatRequest([ChatMessage.User(text)], [], "ignored", SessionId: sessionId));
        using var response = await _http.PostAsJsonAsync(FactoryWire.GatewayPath(launch.RunId), request, FactoryWire.Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<GatewayEvent>(line, FactoryWire.Json)!)];
    }

    public async Task<TurnStreamResult> RunTurnAsync(string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct)
    {
        var call = new TurnCall(this, sessionId, kind, brief, ct) { MaxToolCalls = maxToolCalls };
        owner.Turns.Enqueue(call);
        var behaviour = owner.Script.TryDequeue(out var scripted) ? scripted : owner.Default;
        return await behaviour(call);
    }

    public Task SteerAsync(string sessionId, string message, CancellationToken ct)
    {
        Steered.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> CancelAsync(string sessionId, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct)
    {
        Compactions.Add((sessionId, instruction));
        return Task.FromResult(true);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        ShutdownRequested = true;
        if (owner.ShutdownGate is { } gate)
            await gate;
    }
}

public sealed class FakeWorkerLauncher(FakeWorkspaceProvider workspaces) : IWorkerLauncher, IWorkerClientFactory
{
    public ConcurrentQueue<Func<TurnCall, Task<TurnStreamResult>>> Script { get; } = new();

    public ConcurrentQueue<TurnCall> Turns { get; } = new();

    public ConcurrentQueue<FakeWorker> Workers { get; } = new();

    public Exception? FailLaunch { get; set; }

    /// <summary>While set, a worker asked to shut down does not finish shutting down until this
    /// completes: a run that has stopped is then still tearing down.</summary>
    public Task? ShutdownGate { get; set; }

    /// <summary>What a turn does when the script is empty: an implement turn edits a file and
    /// submits work, a review turn submits a clean review.</summary>
    public Func<TurnCall, Task<TurnStreamResult>> Default { get; set; } = null!;

    public FakeWorkspace WorkspaceOf(FakeWorker worker) =>
        workspaces.Workspaces.Values.First(w => w.Path == worker.Launch.WorkingCopy);

    public Task<IWorkerHandle> LaunchAsync(WorkerLaunch launch, CancellationToken ct)
    {
        if (FailLaunch is not null)
            return Task.FromException<IWorkerHandle>(FailLaunch);

        var worker = new FakeWorker(launch, this);
        Workers.Enqueue(worker);
        return Task.FromResult<IWorkerHandle>(worker);
    }

    /// <summary>The worker launched with this secret: with more than one run at a time, the last
    /// one launched may belong to another run.</summary>
    public IWorkerClient Create(Uri baseAddress, string secret) => Workers.Last(w => w.Launch.Secret == secret);

    public static TurnStreamResult Done(int toolCalls = 3) => new(true, toolCalls, null);

    public static WorkSubmission Work(string summary = "Added CSV export for Orders.") => new(
        summary, [new CriterionCoverage("Administrators can export.", ["Export_Admin_Succeeds"])], ["Export_Admin_Succeeds"],
        [], ["Sign in as an administrator and export"]);
}

// ---- The host under test ----

/// <summary>
/// The real factory host, listening on a loopback port, with SQLite for its store and fakes for
/// everything outside it: the model, git, verification, GitHub and the worker process.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string AdminPassword = "correct-horse-battery";

    private readonly SqliteConnection _connection;
    private int _edits;

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public FactoryOptions Options { get; }

    public ScriptedProvider Provider { get; } = new();

    public FakeWorkspaceProvider Workspaces { get; } = new();

    public ScriptedVerifier Verifier { get; } = new();

    public FakeGitHub GitHub { get; } = new();

    public FakeWorkerLauncher Workers { get; }

    public IFactoryStore Store => App.Services.GetRequiredService<IFactoryStore>();

    public Uri Url { get; private set; } = null!;

    private TestHost(Action<FactoryOptions>? configure, Action<IServiceCollection>? services, string dataDirectory)
    {
        var connectionString = $"Data Source=host-{Guid.NewGuid():n};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        Workers = new FakeWorkerLauncher(Workspaces);
        Workers.Default = DefaultTurnAsync;

        Options = new FactoryOptions
        {
            DataDirectory = dataDirectory,
            OpenRouterApiKey = "test-key",
            AdminPassword = AdminPassword,
            PollInterval = TimeSpan.FromMilliseconds(100),
            // The M1 tests run one task at a time; the concurrency tests set their own cap.
            SlotCap = 1,
            Budget = new BudgetPolicy { OutputAllowanceTokens = 4_000, Margin = 0.10 },
        };
        // The scripted changes are small and clean, so the planner would skip their review; the
        // tests of the review pipeline need one. NoReviewRunTests turns this back on.
        Options.Limits = Options.Limits with { AllowNoReview = false };
        configure?.Invoke(Options);

        App = FactoryHostApp.Build(["--urls", "http://127.0.0.1:0"], Options, s =>
        {
            s.AddDbContextFactory<FactoryDbContext>(o => o.UseSqlite(connectionString));
            s.AddSingleton<IFactoryStore, EfFactoryStore>();
            s.AddSingleton<IChatProviderFactory>(Provider);
            s.AddSingleton<IWorkspaceProvider>(Workspaces);
            s.AddSingleton<IVerifier>(Verifier);
            s.AddSingleton<IGitHub>(GitHub);
            s.AddSingleton<IWorkerLauncher>(Workers);
            s.AddSingleton<IWorkerClientFactory>(Workers);
            s.AddSingleton<IUserDirectory, FakeUserDirectory>();
            s.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
            services?.Invoke(s);
        });

        Client = NewClient();
    }

    public static async Task<TestHost> StartAsync(
        Action<FactoryOptions>? configure = null, Action<IServiceCollection>? services = null, bool signIn = true, bool startCoordinator = true)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("litos-factory-host-").FullName;
        var host = new TestHost(configure, s =>
        {
            if (!startCoordinator)
            {
                // The API under test, with nothing claiming queued work behind it.
                foreach (var hosted in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList())
                    s.Remove(hosted);
            }

            services?.Invoke(s);
        }, dataDirectory);

        await using (var db = await host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        Assert.Null(await FactoryHostApp.PrepareAsync(host.App, host.Options, default));
        await host.App.StartAsync();
        host.Url = new Uri(host.App.Urls.First());
        host.Client.BaseAddress = host.Url;
        if (startCoordinator)
            await host.App.Services.GetRequiredService<RunCoordinator>().Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (signIn)
            await host.SignInAsync(host.Client);
        return host;
    }

    /// <summary>A browser: its own cookie jar, and the CSRF header the SPA always sends.</summary>
    public HttpClient NewClient(bool csrfHeader = true)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = Url };
        if (csrfHeader)
            client.DefaultRequestHeaders.Add(FactoryAuth.CsrfHeader, "1");
        return client;
    }

    public async Task SignInAsync(HttpClient client, string user = "admin", string password = AdminPassword)
    {
        using var response = await client.PostAsJsonAsync("api/auth/login", new { userName = user, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public const string MemberPassword = "a-member-password";

    /// <summary>A Member account, belonging to the given projects only.</summary>
    public async Task<Guid> CreateMemberAsync(string userName, params Guid[] projectIds)
    {
        using var scope = App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<FactoryUser>>();
        var user = new FactoryUser { UserName = userName, DisplayName = userName };
        var created = await users.CreateAsync(user, MemberPassword);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, FactoryRoles.Member);

        var admin = await AdminIdAsync();
        foreach (var projectId in projectIds)
            await Store.AddMemberAsync(projectId, user.Id, admin, DateTimeOffset.UtcNow, default);
        return user.Id;
    }

    /// <summary>A browser signed in as that Member.</summary>
    public async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = NewClient();
        await SignInAsync(client, userName, MemberPassword);
        return client;
    }

    public async Task<Guid> AdminIdAsync()
    {
        using var response = await Client.GetAsync("api/auth/me");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public async Task<JsonElement> PostAsync(string path, object? body, HttpStatusCode expected)
    {
        using var response = await Client.PostAsJsonAsync(path, body ?? new { });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST {path}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    public async Task<Guid> RegisterProjectAsync(string repository = "salesapp")
    {
        var project = await PostAsync("api/projects", new { gitHubUrl = $"https://github.com/acme/{repository}", preset = "dotnet" }, HttpStatusCode.Created);
        return project.GetProperty("id").GetGuid();
    }

    public async Task<Guid> CreateThreadAsync(Guid projectId, string title = "Add CSV export", long? budgetCap = null)
    {
        var thread = await PostAsync("api/threads", new { projectId, title, typeLabel = "feature", budgetCap }, HttpStatusCode.Created);
        return thread.GetProperty("id").GetGuid();
    }

    public Task<JsonElement> DelegateAsync(Guid threadId, string text = "@factory Add CSV export for Orders.", string? messageId = null, HttpStatusCode expected = HttpStatusCode.Accepted) =>
        PostAsync($"api/threads/{threadId}/messages", new { messageId = messageId ?? Guid.NewGuid().ToString(), text }, expected);

    public async Task<ThreadDetails> ThreadAsync(Guid threadId) => (await Store.GetThreadAsync(threadId, default))!;

    /// <summary>Waits until the thread reaches a state, or fails with what it was doing instead.</summary>
    public async Task<ThreadDetails> WaitForStateAsync(Guid threadId, LifecycleState state, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var details = await ThreadAsync(threadId);
            if (details.Thread.State == state && App.Services.GetRequiredService<RunRegistry>().FindByThread(threadId) is null == (state != LifecycleState.Running))
                return details;
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Thread is {details.Thread.State} ({details.Thread.StateReason}), not {state}. Messages: " +
                    string.Join(" | ", details.Messages.Select(m => $"[{m.Kind}] {m.Text}")));
            }

            await Task.Delay(40);
        }
    }

    public const string DefaultChatReply = "Orders are exported from src/Orders.cs.";

    /// <summary>The behaviour of a well-behaved agent: do the work for the kind of turn it is.</summary>
    public async Task<TurnStreamResult> DefaultTurnAsync(TurnCall call)
    {
        switch (call.Kind)
        {
            case TurnKind.Review:
                await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission([]));
                break;
            case TurnKind.Scan:
                await call.Worker.SubmitAsync(call.SessionId, new PlanSubmission("Change Orders.", ["src/Orders.cs"], []));
                break;
            case TurnKind.Chat:
                return new TurnStreamResult(true, 1, null, DefaultChatReply);
            default:
                Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", $"edit {Interlocked.Increment(ref _edits)}\n");
                await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work());
                break;
        }

        return FakeWorkerLauncher.Done();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            Directory.Delete(Options.DataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
