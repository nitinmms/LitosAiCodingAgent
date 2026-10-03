using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Host;

/// <summary>
/// The host's configuration. Everything secret here — the database connection string, the
/// provider key, the GitHub token — comes from the host's own environment or its private
/// settings file, and none of it is ever handed to a worker
/// (ReadMe_LitosSoftwareFactory_V1.md §9.3, §13.3).
/// </summary>
public sealed class FactoryOptions
{
    /// <summary>M1 runs every task on OpenRouter.</summary>
    public const string OpenRouter = "openrouter";

    public string? ConnectionString { get; set; }

    public string? MigrationsConnectionString { get; set; }

    public string DataDirectory { get; set; } = "";

    public string? GitHubToken { get; set; }

    public string? OpenRouterApiKey { get; set; }

    public string AdminUser { get; set; } = "admin";

    public string? AdminPassword { get; set; }

    public string Provider { get; set; } = OpenRouter;

    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash";

    public int ContextLength { get; set; } = 1_048_576;

    /// <summary>The cap a new thread gets when its creator sets none; null means no cap.</summary>
    public long? DefaultBudget { get; set; } = 300_000;

    /// <summary>Concurrent runs across all repositories. It stays at 1 until M2.</summary>
    public int SlotCap { get; set; } = 1;

    public BudgetPolicy Budget { get; set; } = new();

    public RunLimits Limits { get; set; } = new();

    /// <summary>Whether runs use Programmatic Tool Calling. On by default (§8).</summary>
    public bool PtcEnabled { get; set; } = true;

    /// <summary>The worker executable; null finds it beside the host.</summary>
    public string? WorkerPath { get; set; }

    public string CommitName { get; set; } = "Litos Software Factory";

    public string CommitEmail { get; set; } = "factory@litos.invalid";

    /// <summary>How often the coordinator looks for queued work when nothing has signalled it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string WorkspacesDirectory => Path.Combine(DataDirectory, "workspaces");

    public string RunDirectory(Guid runId) => Path.Combine(DataDirectory, "runs", runId.ToString("N"));

    /// <summary>Reads the options from configuration: environment variables, and the private
    /// settings file when one was given.</summary>
    public static FactoryOptions From(IConfiguration configuration)
    {
        var options = new FactoryOptions
        {
            ConnectionString = configuration["ConnectionStrings:FactoryState"],
            MigrationsConnectionString = configuration["ConnectionStrings:FactoryMigrations"],
            DataDirectory = configuration["FACTORY_DATA_DIR"] ?? "",
            GitHubToken = NullIfEmpty(configuration["FACTORY_GITHUB_TOKEN"]),
            OpenRouterApiKey = NullIfEmpty(configuration["OPENROUTER_API_KEY"]),
            AdminUser = configuration["FACTORY_ADMIN_USER"] ?? "admin",
            AdminPassword = NullIfEmpty(configuration["FACTORY_ADMIN_PASSWORD"]),
            WorkerPath = NullIfEmpty(configuration["FACTORY_WORKER_PATH"]),
        };

        if (NullIfEmpty(configuration["FACTORY_MODEL"]) is { } model)
            options.Model = model;
        if (int.TryParse(configuration["FACTORY_CONTEXT_LENGTH"], out var contextLength) && contextLength > 0)
            options.ContextLength = contextLength;
        if (int.TryParse(configuration["FACTORY_OUTPUT_ALLOWANCE"], out var outputAllowance) && outputAllowance > 0)
            options.Budget = options.Budget with { OutputAllowanceTokens = outputAllowance };
        if (double.TryParse(configuration["FACTORY_CACHED_INPUT_WEIGHT"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var weight)
            && weight is >= 0 and <= 1)
        {
            options.Budget = options.Budget with { CachedInputWeight = weight };
        }

        if (double.TryParse(configuration["FACTORY_REWORK_TOP_UP"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var topUp)
            && topUp is >= 0 and <= 10)
        {
            options.Budget = options.Budget with { ReworkTopUpShare = topUp };
        }

        if (configuration["FACTORY_DEFAULT_BUDGET"] is { Length: > 0 } defaultBudget)
        {
            // "none" removes the default cap; a number replaces it.
            if (defaultBudget.Equals("none", StringComparison.OrdinalIgnoreCase))
                options.DefaultBudget = null;
            else if (long.TryParse(defaultBudget, out var cap) && cap > 0)
                options.DefaultBudget = cap;
        }
        return options;
    }

    /// <summary>Every reason the host cannot start with these options. Empty means it can.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ConnectionString))
            problems.Add("ConnectionStrings__FactoryState is not set.");
        if (string.IsNullOrWhiteSpace(DataDirectory))
            problems.Add("FACTORY_DATA_DIR is not set.");
        if (Provider != OpenRouter)
            problems.Add($"M1 supports the '{OpenRouter}' provider only, but '{Provider}' is configured.");
        if (string.IsNullOrWhiteSpace(OpenRouterApiKey))
            problems.Add("OPENROUTER_API_KEY is not set in the host's environment.");
        if (string.IsNullOrWhiteSpace(Model))
            problems.Add("No model is configured.");
        if (SlotCap < 1)
            problems.Add("The slot cap must be at least 1.");
        return problems;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Reads a KEY=VALUE settings file (.env.factory) into configuration. The values go into the
/// host's configuration only — they are not exported into the process environment, so nothing
/// the host starts can inherit them.
/// </summary>
public static class EnvFile
{
    public static IReadOnlyDictionary<string, string?> Read(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            // ConnectionStrings__FactoryState is the environment spelling of ConnectionStrings:FactoryState.
            var key = line[..separator].Trim().Replace("__", ":");
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            values[key] = value;
        }

        return values;
    }
}
