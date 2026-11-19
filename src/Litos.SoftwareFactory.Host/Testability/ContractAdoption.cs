using System.Text.Json;
using Litos.Probe.Web;
using Litos.Probe.Web.Core.Contract;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>What adopting the default branch's contract would change, shown before it is done.</summary>
/// <param name="Commit">The default branch's head it was read at; an adoption must name it.</param>
public sealed record AdoptionPreview(
    string Commit, bool Found, IReadOnlyList<string> Problems, IReadOnlyList<string> MissingSecrets, IReadOnlyList<DiffLine> Diff, bool Changes)
{
    public bool CanAdopt => Found && Problems.Count == 0 && MissingSecrets.Count == 0 && Changes;
}

public sealed class AdoptionRefusedException(string message, bool conflict = false) : Exception(message)
{
    /// <summary>The default branch moved since the preview: check again rather than adopt blind.</summary>
    public bool Conflict { get; } = conflict;
}

/// <summary>
/// Adoption (testability-architecture.md §5): the <c>litos.test.yaml</c> on the default branch,
/// never a task branch, parsed onto the profile's v3 blocks, shown as a diff, and saved as a new
/// profile revision with an audit entry. Only the DB copy is ever used; the file is a draft.
/// </summary>
public sealed class ContractAdoption(
    IFactoryStore store, ReadingCopies readingCopies, ITestabilityEngine engine, FactorySettings settings, IClock clock)
{
    public async Task<AdoptionPreview> PreviewAsync(Project project, CancellationToken ct)
    {
        var (commit, contract, problems) = await ReadMainAsync(project, ct);
        var current = VerificationProfile.Parse(project.VerificationProfileJson).WebApp();
        if (contract is null)
            return new AdoptionPreview(commit, Found: problems.Count > 0, problems, [], [], Changes: false);

        var diff = ContractDiff.Lines(current is null ? "" : Text(current), Text(contract));
        return new AdoptionPreview(commit, true, problems, TestabilityApi.MissingSecrets(project.Id, contract, settings), diff, ContractDiff.Changes(diff));
    }

    /// <summary>Saves the default branch's contract as the project's, if it is still the one at <paramref name="commit"/>.</summary>
    /// <exception cref="AdoptionRefusedException">It moved, does not run, or needs secrets not set yet.</exception>
    public async Task<Project> AdoptAsync(Project project, string commit, Guid actorId, CancellationToken ct)
    {
        var (head, contract, problems) = await ReadMainAsync(project, ct);
        if (!string.Equals(head, commit, StringComparison.OrdinalIgnoreCase))
            throw new AdoptionRefusedException($"The default branch moved to {Short(head)} since {Short(commit)} was shown: review the contract again.", conflict: true);
        if (contract is null)
            throw new AdoptionRefusedException(problems.Count > 0
                ? "litos.test.yaml on the default branch does not run as written: " + string.Join(" ", problems)
                : "There is no litos.test.yaml on the default branch.");
        if (problems.Count > 0)
            throw new AdoptionRefusedException("litos.test.yaml on the default branch does not run as written: " + string.Join(" ", problems));
        if (TestabilityApi.MissingSecrets(project.Id, contract, settings) is { Count: > 0 } missing)
            throw new AdoptionRefusedException($"Set the project's secrets {string.Join(", ", missing)} first: the contract uses them.");

        var adopted = contract with
        {
            Testability = contract.Testability with { AdoptedFrom = new ContractOrigin { Commit = head, Path = ContractYaml.FileName } },
        };
        var profile = VerificationProfile.Parse(project.VerificationProfileJson).WithWebApp(adopted);
        var updated = await store.UpdateProjectProfileAsync(project.Id, profile.ToJson(), actorId, clock.UtcNow, ct);
        await store.AddAuditAsync(new AuditEvent
        {
            ActorId = actorId,
            Action = AuditActions.TestabilityAdopt,
            TargetType = AuditTargets.Project,
            TargetId = project.Id,
            ProjectId = project.Id,
            DetailsJson = JsonSerializer.Serialize(new { Commit = head, Path = ContractYaml.FileName, updated.ProfileRevision }),
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);
        return updated;
    }

    /// <summary>Whether the file on disk says something other than the adopted contract.</summary>
    public static bool Differs(string root, WebAppContract adopted)
    {
        try
        {
            return Text(ContractYaml.Load(Path.Combine(root, ContractYaml.FileName))) != Text(adopted);
        }
        catch (Exception ex) when (ex is ContractFormatException or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// The contract as yaml, without where it was adopted from and with its data maps in key order,
    /// so two copies compare by what they say: the adopted copy comes back from a jsonb column,
    /// which does not keep the order the file wrote its environment or roles in.
    /// </summary>
    public static string Text(WebAppContract contract) =>
        ContractYaml.Canonical(contract with { Testability = contract.Testability with { AdoptedFrom = null } });

    private async Task<(string Commit, WebAppContract? Contract, IReadOnlyList<string> Problems)> ReadMainAsync(Project project, CancellationToken ct)
    {
        using var held = await readingCopies.HoldAsync(project.Id, ct);
        var workspace = readingCopies.For(project);
        await workspace.EnsureClonedAsync(ct);
        await workspace.FetchAsync(ct);
        await workspace.CheckoutForReadingAsync(project.DefaultBranch, ct);
        var commit = (await workspace.GetStatusAsync(ct)).HeadCommit;

        var path = Path.Combine(workspace.Path, ContractYaml.FileName);
        if (!File.Exists(path))
            return (commit, null, []);
        try
        {
            var contract = ContractYaml.Load(path);
            var problems = engine.Validate(contract);
            return (commit, contract, problems);
        }
        catch (ContractFormatException ex)
        {
            return (commit, null, [ex.Message]);
        }
    }

    private static string Short(string commit) => commit[..Math.Min(7, commit.Length)];
}
