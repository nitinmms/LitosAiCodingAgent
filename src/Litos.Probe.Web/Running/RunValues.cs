using System.Security.Cryptography;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Running;

/// <summary>
/// One run's placeholder values: its ports, its generated values and the secrets it resolved.
/// Every secret and generated value is handed to the redactor the moment it is read, so nothing
/// written afterwards can carry it.
/// </summary>
internal sealed class RunValues(IReadOnlyDictionary<string, int> ports, ISecretResolver secrets, SecretRedactor redactor, bool secretsAllowed = true) : IPlaceholderValues
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
        if (!secretsAllowed)
            throw new PlaceholderException($"{{secret:{name}}} is not allowed here");
        var value = secrets.Resolve(name);
        redactor.Add(value);
        return value;
    }

    /// <summary>The same values with secrets refused, for build and reset commands.</summary>
    public RunValues WithoutSecrets() => new(ports, secrets, redactor, secretsAllowed: false);
}
