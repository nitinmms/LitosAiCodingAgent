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
    public static string Json(params object?[] nameValuePairs)
    {
        if (nameValuePairs is null || nameValuePairs.Length == 0)
            return "{}";

        // A single string argument is almost certainly already-built JSON reaching this overload by
        // accident (params beats the string overload for some call shapes). Pass it through rather
        // than producing the nonsense {"{\"path\":...}": null}.
        if (nameValuePairs.Length == 1 && nameValuePairs[0] is string loneJson)
            return string.IsNullOrWhiteSpace(loneJson) ? "{}" : loneJson;

        if (nameValuePairs.Length % 2 != 0)
            throw new ArgumentException(
                $"Tool arguments must be name, value pairs — got {nameValuePairs.Length} values. "
                + "Example: write_file(\"path\", @\"c:\\dir\\f.txt\", \"content\", text)");

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < nameValuePairs.Length; i += 2)
        {
            if (nameValuePairs[i] is not string name || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    $"Tool argument name at position {i} must be a non-empty string — got "
                    + $"{(nameValuePairs[i] is null ? "null" : nameValuePairs[i]!.GetType().Name)}. "
                    + "Example: write_file(\"path\", @\"c:\\dir\\f.txt\", \"content\", text)");

            map[name] = nameValuePairs[i + 1];
        }

        return JsonSerializer.Serialize(map);
    }
}
