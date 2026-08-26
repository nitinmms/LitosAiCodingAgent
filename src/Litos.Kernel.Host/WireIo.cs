using System.Text.Json;
using Litos.Kernel;

namespace Litos.Kernel.Host;

/// <summary>
/// Reads/writes one KernelWireMessage per line. Kept separate from RunLoop/ScriptSession so the
/// "one JSON object per line over stdio" framing (§7's wire-protocol decision) has exactly one
/// place that knows about newline-delimited framing at all.
/// </summary>
internal static class WireIo
{
    public static async Task<KernelWireMessage?> ReadAsync(TextReader input, CancellationToken ct)
    {
        var line = await input.ReadLineAsync(ct);
        if (line is null)
            return null; // EOF: the parent's stdin pipe closed, i.e. the parent process is gone.
        if (string.IsNullOrWhiteSpace(line))
            return await ReadAsync(input, ct);
        return JsonSerializer.Deserialize(line, KernelProtocolJsonContext.Default.KernelWireMessage);
    }

    public static async Task WriteAsync(TextWriter output, KernelWireMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, KernelProtocolJsonContext.Default.KernelWireMessage);
        await output.WriteLineAsync(json.AsMemory(), ct);
        await output.FlushAsync(ct);
    }
}
