namespace Litos.Tools.Shell;

/// <summary>
/// Per-tool trust level for a gated tool call — mirrors OpenClaw's tools.exec.security modes
/// (deny/ask/full), the closest validated prior art for this exact tri-state
/// (docs.openclaw.ai/tools/exec-approvals). Deny: the gate refuses instantly, before the tool ever
/// runs. Ask: the tool runs only after an out-of-band approval (Telegram inline buttons, or the
/// web Ask-mode approval panel) resolves it. Full: runs immediately, no approval wait at all.
/// Shared by TelegramConfig (per-Telegram-tool permissions) and McpConfig (per-MCP-server/tool
/// permissions) — the identical enum, kept in one place to avoid drift between them.
/// </summary>
public enum ToolPermission
{
    Deny,
    Ask,
    Full,
}
