using System.Diagnostics;

namespace Litos.Console;

/// <summary>
/// Reads a raw bitmap directly from the OS clipboard, bypassing Terminal.Gui's own IClipboard
/// entirely — its whole surface is string-typed (GetClipboardData/SetClipboardData/
/// TryGetClipboardData all take/return string), with no image/bitmap/DIB member anywhere in the
/// assembly, and no terminal emulator delivers a raw bitmap as a Ctrl+V input event; paste is
/// always intercepted by the terminal and only text is ever forwarded to the running process.
/// This is universal, not a Terminal.Gui limitation — confirmed by researching how Claude Code (a
/// directly comparable terminal agent) handles the same problem: native-Windows-terminal raw-
/// bitmap paste is a known open issue, and on Linux it shells out to xclip/wl-paste rather than
/// using any terminal-level clipboard API, which is the precedent this class follows for Linux.
///
/// Each platform path returns null (not throws) for "no image on the clipboard right now" —
/// Composer's Ctrl+V handler falls back to Terminal.Gui's normal string paste whenever this
/// returns null, so text paste is never regressed on any platform.
/// </summary>
public static class ClipboardImageReader
{
    /// <summary>Attempts to read a PNG-encoded image from the OS clipboard for the current platform. Never throws.</summary>
    public static async Task<byte[]?> TryReadPngAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Win32Clipboard.TryReadDibAsPng();
            if (OperatingSystem.IsMacOS())
                return await TryReadMacAsync(ct);
            if (OperatingSystem.IsLinux())
                return await TryReadLinuxAsync(ct);
        }
        catch
        {
            // Any unexpected failure (permission, malformed clipboard data, process launch
            // failure) degrades to "no image found" — Ctrl+V's text-paste fallback always still
            // works, so a clipboard-image error should never surface as a hard failure.
            return null;
        }

        return null;
    }

    /// <summary>
    /// osascript -e 'the clipboard as «class PNGf»' writes raw PNG bytes to stdout when the
    /// clipboard holds image data; a non-zero exit or empty output means no image is present
    /// (never an error to surface — same "just fall through" contract as every other platform).
    /// </summary>
    private static async Task<byte[]?> TryReadMacAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("osascript", "-e \"the clipboard as «class PNGf»\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        var stdout = await process.StandardOutput.BaseStream.ReadAllBytesAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 && stdout.Length > 0 ? stdout : null;
    }

    /// <summary>
    /// Tries wl-paste first (Wayland, gated on $WAYLAND_DISPLAY actually being set — running
    /// wl-paste under X11 or a bare TTY just hangs/errors), falling back to xclip (X11). Neither
    /// binary present is treated as "no image on clipboard" and falls through to text paste, but
    /// callers should surface a one-line hint the first time this happens (see
    /// LinuxClipboardToolMissingHint) rather than staying silent about the missing dependency —
    /// per Claude Code issue #29204, silently falling through with no explanation is itself a
    /// known rough edge worth avoiding.
    /// </summary>
    private static async Task<byte[]?> TryReadLinuxAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && IsToolAvailable("wl-paste"))
        {
            var wlResult = await RunAndCaptureAsync("wl-paste", "-t image/png", ct);
            if (wlResult is { Length: > 0 })
                return wlResult;
        }

        if (IsToolAvailable("xclip"))
        {
            var targets = await RunAndCaptureAsync("xclip", "-selection clipboard -t TARGETS -o", ct);
            var targetList = targets is null ? "" : System.Text.Encoding.UTF8.GetString(targets);
            if (System.Text.RegularExpressions.Regex.IsMatch(targetList, "image/(png|jpeg|jpg|gif|webp|bmp)"))
                return await RunAndCaptureAsync("xclip", "-selection clipboard -t image/png -o", ct);

            return null;
        }

        NoLinuxClipboardToolFound = true;
        return null;
    }

    /// <summary>
    /// Set the first time a Linux paste attempt finds neither wl-paste nor xclip available —
    /// Composer surfaces a one-line hint on this exact transition (false -> true) rather than
    /// every subsequent paste, matching "surface it once" rather than nagging on every Ctrl+V.
    /// </summary>
    public static bool NoLinuxClipboardToolFound { get; private set; }

    private static bool IsToolAvailable(string toolName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("which", toolName) { RedirectStandardOutput = true, UseShellExecute = false });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]?> RunAndCaptureAsync(string fileName, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        var stdout = await process.StandardOutput.BaseStream.ReadAllBytesAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 && stdout.Length > 0 ? stdout : null;
    }

    private static async Task<byte[]> ReadAllBytesAsync(this Stream stream, CancellationToken ct)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, ct);
        return memoryStream.ToArray();
    }
}
