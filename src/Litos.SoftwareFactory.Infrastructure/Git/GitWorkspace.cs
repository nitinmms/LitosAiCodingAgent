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

    public Task FetchAsync(CancellationToken ct) =>
        GitAsync(options.Path, ["fetch", "--prune", "origin"], authenticated: true, ct);

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

    public async Task<WorkspaceDiff> DiffAsync(string baseCommit, CancellationToken ct)
    {
        // Staging everything first is what makes new, still-untracked files part of the diff. It
        // costs nothing: the host commits the whole working copy at handoff anyway.
        await GitAsync(options.Path, ["add", "--all"], authenticated: false, ct);
        var patch = await GitAsync(
            options.Path, ["diff", "--cached", "--unified=0", "--no-color", "--no-ext-diff", baseCommit], authenticated: false, ct);
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
            ["-c", $"user.name={author.Name}", "-c", $"user.email={author.Email}", "commit", "--no-gpg-sign", "-m", fullMessage],
            authenticated: false, ct);
        return (await GitAsync(options.Path, ["rev-parse", "HEAD"], authenticated: false, ct)).Trim();
    }

    public async Task PushAsync(string branch, string defaultBranch, CancellationToken ct)
    {
        RequireTaskBranch(branch, defaultBranch);

        // An explicit refspec with no leading "+": a push that is not a fast-forward is rejected
        // by the remote rather than forced.
        await GitAsync(options.Path, ["push", "origin", $"refs/heads/{branch}:refs/heads/{branch}"], authenticated: true, ct);
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
        var environment = new Dictionary<string, string?>
        {
            // Never stop to ask for a username or password: fail instead.
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            // Paths are reported as written rather than octal-escaped.
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.quotepath",
            ["GIT_CONFIG_VALUE_0"] = "false",
        };

        if (authenticated && options.AccessToken is { Length: > 0 } token)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            environment["GIT_CONFIG_COUNT"] = "2";
            environment["GIT_CONFIG_KEY_1"] = "http.extraheader";
            environment["GIT_CONFIG_VALUE_1"] = $"Authorization: Basic {basic}";
        }

        return _processRunner.RunAsync(
            new ProcessRequest("git", arguments, workingDirectory) { Timeout = options.CommandTimeout, Environment = environment }, ct);
    }

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
