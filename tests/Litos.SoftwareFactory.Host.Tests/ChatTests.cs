using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Chat before @factory (docs/software-factory/m2-architecture.md §5): a plain message is
/// answered by one read-only turn on the project's reading copy. It never changes the task's
/// state, never touches the task's working copy, and is charged to its own budget.
/// </summary>
public sealed class ChatTests
{
    private const string Question = "Where are orders exported?";

    private static async Task<(TestHost Host, Guid ProjectId, Guid ThreadId)> StartAsync(Action<FactoryOptions>? configure = null)
    {
        var host = await TestHost.StartAsync(configure);
        var projectId = await host.RegisterProjectAsync();
        return (host, projectId, await host.CreateThreadAsync(projectId));
    }

    private static Task<System.Text.Json.JsonElement> SayAsync(TestHost host, Guid threadId, string text = Question, HttpStatusCode expected = HttpStatusCode.Accepted) =>
        host.DelegateAsync(threadId, text, expected: expected);

    /// <summary>Waits until no chat run is answering on the thread and the factory has spoken
    /// after the last thing a person said; returns that last message.</summary>
    private static async Task<(ThreadDetails Details, ThreadMessage Answer)> AnswerAsync(TestHost host, Guid threadId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var details = await host.ThreadAsync(threadId);
            var messages = details.Messages.OrderBy(m => m.Sequence).ToList();
            var lastPerson = messages.FindLastIndex(m => m.Author == MessageAuthor.User);
            if (details.ChatRun is null && lastPerson >= 0 && messages.Count > lastPerson + 1
                && host.App.Services.GetRequiredService<RunRegistry>().Count == 0)
            {
                return (details, messages[^1]);
            }

            if (DateTime.UtcNow > deadline)
                Assert.Fail("No answer. Messages: " + string.Join(" | ", messages.Select(m => $"[{m.Author} {m.Kind}] {m.Text}")));
            await Task.Delay(40);
        }
    }

    private static async Task<TaskRun> ChatRunAsync(TestHost host, Guid threadId)
    {
        await using var db = await host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Runs.AsNoTracking().Where(r => r.ThreadId == threadId && r.Kind == RunKind.Chat).OrderByDescending(r => r.CreatedAt).FirstAsync();
    }

    /// <summary>A chat turn that waits until the test lets it answer, or until it is cancelled.</summary>
    private static (TaskCompletionSource Started, TaskCompletionSource Release) HoldChat(TestHost host, string reply = "Held answer.")
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            Assert.Equal(TurnKind.Chat, call.Kind);
            started.TrySetResult();
            await release.Task.WaitAsync(call.Token);
            return new TurnStreamResult(true, 1, null, reply);
        });
        return (started, release);
    }

    // ---- Answering ----

    [Fact]
    public async Task APlainMessageInDraft_IsAnswered_FromTheDefaultBranch_AndStartsNoWork()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;

        var result = await SayAsync(host, threadId);

        Assert.Equal("Chat", result.GetProperty("outcome").GetString());
        var (details, answer) = await AnswerAsync(host, threadId);
        Assert.Equal((MessageAuthor.Factory, MessageKind.Text, TestHost.DefaultChatReply), (answer.Author, answer.Kind, answer.Text));

        // The task is untouched: still a draft, no task run, no lease, its working copy never used.
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
        Assert.Null(details.LatestRun);
        Assert.False(host.Workspaces.Workspaces.ContainsKey(projectId));
        Assert.Equal(0, details.Thread.TokensUsed);

        var reading = host.Workspaces.ReadingOf(projectId);
        Assert.Equal(["clone", "fetch", "read main"], reading.Calls);

        var run = await ChatRunAsync(host, threadId);
        Assert.Equal((RunStatus.Finished, Question), (run.Status, run.Request));
        var turn = Assert.Single(host.Workers.Turns);
        Assert.Equal(TurnKind.Chat, turn.Kind);
        Assert.Equal($"chat-{run.Id:N}", turn.SessionId);
        Assert.Equal(host.Options.ChatMaxToolCalls, turn.MaxToolCalls);
        Assert.Contains($"> {Question}", turn.Brief);
        Assert.Contains("the default branch `main`", turn.Brief);

        var worker = Assert.Single(host.Workers.Workers);
        Assert.Equal(reading.Path, worker.Launch.WorkingCopy);
        Assert.Equal(host.Options.RunTempDirectory(run.Id), worker.Launch.TempDirectory);
        Assert.True(worker.ShutdownRequested);
        Assert.True(worker.HasExited);
        Assert.False(Directory.Exists(host.Options.RunTempDirectory(run.Id)));
    }

    [Fact]
    public async Task AfterAHandoff_ChatReadsTheTaskBranch_AndTheTaskStaysWhereItWas()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId);
        var handedOff = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var branch = handedOff.Thread.Branch!;
        var turnsBefore = host.Workers.Turns.Count;

        await SayAsync(host, threadId, "Why is the export a separate class?");
        var (details, answer) = await AnswerAsync(host, threadId);

        Assert.Equal(TestHost.DefaultChatReply, answer.Text);
        Assert.Contains($"read {branch}", host.Workspaces.ReadingOf(projectId).Calls);
        var chatTurn = host.Workers.Turns.Skip(turnsBefore).Single();
        Assert.Contains($"the task branch `{branch}`", chatTurn.Brief);
        Assert.Contains("Add CSV export for Orders.", chatTurn.Brief); // the task's request

        Assert.Equal(LifecycleState.AwaitingHumanTesting, details.Thread.State);
        Assert.Equal(handedOff.LatestRun!.Id, details.LatestRun!.Id);
        Assert.Equal(handedOff.Thread.TokensUsed, details.Thread.TokensUsed);
    }

    [Fact]
    public async Task ATaskBranchGoneFromTheRemote_FallsBackToTheDefaultBranch_AndSaysSo()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId);
        var branch = (await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting)).Thread.Branch!;
        host.Workspaces.ReadingOf(projectId).MissingBranches.Add(branch);

        await SayAsync(host, threadId);
        await AnswerAsync(host, threadId);

        Assert.Contains("read main", host.Workspaces.ReadingOf(projectId).Calls);
        Assert.Contains($"(the task branch `{branch}` is no longer on the remote)", host.Workers.Turns.Last().Brief);
    }

    [Fact]
    public async Task TheBrief_CarriesTheThreadSoFar_ButNotTheQuestionTwice()
    {
        var (host, _, threadId) = await StartAsync();
        await using var __ = host;
        await SayAsync(host, threadId, "Is there an export already?");
        await AnswerAsync(host, threadId);

        await SayAsync(host, threadId, "Then where would it go?");
        await AnswerAsync(host, threadId);

        var brief = host.Workers.Turns.Last().Brief;
        Assert.Contains("## The thread so far", brief);
        Assert.Contains("**Person:**\n> Is there an export already?", brief);
        Assert.Contains($"**Factory:**\n> {TestHost.DefaultChatReply}", brief);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(brief, "Then where would it go\\?"));
        Assert.True(brief.IndexOf("## The question", StringComparison.Ordinal) < brief.IndexOf("Then where would it go?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conversation_LeavesOutStatusNotes_AndEverythingFromTheQuestionOn()
    {
        var thread = new TaskThread { Id = Guid.NewGuid(), Title = "t", SessionId = "s", Provider = "p", Model = "m" };
        ThreadMessage Message(long sequence, MessageAuthor author, MessageKind kind, string text) =>
            new() { Id = Guid.NewGuid(), ThreadId = thread.Id, Sequence = sequence, Author = author, Kind = kind, Text = text };
        var details = new ThreadDetails(
            thread, new Project { Name = "p", GitHubOwner = "o", GitHubRepository = "r", DefaultBranch = "main", VerificationProfileJson = "{}" },
            [
                Message(3, MessageAuthor.User, MessageKind.Text, "Same words"),
                Message(1, MessageAuthor.User, MessageKind.Text, "Same words"),
                Message(2, MessageAuthor.Factory, MessageKind.Status, "Queued."),
                Message(4, MessageAuthor.Factory, MessageKind.Status, "after"),
            ],
            [], null, null, null, []);
        var run = new TaskRun { Id = Guid.NewGuid(), Request = "Same words", PromptRevision = "x" };

        var lines = ChatExecutor.Conversation(details, run);

        // Ordered by sequence, cut at the last time the question was said, and without the status note.
        Assert.Equal([new Core.Briefs.ChatLine("Person", "Same words")], lines);
    }

    // ---- Showing it working ----

    [Fact]
    public async Task WhileAnswering_TheThreadIsToldWhatItIsDoing_AndTheProgressGoesWithTheAnswer()
    {
        var (host, _, threadId) = await StartAsync(o => o.ChatProgressInterval = TimeSpan.FromMilliseconds(20));
        await using var _ = host;
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            call.Report(new TurnProgress(TurnProgressKind.ToolCall, "search_code", JsonDocument.Parse("""{"pattern":"Export"}""").RootElement));
            call.Report(new TurnProgress(TurnProgressKind.ModelReply));
            call.Report(new TurnProgress(TurnProgressKind.ToolResult));
            call.Report(new TurnProgress(TurnProgressKind.ToolCall, "read_file", JsonDocument.Parse("""{"path":"src/Orders.cs"}""").RootElement));
            call.Report(new TurnProgress(TurnProgressKind.ModelReply));
            reading.SetResult();
            await release.Task.WaitAsync(call.Token);
            return new TurnStreamResult(true, 2, null, "In src/Orders.cs.");
        });

        await SayAsync(host, threadId);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The thread's view says what the answer is doing, once the next report is written.
        System.Text.Json.JsonElement progress = default;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var view = await host.GetAsync($"api/threads/{threadId}");
            progress = view.GetProperty("chatProgress");
            if (progress.ValueKind == JsonValueKind.Object && progress.GetProperty("activity").GetString() != ChatProgressTracker.Thinking
                && progress.GetProperty("modelCalls").GetInt32() == 2)
            {
                break;
            }

            await Task.Delay(30);
        }

        Assert.Equal("Read src/Orders.cs", progress.GetProperty("activity").GetString());
        Assert.Equal((2, 1), (progress.GetProperty("modelCalls").GetInt32(), progress.GetProperty("toolCalls").GetInt32()));

        release.SetResult();
        var (details, answer) = await AnswerAsync(host, threadId);
        Assert.Equal("In src/Orders.cs.", answer.Text);
        Assert.Null(details.ChatProgress);

        // Every report on the stream came before the reply, and the first said where it started.
        var events = await host.Store.ReadEventsAsync(threadId, 0, 500, default);
        var reports = events.Where(e => e.Type == EventTypes.ChatProgress).ToList();
        Assert.NotEmpty(reports);
        Assert.Equal(ChatProgressTracker.Fetching, JsonDocument.Parse(reports[0].PayloadJson).RootElement.GetProperty("activity").GetString());
        var reply = events.Last(e => e.Type == EventTypes.MessageAdded);
        Assert.True(reports.Max(e => e.Sequence) < reply.Sequence);
    }

    [Fact]
    public async Task ATurnThatSaysNothing_IsReportedOnlyWhenSomethingChanges()
    {
        var (host, _, threadId) = await StartAsync(o => o.ChatProgressInterval = TimeSpan.FromMilliseconds(20));
        await using var _ = host;
        host.Workers.Script.Enqueue(async call =>
        {
            await Task.Delay(400, call.Token);
            return new TurnStreamResult(true, 0, null, "Quick.");
        });

        await SayAsync(host, threadId);
        await AnswerAsync(host, threadId);

        // Getting the code, starting, thinking: then nothing new for 400 ms, so nothing more written.
        var reports = (await host.Store.ReadEventsAsync(threadId, 0, 500, default)).Count(e => e.Type == EventTypes.ChatProgress);
        Assert.InRange(reports, 1, 3);
    }

    /// <summary>A question asked before delegating is not what the task was asked to do: a change
    /// request's brief carries the delegation as the original request.</summary>
    [Fact]
    public async Task AQuestionAskedFirst_IsNeverTakenForTheTasksRequest()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await SayAsync(host, threadId, "Is there an export already?");
        await AnswerAsync(host, threadId);
        await host.DelegateAsync(threadId, "@factory Add CSV export for Orders.");
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var turns = host.Workers.Turns.Count;

        await host.DelegateAsync(threadId, "@factory Quote fields that contain commas.");
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        // The rework's review is told what the task was for: the delegation, not the question.
        var review = host.Workers.Turns.Skip(turns).First(t => t.Kind == TurnKind.Review);
        Assert.Contains("Add CSV export for Orders.", review.Brief);
        Assert.DoesNotContain("Is there an export already?", review.Brief);
    }

    // ---- When there is no answer ----

    [Fact]
    public async Task ATurnWithNoReply_SaysSo_InAStatusNote()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(true, 4, null)));

        await SayAsync(host, threadId);
        var (_, answer) = await AnswerAsync(host, threadId);

        Assert.Equal((MessageAuthor.Factory, MessageKind.Status, ChatExecutor.NoAnswer), (answer.Author, answer.Kind, answer.Text));
    }

    [Fact]
    public async Task ATurnThatFailed_SaysWhy()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(false, 0, "The model is unavailable.")));

        await SayAsync(host, threadId);
        var (details, answer) = await AnswerAsync(host, threadId);

        Assert.Equal(MessageKind.Status, answer.Kind);
        Assert.Equal("Litos could not answer that: The model is unavailable.", answer.Text);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
    }

    [Fact]
    public async Task AReadingCopyThatCannotBeFetched_SaysWhy_AndStartsNoWorker()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;
        host.Workspaces.ReadingOf(projectId).FailFetch = new WorkspaceException("The remote could not be reached.");

        await SayAsync(host, threadId);
        var (_, answer) = await AnswerAsync(host, threadId);

        Assert.Equal("Litos could not answer that: The remote could not be reached.", answer.Text);
        Assert.Empty(host.Workers.Workers);
    }

    [Fact]
    public async Task AnAnswerThatTakesTooLong_IsStopped_AndSaysSo()
    {
        var (host, _, threadId) = await StartAsync(o => o.ChatTimeout = TimeSpan.FromMilliseconds(300));
        await using var _ = host;
        var (started, _) = HoldChat(host);

        await SayAsync(host, threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var (details, answer) = await AnswerAsync(host, threadId);

        Assert.Equal(MessageKind.Status, answer.Kind);
        Assert.Contains("took longer than", answer.Text);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
        Assert.True(Assert.Single(host.Workers.Workers).ShutdownRequested);
    }

    // ---- One answer at a time ----

    [Fact]
    public async Task WhileAnswering_TheThreadSaysSo_AndASecondMessageIs409()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        var (started, release) = HoldChat(host);

        await SayAsync(host, threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True((await host.GetAsync($"api/threads/{threadId}")).GetProperty("chatPending").GetBoolean());
        var refusal = await SayAsync(host, threadId, "And another thing?", HttpStatusCode.Conflict);
        Assert.Contains("still answering", refusal.GetProperty("error").GetString());

        release.SetResult();
        var (_, answer) = await AnswerAsync(host, threadId);
        Assert.Equal("Held answer.", answer.Text);
        Assert.False((await host.GetAsync($"api/threads/{threadId}")).GetProperty("chatPending").GetBoolean());
        Assert.Single(host.Workers.Turns);
    }

    [Fact]
    public async Task TwoAnswersOnOneProject_TakeTurnsOnItsReadingCopy()
    {
        var (host, projectId, firstThread) = await StartAsync(o => o.SlotCap = 2);
        await using var _ = host;
        var secondThread = await host.CreateThreadAsync(projectId, "Another task");
        var (started, release) = HoldChat(host);

        await SayAsync(host, firstThread);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await SayAsync(host, secondThread);

        // The second run is claimed, but waits for the reading copy rather than checking it out under the first.
        await Task.Delay(300);
        Assert.Single(host.Workers.Turns);
        Assert.Equal(["clone", "fetch", "read main"], host.Workspaces.ReadingOf(projectId).Calls);

        release.SetResult();
        await AnswerAsync(host, firstThread);
        var (_, answer) = await AnswerAsync(host, secondThread);
        Assert.Equal(TestHost.DefaultChatReply, answer.Text);
        Assert.Equal(2, host.Workers.Turns.Count);
    }

    // ---- What a plain message is in other states ----

    [Fact]
    public async Task WhileTheTaskIsRunning_APlainMessageIsAFollowUp_ForTheTasksAgent()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var result = await SayAsync(host, threadId, "Use semicolons, not commas.");

        Assert.Equal("FollowUp", result.GetProperty("outcome").GetString());
        Assert.Contains("Use semicolons, not commas.", Assert.Single(Assert.Single(host.Workers.Workers).Steered));
        Assert.Null((await host.ThreadAsync(threadId)).ChatRun);
        await host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task WhenTheTaskIsPaused_APlainMessageIs409()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await host.WaitForStateAsync(threadId, LifecycleState.PausedUser);

        await SayAsync(host, threadId, expected: HttpStatusCode.Conflict);

        Assert.Null((await host.ThreadAsync(threadId)).ChatRun);
    }

    // ---- Its own budget ----

    [Fact]
    public async Task AChatModelCall_IsChargedToTheChat_NotToTheTask()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.CallModelAsync(call.SessionId, "what is in Orders.cs?");
            return new TurnStreamResult(true, 1, null, "Read it.");
        });

        await SayAsync(host, threadId);
        var (details, answer) = await AnswerAsync(host, threadId);

        Assert.Equal("Read it.", answer.Text);
        Assert.Single(host.Provider.Requests);
        Assert.Equal((0L, 0L), (details.Thread.TokensUsed, details.Thread.TokensReserved));
        var run = await ChatRunAsync(host, threadId);
        Assert.True(run.ChatTokensUsed > 0);
        Assert.Equal((100_000L, 0L), (run.ChatBudgetCap!.Value, run.ChatTokensReserved));
    }

    [Fact]
    public async Task AChatOverItsCap_IsRefusedBeforeTheModelIsCalled_AndSaysWhy()
    {
        var (host, _, threadId) = await StartAsync(o => o.ChatTurnCap = 10);
        await using var _ = host;
        host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.CallModelAsync(call.SessionId);
            return new TurnStreamResult(false, 0, "refused");
        });

        await SayAsync(host, threadId);
        var (details, answer) = await AnswerAsync(host, threadId);

        Assert.Empty(host.Provider.Requests);
        Assert.Equal(MessageKind.Status, answer.Kind);
        Assert.StartsWith("Litos stopped before answering:", answer.Text);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
    }

    // ---- Liveness ----

    [Fact]
    public async Task AChatRunNoExecutorOwns_IsFinishedWithANote_AndTheTaskIsLeftAlone()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await SayAsync(host, threadId);
        var orphan = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        Assert.Equal(RunKind.Chat, orphan.Run.Kind);

        Assert.Equal(1, await host.App.Services.GetRequiredService<RunLiveness>().SweepAsync(default));

        var details = await host.ThreadAsync(threadId);
        Assert.Null(details.ChatRun);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
        Assert.Equal(RunLiveness.ChatMessage, details.Messages.OrderBy(m => m.Sequence).Last().Text);
        Assert.Equal(0, await host.App.Services.GetRequiredService<RunLiveness>().SweepAsync(default));
    }

    [Fact]
    public async Task AtStartup_AChatRunOfAPreviousHost_IsFinishedWithANote()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await SayAsync(host, threadId);
        await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default);

        await host.App.Services.GetRequiredService<RunLiveness>().RecoverAtStartupAsync(default);

        var details = await host.ThreadAsync(threadId);
        Assert.Null(details.ChatRun);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
        Assert.Equal(RunLiveness.ChatMessage, details.Messages.OrderBy(m => m.Sequence).Last().Text);
    }
}

