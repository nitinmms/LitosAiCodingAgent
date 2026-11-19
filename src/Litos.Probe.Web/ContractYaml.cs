using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Litos.Probe.Web;

public sealed class ContractFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <c>litos.test.yaml</c>: the contract in snake_case, mapped 1:1 onto the JSON model, so there is
/// one schema. The yaml is turned into JSON and read by the same deserializer the factory uses
/// for profile v3, which also rejects misspelt keys rather than ignoring them.
/// </summary>
public static class ContractYaml
{
    public const string FileName = "litos.test.yaml";

    /// <summary>Maps whose keys are data (variable, role and endpoint names), kept as written.</summary>
    private static readonly HashSet<string> VerbatimKeyMaps = ["environment", "roles", "protected", "denied", "test_endpoints"];

    /// <summary>The only scalars that are not strings. Everything else stays text, so <c>PORT: 3000</c> or a numeric argument still reads as a string.</summary>
    private static readonly HashSet<string> NumberKeys = ["version", "timeout_seconds", "start_timeout_seconds"];
    private static readonly HashSet<string> BooleanKeys = ["console_errors"];

    public static WebAppContract Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new ContractFormatException($"{FileName} is not valid YAML: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ContractFormatException($"{FileName} must be a mapping with 'application' and 'testability'.");

        var json = ToJson(root, parentKey: null);
        try
        {
            return json.Deserialize<WebAppContract>(ProbeWebJson.Options) ?? throw new ContractFormatException($"{FileName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ContractFormatException($"{FileName}: {Describe(ex)}", ex);
        }
    }

    public static WebAppContract Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The contract as yaml, for a draft the factory writes or the CLI prints.</summary>
    public static string Serialize(WebAppContract contract) => Serialize(contract, sortDataMaps: false);

    /// <summary>
    /// The contract as yaml with every data map (environment, roles, routes, test endpoints) in key
    /// order, so two copies compare by what they say. Order in those maps means nothing to the Gate,
    /// and a contract read back from a jsonb column has lost it (jsonb sorts keys by length).
    /// </summary>
    public static string Canonical(WebAppContract contract) => Serialize(contract, sortDataMaps: true);

    private static string Serialize(WebAppContract contract, bool sortDataMaps)
    {
        var node = JsonSerializer.SerializeToNode(contract, ProbeWebJson.Options)!;
        var builder = new StringBuilder();
        Write(builder, node, 0, parentKey: null, sortDataMaps);
        return builder.ToString();
    }

    private static JsonNode? ToJson(YamlNode node, string? parentKey) => node switch
    {
        YamlMappingNode map => ToJsonObject(map, parentKey),
        // An empty list item (`- ` alone) is an empty string, never a null inside a command.
        YamlSequenceNode list => new JsonArray([.. list.Children.Select(c => ToJson(c, parentKey) ?? JsonValue.Create(""))]),
        YamlScalarNode scalar => ToJsonScalar(scalar, parentKey),
        _ => throw new ContractFormatException($"{FileName}: unsupported YAML node at {node.Start}"),
    };

    private static JsonObject ToJsonObject(YamlMappingNode map, string? parentKey)
    {
        var verbatim = parentKey is not null && VerbatimKeyMaps.Contains(parentKey);
        var result = new JsonObject();
        foreach (var (keyNode, value) in map.Children)
        {
            var key = ((YamlScalarNode)keyNode).Value ?? "";
            var name = verbatim ? key : CamelCase(key);
            // YamlDotNet already refuses a key written twice; this catches two spellings of one
            // field (base_url and baseUrl) that map to the same name.
            if (result.ContainsKey(name))
                throw new ContractFormatException($"{FileName}: '{key}' appears twice at {keyNode.Start}");
            // Inside a verbatim map the value's own keys are schema again (roles.admin.user).
            var json = ToJson(value, verbatim ? null : key);
            if (json is null && !verbatim)
                continue; // `build:` with nothing after it means "not given", so the default applies
            result[name] = json ?? JsonValue.Create("");
        }

        return result;
    }

    private static JsonValue? ToJsonScalar(YamlScalarNode scalar, string? key)
    {
        var text = scalar.Value ?? "";
        var plain = scalar.Style == ScalarStyle.Plain;
        if (plain && text is ("" or "~" or "null"))
            return null;

        if (key is not null && NumberKeys.Contains(key) && plain)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : throw new ContractFormatException($"{FileName}: {key} must be a whole number, not '{text}'");
        }

        if (key is not null && BooleanKeys.Contains(key) && plain)
        {
            return bool.TryParse(text, out var flag)
                ? JsonValue.Create(flag)
                : throw new ContractFormatException($"{FileName}: {key} must be true or false, not '{text}'");
        }

        return JsonValue.Create(text);
    }

    /// <summary>The deserializer's message, with its JSON path turned back into yaml key names.</summary>
    private static string Describe(JsonException ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut >= 0)
            message = message[..cut];
        return ex.Path is { } path ? $"{SnakeCase(path.TrimStart('$', '.'))}: {message}" : message;
    }

    internal static string CamelCase(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return snake;
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    internal static string SnakeCase(string camel)
    {
        var builder = new StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c))
                builder.Append('_').Append(char.ToLowerInvariant(c));
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, int indent, string? parentKey, bool sortDataMaps)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject obj:
                var verbatim = parentKey is not null && VerbatimKeyMaps.Contains(parentKey);
                var entries = verbatim && sortDataMaps ? obj.OrderBy(e => e.Key, StringComparer.Ordinal) : obj.AsEnumerable();
                foreach (var (name, value) in entries)
                {
                    var key = verbatim ? name : SnakeCase(name);
                    if (IsEmpty(value))
                        continue;
                    if (value is JsonObject or JsonArray && !IsInlineList(value))
                    {
                        builder.Append(pad).Append(Quote(key)).AppendLine(":");
                        Write(builder, value, indent + 2, verbatim ? null : key, sortDataMaps);
                    }
                    else
                    {
                        builder.Append(pad).Append(Quote(key)).Append(": ").AppendLine(Scalar(value));
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonObject child)
                    {
                        var lines = new StringBuilder();
                        Write(lines, child, indent + 2, parentKey, sortDataMaps);
                        var text = lines.ToString();
                        builder.Append(pad).Append("- ").Append(text.TrimStart());
                    }
                    else
                    {
                        builder.Append(pad).Append("- ").AppendLine(Scalar(item));
                    }
                }

                break;
        }
    }

    private static bool IsEmpty(JsonNode? value) =>
        value is null || value is JsonObject { Count: 0 } || value is JsonArray { Count: 0 };

    /// <summary>A list of plain strings, such as a command, reads best on one line.</summary>
    private static bool IsInlineList(JsonNode? value) => value is JsonArray a && a.All(i => i is JsonValue);

    private static string Scalar(JsonNode? value) => value switch
    {
        JsonArray array => "[" + string.Join(", ", array.Select(Scalar)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Quote(v.GetValue<string>()),
        JsonValue v => v.ToJsonString(),
        _ => "null",
    };

    /// <summary>Double-quoted unless the text is plainly safe, so <c>{port:web}</c> or <c>"yes"</c> round-trips as a string.</summary>
    private static string Quote(string text)
    {
        var safe = text.Length > 0
            && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/')
            && !char.IsDigit(text[0]) && text is not ("true" or "false" or "null" or "yes" or "no" or "on" or "off");
        return safe ? text : JsonSerializer.Serialize(text);
    }
}
