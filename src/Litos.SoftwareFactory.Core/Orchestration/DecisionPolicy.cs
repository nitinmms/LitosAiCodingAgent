using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>What the host does with each choice a decision scan found.</summary>
/// <param name="Ask">Choices that become a decision card, in the order they will be asked.</param>
/// <param name="Assume">Choices the run proceeds on, stated to the implementer and in the handoff.</param>
public sealed record ScanDecisions(IReadOnlyList<OpenChoice> Ask, IReadOnlyList<OpenChoice> Assume)
{
    /// <summary>The line the thread shows when the scan finishes.</summary>
    public string Describe()
    {
        var total = Ask.Count + Assume.Count;
        if (total == 0)
            return "Decision scan: the request leaves no choice open. Implementing.";

        var found = total == 1 ? "1 open choice" : $"{total} open choices";
        var asking = Ask.Count switch
        {
            0 => "none needs you",
            1 => "asking you 1",
            _ => $"asking you {Ask.Count}",
        };
        var assuming = Assume.Count == 0
            ? ""
            : " Assuming: " + string.Join("; ", Assume.Select(DecisionPolicy.Assumption)) + ".";
        return $"Decision scan: {found}, {asking}.{assuming}";
    }
}

/// <summary>
/// Decides, by rule, which of a scan's choices a person must make. No task in the evaluation ever
/// called request_decision: on F6 the agent saw the file-format question twice and decided it
/// alone, and briefs asking it to ask did not change that. Research on coding agents finds the
/// same, and that a separate step whose only job is to find what a request leaves open does better
/// than reminding the working agent to ask. So the scan lists the choices, and this rule, not the
/// agent, decides which are asked.
/// </summary>
public static class DecisionPolicy
{
    /// <summary>
    /// A choice is asked when it is in an always-ask category, nothing in the request, the code or
    /// an earlier decision settles it, and it offers at least two options. At most
    /// <paramref name="maxQuestions"/> are asked, in the scan's order; everything else is assumed.
    /// </summary>
    public static ScanDecisions Decide(PlanSubmission plan, int maxQuestions)
    {
        var ask = new List<OpenChoice>();
        var assume = new List<OpenChoice>();
        foreach (var choice in plan.Choices)
        {
            if (ask.Count < maxQuestions && NeedsAPerson(choice))
                ask.Add(choice);
            else
                assume.Add(choice);
        }

        return new ScanDecisions(ask, assume);
    }

    public static bool NeedsAPerson(OpenChoice choice) =>
        ChoiceCategories.AlwaysAsk.Contains(choice.Category, StringComparer.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(choice.SettledBy)
        && choice.Options.Count >= 2;

    /// <summary>The decision card for a choice the scan raised.</summary>
    public static DecisionSubmission ToDecision(OpenChoice choice) => new(
        choice.Question,
        string.IsNullOrWhiteSpace(choice.Why) ? "The request does not say, and the choice changes what users or callers get." : choice.Why!,
        [.. choice.Options.Take(4)],
        choice.Recommendation,
        null);

    /// <summary>How an assumed choice is stated: the question and what the run will do.</summary>
    public static string Assumption(OpenChoice choice)
    {
        var answer = !string.IsNullOrWhiteSpace(choice.Recommendation) ? choice.Recommendation!
            : choice.Options.Count > 0 ? choice.Options[0]
            : "the agent's judgement";
        var basis = string.IsNullOrWhiteSpace(choice.SettledBy) ? "" : $" ({choice.SettledBy!.Trim()})";
        return $"{choice.Question.Trim().TrimEnd('?')}: {answer.Trim().TrimEnd('.')}{basis}";
    }
}
