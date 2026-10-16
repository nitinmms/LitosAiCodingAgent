using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Lesson reflection end to end (m4-architecture.md §5): accepting a task queues a reflection,
/// whose one turn proposes lessons that land as suggestions on the thread; it never undoes or delays
/// the acceptance (acceptance scenario 18); a short budget defers it; a failure offers Retry and Skip.
/// </summary>
public sealed class ReflectionTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> HandedOffAsync(long? cap = null)
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync(), budgetCap: cap);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        return threadId;
    }

    private async Task<JsonElement> ReflectionAsync(Guid threadId, string status, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var details = await _host.GetAsync($"api/threads/{threadId}");
            if (details.GetProperty("reflection") is { ValueKind: JsonValueKind.Object } reflection && reflection.GetProperty("status").GetString() == status)
                return details;
            Assert.True(DateTime.UtcNow < deadline, $"The reflection did not become {status}: {details.GetProperty("reflection")}");
            await Task.Delay(40);
        }
    }

    private async Task<string[]> NotesAsync(Guid threadId) =>
        [.. (await _host.ThreadAsync(threadId)).Messages.Select(m => m.Text)];

    [Fact]
    public async Task AcceptingRunsAReflection_WhoseProposal_LandsOnTheThreadAsASuggestion()
    {
        var threadId = await HandedOffAsync();
        _host.Workers.Script.Enqueue(async call =>
        {
            // The brief names each source with the id to cite: here, the handoff.
            var handoff = Regex.Match(call.Brief, @"\*\*Evidence\*\* `([0-9a-f-]{36})`").Groups[1].Value;
            await call.Worker.SubmitAsync(call.SessionId, new LessonsSubmission(
                [new LessonProposal("Export files are UTF-8 with a byte order mark.", ["csv", "export"], [], "Evidence", handoff, "Excel needs it.")]));
            return FakeWorkerLauncher.Done();
        });

        var accepted = await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);
        Assert.Equal("Accepted", accepted.GetProperty("state").GetString()); // at once: the reflection comes after

        var details = await ReflectionAsync(threadId, "Completed");
        var proposed = Assert.Single(details.GetProperty("proposedLessons").EnumerateArray());
        Assert.Equal(("Suggested", "Export files are UTF-8 with a byte order mark."),
            (proposed.GetProperty("status").GetString(), proposed.GetProperty("current").GetProperty("rule").GetString()));
        Assert.Equal(JsonValueKind.Null, proposed.GetProperty("current").GetProperty("author").ValueKind);
        Assert.Equal([1L], details.GetProperty("reflection").GetProperty("outcome").GetProperty("lessons").EnumerateArray().Select(n => n.GetInt64()));
        Assert.Contains("Lesson reflection proposed a lesson, L-1. An Admin can remember it for the project below, or on the Lessons page.", await NotesAsync(threadId));
        var turn = _host.Workers.Turns.Last();
        Assert.Equal(TurnKind.Reflection, turn.Kind);
        Assert.Contains("Lesson reflection", turn.Brief);
        Assert.Equal("Accepted", (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("thread").GetProperty("state").GetString());
    }

    [Fact]
    public async Task WithTooLittleBudgetLeft_TheReflectionIsDeferred_AndRunsOnceTheCapIsRaisedAndItIsRetried()
    {
        var threadId = await HandedOffAsync(cap: 20_000);

        await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);
        await ReflectionAsync(threadId, "DeferredBudget");
        Assert.Contains("Task accepted. Lesson reflection is deferred; increase the allowance, add a lesson manually, or skip.", await NotesAsync(threadId));

        var refused = await _host.PostAsync($"api/threads/{threadId}/reflection/retry", null, HttpStatusCode.Conflict);
        Assert.Contains("Raise its cap first", refused.GetProperty("error").GetString());
        await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = 200_000 }, HttpStatusCode.OK);
        await _host.PostAsync($"api/threads/{threadId}/reflection/retry", null, HttpStatusCode.OK);

        var details = await ReflectionAsync(threadId, "Completed");
        Assert.Empty(details.GetProperty("proposedLessons").EnumerateArray());
        Assert.Contains("Lesson reflection found nothing a later task needs, so it proposed no lessons.", await NotesAsync(threadId));
    }

    [Fact]
    public async Task AReflectionThatSubmitsNothing_Fails_TheTaskStaysAccepted_AndItCanBeSkipped()
    {
        var threadId = await HandedOffAsync();
        _host.Workers.Script.Enqueue(_ => Task.FromResult(FakeWorkerLauncher.Done()));

        await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);

        var failed = await ReflectionAsync(threadId, "Failed");
        Assert.Equal("Accepted", failed.GetProperty("thread").GetProperty("state").GetString());
        Assert.Contains(await NotesAsync(threadId), n => n.StartsWith("Lesson reflection failed: the model finished without calling submit_lessons.", StringComparison.Ordinal));
        await _host.PostAsync($"api/threads/{threadId}/reflection/skip", null, HttpStatusCode.OK);
        await ReflectionAsync(threadId, "Skipped");
    }

    [Fact]
    public async Task WithReflectionOff_AcceptingQueuesNone()
    {
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();
        await settings.SaveAsync(SettingsSections.Budgets, settings.Budgets with { ReflectionAllowanceTokens = 0 }, settings.RevisionOf(SettingsSections.Budgets), null, default);
        var threadId = await HandedOffAsync();
        var turns = _host.Workers.Turns.Count;

        await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);
        await Task.Delay(300);

        Assert.Equal(JsonValueKind.Null, (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("reflection").ValueKind);
        Assert.Equal(turns, _host.Workers.Turns.Count);
    }
}
