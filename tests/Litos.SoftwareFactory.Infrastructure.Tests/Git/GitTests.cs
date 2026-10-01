using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Git;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Git;

public class DiffParserTests
{
    [Fact]
    public void Parse_ZeroContextDiff_GivesAddedRangesInTheNewFile()
    {
        const string patch = """
            diff --git a/src/Orders.cs b/src/Orders.cs
            index 1111111..2222222 100644
            --- a/src/Orders.cs
            +++ b/src/Orders.cs
            @@ -10,0 +11,3 @@ public class Orders
            +    public void Export()
            +    {
            +    }
            @@ -40 +44 @@ public class Orders
            -    old line
            +    new line
            """;

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal("src/Orders.cs", file.Path);
        Assert.Equal([new LineRange(11, 13), new LineRange(44, 44)], file.AddedLines);
        Assert.Equal(4, file.AddedLineCount);
    }

    [Fact]
    public void Parse_DiffWithContext_CountsOnlyAddedLines()
    {
        const string patch = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,5 +1,6 @@
             one
             two
            +two and a half
             three
            -four
            +FOUR
             five
            """;

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal([new LineRange(3, 3), new LineRange(5, 5)], file.AddedLines);
    }

    [Fact]
    public void Parse_NewFile_IsOneRangeFromLineOne()
    {
        const string patch = """
            diff --git a/tests/NewTests.cs b/tests/NewTests.cs
            new file mode 100644
            index 0000000..3333333
            --- /dev/null
            +++ b/tests/NewTests.cs
            @@ -0,0 +1,4 @@
            +line 1
            +line 2
            +line 3
            +line 4
            """;

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal("tests/NewTests.cs", file.Path);
        Assert.Equal([new LineRange(1, 4)], file.AddedLines);
    }

    [Fact]
    public void Parse_DeletedFile_HasNoLinesInTheNewVersion_SoItIsLeftOut()
    {
        const string patch = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            --- a/old.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -gone
            -also gone
            """;

        Assert.Empty(DiffParser.Parse(patch));
    }

