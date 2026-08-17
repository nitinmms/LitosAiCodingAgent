using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Litos.Examples.BlazorChat.Components.Pages;

/// <summary>
/// Renders assistant text with Markdown-style links (e.g. share_file's own
/// "[Download README.md](http://localhost:8080/files/{token})" output, see Files/ShareFileTool.cs)
/// as clickable &lt;a&gt; tags — mirrors AngularChat's app.js `linkify` filter. HTML-encodes the raw
/// text first, same as Razor's own `@expression` output would've done, so nothing else in a
/// message is ever interpreted as HTML; only the bracket/paren link syntax becomes a real anchor.
/// The result is a MarkupString (Blazor's equivalent of Angular's $sce.trustAsHtml) specifically
/// because this method builds it from already-escaped text plus a fixed, known-safe anchor shape —
/// never from raw assistant-authored HTML.
/// </summary>
public static partial class ChatTextRenderer
{
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex MarkdownLink();

    public static MarkupString RenderAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(text);

        var escaped = HtmlEncoder.Default.Encode(text);
        var withLinks = MarkdownLink().Replace(escaped, match =>
        {
            var label = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{label}</a>";
        });

        return new MarkupString(withLinks);
    }
}