/// <summary>A chat run beside a task's run is never what a pause, a cancel or a follow-up reaches.</summary>
public sealed class ChatRegistryTests
{
    [Fact]
    public void FindByThread_SkipsAChatRun()
    {
        var registry = new RunRegistry();
        var threadId = Guid.NewGuid();
        var chat = new ActiveRun(Guid.NewGuid(), threadId, Guid.NewGuid(), "p", "m", isChat: true);
        registry.TryAdd(chat);

        Assert.Null(registry.FindByThread(threadId));
        Assert.Same(chat, registry.Find(chat.RunId));

        var task = new ActiveRun(Guid.NewGuid(), threadId, Guid.NewGuid(), "p", "m");
        registry.TryAdd(task);
        Assert.Same(task, registry.FindByThread(threadId));
        Assert.True(chat.IsChat);
        Assert.False(task.IsChat);
    }
}

/// <summary>How a chat answer's worker events become what the person waiting is shown.</summary>
public sealed class ChatProgressTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static TurnProgress Call(string tool, string arguments) => new(TurnProgressKind.ToolCall, tool, JsonDocument.Parse(arguments).RootElement);

    [Fact]
    public void Starts_ByGettingTheLatestCode()
    {
        var runId = Guid.NewGuid();

        Assert.Equal(new ChatProgress(runId, T0, 0, 0, ChatProgressTracker.Fetching), new ChatProgressTracker(runId, T0).Snapshot);
    }

    [Fact]
    public void CountsModelCallsAndToolCalls_AndSaysWhatItIsDoing()
    {
        var tracker = new ChatProgressTracker(Guid.NewGuid(), T0);

        tracker.Apply(Call("read_file", """{"path":"src/Orders.cs"}"""));
        Assert.Equal("Read src/Orders.cs", tracker.Snapshot.Activity);
        tracker.Apply(new TurnProgress(TurnProgressKind.ModelReply));
        tracker.Apply(new TurnProgress(TurnProgressKind.ToolResult));

        var snapshot = tracker.Snapshot;
        Assert.Equal((1, 1, ChatProgressTracker.Thinking), (snapshot.ModelCalls, snapshot.ToolCalls, snapshot.Activity));
    }

    [Fact]
    public void EveryChange_MovesTheVersion()
    {
        var tracker = new ChatProgressTracker(Guid.NewGuid(), T0);
        var start = tracker.Version;

        tracker.Set(ChatProgressTracker.Starting);
        tracker.Apply(new TurnProgress(TurnProgressKind.ModelReply));

        Assert.Equal(start + 2, tracker.Version);
    }

    [Theory]
    [InlineData("search_code", """{"pattern":"Export"}""", "Search")]
    [InlineData("list_directory", """{"path":"src"}""", "List src")]
    [InlineData("run_kernel_code", """{"code":"var x = 1;"}""", ChatProgressTracker.RunningCode)]
    [InlineData("some_new_tool", "{}", "some_new_tool")]
    public void Describe_UsesTheWordsTheOtherFacesUse(string tool, string arguments, string expected)
    {
        Assert.StartsWith(expected, ChatProgressTracker.Describe(Call(tool, arguments)));
    }

    [Fact]
    public void Describe_AVeryLongCall_IsCut()
    {
        var described = ChatProgressTracker.Describe(Call("read_file", $$"""{"path":"{{new string('a', 500)}}"}"""));

        Assert.EndsWith("...", described);
        Assert.True(described.Length <= 123);
    }
}
