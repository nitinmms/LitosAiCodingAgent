using System.Security.Cryptography;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Running;

/// <summary>
/// One run's placeholder values: its ports, its generated values and the secrets it resolved.
/// It is the one place values enter a run, so every secret and generated value is handed to the
/// redactor the moment it is read, and nothing written afterwards can carry it. Where secrets may
/// go is the validator's rule (README §10), checked before any run starts.
/// </summary>
internal sealed class RunValues(IReadOnlyDictionary<string, int> ports, ISecretResolver secrets, SecretRedactor redactor) : IPlaceholderValues
{
    public int? Port(string process) => ports.TryGetValue(process, out var port) ? port : null;

    /// <summary>Services arrive with W2; the validator already refuses them.</summary>
    public string? Service(string name, string key) => null;

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
