using System.Security.Cryptography;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Running;

/// <summary>
/// One run's placeholder values: its ports, its services' ports and values, its generated values
/// and the secrets it resolved. It is the one place values enter a run, so every secret and
/// generated value is handed to the redactor the moment it is read, and nothing written afterwards
/// can carry it. Where secrets may go is the validator's rule (README §10), checked before any run.
/// </summary>
/// <param name="services">Keyed <c>name:key</c>, as in <c>{service:db:port}</c>; filled once the services' values are known.</param>
internal sealed class RunValues(
    IReadOnlyDictionary<string, int> ports, ISecretResolver secrets, SecretRedactor redactor, IReadOnlyDictionary<string, string>? services = null) : IPlaceholderValues
{
    public int? Port(string process) => ports.TryGetValue(process, out var port) ? port : null;

    public string? Service(string name, string key) => services?.GetValueOrDefault($"{name}:{key}");

    public string Generated()
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        redactor.Add(value);
        return value;
    }

    public string? Secret(string name)
    {
        var value = secrets.Resolve(name);
        redactor.Add(value);
        return value;
    }
}
