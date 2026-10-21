using System.Drawing;
using System.Windows.Forms;

namespace Litos.SoftwareFactory.Tray.Windows;

/// <summary>What the Settings window was saved with. <see cref="Token"/> is null when the saved one stays.</summary>
internal sealed record SettingsResult(TraySettings Settings, Uri Address, string? Token);

/// <summary>
/// The Settings window (§26.3, as on the proposal page): the factory's address and token, with a
/// connection test; the sound, with a Play button; reminders; staying silent while Windows holds
/// notifications back; starting at sign-in. The token is never shown again once saved.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly (AlertSound Sound, string Text)[] SoundChoices =
        [(AlertSound.Ding, "Ding"), (AlertSound.DingDong, "Ding-dong"), (AlertSound.Beeps, "Meal-ready beeps (as in the web app)"), (AlertSound.None, "None")];

    private static readonly (ReminderInterval Interval, string Text)[] ReminderChoices =
        [(ReminderInterval.Never, "Never"), (ReminderInterval.Every15Minutes, "Every 15 minutes"), (ReminderInterval.Hourly, "Every hour")];

    private readonly TraySettings _settings;
    private readonly Func<Uri, string?> _savedToken;
    private readonly IDesktop _desktop;

    private readonly TextBox _address = new() { Width = 360 };
    private readonly CheckBox _plainHttp = new() { Text = "Allow an unencrypted (http://) connection to another machine", AutoSize = true };
    private readonly TextBox _token = new() { Width = 360, UseSystemPasswordChar = true };
    private readonly LinkLabel _createToken = new() { Text = "Create a token in the web app (Tray app page)", AutoSize = true };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    private readonly ComboBox _sound = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly Button _play = new() { Text = "Play", AutoSize = true };
    private readonly ComboBox _reminder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly CheckBox _silent = new() { Text = "Stay silent while Windows holds notifications back (presenting, full screen)", AutoSize = true };
    private readonly CheckBox _startAtSignIn = new() { Text = "Start when I sign in to Windows", AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(460, 0) };
    private CancellationTokenSource? _testing;

    public SettingsResult? Result { get; private set; }

    public SettingsForm(TraySettings settings, string? suggestedAddress, Func<Uri, string?> savedToken, IDesktop desktop)
    {
        _settings = settings;
        _savedToken = savedToken;
        _desktop = desktop;

        Text = "Litos Factory Tray: Settings";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;

        _address.Text = settings.Address ?? suggestedAddress ?? "";
        _address.PlaceholderText = "https://factory.example.com or http://127.0.0.1:5180";
        _plainHttp.Checked = settings.AllowPlainHttp;
        foreach (var (_, text) in SoundChoices)
            _sound.Items.Add(text);
        _sound.SelectedIndex = Array.FindIndex(SoundChoices, c => c.Sound == settings.Sound);
        foreach (var (_, text) in ReminderChoices)
            _reminder.Items.Add(text);
        _reminder.SelectedIndex = Array.FindIndex(ReminderChoices, c => c.Interval == settings.Reminder);
        _silent.Checked = settings.SilentWhenBusy;
        _startAtSignIn.Checked = desktop.StartsAtSignIn() || (settings.Address is null && settings.StartAtSignIn);
        ShowSavedToken();

        _address.TextChanged += (_, _) => ShowSavedToken();
        _plainHttp.CheckedChanged += (_, _) => ShowSavedToken();
        _createToken.LinkClicked += (_, _) => OpenTokenPage();
        _test.Click += async (_, _) => await TestAsync();
        _play.Click += (_, _) => _desktop.Play(SoundChoices[_sound.SelectedIndex].Sound);

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveIfValid();
        AcceptButton = save;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
        void Add(Control control, int top = 0) => layout.Controls.Add(Pad(control, top));

        Add(Heading("Factory"));
        Add(Caption("Address, as the web app's Tray app page shows it"), 4);
        Add(_address);
        Add(_plainHttp);
        Add(Caption("Access token"), 8);
        Add(_token);
        Add(_createToken);
        Add(Row(_test, _testResult), 4);
        Add(Heading("Alerts"), 16);
        Add(Caption("Sound"), 4);
        Add(Row(_sound, _play));
        Add(Caption("Remind me again while a task waits"), 8);
        Add(_reminder);
        Add(_silent, 8);
        Add(_startAtSignIn);
        Add(_error, 8);
        var buttons = Row(save, cancel);
        buttons.Anchor = AnchorStyles.Right;
        Add(buttons, 12);
        Controls.Add(layout);
    }

    private Uri? AddressNow => FactoryAddress.Parse(_address.Text, _plainHttp.Checked).Address;

    private void ShowSavedToken()
    {
        var saved = AddressNow is { } address && _savedToken(address) is not null;
        _token.PlaceholderText = saved ? "Saved. Paste a new token only to replace it." : "lsf_…";
    }

    private void OpenTokenPage()
    {
        if (AddressNow is { } address)
            _desktop.Open(FactoryAddress.TrayPage(address));
        else
            _error.Text = FactoryAddress.Parse(_address.Text, _plainHttp.Checked).Error;
    }

    /// <summary>The address and the token to use, or why not.</summary>
    private (Uri? Address, string? Token, string? Error) CheckInputs()
    {
        var (address, error) = FactoryAddress.Parse(_address.Text, _plainHttp.Checked);
        if (address is null)
            return (null, null, error);

        var typed = _token.Text.Trim();
        if (typed.Length > 0 && !typed.StartsWith("lsf_", StringComparison.Ordinal))
            return (null, null, "An access token starts with lsf_. Copy it from the web app's Tray app page.");
        var token = typed.Length > 0 ? typed : _savedToken(address);
        return token is null
            ? (null, null, "Paste an access token. Create one in the web app: top bar → Tray app → Create token.")
            : (address, token, null);
    }

    private async Task TestAsync()
    {
        var (address, token, error) = CheckInputs();
        _testResult.ForeColor = Color.Firebrick;
        if (address is null)
        {
            _testResult.Text = error;
            return;
        }

        _testing?.Cancel();
        _testing = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _test.Enabled = false;
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = "Connecting…";
        try
        {
            using var http = FactoryClient.CreateHttp(address, token!);
            var me = await new FactoryClient(http).MeAsync(_testing.Token);
            _testResult.ForeColor = Color.ForestGreen;
            _testResult.Text = $"Connected as {me.Name}.";
        }
        catch (TokenRefusedException refused)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = refused.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = ex is HttpRequestException { StatusCode: null }
                ? $"Can't reach {address.Authority}: {ex.InnerException?.Message ?? ex.Message}"
                : ex is OperationCanceledException ? $"{address.Authority} did not answer." : $"{address.Authority} answered, but not as a Litos factory.";
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private void SaveIfValid()
    {
        var (address, _, error) = CheckInputs();
        if (address is null)
        {
            _error.Text = error;
            return;
        }

        var typed = _token.Text.Trim();
        Result = new SettingsResult(
            _settings with
            {
                Address = address.ToString(),
                AllowPlainHttp = _plainHttp.Checked,
                Sound = SoundChoices[_sound.SelectedIndex].Sound,
                Reminder = ReminderChoices[_reminder.SelectedIndex].Interval,
                SilentWhenBusy = _silent.Checked,
                StartAtSignIn = _startAtSignIn.Checked,
            },
            address,
            typed.Length > 0 ? typed : null);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testing?.Cancel();
        base.OnFormClosed(e);
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Control Pad(Control control, int top)
    {
        control.Margin = new Padding(0, top, 0, 2);
        return control;
    }
}
