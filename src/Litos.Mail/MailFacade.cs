using Litos.Agent.Tools;
using Litos.Tools.Attachments;

namespace Litos.Mail;

/// <summary>
/// What a face asks when it wants mail tools (Implement_Buzzies.md, Phase 4): which client is in
/// force, whether anything is connected at all, and the broker built over it. It is the one place
/// that decides between a real Gmail connection and the sample inbox, so every face labels sample
/// data the same way and a Buzzy never carries five tools that can only fail.
///
/// Deliberately free of Avalonia and of any approval UI: the caller supplies
/// <see cref="IMailApproval"/>, so the Gui's approval card and a test's auto-approver are the same
/// shape to the broker.
/// </summary>
public sealed class MailConnection
{
    private readonly MailSettingsStore _settings;
    private readonly MailTokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly Lazy<GmailClient> _gmail;
    private readonly FixtureMailClient _fixture = new();

    public MailConnection(
        MailSettingsStore settings, MailTokenProvider tokens, HttpClient? http = null, TimeProvider? clock = null)
    {
        _settings = settings;
        _tokens = tokens;
        _http = http ?? new HttpClient();
        _gmail = new Lazy<GmailClient>(() => new GmailClient(_http, _tokens.AccessTokenAsync));
        Clock = clock ?? TimeProvider.System;
    }

    /// <summary>The clock the connection's own labels use; here so a window can say "connected at"
    /// without reaching for the system clock separately in a test.</summary>
    public TimeProvider Clock { get; }

    public HttpClient Http => _http;

    public MailSettingsStore Settings => _settings;

    public MailTokenProvider Tokens => _tokens;

    /// <summary>True when a mailbox is usable: a real connection, or the sample inbox switched on.</summary>
    public bool IsConnected
    {
        get
        {
            var saved = _settings.Load();
            return saved.UseFixtureData || saved.Connection is not null;
        }
    }

    /// <summary>Sample data in force, so a window or a sidebar can say so plainly.</summary>
    public bool UsingFixtureData => _settings.Load().UseFixtureData;

    /// <summary>The client in force. The sample inbox wins whenever it is switched on, so a demo
    /// can never accidentally reach a real mailbox.</summary>
    public IMailClient Client
    {
        get
        {
            var saved = _settings.Load();
            return saved.UseFixtureData || saved.Connection is null ? _fixture : _gmail.Value;
        }
    }

    public string Describe => Client.Describe;

    /// <summary>The broker's tools over the current client, or none when nothing is connected.</summary>
    public IReadOnlyList<ITool> Tools(IMailApproval approval, IAttachmentConverter? converter = null) =>
        IsConnected ? new MailBroker(Client, approval, converter).Tools : [];

    /// <summary>The tools whose work is read-only: what an unattended routine may have. Downloading
    /// an attachment and saving a draft each need a person's approval, so routines get neither.</summary>
    public IReadOnlyList<ITool> ReadOnlyTools(IAttachmentConverter? converter = null) =>
        IsConnected
            ? [.. new MailBroker(Client, approval: null, converter).Tools.Where(t => MailToolNames.ReadOnly.Contains(t.Name))]
            : [];

    /// <summary>The current connection as the settings window shows it, or null when there is none.</summary>
    public MailConnectionInfo? Info()
    {
        var saved = _settings.Load();
        if (saved.UseFixtureData)
            return new MailConnectionInfo("Sample data", "the sample inbox (fixture data, not a real mailbox)", CanDraft: true);
        if (saved.Connection is not { } connection)
            return null;
        return new MailConnectionInfo(connection.Address, connection.Describe, connection.CanDraft);
    }
}

/// <summary>A connection as a window shows it: who, how, and whether drafting is possible.</summary>
public sealed record MailConnectionInfo(string Address, string Description, bool CanDraft);

/// <summary>The names the broker's tools have, so a Buzzy's allowlist can grant them without the
/// broker being built — and so one list says which of them a routine may use.</summary>
public static class MailToolNames
{
    public const string Search = "mail_search";
    public const string Read = "mail_read";
    public const string Summarise = "mail_summarise";
    public const string DownloadAttachment = "mail_download_attachment";
    public const string Draft = "mail_draft";

    public static IReadOnlyList<string> All { get; } = [Search, Read, Summarise, DownloadAttachment, Draft];

    /// <summary>Reading only: what an unattended run may do with mail.</summary>
    public static IReadOnlySet<string> ReadOnly { get; } = new HashSet<string> { Search, Read, Summarise };
}
