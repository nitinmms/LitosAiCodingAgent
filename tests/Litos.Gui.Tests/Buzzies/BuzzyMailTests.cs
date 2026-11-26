using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Gui.Buzzies;
using Litos.Mail;
using Litos.Tools.Shell;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// The Gmail connector as a Buzzy sees it (Implement_Buzzies.md, Phase 4). What matters here is the
/// intersection of two rules: a Buzzy gets mail tools only if its profile grants them, and only if a
/// mailbox is actually connected — and a routine gets the read-only three, never the two that need a
/// person to answer an approval card.
/// </summary>
public class BuzzyMailPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static ToolRegistry FullRegistry()
    {
        var tools = new List<ITool>();
        foreach (var name in BuzzyToolNames.PathScoped.Concat(
                     [BuzzyToolNames.WebSearch, BuzzyToolNames.Skill, BuzzyToolNames.Artifact, BuzzyToolNames.Shell]))
            tools.Add(new RecordingTool(name, pathRequired: BuzzyToolNames.PathScoped.Contains(name)));
        return new ToolRegistry(tools);
    }

    private static MailConnection ConnectedMail(TempDir temp, bool approvedDrafting = true)
    {
        var settings = new MailSettingsStore(Path.Combine(temp.Path, "gmail.json"));
        var scopes = approvedDrafting
            ? new[] { MailOAuth.ReadOnlyScope, MailOAuth.ComposeScope }
            : [MailOAuth.ReadOnlyScope];
        settings.Connect(settings.Load(), new MailAccount("me@example.com", scopes, Now));
        var tokens = new MailTokenProvider(new TestCredentialStore(), settings, new HttpClient());
        return new MailConnection(settings, tokens, new HttpClient());
    }

    private static BuzzyProfile Buzzy(BuzzyStore store, params string[] tools) =>
        store.Create(BuzzyTemplates.Buddy, "Penny", "#FFC21A", Now, tools, soul: "test");

    private static ToolRegistry Build(BuzzyStore store, BuzzyProfile profile, BuzzyToolContext context) =>
        BuzzyToolPolicy.Build(FullRegistry(), profile, context);

    [Fact]
    public void AMailGrant_WithNoMailboxConnected_AddsNoTools()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var profile = Buzzy(store, BuzzyToolNames.ReadFile, BuzzyToolNames.MailRead);

        var registry = Build(store, profile, new BuzzyToolContext(store, new PendingApprovalStore(), Mail: null));

        Assert.DoesNotContain(BuzzyToolNames.MailRead, registry.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void AConnectedMailbox_ButNoGrant_AddsNoTools()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var mail = ConnectedMail(temp);
        var profile = Buzzy(store, BuzzyToolNames.ReadFile);

        var registry = Build(store, profile,
            new BuzzyToolContext(store, new PendingApprovalStore(), Mail: mail, MailApproval: new FakeMailApproval()));

        Assert.DoesNotContain(registry.Schemas, s => s.Name.StartsWith("mail_", StringComparison.Ordinal));
    }

    [Fact]
    public void AGrantedAndConnectedBuzzy_GetsExactlyTheToolsItAskedFor()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var mail = ConnectedMail(temp);
        var profile = Buzzy(store, BuzzyToolNames.MailSearch, BuzzyToolNames.MailRead, BuzzyToolNames.Artifact);

        var names = Build(store, profile,
                new BuzzyToolContext(store, new PendingApprovalStore(), Mail: mail, MailApproval: new FakeMailApproval()))
            .Schemas.Select(s => s.Name).ToHashSet();

        Assert.Contains(BuzzyToolNames.MailSearch, names);
        Assert.Contains(BuzzyToolNames.MailRead, names);
        Assert.DoesNotContain(BuzzyToolNames.MailDraft, names);
        Assert.DoesNotContain(BuzzyToolNames.MailDownloadAttachment, names);
    }

    [Fact]
    public void Drafting_IsDroppedWhenComposeWasNotActuallyGranted()
    {
        // The profile grants it; Google's consent screen didn't. The tool must not appear, or a turn
        // would offer the model something that can only fail.
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var mail = ConnectedMail(temp, approvedDrafting: false);
        var profile = Buzzy(store, BuzzyToolNames.MailRead, BuzzyToolNames.MailDraft);

        var names = Build(store, profile,
                new BuzzyToolContext(store, new PendingApprovalStore(), Mail: mail, MailApproval: new FakeMailApproval()))
            .Schemas.Select(s => s.Name).ToHashSet();

        Assert.Contains(BuzzyToolNames.MailRead, names);
        Assert.DoesNotContain(BuzzyToolNames.MailDraft, names);
    }

    [Fact]
    public void TheBuddyTemplate_GrantsTheWholeMailSet()
    {
        Assert.Contains(BuzzyToolNames.MailSearch, BuzzyTemplates.Buddy.Tools);
        Assert.Contains(BuzzyToolNames.MailDraft, BuzzyTemplates.Buddy.Tools);
        Assert.DoesNotContain(BuzzyToolNames.MailRead, BuzzyTemplates.Scout.Tools);
        Assert.DoesNotContain(BuzzyToolNames.MailRead, BuzzyTemplates.Builder.Tools);
    }

    [Fact]
    public void EveryMailToolIsGrantable_SoAHandWrittenProfileCannotSmuggleMore()
    {
        foreach (var name in BuzzyToolNames.Mail)
            Assert.Contains(name, BuzzyToolPolicy.Grantable);

        Assert.DoesNotContain("mail_send", BuzzyToolPolicy.Grantable);
        Assert.DoesNotContain("mail_delete", BuzzyToolPolicy.Grantable);
    }

    [Fact]
    public void SetTool_AddsAndRemovesAGrant_AndPersists()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var profile = store.Create(BuzzyTemplates.Scout, "Scout", "#2FB457", Now);

        Assert.DoesNotContain(BuzzyToolNames.MailRead, profile.Tools);
        store.SetTool(profile.Id, BuzzyToolNames.MailRead, granted: true);
        Assert.Contains(BuzzyToolNames.MailRead, store.Get(profile.Id)!.Tools);

        store.SetTool(profile.Id, BuzzyToolNames.MailRead, granted: false);
        Assert.DoesNotContain(BuzzyToolNames.MailRead, store.Get(profile.Id)!.Tools);
    }

    [Fact]
    public void SetTool_RefusesANameThePolicyWouldIgnoreAnyway()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var profile = store.Create(BuzzyTemplates.Buddy, "Penny", "#FFC21A", Now);

        // Not merely ignored: a store that silently "grants" something the policy drops would let a
        // settings window report success for a grant that can never take effect.
        Assert.Throws<ArgumentException>(() => store.SetTool(profile.Id, "mail_send", granted: true));
        Assert.Throws<ArgumentException>(() => store.SetTool(profile.Id, "mcp__x__y", granted: true));
    }

    [Fact]
    public void AnUnattendedRun_GetsOnlyTheReadOnlyMailTools()
    {
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var mail = ConnectedMail(temp);
        var profile = Buzzy(store, [.. BuzzyToolNames.Mail]);

        var names = Build(store, profile,
                new BuzzyToolContext(store, new PendingApprovalStore(), Unattended: true, Mail: mail))
            .Schemas.Select(s => s.Name).ToHashSet();

        Assert.Contains(BuzzyToolNames.MailSearch, names);
        Assert.Contains(BuzzyToolNames.MailRead, names);
        Assert.Contains(BuzzyToolNames.MailSummarise, names);
        Assert.DoesNotContain(BuzzyToolNames.MailDraft, names);
        Assert.DoesNotContain(BuzzyToolNames.MailDownloadAttachment, names);
    }

    [Fact]
    public void MailTools_AreNotTakenFromTheSharedRegistry_EvenIfItHappensToHaveTheSameName()
    {
        // A mail tool has to be the broker's, with its approvals and untrusted wrapping — never some
        // other tool that merely shares the name.
        using var temp = new TempDir();
        var store = new BuzzyStore(temp.Path);
        var profile = Buzzy(store, BuzzyToolNames.MailSearch);

        var registry = BuzzyToolPolicy.Build(
            new ToolRegistry([new RecordingTool(BuzzyToolNames.MailSearch, pathRequired: false)]),
            profile,
            new BuzzyToolContext(store, new PendingApprovalStore(), Mail: null));

        Assert.Empty(registry.Schemas);
    }
}

