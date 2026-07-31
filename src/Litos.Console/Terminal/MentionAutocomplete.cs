using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// The @-mention live filtered dropdown, wired to Composer's TextView via Terminal.Gui's real
/// ISuggestionGenerator/Autocomplete popup — replacing MentionInputPrompt's hand-rolled raw-key
/// loop and its private Layout class entirely (ReadMe_AgentDesign.md §7.5, §7.6 step 3).
///
/// Reuses MentionParser's mention-detection shape (a preceding unescaped '@' with no whitespace
/// between it and the cursor) via ComposerState.FindActiveMention, and FileIndex.BuildRelativePaths
/// for the candidate list, both verbatim from the old implementation.
///
/// Terminal.Gui's own TextView/Autocomplete plumbing supplies AutocompleteContext per logical
/// line (already handling multi-line wrap internally, since CurrentLine is one line's cells, not
/// the whole wrapped buffer) — this is what structurally avoids the old line-wrap crash
/// (§6.1.2): there is no promptWidth/consoleWidth column arithmetic anywhere in this class.
///
/// Suggestion.Remove/Replacement is exactly the splice-after-'@' fix from §6.1.1: Remove is the
/// length of the partial token typed so far (not including the '@'), so applying a Suggestion
/// never deletes the '@' the mention parser depends on.
/// </summary>
public sealed class MentionAutocomplete(Func<string> workingDirectoryProvider) : ISuggestionGenerator
{
    private const int MaxSuggestions = 8;
    private IReadOnlyList<string>? _fileIndexCache;

    /// <summary>Rebuilds the cached file index on next use — call when the working directory changes.</summary>
    public void InvalidateCache() => _fileIndexCache = null;

    public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context)
    {
        var lineText = string.Concat(context.CurrentLine.Select(c => c.Grapheme));
        var cursor = Math.Clamp(context.CursorPosition, 0, lineText.Length);

        var state = new ComposerState();
        state.SetText(lineText);
        state.SetCursor(cursor);

        var mention = state.FindActiveMention();
        if (mention is null)
            return [];

        _fileIndexCache ??= FileIndex.BuildRelativePaths(workingDirectoryProvider());
        var matches = Filter(_fileIndexCache, mention.Token);

        var removeLength = cursor - (mention.Start + 1);
        return matches.Select(path => new Suggestion(removeLength, path, path));
    }

    public bool IsWordChar(string grapheme) =>
        grapheme.Length > 0 && !char.IsWhiteSpace(grapheme[0]) && grapheme != "@";

    private static List<string> Filter(IReadOnlyList<string> index, string token)
    {
        if (token.Length == 0)
            return index.Take(MaxSuggestions).ToList();

        return index
            .Where(p => p.Contains(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => !p.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Length)
            .Take(MaxSuggestions)
            .ToList();
    }
}
