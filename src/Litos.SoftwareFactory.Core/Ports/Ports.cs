using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Ports;

// The side effects the domain needs, as interfaces. Infrastructure implements the ones here.
// The persistence ports (the factory store and the outbox event sink) are added with the host's
// database, where their shape is decided by the schema they sit on.

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

// ---- Git working copy ----

/// <summary>A git operation failed. Message is safe to show: it never contains a credential.</summary>
public sealed class WorkspaceException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record WorkspaceStatus(string Branch, string HeadCommit, bool IsClean, IReadOnlyList<string> ChangedPaths);

/// <summary>The lines a diff added or changed in one file, as 1-based line numbers in the new version.</summary>
public sealed record FileChange(string Path, IReadOnlyList<LineRange> AddedLines)
{
    public int AddedLineCount => AddedLines.Sum(r => r.Count);
}

public sealed record WorkspaceDiff(string BaseCommit, string Patch, IReadOnlyList<FileChange> Files)
{
    public int ChangedLineCount => Files.Sum(f => f.AddedLineCount);
}

public sealed record CommitIdentity(string Name, string Email);

/// <summary>
/// One project's factory-owned working copy (ReadMe_LitosSoftwareFactory_V1.md §6.1). Every
/// operation is one the host performs — the agent never commits or pushes. Nothing here can
/// force-push, rewrite history, merge or push to the default branch.
/// </summary>
public interface IWorkspace
{
    string Path { get; }

    /// <summary>Clones the repository if the working copy does not exist yet.</summary>
    Task EnsureClonedAsync(CancellationToken ct);

    Task FetchAsync(CancellationToken ct);

    /// <summary>Creates the task branch from the latest default branch and checks it out.
    /// Returns the base commit.</summary>
    Task<string> CreateTaskBranchAsync(string branch, string defaultBranch, CancellationToken ct);

    /// <summary>Checks out an existing task branch, for rework and resume.</summary>
    Task CheckoutAsync(string branch, CancellationToken ct);

    Task<WorkspaceStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Everything that differs from the base commit, committed or not.</summary>
    Task<WorkspaceDiff> DiffAsync(string baseCommit, CancellationToken ct);

    /// <summary>Stages everything and commits it. Returns the new commit, or null when there was
    /// nothing to commit.</summary>
    Task<string?> CommitAllAsync(string message, CommitIdentity author, string? coAuthoredBy, CancellationToken ct);

    /// <summary>Pushes the task branch. Refuses the default branch.</summary>
    Task PushAsync(string branch, string defaultBranch, CancellationToken ct);
}

// ---- GitHub ----

public sealed record PullRequestRef(int Number, string Url);

public sealed record PullRequestDraft(string Owner, string Repository, string Head, string Base, string Title, string Body);

public interface IGitHub
{
    /// <summary>Creates the draft PR for a task branch, or updates its title and body when one
    /// is already open for that branch.</summary>
    Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct);
}

// ---- Verification ----

public sealed record VerificationRequest(
    string WorkingCopy,
    VerificationProfile Profile,
    /// <summary>The lines the task changed, for changed-line coverage; null for a baseline run.</summary>
    IReadOnlyList<FileChange>? ChangedFiles,
    /// <summary>Where command logs are written.</summary>
    string LogDirectory);

public interface IVerifier
{
    /// <summary>Runs the profile and returns what its reports and exit codes establish.</summary>
    Task<VerificationOutcome> VerifyAsync(VerificationRequest request, CancellationToken ct);
}

// ---- Worker ----

public sealed record WorkerLaunch(
    string RunId, string WorkingCopy, string Provider, string Model, int? ContextLength,
    string DataDirectory, string HostUrl, string Secret);

/// <summary>A running worker process.</summary>
public interface IWorkerHandle : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>With the process id, identifies the process across a host restart (§16).</summary>
    DateTimeOffset StartTime { get; }

    /// <summary>The worker's loopback base address.</summary>
    Uri BaseAddress { get; }

    bool HasExited { get; }

    /// <summary>Hard cancel: kills the worker and everything it started.</summary>
    void Kill();
}

/// <summary>
/// The seam the sandbox tripwire relies on (§17): V1 starts a local process, and a container or
/// VM per run later is a new implementation of this, not a coordinator redesign.
/// </summary>
public interface IWorkerLauncher
{
    Task<IWorkerHandle> LaunchAsync(WorkerLaunch launch, CancellationToken ct);
}

/// <summary>What the host learned from one agent turn's event stream.</summary>
public sealed record TurnStreamResult(bool Completed, int ToolCalls, string? Error);

/// <summary>The host's calls into a worker (§15).</summary>
public interface IWorkerClient
{
    Task<TurnStreamResult> RunTurnAsync(string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct);

    /// <summary>Sends a steering message to the turn in progress.</summary>
    Task SteerAsync(string sessionId, string message, CancellationToken ct);

    /// <summary>Returns false when no turn was running.</summary>
    Task<bool> CancelAsync(string sessionId, CancellationToken ct);

    Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct);

    Task ShutdownAsync(CancellationToken ct);
}
