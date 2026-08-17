using ModelContextProtocol.Protocol;

namespace Litos.Console;

/// <summary>
/// Pure parsing/validation for MCP prompt arguments typed after a spliced
/// /mcp__server__prompt command, split out so it's testable without a Terminal.Gui control tree
/// (mirrors Litos.Gui's own McpPromptArguments, copied verbatim per the Console parity plan's
/// no-Gui-changes rule). Binding is positional (Claude Code's own MCP prompt UX has no named/flag
/// syntax to mirror), matching each typed token in order against a prompt's declared
/// PromptArguments.
/// </summary>
internal static class McpPromptArguments
{
    /// <summary>
    /// Splits a raw argument string into positional tokens. A double-quoted run ("...") is kept
    /// as one token with the quotes stripped, so a value containing spaces (a commit message, a
    /// search query) can be passed as one argument; an unterminated trailing quote runs to
    /// end-of-string rather than erroring — there's no way for the user to see a parse error
    /// before pressing Enter in a single-line input, so this stays lenient rather than
    /// rejecting input outright. Runs of whitespace outside quotes collapse without producing
    /// empty tokens.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string argumentText)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < argumentText.Length)
        {
            while (i < argumentText.Length && char.IsWhiteSpace(argumentText[i]))
                i++;
            if (i >= argumentText.Length)
                break;

            if (argumentText[i] == '"')
            {
                var start = i + 1;
                var end = argumentText.IndexOf('"', start);
                if (end < 0)
                    end = argumentText.Length;
                tokens.Add(argumentText[start..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < argumentText.Length && !char.IsWhiteSpace(argumentText[i]))
                    i++;
                tokens.Add(argumentText[start..i]);
            }
        }

        return tokens;
    }

    internal enum BindErrorKind { MissingRequired, TooManyValues }

    internal sealed record BindError(BindErrorKind Kind, IReadOnlyList<string> Names);

    /// <summary>
    /// Maps positional tokens onto promptArguments (in declared order) into a name-to-value
    /// dictionary suitable for McpServerConnection.GetPromptAsync. A token count exceeding the
    /// number of declared arguments is TooManyValues (binding is purely positional, so there's
    /// no separate "unknown named argument" case). Otherwise, any declared argument with
    /// Required == true left unfilled by the supplied tokens is MissingRequired. An optional
    /// argument beyond the supplied token count is omitted from the result entirely (not passed
    /// as null), so the server applies its own default — matching MCP's own semantics for an
    /// absent optional prompt argument.
    /// </summary>
    internal static (IReadOnlyDictionary<string, object?> Arguments, BindError? Error) Bind(
        IReadOnlyList<string> tokens, IList<PromptArgument>? promptArguments)
    {
        promptArguments ??= [];
        if (tokens.Count > promptArguments.Count)
            return (EmptyArguments, new BindError(BindErrorKind.TooManyValues, []));

        var bound = new Dictionary<string, object?>();
        var missing = new List<string>();
        for (var i = 0; i < promptArguments.Count; i++)
        {
            if (i < tokens.Count)
                bound[promptArguments[i].Name] = tokens[i];
            else if (promptArguments[i].Required == true)
                missing.Add(promptArguments[i].Name);
        }

        return missing.Count > 0
            ? (EmptyArguments, new BindError(BindErrorKind.MissingRequired, missing))
            : (bound, null);
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>();

    /// <summary>
    /// Formats the "&lt;required&gt; [optional]" hint printed right after an unrecognized /foo
    /// command is matched to an MCP prompt but called with too few/too many arguments, in
    /// declared order. Empty/null Arguments yields "" — callers should skip printing a hint line
    /// entirely for a zero-argument prompt rather than show an empty one.
    /// </summary>
    internal static string FormatArgumentHint(IList<PromptArgument>? promptArguments)
    {
        if (promptArguments is null || promptArguments.Count == 0)
            return "";

        return string.Join(" ", promptArguments.Select(a =>
            a.Required == true ? $"<{a.Name}>" : $"[{a.Name}]"));
    }
}
