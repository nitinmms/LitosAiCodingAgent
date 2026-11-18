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

    public static IReadOnlyList<string> Validate(WebAppContract contract, StackProfile profile, ServiceCatalog? services = null)
    {
        services ??= ServiceCatalog.Default;
        var errors = new List<string>();
        // Nulls inside lists and maps break every rule below, so they are reported on their own.
        errors.AddRange(NullValues(contract));
        if (errors.Count > 0)
            return errors;

        var raw = contract.Application;

        if (contract.Version != 1)
            errors.Add($"version: {contract.Version} is not supported (only 1)");
        if (raw.Stack is { } stack && stack != profile.Id)
            errors.Add($"application.stack: the contract names '{stack}' but was checked against the profile '{profile.Id}'");

        var effective = ContractDefaults.Apply(contract, profile);
        var app = effective.Application;
        var processNames = app.Processes.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        ValidateProcesses(app, errors);
        foreach (var (step, i) in (app.Build ?? []).Select((s, i) => (s, i)))
        {
            if (step.Command.Count == 0)
                errors.Add($"application.build[{i}].command: is empty");
        }

        var serviceKeys = ValidateServices(app.Services, services, errors);
        ValidateReset(app.Reset, errors);
        ValidateTestability(effective.Testability, errors);
        ValidatePlaceholders(effective, processNames, serviceKeys, errors);
        return errors;
    }

    /// <summary>
    /// Each service: a known name, an allowlisted image with what it needs, and only {generated}
    /// among its values. Returns, per service, the keys {service:&lt;name&gt;:&lt;key&gt;} may use: its
    /// ports (port, &lt;name&gt;_port) and its declared variables.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ValidateServices(IReadOnlyList<ServiceDefinition> declared, ServiceCatalog catalog, List<string> errors)
    {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (service, i) in declared.Select((s, i) => (s, i)))
        {
            var at = $"application.services[{i}]";
            if (!NamePattern().IsMatch(service.Name))
                errors.Add($"{at}.name: '{service.Name}' must be lower-case letters, digits, '-' or '_'");
            else if (keys.ContainsKey(service.Name))
                errors.Add($"{at}.name: '{service.Name}' is declared twice");

            var image = catalog.Find(service.Image);
            if (image is null)
            {
                errors.Add($"{at}.image: '{service.Image}' is not an allowed service image (allowed: {string.Join(", ", catalog.All.Select(c => c.Repository).Order())})");
            }
            else
            {
                foreach (var required in image.RequiredEnvironment.Where(v => !service.Environment.ContainsKey(v)))
                    errors.Add($"{at}.environment: {image.Repository} needs {required}");
            }

            foreach (var (name, value) in service.Environment)
            {
                foreach (var p in Placeholders.Find(value).Where(p => p.Kind != PlaceholderKind.Generated))
                    errors.Add($"{at}.environment.{name}: {p.Text} is not allowed here; a service's values are literal or {{generated}}");
            }

            var names = new HashSet<string>(service.Environment.Keys, StringComparer.Ordinal) { "port" };
            foreach (var port in image?.Ports.Keys ?? [])
                names.Add($"{port}_port");
            keys.TryAdd(service.Name, names);
        }

        return keys;
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
    private static void ValidatePlaceholders(WebAppContract c, HashSet<string> processNames, Dictionary<string, HashSet<string>> serviceKeys, List<string> errors)
    {
        void Check(string at, string? value, bool secretsAllowed)
        {
            foreach (var p in Placeholders.Find(value))
            {
                if (Placeholders.Problem(p) is { } problem)
                    errors.Add($"{at}: {problem}");
                else if (p.Kind == PlaceholderKind.Port && !processNames.Contains(p.Name!))
                    errors.Add($"{at}: {p.Text} names no process");
                else if (p.Kind == PlaceholderKind.Service && !serviceKeys.ContainsKey(p.Name!))
                    errors.Add($"{at}: {p.Text} names no service");
                else if (p.Kind == PlaceholderKind.Service && !serviceKeys[p.Name!].Contains(p.Key!))
                    errors.Add($"{at}: {p.Text}: the service '{p.Name}' has no '{p.Key}' (it has {string.Join(", ", serviceKeys[p.Name!].Order())})");
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

    /// <summary>Maps whose values are data: a null value there is an empty entry, not an absent setting.</summary>
    private static readonly HashSet<string> DataMaps = ["environment", "roles", "testEndpoints", "protected", "denied"];

    private static readonly System.Text.Json.JsonSerializerOptions WithNulls =
        new(ProbeWebJson.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    /// <summary>
    /// A null list item or map value: hand-written JSON can hold one even though the model's
    /// types say it can't, and the engine would fail on it mid-run instead of here.
    /// </summary>
    private static IEnumerable<string> NullValues(WebAppContract contract)
    {
        var errors = new List<string>();

        void Walk(System.Text.Json.Nodes.JsonNode? node, string path, string? key)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is null)
                            errors.Add($"{path}[{i}]: is empty");
                        else
                            Walk(array[i], $"{path}[{i}]", null);
                    }

                    break;
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        var at = path.Length == 0 ? Snake(name) : key is not null && DataMaps.Contains(key) ? $"{path}.{name}" : $"{path}.{Snake(name)}";
                        if (value is null && key is not null && DataMaps.Contains(key))
                            errors.Add($"{at}: is empty");
                        else
                            Walk(value, at, key is not null && DataMaps.Contains(key) ? null : name);
                    }

                    break;
            }
        }

        Walk(System.Text.Json.JsonSerializer.SerializeToNode(contract, WithNulls), "", null);
        return errors;
    }

    private static string Snake(string camel) =>
        string.Concat(camel.Select(c => char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : c.ToString()));

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
