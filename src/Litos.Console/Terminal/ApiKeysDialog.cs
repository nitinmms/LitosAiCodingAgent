using Litos.Host;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// API keys dialog, used two ways — mirroring Litos.Gui's ApiKeysWindow split (copied, not
/// shared, per the no-Gui-changes rule):
///  - First run (Program.cs, <see cref="RunFirstRun"/>): shown before any outer Run loop is
///    active, so it can run on-thread via a plain app.Run(dialog, null) with no threading concern.
///  - "/keys" (<see cref="ShowAsync"/>): shown mid-session while LitosApp's own outer Run loop is
///    already active, so — per AttachDialog's doc comment — it must use the Begin/Iteration/
///    TaskCompletionSource pattern rather than a nested Run.
///
/// Includes two fixes made while generalizing this from the original SetupWizardDialog: the
/// provider list now includes "local" (a local OpenAI-compatible server, gated on a base URL
/// field rather than a key) and "tavily" (web search), previously entirely absent from first-run
/// setup; and saving now loads the current config and applies a `with` expression instead of
/// constructing a fresh LitosConfig, so a pre-existing LocalBaseUrl/ShellCommandTimeoutSeconds is
/// preserved instead of silently reset to null on every save.
/// </summary>
public static class ApiKeysDialog
{
    private static readonly (string Provider, string Label)[] KeyProviders =
    [
        ("anthropic", "Anthropic (Claude)"),
        ("openai", "OpenAI (GPT)"),
        ("gemini", "Google Gemini"),
        ("openrouter", "OpenRouter"),
        ("local", "Local (LM Studio/Ollama/vLLM) API key — optional"),
        ("tavily", "Tavily (web search)"),
    ];

    private const string LocalBaseUrlKey = "__local_base_url__";

    /// <summary>First-run entry point: no outer Run loop is active yet, so a plain on-thread app.Run is safe.</summary>
    public static LitosConfig RunFirstRun(IApplication app)
    {
        var (dialog, fields, localBaseUrlField) = Build(isFirstRun: true);
        app.Run(dialog, null);
        return Save(dialog.Result, fields, localBaseUrlField, app, isFirstRun: true);
    }

    /// <summary>
    /// "/keys" entry point. An outer Run loop is already active (LitosApp), so this follows
    /// AttachDialog's non-blocking Begin/Iteration/TaskCompletionSource pattern rather than a
    /// nested app.Run — see AttachDialog.cs's doc comment for why that's the only safe shape here.
    /// Returns the updated config, or null if the user canceled without saving.
    /// </summary>
    public static Task<LitosConfig?> ShowAsync(IApplication app)
    {
        var (dialog, fields, localBaseUrlField) = Build(isFirstRun: false);
        var tcs = new TaskCompletionSource<LitosConfig?>();

        var token = app.Begin(dialog);
        if (token is null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        void OnIteration(object? sender, EventArgs e)
        {
            if (!dialog.StopRequested)
                return;

            app.Iteration -= OnIteration;
            app.End(token);

            var config = dialog.Result is null ? null : Save(dialog.Result, fields, localBaseUrlField, app, isFirstRun: false);
            tcs.SetResult(config);
        }

        app.Iteration += OnIteration;
        return tcs.Task;
    }

    private static (Dialog<Dictionary<string, string>> Dialog, Dictionary<string, TextField> Fields, TextField LocalBaseUrlField) Build(bool isFirstRun)
    {
        var dialog = new Dialog<Dictionary<string, string>>
        {
            Title = isFirstRun ? "LitosAiAgent first-run setup" : "API Keys",
            Width = Dim.Percent(70),
            Height = (KeyProviders.Length + 1) * 2 + 5,
        };

        // TextField in this Terminal.Gui version has no placeholder/watermark member (confirmed
        // against the installed 2.0.0-rc.64 assembly XML docs — unlike Litos.Gui's Avalonia
        // TextBox.Watermark), so "already set, leave blank to keep" status is shown as a plain
        // dim label to the right of the field's own label instead of inside the field.
        var config = isFirstRun ? null : LitosConfig.Load();
        var fields = new Dictionary<string, TextField>();
        var row = 0;
        for (; row < KeyProviders.Length; row++)
        {
            var (provider, label) = KeyProviders[row];
            var lbl = new Label { Text = $"{label}:", X = 0, Y = row * 2 };
            var field = new TextField { X = 0, Y = row * 2 + 1, Width = Dim.Fill(), Secret = true };
            fields[provider] = field;
            dialog.Add(lbl, field);

            if (config is not null)
            {
                var status = LitosConfig.IsSetByEnvironmentVariable(provider)
                    ? "already set via env var — leave blank to keep"
                    : config.ApiKeys.ContainsKey(provider)
                        ? "already set — leave blank to keep"
                        : null;
                if (status is not null)
                    dialog.Add(new Label { Text = status, X = Pos.Right(lbl) + 1, Y = row * 2 });
            }
        }

        var localBaseUrlLabel = new Label { Text = "Local server base URL (e.g. http://localhost:1234/v1):", X = 0, Y = row * 2 };
        var localBaseUrlField = new TextField { X = 0, Y = row * 2 + 1, Width = Dim.Fill() };
        dialog.Add(localBaseUrlLabel, localBaseUrlField);
        if (config?.LocalBaseUrl is { } existingUrl)
            dialog.Add(new Label { Text = $"currently: {existingUrl} — leave blank to keep", X = Pos.Right(localBaseUrlLabel) + 1, Y = row * 2 });

        var apiKeys = new Dictionary<string, string>();
        var okButton = new Button { Text = "Save", IsDefault = true };
        okButton.Accepting += (_, _) =>
        {
            foreach (var (provider, field) in fields)
            {
                var value = field.Text?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(value))
                    apiKeys[provider] = value;
            }

            var localBaseUrl = localBaseUrlField.Text?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(localBaseUrl))
                apiKeys[LocalBaseUrlKey] = localBaseUrl;

            dialog.Result = apiKeys;
            dialog.RequestStop();
        };
        dialog.AddButton(okButton);

        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Accepting += (_, _) => dialog.RequestStop();
        dialog.AddButton(cancelButton);

