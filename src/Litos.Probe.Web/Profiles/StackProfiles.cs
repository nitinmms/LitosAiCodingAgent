using System.Text.Json;
using Litos.Probe.Web.Core.Profiles;

namespace Litos.Probe.Web.Profiles;

/// <summary>
/// The stack profiles the engine knows: the ones embedded in this assembly, each replaceable by a
/// host-supplied one with the same id (the factory lets Admins edit them).
/// </summary>
public sealed class StackProfiles
{
    public const string Generic = "generic-http";

    /// <summary>A profile of this tier marks a web stack that can't be run yet.</summary>
    public const int NotSupportedTier = 0;

    private const string ResourcePrefix = "Litos.Probe.Web.Profiles.";
    private readonly Dictionary<string, StackProfile> _byId;

    public StackProfiles(IEnumerable<StackProfile> profiles) =>
        _byId = profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public IReadOnlyCollection<StackProfile> All => _byId.Values;

    public StackProfile? Find(string? id) => id is not null && _byId.TryGetValue(id, out var p) ? p : null;

    /// <summary>The profile a contract names, or the generic one when it names none.</summary>
    public StackProfile ForContract(string? stack) =>
        stack is null ? _byId[Generic] : Find(stack) ?? throw new KeyNotFoundException($"There is no stack profile '{stack}'.");

    public static StackProfiles Embedded() => new(LoadEmbedded());

    /// <summary>The embedded profiles, with any <c>*.json</c> in <paramref name="overrideDirectory"/> replacing or adding by id.</summary>
    public static StackProfiles WithOverrides(string? overrideDirectory)
    {
        var profiles = LoadEmbedded().ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (overrideDirectory is not null && Directory.Exists(overrideDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDirectory, "*.json"))
            {
                var profile = Parse(File.ReadAllText(file), file);
                profiles[profile.Id] = profile;
            }
        }

        return new StackProfiles(profiles.Values);
    }

    internal static IReadOnlyList<StackProfile> LoadEmbedded()
    {
        var assembly = typeof(StackProfiles).Assembly;
        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                .Select(n =>
                {
                    using var stream = assembly.GetManifestResourceStream(n)!;
                    using var reader = new StreamReader(stream);
                    return Parse(reader.ReadToEnd(), n);
                }),
        ];
    }

    private static StackProfile Parse(string json, string source)
    {
        try
        {
            var profile = StackProfile.Parse(json);
            if (string.IsNullOrWhiteSpace(profile.Id))
                throw new JsonException("the profile has no id");
            return profile;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Stack profile {source}: {ex.Message}", ex);
        }
    }
}
