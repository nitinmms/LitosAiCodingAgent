using System.Text.Json;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Persistence;

/// <summary>Project scenarios with revisions and approval (README_Litos.Probe.Web.md W3.6).</summary>
public abstract partial class FactoryStoreContract
{
    private static Func<int, string> ScenarioJson(string id) => revision => $$"""{"id":"{{id}}","revision":{{revision}},"steps":[]}""";

    private Task<ProjectScenario> ReviseAsync(Project project, string id, int minutes = 0) =>
        Store.AddScenarioRevisionAsync(project.Id, id, ScenarioJson(id), "Title " + id, "Criterion " + id, Admin, T0.AddMinutes(minutes), default);

    [SkippableFact]
    public async Task Scenarios_GetTheNextRevision_WrittenIntoTheirJson_AsDrafts()
    {
        var project = await AddProjectAsync();

        var first = await ReviseAsync(project, "cancel");
        var second = await ReviseAsync(project, "cancel", 1);
        var other = await ReviseAsync(project, "book", 2);

        Assert.Equal((1, 2, 1), (first.Revision, second.Revision, other.Revision));
        Assert.All(new[] { first, second, other }, s => Assert.Equal(ScenarioStatus.Draft, s.Status));
        var listed = await Store.ListScenariosAsync(project.Id, default);
        Assert.Equal(["book@1", "cancel@2", "cancel@1"], listed.Select(s => $"{s.ScenarioId}@{s.Revision}"));
        // jsonb does not keep the text as written, so compare what it says.
        Assert.Equal(2, JsonDocument.Parse(listed[1].ScenarioJson).RootElement.GetProperty("revision").GetInt32());
        Assert.Equal(("Title cancel", "Criterion cancel", Admin), (listed[1].Title, listed[1].Criterion, listed[1].CreatedBy));
        Assert.Empty(await Store.ApprovedScenariosAsync(project.Id, default));
    }

    [SkippableFact]
    public async Task Approving_A_Revision_Supersedes_The_One_Approved_Before()
    {
        var project = await AddProjectAsync();
        await ReviseAsync(project, "cancel");
        await ReviseAsync(project, "cancel", 1);

        var approved1 = await Store.ApproveScenarioAsync(project.Id, "cancel", 1, Admin, T0.AddMinutes(2), default);
        Assert.Equal((ScenarioStatus.Approved, Admin, T0.AddMinutes(2)), (approved1.Status, approved1.ApprovedBy, approved1.ApprovedAt));
        await Store.ApproveScenarioAsync(project.Id, "cancel", 2, Admin, T0.AddMinutes(3), default);

        var listed = (await Store.ListScenariosAsync(project.Id, default)).ToDictionary(s => s.Revision);
        Assert.Equal(ScenarioStatus.Approved, listed[2].Status);
        Assert.Equal(ScenarioStatus.Superseded, listed[1].Status);
        Assert.Equal(2, Assert.Single(await Store.ApprovedScenariosAsync(project.Id, default)).Revision);
    }

    [SkippableFact]
    public async Task Only_A_Draft_Can_Be_Approved()
    {
        var project = await AddProjectAsync();
        await ReviseAsync(project, "cancel");
        await Store.ApproveScenarioAsync(project.Id, "cancel", 1, Admin, T0, default);

        await Assert.ThrowsAsync<StoreConflictException>(() => Store.ApproveScenarioAsync(project.Id, "cancel", 1, Admin, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.ApproveScenarioAsync(project.Id, "cancel", 9, Admin, T0, default));
    }

    [SkippableFact]
    public async Task Retiring_Removes_Every_Revision_And_A_New_Revision_Brings_It_Back_As_A_Draft()
    {
        var project = await AddProjectAsync();
        await ReviseAsync(project, "cancel");
        await Store.ApproveScenarioAsync(project.Id, "cancel", 1, Admin, T0, default);
        await ReviseAsync(project, "cancel", 1);

        Assert.Equal(2, await Store.RetireScenarioAsync(project.Id, "cancel", Admin, T0.AddMinutes(5), default));
        Assert.Empty(await Store.ApprovedScenariosAsync(project.Id, default));
        Assert.All(await Store.ListScenariosAsync(project.Id, default), s => Assert.Equal((ScenarioStatus.Retired, (DateTimeOffset?)T0.AddMinutes(5)), (s.Status, s.RetiredAt)));
        Assert.Equal(0, await Store.RetireScenarioAsync(project.Id, "cancel", Admin, T0, default));
        await Assert.ThrowsAsync<StoreConflictException>(() => Store.ApproveScenarioAsync(project.Id, "cancel", 2, Admin, T0, default));

        var revived = await ReviseAsync(project, "cancel", 6);
        Assert.Equal((3, ScenarioStatus.Draft), (revived.Revision, revived.Status));
    }

    [SkippableFact]
    public async Task Scenarios_Belong_To_Their_Project()
    {
        var a = await AddProjectAsync("a");
        var b = await AddProjectAsync("b");
        await ReviseAsync(a, "cancel");
        await Store.ApproveScenarioAsync(a.Id, "cancel", 1, Admin, T0, default);

        Assert.Empty(await Store.ListScenariosAsync(b.Id, default));
        Assert.Empty(await Store.ApprovedScenariosAsync(b.Id, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() => Store.RetireScenarioAsync(b.Id, "cancel", Admin, T0, default));
        await Assert.ThrowsAsync<StoreNotFoundException>(() =>
            Store.AddScenarioRevisionAsync(Guid.NewGuid(), "x", ScenarioJson("x"), null, null, Admin, T0, default));
    }
}
