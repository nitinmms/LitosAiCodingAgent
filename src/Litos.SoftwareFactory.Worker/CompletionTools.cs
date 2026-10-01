using System.Text.Json;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The completion tools (ReadMe_LitosSoftwareFactory_V1.md §8.5): the only way an agent turn
/// reports a result. Each posts its typed payload to the factory host from inside InvokeAsync,
/// which is what makes it behave the same whether the model calls it directly or from PTC kernel
/// code — where tool calls never appear on the turn's event stream at all. The host acts on that
/// callback; the tool result only tells the model what happens next.
///
/// A tool instance belongs to one session, so the host knows which conversation a submission
/// came from without relying on ambient state the kernel bridge does not carry.
/// </summary>
public abstract class CompletionTool(FactoryHostClient host, string sessionId) : ITool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonElement ParameterSchema { get; }

    /// <summary>What the model is told once the host has recorded the submission.</summary>
    protected virtual string Recorded => "Recorded.";

    /// <summary>Builds the submission, or returns a message saying what is wrong with the arguments.</summary>
    protected abstract (Submission? Submission, string? Problem) Read(JsonElement arguments);

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return ToolResult.Error($"{Name} takes a JSON object of arguments.");

        var (submission, problem) = Read(arguments);
        if (submission is null)
            return ToolResult.Error($"{Name} was not recorded: {problem} Call {Name} again with that corrected.");

        SubmissionResponse response;
        try
        {
            response = await host.SubmitAsync(sessionId, submission, ct);
        }
        catch (HttpRequestException ex)
        {
            // Not recorded, and the model must know that: a turn that believes it submitted
            // when it did not would end with nothing for the host to act on.
            return ToolResult.Error($"{Name} could not be recorded: the factory host did not accept it ({ex.Message}). Call {Name} again.");
        }

        return response.Accepted
            ? ToolResult.Ok(string.IsNullOrWhiteSpace(response.Message) ? Recorded : $"{Recorded} {response.Message}")
            : ToolResult.Error($"{Name} was not recorded: {response.Message}");
    }

    protected static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    protected static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>A list of non-empty strings. A missing property is an empty list; a property of
    /// the wrong shape is null, so the caller can say so.</summary>
    protected static IReadOnlyList<string>? Strings(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            if (!string.IsNullOrWhiteSpace(item.GetString()))
                items.Add(item.GetString()!.Trim());
        }

        return items;
    }

    protected static object StringArray(string description) => new { type = "array", items = new { type = "string" }, description };
}

public sealed class SubmitWorkTool(FactoryHostClient host, string sessionId) : CompletionTool(host, sessionId)
{
    public const string ToolName = "submit_work";

    public override string Name => ToolName;

    public override string Description =>
        "Report that the work is complete and ready for the factory to verify. This is the only way to finish an implement, repair or rework turn: "
        + "the factory then runs the build and the unit tests itself. Do not call it until the change and its unit tests are written.";

    public override JsonElement ParameterSchema { get; } = Schema(new
    {
        type = "object",
        properties = new
        {
            summary = new { type = "string", description = "What changed, in a few sentences." },
            criteria = new
            {
                type = "array",
                description = "Each acceptance criterion with the unit tests that cover it, or marked manual-only.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        criterion = new { type = "string" },
                        tests = StringArray("Names of the unit tests covering this criterion."),
                        manualOnly = new { type = "boolean", description = "True when this can only be checked by hand." },
                    },
                    required = new[] { "criterion" },
                },
            },
            testsAdded = StringArray("Unit tests added or changed."),
            knownLimitations = StringArray("Anything left undone, unverified, or assumed."),
            manualTestSteps = StringArray("Steps a person can follow to test the change, with expected results."),
        },
        required = new[] { "summary" },
    });

    protected override (Submission?, string?) Read(JsonElement arguments)
    {
        if (Text(arguments, "summary") is not { } summary)
            return (null, "'summary' is required.");

        var criteria = new List<CriterionCoverage>();
        if (arguments.TryGetProperty("criteria", out var criteriaValue) && criteriaValue.ValueKind != JsonValueKind.Null)
        {
            if (criteriaValue.ValueKind != JsonValueKind.Array)
                return (null, "'criteria' must be an array.");

            foreach (var item in criteriaValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "criterion") is not { } criterion)
                    return (null, "every entry in 'criteria' needs a 'criterion'.");
                if (Strings(item, "tests") is not { } tests)
                    return (null, "'tests' must be an array of test names.");

                var manualOnly = item.TryGetProperty("manualOnly", out var manual) && manual.ValueKind == JsonValueKind.True;
                if (tests.Count == 0 && !manualOnly)
                    return (null, $"criterion '{criterion}' names no tests; list the tests that cover it or set 'manualOnly' to true.");
                criteria.Add(new CriterionCoverage(criterion, tests, manualOnly));
            }
        }

        if (Strings(arguments, "testsAdded") is not { } testsAdded
            || Strings(arguments, "knownLimitations") is not { } limitations
            || Strings(arguments, "manualTestSteps") is not { } manualSteps)
        {
            return (null, "'testsAdded', 'knownLimitations' and 'manualTestSteps' must be arrays of strings.");
        }

        return (new WorkSubmission(summary, criteria, testsAdded, limitations, manualSteps), null);
    }
}

