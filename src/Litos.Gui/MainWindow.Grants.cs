using Avalonia.Controls;
using Litos.Gui.Buzzies;

namespace Litos.Gui;

/// <summary>
/// Creating a Buzzy (Implement_Buzzies.md, Phase 3).
///
/// There is exactly one way to start a creation proposal: the explicit <c>/buzzy</c> command, or
/// "+ Create a Buzzy" for the plain dialog. Nothing is inferred from ordinary chat text. That is a
/// deliberate reversal of the first draft, which guessed from a sentence like "make me a Buzzy" —
/// in the plain chat, which is also the *coding* agent, that guess hijacked real requests
/// ("add an assistant to the checkout page"). An explicit command is a rule that can be stated and
/// tested ("a message not starting with /buzzy never creates a Buzzy"); a sentence pattern can only
/// ever be a corpus that happens to pass.
///
/// The other half is that a misfire stays cheap: the proposal is only a card, and dismissing it puts
/// the user's own text back in the input box, so nothing they typed is lost.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Offered for a per-Buzzy model choice: the provider's own list, best-effort. A
    /// failure here is not an error — the card still offers "follow the app's current model".</summary>
    private async Task<IReadOnlyList<string>> OfferedModelsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await _session.ChatProvider.ListModelsAsync(timeout.Token);
            return [.. models.Select(m => m.Id)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary><c>/buzzy &lt;what it should do&gt;</c>. With no description, says what to type rather
    /// than guessing (every other command's no-argument fallback opens a picker; there is nothing to
    /// pick here, since the description <em>is</em> the input).</summary>
    private async Task HandleBuzzyCommandAsync(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AddToolLine("Describe the Buzzy you want after the command, for example: /buzzy read my invoice folder and write a weekly summary. Or pick a template by hand with \"+ Create a Buzzy\" in the sidebar.");
            return;
        }

        await ProposeBuzzyAsync(argument.Trim());
    }

    private async Task ProposeBuzzyAsync(string request)
    {
        if (TurnInFlight)
        {
            AddToolLine("A turn is still running. Wait for it to finish before creating a Buzzy.");
            return;
        }
        if (RejectIfNoModel())
            return;

        ShowWorkingIndicator();
        AddToolLine("Working out what this Buzzy should be…");

        var models = await OfferedModelsAsync();
        BuzzyProposal proposal;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            proposal = await BuzzyProposer.ProposeAsync(request, models, _session.ChatProvider, _session.Model, timeout.Token);
        }
        catch (Exception ex)
        {
            AddToolLine($"Couldn't work that out ({ex.Message}). Try /buzzy again with a shorter description, or use \"+ Create a Buzzy\" to pick a template by hand.");
            return;
        }
        finally
        {
            HideWorkingIndicator();
        }

        var approved = await BuzzyProposalWindow.ShowAsync(this, Buzzies, proposal, models);
        if (approved is null)
        {
            // Nothing was created, and the description the user typed goes back in the box so they
            // can reword it or send it as an ordinary message instead of retyping it.
            InputBox.Text = request;
            AddToolLine("Nothing was created. Your description is back in the input box.");
            return;
        }

        BuzzyProfile profile;
        try
        {
            profile = Buzzies.Create(
                approved.Template, approved.Name, approved.Color, DateTimeOffset.Now,
                approved.Tools, approved.Model, approved.Soul);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddToolLine($"Couldn't create the Buzzy: {ex.Message}");
            return;
        }

        RebuildBuzzySidebar();
        var role = BuzzyTemplates.Find(profile.Template)?.Role ?? profile.Template;
        var model = profile.Model is { } own ? $" on {own}" : "";
        AddToolLine($"Created {profile.Name} ({role}{model}). Right-click it to grant folders or change its model, or just start talking to it.");
        await SelectBuzzyAsync(profile);
    }

    /// <summary>The sidebar's soul, memory and skills editor. The files stay the source of truth;
    /// this is a place to change them that says what each one does.</summary>
    private async Task EditBuzzyContentsAsync(BuzzyProfile profile)
    {
        await BuzzyEditorWindow.ShowAsync(this, Buzzies, Buzzies.Get(profile.Id) ?? profile);
        // Its soul and memory go into the next prompt, so nothing cached needs clearing: the prompt
        // provider reads both files when a turn starts.
    }

    /// <summary>The sidebar's access settings: folder grants, the model this Buzzy runs on, and
    /// (Phase 4) whether it may use the mail connector.</summary>
    private async Task EditBuzzyAccessAsync(BuzzyProfile profile)
    {
        var before = _buzzy?.Id == profile.Id ? _buzzy : null;
        var updated = await BuzzySettingsWindow.ShowAsync(
            this, Buzzies, Buzzies.Get(profile.Id) ?? profile, await OfferedModelsAsync(), Mail);
        if (updated is null)
            return;

        // Keep the copy the window runs with in step, so the next turn and the status bar agree.
        if (before is not null)
        {
            _buzzy = updated;
            UpdateProviderModelText();
        }
        RebuildBuzzySidebar();
        if (updated.Folders.Count > 0)
            AddToolLine($"{updated.Name} can now use {string.Join(", ", updated.Folders.Select(g => g.Display))}.");
        if (updated.Model is { } own)
            AddToolLine($"{updated.Name} runs on {own}.");
        if (updated.Tools.Any(BuzzyToolNames.Mail.Contains))
        {
            var info = Mail.Info();
            AddToolLine(info is null
                ? $"{updated.Name} may use email, but no mailbox is connected yet (the Gmail row in the sidebar)."
                : $"{updated.Name} may use email: {info.Description}.");
        }
    }
}