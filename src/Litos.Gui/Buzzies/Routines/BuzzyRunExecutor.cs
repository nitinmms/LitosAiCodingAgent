using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;

namespace Litos.Gui.Buzzies.Routines;

/// <summary>One routine run to execute.</summary>
public sealed record RunRequest(BuzzyProfile Profile, Routine Routine, string SessionId, DateTimeOffset StartedAt);

/// <summary>How a run ended. <see cref="Outputs"/> lists files it wrote and artifact links it published.</summary>
public sealed record RunOutcome(RunStatus Status, string? Summary, string? Error, int InputTokens, int OutputTokens, IReadOnlyList<string> Outputs);

public interface IBuzzyRunExecutor
{
    Task<RunOutcome> RunAsync(RunRequest request, CancellationToken ct);
}

/// <summary>What a run is executed with, snapshotted when it starts: the provider and model the
/// user has selected at that moment, and the same per-turn tool registry the window builds.</summary>
public sealed record BuzzyRunEnvironment(
    AgentLoopFactory LoopFactory,
    ToolRegistry AvailableTools,
    IChatProvider Provider,
    string Model,
    int ContextLength,
    ITranscriptStore Transcripts,
    BuzzyToolContext Tools);

/// <summary>
/// Runs a routine as an ordinary Buzzy turn in a fresh session of its own, reviewable afterwards
/// like any conversation, with nobody watching: unattended tools (no shell, nothing that waits on an
/// approval card), the Buzzy's prompt and workspace, and the time and token limits enforced here,
/// outside the model (ReadMe_Buzzies.md §7). Nothing in a run depends on the process's current
/// directory, so it can run while the window shows something else.
/// </summary>
public sealed partial class BuzzyRunExecutor(Func<CancellationToken, Task<BuzzyRunEnvironment>> environment) : IBuzzyRunExecutor
{
    private const int MaxSummaryChars = 600;

    public async Task<RunOutcome> RunAsync(RunRequest request, CancellationToken ct)
    {
        var env = await environment(ct);
        if (string.IsNullOrEmpty(env.Model))
            return new RunOutcome(RunStatus.Failed, null, "No model is selected. Pick one with /model, then run it again.", 0, 0, []);

        var profile = request.Profile;
        var routine = request.Routine;
        var store = env.Tools.Store;
        var workspace = store.WorkspaceOf(profile.Id);
        Directory.CreateDirectory(workspace);

        var registry = BuzzyToolPolicy.Build(env.AvailableTools, profile, env.Tools with { Unattended = true });
        var prompt = new BuzzySystemPromptProvider(env.LoopFactory.SystemPromptProvider, profile, store);
        var loop = env.LoopFactory.WithSystemPromptProvider(prompt).Create(env.Provider, registry);
        var transcript = Transcript.CreateNew(workspace);

        using var timeLimit = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, routine.MaxMinutes)));
        using var budget = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeLimit.Token, budget.Token);

        var inputTokens = 0;
        var outputTokens = 0;
        var lastText = new StringBuilder();
        var currentText = new StringBuilder();
        string? error = null;
        var outputs = new List<string>();

        try
        {
            await foreach (var evt in loop.RunTurnAsync(
                profile.Owner, request.SessionId, transcript, env.Model, BriefFor(routine, request.StartedAt), linked.Token,
                contextWindowTokens: env.ContextLength > 0 ? env.ContextLength : null))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        currentText.Append(delta.Text);
                        break;
                    case MessageCompleted completed:
                        inputTokens += completed.Usage.TotalInputTokens;
                        outputTokens += completed.Usage.OutputTokens;
                        if (currentText.Length > 0)
                        {
                            lastText.Clear().Append(currentText);
                            currentText.Clear();
                        }
                        if (inputTokens + outputTokens > routine.MaxTokens)
                            await budget.CancelAsync();
                        break;
                    case ToolCallCompleted call when call.ToolName is BuzzyToolNames.WriteFile or BuzzyToolNames.EditFile:
                        if (call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("path", out var path)
                            && path.ValueKind == JsonValueKind.String && ShowablePath(workspace, path.GetString()!) is { } shown
                            && !outputs.Contains(shown))
                            outputs.Add(shown);
                        break;
                    case ToolCallResult result when result.ToolName == BuzzyToolNames.Artifact && !result.Result.IsError:
                        foreach (Match link in LinkPattern().Matches(result.Result.Text))
                            if (!outputs.Contains(link.Value))
                                outputs.Add(link.Value);
                        break;
                    case ErrorOccurred failure:
                        error = failure.Exception.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            return Ended(RunStatus.Failed, $"Stopped: the run used more than its budget of {routine.MaxTokens:N0} tokens.");
        }
        catch (OperationCanceledException) when (timeLimit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Ended(RunStatus.Failed, $"Stopped: the run took longer than its limit of {routine.MaxMinutes} minutes.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Ended(RunStatus.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            return Ended(RunStatus.Failed, ex.Message);
        }

        // The loop can also end on its own after a budget cancel lands between rounds.
        if (budget.IsCancellationRequested)
            return Ended(RunStatus.Failed, $"Stopped: the run used more than its budget of {routine.MaxTokens:N0} tokens.");

        return error is null ? Ended(RunStatus.Succeeded, null) : Ended(RunStatus.Failed, error);

        RunOutcome Ended(RunStatus status, string? why)
        {
            var text = (lastText.Length > 0 ? lastText : currentText).ToString().Trim();
            return new RunOutcome(status, text.Length == 0 ? null : Shorten(text), why, inputTokens, outputTokens, outputs);
        }
    }

    /// <summary>A written file as the user should see it: relative to the workspace when inside it
    /// (the only place the run could write), with forward slashes.</summary>
    internal static string? ShowablePath(string workspace, string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
            var relative = Path.GetRelativePath(Path.GetFullPath(workspace), full);
            return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The routine's prompt, prefixed with what an unattended run needs to know.</summary>
    internal static string BriefFor(Routine routine, DateTimeOffset startedAt) =>
        $"[This is your scheduled routine \"{routine.Name}\", started {startedAt.ToLocalTime():dddd d MMMM yyyy, HH:mm}. "
        + "Nobody is watching this run, so don't ask questions: do the task, then finish. "
        + "Save what you produce in your workspace as new files whose names include today's date, never overwriting an earlier run's file. "
        + "End with a two- or three-sentence summary of what you did and the names of the files you saved.]\n\n"
        + routine.Prompt;

    private static string Shorten(string text) =>
        text.Length <= MaxSummaryChars ? text : text[..MaxSummaryChars].TrimEnd() + "…";

    [GeneratedRegex(@"https?://[^\s""'<>)\]]+")]
    private static partial Regex LinkPattern();
}
