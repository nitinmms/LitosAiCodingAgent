using System.Diagnostics;

namespace Litos.Gui;

/// <summary>
/// Starts a fresh copy of Litos.Gui — after saving API keys, which only take effect in a new
/// process (providers are registered once, at DI build time). The counterpart of the VS Code
/// extension's "Reload Window" button. The caller closes this instance once the new one has
/// started.
///
/// Uses the same launch paths as the self-updater's relaunch: on Windows the child must break away
/// from this process's kill-on-close job (Win32JobObject), or it dies the moment this instance
/// exits; on macOS a bundled app is reopened through `open -n` so it starts as a proper app.
/// </summary>
internal static class AppRelauncher
{
    /// <summary>False when the new process could not be started — the caller should then tell
    /// the user to restart by hand rather than closing.</summary>
    public static bool TryRelaunch(SessionResumeArgs? resume)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
            return false;

        var argv = resume?.ToArgv() ?? [];
        try
        {
            if (OperatingSystem.IsWindows())
                return SelfUpdater.StartProcessBreakingAwayFromJob(exePath, QuoteWindowsArguments(argv));

            if (OperatingSystem.IsMacOS() && SelfUpdater.ResolveRunningBundlePath() is { } bundle)
            {
                SelfUpdater.OpenBundle(bundle, argv);
                return true;
            }

            Process.Start(new ProcessStartInfo(exePath, argv) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Joins arguments into one Windows command line, each double-quoted. Paths and
    /// session ids never contain a double quote on Windows, so no further escaping is needed;
    /// a trailing backslash is doubled so it can't escape the closing quote.</summary>
    internal static string QuoteWindowsArguments(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select(a => $"\"{(a.EndsWith('\\') ? a + "\\" : a)}\""));
}
