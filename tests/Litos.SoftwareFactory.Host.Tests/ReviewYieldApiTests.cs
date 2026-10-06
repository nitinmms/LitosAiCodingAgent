using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Review yield: a person judges each review finding, and the factory reports what the reviews
/// cost against what they found that was real (ReadM_SoftwareFactory_ReviewGuidance.md §14).
/// </summary>
public sealed class ReviewYieldApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> HandedOffWithTwoFindingsAsync()
    {
        _host.Workers.Script.Enqueue(_host.DefaultTurnAsync);
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission(
            [
                new ReviewFinding(FindingSeverity.Minor, "src/Invoices.cs", 3, "Regenerating resets the user's choice."),
                new ReviewFinding(FindingSeverity.Minor, "src/Invoices.cs", 9, "A name could be clearer."),
            ]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        return threadId;
    }

    [Fact]
    public async Task AVerdictOnAFinding_ShowsInTheThread_AndInItsReviewYield()
    {
        var threadId = await HandedOffWithTwoFindingsAsync();
        var findings = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("findings").EnumerateArray().ToList();
        Assert.All(findings, f => Assert.Equal(System.Text.Json.JsonValueKind.Null, f.GetProperty("verdict").ValueKind));

        await _host.PostAsync($"api/findings/{findings[0].GetProperty("id").GetGuid()}/verdict", new { verdict = "Real" }, HttpStatusCode.OK);
        await _host.PostAsync($"api/findings/{findings[1].GetProperty("id").GetGuid()}/verdict", new { verdict = "notworthfixing" }, HttpStatusCode.OK);

        var judged = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("verdict").GetString());
        Assert.Equal(["Real", "NotWorthFixing"], judged);

        var yield = await _host.GetAsync($"api/threads/{threadId}/review-yield");
        Assert.Equal(2, yield.GetProperty("findings").GetInt32());
        Assert.Equal(2, yield.GetProperty("judged").GetInt32());
        Assert.Equal(1, yield.GetProperty("real").GetInt32());
        // The scripted worker makes no model calls, so there is no review cost to rate against;
        // ReviewYieldTests covers the arithmetic.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, yield.GetProperty("realPer100KReviewTokens").ValueKind);
    }

    [Fact]
    public async Task TheReviewYieldOfEveryTask_HasARowEach_AndATotal()
    {
        var threadId = await HandedOffWithTwoFindingsAsync();

        var all = await _host.GetAsync("api/review-yield");

        var row = Assert.Single(all.GetProperty("threads").EnumerateArray());
        Assert.Equal(threadId, row.GetProperty("threadId").GetGuid());
        Assert.Equal(2, all.GetProperty("total").GetProperty("findings").GetInt32());
    }

    [Fact]
    public async Task AVerdict_CanBeCleared_AndAnUnknownVerdictOrFinding_IsRefused()
    {
        var threadId = await HandedOffWithTwoFindingsAsync();
        var id = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("findings")[0].GetProperty("id").GetGuid();

        await _host.PostAsync($"api/findings/{id}/verdict", new { verdict = "Wrong" }, HttpStatusCode.OK);
        await _host.PostAsync($"api/findings/{id}/verdict", new { verdict = (string?)null }, HttpStatusCode.OK);
        await _host.PostAsync($"api/findings/{id}/verdict", new { verdict = "Maybe" }, HttpStatusCode.BadRequest);
        await _host.PostAsync($"api/findings/{id}/verdict", new { verdict = "7" }, HttpStatusCode.BadRequest);
        await _host.PostAsync($"api/findings/{Guid.NewGuid()}/verdict", new { verdict = "Real" }, HttpStatusCode.NotFound);

        Assert.Equal(0, (await _host.GetAsync($"api/threads/{threadId}/review-yield")).GetProperty("judged").GetInt32());
    }
}
