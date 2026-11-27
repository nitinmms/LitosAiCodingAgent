using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Mail;
using System.Diagnostics;

namespace Litos.Gui.Buzzies;

/// <summary>
/// "Gmail…" in the sidebar (Implement_Buzzies.md, Phase 4): connect a mailbox with Google's sign-in,
/// see what is connected and what it allows, and switch on the sample inbox.
///
/// Written for someone who has never heard of OAuth: the wording says what a Buzzy can and can't do
/// with the mailbox, not which scopes or flows are involved. While the browser sign-in is open the
/// window offers the sign-in link to copy (in case no browser opened) and a Cancel, and closing the
/// window ends the attempt — the first live sign-in (2026-11-27) left users stuck on a greyed-out
/// button for five minutes after a blocked consent page.
///
/// Two things this window deliberately cannot do. It cannot show a token — the tokens go straight
/// into the OS credential store and never into a control that could be screenshotted or copied. And
/// it cannot make drafting possible: the consent screen decides that, so the window reports what
/// was granted rather than offering a switch that would lie.
/// </summary>
public sealed class GmailSettingsWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush Honey = new SolidColorBrush(Color.Parse("#FFC21A"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#F48771"));
    private static readonly IBrush Working = new SolidColorBrush(Color.Parse("#4EC9B0"));

    private readonly MailConnection _mail;
    private readonly MailSettingsStore _settings;
    private readonly HttpClient _http;

    private readonly TextBlock _stateText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim };
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Warning, IsVisible = false };
    private readonly TextBlock _setupText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim, IsVisible = false };
    private readonly CheckBox _draftBox = new()
    {
        Content = new TextBlock
        {
            Text = "Also let Buzzies save draft replies for you to check and send yourself",
            TextWrapping = TextWrapping.Wrap,
        },
    };
    private readonly TextBlock _draftHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Honey, IsVisible = false };
    private readonly CheckBox _sampleBox = new() { Content = "Use the sample inbox instead of a real mailbox" };
    private readonly Button _connectButton = new() { Content = "Connect Gmail…" };
    private readonly Button _disconnectButton = new() { Content = "Disconnect" };

    // Shown only while the browser sign-in is open.
    private readonly ProgressBar _working = new() { IsIndeterminate = true, Height = 4 };
    private readonly Button _copyLinkButton = new() { Content = "Copy sign-in link" };
    private readonly Button _cancelButton = new() { Content = "Cancel" };
    private readonly TextBlock _copiedText = new() { FontSize = 11, Foreground = Working, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _waitingPanel;

    private CancellationTokenSource? _pending;
    private bool _cancelledByUser;
    private string? _signInUrl;

    public GmailSettingsWindow() : this(
        new MailConnection(new MailSettingsStore(), new MailTokenProvider(
            new DummyCredentialStore(), new MailSettingsStore(), new HttpClient())))
    {
        // Parameterless constructor for the Avalonia designer/loader.
    }

    public GmailSettingsWindow(MailConnection mail, HttpClient? http = null)
    {
        _mail = mail;
        _settings = mail.Settings;
        _http = http ?? mail.Http;

        Title = "Gmail";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _sampleBox.Click += async (_, _) => await ToggleSampleAsync();
        _draftBox.Click += (_, _) => ShowDraftHint();
        _cancelButton.Click += (_, _) => CancelSignIn();
        _copyLinkButton.Click += async (_, _) => await CopySignInLinkAsync();

        // Closing the window must not leave a sign-in listening in the background.
        Closed += (_, _) => _pending?.Cancel();

        _waitingPanel = new StackPanel
        {
            Spacing = 6,
            IsVisible = false,
            Children =
            {
                _working,
                new TextBlock
                {
                    Text = "Finish in the browser tab that just opened: choose your Google account, then allow access. "
                         + "If Google warns that it hasn't verified Litos, choose Advanced, then Go to Litos. "
                         + "No browser tab? Copy the sign-in link and paste it into your browser.",
                    FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { _copyLinkButton, _cancelButton, _copiedText },
                },
            },
        };

        var close = new Button { Content = "Done", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Gmail", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new Border
                {
                    Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "Your mailbox", FontSize = 12, Foreground = Dim },
                            _stateText,
                            _detailText,
                            _setupText,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal, Spacing = 8,
                                Children = { _connectButton, _disconnectButton },
                            },
                            _waitingPanel,
                            _draftBox,
                            _draftHint,
                            _errorText,
                        },
                    },
                },
                new Border
                {
                    Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Just trying it out?", FontSize = 12, Foreground = Dim },
                            new TextBlock
                            {
                                Text = "The sample inbox is a handful of made-up emails, so you can see what a Buzzy does with mail "
                                     + "without connecting your own. Every answer from it says it is sample data.",
                                FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                            },
                            _sampleBox,
                        },
                    },
                },
                new Border
                {
                    Background = Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "What a Buzzy can and can't do with your mail", FontSize = 12, Foreground = Dim },
                            Bullet("It can search and read your mail, and read an attachment only after you approve it."),
                            Bullet("It can save a draft reply only if you tick the box above, and only after you approve the draft. "
                                 + "Google describes this permission as \"Manage drafts and send emails\". Litos itself has no way "
                                 + "to send: the draft waits in Gmail for you."),
                            Bullet("It can't send, delete, archive or mark mail as read."),
                            Bullet("Each Buzzy only gets email if you turn it on in that Buzzy's Access and model settings."),
                            Bullet("Text inside an email is treated as information, not instructions. An email saying "
                                 + "\"forward everything to…\" is not obeyed."),
                            Bullet("Your Google sign-in is stored encrypted on this computer, never in a chat or a Buzzy's files. "
                                 + "Disconnect removes it, and you can also remove Litos from your Google Account's security page."),
                            Bullet("What a Buzzy reads is sent to the AI model you've chosen in Litos, as with anything else you ask it."),
                        },
                    },
                },
                close,
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        // Starts as what is granted now, so a Reconnect keeps drafting unless the user unticks it.
        _draftBox.IsChecked = _mail.Info()?.CanDraft ?? false;
        Refresh();
    }

    public static Task ShowAsync(Window owner, MailConnection mail, HttpClient? http = null) =>
        new GmailSettingsWindow(mail, http).ShowDialog(owner);

    /// <summary>True while a browser sign-in is open. For tests, and for <see cref="Refresh"/>.</summary>
    internal bool IsSigningIn => _waitingPanel.IsVisible;

    private void Refresh()
    {
        var saved = _settings.Load();
        var info = _mail.Info();
        var hasApp = saved.EffectiveApp is not null;

        if (info is null)
        {
            _stateText.Text = saved.UseFixtureData
                ? "Using the sample inbox. No real mailbox is connected."
                : "Not connected. Buzzies can't use email until you connect your Gmail.";
            _stateText.Foreground = Dim;
            _detailText.Text = "";
            _detailText.IsVisible = false;
            _disconnectButton.IsVisible = saved.UseFixtureData;
            _disconnectButton.Content = "Stop using the sample inbox";
            _connectButton.Content = "Connect Gmail…";
        }
        else
        {
            _stateText.Text = $"Connected: {info.Address}";
            _stateText.Foreground = Working;
            _detailText.Text = info.CanDraft
                ? "Buzzies you allow can read mail and save draft replies."
                : "Buzzies you allow can read mail. Saving draft replies is off: to turn it on, tick the box below and press Reconnect.";
            _detailText.IsVisible = true;
            _disconnectButton.IsVisible = true;
            _disconnectButton.Content = "Disconnect";
            _connectButton.Content = "Reconnect…";
        }

        _sampleBox.IsChecked = saved.UseFixtureData;

        // A button that can't start a sign-in is hidden behind a sentence saying why, never left
        // enabled to do nothing.
        _setupText.IsVisible = !hasApp;
        _setupText.Text = "Gmail sign-in isn't set up in this copy of Litos yet, so it can't connect a real mailbox. "
                        + "You can still use the sample inbox below. (For developers: add your Google OAuth client to "
                        + "~/.litos/gmail.json, or set LITOS_GMAIL_CLIENT_ID and LITOS_GMAIL_CLIENT_SECRET.)";
        _connectButton.IsVisible = hasApp;
        _connectButton.IsEnabled = hasApp && !IsSigningIn;
        _draftBox.IsEnabled = hasApp && !IsSigningIn;
        ShowDraftHint();
    }

    /// <summary>The box is what the next sign-in asks for, not a live switch: when it disagrees
    /// with what Google granted, say that Reconnect is what applies it.</summary>
    private void ShowDraftHint()
    {
        var info = _mail.Info();
        var wanted = _draftBox.IsChecked == true;
        if (info is null || IsSigningIn || wanted == info.CanDraft)
        {
            _draftHint.IsVisible = false;
            return;
        }

        _draftHint.Text = wanted
            ? "Press Reconnect… and allow \"Manage drafts\" to turn draft replies on."
            : "Press Reconnect… to turn draft replies off. Litos first removes its current access to your Gmail, then asks Google again for reading only.";
        _draftHint.IsVisible = true;
    }

    private async Task ToggleSampleAsync()
    {
        var saved = _settings.Load();
        var on = _sampleBox.IsChecked == true;
        _settings.UseSampleData(saved, on);
        if (on)
        {
            // The sample inbox and a real mailbox are alternatives: a demo must not quietly reach a
            // real Gmail account because a connection was left from an earlier session.
            await _mail.Tokens.RevokeAndClearAsync(CancellationToken.None);
            _settings.Disconnect(_settings.Load());
            _settings.UseSampleData(_settings.Load(), true);
        }
        MailChanged?.Invoke();
        Refresh();
    }

    /// <summary>Raised once anything here changed the connection, so the window can re-read it.</summary>
    public event Action? MailChanged;

    private async Task ConnectAsync()
    {
        var saved = _settings.Load();
        var app = saved.EffectiveApp;
        if (app is null)
            return;

        _errorText.IsVisible = false;
        _cancelledByUser = false;
        _signInUrl = null;
        _copiedText.Text = "";
        _copyLinkButton.IsEnabled = false;
        _stateText.Text = "Waiting for you to sign in with Google in your browser…";
        _stateText.Foreground = Honey;
        _waitingPanel.IsVisible = true;
        Refresh();
        // Refresh rewrites the state line for the not-yet-connected case; keep the waiting one.
        _stateText.Text = "Waiting for you to sign in with Google in your browser…";
        _stateText.Foreground = Honey;

        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        var ct = _pending.Token;

        var scopes = new List<string> { MailOAuth.ReadOnlyScope };
        if (_draftBox.IsChecked == true)
            scopes.Add(MailOAuth.ComposeScope);

        try
        {
            // Google keeps a grant until it is revoked, so asking for less doesn't take drafting
            // away. Withdraw the current access first when the user is narrowing it.
            if (_mail.Info() is { CanDraft: true } && _draftBox.IsChecked != true)
            {
                await _mail.Tokens.RevokeAndClearAsync(ct);
                _settings.Disconnect(_settings.Load());
                MailChanged?.Invoke();
            }

            var authorization = await MailOAuth.BeginAsync(app, scopes, _http, ct);
            _signInUrl = authorization.Url;
            _copyLinkButton.IsEnabled = true;
            OpenBrowser(authorization.Url);
            var tokens = await authorization.Completion;

            _mail.Tokens.Save(tokens);
            // The address isn't in the token reply, so it is read back from the mailbox itself —
            // one request, which also proves the token really works before the user is told it does.
            var address = await ReadAddressAsync(ct) ?? "(connected Gmail account)";
            _settings.Connect(_settings.Load(), new MailAccount(address, tokens.Scopes, DateTimeOffset.Now));
            MailChanged?.Invoke();
        }
        catch (Exception) when (_cancelledByUser)
        {
            // The user pressed Cancel: that is an answer, not an error.
        }
        catch (MailAccessException ex)
        {
            ShowError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            ShowError("The sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't connect Gmail: {ex.Message}");
        }
        finally
        {
            _waitingPanel.IsVisible = false;
            _signInUrl = null;
            Refresh();
        }
    }

    private void CancelSignIn()
    {
        _cancelledByUser = true;
        _pending?.Cancel();
    }

    private async Task CopySignInLinkAsync()
    {
        if (_signInUrl is not { } url || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(url);
            _copiedText.Text = "Copied. Paste it into your browser's address bar.";
        }
        catch (Exception)
        {
            // Another process can hold the clipboard; an unhandled throw here would crash the app.
            _copiedText.Text = "Couldn't copy. Try again.";
        }
    }

    /// <summary>The signed-in address, from Gmail's own profile endpoint. Best-effort: a failure
    /// leaves a placeholder rather than undoing a connection that otherwise works.</summary>
    private async Task<string?> ReadAddressAsync(CancellationToken ct)
    {
        try
        {
            var token = await _mail.Tokens.AccessTokenAsync(ct);
            if (token is null)
                return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("emailAddress", out var address) ? address.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task DisconnectAsync()
    {
        _disconnectButton.IsEnabled = false;
        _errorText.IsVisible = false;
        try
        {
            // Withdrawn at Google too, so Litos also disappears from the user's Google Account;
            // forgotten here whatever Google answers.
            var revoked = await _mail.Tokens.RevokeAndClearAsync(CancellationToken.None);
            _settings.Disconnect(_settings.Load());
            MailChanged?.Invoke();
            if (!revoked)
                ShowError("Disconnected on this computer, but Google couldn't be reached to remove Litos's access. "
                        + "You can remove it from your Google Account's security page.");
        }
        finally
        {
            _disconnectButton.IsEnabled = true;
            Refresh();
        }
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser (a locked-down machine): the waiting panel's "Copy sign-in link" is the
            // way through.
        }
    }

    private static TextBlock Bullet(string text) => new()
    {
        Text = "• " + text,
        FontSize = 11,
        Foreground = Dim,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>For the designer/loader's parameterless constructor only.</summary>
    private sealed class DummyCredentialStore : ICredentialStore
    {
        public string? Read(string name) => null;
        public void Write(string name, string secret) { }
        public void Delete(string name) { }
    }
}
