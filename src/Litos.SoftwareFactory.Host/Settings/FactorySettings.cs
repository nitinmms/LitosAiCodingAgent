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

    /// <summary>A section was written; its name. Consumers that cache derived state listen.</summary>
    public event Action<string>? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        var rows = await store.ListSettingsAsync(ct);
        lock (_lock)
        {
            _sections.Clear();
            foreach (var row in rows)
                _sections[row.Section] = (row, Deserialize(row));
        }
    }

    public BudgetSettings Budgets => Read<BudgetSettings>(SettingsSections.Budgets);

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

    public async Task<IReadOnlyList<SecretStatus>> SecretsAsync(CancellationToken ct) =>
        [.. (await store.ListSecretsAsync(ct)).Select(s => new SecretStatus(s.Name, s.SetAt, s.SetBy))];

    /// <summary>A secret's value, or null when it is not set.</summary>
    /// <exception cref="SecretUnreadableException">It was protected with a key ring this host does not have.</exception>
    public async Task<string?> SecretAsync(string name, CancellationToken ct) =>
        (await store.ListSecretsAsync(ct)).FirstOrDefault(s => s.Name == name) is { } secret ? protector.Unprotect(secret.Ciphertext) : null;

    public async Task SetSecretAsync(string name, string value, Guid? actorId, CancellationToken ct)
    {
        await store.SetSecretAsync(name, protector.Protect(value), actorId, clock.UtcNow, ct);
        Changed?.Invoke(name);
    }

    public async Task<bool> ClearSecretAsync(string name, Guid actorId, CancellationToken ct)
    {
        var cleared = await store.ClearSecretAsync(name, actorId, clock.UtcNow, ct);
        if (cleared)
            Changed?.Invoke(name);
        return cleared;
    }

    /// <summary>The typed settings of a stored section. A section this build does not know yet is kept as its JSON.</summary>
    private static object Deserialize(SettingsSection row) => row.Section switch
    {
        SettingsSections.Budgets => JsonSerializer.Deserialize<BudgetSettings>(row.Json, FactoryWire.Json) ?? new BudgetSettings(),
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
    /// The environment variables the database now replaces, for the host's log. The provider key
    /// and GitHub token are seeded as secrets too, but the host reads them from its environment
    /// until providers come from settings (m3-architecture.md §10, step 3).
    /// </summary>
    public static readonly IReadOnlyList<string> Replaced = ["FACTORY_DEFAULT_BUDGET", "FACTORY_SLOT_CAP", "FACTORY_REWORK_TOP_UP"];

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
            }, 0, actorId: null, ct);
            seeded = true;
        }

        var secrets = (await settings.SecretsAsync(ct)).Select(s => s.Name).ToHashSet();
        foreach (var (name, value) in new[]
        {
            (SecretNames.Provider(FactoryOptions.OpenRouter), options.OpenRouterApiKey),
            (SecretNames.GitHub, options.GitHubToken),
        })
        {
            if (!string.IsNullOrWhiteSpace(value) && !secrets.Contains(name))
            {
                await settings.SetSecretAsync(name, value, actorId: null, ct);
                seeded = true;
            }
        }

        return seeded;
    }
}
