using Litos.Agent.Providers;
using Litos.Host;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>
/// The providers the gateway calls, built from the factory's settings (m3-architecture.md §4.1).
/// Each is built on first use through the engine's own registration, so the factory runs exactly
/// the provider code the other Litos faces run, with a configuration made here: never the
/// ~/.litos/config.json they share (§9.3). A provider is built again after its key or the
/// providers section changes.
///
/// A provider that is no longer enabled can still be called while its key is set, so a task
/// already on it carries on; Enabled governs only what new threads are offered.
/// </summary>
public sealed class FactoryProviders : IChatProviderFactory
{
    private readonly FactorySettings _settings;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IChatProvider> _built = [];

    public FactoryProviders(FactorySettings settings)
    {
        _settings = settings;
        _settings.Changed += changed =>
        {
            if (changed == Core.Store.SettingsSections.Providers || changed.StartsWith("provider:", StringComparison.Ordinal))
                lock (_lock)
                    _built.Clear();
        };
    }

    public IChatProvider Resolve(string providerName)
    {
        lock (_lock)
            if (_built.TryGetValue(providerName, out var built))
                return built;

        var provider = Build(providerName);
        lock (_lock)
            _built[providerName] = provider;
        return provider;
    }

    private IChatProvider Build(string name)
    {
        var kind = KnownProviders.Find(name) ?? throw new InvalidOperationException($"'{name}' is not a provider the factory knows.");
        var entry = _settings.Providers.Entry(name);
        var key = _settings.Secret(SecretNames.Provider(name));
        if (kind.UsesBaseUrl ? string.IsNullOrWhiteSpace(entry?.BaseUrl) : key is null)
            throw new InvalidOperationException(kind.UsesBaseUrl
                ? $"{kind.DisplayName} has no address. An Admin sets it under Settings, Providers."
                : $"{kind.DisplayName} has no key. An Admin sets it under Settings, Providers.");

        var config = new LitosConfig(
            DefaultProvider: name, DefaultModel: entry?.DefaultModel ?? "", LastWorkingDirectory: null,
            ApiKeys: key is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [name] = key },
            LocalBaseUrl: kind.UsesBaseUrl ? entry!.BaseUrl : null);
        var services = new ServiceCollection().AddLitosAgent(config).BuildServiceProvider();
        return services.GetRequiredKeyedService<IChatProvider>(name);
    }
}
