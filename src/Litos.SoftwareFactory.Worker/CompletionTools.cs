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

    /// <summary>The first of several accepted names that holds text. From PTC kernel code the
    /// model sees only a tool's top-level signature, never the fields of an array's items, so it
    /// guesses them; every review's first submit_review used 'description' for 'text'. Taking the
    /// common guesses costs nothing, and a rejected submission costs a whole model call.</summary>
    protected static string? Text(JsonElement arguments, params string[] names)
    {
        foreach (var name in names)
            if (Text(arguments, name) is { } text)
                return text;
        return null;
    }

    /// <summary>A list of non-empty strings. A missing property is an empty list, and a single
    /// string is a list of one (the model often passes one test or one limitation that way); a
    /// property of any other shape is null, so the caller can say so.</summary>
    protected static IReadOnlyList<string>? Strings(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!.Trim()];
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
                // A plain string is not taken as a criterion: it would lose which tests cover it,
                // and a criterion with no test sends the change to a full review.
                if (item.ValueKind != JsonValueKind.Object)
                    return (null, $"every entry in 'criteria' must be an object, not a string: {CriterionShape}.");
                if (Text(item, "criterion", "name", "text", "description") is not { } criterion)
                    return (null, $"every entry in 'criteria' needs a 'criterion': {CriterionShape}.");
                if (Strings(item, item.TryGetProperty("tests", out _) ? "tests" : "testNames") is not { } tests)
                    return (null, "'tests' must be an array of test names.");

                var manualOnly = (item.TryGetProperty("manualOnly", out var manual) || item.TryGetProperty("manual", out manual)) && manual.ValueKind == JsonValueKind.True;
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

    private const string CriterionShape =
        "{ \"criterion\": \"Admins can export.\", \"tests\": [\"Export_Admin_Succeeds\"] }, or { \"criterion\": \"...\", \"manualOnly\": true }";
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
        if (LooksLikeAProbe(question, why, options) is { } probe)
            return (null, $"{probe} Every request_decision is shown to a person and stops the work until they answer, so call it only with the real question; never to test the tool.");

        return (new DecisionSubmission(question, why, options, Text(arguments, "recommendation"), Text(arguments, "impact")), null);
    }

    /// <summary>The shortest question or reason a real decision has been seen to need.</summary>
    public const int MinimumTextLength = 20;

    /// <summary>
    /// A call that cannot be a real decision: an agent once probed the tool's parameter names with
    /// question "Q test2", reason "w" and options "a" and "b", and the probe reached the user as a
    /// decision while the real question was never asked. Not a quality bar: only text too short to
    /// say anything, or options that are not choices.
    /// </summary>
    internal static string? LooksLikeAProbe(string question, string whyItBlocks, IReadOnlyList<string> options)
    {
        if (question.Length < MinimumTextLength)
            return $"'question' is too short to be a real decision (\"{question}\"): ask it as a full sentence.";
        if (whyItBlocks.Length < MinimumTextLength)
            return $"'whyItBlocks' is too short to explain anything (\"{whyItBlocks}\").";
        if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() < options.Count)
            return "'options' repeats a choice.";
        if (options.All(option => option.Length == 1))
            return "'options' are single characters, not choices a person can make: describe each one.";
        return null;
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

            if (Text(item, "file", "path") is not { } file || Text(item, "text", "description", "finding", "message") is not { } text)
                return (null, "every finding needs a 'file' and a 'text', as in { \"severity\": \"minor\", \"file\": \"src/Orders.cs\", \"line\": 42, \"text\": \"One sentence.\" }.");

            findings.Add(new ReviewFinding(severity, file, Line(item), text));
        }

        return (new ReviewSubmission(findings), null);
    }

    /// <summary>A positive line number, as a number or as numeric text; anything else is no line.</summary>
    private static int? Line(JsonElement item)
    {
        if (!item.TryGetProperty("line", out var value))
            return null;
        var number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => 0,
        };
        return number > 0 ? number : null;
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

