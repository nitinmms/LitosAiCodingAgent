using System.Text.RegularExpressions;

namespace Litos.Probe.Web.Core.Contract;

public enum PlaceholderKind { Port, Service, Generated, Secret }

/// <summary>One <c>{…}</c> reference in a contract value.</summary>
public sealed record PlaceholderRef(PlaceholderKind Kind, string Text, string? Name, string? Key);

/// <summary>Supplies the run's values. Each member returns null when it has none.</summary>
public interface IPlaceholderValues
{
    int? Port(string process);
    string? Service(string name, string key);
    string Generated();
    string? Secret(string name);
}

public sealed class PlaceholderException(string message) : Exception(message);

/// <summary>
/// The contract's placeholders (README §4): <c>{port:web}</c>, <c>{service:db:port}</c>,
/// <c>{service:db:POSTGRES_PASSWORD}</c>, <c>{generated}</c> and <c>{secret:NAME}</c>. Only
/// Probe.Web resolves them, at start, so a stored contract never holds a port or a password.
/// Braces that are not one of these forms are left alone: a JSON body or a regex keeps its own.
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"\{(?<kind>port|service|secret|generated)(?::(?<name>[A-Za-z0-9_.\-]+))?(?::(?<key>[A-Za-z0-9_.\-]+))?\}")]
    private static partial Regex Pattern();

    public static IEnumerable<PlaceholderRef> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (Match m in Pattern().Matches(text))
        {
            var name = m.Groups["name"].Success ? m.Groups["name"].Value : null;
            var key = m.Groups["key"].Success ? m.Groups["key"].Value : null;
            yield return m.Groups["kind"].Value switch
            {
                "port" => new PlaceholderRef(PlaceholderKind.Port, m.Value, name, key),
                "service" => new PlaceholderRef(PlaceholderKind.Service, m.Value, name, key),
                "secret" => new PlaceholderRef(PlaceholderKind.Secret, m.Value, name, key),
                _ => new PlaceholderRef(PlaceholderKind.Generated, m.Value, name, key),
            };
        }
    }

    /// <summary>A placeholder is well formed when it has exactly the parts its kind needs.</summary>
    public static string? Problem(PlaceholderRef p) => p.Kind switch
    {
        PlaceholderKind.Port when p.Name is null || p.Key is not null => $"{p.Text}: write {{port:<process>}}",
        PlaceholderKind.Service when p.Name is null || p.Key is null => $"{p.Text}: write {{service:<name>:port}} or {{service:<name>:<VAR>}}",
        PlaceholderKind.Secret when p.Name is null || p.Key is not null => $"{p.Text}: write {{secret:NAME}}",
        PlaceholderKind.Generated when p.Name is not null => $"{p.Text}: write {{generated}}",
        _ => null,
    };

    public static string Resolve(string text, IPlaceholderValues values) =>
        Pattern().Replace(text, m =>
        {
            var p = Find(m.Value).Single();
            if (Problem(p) is { } problem)
                throw new PlaceholderException(problem);

            return p.Kind switch
            {
                PlaceholderKind.Port => values.Port(p.Name!)?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? throw new PlaceholderException($"{p.Text}: there is no process named '{p.Name}'"),
                PlaceholderKind.Service => values.Service(p.Name!, p.Key!)
                    ?? throw new PlaceholderException($"{p.Text}: no such service value"),
                PlaceholderKind.Secret => values.Secret(p.Name!)
                    ?? throw new PlaceholderException($"{p.Text}: the secret {p.Name} is not set"),
                _ => values.Generated(),
            };
        });

    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> environment, IPlaceholderValues values) =>
        environment.ToDictionary(e => e.Key, e => Resolve(e.Value, values));
}
