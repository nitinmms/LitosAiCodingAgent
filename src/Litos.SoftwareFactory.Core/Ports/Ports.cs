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

/// <summary>
/// What the working copy looked like at a checkpoint: the branch, its head commit, and a SHA-256
/// of every file that differed from that commit. A resumed run compares this with the working
/// copy now, so changes made while it was stopped are pointed out (§16).
/// </summary>
/// <param name="Files">Path to lower-case hex SHA-256; <see cref="Deleted"/> for a deleted file.</param>
/// <param name="Truncated">More files differed than were hashed.</param>
public sealed record WorkspaceSnapshot(string Branch, string Head, IReadOnlyDictionary<string, string> Files, bool Truncated = false)
{
    /// <summary>The hash recorded for a file that is changed by being deleted.</summary>
    public const string Deleted = "";

    /// <summary>The most files a snapshot hashes.</summary>
    public const int MaxFiles = 1_000;
}

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

    /// <summary>
    /// For a reading copy only (chat, m2-architecture.md §5): puts the working copy at the remote's
    /// <paramref name="branch"/> as fetched, detached, discarding anything a previous reader left.
    /// Never used on a task's working copy, whose edits it would throw away.
    /// </summary>
    Task CheckoutForReadingAsync(string branch, CancellationToken ct);

    Task<WorkspaceStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>The branch, the head commit and a hash of every file that differs from it, for
    /// a resumed run to compare with (§16). Never follows a path outside the working copy.</summary>
    Task<WorkspaceSnapshot> SnapshotAsync(CancellationToken ct);

    /// <summary>Everything that differs from the base commit, committed or not.</summary>
    Task<WorkspaceDiff> DiffAsync(string baseCommit, CancellationToken ct);

    /// <summary>Stages everything and commits it. Returns the new commit, or null when there was
    /// nothing to commit.</summary>
    Task<string?> CommitAllAsync(string message, CommitIdentity author, string? coAuthoredBy, CancellationToken ct);

    /// <summary>Pushes the task branch. Refuses the default branch.</summary>
    Task PushAsync(string branch, string defaultBranch, CancellationToken ct);

    /// <summary>
    /// Moves uncommitted edits out of the way without losing them, so the working copy is clean
    /// for another task: what a cancelled or abandoned task leaves behind. They go to the
    /// working copy's stash under <paramref name="label"/>, where a person can still get them
    /// back. Does nothing when the working copy is clean. Call it only while holding the
    /// repository, so the edits cannot be a running task's.
    /// </summary>
    /// <returns>True when something was set aside.</returns>
    Task<bool> SetAsideUncommittedChangesAsync(string label, CommitIdentity identity, CancellationToken ct);

    /// <summary>
    /// Throws away edits that were never committed, on the task branch: what a withdrawn rework
    /// run leaves behind. Commits are untouched — the branch stays at its last handoff — and
    /// ignored files such as build output are left alone. Refuses the default branch.
    /// </summary>
    Task DiscardUncommittedChangesAsync(string branch, string defaultBranch, CancellationToken ct);
}

// ---- GitHub ----

public sealed record PullRequestRef(int Number, string Url);

public sealed record PullRequestDraft(string Owner, string Repository, string Head, string Base, string Title, string Body);

public interface IGitHub
{
    /// <summary>Creates the draft PR for a task branch, or updates its title and body when one
    /// is already open for that branch.</summary>
    Task<PullRequestRef> CreateOrUpdateDraftPullRequestAsync(PullRequestDraft draft, CancellationToken ct);

    /// <summary>Where a pull request stands on GitHub now. People merge and close there, not in
    /// the factory, so this is asked rather than remembered.</summary>
    Task<PullRequestState> GetPullRequestStateAsync(string owner, string repository, int number, CancellationToken ct);
}

public enum PullRequestState
{
    Draft,
    Open,
    Merged,

    /// <summary>Closed without being merged.</summary>
    Closed,
}

// ---- Verification ----

public sealed record VerificationRequest(
    string WorkingCopy,
    VerificationProfile Profile,
    /// <summary>The lines the task changed, for changed-line coverage; null for a baseline run.</summary>
    IReadOnlyList<FileChange>? ChangedFiles,
    /// <summary>Where command logs are written.</summary>
    string LogDirectory)
{
    /// <summary>The run's own TEMP, TMP and TMPDIR for the commands, so runs going on at the same
    /// time never share temporary files; null keeps the host's.</summary>
    public string? TempDirectory { get; init; }
}

public interface IVerifier
{
    /// <summary>Runs the profile and returns what its reports and exit codes establish.</summary>
    Task<VerificationOutcome> VerifyAsync(VerificationRequest request, CancellationToken ct);
}

// ---- Worker ----

public sealed record WorkerLaunch(
    string RunId, string WorkingCopy, string Provider, string Model, int? ContextLength,
    string DataDirectory, string HostUrl, string Secret)
{
    /// <summary>Whether new sessions start with Programmatic Tool Calling on (the default, §8).</summary>
    public bool PtcEnabled { get; init; } = true;

    /// <summary>The run's own TEMP, TMP and TMPDIR for the worker and every command it starts;
    /// null keeps the host's.</summary>
    public string? TempDirectory { get; init; }
}

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
/// <param name="Reply">The text of the turn's last message that had any: a chat turn's answer.
/// Other turns report what they achieved through their completion tools, never through this.</param>
public sealed record TurnStreamResult(bool Completed, int ToolCalls, string? Error, string? Reply = null);

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

// ---- One host per database ----

/// <summary>
/// Held by the one host that may use the factory database. Startup recovery marks every Running
/// run of a previous host Interrupted, which would break the live runs of a second host on the
/// same database, so a second host must refuse to start (docs/software-factory/m2-architecture.md §3.5).
/// </summary>
public interface IHostInstanceLock : IAsyncDisposable
{
    /// <summary>Takes the lock for this host's lifetime. Returns why the host must not start, or null.</summary>
    Task<string?> AcquireAsync(CancellationToken ct);

    /// <summary>Whether the lock is still held; false once its connection has been lost, when
    /// another host may have taken it.</summary>
    Task<bool> IsHeldAsync(CancellationToken ct);
}

/// <summary>For a database only this process can reach (the tests' SQLite): nothing to guard.</summary>
public sealed class NoHostInstanceLock : IHostInstanceLock
{
    public Task<string?> AcquireAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> IsHeldAsync(CancellationToken ct) => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
