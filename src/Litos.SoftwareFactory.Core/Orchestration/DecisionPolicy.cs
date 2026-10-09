using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>What the host does with each choice a decision scan found.</summary>
/// <param name="Ask">Choices that become a decision card, in the order they will be asked.</param>
/// <param name="Assume">Choices the run proceeds on, stated to the implementer and in the handoff.</param>
public sealed record ScanDecisions(IReadOnlyList<OpenChoice> Ask, IReadOnlyList<OpenChoice> Assume)
{
    /// <summary>The note the thread shows when the scan finishes: a line saying what it found, then
    /// one line per assumption. Joined into one sentence, seven of them read as a wall of text.</summary>
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
            : " Assuming:" + string.Concat(Assume.Select(choice => $"\n- {DecisionPolicy.Assumption(choice)}"));
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

    /// <remarks>
    /// An existing-data choice is asked even when the scan says something settles it. On F7 the
    /// scan marked "how the format changes, and when an existing file is upgraded" as settled by
    /// "Versions must survive reopening and compaction", which does not settle it: the same choice
    /// F6 made alone and that made every existing database unreadable. A missed existing-data
    /// question costs the most, so the scan's judgement that one is settled is not trusted.
    /// </remarks>
    public static bool NeedsAPerson(OpenChoice choice) =>
        ChoiceCategories.AlwaysAsk.Contains(choice.Category, StringComparer.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(choice.SettledBy) || string.Equals(choice.Category, ChoiceCategories.ExistingData, StringComparison.OrdinalIgnoreCase))
        && choice.Options.Count >= 2;

    /// <summary>The shortest stretch of an answer a re-check must quote for it to count.</summary>
    public const int MinQuotedAnswer = 12;

    /// <summary>
    /// Whether a choice is settled by a person's answer: its <c>SettledBy</c> quotes one of the
    /// answers, at least <see cref="MinQuotedAnswer"/> characters of it (or the whole answer, when
    /// shorter). This holds for existing-data choices too: what a person answered settles them,
    /// where the request's own wording does not (DecisionPolicy.NeedsAPerson).
    /// </summary>
    public static bool SettledByAnswer(OpenChoice choice, IEnumerable<string> answers)
    {
        if (string.IsNullOrWhiteSpace(choice.SettledBy))
            return false;
        var quoted = Normalise(choice.SettledBy);
        foreach (var answer in answers.Select(Normalise).Where(a => a.Length > 0))
        {
            if (answer.Length <= MinQuotedAnswer)
            {
                if (quoted.Contains(answer, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            for (var start = 0; start + MinQuotedAnswer <= answer.Length; start++)
            {
                if (quoted.Contains(answer.AsSpan(start, MinQuotedAnswer), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sorts the questions not yet asked by a re-check: those an answer settles, and those still to
    /// ask, in their order. A pending question the re-check did not return, or returned reworded,
    /// is still asked.
    /// </summary>
    public static (IReadOnlyList<OpenChoice> StillAsk, IReadOnlyList<OpenChoice> Settled) Recheck(
        IReadOnlyList<OpenChoice> pending, PlanSubmission recheck, IReadOnlyList<string> answers)
    {
        var still = new List<OpenChoice>();
        var settled = new List<OpenChoice>();
        foreach (var question in pending)
        {
            var returned = recheck.Choices.FirstOrDefault(c => Normalise(c.Question).Equals(Normalise(question.Question), StringComparison.OrdinalIgnoreCase));
            if (returned is not null && SettledByAnswer(returned, answers))
                settled.Add(question with { SettledBy = returned.SettledBy!.Trim() });
            else
                still.Add(question);
        }

        return (still, settled);
    }

    /// <summary>The line the thread shows after a re-check.</summary>
    public static string DescribeRecheck(int settled, int stillAsk) => (settled, stillAsk) switch
    {
        (0, _) => "Re-checked the remaining questions against your answers: none is settled yet.",
        (_, 0) => $"Re-checked the remaining questions against your answers: {(settled == 1 ? "1 is" : $"{settled} are")} settled, so nothing more needs asking.",
        _ => $"Re-checked the remaining questions against your answers: {(settled == 1 ? "1 is" : $"{settled} are")} settled; {(stillAsk == 1 ? "1 still needs" : $"{stillAsk} still need")} you.",
    };

    private static string Normalise(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('"', '\'', '“', '”');

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
