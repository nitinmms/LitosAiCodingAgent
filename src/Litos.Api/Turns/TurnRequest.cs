namespace Litos.Api.Turns;

/// <summary>
/// JSON body shape for a text-only turn — unchanged from before attachment support. A
/// multipart/form-data request bypasses this record entirely (TurnsEndpoints reads
/// HttpRequest.Form directly for that case) since ASP.NET Core Minimal APIs bind at most one
/// body source per route; content-type decides which path a given request takes.
/// </summary>
public sealed record TurnRequest(string Input);
