using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Office.Plan;

/// <summary>
/// Reading and writing workflow.json and validation.json. Strict by design: an unknown operation,
/// an unknown field, an unknown enum value or a missing required field is an error, never ignored,
/// because whatever this parses is what a routine will run unattended.
/// </summary>
public static class PlanJson
{
    public const int MaxPlanBytes = 64 * 1024;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static string Serialize(WorkflowPlan plan) => JsonSerializer.Serialize(plan, Options);

    public static string Serialize(ValidationSpec spec) => JsonSerializer.Serialize(spec, Options);

    public static bool TryParse(string json, out WorkflowPlan? plan, out string? error) =>
        TryParseCore(json, out plan, out error);

    public static bool TryParse(string json, out ValidationSpec? spec, out string? error) =>
        TryParseCore(json, out spec, out error);

    private static bool TryParseCore<T>(string json, out T? value, out string? error) where T : class
    {
        value = null;
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPlanBytes)
        {
            error = $"The workflow file is larger than {MaxPlanBytes / 1024} KB, so it was not read.";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            error = value is null ? "The workflow file is empty." : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            error = Describe(ex);
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"The workflow file isn't valid: {ex.Message}";
            return false;
        }
    }

    /// <summary>The parser's message, with where it happened, e.g. "… at $.steps[2]".</summary>
    private static string Describe(JsonException ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut > 0)
            message = message[..cut];
        return ex.Path is { Length: > 0 } path
            ? $"The workflow file isn't valid at {path}: {message}"
            : $"The workflow file isn't valid: {message}";
    }
}
