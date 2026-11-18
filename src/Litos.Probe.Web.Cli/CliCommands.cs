using System.Globalization;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Detection;
using Litos.Probe.Web.Profiles;

namespace Litos.Probe.Web.Cli;

internal sealed record CliSettings(bool Headed, string? OutputFolder, TimeSpan Timeout, string? ProfilesFolder);

/// <summary>The CLI's commands, separate from argument parsing so they can be tested with a writer.</summary>
internal sealed class CliCommands(TextWriter output, CliSettings settings)
{
    public const int Passed = 0;
    public const int Failed = 1;
    public const int Environment = 2;
    public const int BadContract = 3;

    private readonly StackProfiles _profiles = StackProfiles.WithOverrides(settings.ProfilesFolder);

    public int Detect(string repo)
    {
        if (!Directory.Exists(repo))
            return Fail($"No such folder: {repo}", BadContract);

        var result = new StackDetector().Detect(repo, _profiles.All);
        output.WriteLine(result.Status switch
        {
            DetectionStatus.NotAWebApp => "Not a web app: no stack profile's markers matched.",
            DetectionStatus.StackNotSupported => "A web app, but its stack is not supported yet.",
            DetectionStatus.Detected => "Detected:",
            _ => "Several web apps; pick one with --dir:",
        });
        foreach (var c in result.Candidates)
        {
            var tier = c.Tier == StackProfiles.NotSupportedTier ? "not supported" : $"tier {c.Tier}";
            output.WriteLine($"  {c.ProfileId,-24} {(c.Directory.Length == 0 ? "." : c.Directory),-30} ({tier}; {string.Join(", ", c.Evidence)})");
        }

        return result.Status is DetectionStatus.Detected or DetectionStatus.Several ? Passed : Failed;
    }

    public async Task<int> DryRunAsync(string repo, string? profileId, string? directory, CancellationToken ct)
    {
        if (!Directory.Exists(repo))
            return Fail($"No such folder: {repo}", BadContract);

        var profile = profileId is null ? null : _profiles.Find(profileId);
        if (profileId is not null && profile is null)
            return Fail($"There is no stack profile '{profileId}'. Known: {string.Join(", ", _profiles.All.Select(p => p.Id).Order())}.", BadContract);

        if (profile is null || directory is null)
        {
            var detected = new StackDetector().Detect(repo, profile is null ? _profiles.All : [profile]).Best;
            if (detected is null)
                return Fail("No supported stack detected. Name one with --profile.", BadContract);
            profile ??= _profiles.Find(detected.ProfileId)!;
            directory ??= detected.Directory;
        }

        output.WriteLine($"Dry run of {profile.Id} in {(directory.Length == 0 ? "." : directory)}");
        var (options, sink) = Options("defaults");
        var report = await new ProbeWeb(_profiles).DryRunAsync(repo, profile, options, ct, directory);
        return await FinishAsync(report, sink, ct);
    }

    public async Task<int> GateAsync(string repo, string? contractPath, string stepsText, bool smoke, CancellationToken ct)
    {
        if (!Directory.Exists(repo))
            return Fail($"No such folder: {repo}", BadContract);

        GateScope scope;
        try
        {
            scope = GateScope.Parse(stepsText);
        }
        catch (FormatException ex)
        {
            return Fail($"--steps: {ex.Message}", BadContract);
        }

        contractPath ??= Path.Combine(repo, ContractYaml.FileName);
        if (!File.Exists(contractPath))
            return Fail($"No contract at {contractPath}. Write one, or try the stack's defaults with: litos-probe-web dry-run {repo}", BadContract);

        Core.Contract.WebAppContract contract;
        try
        {
            contract = ContractYaml.Load(contractPath);
        }
        catch (ContractFormatException ex)
        {
            return Fail(ex.Message, BadContract);
        }

        var engine = new ProbeWeb(_profiles);
        var errors = engine.Validate(contract);
        if (errors.Count > 0)
        {
            output.WriteLine($"The contract {contractPath} has {errors.Count} problem(s):");
            foreach (var error in errors)
                output.WriteLine($"  - {error}");
            return BadContract;
        }

        output.WriteLine($"{(smoke ? "Smoke" : "Gate")} of {repo} with {contractPath}");
        var (options, sink) = Options("file");
        var report = smoke
            ? await engine.SmokeAsync(repo, contract, options, ct)
            : await engine.GateAsync(repo, contract, scope, options, ct);
        return await FinishAsync(report, sink, ct);
    }

