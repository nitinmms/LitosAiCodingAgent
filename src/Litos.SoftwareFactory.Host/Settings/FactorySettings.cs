using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>The state of one secret, as the app may see it: never its value.</summary>
public sealed record SecretStatus(string Name, DateTimeOffset SetAt, Guid? SetBy);

/// <summary>
/// The factory's settings as the host reads them (m3-architecture.md §3.2). Every section is held
/// in memory and replaced when it is written, which is safe because one host serves one database
/// (m2-architecture.md §3.5). Consumers read a section when they start something (a thread, a run,
/// a claim), never mid-way, so a change affects only what starts afterwards.
/// </summary>
public sealed class FactorySettings(IFactoryStore store, ISecretProtector protector, IClock clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (SettingsSection Row, object Value)> _sections = [];

    /// <summary>Every secret, still protected: a value is unprotected only when it is used.</summary>
    private readonly Dictionary<string, FactorySecret> _secrets = [];

    /// <summary>A section was written; its name. Consumers that cache derived state listen.</summary>
    public event Action<string>? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        var rows = await store.ListSettingsAsync(ct);
        var secrets = await store.ListSecretsAsync(ct);
        lock (_lock)
        {
            _sections.Clear();
            foreach (var row in rows)
                _sections[row.Section] = (row, Deserialize(row));
            _secrets.Clear();
            foreach (var secret in secrets)
                _secrets[secret.Name] = secret;
        }
    }

    public BudgetSettings Budgets => Read<BudgetSettings>(SettingsSections.Budgets);

    public ProviderSettings Providers => Read<ProviderSettings>(SettingsSections.Providers);

    /// <summary>Whether a provider can be called: its address is set if it is reached at one, otherwise its key.</summary>
    public bool IsUsable(ProviderEntry provider) => KnownProviders.Find(provider.Name) is { } kind
        && (kind.UsesBaseUrl ? !string.IsNullOrWhiteSpace(provider.BaseUrl) : IsSet(SecretNames.Provider(provider.Name)));

    /// <summary>A section's settings; its defaults when it was never written.</summary>
    public T Read<T>(string section) where T : class, new()
    {
        lock (_lock)
            return _sections.TryGetValue(section, out var held) && held.Value is T value ? value : new T();
    }

    /// <summary>The revision a change must name; 0 for a section never written.</summary>
    public long RevisionOf(string section)
    {
        lock (_lock)
            return _sections.TryGetValue(section, out var held) ? held.Row.Revision : 0;
    }

    /// <summary>Writes a section, if it is still at the revision the caller read.</summary>
    /// <exception cref="StoreConflictException">Someone else changed it first.</exception>
    public async Task<long> SaveAsync<T>(string section, T settings, long expectedRevision, Guid? actorId, CancellationToken ct)
        where T : class
    {
        var saved = await store.SaveSettingsAsync(section, JsonSerializer.Serialize(settings, FactoryWire.Json), expectedRevision, actorId, clock.UtcNow, ct);
        lock (_lock)
            _sections[section] = (saved, settings);
        Changed?.Invoke(section);
        return saved.Revision;
    }

    // ---- Secrets: kept protected, never shown again ----

    public IReadOnlyList<SecretStatus> Secrets
    {
        get
        {
            lock (_lock)
                return [.. _secrets.Values.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new SecretStatus(s.Name, s.SetAt, s.SetBy))];
        }
    }

    public bool IsSet(string name)
    {
        lock (_lock)
            return _secrets.ContainsKey(name);
    }

    /// <summary>A secret's value, or null when it is not set.</summary>
    /// <exception cref="SecretUnreadableException">It was protected with a key ring this host does not have.</exception>
    public string? Secret(string name)
    {
        string ciphertext;
        lock (_lock)
        {
            if (!_secrets.TryGetValue(name, out var secret))
                return null;
            ciphertext = secret.Ciphertext;
        }

        return protector.Unprotect(ciphertext);
    }

    public async Task SetSecretAsync(string name, string value, Guid? actorId, CancellationToken ct)
    {
        var secret = new FactorySecret { Name = name, Ciphertext = protector.Protect(value), SetAt = clock.UtcNow, SetBy = actorId };
        await store.SetSecretAsync(name, secret.Ciphertext, actorId, secret.SetAt, ct);
        lock (_lock)
            _secrets[name] = secret;
        Changed?.Invoke(name);
    }

    public async Task<bool> ClearSecretAsync(string name, Guid actorId, CancellationToken ct)
    {
        var cleared = await store.ClearSecretAsync(name, actorId, clock.UtcNow, ct);
        lock (_lock)
            _secrets.Remove(name);
        if (cleared)
            Changed?.Invoke(name);
        return cleared;
    }

    /// <summary>The typed settings of a stored section. A section this build does not know yet is kept as its JSON.</summary>
    private static object Deserialize(SettingsSection row) => row.Section switch
    {
        SettingsSections.Budgets => JsonSerializer.Deserialize<BudgetSettings>(row.Json, FactoryWire.Json) ?? new BudgetSettings(),
        SettingsSections.Providers => JsonSerializer.Deserialize<ProviderSettings>(row.Json, FactoryWire.Json) ?? new ProviderSettings(),
        _ => JsonDocument.Parse(row.Json).RootElement.Clone(),
    };
}