public sealed class RequestDecisionTool(FactoryHostClient host, string sessionId) : CompletionTool(host, sessionId)
{
    public const string ToolName = "request_decision";

    public override string Name => ToolName;

    public override string Description =>
        "Ask a person to make a material choice that cannot reasonably be inferred from the request, the code or earlier decisions — "
        + "an ambiguous business rule, a dependency outside the agreed scope, a destructive data operation. "
        + "Work stops until they answer, so do not use it for details you can settle yourself.";

    public override JsonElement ParameterSchema { get; } = Schema(new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "The decision needed, as one question." },
            whyItBlocks = new { type = "string", description = "Why work cannot continue without it." },
            options = new { type = "array", items = new { type = "string" }, minItems = 2, maxItems = 4, description = "Two to four concrete options." },
            recommendation = new { type = "string", description = "The option you would choose, and why." },
            impact = new { type = "string", description = "The files and behaviour the choice affects." },
        },
        required = new[] { "question", "whyItBlocks", "options" },
    });

    protected override string Recorded => "Recorded. Stop now and wait for the answer; make no further changes.";

    protected override (Submission?, string?) Read(JsonElement arguments)
    {
        if (Text(arguments, "question") is not { } question)
            return (null, "'question' is required.");
        if (Text(arguments, "whyItBlocks") is not { } why)
            return (null, "'whyItBlocks' is required.");
        if (Strings(arguments, "options") is not { } options)
            return (null, "'options' must be an array of strings.");
        if (options.Count is < 2 or > 4)
            return (null, $"'options' must offer two to four choices, but has {options.Count}.");

        return (new DecisionSubmission(question, why, options, Text(arguments, "recommendation"), Text(arguments, "impact")), null);
    }
}

public sealed class SubmitReviewTool(FactoryHostClient host, string sessionId) : CompletionTool(host, sessionId)
{
    public const string ToolName = "submit_review";

    public override string Name => ToolName;

    public override string Description =>
        "Report the findings of the review. This is the only way to finish a review turn. An empty findings list means the change is clean.";

    public override JsonElement ParameterSchema { get; } = Schema(new
    {
        type = "object",
        properties = new
        {
            findings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        severity = new { type = "string", @enum = new[] { "blocking", "minor" }, description = "'blocking' must be fixed before a person tests the change." },
                        file = new { type = "string", description = "Repository-relative path." },
                        line = new { type = "integer", description = "Line number in the changed file, when the finding has one." },
                        text = new { type = "string", description = "The finding, in one sentence." },
                    },
                    required = new[] { "severity", "file", "text" },
                },
            },
        },
        required = new[] { "findings" },
    });

    protected override (Submission?, string?) Read(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("findings", out var findingsValue) || findingsValue.ValueKind != JsonValueKind.Array)
            return (null, "'findings' is required and must be an array (empty when the change is clean).");

        var findings = new List<ReviewFinding>();
        foreach (var item in findingsValue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return (null, "every finding must be an object.");

            FindingSeverity severity;
            switch (Text(item, "severity")?.ToLowerInvariant())
            {
                case "blocking":
                    severity = FindingSeverity.Blocking;
                    break;
                case "minor":
                    severity = FindingSeverity.Minor;
                    break;
                default:
                    return (null, "every finding needs a 'severity' of 'blocking' or 'minor'.");
            }

            if (Text(item, "file") is not { } file || Text(item, "text") is not { } text)
                return (null, "every finding needs a 'file' and a 'text'.");

            int? line = item.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number && lineValue.TryGetInt32(out var number) && number > 0
                ? number
                : null;
            findings.Add(new ReviewFinding(severity, file, line, text));
        }

        return (new ReviewSubmission(findings), null);
    }
}

public sealed class SubmitSpecTool(FactoryHostClient host, string sessionId) : CompletionTool(host, sessionId)
{
    public const string ToolName = "submit_spec";

    public override string Name => ToolName;

    public override string Description =>
        "Propose the specification for this task, for the user to approve or edit. This is the only way to finish a spec turn.";

    public override JsonElement ParameterSchema { get; } = Schema(new
    {
        type = "object",
        properties = new
        {
            summary = new { type = "string", description = "What will be built." },
            acceptanceCriteria = StringArray("Numbered-list items, each one checkable."),
            affectedAreas = StringArray("Parts of the code or product that will change."),
            testPlan = new { type = "string", description = "Which criteria unit tests will cover and which are manual-only." },
            openQuestions = StringArray("Anything a person must answer before implementation."),
        },
        required = new[] { "summary", "acceptanceCriteria", "testPlan" },
    });

    protected override string Recorded => "Recorded. Stop now; the user will approve or edit the specification.";

    protected override (Submission?, string?) Read(JsonElement arguments)
    {
        if (Text(arguments, "summary") is not { } summary)
            return (null, "'summary' is required.");
        if (Strings(arguments, "acceptanceCriteria") is not { Count: > 0 } criteria)
            return (null, "'acceptanceCriteria' must list at least one criterion.");
        if (Text(arguments, "testPlan") is not { } testPlan)
            return (null, "'testPlan' is required.");
        if (Strings(arguments, "affectedAreas") is not { } areas || Strings(arguments, "openQuestions") is not { } questions)
            return (null, "'affectedAreas' and 'openQuestions' must be arrays of strings.");

        return (new SpecSubmission(summary, criteria, areas, testPlan, questions), null);
    }
}
