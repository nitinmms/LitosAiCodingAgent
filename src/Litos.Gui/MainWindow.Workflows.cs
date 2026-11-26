using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Agent.Streaming;
using Litos.Gui.Buzzies;
using Litos.Gui.Buzzies.Routines;
using Litos.Gui.Buzzies.Workflows;

namespace Litos.Gui;

/// <summary>
/// Learn This Workflow (Implement_Buzzies.md, Phase 2). After a Buzzy turn that went well, a row
/// of actions under it: Open artifact (when the turn published one), Remember this workflow (draft
/// a reusable skill from the conversation, review it, save it as a new version) and Repeat every…
/// (a routine for the same request, pinned to the workflow saved in this conversation, if any).
/// Also the review of memories that routines proposed.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly IBrush ActionRowBrush = new SolidColorBrush(Color.Parse("#252526"));

    // The workflow version saved in each conversation, so Repeat every… made afterwards follows it.
    private readonly Dictionary<string, WorkflowVersion> _workflowBySession = [];

    private WorkflowStore Workflows => new(Buzzies);

    private MemoryProposals MemoryProposals => new(Buzzies);

    private static void CollectArtifactLinks(ToolCallResult result, List<string> links)
    {
        if (result.ToolName != BuzzyToolNames.Artifact || result.Result.IsError)
            return;
        foreach (Match link in Regex.Matches(result.Result.Text, @"https?://[^\s""'<>)\]]+"))
            if (!links.Contains(link.Value))
                links.Add(link.Value);
    }

    private void AddWorkflowActions(BuzzyProfile profile, string request, IReadOnlyList<string> links)
    {
        var sessionId = _sessionId;
        var row = new WrapPanel { Orientation = Orientation.Horizontal };

        Button Action(string label, string tip, Func<Task> onClick)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) };
            ToolTip.SetTip(button, tip);
            button.Click += async (_, _) =>
            {
                if (TurnInFlight)
                {
                    AddToolLine("Wait for the current turn to finish first.");
                    return;
                }
                if (_buzzy?.Id != profile.Id || _sessionId != sessionId)
                {
                    AddToolLine($"That belongs to an earlier conversation with {profile.Name}. Open it again to use these actions.");
                    return;
                }
                await onClick();
            };
            return button;
        }

        if (links.Count > 0)
            row.Children.Add(Action("Open artifact", links[0], () =>
            {
                OpenUrl(links[0], _transcript.WorkingDirectory);
                return Task.CompletedTask;
            }));
        row.Children.Add(Action("Remember this workflow", $"Save what {profile.Name} just did as a reusable skill you review first",
            () => RememberWorkflowAsync(profile, request)));
        row.Children.Add(Action("Repeat every…", $"Have {profile.Name} do this again on a schedule",
            () => RepeatAsync(profile, request, _workflowBySession.GetValueOrDefault(sessionId))));

        TranscriptPanel.Children.Add(new Border
        {
            Background = ActionRowBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
        });
        ScrollToEndAfterLayout();
    }

    private async Task RememberWorkflowAsync(BuzzyProfile profile, string request)
    {
        if (RejectIfNoModel())
            return;
        var trace = WorkflowTrace.From(_transcript.Messages);
        if (trace.IsEmpty)
        {
            AddToolLine("There's nothing in this conversation to remember yet.");
            return;
        }

        AddToolLine($"{profile.Name} is drafting a workflow from this conversation…");
        ShowWorkingIndicator();
        WorkflowDraft draft;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            draft = await WorkflowDrafter.DraftAsync(trace, profile.Name, _session.ChatProvider, _session.Model, timeout.Token);
        }
        catch (Exception ex)
        {
            AddToolLine($"Couldn't draft it automatically ({ex.Message}), so here is an outline to edit.");
            draft = WorkflowDrafter.Parse("", trace);
        }
        finally
        {
            HideWorkingIndicator();
        }

        var store = Workflows;
        var reviewed = await WorkflowReviewWindow.ShowAsync(this, draft, profile.Name, trace.ToolsUsed, name => store.LatestVersion(profile.Id, name));
        if (reviewed is null)
            return;

        WorkflowVersion saved;
        try
        {
            saved = store.Save(profile.Id, reviewed.Draft.Name, reviewed.Draft.Description, reviewed.Draft.Body, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddToolLine($"Couldn't save the workflow: {ex.Message}");
            return;
        }

        _workflowBySession[_sessionId] = saved;
        AddToolLine($"Saved workflow \"{saved.Name}\" version {saved.Version}. {profile.Name} can follow it in any conversation (it's in its skills), and routines can repeat it.");
        if (reviewed.ThenSchedule)
            await RepeatAsync(profile, request, saved);
    }

    private async Task RepeatAsync(BuzzyProfile profile, string request, WorkflowVersion? workflow)
    {
        if (_routines is null)
        {
            AddToolLine("Routines are unavailable this run.");
            return;
        }

        var draft = RepeatDraft(profile, request, workflow, DateTimeOffset.UtcNow);
        var saved = await RoutineEditorWindow.ShowAsync(this, null, Buzzies.List(), profile.Id, draft);
        if (saved is null)
            return;

        _routines.Database.SaveRoutine(saved);
        _routines.NotifyChanged();
        var follows = saved.FollowsWorkflow ? $", following workflow \"{saved.SkillName}\" v{saved.SkillVersion}" : "";
        AddToolLine($"{profile.Name} will repeat this: {RoutineSchedule.Describe(saved).ToLowerInvariant()}{follows}. Change it any time in Routines.");
    }

    /// <summary>The routine Repeat every… proposes: the same request, every Friday at 17:00 (the
    /// doc's "Repeat Every Friday"), following the workflow saved in this conversation if there is one.</summary>
    internal static Routine RepeatDraft(BuzzyProfile profile, string request, WorkflowVersion? workflow, DateTimeOffset now)
    {
        var oneLine = request.ReplaceLineEndings(" ").Trim();
        var name = workflow is not null
            ? char.ToUpperInvariant(workflow.Name[0]) + workflow.Name[1..].Replace('-', ' ')
            : oneLine.Length <= 48 ? oneLine : oneLine[..48].TrimEnd() + "…";
        return new Routine(
            Id: Guid.NewGuid().ToString("n"),
            BuzzyId: profile.Id,
            Name: name.Length == 0 ? "Repeat" : name,
            Prompt: request.Trim(),
            Trigger: RoutineTrigger.Weekly,
            TimeOfDay: new TimeOnly(17, 0),
            Day: DayOfWeek.Friday,
            IntervalMinutes: null,
            TimeZoneId: TimeZoneInfo.Local.Id,
            Enabled: true,
            MaxMinutes: Routine.DefaultMaxMinutes,
            MaxTokens: Routine.DefaultMaxTokens,
            MissedRuns: MissedRunPolicy.Skip,
            CreatedAt: now,
            SkillName: workflow?.Name,
            SkillVersion: workflow?.Version);
    }

    private async Task ReviewMemoriesAsync(BuzzyProfile profile)
    {
        await MemoryReviewWindow.ShowAsync(this, MemoryProposals, profile.Id, profile.Name);
        RefreshBuzzyRowStatus(profile.Id);
    }

    private int PendingMemoryCount(string buzzyId) => MemoryProposals.List(buzzyId).Count;
}
