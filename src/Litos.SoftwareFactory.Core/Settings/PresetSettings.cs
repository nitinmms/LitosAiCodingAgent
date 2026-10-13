using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// One verification preset (blueprint §10.2, m3-architecture.md §8): a profile an Admin starts a
/// project from. A project copies it when registered, so editing a preset never changes a project.
/// </summary>
public sealed record VerificationPreset
{
    /// <summary>How registration names it, such as "dotnet".</summary>
    public required string Name { get; init; }

    /// <summary>What people see, such as ".NET".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The profile as JSON, in VerificationProfile's own form: kept as text, so an Admin edits it
    /// as written and the profile's formats keep their spelling wherever it travels.
    /// </summary>
    public required string Profile { get; init; }
}

/// <summary>The Presets tab: the profiles a project can be registered with. Seeded from the presets the factory ships.</summary>
public sealed partial record PresetSettings
{
    public IReadOnlyList<VerificationPreset> Presets { get; init; } = [];

    public const int MaxPresets = 30;
    public const int MaxProfileChars = 50_000;

    public VerificationPreset? Find(string name) => Presets.FirstOrDefault(p => p.Name == name);

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Presets.Count == 0)
            errors.Add("Keep at least one preset: a project is registered from one.");
        if (Presets.Count > MaxPresets)
            errors.Add($"There can be at most {MaxPresets} presets.");

        foreach (var duplicate in Presets.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"There are two presets named \"{duplicate.Key}\".");

        foreach (var preset in Presets)
        {
            if (!NamePattern().IsMatch(preset.Name ?? ""))
            {
                errors.Add($"\"{preset.Name}\" is not a usable preset name: lower-case letters, digits and '-', up to 40, starting with a letter or digit.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(preset.DisplayName))
                errors.Add($"{preset.Name}: give it a name people see.");
            errors.AddRange(ProblemsOf(preset).Select(problem => $"{preset.Name}: {problem}"));
        }

        return errors;
    }

    /// <summary>Why a preset's profile cannot be used, as it would be checked at registration.</summary>
    public static IReadOnlyList<string> ProblemsOf(VerificationPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Profile))
            return ["its profile is empty."];
        if (preset.Profile.Length > MaxProfileChars)
            return [$"its profile can be at most {MaxProfileChars.ToString("N0", CultureInfo.InvariantCulture)} characters."];

        try
        {
            return VerificationProfile.Parse(preset.Profile).Validate();
        }
        catch (JsonException ex)
        {
            return [$"its profile is not valid JSON: {ex.Message}"];
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();
}
