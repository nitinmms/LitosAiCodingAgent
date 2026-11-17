namespace Litos.Probe.Web.Core;

/// <summary>
/// Replaces the run's resolved secret values wherever text leaves the engine: check messages, log
/// excerpts and the saved process logs (README §10). Longest first, so a secret that contains
/// another is replaced whole.
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "••••";

    private readonly List<string> _values = [];
    private readonly Lock _lock = new();

    public static SecretRedactor None { get; } = new();

    public void Add(string? value)
    {
        // A one- or two-character "secret" would blank out ordinary text everywhere.
        if (string.IsNullOrEmpty(value) || value.Length < 3)
            return;

        lock (_lock)
        {
            if (_values.Contains(value))
                return;
            _values.Add(value);
            _values.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public string Redact(string text)
    {
        lock (_lock)
        {
            foreach (var value in _values)
                text = text.Replace(value, Mask, StringComparison.Ordinal);
        }

        return text;
    }

    public IReadOnlyList<string> Redact(IEnumerable<string> lines) => [.. lines.Select(Redact)];
}
