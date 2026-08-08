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
/// Always writes to User-scope environment variables (persists across restarts, no admin
/// elevation), never to config.json, per how this dialog was specified.
/// </summary>
public sealed partial class ApiKeysWindow : Window
{
    private static readonly (string Provider, string EnvVar, Func<ApiKeysWindow, TextBox> Field)[] Fields =
    [
        ("anthropic", "ANTHROPIC_API_KEY", w => w.AnthropicBox),
        ("openai", "OPENAI_API_KEY", w => w.OpenAiBox),
        ("gemini", "GEMINI_API_KEY", w => w.GeminiBox),
        ("openrouter", "OPENROUTER_API_KEY", w => w.OpenRouterBox),
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

        if (isFirstRun)
        {
            HeadingText.Text = "No API key was found for any provider.";
            SubText.Text = "Enter at least one key below. Saving writes it as a Windows environment variable for your user account — Litos will then close so you can restart it.";
            CancelButton.Content = "Cancel and Exit";
            SaveButton.Content = "Save and Exit";
        }
        else
        {
            HeadingText.Text = "API Keys";
            SubText.Text = "Saved keys are written as Windows environment variables for your user account. Leave a field blank to keep its current value.";
            CancelButton.Content = "Close";
            SaveButton.Content = "Save";

            foreach (var (provider, envVar, field) in Fields)
            {
                field(this).PlaceholderText = LitosConfig.IsSetByEnvironmentVariable(provider)
                    ? $"{envVar} — already set"
                    : envVar;
            }
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
            .Select(f => (f.EnvVar, Value: f.Field(this).Text))
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .ToList();

        if (entries.Count == 0)
        {
            ErrorText.IsVisible = true;
            return;
        }

        foreach (var (envVar, value) in entries)
            Environment.SetEnvironmentVariable(envVar, value, EnvironmentVariableTarget.User);

        Saved = true;
        if (_isFirstRun)
            Close();
        else
            Close(true);
    }
}
