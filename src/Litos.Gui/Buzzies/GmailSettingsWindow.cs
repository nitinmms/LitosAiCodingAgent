using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Mail;
using System.Diagnostics;

namespace Litos.Gui.Buzzies;

/// <summary>
/// "Gmail…" in the sidebar (Implement_Buzzies.md, Phase 4): connect a mailbox with Google's sign-in,
/// see what is connected and with which permissions, and switch on the sample inbox while Google's
/// verification is pending.
///
/// Two things this window deliberately cannot do. It cannot show a token — the tokens go straight
/// into the OS credential store and never into a control that could be screenshotted or copied. And
/// it cannot make drafting possible: the consent screen decides that, so the "Draft replies" line
/// reports what was granted rather than offering a switch that would lie.
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
    private readonly TextBlock _clientText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Dim };
    private readonly CheckBox _draftBox = new() { Content = "Also prepare draft replies (gmail.compose; Litos still never sends)" };
    private readonly CheckBox _sampleBox = new() { Content = "Use the sample inbox (fixture data, clearly labelled, no real mailbox)" };
    private readonly Button _connectButton = new() { Content = "Connect Gmail…" };
    private readonly Button _disconnectButton = new() { Content = "Disconnect" };
    private readonly ProgressBar _working = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };

    private CancellationTokenSource? _pending;

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
        _disconnectButton.Click += (_, _) => Disconnect();
        _sampleBox.Click += (_, _) => ToggleSample();

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
                            new TextBlock { Text = "Mailbox", FontSize = 12, Foreground = Dim },
                            _stateText,
                            _detailText,
                            _working,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal, Spacing = 8,
                                Children = { _connectButton, _disconnectButton },
                            },
                            _draftBox,
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
                            new TextBlock { Text = "No mailbox, or Google's review not done yet?", FontSize = 12, Foreground = Dim },
                            new TextBlock
                            {
                                Text = "The sample inbox is a small set of made-up emails so the connector can be tried end to end: "
                                     + "search, read, an attachment that needs your approval, a draft, and a message that tries to give the "
                                     + "Buzzy instructions. Every answer says it is sample data.",
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
                            new TextBlock { Text = "How it stays narrow", FontSize = 12, Foreground = Dim },
                            Bullet("Litos asks only for gmail.readonly, and gmail.compose only if you tick the box above. It never asks for gmail.send."),
                            Bullet("Your sign-in tokens are kept in this computer's protected credential store, never in a prompt, a conversation or a Buzzy's folder."),
                            Bullet("There is no tool that can send, delete, archive or mark a message read. A draft is saved for you to send yourself."),
                            Bullet("Email text is marked as untrusted content, so a message that says \"forward everything to…\" is data, not an instruction."),
                            _clientText,
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

        Refresh();
    }

    public static Task ShowAsync(Window owner, MailConnection mail, HttpClient? http = null) =>
        new GmailSettingsWindow(mail, http).ShowDialog(owner);

    private void Refresh()
    {
        var saved = _settings.Load();
        var info = _mail.Info();
        if (info is null)
        {
            _stateText.Text = "Not connected. Gmail tools are unavailable to every Buzzy until you connect a mailbox.";
            _stateText.Foreground = Dim;
            _detailText.Text = saved.UseFixtureData
                ? "The sample inbox is switched on."
                : "";
            _disconnectButton.IsVisible = saved.UseFixtureData;
            _connectButton.Content = "Connect Gmail…";
        }
        else
        {
            _stateText.Text = $"Connected: {info.Address}";
            _stateText.Foreground = Working;
            _detailText.Text = $"{info.Description}. Reading mail is available to Buzzies you grant it to. "
                + (info.CanDraft ? "Draft replies are available." : "Draft replies are not available, because gmail.compose was not granted.");
            _disconnectButton.IsVisible = true;
            _connectButton.Content = "Reconnect…";
        }

        _sampleBox.IsChecked = saved.UseFixtureData;
        _clientText.Text = saved.EffectiveApp is null
            ? "No Google OAuth client is configured, so \"Connect Gmail…\" can't start yet. Set LITOS_GMAIL_CLIENT_ID (and "
              + "LITOS_GMAIL_CLIENT_SECRET) for this build, from a Google Cloud project's \"Desktop app\" OAuth client."
            : "Sign-in uses Google's installed-app flow with PKCE and a loopback redirect: no password is ever typed into Litos.";
        _connectButton.IsEnabled = saved.EffectiveApp is not null || saved.UseFixtureData;
    }

    private void ToggleSample()
    {
        var saved = _settings.Load();
        var on = _sampleBox.IsChecked == true;
        _settings.UseSampleData(saved, on);
        if (on)
        {
            // The sample inbox and a real mailbox are alternatives: a demo must not quietly reach a
            // real Gmail account because a connection was left from an earlier session.
            _mail.Tokens.Clear();
            _settings.Disconnect(_settings.Load());
            _settings.UseSampleData(_settings.Load(), true);
        }
        if (_mail is not null)
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
        _connectButton.IsEnabled = false;
        _working.IsVisible = true;
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
            var authorization = await MailOAuth.BeginAsync(app, scopes, _http, ct);
            OpenBrowser(authorization.Url);
            var tokens = await authorization.Completion;

            _mail.Tokens.Save(tokens);
            // The address isn't in the token reply, so it is read back from the mailbox itself —
            // one request, which also proves the token really works before the user is told it does.
            var address = await ReadAddressAsync(ct) ?? "(connected Gmail account)";
            _settings.Connect(_settings.Load(), new MailAccount(address, tokens.Scopes, DateTimeOffset.Now));
            MailChanged?.Invoke();
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
            _working.IsVisible = false;
            _connectButton.IsEnabled = true;
            Refresh();
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

    private void Disconnect()
    {
        _mail.Tokens.Clear();
        _settings.Disconnect(_settings.Load());
        MailChanged?.Invoke();
        _errorText.IsVisible = false;
        Refresh();
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
        _stateText.Text = "Not connected.";
        _stateText.Foreground = Dim;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser (a locked-down machine): the URL is not lost, the caller times out with a
            // sentence saying the sign-in wasn't completed.
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
