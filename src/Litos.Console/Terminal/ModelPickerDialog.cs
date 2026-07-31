using Litos.Agent.Providers;
using Terminal.Gui.App;

namespace Litos.Console.Terminal;

/// <summary>Replaces Rendering/ModelPicker.cs's Spectre SelectionPrompt&lt;T&gt; usage.</summary>
public static class ModelPickerDialog
{
    public static string PickProvider(IApplication app, IReadOnlyList<string> providerNames, string current)
    {
        var picked = PickerDialog<string>.Pick(app, "Select a provider", providerNames, name => name,
            initialText: providerNames.Contains(current) ? current : providerNames[0]);
        return picked ?? current;
    }

    public static ModelInfo PickModel(IApplication app, IReadOnlyList<ModelInfo> models)
    {
        var defaultModel = models.FirstOrDefault(m => m.IsDefault) ?? models.First();
        var picked = PickerDialog<ModelInfo>.Pick(app, "Select a model (type to filter)", models, m => m.DisplayName,
            initialText: defaultModel.DisplayName);
        return picked ?? defaultModel;
    }
}
