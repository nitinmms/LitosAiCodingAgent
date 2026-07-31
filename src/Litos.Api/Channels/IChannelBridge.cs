namespace Litos.Api.Channels;

/// <summary>
/// Shared shape for future chat-platform bridges (Telegram, WhatsApp, Email, Rocket.Chat — see
/// ReadMe_TelegramIntegrationTool.md §5). No implementation exists yet in this pass; this is the
/// stub the design docs assume "lives inside Litos.Api.Channels.*" from day one, so a first bridge
/// slots in without reworking Litos.Api itself.
/// </summary>
public interface IChannelBridge
{
    /// <summary>Used for the channel's SessionOwner value, e.g. "telegram", "whatsapp".</summary>
    string ChannelName { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    Task<PairingHandle> BeginPairingAsync(CancellationToken ct);
}

/// <summary>
/// Whatever a channel's setup page needs to render to complete pairing — a QR image, a plain
/// code, or (for allowlist-based channels like email) nothing at all. Shape intentionally left
/// loose; not exercised by any real bridge in this pass.
/// </summary>
public sealed record PairingHandle(string DisplayText, byte[]? QrPngBytes = null);
