using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Factory settings in the database, edited by Admins (m3-architecture.md §3, §5): seeded from the
/// host's environment on first start, then read by what they govern without a restart.
/// </summary>
public sealed class SettingsApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<JsonElement> SettingsAsync() => await _host.GetAsync("api/admin/settings");

    private async Task<HttpResponseMessage> PutBudgetsAsync(long revision, object settings, HttpClient? client = null) =>
        await (client ?? _host.Client).PutAsJsonAsync("api/admin/settings/budgets", new { revision, settings });

    private async Task<long> SaveBudgetsAsync(BudgetSettings settings)
    {
        var revision = (await SettingsAsync()).GetProperty("budgets").GetProperty("revision").GetInt64();
        using var response = await PutBudgetsAsync(revision, settings);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();
    }

    // ---- Seeding and reading ----

    [Fact]
    public async Task TheFirstStart_WritesTheBudgetsFromTheHostsEnvironment()
    {
        var budgets = (await SettingsAsync()).GetProperty("budgets");

        Assert.Equal(1, budgets.GetProperty("revision").GetInt64());
        var settings = budgets.GetProperty("settings");
        Assert.Equal(_host.Options.SlotCap, settings.GetProperty("slotCap").GetInt32()); // TestHost runs one task at a time
        Assert.Equal(_host.Options.DefaultBudget, settings.GetProperty("defaultTaskBudget").GetInt64());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("maximumTaskBudget").ValueKind);
    }

    [Fact]
    public async Task TheProviderKey_IsSeededAsASecret_AndNeverShown()
    {
        var body = (await SettingsAsync()).GetRawText();

        var secrets = (await SettingsAsync()).GetProperty("secrets").EnumerateArray().ToList();
        var key = Assert.Single(secrets, s => s.GetProperty("name").GetString() == "provider:openrouter");
        Assert.Equal(JsonValueKind.Null, key.GetProperty("setBy").ValueKind); // seeded by the host, not an Admin
        Assert.DoesNotContain(_host.Options.OpenRouterApiKey!, body);
        Assert.Equal(_host.Options.OpenRouterApiKey, await _host.App.Services.GetRequiredService<FactorySettings>().SecretAsync("provider:openrouter", default));
    }

    [Fact]
    public async Task SeedingAgain_LeavesAnAdminsChangeAlone()
    {
        await SaveBudgetsAsync(new BudgetSettings { SlotCap = 3 });
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();

        Assert.False(await SettingsSeeding.SeedAsync(settings, _host.Options, default));

        Assert.Equal(3, settings.Budgets.SlotCap);
        Assert.Equal(2, settings.RevisionOf(SettingsSections.Budgets));
    }

    [Fact]
    public async Task ASavedChange_IsWhatTheNextHostLoads()
    {
        await SaveBudgetsAsync(new BudgetSettings { SlotCap = 5, MaximumTaskBudget = 900_000 });

        var restarted = new FactorySettings(_host.Store, _host.App.Services.GetRequiredService<ISecretProtector>(), new SystemClock());
        await restarted.LoadAsync(default);

        Assert.Equal((5, 900_000L), (restarted.Budgets.SlotCap, restarted.Budgets.MaximumTaskBudget!.Value));
    }

    // ---- Changing them ----

    [Fact]
    public async Task AChange_RaisesTheRevision_AndIsAudited()
    {
        Assert.Equal(2, await SaveBudgetsAsync(new BudgetSettings { SlotCap = 3 }));

        var admin = await _host.AdminIdAsync();
        var audit = await _host.Store.ListAuditAsync(null, 10, default);
        Assert.Contains(audit, a => a.Action == AuditActions.SettingsUpdate && a.ActorId == admin);
    }

    [Fact]
    public async Task AChangeFromAStaleRevision_Is409_AndKeepsTheOtherAdminsChange()
    {
        await SaveBudgetsAsync(new BudgetSettings { SlotCap = 3 });

        using var stale = await PutBudgetsAsync(1, new BudgetSettings { SlotCap = 9 });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("changed by someone else", await stale.Content.ReadAsStringAsync());
        Assert.Equal(3, (await SettingsAsync()).GetProperty("budgets").GetProperty("settings").GetProperty("slotCap").GetInt32());
    }

    [Fact]
    public async Task AnUnusableChange_Is400_WithEveryReason_AndChangesNothing()
    {
        using var response = await PutBudgetsAsync(1, new BudgetSettings { SlotCap = 0, RepairCyclesPerRun = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("errors").GetArrayLength());
        Assert.Equal(1, (await SettingsAsync()).GetProperty("budgets").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task AMember_CannotReadOrChangeSettings()
    {
        await _host.CreateMemberAsync("ben");
        using var ben = await _host.SignedInAsync("ben");

        using var read = await ben.GetAsync("api/admin/settings");
        using var write = await PutBudgetsAsync(1, new BudgetSettings(), ben);
        using var secret = await ben.PutAsJsonAsync("api/admin/secrets/github", new { value = "x" });

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden], [read.StatusCode, write.StatusCode, secret.StatusCode]);
    }

    // ---- Secrets ----

    [Fact]
    public async Task ASecret_IsSetAndCleared_ButNeverReturned()
    {
        using (var set = await _host.Client.PutAsJsonAsync("api/admin/secrets/github", new { value = " ghp_secret_value " }))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var body = (await SettingsAsync()).GetRawText();
        Assert.DoesNotContain("ghp_secret_value", body);
        var github = Assert.Single((await SettingsAsync()).GetProperty("secrets").EnumerateArray(), s => s.GetProperty("name").GetString() == "github");
        Assert.Equal(await _host.AdminIdAsync(), github.GetProperty("setBy").GetGuid());
        Assert.Equal("ghp_secret_value", await _host.App.Services.GetRequiredService<FactorySettings>().SecretAsync("github", default));

        using (var cleared = await _host.Client.DeleteAsync("api/admin/secrets/github"))
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        using (var again = await _host.Client.DeleteAsync("api/admin/secrets/github"))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData("anything", "x")]
    [InlineData("github", "   ")]
    public async Task AnUnknownSecret_OrAnEmptyValue_Is400(string name, string value)
    {
        using var response = await _host.Client.PutAsJsonAsync($"api/admin/secrets/{name}", new { value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- What the budgets govern ----

    [Fact]
    public async Task ANewThread_GetsTheDefaultBudget_FromSettings()
    {
        await SaveBudgetsAsync(new BudgetSettings { DefaultTaskBudget = 123_000 });

        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        Assert.Equal(123_000, (await _host.ThreadAsync(threadId)).Thread.BudgetCap);
        Assert.Equal(123_000, (await _host.GetAsync("api/settings")).GetProperty("defaultBudget").GetInt64());
    }

    [Fact]
    public async Task ABudgetAboveTheMaximum_IsRefused_AtCreationAndWhenRaised()
    {
        await SaveBudgetsAsync(new BudgetSettings { DefaultTaskBudget = 300_000, MaximumTaskBudget = 600_000 });
        var projectId = await _host.RegisterProjectAsync();

        var refused = await _host.PostAsync("api/threads", new { projectId, title = "Too big", typeLabel = "feature", budgetCap = 700_000 }, HttpStatusCode.BadRequest);
        Assert.Contains("at most 600,000 tokens", refused.GetProperty("error").GetString());

        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = 700_000 }, HttpStatusCode.BadRequest);
        await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = (long?)null }, HttpStatusCode.BadRequest);
        Assert.Equal(300_000, (await _host.ThreadAsync(threadId)).Thread.BudgetCap);
        Assert.Equal(600_000, (await _host.GetAsync("api/settings")).GetProperty("maximumBudget").GetInt64());
    }

    /// <summary>TestHost's slot cap is 1: raising it lets a second task start, with no restart.</summary>
    [Fact]
    public async Task RaisingTheSlotCap_LetsAWaitingTaskStart()
    {
        var started = 0;
        _host.Workers.Default = async call =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        };
        var first = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("salesapp"));
        var second = await _host.CreateThreadAsync(await _host.RegisterProjectAsync("filedb"));
        await _host.DelegateAsync(first);
        await _host.DelegateAsync(second);
        await WaitUntilAsync(() => Volatile.Read(ref started) == 1);
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref started)); // the cap of 1 holds the second back

        await SaveBudgetsAsync(new BudgetSettings { SlotCap = 2 });

        await WaitUntilAsync(() => Volatile.Read(ref started) == 2);
        await _host.PostAsync($"api/threads/{first}/cancel", null, HttpStatusCode.Accepted);
        await _host.PostAsync($"api/threads/{second}/cancel", null, HttpStatusCode.Accepted);
    }

    /// <summary>With no repair cycles, failing tests are not sent back to the agent to fix.</summary>
    [Fact]
    public async Task RepairCycles_ComeFromSettings_WhenTheRunStarts()
    {
        await SaveBudgetsAsync(new BudgetSettings { RepairCyclesPerRun = 0 });
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("Orders.Export_Works"));
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);

        await WaitUntilAsync(async () => (await _host.ThreadAsync(threadId)).Thread.State is not (LifecycleState.Queued or LifecycleState.Running));
        Assert.DoesNotContain(_host.Workers.Turns, t => t.Kind == TurnKind.Repair);
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition was not met in time.");
            await Task.Delay(40);
        }
    }
}