    private (ProbeWebOptions Options, FolderArtifactSink Sink) Options(string source)
    {
        var folder = settings.OutputFolder
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".probe-web", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var sink = new FolderArtifactSink(folder);
        // A developer's own machine: the app gets this shell's environment, and secrets come from
        // variables of the same name.
        var environment = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var options = new ProbeWebOptions(new EnvironmentSecretResolver(), sink, environment, settings.Timeout, Headless: !settings.Headed)
        {
            ContractSource = source,
            Progress = line => output.WriteLine($"  {line}"),
        };
        return (options, sink);
    }

    private async Task<int> FinishAsync(ProbeWebReport report, FolderArtifactSink sink, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(sink.Folder, "report.json"), report.ToJson(), ct);
        output.WriteLine();
        Summary.Write(output, report, sink.Folder);
        return ExitCode(report.Outcome);
    }

    internal static int ExitCode(ProbeWebOutcome outcome) => outcome switch
    {
        ProbeWebOutcome.Passed => Passed,
        ProbeWebOutcome.EnvironmentFailure => Environment,
        ProbeWebOutcome.InvalidContract => BadContract,
        _ => Failed,
    };

    private int Fail(string message, int code)
    {
        output.WriteLine(message);
        return code;
    }
}

/// <summary>The one-screen summary after a run.</summary>
internal static class Summary
{
    public static void Write(TextWriter output, ProbeWebReport report, string folder)
    {
        output.WriteLine($"{report.Kind} · {report.Outcome}{(report.Commit is null ? "" : $" · commit {report.Commit}")} · {report.Usage.WallSeconds:0.0}s · {report.Usage.ModelCalls} model calls");
        if (report.App is { } app)
        {
            output.WriteLine($"App: {app.BaseUrl} ({string.Join(", ", app.Processes.Select(p => $"{p.Name}:{p.Port}"))}), started in {app.StartSeconds:0.0}s");
            foreach (var service in app.Services)
                output.WriteLine($"Service: {service.Name} ({service.Image}) on {string.Join(", ", service.Ports.Select(p => $"{p.Key} {p.Value}"))}");
        }

        foreach (var check in report.Checks)
        {
            var mark = check.Status switch { CheckStatus.Passed => "✓", CheckStatus.Failed => "✗", _ => "–" };
            var tag = check.Classification is { } c ? $" [{c}]" : "";
            output.WriteLine($"  {mark} {check.Id}{tag}");
            if (check.Message is not null && (check.Status != CheckStatus.Passed || check.Classification == Classification.Flaky))
                output.WriteLine($"      {check.Message}");
            foreach (var line in check.Failed ? check.Evidence.Log.Take(5) : [])
                output.WriteLine($"      | {line}");
        }

        if (report.Lint is { } lint)
            output.WriteLine($"Lint: {lint.Pages} pages, {lint.Interactive} interactive elements, {lint.Unnamed} unnamed");
        if (report.Emails.Count > 0)
        {
            output.WriteLine($"Emails caught: {report.Emails.Count}");
            foreach (var email in report.Emails.Take(10))
                output.WriteLine($"  - to {string.Join(", ", email.To)}: {email.Subject}");
        }

        if (report.Findings.Count > 0)
        {
            output.WriteLine("Findings:");
            foreach (var finding in report.Findings)
                output.WriteLine($"  - {finding}");
        }

        output.WriteLine($"Report and artifacts: {folder}");
    }
}
