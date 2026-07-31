using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Tools.Shell;

namespace Litos.Api.Channels.Telegram;

public sealed record LinkedChat(long ChatId, string SessionId, DateTimeOffset LinkedAt);

/// <summary>
/// Telegram channel state — everything about the bridge except the bot token itself, which
/// follows LitosConfig's own env-var-first-then-file precedent (config.GetApiKey("telegram"),
/// ReadMe_TelegramIntegrationTool.md §8) rather than living here. Persisted separately from
/// LitosConfig because it's Litos.Api's own state, not something a desktop install needs to
/// know about (ReadMe_TelegramIntegrationTool.md §0) — lands under the same /data mount
/// (HeadlessServiceTool.md §5.6) as session transcripts, distinct from ~/.litos/config.json.
///
/// ToolPermissions gates ShellTool/WriteFileTool/EditFileTool for every Telegram-originated turn —
/// deliberately channel-wide, not per-chat: anyone who reached the channel got there through the
/// same pairing gate, so per-chat elevation would be a distinction without a difference for a
/// personal single-user bot. A tool absent from the dictionary defaults to Deny — the same safe
/// default the old AllowedTools allowlist had for anything not listed. Toggled from
/// TelegramSetup.razor's "Tool access" section.
/// </summary>
public sealed record TelegramConfig(
    bool Enabled,
    IReadOnlyDictionary<string, ToolPermission> ToolPermissions,
    IReadOnlyList<LinkedChat> LinkedChats)
{
    public static TelegramConfig Empty { get; } =
        new(Enabled: false, ToolPermissions: new Dictionary<string, ToolPermission>(), LinkedChats: []);

    public ToolPermission PermissionFor(string toolName) =>
        ToolPermissions.GetValueOrDefault(toolName, ToolPermission.Deny);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TelegramConfig Load(string path)
    {
        if (!File.Exists(path))
            return Empty;

        try
        {
            var json = File.ReadAllText(path);
            return TryMigrateFromAllowedTools(json) ?? JsonSerializer.Deserialize<TelegramConfig>(json, JsonOptions) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// One-time upgrade path for telegram.json files written before ToolPermissions replaced the
    /// old binary AllowedTools allowlist. Each previously-allowed tool becomes Ask rather than
    /// Full — preserving "you'll be prompted" instead of silently granting blanket auto-run to an
    /// existing deployment on upgrade — everything else stays Deny, same as before. Returns null
    /// (falls through to normal deserialization) for any file that's already in the new shape.
    /// </summary>
    private static TelegramConfig? TryMigrateFromAllowedTools(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("AllowedTools", out var allowedTools))
            return null;

        var permissions = allowedTools.EnumerateArray()
            .Select(e => e.GetString())
            .OfType<string>()
            .ToDictionary(tool => tool, _ => ToolPermission.Ask);

        var enabled = doc.RootElement.TryGetProperty("Enabled", out var enabledEl) && enabledEl.GetBoolean();
        var linkedChats = doc.RootElement.TryGetProperty("LinkedChats", out var linkedChatsEl)
            ? JsonSerializer.Deserialize<IReadOnlyList<LinkedChat>>(linkedChatsEl.GetRawText(), JsonOptions) ?? []
            : [];

        return new TelegramConfig(enabled, permissions, linkedChats);
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}

/// <summary>
/// Thread-safe in-memory holder for the live TelegramConfig — the single source of truth every
/// gating check, chat lookup, and admin-page edit reads/writes against, backed by
/// TelegramConfig's own Load/Save for persistence at <paramref name="stateFilePath"/>. A plain DI
/// singleton rather than re-reading the file on every access, mirroring how AgentWorker holds its
/// own provider/model settings in memory. The path is constructor-injected (defaulting to
/// {~/.litos or LITOS_STATE_DIR}/telegram.json) rather than hardcoded, the same reasoning
/// JsonlTranscriptStore's own constructor-injectable root follows — keeps tests off the real
/// user profile directory.
/// </summary>
public sealed class TelegramConfigStore
{
    private readonly Lock _lock = new();
    private readonly string _stateFilePath;
    private TelegramConfig _current;

    public TelegramConfigStore(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(DefaultStateDirectory(), "telegram.json");
        _current = TelegramConfig.Load(_stateFilePath);
    }

    private static string DefaultStateDirectory() =>
        Environment.GetEnvironmentVariable("LITOS_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos");

    public TelegramConfig Current
    {
        get { lock (_lock) return _current; }
    }

    public void Update(Func<TelegramConfig, TelegramConfig> mutate)
    {
        lock (_lock)
        {
            _current = mutate(_current);
            _current.Save(_stateFilePath);
        }
    }
}
