namespace Litos.Probe.Web.Core.Scenarios;

/// <summary>A scenario's steps in words, as reports, briefs and the spec card show them.</summary>
public static class ScenarioText
{
    public static string Describe(ScenarioStep s) => s.Type switch
    {
        StepType.Navigate => $"open {s.Path}",
        StepType.Click => $"click {s.Target}",
        StepType.Fill => Contract.Placeholders.Find(s.Value).Any(p => p.Kind == Contract.PlaceholderKind.Secret)
            ? $"fill {s.Target} with a secret"
            : $"fill {s.Target} with \"{s.Value}\"",
        StepType.Select => $"select \"{s.Value}\" in {s.Target}",
        StepType.Check => $"check {s.Target}",
        StepType.Uncheck => $"uncheck {s.Target}",
        StepType.WaitFor => $"wait for {s.Target}",
        StepType.Screenshot => $"screenshot{(s.Name is null ? "" : $" {s.Name}")}",
        StepType.ExpectVisible => $"expect {s.Target} visible",
        StepType.ExpectHidden => $"expect {s.Target} hidden",
        StepType.ExpectText => $"expect {s.Target} to show \"{s.Text}\"",
        StepType.ExpectError => $"expect the error \"{s.Text}\"",
        StepType.ExpectUrl => $"expect the URL to contain \"{s.Url}\"",
        StepType.ExpectState => $"expect {s.Target} {s.State.ToString()!.ToLowerInvariant()}",
        StepType.ExpectResponse => $"expect {s.Method ?? "a request to"} {s.Path} to answer {s.Status}",
        _ => s.Type.ToString(),
    };

    /// <summary>A whole scenario on one line: "as patient, open /my/appointments; click …; expect …".</summary>
    public static string Summarise(WebScenario s) =>
        $"{(s.Role is null ? "signed out" : $"as {s.Role}")}, open {s.Start}; {string.Join("; ", s.Steps.Select(Describe))}";
}
