using Litos.Tools.Shell;

namespace Litos.Mail;

/// <summary>
/// The bridge from <see cref="IMailApproval"/> to the in-memory approval store every Litos face
/// already renders as an Approve/Deny card (the same one MCP's Ask mode and Builder's shell use).
/// A Buzzy's mail tool asks "Fetch invoice-2026-114.pdf from an email so the Buzzy can read it" and
/// the card appears in that conversation, bound to that one request.
///
/// <see cref="PendingApprovalStore"/> lives in Litos.Tools rather than in a face, so this adapter
/// does too — the seam is the store, not Avalonia, and a second face gets the same behaviour by
/// passing the same kind of store.
/// </summary>
public sealed class PendingMailApproval(PendingApprovalStore approvals, string? who = null) : IMailApproval
{
    public async Task<bool> ApproveAsync(string summary, string? detail, CancellationToken ct)
    {
        var preview = new ToolInvocationPreview(
            "mail", who is null ? summary : $"{who} wants to: {summary}", detail);
        var (_, decision) = approvals.AddPending(preview);
        return await decision.WaitAsync(ct) != ApprovalDecision.Deny;
    }
}