        return (dialog, fields, localBaseUrlField);
    }

    private static LitosConfig Save(
        Dictionary<string, string>? result, Dictionary<string, TextField> fields, TextField localBaseUrlField, IApplication app, bool isFirstRun)
    {
        var entered = result ?? [];
        entered.Remove(LocalBaseUrlKey, out var localBaseUrl);

        var currentConfig = LitosConfig.Load();
        var defaultProvider = isFirstRun ? PickDefaultProvider(app, entered) : null;
        var config = MergeConfig(currentConfig, entered, localBaseUrl, defaultProvider);

        config.Save();
        return config;
    }

    /// <summary>
    /// Pure merge of dialog input into the existing config, split out so it's testable without a
    /// Terminal.Gui control tree or real filesystem I/O (same reasoning as McpServersWindow.
    /// BuildDefinition in Litos.Gui). Applies a `with` expression over <paramref name="current"/>
    /// rather than constructing a fresh LitosConfig — the original SetupWizardDialog bug this
    /// fixes: ShellCommandTimeoutSeconds (and, before this dialog also captured it, LocalBaseUrl)
    /// used to be silently dropped back to null on every save because the old code always built a
    /// new record from scratch instead of starting from the config already on disk. A blank/null
    /// enteredLocalBaseUrl keeps whatever LocalBaseUrl current already has, matching every
    /// individual key field's own "blank = keep existing" convention.
    /// </summary>
    internal static LitosConfig MergeConfig(
        LitosConfig current, IReadOnlyDictionary<string, string> enteredApiKeys, string? enteredLocalBaseUrl, string? defaultProvider)
    {
        var apiKeys = new Dictionary<string, string>(current.ApiKeys);
        foreach (var (provider, value) in enteredApiKeys)
            apiKeys[provider] = value;

        return current with
        {
            ApiKeys = apiKeys,
            LocalBaseUrl = !string.IsNullOrEmpty(enteredLocalBaseUrl) ? enteredLocalBaseUrl : current.LocalBaseUrl,
            DefaultProvider = defaultProvider ?? current.DefaultProvider,
        };
    }

    private static string PickDefaultProvider(IApplication app, Dictionary<string, string> apiKeys)
    {
        var chatProviderKeys = apiKeys.Keys.Where(LitosConfig.ChatProviderNames.Contains).ToList();
        if (chatProviderKeys.Count == 0)
            return "anthropic";
        if (chatProviderKeys.Count == 1)
            return chatProviderKeys[0];

        return PickerDialog<string>.Pick(app, "Default provider", chatProviderKeys, p => p) ?? chatProviderKeys[0];
    }
}
