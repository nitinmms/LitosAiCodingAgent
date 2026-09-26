using System.Text.Json;

namespace Litos.Kernel.Host;

/// <summary>
/// Builds a bridged tool's arguments JSON from name/value pairs, so a script never hand-assembles
/// JSON in a C# string literal.
///
/// Why this exists: the original wrappers took only a raw `string argsJson`, which forces the model
/// through two layers of escaping at once — C#'s, then JSON's — and it reliably got them wrong on
/// Windows paths. Measured over one real 38-eval session, hand-written JSON produced errors like:
///
///   await list_directory("{\"path\":\"c:\\temp\\snake1\"}");
///   -> 's' is an invalid escapable character within a JSON string.
///
/// That looks right, and is not: C# turns "c:\\temp" into the characters c:\temp, and a lone
/// backslash is not a legal JSON escape, so the JSON parser rejects it. The model's own attempted
/// workaround — chk.Replace("\\","\\\\") sprinkled into string concatenation — is evidence of how
/// unreasonable the raw-JSON surface is to target by hand.
///
/// With the params overload the same call is simply:
///
///   await list_directory("path", @"c:\temp\snake1");
///
/// JsonSerializer handles every escaping concern, and a verbatim string keeps C#'s own layer out of
/// the way too. The raw-JSON overload stays for callers that already have a JSON document.
/// </summary>
public static class KernelArgs
{
    private const string PairExample =
        "Example: write_file(\"path\", @\"c:\\dir\\f.txt\", \"content\", text)";

    /// <summary>
    /// Guards the generated raw-arguments-JSON overload (`{tool}(string argsJson)`).
    ///
    /// Overload resolution sends a single string argument HERE, not to the params overload — the
    /// `string` parameter is applicable without params expansion, so it wins. That made a natural
    /// mistake fail opaquely: a model writing the positional call `shell("pwd &amp;&amp; ls")` had
    /// "pwd &amp;&amp; ls" forwarded verbatim as the arguments JSON, and the only feedback was
    /// ToolBridge's raw parser message, "'p' is an invalid start of a value. Path: $ | LineNumber: 0
    /// | BytePositionInLine: 0." — which names neither the tool, the rule, nor the fix. Observed
    /// live; it cost a round trip, and the model's recovery was a guess.
    ///
    /// Note that an odd count already produced a good error two methods down, and 1 IS odd: the
    /// single-string case simply never reached it. So this restores that guidance for the one arity
    /// that was missing it, naming the tool and its first parameter since the generated caller knows
    /// both. A string that really is a JSON document still passes straight through.
    /// </summary>
    public static string RawJson(string toolName, string firstArgumentName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return "{}";

        if (LooksLikeJsonObject(argsJson))
            return argsJson;

        throw new ArgumentException(
            $"{toolName} was called with a single string, which is the raw arguments-JSON overload, "
            + $"but {Display(argsJson)} is not a JSON object. Pass arguments as name, value pairs "
            + $"instead: {toolName}(\"{firstArgumentName}\", {Display(argsJson)}).");
    }

    private static bool LooksLikeJsonObject(string value) => value.AsSpan().TrimStart().StartsWith("{");

    /// <summary>Renders a value for an error message, quoted and clipped so a whole file's contents can't flood the model's context.</summary>
    private static string Display(string value)
    {
        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 60 ? $"\"{singleLine}\"" : $"\"{singleLine[..60]}…\"";
    }

    public static string Json(params object?[] nameValuePairs)
    {
        if (nameValuePairs is null || nameValuePairs.Length == 0)
            return "{}";

        // A single string argument is already-built JSON reaching this overload rather than the
        // string one (which normally wins — see RawJson). Pass it through rather than producing the
        // nonsense {"{\"path\":...}": null}, but hold it to the same "is it actually JSON?" rule
        // RawJson applies, so one mistake cannot have two different outcomes depending on which
        // overload happened to bind.
        if (nameValuePairs.Length == 1 && nameValuePairs[0] is string loneJson)
        {
            if (string.IsNullOrWhiteSpace(loneJson))
                return "{}";
            if (LooksLikeJsonObject(loneJson))
                return loneJson;
            throw new ArgumentException(
                $"Tool arguments must be name, value pairs — got a single value, {Display(loneJson)}, "
                + $"which is not a JSON object either. {PairExample}");
        }

        if (nameValuePairs.Length % 2 != 0)
            throw new ArgumentException(
                $"Tool arguments must be name, value pairs — got {nameValuePairs.Length} values. "
                + PairExample);

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < nameValuePairs.Length; i += 2)
        {
            if (nameValuePairs[i] is not string name || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    $"Tool argument name at position {i} must be a non-empty string — got "
                    + $"{(nameValuePairs[i] is null ? "null" : nameValuePairs[i]!.GetType().Name)}. "
                    + PairExample);

            map[name] = nameValuePairs[i + 1];
        }

        return JsonSerializer.Serialize(map);
    }
}
