using System.Text;
using System.Text.Json;
using Litos.Kernel;

namespace Litos.Kernel.Host;

/// <summary>
/// Generates the bootstrap C# source that defines one `Task&lt;string&gt; {toolName}(string
/// argsJson)` wrapper per bridged tool — this is what lets synchronous-looking script code like
/// `var text = await read_file("{\"path\":\"a.txt\"}");` actually be backed by a round trip to the
/// host process (§8.2). Tool names may contain characters invalid in a C# identifier (e.g. MCP's
/// "mcp__server__tool" is fine, but a defensive sanitizer keeps this robust against any future
/// tool naming) — sanitized to a safe identifier, with the original name passed as the literal
/// argument to ToolBridge.CallAsync so routing is unaffected by the rename.
/// </summary>
internal static class ToolWrapperCodeGen
{
    public static string Generate(IReadOnlyList<BridgedToolSchema> tools)
    {
        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var identifier = Sanitize(tool.Name);
            var literalName = EscapeForStringLiteral(tool.Name);
            var doc = EscapeForDocComment(tool.Description);

            sb.AppendLine($"/// <summary>{doc}</summary>");
            // Routed through KernelArgs.RawJson rather than passed straight to CallAsync: a single
            // string binds HERE rather than to the params overload below, so a positional call like
            // shell("pwd && ls") lands in this wrapper and used to reach ToolBridge's JSON parser as
            // garbage. RawJson turns that into guidance naming this tool and its first parameter —
            // which is why firstArgumentName is baked in at generation time, the only place the
            // schema is in scope.
            var firstArgumentName = EscapeForStringLiteral(FirstArgumentName(tool));
            sb.AppendLine(
                $"async global::System.Threading.Tasks.Task<string> {identifier}(string argsJson = \"{{}}\") " +
                $"=> await global::Litos.Kernel.Host.ScriptSession.BridgeField!.CallAsync(\"{literalName}\", " +
                $"global::Litos.Kernel.Host.KernelArgs.RawJson(\"{literalName}\", \"{firstArgumentName}\", argsJson));");

            // Params overload — the one a script should normally reach for. See KernelArgs.Json.
            sb.AppendLine($"/// <summary>{doc} (Pass arguments as name, value pairs — e.g. {identifier}(\"path\", @\"c:\\dir\\f.txt\") — so values are JSON-encoded for you.)</summary>");
            sb.AppendLine(
                $"async global::System.Threading.Tasks.Task<string> {identifier}(params object?[] nameValuePairs) " +
                $"=> await global::Litos.Kernel.Host.ScriptSession.BridgeField!.CallAsync(\"{literalName}\", global::Litos.Kernel.Host.KernelArgs.Json(nameValuePairs));");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The argument name to show a model that called this tool positionally — its first required
    /// parameter, else its first declared one. Falls back to a placeholder for a schema with no
    /// properties at all, where there is nothing truthful to suggest.
    /// </summary>
    private static string FirstArgumentName(BridgedToolSchema tool)
    {
        var schema = tool.ParameterSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return "name";

        if (schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array
            && required.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first
            && first.GetString() is { Length: > 0 } requiredName)
            return requiredName;

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.EnumerateObject().FirstOrDefault() is { Name.Length: > 0 } declared)
            return declared.Name;

        return "name";
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var result = new string(chars);
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string EscapeForStringLiteral(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeForDocComment(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", " ").Replace("\r", "");
}
