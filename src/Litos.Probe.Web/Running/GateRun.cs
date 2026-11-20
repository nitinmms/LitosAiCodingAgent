using System.Diagnostics;
using System.Text.Json;
using Litos.Probe.Web.Browser;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Processes;
using Microsoft.Playwright;

namespace Litos.Probe.Web.Running;

/// <summary>
/// One run of the Gate (README §6.1), in any of its scopes: all 8 steps, smoke (1–6), or a dry
/// run of a stack profile's defaults. It owns the app and the browser for the run's lifetime, and
/// whatever happens (a failure, its time limit, the caller cancelling) they are stopped before it
/// returns.
/// </summary>
internal sealed class GateRun
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly string _workingCopy;
    private readonly WebAppContract _contract;
    private readonly StackProfile _profile;
    private readonly GateScope _scope;
    private readonly ProbeWebOptions _o;
    private readonly ReportKind _kind;
    private readonly IProcessLauncher _launcher;
    private readonly ServiceCatalog? _services;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private IReadOnlyList<CapturedEmail> _emails = [];
    private readonly SecretRedactor _redactor = new();
    private readonly List<CheckResult> _checks = [];
    private readonly List<string> _findings = [];
    private readonly List<LintFinding> _lintFindings = [];

    /// <summary>Console errors the stack profile ignores, by rule, for the report's findings.</summary>
    private readonly Dictionary<IgnoredConsole, int> _ignoredConsole = [];
    private readonly Dictionary<string, string> _storageStates = new(StringComparer.Ordinal);
    private int _lintPages;
    private int _lintInteractive;
    private bool _loginSeen;
    private CheckKind _current = CheckKind.Build;
    private AppEnvironment _env = null!;
    private BrowserHost? _browser;
    private string _scratch = "";
    private CancellationToken _limit;

    public GateRun(string workingCopy, WebAppContract effectiveContract, StackProfile profile, GateScope scope, ReportKind kind, ProbeWebOptions options, IProcessLauncher launcher,
        ServiceCatalog? services = null)
    {
        _services = services;
        _workingCopy = Path.GetFullPath(workingCopy);
        _contract = effectiveContract;
        _profile = profile;
        _scope = scope;
        _kind = kind;
        _o = options;
        _launcher = launcher;
    }

    private AuthDefinition Auth => _contract.Testability.Auth;

    public async Task<ProbeWebReport> RunAsync(CancellationToken ct)
    {
        var wall = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_o.Timeout);
        _limit = limit.Token;
        _scratch = Path.Combine(_o.TempDirectory ?? Path.GetTempPath(), "litos-probe-web", "runs", _runId);
        Directory.CreateDirectory(_scratch);
        AppInfo? appInfo = null;

        try
        {
            using var folderLock = WorkingCopyLock.Acquire(_workingCopy);
            _env = new AppEnvironment(_workingCopy, _contract, _profile, _o, _redactor, _launcher, _scratch, _services, _runId);
            // When the limit passes, closing the browser ends whatever page call is hanging.
            using var abort = limit.Token.Register(() => _ = _browser?.DisposeAsync().AsTask());
            try
            {
                await StepsAsync(limit.Token);
            }
            // The limit passed: either a cancellation, or a page call failing because the browser
            // was closed under it. The caller's own cancellation propagates.
            catch (Exception ex) when (!ct.IsCancellationRequested
                && (ex is OperationCanceledException || (IsBrowserError(ex) && limit.IsCancellationRequested)))
            {
                Add(Classifier.Fail("timeout", _current, FailureCause.Timeout, $"Stopped: the run went past its {_o.Timeout.TotalMinutes:0.#}-minute limit."), force: true);
            }
            finally
            {
                // Each part of the teardown runs whatever the part before it did: the app's
                // processes must go even if the browser's driver misbehaves.
                if (_browser is not null)
                    await _browser.DisposeAsync();
                try
                {
                    // Emails are read while Mailpit still runs, then everything stops.
                    if (_env.HasServices)
                        _emails = await _env.CaptureEmailsAsync(_o.Artifacts, CancellationToken.None);
                    await _env.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        await _env.SaveLogsAsync(_o.Artifacts, CancellationToken.None);
                    }
                    catch (IOException ex)
                    {
                        _findings.Add($"The app's logs could not be saved: {ex.Message}");
                    }
                }

                _findings.AddRange(_env.StopWarnings);
                if (_checks.Any(c => c.Kind == CheckKind.Start && c.Status == CheckStatus.Passed))
                {
                    appInfo = new AppInfo
                    {
                        BaseUrl = _env.BaseUrl,
                        StartSeconds = Math.Round(_env.StartDuration.TotalSeconds, 1),
                        Processes = _env.Processes,
                        Services = _env.Services,
                    };
                }
            }
        }
        catch (WorkingCopyBusyException ex)
        {
            Add(Classifier.Fail("start", CheckKind.Start, FailureCause.NotReady, ex.Message), force: true);
        }
        finally
        {
            TryDelete(_scratch);
        }

        var checks = _checks.OrderBy(c => Step(c.Kind)).ToList();
        if (_kind == ReportKind.DryRun)
            _findings.InsertRange(0, DryRunFindings(checks));

        return new ProbeWebReport
        {
            Kind = _kind,
            Outcome = ReportOutcome.Compute(checks),
            Commit = await CommitAsync(),
            Contract = new ContractInfo
            {
                Source = _kind == ReportKind.DryRun ? "defaults" : _o.ContractSource,
                Revision = _o.ContractRevision,
                Stack = _profile.Id,
            },
            App = appInfo,
            Checks = checks,
            Lint = checks.Any(c => c.Kind == CheckKind.SelectorLint) ? new LintSummary
            {
                Pages = _lintPages,
                Interactive = _lintInteractive,
                Unnamed = _lintFindings.Count,
                Findings = _lintFindings,
            } : null,
            Findings = [.. _findings.Concat(IgnoredConsoleFindings()).Select(_redactor.Redact)],
            Emails = _emails,
            Usage = new Usage { ModelCalls = 0, WallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 1) },
        };
    }

    private async Task StepsAsync(CancellationToken ct)
    {
        if (!await BringUpAsync(ct))
            return;

        try
        {
            _browser = await BrowserHost.LaunchAsync(_o.Headless);
        }
        catch (BrowserUnavailableException ex)
        {
            Add(Classifier.Fail("browser", CheckKind.Start, FailureCause.NotReady, ex.Message));
            return;
        }

        await SignInAllAsync(ct);
        await SignedOutStepsAsync(ct);
        await RoleStepsAsync(ct);
        AddLint();

        // Step 7 last: it restarts the app as Production.
        if (_scope.Includes(7))
        {
            _current = CheckKind.ProductionTestEndpoints;
            Add(await ProductionEndpointsAsync(ct));
        }
    }

    /// <summary>Steps 1 and 2: build, start and reset, in the order the reset strategy needs. False when the app isn't up.</summary>
    private async Task<bool> BringUpAsync(CancellationToken ct)
    {
        _current = CheckKind.Build;
        if (Add(await _env.BuildAsync(ct)).Failed)
            return false;

        // Services before the app: its reset and its processes need the database up.
        if (_env.HasServices)
        {
            _current = CheckKind.Services;
            if (Add(await _env.StartServicesAsync(ct)).Failed)
                return false;
        }

        if (_env.ResetsBeforeStart)
        {
            _current = CheckKind.Reset;
            if (Add(await _env.ResetAsync(ct)).Failed)
                return false;
        }

        _current = CheckKind.Start;
        if (Add(await _env.StartAsync(overrides: null, label: "", strict: true, ct)).Failed)
            return false;

        if (!_env.ResetsBeforeStart)
        {
            _current = CheckKind.Reset;
            if (Add(await _env.ResetAsync(ct)).Failed)
                return false;
        }

        return true;
    }

    private bool HasLogin => Auth.Type == AuthType.Form;

    /// <summary>Steps 4 and 8 both need the pages visited; only step 4 records the visits as checks.</summary>
    private bool VisitsRoutes => _scope.Includes(4) || _scope.Includes(8);

    /// <summary>Step 3: sign in as each role, keeping only the storage state, in memory.</summary>
    private async Task SignInAllAsync(CancellationToken ct)
    {
        if (!HasLogin)
        {
            if (_scope.Includes(3))
                Add(CheckResult.Skip("signin", CheckKind.SignIn, "The app has no login (auth.type: none)."));
            return;
        }

        if (!_scope.Includes(3) && !_scope.Includes(4) && !_scope.Includes(6))
            return;

        _current = CheckKind.SignIn;
        foreach (var (role, credentials) in Auth.Roles)
        {
            ct.ThrowIfCancellationRequested();
            Add(await SignInAsync(role, credentials));
        }
    }

    /// <summary>Step 4's public routes and step 5's logged-out redirects, in one signed-out traced context.</summary>
    private async Task SignedOutStepsAsync(CancellationToken ct)
    {
        var routes = _contract.Testability.Routes;
        await using var anonymous = await OpenSessionAsync(role: null, trace: true);
        await VisitRoutesAsync(anonymous, routes.Public, ct);

        if (!_scope.Includes(5))
            return;

        _current = CheckKind.LoggedOutRedirect;
        if (!HasLogin)
        {
            Add(CheckResult.Skip("logged-out", CheckKind.LoggedOutRedirect, "The app has no login (auth.type: none)."));
            return;
        }

        foreach (var route in routes.Protected.Values.SelectMany(r => r).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Add(await WithRerunAsync(anonymous, (s, _) => LoggedOutCheckAsync(s, route)), anonymous);
        }
    }

    /// <summary>Step 4's protected routes and step 6's denied ones, per role, each role in its own traced context.</summary>
    private async Task RoleStepsAsync(CancellationToken ct)
    {
        var routes = _contract.Testability.Routes;
        foreach (var role in routes.Protected.Keys.Concat(routes.Denied.Keys).Distinct(StringComparer.Ordinal))
        {
            var mine = routes.Protected.GetValueOrDefault(role) ?? [];
            var denied = routes.Denied.GetValueOrDefault(role) ?? [];
            if (!_storageStates.ContainsKey(role))
            {
                SkipRole(role, mine, denied);
                continue;
            }

            await using var session = await OpenSessionAsync(role, trace: true);
            await VisitRoutesAsync(session, mine, ct);

            if (!_scope.Includes(6))
                continue;
            _current = CheckKind.Denied;
            foreach (var route in denied)
            {
                ct.ThrowIfCancellationRequested();
                Add(await WithRerunAsync(session, (s, _) => DeniedCheckAsync(s, route)), session);
            }
        }
    }

    private async Task VisitRoutesAsync(Session session, IEnumerable<string> routes, CancellationToken ct)
    {
        if (!VisitsRoutes)
            return;

        _current = CheckKind.RouteVisit;
        foreach (var route in routes)
        {
            ct.ThrowIfCancellationRequested();
            var check = await WithRerunAsync(session, (s, rerun) => VisitCheckAsync(s, route, lint: !rerun));
            if (_scope.Includes(4))
                Add(check, session);
            if (_kind == ReportKind.DryRun && session.Role is null && route == "/")
                _loginSeen = await HasLoginAsync(session.Page);
        }
    }

    /// <summary>A role whose sign-in failed: its pages are skipped, not failed a second time.</summary>
    private void SkipRole(string role, IEnumerable<string> mine, IEnumerable<string> denied)
    {
        foreach (var route in _scope.Includes(4) ? mine : [])
            Add(CheckResult.Skip($"visit:{role}:{route}", CheckKind.RouteVisit, $"Not signed in as {role}.") with { Role = role, Route = route });
        foreach (var route in _scope.Includes(6) ? denied : [])
            Add(CheckResult.Skip($"denied:{role}:{route}", CheckKind.Denied, $"Not signed in as {role}.") with { Role = role, Route = route });
    }

    /// <summary>Step 8: the lint, over every page step 4 visited.</summary>
    private void AddLint()
    {
        if (!_scope.Includes(8))
            return;

        _current = CheckKind.SelectorLint;
        Add(_lintFindings.Count == 0
            ? CheckResult.Pass("lint", CheckKind.SelectorLint) with { Message = $"{_lintInteractive} interactive elements on {_lintPages} pages, all named." }
            : Classifier.Fail("lint", CheckKind.SelectorLint, FailureCause.UnnamedElement,
                $"{_lintFindings.Count} interactive element(s) have no accessible name and no data-testid, e.g. {_lintFindings[0].Element} on {_lintFindings[0].Route}."));
    }

    // ---- Checks -----------------------------------------------------------------------------

    private async Task<CheckResult> SignInAsync(string role, RoleCredentials credentials)
    {
        var id = $"signin:{role}";
        var started = Stopwatch.StartNew();
        string user, password;
        try
        {
            var values = new RunValues(new Dictionary<string, int>(), _o.Secrets, _redactor);
            user = Placeholders.Resolve(credentials.User, values);
            password = Placeholders.Resolve(credentials.Password, values);
        }
        catch (PlaceholderException ex)
        {
            return Classifier.Fail(id, CheckKind.SignIn, FailureCause.SecretMissing, $"Cannot sign in as {role}: {ex.Message}.") with { Role = role };
        }

        _redactor.Add(password);
        var loginUrl = Auth.LoginUrl!;
        // No tracing here: a trace would record the password being typed.
        var context = await _browser!.NewContextAsync(_env.BaseUrl, _env.AppOrigins, _contract.Application.AllowedOrigins, storageState: null);
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Ms(_o.PageTimeout) });

            var userField = page.GetByLabel(Auth.Fields!.User, new PageGetByLabelOptions { Exact = true }).First;
            var passwordField = page.GetByLabel(Auth.Fields.Password, new PageGetByLabelOptions { Exact = true }).First;
            var submit = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Auth.Submit, Exact = true }).First;
            foreach (var (locator, what) in new[] { (userField, $"field labelled '{Auth.Fields.User}'"), (passwordField, $"field labelled '{Auth.Fields.Password}'"), (submit, $"button named '{Auth.Submit}'") })
            {
                if (!await AppearsAsync(locator))
                    return Classifier.Fail(id, CheckKind.SignIn, FailureCause.LoginFieldUnlabelled, $"There is no {what} on {loginUrl}.") with { Role = role, DurationMs = started.ElapsedMilliseconds };
            }

            await userField.FillAsync(user);
            await passwordField.FillAsync(password);
            await submit.ClickAsync();

            if (!await SignedInAsync(page))
            {
                // What the page said and what the server logged (finding 10): a seeding error at
                // start-up is the usual cause, and it is in the log long before the sign-in.
                var screenshot = await ScreenshotAsync(page, $"signin-{role}");
                return Classifier.Fail(id, CheckKind.SignIn, FailureCause.SignInNotReached,
                    $"Signed in as {role}, but {Describe(Auth.SuccessWhen!)} was not reached (the page is {PageWatcher.PathOf(page.Url)}). Check the test user's credentials.") with
                {
                    Role = role,
                    DurationMs = started.ElapsedMilliseconds,
                    Evidence = new Evidence { Log = _env.DiagnosisLines(), Screenshots = screenshot is null ? [] : [screenshot] },
                };
            }

            _storageStates[role] = await context.StorageStateAsync();
            return CheckResult.Pass(id, CheckKind.SignIn) with { Role = role, DurationMs = started.ElapsedMilliseconds };
        }
        catch (Exception ex) when (IsBrowserError(ex) && !_limit.IsCancellationRequested)
        {
            return Classifier.Fail(id, CheckKind.SignIn, FailureCause.SignInNotReached, $"Signing in as {role} failed: {FirstLine(ex.Message)}") with { Role = role };
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private async Task<CheckResult> VisitCheckAsync(Session s, string route, bool lint)
    {
        var id = $"visit:{s.Label}:{route}";
        var v = await VisitAsync(s, route, waitReady: true, lint, $"visit-{s.Label}-{route}");

        if (lint && v.Lint is { } result)
        {
            _lintPages++;
            _lintInteractive += result.Interactive;
            _lintFindings.AddRange(result.Unnamed.Select(e => new LintFinding(route, s.Role, e)));
        }

        var failure = PageBroken(v, route, "") ?? v switch
        {
            { Status: 404 } => (FailureCause.RouteNotFound, $"{route} answered 404: the contract declares a route the app does not have."),
            _ when s.Role is not null && IsLoginPage(v.FinalPath) => (FailureCause.SignedOutDuringVisit, $"{route} sent {s.Role} back to the login page."),
            { MarkerMissing: true } => (FailureCause.MarkerMissing, $"The readiness marker {ReadyDescription()} never appeared on {route}."),
            { Signals.Count: > 0 } => (FailureCause.PageSignal, Summarise(v.Signals)),
            { Log.Count: > 0 } => (FailureCause.ServerLogError, $"The server logged an error during the visit: {v.Log[0]}"),
            _ => ((FailureCause, string)?)null,
        };

        return Finish(id, CheckKind.RouteVisit, s.Role, route, v, failure);
    }

    private async Task<CheckResult> LoggedOutCheckAsync(Session s, string route)
    {
        var v = await VisitAsync(s, route, waitReady: false, lint: false, $"logged-out-{route}");
        var failure = PageBroken(v, route, " while signed out") ?? v switch
        {
            _ when IsLoginPage(v.FinalPath) => ((FailureCause, string)?)null,
            _ => (FailureCause.NotRedirectedToLogin, $"{route} opened without signing in (it ended at {v.FinalPath}, HTTP {v.Status}), instead of redirecting to {Auth.LoginUrl}."),
        };
        return Finish($"logged-out:{route}", CheckKind.LoggedOutRedirect, null, route, v, failure);
    }

    private async Task<CheckResult> DeniedCheckAsync(Session s, string route)
    {
        var v = await VisitAsync(s, route, waitReady: false, lint: false, $"denied-{s.Label}-{route}");
        var failure = PageBroken(v, route, $" to {s.Role}") ?? v switch
        {
            { Status: 401 or 403 } => ((FailureCause, string)?)null,
            _ when !SamePath(v.FinalPath, route) => null,
            _ => (FailureCause.DeniedRouteOpened, $"{s.Role} opened {route} (HTTP {v.Status}); it should be refused (401/403) or redirected away."),
        };
        return Finish($"denied:{s.Role}:{route}", CheckKind.Denied, s.Role, route, v, failure);
    }

    /// <summary>What fails any page check before its own rule applies: the page didn't load, or the server failed.</summary>
    private static (FailureCause, string)? PageBroken(PageVisit v, string route, string context) => v switch
    {
        { LoadError: { } error } => (FailureCause.PageSignal, $"{route} did not load: {error}"),
        { Status: >= 500 } => (FailureCause.PageServerError, $"{route} answered HTTP {v.Status}{context}."),
        _ => null,
    };

    /// <summary>Gate step 7: restart as Production and require every <c>/__test/*</c> endpoint to be gone.</summary>
    private async Task<CheckResult> ProductionEndpointsAsync(CancellationToken ct)
    {
        const string id = "production-test-endpoints";
        if (_profile.ProductionEnvironment.Count == 0)
            return CheckResult.Skip(id, CheckKind.ProductionTestEndpoints, $"The stack profile {_profile.Id} has no Production switch.");

        var endpoints = _contract.Testability.TestEndpoints.Values.ToList();
        if (_contract.Application.Reset is { Strategy: ResetStrategy.Endpoint, Endpoint: { } reset })
            endpoints.Add(reset);
        if (endpoints.Count == 0)
            endpoints.Add("POST /__test/reset");

        var started = Stopwatch.StartNew();
        await _env.StopAsync();
        var start = await _env.StartAsync(_profile.ProductionEnvironment, label: "production", strict: false, ct);
        if (start.Failed)
        {
            return Classifier.Fail(id, CheckKind.ProductionTestEndpoints, FailureCause.ProductionStartFailed,
                $"The app did not start as Production ({string.Join(", ", _profile.ProductionEnvironment.Select(e => $"{e.Key}={e.Value}"))}): {start.Message}") with
            {
                Evidence = start.Evidence,
            };
        }

        var open = new List<string>();
        foreach (var endpoint in endpoints.Distinct(StringComparer.Ordinal))
        {
            var (method, path) = AppEnvironment.SplitEndpoint(endpoint);
            try
            {
                // What this app answers for the same method on a path that cannot exist: Blazor, for
                // one, answers any unknown POST with 400, not 404.
                using var control = await _env.SendAsync(method, $"/__test/__absent-{Guid.NewGuid():N}", ct);
                using var response = await _env.SendAsync(method, path, ct);
                if (!LooksAbsent((int)response.StatusCode, (int)control.StatusCode))
                    open.Add($"{method} {path} answered {(int)response.StatusCode} (an unknown path answers {(int)control.StatusCode})");
            }
            catch (HttpRequestException ex)
            {
                open.Add($"{method} {path} failed: {ex.Message}");
            }
        }

        await _env.StopAsync();
        return open.Count == 0
            ? CheckResult.Pass(id, CheckKind.ProductionTestEndpoints) with { Message = $"{endpoints.Count} test endpoint(s) are absent in Production.", DurationMs = started.ElapsedMilliseconds }
            : Classifier.Fail(id, CheckKind.ProductionTestEndpoints, FailureCause.TestEndpointInProduction,
                $"Test endpoints must exist only in Testing, but in Production {string.Join("; ", open)}.") with { DurationMs = started.ElapsedMilliseconds };
    }

    /// <summary>
    /// A test endpoint is absent when it answers as an unknown path does (or 404/405), and never
    /// when it succeeds. Comparing with a control keeps step 7 free of per-stack status codes.
    /// </summary>
    internal static bool LooksAbsent(int status, int controlStatus) =>
        status is < 200 or >= 300 && (status == controlStatus || status is 404 or 405);

    // ---- Pages ------------------------------------------------------------------------------

    private sealed record PageVisit(int? Status, string FinalPath, string? LoadError, bool MarkerMissing, IReadOnlyList<string> Signals, int ConsoleErrors, IReadOnlyList<string> Log, string? Screenshot, LintResult? Lint, long DurationMs);

    private async Task<PageVisit> VisitAsync(Session s, string route, bool waitReady, bool lint, string shotName)
    {
        var started = Stopwatch.StartNew();
        var logMark = _env.Mark();
        var signalMark = s.Watcher.Mark();
        int? status = null;
        string? loadError = null;
        var markerMissing = false;
        try
        {
            var response = await s.Page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Ms(_o.PageTimeout) });
            status = response?.Status;
            if (waitReady)
                markerMissing = !await PageReadyAsync(s.Page);
        }
        catch (Exception ex) when (IsBrowserError(ex) && !_limit.IsCancellationRequested)
        {
            loadError = FirstLine(ex.Message);
        }

        await Task.Delay(Settle, _limit);
        var markers = loadError is null ? await VisibleErrorMarkersAsync(s.Page) : [];
        var lintResult = lint && loadError is null ? await LintAsync(s.Page) : null;
        var screenshot = await ScreenshotAsync(s.Page, shotName);
        var (signals, consoleErrors) = s.Watcher.Since(signalMark);

        return new PageVisit(status, PageWatcher.PathOf(s.Page.Url), loadError, markerMissing, [.. signals, .. markers], consoleErrors,
            _env.ErrorLinesSince(logMark), screenshot, lintResult, started.ElapsedMilliseconds);
    }

    private CheckResult Finish(string id, CheckKind kind, string? role, string route, PageVisit v, (FailureCause Cause, string Message)? failure)
    {
        var check = failure is { } f
            ? Classifier.Fail(id, kind, f.Cause, _redactor.Redact(f.Message))
            : CheckResult.Pass(id, kind);
        return check with
        {
            Role = role,
            Route = route,
            DurationMs = v.DurationMs,
            Evidence = new Evidence
            {
                Screenshots = v.Screenshot is null ? [] : [v.Screenshot],
                ConsoleErrors = v.ConsoleErrors,
                Signals = _redactor.Redact(v.Signals),
                Log = v.Log,
            },
        };
    }

    /// <summary>
    /// Rerun before blame (testability-architecture.md §7.3): an application failure is tried once
    /// more in a fresh context. Passing then makes it Flaky: reported, not repaired.
    /// </summary>
    private async Task<CheckResult> WithRerunAsync(Session session, Func<Session, bool, Task<CheckResult>> attempt)
    {
        var first = await attempt(session, false);
        if (!first.Failed || first.Classification != Classification.ApplicationDefect)
            return first;

        for (var i = 0; i < _o.FlakyReruns; i++)
        {
            await using var fresh = await OpenSessionAsync(session.Role, trace: false);
            var again = await attempt(fresh, true);
            if (!again.Failed)
            {
                return again with
                {
                    Classification = Classification.Flaky,
                    Message = $"Failed once ({first.Message}), then passed on its rerun in a fresh context.",
                    Evidence = first.Evidence with { Screenshots = [.. first.Evidence.Screenshots, .. again.Evidence.Screenshots] },
                };
            }
        }

        return first;
    }

    private async Task<bool> PageReadyAsync(IPage page)
    {
        var ready = _contract.Testability.Ready;
        try
        {
            if (ready?.Selector is { } selector)
                await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = Ms(_o.PageTimeout) });
            if (ready?.Js is { } js)
                await page.WaitForFunctionAsync(js, null, new PageWaitForFunctionOptions { Timeout = Ms(_o.PageTimeout) });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> VisibleErrorMarkersAsync(IPage page)
    {
        var found = new List<string>();
        foreach (var signal in _contract.Testability.FailOn)
        {
            try
            {
                if (signal.SelectorVisible is { } selector && await page.Locator(selector).First.IsVisibleAsync())
                    found.Add($"Error marker visible: {selector}");
                else if (signal.TextVisible is { } text && await page.GetByText(text).First.IsVisibleAsync())
                    found.Add($"Error text visible: \"{text}\"");
            }
            catch (Exception ex) when (IsBrowserError(ex))
            {
                // An invalid selector in a contract is not an app failure.
            }
        }

        return found;
    }

    private static async Task<LintResult?> LintAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateAsync<JsonElement>(PageScripts.Lint);
            return new LintResult(
                json.GetProperty("interactive").GetInt32(),
                [.. json.GetProperty("unnamed").EnumerateArray().Select(e => e.GetString() ?? "")]);
        }
        catch (Exception ex) when (IsBrowserError(ex))
        {
            return null;
        }
    }

    private static async Task<bool> HasLoginAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(PageScripts.HasLogin);
        }
        catch (Exception ex) when (IsBrowserError(ex))
        {
            return false;
        }
    }

    private async Task<string?> ScreenshotAsync(IPage page, string name)
    {
        try
        {
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true, Timeout = Ms(_o.PageTimeout) });
            using var content = new MemoryStream(bytes);
            return await _o.Artifacts.SaveAsync(ArtifactKind.Screenshot, ArtifactName(name) + ".png", content, CancellationToken.None);
        }
        catch (Exception ex) when (IsBrowserError(ex))
        {
            return null;
        }
    }

    private async Task<bool> SignedInAsync(IPage page)
    {
        var when = Auth.SuccessWhen!;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < _o.PageTimeout)
        {
            var url = page.Url;
            var ok = (when.UrlNotContains is null || !url.Contains(when.UrlNotContains, StringComparison.OrdinalIgnoreCase))
                && (when.UrlContains is null || url.Contains(when.UrlContains, StringComparison.OrdinalIgnoreCase))
                && (when.SelectorVisible is null || await page.Locator(when.SelectorVisible).First.IsVisibleAsync());
            if (ok)
            {
                // The redirect after the form post may still be loading; the storage state is
                // taken from the cookies either way, so this wait is best-effort.
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 5_000 });
                }
                catch (TimeoutException)
                {
                }

                return true;
            }

            await Task.Delay(200, _limit);
        }

        return false;
    }

    private async Task<bool> AppearsAsync(ILocator locator)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = Ms(_o.PageTimeout) });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ---- Sessions ---------------------------------------------------------------------------

    /// <summary>A browser context for one role (or signed out), with its page and signal watcher, and optionally a trace.</summary>
    private sealed class Session(GateRun run, string? role, IBrowserContext context, IPage page, bool traced) : IAsyncDisposable
    {
        public string? Role { get; } = role;
        public string Label => Role ?? "anonymous";
        public IPage Page { get; } = page;
        public PageWatcher Watcher { get; } = new(page, url => run._env.AppOrigins.Contains(BrowserHost.OriginOf(url)), run._contract.Testability.ConsoleErrors,
            run._profile.IgnoreConsole, run.CountIgnored);
        public List<int> CheckIndexes { get; } = [];

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (traced)
                {
                    var path = Path.Combine(run._scratch, $"trace-{ArtifactName(Label)}.zip");
                    await context.Tracing.StopAsync(new TracingStopOptions { Path = path });
                    string id;
                    await using (var file = File.OpenRead(path))
                        id = await run._o.Artifacts.SaveAsync(ArtifactKind.Trace, $"trace-{ArtifactName(Label)}.zip", file, CancellationToken.None);
                    foreach (var index in CheckIndexes)
                        run._checks[index] = run._checks[index] with { Evidence = run._checks[index].Evidence with { Trace = id } };
                }

                await context.CloseAsync();
            }
            catch (Exception ex) when (IsBrowserError(ex))
            {
                // The browser is already gone (the run's limit passed); the trace is lost with it.
            }
        }
    }

    private async Task<Session> OpenSessionAsync(string? role, bool trace)
    {
        var state = role is null ? null : _storageStates.GetValueOrDefault(role);
        var context = await _browser!.NewContextAsync(_env.BaseUrl, _env.AppOrigins, _contract.Application.AllowedOrigins, state);
        if (trace)
            await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Title = role ?? "anonymous" });
        var page = await context.NewPageAsync();
        return new Session(this, role, context, page, trace);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private void CountIgnored(IgnoredConsole rule)
    {
        lock (_ignoredConsole)
            _ignoredConsole[rule] = _ignoredConsole.GetValueOrDefault(rule) + 1;
    }

    private List<string> IgnoredConsoleFindings()
    {
        lock (_ignoredConsole)
        {
            return [.. _ignoredConsole.Select(p =>
                $"Ignored {p.Value} console.error line{(p.Value == 1 ? "" : "s")} from {p.Key.Source} ({_profile.Id} profile): {p.Key.Reason}")];
        }
    }

    private CheckResult Add(CheckResult check, Session? session = null, bool force = false)
    {
        // After the limit passes, the browser is closed under whatever was running, and the
        // failures that follow are noise: only the timeout itself is recorded.
        if (_limit.IsCancellationRequested && !force)
            return check;

        check = check with { Message = check.Message is null ? null : _redactor.Redact(check.Message) };
        _checks.Add(check);
        session?.CheckIndexes.Add(_checks.Count - 1);
        _o.Progress?.Invoke($"{Symbol(check.Status)} {check.Id}{(check.Message is null || check.Status == CheckStatus.Passed ? "" : $": {check.Message}")}");
        return check;
    }

    private IEnumerable<string> DryRunFindings(IReadOnlyList<CheckResult> checks)
    {
        yield return $"No contract: these are the {_profile.Id} profile's defaults ({string.Join(" · ", _contract.Application.Processes.Select(p => string.Join(' ', p.Command)))}).";
        foreach (var check in checks.Where(c => c.Failed))
            yield return check.Message ?? check.Id;
        if (checks.Any(c => c.Kind == CheckKind.Start && c.Status == CheckStatus.Passed))
        {
            yield return "No reset declared: add one (strategy: none if the app keeps no data).";
            if (_loginSeen)
                yield return "A sign-in form or link is on /: declare auth (type: form) and one test user per role, as secrets.";
            if (_profile.Readiness.Mode == ReadinessMode.NeedsMarker && !checks.Any(c => c.Failed && c.Message?.Contains("readiness marker") == true))
                yield return $"The readiness marker {_profile.Readiness.DefaultSelector} is present.";
        }
    }

    private async Task<string?> CommitAsync()
    {
        try
        {
            var launch = new ProcessLaunch("git", "git", ["rev-parse", "--short", "HEAD"], _workingCopy, _o.BaseEnvironment);
            var result = await CommandRunner.RunAsync(_launcher, launch, TimeSpan.FromSeconds(10), CancellationToken.None, 5);
            return result.Succeeded ? result.Tail.LastOrDefault(l => l.Stream == OutputStream.Out)?.Text.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool IsLoginPage(string path) =>
        Auth.LoginUrl is { } login && PathOnly(path).StartsWith(PathOnly(login), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(PathOnly(a).TrimEnd('/'), PathOnly(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string PathOnly(string pathAndQuery) => pathAndQuery.Split('?', '#')[0];

    private string ReadyDescription() =>
        _contract.Testability.Ready is { } r ? r.Selector ?? r.Js ?? "" : "";

    private static string Describe(SuccessCondition when) => string.Join(" and ", new[]
    {
        when.UrlNotContains is { } a ? $"a URL without '{a}'" : null,
        when.UrlContains is { } b ? $"a URL with '{b}'" : null,
        when.SelectorVisible is { } c ? $"{c} visible" : null,
    }.Where(s => s is not null));

    private static string Summarise(IReadOnlyList<string> signals) =>
        signals.Count == 1 ? signals[0] : $"{signals[0]} (and {signals.Count - 1} more signal{(signals.Count == 2 ? "" : "s")})";

    private static int Step(CheckKind kind) => kind switch
    {
        CheckKind.Build or CheckKind.Services or CheckKind.Start => 1,
        CheckKind.Reset => 2,
        CheckKind.SignIn => 3,
        CheckKind.RouteVisit => 4,
        CheckKind.LoggedOutRedirect => 5,
        CheckKind.Denied => 6,
        CheckKind.ProductionTestEndpoints => 7,
        CheckKind.SelectorLint => 8,
        _ => 9,
    };

    private static string Symbol(CheckStatus status) => status switch
    {
        CheckStatus.Passed => "✓",
        CheckStatus.Failed => "✗",
        _ => "–",
    };

    internal static string ArtifactName(string text)
    {
        var chars = text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-').ToArray();
        var name = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "root" : name.Length > 80 ? name[..80] : name;
    }

    private static float Ms(TimeSpan span) => (float)span.TotalMilliseconds;

    /// <summary>What a page call throws: Playwright's own errors, and plain <see cref="TimeoutException"/> for its timeouts.</summary>
    private static bool IsBrowserError(Exception ex) => ex is PlaywrightException or TimeoutException;

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by an exiting process; the temp folder is cleaned up eventually.
        }
    }
}
