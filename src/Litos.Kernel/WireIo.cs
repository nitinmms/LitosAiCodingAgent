using System.Text.Json;

namespace Litos.Kernel;

/// <summary>
/// .NET-side (KernelSession) counterpart to Litos.Kernel.Host's own WireIo — duplicated rather
/// than shared via a project reference, since Litos.Kernel.Host depends on Litos.Kernel (§8.1),
/// not the other way around, and this is a handful of lines wrapping one JSON line per message.
/// </summary>
internal static class WireIo
{
    public static async Task<KernelWireMessage?> ReadAsync(TextReader input, CancellationToken ct)
    {
        var line = await input.ReadLineAsync(ct);
        if (line is null)
            return null;
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
