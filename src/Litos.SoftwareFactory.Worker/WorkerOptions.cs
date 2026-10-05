using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Everything a worker is told at launch. The non-secret part arrives as arguments, the secret
/// part through the environment — command lines are visible to other processes on the machine.
/// Nothing here is ever read from, or written to, the ~/.litos/config.json other Litos faces share.
/// </summary>
public sealed record WorkerOptions(
    string RunId, string Secret, Uri HostUrl, string Provider, string Model, int? ContextLength,
    string DataDirectory, int? ParentProcessId, bool PtcEnabled)
{
    /// <summary>
    /// How many lines a read_file with no 'limit' returns, and the most any one read returns.
    /// R1 printed ten whole files of 9,000 to 24,000 characters while exploring, and paused on
    /// budget three times; briefs asking for less did not change that. A limit per file keeps every
    /// file of a many-file script visible, each as its start and a note on how to read the rest.
    /// </summary>
    public const int ReadFileDefaultLines = 400;

    /// <inheritdoc cref="ReadFileDefaultLines"/>
    public const int ReadFileMaxBytes = 20 * 1024;

    /// <summary>
    /// A safety cap on what one kernel eval returns, in characters (about 6,000 tokens): its start
    /// and its end are kept, since a test run's summary and failures come last. An 8,000-character
    /// cap that kept only the start cut five of seven files from one script, and the agent spent
    /// extra calls reading them again; the per-file read limit does that job now.
    /// </summary>
    public const int KernelOutputCapChars = 24_000;

    /// <summary>Transcripts live in the factory data directory, so runs never appear in VS Code's
    /// session history.</summary>
    public string SessionsDirectory => Path.Combine(DataDirectory, "runs", RunId, "sessions");

    /// <exception cref="WorkerOptionsException">Something required is missing or malformed.</exception>
    public static WorkerOptions Parse(IReadOnlyList<string> arguments, Func<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i++)
        {
            var name = arguments[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
                throw new WorkerOptionsException($"Unexpected argument '{name}'.");
            if (i + 1 >= arguments.Count)
                throw new WorkerOptionsException($"Argument '{name}' has no value.");
            values[name] = arguments[++i];
        }

        string Required(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new WorkerOptionsException($"Missing required argument '{name}'.");

        string RequiredVariable(string name) =>
            environment(name) is { Length: > 0 } value
                ? value
                : throw new WorkerOptionsException($"Missing required environment variable '{name}'.");

        int? OptionalInt(string name)
        {
            if (!values.TryGetValue(name, out var text))
                return null;
            return int.TryParse(text, out var number) && number > 0
                ? number
                : throw new WorkerOptionsException($"Argument '{name}' must be a positive whole number, but was '{text}'.");
        }

        var hostUrlText = RequiredVariable(FactoryWire.HostUrlVariable);
        if (!Uri.TryCreate(hostUrlText, UriKind.Absolute, out var hostUrl) || hostUrl.Scheme is not ("http" or "https"))
            throw new WorkerOptionsException($"{FactoryWire.HostUrlVariable} is not an http(s) URL.");

        var ptc = values.GetValueOrDefault("--ptc", "on");
        if (ptc is not ("on" or "off"))
            throw new WorkerOptionsException($"Argument '--ptc' must be 'on' or 'off', but was '{ptc}'.");

        return new WorkerOptions(
            RunId: RequiredVariable(FactoryWire.RunIdVariable),
            Secret: RequiredVariable(FactoryWire.WorkerSecretVariable),
            HostUrl: hostUrl,
            Provider: Required("--provider"),
            Model: Required("--model"),
            ContextLength: OptionalInt("--context-length"),
            DataDirectory: Path.GetFullPath(Required("--data-dir")),
            ParentProcessId: OptionalInt("--parent-pid"),
            PtcEnabled: ptc == "on");
    }
}

public static class WorkerLaunchVariables
{
    /// <summary>
    /// Removes the launch secret, host URL and run id from this process's environment once they
    /// have been read. Everything the worker starts — the agent's shell commands above all —
    /// inherits its environment, and with these three an agent could call the factory host as
    /// if it were the worker and submit its own results.
    /// </summary>
    public static void RemoveFromEnvironment(Action<string, string?> setVariable)
    {
        setVariable(FactoryWire.WorkerSecretVariable, null);
        setVariable(FactoryWire.HostUrlVariable, null);
        setVariable(FactoryWire.RunIdVariable, null);
    }
}

public sealed class WorkerOptionsException(string message) : Exception(message);
