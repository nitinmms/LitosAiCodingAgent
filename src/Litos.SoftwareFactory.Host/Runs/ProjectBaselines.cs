using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>Where a project's baseline stands, for the Projects page.</summary>
public enum BaselineState
{
    NotMeasured,
    Measuring,
    Measured,

    /// <summary>The profile could not be run at all: the repository could not be cloned or read.</summary>
    CouldNotMeasure,
}

/// <summary>The projects being measured, and why the last attempt at others could not run.</summary>
public sealed record BaselineStates(IReadOnlySet<Guid> Measuring, IReadOnlyDictionary<Guid, string> Failures)
{
    /// <param name="hasBaseline">Whether the project has a baseline at its profile revision, read after this snapshot.</param>
    public (BaselineState State, string? Problem) StateOf(Guid projectId, bool hasBaseline) =>
        Measuring.Contains(projectId) ? (BaselineState.Measuring, null)
        : Failures.TryGetValue(projectId, out var problem) ? (BaselineState.CouldNotMeasure, problem)
        : (hasBaseline ? BaselineState.Measured : BaselineState.NotMeasured, null);
}

/// <summary>
/// A project's first baseline, measured as soon as it is registered or its profile is replaced
/// (m3-architecture.md §8) rather than at its first task, so an Admin sees at once whether the
/// profile builds and tests the repository. It runs on the project's reading copy at the default
/// branch's head, taking its turn with chats for the copy and with runs for a verification permit,
/// and is saved as an ordinary baseline: a task that starts from that commit reuses it.
/// </summary>
public sealed class ProjectBaselines(
    IFactoryStore store, FactoryOptions options, ReadingCopies readingCopies, IVerifier verifier, VerificationGate gate,
    IHostApplicationLifetime lifetime, ILogger<ProjectBaselines> logger)
{
    private readonly Lock _lock = new();

    /// <summary>The projects being measured, each with whether its profile changed since it started.</summary>
    private readonly Dictionary<Guid, bool> _measuring = [];

    /// <summary>Why the last attempt at a project's baseline could not run, until one does.</summary>
    private readonly Dictionary<Guid, string> _failures = [];

    /// <summary>The task of the latest measurement, for tests to wait on.</summary>
    internal Task? Latest { get; private set; }

    /// <summary>Starts measuring the project's baseline, or measures again once the one under way ends.</summary>
    public void Start(Guid projectId)
    {
        if (!options.BaselineOnRegistration)
            return;

        lock (_lock)
        {
            if (_measuring.ContainsKey(projectId))
            {
                _measuring[projectId] = true;
                return;
            }

            _measuring[projectId] = false;
            _failures.Remove(projectId);
            Latest = Task.Run(() => MeasureUntilCurrentAsync(projectId, lifetime.ApplicationStopping));
        }
    }

    /// <summary>
    /// What is being measured now. Taken before the saved baselines are read: a measurement saves
    /// its baseline before it stops counting as under way, so one that ends in between is seen as
    /// under way rather than as never measured.
    /// </summary>
    public BaselineStates Snapshot()
    {
        lock (_lock)
            return new BaselineStates(_measuring.Keys.ToHashSet(), new Dictionary<Guid, string>(_failures));
    }

    private async Task MeasureUntilCurrentAsync(Guid projectId, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (await store.GetProjectAsync(projectId, ct) is { } project)
                    await MeasureAsync(project, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The baseline of project {ProjectId} could not be measured.", projectId);
                lock (_lock)
                    _failures[projectId] = ex.Message;
            }

            lock (_lock)
            {
                // A profile replaced while this one ran is measured next, at its own revision.
                if (!_measuring[projectId] || ct.IsCancellationRequested)
                {
                    _measuring.Remove(projectId);
                    return;
                }

                _measuring[projectId] = false;
                _failures.Remove(projectId);
            }
        }
    }

    private async Task MeasureAsync(Project project, CancellationToken ct)
    {
        using var held = await readingCopies.HoldAsync(project.Id, ct);
        var workspace = readingCopies.For(project);
        await workspace.EnsureClonedAsync(ct);
        await workspace.FetchAsync(ct);
        await workspace.CheckoutForReadingAsync(project.DefaultBranch, ct);
        var commit = (await workspace.GetStatusAsync(ct)).HeadCommit;
        if (await store.FindBaselineAsync(project.Id, commit, project.ProfileRevision, ct) is not null)
            return;

        var directory = Path.Combine(options.DataDirectory, "projects", project.Id.ToString("N"), $"baseline-r{project.ProfileRevision}-{commit[..Math.Min(12, commit.Length)]}");
        var request = new VerificationRequest(workspace.Path, VerificationProfile.Parse(project.VerificationProfileJson), ChangedFiles: null, directory)
        {
            TempDirectory = Path.Combine(directory, "tmp"),
        };

        VerificationOutcome outcome;
        using (await gate.EnterAsync(null, ct))
            outcome = await verifier.VerifyAsync(request, ct);

        await store.SaveVerificationAsync(new VerificationRecord
        {
            ProjectId = project.Id,
            Kind = VerificationKind.Baseline,
            Commit = commit,
            ProfileRevision = project.ProfileRevision,
            Build = outcome.Build,
            UnitTests = outcome.UnitTests,
            Coverage = outcome.Coverage,
            PassedCount = outcome.PassedCount,
            FailedCount = outcome.FailedCount,
            SkippedCount = outcome.SkippedCount,
            OutcomeJson = JsonSerializer.Serialize(outcome, FactoryWire.Json),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}
