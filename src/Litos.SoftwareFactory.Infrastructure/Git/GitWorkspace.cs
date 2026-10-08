using System.Security.Cryptography;
using System.Text;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Git;

public sealed record GitWorkspaceOptions(string Path, string RemoteUrl)
{
    /// <summary>
    /// A token for the remote, or null when the remote needs none. It is handed to git through
    /// the process environment for the one command that needs it: never on a command line, never
    /// in the remote URL, and never written to the working copy's config, where a worker running
    /// in that directory could read it.
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>Only branches under this prefix can be created or pushed.</summary>
    public string TaskBranchPrefix { get; init; } = "factory/";

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// One project's factory-owned working copy, driven through the git CLI with argument arrays
/// (docs/software-factory/m1-architecture.md §5). The factory never force-pushes, rewrites
/// history, merges or pushes to the default branch — there is no code path here that could.
/// </summary>
public sealed class GitWorkspace(GitWorkspaceOptions options, IProcessRunner? processRunner = null) : IWorkspace
{
    private readonly IProcessRunner _processRunner = processRunner ?? new ProcessRunner();

    public string Path => options.Path;

    public async Task EnsureClonedAsync(CancellationToken ct)
    {
        if (Directory.Exists(System.IO.Path.Combine(options.Path, ".git")))
            return;

        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.Path))!;
        Directory.CreateDirectory(parent);
        await GitAsync(parent, ["clone", "--", options.RemoteUrl, options.Path], authenticated: true, ct);
    }

    // Fetch and push name the remote by its URL, not as "origin": the working copy's own config
    // is writable by the agent, and "origin" would resolve through whatever it says there.
    public Task FetchAsync(CancellationToken ct) =>
        GitAsync(options.Path, ["fetch", "--prune", options.RemoteUrl, "+refs/heads/*:refs/remotes/origin/*"], authenticated: true, ct);

    public async Task<string> CreateTaskBranchAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        RequireTaskBranch(branch, defaultBranch);
        await RequireCleanAsync(ct);

        var baseRef = $"origin/{defaultBranch}";
        var baseCommit = (await GitAsync(options.Path, ["rev-parse", "--verify", $"{baseRef}^{{commit}}"], authenticated: false, ct)).Trim();

        // -b, not -B: an existing branch of this name is someone's work and must not be reset.
        await GitAsync(options.Path, ["checkout", "--no-track", "-b", branch, baseRef], authenticated: false, ct);
        return baseCommit;
    }

    public async Task CheckoutAsync(string branch, CancellationToken ct)
    {
        await RequireCleanAsync(ct);
        await GitAsync(options.Path, ["checkout", branch], authenticated: false, ct);
    }

    public async Task CheckoutForReadingAsync(string branch, CancellationToken ct)
    {
        if (branch.Length == 0 || branch.StartsWith('-') || branch.Contains("..", StringComparison.Ordinal)
            || branch.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            throw new WorkspaceException($"'{branch}' is not a valid branch name.");
        }

        await GitAsync(options.Path, ["checkout", "--force", "--detach", $"origin/{branch}"], authenticated: false, ct);
        // Whatever an earlier reader's tools left behind; ignored files (build output) are kept.
        await GitAsync(options.Path, ["clean", "-fd"], authenticated: false, ct);
    }

    public async Task<WorkspaceStatus> GetStatusAsync(CancellationToken ct)
    {
        var branch = (await GitAsync(options.Path, ["rev-parse", "--abbrev-ref", "HEAD"], authenticated: false, ct)).Trim();
        var head = (await GitAsync(options.Path, ["rev-parse", "HEAD"], authenticated: false, ct)).Trim();
        var porcelain = await GitAsync(options.Path, ["status", "--porcelain=v1", "--untracked-files=all"], authenticated: false, ct);

        // Each line is "XY path" (or "XY old -> new" for a rename): two status letters and a space.
        var changed = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 3)
            .Select(line => line[3..].Split(" -> ")[^1].Trim('"'))
            .ToList();

        return new WorkspaceStatus(branch, head, changed.Count == 0, changed);
    }

    public async Task<WorkspaceSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(options.Path)) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in status.ChangedPaths.Take(WorkspaceSnapshot.MaxFiles))
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(options.Path, path));
            if (full.StartsWith(root, comparison))
                files[path] = await HashAsync(full, ct);
        }

        return new WorkspaceSnapshot(status.Branch, status.HeadCommit, files, Truncated: status.ChangedPaths.Count > WorkspaceSnapshot.MaxFiles);
    }

    /// <summary>A file's SHA-256. A symbolic link is hashed by where it points, never followed,
    /// so nothing outside the working copy is read.</summary>
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is { } target)
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("link:" + target)));
        if (!info.Exists)
            return Directory.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData("directory"u8)) : WorkspaceSnapshot.Deleted;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    public async Task<WorkspaceDiff> DiffAsync(string baseCommit, CancellationToken ct)
    {
        // Staging everything first is what makes new, still-untracked files part of the diff. It
        // costs nothing: the host commits the whole working copy at handoff anyway.
        await GitAsync(options.Path, ["add", "--all"], authenticated: false, ct);
        var patch = await GitAsync(
            options.Path,
            // The prefixes are pinned because the parser relies on them and a user's git config
            // (diff.noprefix, diff.mnemonicPrefix) can change them; renames are shown as an add
            // and a delete so every added line has a plain path.
            ["diff", "--cached", "--unified=0", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames",
                "--src-prefix=a/", "--dst-prefix=b/", baseCommit],
            authenticated: false, ct);
        return new WorkspaceDiff(baseCommit, patch, DiffParser.Parse(patch));
    }

    public async Task<string?> CommitAllAsync(string message, CommitIdentity author, string? coAuthoredBy, CancellationToken ct)
    {
        await GitAsync(options.Path, ["add", "--all"], authenticated: false, ct);

        // `diff --cached --quiet` exits 1 when something is staged.
        var staged = await RunAsync(options.Path, ["diff", "--cached", "--quiet"], authenticated: false, ct);
        if (staged.ExitCode == 0)
            return null;

        var fullMessage = coAuthoredBy is null ? message : $"{message.TrimEnd()}\n\nCo-authored-by: {coAuthoredBy}";
        await GitAsync(
            options.Path,
            ["-c", $"user.name={author.Name}", "-c", $"user.email={author.Email}", "commit", "--no-gpg-sign", "--no-verify", "-m", fullMessage],
            authenticated: false, ct);
        return (await GitAsync(options.Path, ["rev-parse", "HEAD"], authenticated: false, ct)).Trim();
    }

    public async Task<bool> SetAsideUncommittedChangesAsync(string label, CommitIdentity identity, CancellationToken ct)
    {
        if ((await GetStatusAsync(ct)).IsClean)
            return false;

        // A stash is a commit, so it needs an identity; --include-untracked takes new files
        // too, and leaves ignored files (build output) where they are.
        await GitAsync(
            options.Path,
            ["-c", $"user.name={identity.Name}", "-c", $"user.email={identity.Email}", "stash", "push", "--include-untracked", "-m", label],
            authenticated: false, ct);
        return true;
    }

    public async Task DiscardUncommittedChangesAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        RequireTaskBranch(branch, defaultBranch);

        // Only ever on the branch that is checked out: this must not switch branches with edits
        // in the tree, and must not clean a working copy that is on someone else's branch.
        var current = (await GitAsync(options.Path, ["rev-parse", "--abbrev-ref", "HEAD"], authenticated: false, ct)).Trim();
        if (current != branch)
            throw new WorkspaceException($"The working copy is on '{current}', not '{branch}'; nothing was discarded.");

        // HEAD, not a named commit: this drops edits, it never moves the branch.
        await GitAsync(options.Path, ["reset", "--hard", "HEAD"], authenticated: false, ct);
        // -d takes untracked directories too; without -x, ignored files (build output) stay.
        await GitAsync(options.Path, ["clean", "-fd"], authenticated: false, ct);
    }

    public async Task PushAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        RequireTaskBranch(branch, defaultBranch);

        // An explicit refspec with no leading "+": a push that is not a fast-forward is rejected
        // by the remote rather than forced.
        await GitAsync(options.Path, ["push", options.RemoteUrl, $"refs/heads/{branch}:refs/heads/{branch}"], authenticated: true, ct);
    }

    private void RequireTaskBranch(string branch, string defaultBranch)
    {
        if (string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceException($"The factory never creates or pushes the default branch '{defaultBranch}'.");
        if (!branch.StartsWith(options.TaskBranchPrefix, StringComparison.Ordinal) || branch.Length == options.TaskBranchPrefix.Length)
            throw new WorkspaceException($"'{branch}' is not a factory task branch: it must start with '{options.TaskBranchPrefix}'.");
        if (branch.Contains("..", StringComparison.Ordinal) || branch.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new WorkspaceException($"'{branch}' is not a valid branch name.");
    }

    private async Task RequireCleanAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (!status.IsClean)
        {
            throw new WorkspaceException(
                $"The working copy has uncommitted changes on '{status.Branch}' ({status.ChangedPaths.Count} path(s), first: {status.ChangedPaths[0]}).");
        }
    }

    private async Task<string> GitAsync(string workingDirectory, IReadOnlyList<string> arguments, bool authenticated, CancellationToken ct)
    {
        var result = await RunAsync(workingDirectory, arguments, authenticated, ct);
        if (result.Succeeded)
            return result.StandardOutput;

        var command = $"git {DescribeCommand(arguments)}";
        if (!result.Started)
            throw new WorkspaceException($"{command} could not be started: git was not found.");
        if (result.TimedOut)
            throw new WorkspaceException($"{command} timed out after {options.CommandTimeout.TotalSeconds:0} seconds.");
        throw new WorkspaceException($"{command} failed (exit code {result.ExitCode}): {Scrub(result.StandardError.Trim())}");
    }

    private Task<ProcessResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, bool authenticated, CancellationToken ct)
    {
        // Configuration passed through the environment outranks the repository's own config,
        // which the agent can edit. Hooks, the fsmonitor hook and the "ext" transport are ways a
        // repository's config makes git run a program, and the host runs git here with its own
        // environment — so they are switched off for every command.
        List<(string Key, string Value)> config =
        [
            ("core.quotepath", "false"), // paths are reported as written rather than octal-escaped
            ("core.hooksPath", NoHooksDirectory.Value),
            ("core.fsmonitor", "false"),
            ("protocol.ext.allow", "never"),
        ];

        if (authenticated && options.AccessToken is { Length: > 0 } token)
        {
            // Scoped to the real remote: if the repository's config rewrites the URL to some other
            // server (url.<x>.insteadOf), the header does not match it and is not sent there.
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            config.Add(($"http.{options.RemoteUrl}.extraheader", $"Authorization: Basic {basic}"));
        }

        var environment = new Dictionary<string, string?>
        {
            // Never stop to ask for a username or password: fail instead.
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_CONFIG_COUNT"] = config.Count.ToString(),
        };
        for (var i = 0; i < config.Count; i++)
        {
            environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        return _processRunner.RunAsync(
            new ProcessRequest("git", arguments, workingDirectory) { Timeout = options.CommandTimeout, Environment = environment }, ct);
    }

    /// <summary>An empty directory outside every working copy, for core.hooksPath to point at.</summary>
    private static readonly Lazy<string> NoHooksDirectory = new(() =>
        Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litos-factory-git-no-hooks")).FullName);

    /// <summary>The subcommand and its flags, without commit messages or identities.</summary>
    private static string DescribeCommand(IReadOnlyList<string> arguments)
    {
        var parts = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-c" or "-m")
            {
                i++; // skip the value
                continue;
            }

            parts.Add(arguments[i]);
        }

        return string.Join(' ', parts);
    }

    /// <summary>Belt and braces: git does not echo the header, but nothing leaving this class
    /// may carry the token.</summary>
    private string Scrub(string text) =>
        options.AccessToken is { Length: > 0 } token ? text.Replace(token, "***", StringComparison.Ordinal) : text;
}
