using System.Diagnostics;
using System.Text.RegularExpressions;
using Litos.Probe.Web.Browser;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;
using Microsoft.Playwright;
using ElementState = Litos.Probe.Web.Core.Scenarios.ElementState;

namespace Litos.Probe.Web.Running;

/// <summary>
/// Fixed-step scenarios (README_Litos.Probe.Web.md §6.2, W3.5), after the app is up. Each runs on
/// clean data, signed in afresh as its role, in its own browser context, so order never matters
/// (decided 2026-11-21). A target that finds nothing is a TestDefect, with what the page has
/// instead, so a person can tell a renamed button from a missing feature.
/// </summary>
internal sealed partial class GateRun
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<WebScenario> _scenarios;

    private async Task ScenarioStepsAsync(CancellationToken ct)
    {
        _current = CheckKind.Scenario;
        for (var i = 0; i < _scenarios.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scenario = _scenarios[i];

            // The bring-up's reset covers the first scenario; every later one starts clean too.
            if (i > 0 && await ResetBetweenAsync(ct) is { } failed)
            {
                Add(ScenarioCheck(scenario) with
                {
                    Status = CheckStatus.Failed,
                    Classification = Classification.EnvironmentFailure,
                    Message = $"The data could not be reset before this scenario: {failed.Message}",
                    Evidence = failed.Evidence,
                });
                foreach (var rest in _scenarios.Skip(i + 1))
                    Add(ScenarioCheck(rest) with { Status = CheckStatus.Skipped, Message = "Not run: the reset before an earlier scenario failed." });
                return;
            }

            var first = await AttemptAsync(scenario, trace: !FillsSecret(scenario), ct);
            Add(first.Classification == Classification.ApplicationDefect ? await RerunAsync(scenario, first, ct) : first);
        }
    }

    /// <summary>An application failure is tried once more on clean data in a fresh context; passing then makes it Flaky.</summary>
    private async Task<CheckResult> RerunAsync(WebScenario scenario, CheckResult first, CancellationToken ct)
    {
        for (var i = 0; i < _o.FlakyReruns; i++)
        {
            if (await ResetBetweenAsync(ct) is not null)
                return first;
            var again = await AttemptAsync(scenario, trace: false, ct);
            if (!again.Failed)
            {
                return again with
                {
                    Classification = Classification.Flaky,
                    Message = $"Failed once ({first.Message}), then passed on its rerun on clean data.",
                    Evidence = again.Evidence with { Screenshots = [.. first.Evidence.Screenshots, .. again.Evidence.Screenshots], Trace = first.Evidence.Trace },
                };
            }
        }

        return first;
    }

    /// <summary>The contract's reset; a SQLite snapshot is restored with the app stopped. The failing check, or null.</summary>
    private async Task<CheckResult?> ResetBetweenAsync(CancellationToken ct)
    {
        if (_env.ResetsBeforeStart)
        {
            await _env.StopAsync();
            var restored = await _env.ResetAsync(ct);
            if (restored.Failed)
                return restored;
            var started = await _env.StartAsync(overrides: null, label: "", strict: true, ct);
            return started.Failed ? started : null;
        }

        var reset = await _env.ResetAsync(ct);
        return reset.Failed ? reset : null;
    }

    private CheckResult ScenarioCheck(WebScenario s) => new()
    {
        Id = $"scenario:{s.Id}",
        Kind = CheckKind.Scenario,
        Role = s.Role,
        Route = s.Start,
        Scenario = new ScenarioInfo(s.Id, s.Revision, s.Title, s.Criterion),
    };

    /// <summary>A scenario that types a secret is never traced: a trace would record the value being typed.</summary>
    private static bool FillsSecret(WebScenario s) =>
        s.Steps.Any(step => Placeholders.Find(step.Value).Any(p => p.Kind == PlaceholderKind.Secret));

    private async Task<CheckResult> AttemptAsync(WebScenario scenario, bool trace, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var check = ScenarioCheck(scenario);

        if (scenario.Role is { } role)
        {
            var signIn = await SignInAsync(role, Auth.Roles[role]);
            if (signIn.Failed)
            {
                return check with
                {
                    Status = CheckStatus.Failed,
                    Classification = signIn.Classification,
                    Message = $"Could not sign in as {role}: {signIn.Message}",
                    Evidence = signIn.Evidence,
                    DurationMs = started.ElapsedMilliseconds,
                };
            }
        }

        var session = await OpenSessionAsync(scenario.Role, trace: false);
        string? traceId = null;
        try
        {
            if (trace)
                await session.Context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Title = $"scenario {scenario.Id}" });

            var run = new ScenarioAttempt(this, session, scenario);
            var result = await run.RunAsync(ct);
            if (trace)
                traceId = await SaveTraceAsync(session, scenario);

            return check with
            {
                Status = result.Failure is null ? CheckStatus.Passed : CheckStatus.Failed,
                Classification = result.Failure is { } f ? Classifier.Classify(f.Cause) : null,
                Message = result.Failure is { } g ? _redactor.Redact(g.Message) : null,
                Steps = result.Steps,
                DurationMs = started.ElapsedMilliseconds,
                Evidence = new Evidence
                {
                    Screenshots = result.Screenshots,
                    Trace = traceId,
                    ConsoleErrors = result.ConsoleErrors,
                    Signals = _redactor.Redact(result.Signals),
                    Log = result.Log,
                },
            };
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<string?> SaveTraceAsync(Session session, WebScenario scenario)
    {
        try
        {
            var path = Path.Combine(_scratch, $"trace-scenario-{ArtifactName(scenario.Id)}.zip");
            await session.Context.Tracing.StopAsync(new TracingStopOptions { Path = path });
            await using var file = File.OpenRead(path);
            return await _o.Artifacts.SaveAsync(ArtifactKind.Trace, $"trace-scenario-{ArtifactName(scenario.Id)}.zip", file, CancellationToken.None);
        }
        catch (Exception ex) when (IsBrowserError(ex) || ex is IOException)
        {
            return null;
        }
    }

    private sealed record AttemptResult(
        (FailureCause Cause, string Message)? Failure, IReadOnlyList<StepResult> Steps, IReadOnlyList<string> Screenshots,
        IReadOnlyList<string> Signals, int ConsoleErrors, IReadOnlyList<string> Log);

    /// <summary>One run of one scenario in one browser context.</summary>
    private sealed class ScenarioAttempt(GateRun run, Session session, WebScenario scenario)
    {
        private readonly List<StepResult> _steps = [];
        private readonly List<string> _screenshots = [];
        private readonly List<string> _signals = [];
        private readonly List<string> _log = [];
        private readonly List<(string Method, string Path, int Status)> _responses = [];
        private int _consoleErrors;

        /// <summary>Where the last action began in <see cref="_responses"/>: an expectResponse looks from there on.</summary>
        private int _actionMark;

        private IPage Page => session.Page;

        public async Task<AttemptResult> RunAsync(CancellationToken ct)
        {
            Page.Response += (_, response) =>
            {
                if (run._env.AppOrigins.Contains(BrowserHost.OriginOf(response.Url)))
                {
                    lock (_responses)
                        _responses.Add((response.Request.Method, PageWatcher.PathOf(response.Url).Split('?', 2)[0], response.Status));
                }
            };

            (FailureCause, string)? failure = null;
            var steps = new List<(string Description, Func<Task<(FailureCause, string)?>> Act, ScenarioStep? Step)>
            {
                ($"open {scenario.Start}", () => NavigateAsync(scenario.Start), null),
            };
            steps.AddRange(scenario.Steps.Select(s => (Describe(s), (Func<Task<(FailureCause, string)?>>)(() => StepAsync(s)), (ScenarioStep?)s)));

            for (var i = 0; i < steps.Count; i++)
            {
                var (description, act, step) = steps[i];
                if (failure is not null)
                {
                    _steps.Add(new StepResult(i, description, CheckStatus.Skipped, null, 0));
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                var logMark = run._env.Mark();
                var signalMark = session.Watcher.Mark();
                if (step is null || !ScenarioValidator.IsAssertion(step.Type))
                {
                    lock (_responses)
                        _actionMark = _responses.Count;
                }

                var stepFailure = await act();
                if (step is null || !ScenarioValidator.IsAssertion(step.Type))
                    await Task.Delay(Settle, run._limit);

                // The page's own signals and the server's error lines fail the step that caused them.
                var (signals, consoleErrors) = session.Watcher.Since(signalMark);
                var markers = await run.VisibleErrorMarkersAsync(Page);
                var log = run._env.ErrorLinesSince(logMark);
                _consoleErrors += consoleErrors;
                _signals.AddRange(signals.Concat(markers));
                _log.AddRange(log);
                if (stepFailure is null && signals.Concat(markers).FirstOrDefault() is { } signal)
                    stepFailure = (FailureCause.PageSignal, $"{signal}{(log.Count > 0 ? $" (server: {log[0]})" : "")}");
                else if (stepFailure is null && log.Count > 0)
                    stepFailure = (FailureCause.ServerLogError, $"The server logged an error: {log[0]}");

                string? shot = null;
                if (step?.Type == StepType.Screenshot || stepFailure is not null)
                {
                    shot = await run.ScreenshotAsync(Page, $"scenario-{scenario.Id}-{(step?.Name ?? $"step-{i}")}");
                    if (shot is not null)
                        _screenshots.Add(shot);
                }

                _steps.Add(new StepResult(i, description, stepFailure is null ? CheckStatus.Passed : CheckStatus.Failed,
                    stepFailure is { } sf ? run._redactor.Redact(sf.Item2) : null, clock.ElapsedMilliseconds, shot));
                if (stepFailure is { } failed)
                    failure = (failed.Item1, $"Step {i} of {steps.Count - 1}, {description}: {failed.Item2}");
            }

            // The page as the scenario left it.
            if (failure is null && await run.ScreenshotAsync(Page, $"scenario-{scenario.Id}-end") is { } end)
                _screenshots.Add(end);

            return new AttemptResult(failure, _steps, _screenshots, _signals, _consoleErrors, _log);
        }

        private async Task<(FailureCause, string)?> StepAsync(ScenarioStep step)
        {
            var timeout = step.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : DefaultStepTimeout;
            try
            {
                return step.Type switch
                {
                    StepType.Navigate => await NavigateAsync(step.Path!),
                    StepType.Click => await ActAsync(step, timeout, l => l.ClickAsync(new LocatorClickOptions { Timeout = Ms(timeout) })),
                    StepType.Fill => await FillAsync(step, timeout),
                    StepType.Select => await ActAsync(step, timeout, l => l.SelectOptionAsync(step.Value!, new LocatorSelectOptionOptions { Timeout = Ms(timeout) })),
                    StepType.Check => await ActAsync(step, timeout, l => l.CheckAsync(new LocatorCheckOptions { Timeout = Ms(timeout) })),
                    StepType.Uncheck => await ActAsync(step, timeout, l => l.UncheckAsync(new LocatorUncheckOptions { Timeout = Ms(timeout) })),
                    StepType.WaitFor or StepType.ExpectVisible => await ExpectVisibleAsync(step.Target!, timeout),
                    StepType.Screenshot => null,
                    StepType.ExpectHidden => await ExpectHiddenAsync(step.Target!, timeout),
                    StepType.ExpectText => await ExpectTextAsync(step.Target!, step.Text!, timeout),
                    StepType.ExpectError => await ExpectTextAsync(step.Target ?? new Target { Role = "alert" }, step.Text!, timeout),
                    StepType.ExpectUrl => await ExpectUrlAsync(step.Url!, timeout),
                    StepType.ExpectState => await ExpectStateAsync(step.Target!, step.State!.Value, timeout),
                    StepType.ExpectResponse => await ExpectResponseAsync(step, timeout),
                    _ => (FailureCause.ScenarioInvalid, $"{step.Type} is not a step Probe.Web runs."),
                };
            }
            catch (PlaceholderException ex)
            {
                return (ex.Kind == PlaceholderKind.Secret ? FailureCause.SecretMissing : FailureCause.ScenarioInvalid, ex.Message);
            }
            catch (Exception ex) when (IsBrowserError(ex) && !run._limit.IsCancellationRequested)
            {
                // The element was there but could not be acted on: disabled, covered, detached.
                return (FailureCause.AssertionFailed, FirstLine(ex.Message));
            }
        }

        private async Task<(FailureCause, string)?> NavigateAsync(string path)
        {
            try
            {
                var response = await Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Ms(run._o.PageTimeout) });
                if (response?.Status == 404)
                    return (FailureCause.RouteNotFound, $"{path} answered 404: the app has no such page.");
                if (response?.Status >= 500)
                    return (FailureCause.PageServerError, $"{path} answered HTTP {response.Status}.");
                if (!await run.PageReadyAsync(Page))
                    return (FailureCause.MarkerMissing, $"{path} never became ready ({run.ReadyDescription()}).");
                return null;
            }
            catch (Exception ex) when (IsBrowserError(ex) && !run._limit.IsCancellationRequested)
            {
                return (FailureCause.PageSignal, $"{path} did not load: {FirstLine(ex.Message)}");
            }
        }

        private async Task<(FailureCause, string)?> ActAsync(ScenarioStep step, TimeSpan timeout, Func<ILocator, Task> act)
        {
            var found = await ResolveAsync(step.Target!, timeout);
            if (found.Failure is { } failure)
                return failure;
            await act(found.Locator!);
            return null;
        }

        private async Task<(FailureCause, string)?> FillAsync(ScenarioStep step, TimeSpan timeout)
        {
            var value = step.Value!.Replace("{run}", run._runId, StringComparison.Ordinal);
            // Secrets go through the run's values, so the redactor has them before anything is written.
            value = Placeholders.Resolve(value, new RunValues(new Dictionary<string, int>(), run._o.Secrets, run._redactor));
            return await ActAsync(step, timeout, l => l.FillAsync(value, new LocatorFillOptions { Timeout = Ms(timeout) }));
        }

        private async Task<(FailureCause, string)?> ExpectVisibleAsync(Target target, TimeSpan timeout)
        {
            var found = await ResolveAsync(target, timeout);
            if (found.Failure is { } failure)
                return failure;
            return await PollAsync(timeout, () => found.Locator!.IsVisibleAsync())
                ? null
                : (FailureCause.AssertionFailed, $"{target} is on the page but not visible.");
        }

        private async Task<(FailureCause, string)?> ExpectHiddenAsync(Target target, TimeSpan timeout)
        {
            // Nothing found is hidden too: unlike every other step, a missing element passes here.
            return await PollAsync(timeout, async () => await VisibleCountAsync(target) == 0)
                ? null
                : (FailureCause.AssertionFailed, $"{target} is still visible.");
        }

        private async Task<int> VisibleCountAsync(Target target)
        {
            var scope = Page.Locator(":root");
            if (target.Within is { } within)
            {
                var container = await ResolveAsync(within, TimeSpan.Zero);
                if (container.Failure is not null)
                    return 0;
                scope = container.Locator!;
            }

            var all = Raw(scope, target);
            if (target.Nth is { } n)
                all = all.Nth(n);
            return await all.EvaluateAllAsync<int>("els => els.filter(e => e.checkVisibility ? e.checkVisibility() : e.offsetParent !== null).length");
        }

        private async Task<(FailureCause, string)?> ExpectTextAsync(Target target, string text, TimeSpan timeout)
        {
            var found = await ResolveAsync(target, timeout);
            if (found.Failure is { } failure)
                return failure;

            var wanted = Normalise(text);
            var actual = "";
            var ok = await PollAsync(timeout, async () =>
            {
                actual = Normalise(await found.Locator!.InnerTextAsync(new LocatorInnerTextOptions { Timeout = Ms(timeout) }));
                return actual.Contains(wanted, StringComparison.Ordinal);
            });
            return ok ? null : (FailureCause.AssertionFailed, $"{target} shows \"{Clip(actual)}\", which does not contain \"{wanted}\".");
        }

        private async Task<(FailureCause, string)?> ExpectUrlAsync(string url, TimeSpan timeout) =>
            await PollAsync(timeout, () => Task.FromResult(PageWatcher.PathOf(Page.Url).Contains(url, StringComparison.Ordinal)))
                ? null
                : (FailureCause.AssertionFailed, $"The page is {PageWatcher.PathOf(Page.Url)}, which does not contain \"{url}\".");

        private async Task<(FailureCause, string)?> ExpectStateAsync(Target target, ElementState state, TimeSpan timeout)
        {
            var found = await ResolveAsync(target, timeout);
            if (found.Failure is { } failure)
                return failure;

            var element = found.Locator!;
            var ok = await PollAsync(timeout, async () => state switch
            {
                ElementState.Enabled => await element.IsEnabledAsync(),
                ElementState.Disabled => await element.IsDisabledAsync(),
                ElementState.Checked => await element.IsCheckedAsync(),
                _ => !await element.IsCheckedAsync(),
            });
            return ok ? null : (FailureCause.AssertionFailed, $"{target} is not {state.ToString().ToLowerInvariant()}.");
        }

        private async Task<(FailureCause, string)?> ExpectResponseAsync(ScenarioStep step, TimeSpan timeout)
        {
            var path = step.Path!;
            bool Matches((string Method, string Path, int Status) r) =>
                (step.Method is null || string.Equals(r.Method, step.Method, StringComparison.OrdinalIgnoreCase))
                && (path.EndsWith('*') ? r.Path.StartsWith(path[..^1], StringComparison.Ordinal) : r.Path == path);

            List<(string Method, string Path, int Status)> Since()
            {
                lock (_responses)
                    return [.. _responses.Skip(_actionMark).Where(Matches)];
            }

            if (await PollAsync(timeout, () => Task.FromResult(Since().Any(r => r.Status == step.Status))))
                return null;

            var seen = Since();
            var request = $"{step.Method ?? "a request to"} {path}";
            return (FailureCause.AssertionFailed, seen.Count == 0
                ? $"No {request} was made after the step before."
                : $"{request} answered {string.Join(", ", seen.Select(r => r.Status).Distinct())}, not {step.Status}.");
        }

        // ---- Targets ----------------------------------------------------------------------

        private sealed record Found(ILocator? Locator, (FailureCause, string)? Failure);

        /// <summary>The one element a target names, waiting up to <paramref name="timeout"/> for it to appear.</summary>
        private async Task<Found> ResolveAsync(Target target, TimeSpan timeout)
        {
            var scope = Page.Locator(":root");
            if (target.Within is { } within)
            {
                var container = await ResolveAsync(within, timeout);
                if (container.Failure is not null)
                    return container;
                scope = container.Locator!;
            }

            var all = Raw(scope, target);
            var count = 0;
            await PollAsync(timeout, async () => (count = await all.CountAsync()) > 0);

            if (count == 0)
                return new Found(null, (FailureCause.LocatorNotFound, await NotFoundAsync(target)));
            if (target.Nth is { } n)
            {
                return n < count
                    ? new Found(all.Nth(n), null)
                    : new Found(null, (FailureCause.LocatorNotFound, $"{target}: there {(count == 1 ? "is 1 match" : $"are {count} matches")}, so there is no match {n + 1}."));
            }

            return count == 1
                ? new Found(all, null)
                : new Found(null, (FailureCause.LocatorAmbiguous,
                    $"{target} matches {count} elements. Add within (a container such as its row) or nth to say which one."));
        }

        private static ILocator Raw(ILocator scope, Target t) =>
            t.Role is { } role ? scope.GetByRole(Enum.Parse<AriaRole>(role, ignoreCase: true), new LocatorGetByRoleOptions { Name = t.Name, Exact = t.Name is not null })
            : t.Label is { } label ? scope.GetByLabel(label, new LocatorGetByLabelOptions { Exact = true })
            : t.TestId is { } id ? scope.GetByTestId(id)
            : t.Text is { } text ? scope.GetByText(text, new LocatorGetByTextOptions { Exact = true })
            : scope.Locator(t.Css!);

        /// <summary>"No button named "Save note" on /. Buttons on the page: "Add note"." The person decides: renamed, or missing.</summary>
        private async Task<string> NotFoundAsync(Target target)
        {
            var where = PageWatcher.PathOf(Page.Url);
            var (kind, names) = target switch
            {
                { Role: { } role } => (Plural(role), await NamesAsync(Page.GetByRole(Enum.Parse<AriaRole>(role, ignoreCase: true)), NameScript)),
                { Label: not null } => ("Field labels", await NamesAsync(Page.Locator("label"), "e => e.innerText")),
                { TestId: not null } => ("data-testid values", await NamesAsync(Page.Locator("[data-testid]"), "e => e.getAttribute('data-testid')")),
                _ => ("", (IReadOnlyList<string>)[]),
            };

            var missing = target.Role is { } r && target.Name is { } name ? $"No {r} named \"{name}\"" : $"Nothing matches {target}";
            if (kind.Length == 0)
                return $"{missing} on {where}.";
            return names.Count == 0
                ? $"{missing} on {where}, and there are no {kind.ToLowerInvariant()} on the page."
                : $"{missing} on {where}. {kind} on the page: {string.Join(", ", names.Select(n => $"\"{n}\""))}.";
        }

        private const string NameScript = "e => e.getAttribute('aria-label') || e.innerText || e.value || e.getAttribute('title') || ''";

        private static async Task<IReadOnlyList<string>> NamesAsync(ILocator elements, string name)
        {
            try
            {
                var all = await elements.EvaluateAllAsync<string[]>($"els => els.map({name})");
                return [.. all.Select(Normalise).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Take(8).Select(n => Clip(n, 60))];
            }
            catch (Exception ex) when (IsBrowserError(ex))
            {
                return [];
            }
        }

        private static string Plural(string role) => role switch
        {
            "checkbox" => "Checkboxes",
            "listbox" => "Listboxes",
            "textbox" => "Text boxes",
            _ => char.ToUpperInvariant(role[0]) + role[1..] + "s",
        };

        /// <summary>Polls until <paramref name="condition"/> holds or <paramref name="timeout"/> passes; checks at least once.</summary>
        private async Task<bool> PollAsync(TimeSpan timeout, Func<Task<bool>> condition)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (await condition())
                        return true;
                }
                catch (Exception ex) when (IsBrowserError(ex) && !run._limit.IsCancellationRequested)
                {
                    // Mid-navigation, say: try again until the time is up.
                }

                if (clock.Elapsed >= timeout)
                    return false;
                await Task.Delay(150, run._limit);
            }
        }

        private static string Describe(ScenarioStep s) => s.Type switch
        {
            StepType.Navigate => $"open {s.Path}",
            StepType.Click => $"click {s.Target}",
            StepType.Fill => Placeholders.Find(s.Value).Any(p => p.Kind == PlaceholderKind.Secret)
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

        private static string Normalise(string text) => WhiteSpace().Replace(text, " ").Trim();

        private static string Clip(string text, int max = 200) => text.Length > max ? text[..max] + "…" : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
