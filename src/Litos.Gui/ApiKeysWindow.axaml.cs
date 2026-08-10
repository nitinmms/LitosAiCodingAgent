using Avalonia.Controls;
using Litos.Host;

namespace Litos.Gui;

/// <summary>
/// API keys dialog, used two ways:
///  - First run (Program.cs): shown standalone under ApiKeysApp, its own mini Avalonia lifetime,
///    before the real App/MainWindowSession exist (no owner). Save or Cancel both end the process.
///  - "/keys" (MainWindow): shown as a normal owned dialog over the running app, so a key can be
///    added or rotated later without restarting from scratch. Save leaves the app running; the
///    caller re-loads LitosConfig and reports which providers picked up a change.
/// Fields for providers that already have a key (env var or config.json) are pre-marked via
/// Watermark rather than echoing the real secret back into the box — typing a new value overwrites
/// it, leaving it blank leaves the existing key untouched.
/// Persistence is platform-dependent: Windows writes User-scope environment variables (persists
/// across restarts, no admin elevation). Environment.SetEnvironmentVariable's User/Machine targets
/// are Windows-only (PlatformNotSupportedException elsewhere per .NET docs), and there is no macOS
/// equivalent of "persist an env var for future GUI-launched processes" without a LaunchAgent, so
/// non-Windows platforms write straight to config.json (LitosConfig.Save), which Load() already
/// reads as a fallback whenever the matching env var isn't set.
/// </summary>
public sealed partial class ApiKeysWindow : Window
{
    private static readonly (string Provider, string EnvVar, Func<ApiKeysWindow, TextBox> Field)[] Fields =
    [
        ("anthropic", "ANTHROPIC_API_KEY", w => w.AnthropicBox),
        ("openai", "OPENAI_API_KEY", w => w.OpenAiBox),
        ("gemini", "GEMINI_API_KEY", w => w.GeminiBox),
        ("openrouter", "OPENROUTER_API_KEY", w => w.OpenRouterBox),
        ("local", "LOCAL_API_KEY", w => w.LocalApiKeyBox),
        ("tavily", "TAVILY_API_KEY", w => w.TavilyBox),
    ];

    private readonly bool _isFirstRun;

    public bool Saved { get; private set; }

    public ApiKeysWindow()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => TrySave();
    }

    private ApiKeysWindow(bool isFirstRun) : this()
    {
        _isFirstRun = isFirstRun;

        var storageDescription = OperatingSystem.IsWindows()
            ? "as a Windows environment variable for your user account"
            : "to ~/.litos/config.json";

        if (isFirstRun)
        {
            HeadingText.Text = "No API key was found for any provider.";
            SubText.Text = $"Enter at least one key below. Saving writes it {storageDescription} — Litos will then close so you can restart it.";
            CancelButton.Content = "Cancel and Exit";
            SaveButton.Content = "Save and Exit";
        }
        else
        {
            HeadingText.Text = "API Keys";
            SubText.Text = $"Saved keys are written {storageDescription}. Leave a field blank to keep its current value.";
            CancelButton.Content = "Close";
            SaveButton.Content = "Save";

            var config = LitosConfig.Load();
            foreach (var (provider, envVar, field) in Fields)
            {
                field(this).PlaceholderText = LitosConfig.IsSetByEnvironmentVariable(provider)
                    ? $"{envVar} — already set"
                    : config.ApiKeys.ContainsKey(provider)
                        ? "Already set — leave blank to keep"
                        : envVar;
            }

            // Not a secret (unlike every field above), so it isn't masked/write-only — but it
            // still uses the same "current value shown in the placeholder, leave blank to keep"
            // convention as the key fields for consistency within this dialog.
            LocalBaseUrlBox.PlaceholderText = config.LocalBaseUrl is { } url
                ? $"{url} — leave blank to keep"
                : "http://localhost:1234/v1 (can be any host on your network)";
        }
    }

    /// <summary>First-run entry point (Program.cs, no owner window exists yet).</summary>
    public static ApiKeysWindow CreateForFirstRun() => new(isFirstRun: true);

    /// <summary>"/keys" entry point — normal owned modal over the running MainWindow.</summary>
    public static Task<bool> ShowAsync(Window owner)
    {
        var window = new ApiKeysWindow(isFirstRun: false);
        return window.ShowDialog<bool>(owner);
    }

    private void TrySave()
    {
        var entries = Fields
            .Select(f => (f.Provider, f.EnvVar, Value: f.Field(this).Text))
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .ToList();
        var localBaseUrl = LocalBaseUrlBox.Text;
        var hasLocalBaseUrl = !string.IsNullOrWhiteSpace(localBaseUrl);

        if (entries.Count == 0 && !hasLocalBaseUrl)
        {
            ErrorText.IsVisible = true;
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var (_, envVar, value) in entries)
                Environment.SetEnvironmentVariable(envVar, value, EnvironmentVariableTarget.User);
        }

        // LocalBaseUrl always goes to config.json — it isn't a secret, so (unlike the keys
        // above) it has no Windows-user-environment-variable storage path; on non-Windows every
        // key does too.
        if (hasLocalBaseUrl || !OperatingSystem.IsWindows())
        {
            var config = LitosConfig.Load();
            var apiKeys = new Dictionary<string, string>(config.ApiKeys);
            if (!OperatingSystem.IsWindows())
                foreach (var (provider, _, value) in entries)
                    apiKeys[provider] = value!;
            config = config with
            {
                ApiKeys = apiKeys,
                LocalBaseUrl = hasLocalBaseUrl ? localBaseUrl!.Trim() : config.LocalBaseUrl,
            };
            config.Save();
        }

        Saved = true;
        if (_isFirstRun)
            Close();
        else
            Close(true);
    }
}
