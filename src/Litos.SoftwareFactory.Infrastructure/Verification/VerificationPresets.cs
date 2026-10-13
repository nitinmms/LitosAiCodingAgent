using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// The profiles the factory ships (ReadMe_LitosSoftwareFactory_V1.md §10.2): JSON files embedded
/// here, not special code paths, since the verifier treats a preset exactly like any hand-written
/// profile. They seed the editable presets section on the host's first start; after that the
/// section is what registration offers (m3-architecture.md §8).
/// </summary>
public static class VerificationPresets
{
    public const string DotNet = "dotnet";
    public const string NodeReact = "node-react";

    public static IReadOnlyList<string> Names { get; } = [DotNet, NodeReact];

    /// <summary>What people see a shipped preset called.</summary>
    public static string DisplayName(string name) => name switch
    {
        DotNet => ".NET",
        NodeReact => "Node / React",
        _ => name,
    };

    /// <exception cref="ArgumentException">No preset has this name.</exception>
    public static VerificationProfile Load(string name) => VerificationProfile.Parse(LoadJson(name));

    public static string LoadJson(string name)
    {
        if (!Names.Contains(name))
            throw new ArgumentException($"Unknown verification preset '{name}'. Known presets: {string.Join(", ", Names)}.", nameof(name));

        var assembly = typeof(VerificationPresets).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".Verification.Presets.{name}.json", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }
}
