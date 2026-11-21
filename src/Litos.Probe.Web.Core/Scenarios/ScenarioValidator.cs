using System.Text.RegularExpressions;
using Litos.Probe.Web.Core.Contract;

namespace Litos.Probe.Web.Core.Scenarios;

/// <summary>
/// Whether a scenario can run against a contract, before anything starts. A scenario that does
/// not is a TestDefect for a person: nothing in the app can fix it.
/// </summary>
public static partial class ScenarioValidator
{
    public const int MaxSteps = 100;
    public const int MaxTimeoutSeconds = 60;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();

    /// <summary>The ARIA roles a target may name: the ones Playwright's <c>GetByRole</c> knows.</summary>
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "alert", "alertdialog", "application", "article", "banner", "blockquote", "button", "caption", "cell", "checkbox",
        "code", "columnheader", "combobox", "complementary", "contentinfo", "definition", "deletion", "dialog", "directory",
        "document", "emphasis", "feed", "figure", "form", "generic", "grid", "gridcell", "group", "heading", "img", "insertion",
        "link", "list", "listbox", "listitem", "log", "main", "marquee", "math", "meter", "menu", "menubar", "menuitem",
        "menuitemcheckbox", "menuitemradio", "navigation", "none", "note", "option", "paragraph", "presentation",
        "progressbar", "radio", "radiogroup", "region", "row", "rowgroup", "rowheader", "scrollbar", "search", "searchbox",
        "separator", "slider", "spinbutton", "status", "strong", "subscript", "superscript", "switch", "tab", "table",
        "tablist", "tabpanel", "term", "textbox", "time", "timer", "toolbar", "tooltip", "tree", "treegrid", "treeitem",
    };

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public static IReadOnlyList<string> Validate(WebScenario scenario, WebAppContract contract)
    {
        var problems = new List<string>();
        if (!IdPattern().IsMatch(scenario.Id))
            problems.Add($"id: \"{scenario.Id}\" must be lower-case letters, digits and '-', at most 64 characters");
        if (scenario.Revision < 1)
            problems.Add("revision: must be 1 or more");

        var auth = contract.Testability.Auth;
        if (scenario.Role is { } role)
        {
            if (auth.Type != AuthType.Form)
                problems.Add($"role: the app has no login (auth.type: none), so a scenario cannot sign in as {role}; leave role out");
            else if (!auth.Roles.ContainsKey(role))
                problems.Add($"role: \"{role}\" is not one of the contract's roles ({string.Join(", ", auth.Roles.Keys.Order(StringComparer.Ordinal))})");
        }

        RouteProblem("start", scenario.Start, problems);
        if (scenario.Steps.Count == 0)
            problems.Add("steps: a scenario needs at least one step");
        if (scenario.Steps.Count > MaxSteps)
            problems.Add($"steps: at most {MaxSteps}");
        if (scenario.Steps.Count > 0 && !scenario.Steps.Any(s => IsAssertion(s.Type)))
            problems.Add("steps: a scenario must check something: add at least one expect step");

        for (var i = 0; i < scenario.Steps.Count; i++)
            problems.AddRange(StepProblems(scenario.Steps[i]).Select(p => $"steps[{i + 1}] ({Name(scenario.Steps[i].Type)}): {p}"));
        return problems;
    }

    public static bool IsAssertion(StepType type) => type >= StepType.ExpectVisible;

    /// <summary>The step type as the JSON writes it: <c>expectText</c>.</summary>
    public static string Name(StepType type) => char.ToLowerInvariant(type.ToString()[0]) + type.ToString()[1..];

    private static IEnumerable<string> StepProblems(ScenarioStep step)
    {
        var needsTarget = step.Type is StepType.Click or StepType.Fill or StepType.Select or StepType.Check or StepType.Uncheck
            or StepType.WaitFor or StepType.ExpectVisible or StepType.ExpectHidden or StepType.ExpectText or StepType.ExpectState;
        var mayHaveTarget = needsTarget || step.Type is StepType.ExpectError;

        if (needsTarget && step.Target is null)
            yield return "target is required";
        if (!mayHaveTarget && step.Target is not null)
            yield return "takes no target";
        if (step.Target is { } target)
        {
            foreach (var problem in TargetProblems(target, depth: 0))
                yield return problem;
        }

        if (step.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > MaxTimeoutSeconds))
            yield return $"timeoutSeconds: between 1 and {MaxTimeoutSeconds}";

        switch (step.Type)
        {
            case StepType.Navigate:
                var routes = new List<string>();
                RouteProblem("path", step.Path, routes);
                foreach (var problem in routes)
                    yield return problem;
                break;
            case StepType.Fill or StepType.Select:
                if (step.Value is null)
                    yield return "value is required (it may be empty for fill)";
                else if (step.Type == StepType.Select && step.Value.Length == 0)
                    yield return "value: the option's label or value";
                foreach (var p in Placeholders.Find(step.Value))
                {
                    if (p.Kind != PlaceholderKind.Secret)
                        yield return $"value: {p.Text} is not allowed here; a scenario value may use {{run}} and {{secret:NAME}}";
                    else if (Placeholders.Problem(p) is { } problem)
                        yield return $"value: {problem}";
                }
                break;
            case StepType.ExpectText or StepType.ExpectError:
                if (string.IsNullOrWhiteSpace(step.Text))
                    yield return "text is required";
                break;
            case StepType.ExpectUrl:
                if (string.IsNullOrWhiteSpace(step.Url))
                    yield return "url is required: text the page's path contains, such as /my/appointments";
                break;
            case StepType.ExpectState:
                if (step.State is null)
                    yield return "state is required: enabled, disabled, checked or unchecked";
                break;
            case StepType.ExpectResponse:
                if (step.Path is not { Length: > 0 } path || !path.StartsWith('/'))
                    yield return "path is required: the request's path, such as /api/notes (end it with * for a prefix)";
                if (step.Status is not (>= 100 and <= 599))
                    yield return "status is required: the HTTP status the request answered, such as 201";
                if (step.Method is { } method && !Methods.Contains(method))
                    yield return $"method: one of {string.Join(", ", Methods)}";
                break;
            case StepType.Screenshot:
                if (step.Name is { } name && !NamePattern().IsMatch(name))
                    yield return "name: lower-case letters, digits and '-', at most 40 characters";
                break;
        }

        if (step.Type is not (StepType.Fill or StepType.Select) && step.Value is not null)
            yield return "value is only for fill and select";
    }

    private static IEnumerable<string> TargetProblems(Target t, int depth)
    {
        var kinds = new[] { t.Role, t.Label, t.TestId, t.Text, t.Css }.Count(k => k is not null);
        if (kinds != 1)
            yield return "target: give exactly one of role, label, testId, text or css";
        if (t.Role is { } role && !Roles.Contains(role))
            yield return $"target.role: \"{role}\" is not an ARIA role";
        if (t.Name is not null && t.Role is null)
            yield return "target.name goes with role: the element's accessible name";
        if (t.Nth is < 0)
            yield return "target.nth: 0 or more";
        if (t.Within is { } within)
        {
            if (depth >= 2)
                yield return "target.within: at most two levels";
            else
            {
                foreach (var problem in TargetProblems(within, depth + 1))
                    yield return problem.Replace("target.", "target.within.", StringComparison.Ordinal).Replace("target:", "target.within:", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A route on the app itself: never another origin, so a scenario cannot leave the app.</summary>
    private static void RouteProblem(string field, string? route, List<string> problems)
    {
        if (route is not { Length: > 0 } || !route.StartsWith('/') || route.StartsWith("//", StringComparison.Ordinal) || route.Contains('\\'))
            problems.Add($"{field}: a route on the app, starting with / (such as /my/appointments)");
    }
}
