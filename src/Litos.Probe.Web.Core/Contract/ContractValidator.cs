using System.Text.RegularExpressions;
using Litos.Probe.Web.Core.Profiles;

namespace Litos.Probe.Web.Core.Contract;

/// <summary>
/// Whether a contract can be run, with every problem in one list (testability-architecture.md §5
/// rules). Checked against the contract after its stack profile's defaults are applied, so a
/// process may omit the serve command its profile supplies.
/// </summary>
public static partial class ContractValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(?:(GET|POST|PUT|PATCH|DELETE) )?/\S*$")]
    private static partial Regex EndpointPattern();

    public static IReadOnlyList<string> Validate(WebAppContract contract, StackProfile profile)
    {
        var errors = new List<string>();
        var raw = contract.Application;

        if (contract.Version != 1)
            errors.Add($"version: {contract.Version} is not supported (only 1)");
        if (raw.Stack is { } stack && stack != profile.Id)
            errors.Add($"application.stack: the contract names '{stack}' but was checked against the profile '{profile.Id}'");
        if (raw.Services.Count > 0)
            errors.Add("application.services: service containers are not supported yet (they come with the factory integration, W2)");

        var effective = ContractDefaults.Apply(contract, profile);
        var app = effective.Application;
        var processNames = app.Processes.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        ValidateProcesses(app, errors);
        foreach (var (step, i) in (app.Build ?? []).Select((s, i) => (s, i)))
        {
            if (step.Command.Count == 0)
                errors.Add($"application.build[{i}].command: is empty");
        }

        ValidateReset(app.Reset, errors);
        ValidateTestability(effective.Testability, errors);
        ValidatePlaceholders(effective, processNames, errors);
        return errors;
    }

    private static void ValidateProcesses(AppDefinition app, List<string> errors)
    {
        if (app.Processes.Count == 0)
            errors.Add("application.processes: at least one process is required");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (process, i) in app.Processes.Select((p, i) => (p, i)))
        {
            var at = $"application.processes[{i}]";
            if (!NamePattern().IsMatch(process.Name))
                errors.Add($"{at}.name: '{process.Name}' must be lower-case letters, digits, '-' or '_'");
            else if (!seen.Add(process.Name))
                errors.Add($"{at}.name: '{process.Name}' is declared twice");
            if (process.Command.Count == 0)
                errors.Add($"{at}.command: is empty and the stack profile has no default serve command");
            if (process.Ready?.Http is { } http && !http.StartsWith('/'))
                errors.Add($"{at}.ready.http: '{http}' must be a path starting with '/'");
            if (process.StartTimeoutSeconds <= 0)
                errors.Add($"{at}.start_timeout_seconds: must be positive");
        }

        if (string.IsNullOrWhiteSpace(app.BaseUrl))
            errors.Add("application.base_url: name the process the browser opens");
        else if (app.Processes.Count > 0 && !seen.Contains(app.BaseUrl))
            errors.Add($"application.base_url: there is no process named '{app.BaseUrl}'");
    }

    private static void ValidateReset(ResetDefinition? reset, List<string> errors)
    {
        switch (reset)
        {
            case null:
                errors.Add("application.reset: a reset is required (use strategy: none for an app that keeps no data)");
                break;
            case { Strategy: ResetStrategy.Command, Command.Count: 0 }:
                errors.Add("application.reset.command: the command strategy needs a command");
                break;
            case { Strategy: ResetStrategy.Endpoint } when reset.Endpoint is null || !EndpointPattern().IsMatch(reset.Endpoint):
                errors.Add("application.reset.endpoint: write it as 'POST /__test/reset'");
                break;
            case { Strategy: ResetStrategy.SqliteSnapshot } when string.IsNullOrWhiteSpace(reset.File):
                errors.Add("application.reset.file: the sqlite-snapshot strategy needs the database file");
                break;
            case { Strategy: ResetStrategy.SqliteSnapshot } when reset.File is { } file && (Path.IsPathRooted(file) || file.Split('/', '\\').Contains("..")):
                errors.Add($"application.reset.file: '{file}' must be a path inside the working copy");
                break;
        }
    }

    private static void ValidateTestability(TestabilityDefinition t, List<string> errors)
    {
        var auth = t.Auth;
        var roles = auth.Roles.Keys.ToHashSet(StringComparer.Ordinal);

        if (auth.Type == AuthType.Form)
        {
            if (string.IsNullOrWhiteSpace(auth.LoginUrl) || !auth.LoginUrl.StartsWith('/'))
                errors.Add("testability.auth.login_url: form login needs the login page's path");
            if (auth.Fields is null || string.IsNullOrWhiteSpace(auth.Fields.User) || string.IsNullOrWhiteSpace(auth.Fields.Password))
                errors.Add("testability.auth.fields: form login needs the accessible labels of the user and password fields");
            if (string.IsNullOrWhiteSpace(auth.Submit))
                errors.Add("testability.auth.submit: form login needs the submit button's accessible name");
            if (auth.SuccessWhen is null || auth.SuccessWhen.IsEmpty)
                errors.Add("testability.auth.success_when: say how a finished sign-in is recognised");
            if (roles.Count == 0)
                errors.Add("testability.auth.roles: form login needs at least one role");
            foreach (var (role, credentials) in auth.Roles)
            {
                if (!NamePattern().IsMatch(role))
                    errors.Add($"testability.auth.roles.{role}: role names must be lower-case letters, digits, '-' or '_'");
                if (string.IsNullOrWhiteSpace(credentials.User) || string.IsNullOrWhiteSpace(credentials.Password))
                    errors.Add($"testability.auth.roles.{role}: needs a user and a password");
            }
        }
        else if (roles.Count > 0)
        {
            errors.Add("testability.auth.roles: roles need a login (auth.type: form)");
        }

        foreach (var route in t.Routes.Public)
            CheckRoute("testability.routes.public", route, errors);
        foreach (var (section, map) in new[] { ("protected", t.Routes.Protected), ("denied", t.Routes.Denied) })
        {
            foreach (var (role, routes) in map)
            {
                if (!roles.Contains(role))
                    errors.Add($"testability.routes.{section}.{role}: there is no role '{role}' in auth.roles");
                foreach (var route in routes)
                    CheckRoute($"testability.routes.{section}.{role}", route, errors);
            }
        }

        foreach (var (name, endpoint) in t.TestEndpoints)
        {
            if (!EndpointPattern().IsMatch(endpoint))
                errors.Add($"testability.test_endpoints.{name}: write it as 'POST /__test/{name}'");
        }

        foreach (var (signal, i) in t.FailOn.Select((s, i) => (s, i)))
        {
            var set = new[] { signal.SelectorVisible, signal.TextVisible, signal.LogPattern }.Count(v => v is not null);
            if (set != 1)
                errors.Add($"testability.fail_on[{i}]: set exactly one of selector_visible, text_visible or log_pattern");
            if (signal.LogPattern is { } pattern && !IsRegex(pattern))
                errors.Add($"testability.fail_on[{i}].log_pattern: '{pattern}' is not a valid regular expression");
        }

        if (t.Ready is { Selector: null, Js: null })
            errors.Add("testability.ready: give a selector or a js expression");
    }

    private static void CheckRoute(string at, string route, List<string> errors)
    {
        if (!route.StartsWith('/') || route.StartsWith("//", StringComparison.Ordinal))
            errors.Add($"{at}: '{route}' must be a path on the app, starting with a single '/'");
    }

    /// <summary>
    /// Every placeholder must be well formed and name something that exists. Secrets are allowed
    /// only where README §10 says they go: app process environments and sign-in credentials.
    /// </summary>
    private static void ValidatePlaceholders(WebAppContract c, HashSet<string> processNames, List<string> errors)
    {
        void Check(string at, string? value, bool secretsAllowed)
        {
            foreach (var p in Placeholders.Find(value))
            {
                if (Placeholders.Problem(p) is { } problem)
                    errors.Add($"{at}: {problem}");
                else if (p.Kind == PlaceholderKind.Port && !processNames.Contains(p.Name!))
                    errors.Add($"{at}: {p.Text} names no process");
                else if (p.Kind == PlaceholderKind.Service)
                    errors.Add($"{at}: {p.Text} needs a service, and services come with W2");
                else if (p.Kind == PlaceholderKind.Secret && !secretsAllowed)
                    errors.Add($"{at}: {p.Text} is not allowed here; secrets go only into process environments and role credentials");
            }
        }

        var app = c.Application;
        foreach (var (step, i) in (app.Build ?? []).Select((s, i) => (s, i)))
        {
            foreach (var arg in step.Command)
                Check($"application.build[{i}].command", arg, secretsAllowed: false);
            foreach (var (name, value) in step.Environment)
                Check($"application.build[{i}].environment.{name}", value, secretsAllowed: false);
        }

        foreach (var (process, i) in app.Processes.Select((p, i) => (p, i)))
        {
            foreach (var arg in process.Command)
                Check($"application.processes[{i}].command", arg, secretsAllowed: false);
            foreach (var (name, value) in process.Environment)
                Check($"application.processes[{i}].environment.{name}", value, secretsAllowed: true);
        }

        if (app.Reset is { } reset)
        {
            foreach (var arg in reset.Command.Concat(reset.Seed))
                Check("application.reset", arg, secretsAllowed: false);
            foreach (var (name, value) in reset.Environment)
                Check($"application.reset.environment.{name}", value, secretsAllowed: false);
        }

        foreach (var (role, credentials) in c.Testability.Auth.Roles)
        {
            Check($"testability.auth.roles.{role}.user", credentials.User, secretsAllowed: true);
            Check($"testability.auth.roles.{role}.password", credentials.Password, secretsAllowed: true);
        }
    }

    /// <summary>Every secret the contract needs, so a caller can say which are missing before a run.</summary>
    public static IReadOnlyList<string> SecretNames(WebAppContract c) =>
    [
        .. c.Application.Processes.SelectMany(p => p.Environment.Values)
            .Concat(c.Testability.Auth.Roles.Values.SelectMany(r => new[] { r.User, r.Password }))
            .SelectMany(Placeholders.Find)
            .Where(p => p.Kind == PlaceholderKind.Secret && p.Name is not null)
            .Select(p => p.Name!)
            .Distinct(StringComparer.Ordinal),
    ];

    private static bool IsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