/// <summary>
/// The first start after M3: the settings the host used to read from its environment are written
/// to the database once (m3-architecture.md §3.3). After that the environment no longer sets them.
/// </summary>
public static class SettingsSeeding
{
    /// <summary>
    /// The environment variables the database now replaces, for the host's log.
    /// FACTORY_CONTEXT_LENGTH is still the window of a thread created before M3.
    /// </summary>
    public static readonly IReadOnlyList<string> Replaced =
    [
        "FACTORY_DEFAULT_BUDGET", "FACTORY_SLOT_CAP", "FACTORY_REWORK_TOP_UP", "FACTORY_OUTPUT_ALLOWANCE", "FACTORY_MODEL",
        "OPENROUTER_API_KEY", "FACTORY_GITHUB_TOKEN",
    ];

    /// <summary>Writes what is missing; returns whether anything was.</summary>
    public static async Task<bool> SeedAsync(FactorySettings settings, FactoryOptions options, CancellationToken ct)
    {
        var seeded = false;
        if (settings.RevisionOf(SettingsSections.Budgets) == 0)
        {
            await settings.SaveAsync(SettingsSections.Budgets, new BudgetSettings
            {
                DefaultTaskBudget = options.DefaultBudget,
                SlotCap = options.SlotCap,
                ReworkTopUpShare = options.Budget.ReworkTopUpShare,
                RepairCyclesPerRun = options.Limits.MaxRepairCycles,
                OutputAllowanceTokens = options.Budget.OutputAllowanceTokens,
            }, 0, actorId: null, ct);
            seeded = true;
        }

        if (settings.RevisionOf(SettingsSections.Providers) == 0)
        {
            // M2 ran every task on one OpenRouter model; it stays the default.
            await settings.SaveAsync(SettingsSections.Providers, new ProviderSettings
            {
                Providers =
                [
                    new ProviderEntry
                    {
                        Name = FactoryOptions.OpenRouter,
                        Enabled = true,
                        Models = [new AllowedModel(options.Model, options.ContextLength)],
                        DefaultModel = options.Model,
                    },
                ],
                DefaultProvider = FactoryOptions.OpenRouter,
            }, 0, actorId: null, ct);
            seeded = true;
        }

        foreach (var (name, value) in new[]
        {
            (SecretNames.Provider(FactoryOptions.OpenRouter), options.OpenRouterApiKey),
            (SecretNames.GitHub, options.GitHubToken),
        })
        {
            if (!string.IsNullOrWhiteSpace(value) && !settings.IsSet(name))
            {
                await settings.SetSecretAsync(name, value, actorId: null, ct);
                seeded = true;
            }
        }

        return seeded;
    }
}