/// <summary>Approves nothing, so a test that forgets an approval can't silently pass.</summary>
internal sealed class FakeMailApproval : IMailApproval
{
    public Task<bool> ApproveAsync(string summary, string? detail, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>A credential store that keeps nothing, for policies that never read a token.</summary>
internal sealed class TestCredentialStore : ICredentialStore
{
    public string? Read(string name) => null;
    public void Write(string name, string secret) { }
    public void Delete(string name) { }
}

/// <summary>
/// The approval card a mail tool raises: the same PendingApprovalStore the transcript renders, so a
/// download or a draft shows as an Approve/Deny card naming the Buzzy that asked.
/// </summary>
public class PendingMailApprovalTests
{
    [Fact]
    public async Task Approving_LetsTheToolProceed()
    {
        var approvals = new PendingApprovalStore();
        PendingApproval? asked = null;
        approvals.Added += a =>
        {
            asked = a;
            approvals.Resolve(a.Id, ApprovalDecision.Approve);
        };

        var approved = await new PendingMailApproval(approvals, "Buddy").ApproveAsync("Fetch invoice.pdf", "48 KB", default);

        Assert.True(approved);
        Assert.NotNull(asked);
        Assert.Equal("mail", asked!.Preview.ToolName);
        Assert.Contains("Buddy", asked.Preview.Summary);
        Assert.Contains("Fetch invoice.pdf", asked.Preview.Summary);
        Assert.Equal("48 KB", asked.Preview.DiffOrCommand);
    }

    [Fact]
    public async Task Denying_StopsIt()
    {
        var approvals = new PendingApprovalStore();
        approvals.Added += a => approvals.Resolve(a.Id, ApprovalDecision.Deny);

        Assert.False(await new PendingMailApproval(approvals).ApproveAsync("Save a draft", null, default));
    }

    [Fact]
    public async Task ApproveAlways_IsTreatedAsApprovingThisOneRequest()
    {
        // Mail asks per action, not per session: there is no standing grant to record in this phase.
        var approvals = new PendingApprovalStore();
        approvals.Added += a => approvals.Resolve(a.Id, ApprovalDecision.ApproveAlways);

        Assert.True(await new PendingMailApproval(approvals).ApproveAsync("Save a draft", null, default));
    }

    [Fact]
    public async Task NobodyAnswering_TimesOutAsARefusalRatherThanHanging()
    {
        var approvals = new PendingApprovalStore(TimeSpan.FromMilliseconds(50));

        Assert.False(await new PendingMailApproval(approvals).ApproveAsync("Fetch x.pdf", null, default));
    }
}
