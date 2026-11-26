using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Gui.Buzzies;
using Litos.Mail;

namespace Litos.Gui;

/// <summary>
/// The Gmail connector (Implement_Buzzies.md, Phase 4). One connection per machine, in
/// <c>~/.litos/gmail.json</c> with its tokens in the OS credential store; every Buzzy that is granted
/// mail tools uses it, and a Buzzy that isn't granted them can't see it at all.
///
/// Connecting is a deliberate act in a window of its own — never something a chat turn or a routine
/// can do — because it hands a model access to a real mailbox. Disconnecting is equally deliberate
/// and deletes the stored token, so "disconnect" means the tools stop working immediately rather
/// than at some later expiry.
/// </summary>
public sealed partial class MainWindow
{
    private MailConnection? _mail;
    private HttpClient? _mailHttp;

    /// <summary>Built once, on first use: the settings store, the protected token store and the
    /// HTTP client all live for the whole window, so a token refreshed for one turn is reused by the
    /// next rather than being fetched again.</summary>
    private MailConnection Mail
    {
        get
        {
            if (_mail is null)
            {
                _mailHttp = MailHttpClient();
                var settings = new MailSettingsStore();
                var tokens = new MailTokenProvider(CredentialStores.ForThisMachine(), settings, _mailHttp);
                _mail = new MailConnection(settings, tokens, _mailHttp);
            }
            return _mail;
        }
    }

    /// <summary>Its own client, not the update checker's: mail traffic is unrelated to update
    /// traffic, and one stalled mailbox request must not sit behind a GitHub download.</summary>
    private static HttpClient MailHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("Litos.Gui", "1.0"));
        return client;
    }

    private async Task OpenGmailSettingsAsync()
    {
        await GmailSettingsWindow.ShowAsync(this, Mail, _mailHttp);
        RebuildBuzzySidebar();

        var info = Mail.Info();
        AddToolLine(info is null
            ? "Gmail is not connected. Buzzy mail tools are unavailable until you connect a mailbox."
            : $"Gmail: {info.Description}.");
    }

    /// <summary>The subtitle a Buzzy's row shows when it uses mail, so "this one reads my mail" is
    /// visible in the sidebar instead of only inside its settings.</summary>
    private string? MailLabelFor(BuzzyProfile profile)
    {
        if (!profile.Tools.Any(BuzzyToolNames.Mail.Contains))
            return null;
        var info = Mail.Info();
        if (info is null)
            return "mail granted, not connected";
        return Mail.UsingFixtureData ? "sample mail" : "mail";
    }

    /// <summary>The sidebar's connector row: one line saying whether Gmail is connected, because an
    /// unconnected mailbox is why a Buzzy's mail tools are missing.</summary>
    private Control GmailSidebarRow()
    {
        var info = Mail.Info();
        var subtitle = new TextBlock
        {
            Text = info is null ? "Not connected" : info.Description,
            FontSize = 12,
            Foreground = info is null ? DimTextBrush : new SolidColorBrush(Color.Parse("#4EC9B0")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var row = SidebarRow(GmailIcon(), "Gmail", subtitle);
        row.ContextMenu = new ContextMenu
        {
            Items =
            {
                MenuAction("Gmail settings…", () => _ = OpenGmailSettingsAsync()),
                MenuAction(Mail.UsingFixtureData ? "Switch off the sample inbox" : "Use the sample inbox", ToggleSampleInbox),
            },
        };
        ToolTip.SetTip(row, info is null
            ? "Connect a mailbox to give a Buzzy the mail tools. Click for settings."
            : $"{info.Description}. Click for settings.");
        row.Click += async (_, _) => await OpenGmailSettingsAsync();
        return row;
    }

    private static MenuItem MenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ToggleSampleInbox()
    {
        var settings = Mail.Settings;
        var saved = settings.Load();
        settings.UseSampleData(saved, !saved.UseFixtureData);
        RebuildBuzzySidebar();
        AddToolLine(settings.Load().UseFixtureData
            ? "The sample inbox is on: mail tools answer from sample data, clearly labelled, not a real mailbox."
            : "The sample inbox is off. Mail tools need a connected Gmail account.");
    }

    private static Avalonia.Controls.Shapes.Path GmailIcon() => new()
    {
        Data = Geometry.Parse("M3 6.5h18v11H3zM3 6.5l9 7 9-7"),
        Stroke = new SolidColorBrush(Color.Parse("#EA4335")),
        StrokeThickness = 1.6,
        Width = 22,
        Height = 22,
        Stretch = Stretch.Uniform,
    };
}
