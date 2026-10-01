using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Verification;

/// <summary>
/// Ready-made profiles an Admin picks when registering a project and then adjusts
/// (ReadMe_LitosSoftwareFactory_V1.md §10.2). They are data shipped with the factory — JSON
/// files embedded here — not special code paths: the verifier treats a preset exactly like any
/// hand-written profile.
/// </summary>
public static class VerificationPresets
{
    public const string DotNet = "dotnet";
    public const string NodeReact = "node-react";

    public static IReadOnlyList<string> Names { get; } = [DotNet, NodeReact];

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