    [Fact]
    public void Parse_PureDeletionInAFile_AddsNoLines()
    {
        const string patch = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -3,2 +2,0 @@
            -removed
            -removed too
            """;

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Empty(file.AddedLines);
        Assert.Equal(0, file.AddedLineCount);
    }

    [Fact]
    public void Parse_SeveralFiles_KeepsThemApart()
    {
        const string patch = """
            diff --git a/one.txt b/one.txt
            --- a/one.txt
            +++ b/one.txt
            @@ -1 +1 @@
            -a
            +b
            diff --git a/two.txt b/two.txt
            --- a/two.txt
            +++ b/two.txt
            @@ -5,0 +6,2 @@
            +x
            +y
            """;

        var files = DiffParser.Parse(patch);

        Assert.Equal(["one.txt", "two.txt"], files.Select(f => f.Path));
        Assert.Equal([new LineRange(1, 1)], files[0].AddedLines);
        Assert.Equal([new LineRange(6, 7)], files[1].AddedLines);
    }

    /// <summary>An added line whose own text starts with "++ " looks like a file header
    /// ("+++ ...") once the diff's "+" is in front of it; inside a hunk it is just a line.</summary>
    [Fact]
    public void Parse_AddedLineThatLooksLikeAFileHeader_IsCountedAsALine()
    {
        const string patch = """
            diff --git a/notes.md b/notes.md
            --- a/notes.md
            +++ b/notes.md
            @@ -1,0 +2,2 @@
            +++ b/not-a-file
            +real line
            """;

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal("notes.md", file.Path);
        Assert.Equal([new LineRange(2, 3)], file.AddedLines);
    }

    [Fact]
    public void Parse_NoNewlineMarker_IsNotALine()
    {
        const string patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n";

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal([new LineRange(1, 1)], file.AddedLines);
    }

    [Fact]
    public void Parse_QuotedPathAndWindowsLineEndings_AreHandled()
    {
        const string patch = "diff --git \"a/dir/with space.txt\" \"b/dir/with space.txt\"\r\n--- \"a/dir/with space.txt\"\r\n+++ \"b/dir/with space.txt\"\r\n@@ -0,0 +1 @@\r\n+x\r\n";

        var file = Assert.Single(DiffParser.Parse(patch));

        Assert.Equal("dir/with space.txt", file.Path);
        Assert.Equal([new LineRange(1, 1)], file.AddedLines);
    }

    [Fact]
    public void Parse_EmptyPatch_IsEmpty()
    {
        Assert.Empty(DiffParser.Parse(""));
    }

    [Fact]
    public void Parse_BinaryFile_HasNoLineRanges()
    {
        const string patch = """
            diff --git a/logo.png b/logo.png
            index 1111111..2222222 100644
            Binary files a/logo.png and b/logo.png differ
            """;

        Assert.Empty(DiffParser.Parse(patch));
    }
}

/// <summary>
/// GitWorkspace against real repositories in a scratch directory: a bare "remote" stands in for
/// GitHub, so clone, fetch, branch, commit and push all run for real.
/// </summary>
public class GitWorkspaceTests : IAsyncLifetime
{
    private static readonly CommitIdentity Factory = new("Litos Factory", "factory@litos.invalid");

    private readonly TempDirectory _temp = new();
    private readonly ProcessRunner _runner = new();
    private string _remote = "";
    private string _seed = "";
    private GitWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _remote = _temp.Combine("remote.git");
        _seed = _temp.Combine("seed");
        await GitAsync(_temp.Path, "init", "--bare", "--initial-branch=main", _remote);
        await GitAsync(_temp.Path, "clone", _remote, _seed);
        File.WriteAllText(Path.Combine(_seed, "README.md"), "line 1\nline 2\nline 3\n");
        await SeedCommitAsync("Initial commit");
        await GitAsync(_seed, "push", "origin", "HEAD:refs/heads/main");

        _workspace = new GitWorkspace(new GitWorkspaceOptions(_temp.Combine("workspaces", "project-1"), _remote));
        await _workspace.EnsureClonedAsync(default);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await _runner.RunAsync(new ProcessRequest("git", arguments, directory), default);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private async Task SeedCommitAsync(string message)
    {
        await GitAsync(_seed, "add", "--all");
        await GitAsync(_seed, "-c", "user.name=Seeder", "-c", "user.email=seed@example.invalid", "commit", "--no-gpg-sign", "-m", message);
    }

    private string InWorkspace(string relativePath) => Path.Combine(_workspace.Path, relativePath);

    // ---- Clone and fetch ----

    [Fact]
    public async Task EnsureClonedAsync_ClonesTheRepository()
    {
        Assert.True(File.Exists(InWorkspace("README.md")));
        Assert.Equal("main", (await _workspace.GetStatusAsync(default)).Branch);
    }

    [Fact]
    public async Task EnsureClonedAsync_AlreadyCloned_LeavesTheWorkingCopyAlone()
    {
        File.WriteAllText(InWorkspace("local-only.txt"), "keep me");

        await _workspace.EnsureClonedAsync(default);

        Assert.True(File.Exists(InWorkspace("local-only.txt")));
    }

    [Fact]
    public async Task EnsureClonedAsync_UnreachableRemote_ThrowsWorkspaceException()
    {
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_temp.Combine("workspaces", "bad"), _temp.Combine("no-such-remote.git")));

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => workspace.EnsureClonedAsync(default));

        Assert.Contains("git clone", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_BringsInNewCommitsOnTheDefaultBranch()
    {
        File.WriteAllText(Path.Combine(_seed, "later.txt"), "added later");
        await SeedCommitAsync("Later commit");
        await GitAsync(_seed, "push", "origin", "HEAD:refs/heads/main");
        var remoteHead = await GitAsync(_seed, "rev-parse", "HEAD");

        await _workspace.FetchAsync(default);
        var baseCommit = await _workspace.CreateTaskBranchAsync("factory/7f3a-later", "main", default);

        Assert.Equal(remoteHead, baseCommit);
        Assert.True(File.Exists(InWorkspace("later.txt"))); // branched from the latest default branch
    }

    // ---- Task branches ----

    [Fact]
    public async Task CreateTaskBranchAsync_ChecksOutANewBranchFromTheDefaultBranch_AndReturnsTheBaseCommit()
    {
        var mainHead = await GitAsync(_workspace.Path, "rev-parse", "origin/main");

        var baseCommit = await _workspace.CreateTaskBranchAsync("factory/7f3a-csv-export", "main", default);

        var status = await _workspace.GetStatusAsync(default);
        Assert.Equal(mainHead, baseCommit);
        Assert.Equal("factory/7f3a-csv-export", status.Branch);
        Assert.Equal(mainHead, status.HeadCommit);
        Assert.True(status.IsClean);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_DirtyWorkingCopy_IsRejected()
    {
        File.WriteAllText(InWorkspace("stray.txt"), "left behind");

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.CreateTaskBranchAsync("factory/x", "main", default));

        Assert.Contains("uncommitted changes", ex.Message);
        Assert.Contains("stray.txt", ex.Message);
        Assert.Equal("main", (await _workspace.GetStatusAsync(default)).Branch);
    }

    [Fact]
    public async Task CreateTaskBranchAsync_BranchAlreadyExists_IsRejected_RatherThanReset()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("work.txt"), "work");
        var commit = await _workspace.CommitAllAsync("Work", Factory, null, default);
        await _workspace.CheckoutAsync("main", default);

        await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.CreateTaskBranchAsync("factory/x", "main", default));

        Assert.Equal(commit, await GitAsync(_workspace.Path, "rev-parse", "factory/x")); // its work is still there
    }

    [Theory]
    [InlineData("main")]
    [InlineData("MAIN")]
    [InlineData("feature/not-factory")]
    [InlineData("factory/")]
    [InlineData("factory/has space")]
    [InlineData("factory/a..b")]
    [InlineData("factory/a:b")]
    public async Task CreateTaskBranchAsync_NotAFactoryTaskBranch_IsRejected(string branch)
    {
        await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.CreateTaskBranchAsync(branch, "main", default));

        Assert.Equal("main", (await _workspace.GetStatusAsync(default)).Branch);
    }

    [Fact]
    public async Task CheckoutAsync_ReturnsToAnExistingTaskBranch_ForRework()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("work.txt"), "work");
        await _workspace.CommitAllAsync("Work", Factory, null, default);
        await _workspace.CheckoutAsync("main", default);
        Assert.False(File.Exists(InWorkspace("work.txt")));

        await _workspace.CheckoutAsync("factory/x", default);

        Assert.Equal("factory/x", (await _workspace.GetStatusAsync(default)).Branch);
        Assert.True(File.Exists(InWorkspace("work.txt")));
    }

    [Fact]
    public async Task CheckoutAsync_DirtyWorkingCopy_IsRejected()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("uncommitted.txt"), "work in progress");

        await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.CheckoutAsync("main", default));

        Assert.True(File.Exists(InWorkspace("uncommitted.txt"))); // nothing was thrown away
    }

    // ---- Status ----

    [Fact]
    public async Task GetStatusAsync_ListsModifiedNewAndDeletedPaths()
    {
        File.WriteAllText(InWorkspace("README.md"), "changed\n");
        Directory.CreateDirectory(InWorkspace("src"));
        File.WriteAllText(InWorkspace("src/New File.cs"), "new");

        var status = await _workspace.GetStatusAsync(default);

        Assert.False(status.IsClean);
        Assert.Equal(new[] { "README.md", "src/New File.cs" }.Order(), status.ChangedPaths.Order());
    }

    // ---- Diff ----

    [Fact]
    public async Task DiffAsync_CoversCommittedUncommittedAndUntrackedChanges_AgainstTheBaseCommit()
    {
        var baseCommit = await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("README.md"), "line 1\nline 2 changed\nline 3\nline 4\n");
        await _workspace.CommitAllAsync("Committed change", Factory, null, default);
        Directory.CreateDirectory(InWorkspace("src"));
        File.WriteAllText(InWorkspace("src/Orders.cs"), "a\nb\nc\n"); // untracked

        var diff = await _workspace.DiffAsync(baseCommit, default);

        Assert.Equal(baseCommit, diff.BaseCommit);
        Assert.Equal(new[] { "README.md", "src/Orders.cs" }, diff.Files.Select(f => f.Path).Order());
        Assert.Equal([new LineRange(2, 2), new LineRange(4, 4)], diff.Files.Single(f => f.Path == "README.md").AddedLines);
        Assert.Equal([new LineRange(1, 3)], diff.Files.Single(f => f.Path == "src/Orders.cs").AddedLines);
        Assert.Equal(5, diff.ChangedLineCount);
        Assert.Contains("+line 2 changed", diff.Patch);
    }

    [Fact]
    public async Task DiffAsync_NothingChanged_IsEmpty()
    {
        var baseCommit = await _workspace.CreateTaskBranchAsync("factory/x", "main", default);

        var diff = await _workspace.DiffAsync(baseCommit, default);

        Assert.Empty(diff.Files);
        Assert.Equal(0, diff.ChangedLineCount);
    }

    // ---- Commit ----

    [Fact]
    public async Task CommitAllAsync_CommitsEverything_AsTheFactory_WithACoAuthorTrailer()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("README.md"), "changed\n");
        File.WriteAllText(InWorkspace("new.txt"), "new\n");

        var commit = await _workspace.CommitAllAsync("Add CSV export", Factory, "Nitin Sharma <nitin@example.invalid>", default);

        Assert.NotNull(commit);
        Assert.True((await _workspace.GetStatusAsync(default)).IsClean);
        Assert.Equal("Litos Factory <factory@litos.invalid>", await GitAsync(_workspace.Path, "log", "-1", "--format=%an <%ae>"));
        var message = await GitAsync(_workspace.Path, "log", "-1", "--format=%B");
        Assert.StartsWith("Add CSV export", message);
        Assert.Contains("Co-authored-by: Nitin Sharma <nitin@example.invalid>", message);
        Assert.Equal(commit, (await _workspace.GetStatusAsync(default)).HeadCommit);
    }

    [Fact]
    public async Task CommitAllAsync_NothingToCommit_ReturnsNull_AndCreatesNoCommit()
    {
        var baseCommit = await _workspace.CreateTaskBranchAsync("factory/x", "main", default);

        var commit = await _workspace.CommitAllAsync("Nothing", Factory, null, default);

        Assert.Null(commit);
        Assert.Equal(baseCommit, (await _workspace.GetStatusAsync(default)).HeadCommit);
    }

    [Fact]
    public async Task CommitAllAsync_NoCoAuthor_AddsNoTrailer()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("new.txt"), "new\n");

        await _workspace.CommitAllAsync("Plain", Factory, null, default);

        Assert.DoesNotContain("Co-authored-by", await GitAsync(_workspace.Path, "log", "-1", "--format=%B"));
    }

    [Fact]
    public async Task CommitAllAsync_DoesNotWriteTheFactoryIdentityIntoTheRepositoryConfig()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("new.txt"), "new\n");

        await _workspace.CommitAllAsync("Work", Factory, null, default);

        Assert.DoesNotContain("Litos Factory", File.ReadAllText(InWorkspace(".git/config")));
    }

    // ---- Push ----

    [Fact]
    public async Task PushAsync_PublishesTheTaskBranch_AndLeavesTheDefaultBranchUntouched()
    {
        var mainBefore = await GitAsync(_remote, "rev-parse", "refs/heads/main");
        await _workspace.CreateTaskBranchAsync("factory/7f3a-csv-export", "main", default);
        File.WriteAllText(InWorkspace("new.txt"), "new\n");
        var commit = await _workspace.CommitAllAsync("Work", Factory, null, default);

        await _workspace.PushAsync("factory/7f3a-csv-export", "main", default);

        Assert.Equal(commit, await GitAsync(_remote, "rev-parse", "refs/heads/factory/7f3a-csv-export"));
        Assert.Equal(mainBefore, await GitAsync(_remote, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task PushAsync_ReworkAddsCommitsToTheSameBranch()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("one.txt"), "1\n");
        await _workspace.CommitAllAsync("First", Factory, null, default);
        await _workspace.PushAsync("factory/x", "main", default);

        File.WriteAllText(InWorkspace("two.txt"), "2\n");
        var second = await _workspace.CommitAllAsync("Rework", Factory, null, default);
        await _workspace.PushAsync("factory/x", "main", default);

        Assert.Equal(second, await GitAsync(_remote, "rev-parse", "refs/heads/factory/x"));
        Assert.Equal("3", await GitAsync(_remote, "rev-list", "--count", "refs/heads/factory/x"));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/1.0")]
    public async Task PushAsync_AnythingButAFactoryTaskBranch_IsRefused(string branch)
    {
        var mainBefore = await GitAsync(_remote, "rev-parse", "refs/heads/main");
        File.WriteAllText(InWorkspace("new.txt"), "sneaky\n");
        await GitAsync(_workspace.Path, "add", "--all");
        await GitAsync(_workspace.Path, "-c", "user.name=x", "-c", "user.email=x@example.invalid", "commit", "--no-gpg-sign", "-m", "On main");

        await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.PushAsync(branch, "main", default));

        Assert.Equal(mainBefore, await GitAsync(_remote, "rev-parse", "refs/heads/main"));
    }

    /// <summary>The factory never force-pushes: when the remote branch has moved on, the push
    /// is rejected and the remote keeps its commit.</summary>
    [Fact]
    public async Task PushAsync_RemoteBranchHasDiverged_IsRejected_NotForced()
    {
        await _workspace.CreateTaskBranchAsync("factory/x", "main", default);
        File.WriteAllText(InWorkspace("one.txt"), "1\n");
        await _workspace.CommitAllAsync("First", Factory, null, default);
        await _workspace.PushAsync("factory/x", "main", default);

        // Someone else pushes to the same branch.
        await GitAsync(_seed, "fetch", "origin");
        await GitAsync(_seed, "checkout", "-b", "factory/x", "origin/factory/x");
        File.WriteAllText(Path.Combine(_seed, "theirs.txt"), "theirs\n");
        await SeedCommitAsync("Their commit");
        await GitAsync(_seed, "push", "origin", "factory/x");
        var theirs = await GitAsync(_seed, "rev-parse", "HEAD");

        File.WriteAllText(InWorkspace("two.txt"), "2\n");
        await _workspace.CommitAllAsync("Mine", Factory, null, default);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => _workspace.PushAsync("factory/x", "main", default));

        Assert.Contains("git push", ex.Message);
        Assert.Equal(theirs, await GitAsync(_remote, "rev-parse", "refs/heads/factory/x"));
    }

    // ---- Credentials ----

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ProcessResult Result { get; set; } = new(0, "", "", TimeSpan.Zero, TimedOut: false, Started: true);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private const string Token = "ghp_secretTokenValue123";

    [Fact]
    public async Task Token_IsPassedThroughTheEnvironment_NeverOnTheCommandLine()
    {
        var runner = new RecordingRunner();
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, "https://github.com/acme/salesapp.git") { AccessToken = Token }, runner);

        await workspace.FetchAsync(default);

        var request = Assert.Single(runner.Requests);
        Assert.DoesNotContain(request.Arguments, a => a.Contains(Token) || a.Contains("extraheader", StringComparison.OrdinalIgnoreCase));
        var header = request.Environment["GIT_CONFIG_VALUE_1"]!;
        Assert.Equal("http.extraheader", request.Environment["GIT_CONFIG_KEY_1"]);
        Assert.StartsWith("Authorization: Basic ", header);
        Assert.Equal(
            $"x-access-token:{Token}",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Authorization: Basic ".Length..])));
    }

    [Fact]
    public async Task Token_IsGivenOnlyToCommandsThatTalkToTheRemote()
    {
        var runner = new RecordingRunner { Result = new(0, "main\n", "", TimeSpan.Zero, false, true) };
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, "https://github.com/acme/salesapp.git") { AccessToken = Token }, runner);

        await workspace.GetStatusAsync(default);

        Assert.All(runner.Requests, r => Assert.False(r.Environment.ContainsKey("GIT_CONFIG_VALUE_1")));
    }

    [Fact]
    public async Task EveryGitCommand_RunsWithPromptsDisabled()
    {
        var runner = new RecordingRunner();
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, _remote), runner);

        await workspace.FetchAsync(default);

        Assert.Equal("0", Assert.Single(runner.Requests).Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task FailureMessage_NeverContainsTheToken_OrTheCommitMessage()
    {
        var runner = new RecordingRunner { Result = new(128, "", $"fatal: could not read from https://x-access-token:{Token}@github.com/", TimeSpan.Zero, false, true) };
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, "https://github.com/acme/salesapp.git") { AccessToken = Token }, runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => workspace.FetchAsync(default));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
        Assert.Contains("exit code 128", ex.Message);
    }

    [Fact]
    public async Task GitNotInstalled_IsReportedPlainly()
    {
        var runner = new RecordingRunner { Result = ProcessResult.NotStarted("'git' could not be started") };
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, _remote), runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => workspace.FetchAsync(default));

        Assert.Contains("git was not found", ex.Message);
    }

    [Fact]
    public async Task GitTimesOut_IsReportedPlainly()
    {
        var runner = new RecordingRunner { Result = new(null, "", "", TimeSpan.FromSeconds(600), TimedOut: true, Started: true) };
        var workspace = new GitWorkspace(new GitWorkspaceOptions(_workspace.Path, _remote), runner);

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => workspace.FetchAsync(default));

        Assert.Contains("timed out", ex.Message);
    }
}
