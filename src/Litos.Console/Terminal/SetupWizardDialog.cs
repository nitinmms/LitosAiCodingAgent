using Litos.Host;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// Replaces Rendering/SetupWizard.cs's sequential Spectre TextPrompt.Secret() calls with a
/// single modal Dialog holding one masked TextField per provider, followed by a default-provider
/// picker if more than one key was entered. Runs once, when no API key is configured anywhere.
/// </summary>
public static class SetupWizardDialog
{
    private static readonly (string Provider, string Label)[] Providers =
    [
        ("anthropic", "Anthropic (Claude)"),
        ("openai", "OpenAI (GPT)"),
        ("gemini", "Google Gemini"),
        ("openrouter", "OpenRouter"),
    ];

    public static LitosConfig Run(IApplication app)
    {
        var dialog = new Dialog<Dictionary<string, string>>
        {
            Title = "LitosAiAgent first-run setup",
            Width = Dim.Percent(70),
            Height = Providers.Length * 2 + 5,
        };

        var fields = new Dictionary<string, TextField>();
        for (var i = 0; i < Providers.Length; i++)
        {
            var (provider, label) = Providers[i];
            var lbl = new Label { Text = $"{label}:", X = 0, Y = i * 2 };
            var field = new TextField { X = 0, Y = i * 2 + 1, Width = Dim.Fill(), Secret = true };
            fields[provider] = field;
            dialog.Add(lbl, field);
        }

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
            dialog.Result = apiKeys;
            dialog.RequestStop();
        };
        dialog.AddButton(okButton);

        app.Run(dialog, null);

        if (apiKeys.Count == 0)
            return new LitosConfig("anthropic", null, LastWorkingDirectory: null, apiKeys);

        var defaultProvider = apiKeys.Count == 1
            ? apiKeys.Keys.First()
            : PickerDialog<string>.Pick(app, "Default provider", apiKeys.Keys.ToList(), p => p) ?? apiKeys.Keys.First();

        var config = new LitosConfig(defaultProvider, DefaultModel: null, LastWorkingDirectory: null, apiKeys);
        config.Save();
        return config;
    }
}
