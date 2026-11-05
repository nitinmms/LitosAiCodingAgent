using System.Net;
using System.Text.RegularExpressions;

namespace Litos.Artifacts;

/// <summary>How an artifact gets its name, shared by every face that publishes one: an explicit
/// title, else the page's own <c>&lt;title&gt;</c>, each cleaned to one line of at most
/// <see cref="MaxLength"/> characters.</summary>
public static partial class ArtifactTitles
{
    public const int MaxLength = 200;

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The page's <c>&lt;title&gt;</c>, decoded and cleaned; null when it has none.</summary>
    public static string? FromHtml(string html) =>
        TitlePattern().Match(html) is { Success: true } match ? Clean(WebUtility.HtmlDecode(match.Groups[1].Value)) : null;

    /// <summary>One line, trimmed, at most <see cref="MaxLength"/> characters; null when blank.</summary>
    public static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var oneLine = Whitespace().Replace(title, " ").Trim();
        return oneLine.Length <= MaxLength ? oneLine : oneLine[..MaxLength];
    }
}