/// <summary>
/// submit_plan: the decision scan's result. The scan lists every choice the request leaves open;
/// the host, not the agent, decides which become a question for a person (DecisionPolicy).
/// </summary>
public sealed class SubmitPlanTool(FactoryHostClient host, string sessionId) : CompletionTool(host, sessionId)
{
    public const string ToolName = "submit_plan";

    public override string Name => ToolName;

    public override string Description =>
        "Report the decision scan's result: the approach, the files you expect to change, and every choice the request leaves open. "
        + "This is the only way to finish a scan turn. The factory decides which choices to ask a person about.";

    public override JsonElement ParameterSchema { get; } = Schema(new
    {
        type = "object",
        properties = new
        {
            approach = new { type = "string", description = "How the task would be done, in a few sentences." },
            files = StringArray("Files you expect to change."),
            choices = new
            {
                type = "array",
                description = "Every choice the request leaves open; empty when it leaves none.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        question = new { type = "string" },
                        category = new { type = "string", @enum = ChoiceCategories.All },
                        options = StringArray("Two to four options."),
                        recommendation = new { type = "string" },
                        why = new { type = "string", description = "Who or what the choice affects." },
                        settledBy = new { type = "string", description = "What already settles it, quoted; empty when nothing does." },
                    },
                    required = new[] { "question", "category", "options" },
                },
            },
        },
        required = new[] { "approach", "choices" },
    });

    protected override string Recorded => "Recorded. Stop now; the factory asks a person what needs one, then implements.";

    private const string ChoiceShape =
        "{ \"question\": \"...\", \"category\": \"existing-data\", \"options\": [\"...\", \"...\"], \"recommendation\": \"...\", \"why\": \"...\", \"settledBy\": \"\" }";

    protected override (Submission?, string?) Read(JsonElement arguments)
    {
        if (Text(arguments, "approach", "plan", "summary") is not { } approach)
            return (null, "'approach' is required.");
        if (Strings(arguments, arguments.TryGetProperty("files", out _) ? "files" : "filesToChange") is not { } files)
            return (null, "'files' must be an array of paths.");

        var choices = new List<OpenChoice>();
        if (arguments.TryGetProperty("choices", out var choicesValue) && choicesValue.ValueKind != JsonValueKind.Null)
        {
            if (choicesValue.ValueKind != JsonValueKind.Array)
                return (null, $"'choices' must be an array of objects such as {ChoiceShape}.");

            foreach (var item in choicesValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return (null, $"every entry in 'choices' must be an object, not a string: {ChoiceShape}.");
                if (Text(item, "question", "text") is not { } question)
                    return (null, $"every choice needs a 'question': {ChoiceShape}.");

                // An unknown category is refused, not taken as "other": quietly downgrading a
                // file-format choice would hide exactly what the scan is for.
                var category = Category(Text(item, "category", "kind"));
                if (category is null)
                    return (null, $"choice '{question}' needs a 'category', one of: {string.Join(", ", ChoiceCategories.All)}.");
                if (Strings(item, "options") is not { } options)
                    return (null, "'options' must be an array of strings.");

                choices.Add(new OpenChoice(
                    question, category, options,
                    Text(item, "recommendation", "recommended"),
                    Text(item, "why", "reason", "impact"),
                    Text(item, "settledBy", "settled_by", "settled")));
            }
        }

        return (new PlanSubmission(approach, files, choices), null);
    }

    /// <summary>The category as one of <see cref="ChoiceCategories.All"/>, accepting spaces,
    /// underscores and case; null when it is none of them.</summary>
    private static string? Category(string? value)
    {
        if (value is null)
            return null;
        var normalised = value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (normalised == "public-behavior")
            normalised = ChoiceCategories.PublicBehaviour;
        return ChoiceCategories.All.FirstOrDefault(c => c == normalised);
    }
}
