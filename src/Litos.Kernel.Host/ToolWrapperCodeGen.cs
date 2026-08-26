using System.Text;
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
            sb.AppendLine($"/// <summary>{EscapeForDocComment(tool.Description)}</summary>");
            sb.AppendLine(
                $"async global::System.Threading.Tasks.Task<string> {identifier}(string argsJson = \"{{}}\") " +
                $"=> await global::Litos.Kernel.Host.ScriptSession.BridgeField!.CallAsync(\"{EscapeForStringLiteral(tool.Name)}\", argsJson);");
        }
        return sb.ToString();
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
