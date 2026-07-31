namespace Litos.Api.Turns;

/// <summary>
/// Text-only for now. Attachments' wire format is explicitly deferred
/// (ReadMe_HeadlessServiceTool.md §7.3) — not resolved by this DTO.
/// </summary>
public sealed record TurnRequest(string Input);
