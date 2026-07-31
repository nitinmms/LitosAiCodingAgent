using System.Security.Cryptography;

namespace Litos.Api.Channels.Telegram;

/// <summary>
/// Pairing-code generation and validation for the QR/deep-link linking flow
/// (ReadMe_TelegramIntegrationTool.md §6.4). A code is single-use and short-lived — issued when
/// an admin clicks "Link a Telegram account" on TelegramSetup.razor, consumed the moment a
/// matching `/start &lt;code&gt;` update arrives (or discarded if it expires unused first), so a
/// leaked/screenshotted QR image can't be replayed later.
///
/// In-memory only, matching PendingApprovalStore's own reasoning: a pairing code is a
/// short-lived, operator-initiated artifact, not something that needs to survive a process
/// restart — if the container restarts mid-pairing, the admin just clicks "Link" again.
/// </summary>
public sealed class TelegramPairing
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private string? _pendingCode;
    private DateTimeOffset _expiresAt;

    /// <summary>Issues a fresh single-use code, invalidating any previously-issued unconsumed one.</summary>
    public string IssueCode()
    {
        var code = RandomNumberGenerator.GetHexString(16, lowercase: true);
        lock (_lock)
        {
            _pendingCode = code;
            _expiresAt = DateTimeOffset.UtcNow + CodeLifetime;
        }
        return code;
    }

    /// <summary>
    /// Consumes <paramref name="presented"/> if it matches the currently-pending, unexpired code
    /// — true at most once per IssueCode() call. Constant-time comparison isn't needed here (this
    /// isn't a secret an attacker can probe remotely without also controlling the Telegram bot's
    /// own update stream), unlike AdminTokenFilter's bearer-token check.
    /// </summary>
    public bool TryConsume(string presented)
    {
        lock (_lock)
        {
            if (_pendingCode is null || DateTimeOffset.UtcNow > _expiresAt)
                return false;

            if (_pendingCode != presented)
                return false;

            _pendingCode = null;
            return true;
        }
    }
}
