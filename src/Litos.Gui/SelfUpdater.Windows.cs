using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Litos.Gui;

/// <summary>
/// Windows install path: SelfUpdater.CheckForUpdateAsync's shared logic downloads the new exe, then
/// this file replaces the currently-running one and relaunches it. A running .exe can't overwrite
/// its own file (the OS keeps it locked while it's the active image), so the actual file swap has to
/// happen from a second process that outlives this one — a small generated PowerShell script that
/// waits for this process to exit, moves the new exe into place, then starts it back up.
///
/// That helper process is launched with CREATE_BREAKAWAY_FROM_JOB (via a raw CreateProcess P/Invoke
/// — ProcessStartInfo has no way to set this flag) specifically because of Win32JobObject: this
/// process is a member of a job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, and job membership
/// propagates to every child Process.Start spawns. Without breaking away, the instant this process
/// exits (which the helper is waiting for) the OS would tear down the job and kill the helper — and
/// the relaunched Litos.Gui.exe it just started — before either got to do anything. Win32JobObject
/// now also sets JOB_OBJECT_LIMIT_BREAKAWAY_OK, which is what makes this flag usable at all (a job
/// rejects CREATE_BREAKAWAY_FROM_JOB unless it opted in). MCP child processes never request
/// breakaway, so they're unaffected — still killed on close exactly as before.
/// </summary>
public static partial class SelfUpdater
{
    [SupportedOSPlatform("windows")]
    public static async Task InstallAndRelaunchWindowsAsync(HttpClient http, GitHubAsset asset, SessionResumeArgs resume, CancellationToken ct)
    {
        var extractDir = await DownloadAndExtractAsync(http, asset, ct);
        var newExePath = Directory.GetFiles(extractDir, "*.exe").FirstOrDefault()
            ?? throw new InvalidOperationException($"No .exe found in downloaded asset '{asset.Name}'.");

        var targetExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the running executable's path.");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"litos-update-{Guid.NewGuid():n}.ps1");
        File.WriteAllText(scriptPath, BuildRelaunchScript(Environment.ProcessId, newExePath, targetExePath, resume.ToArgv()));

        var arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";
        if (!StartProcessBreakingAwayFromJob("powershell.exe", arguments))
            throw new InvalidOperationException("Failed to launch the update helper process.");

        Environment.Exit(0);
    }

    /// <summary>
    /// Waits for the current process to fully release its file lock on the old exe (a short retry
    /// loop rather than a fixed sleep — MCP shutdown in Window.Closing is fire-and-forget/unawaited,
    /// so exit-to-lock-release timing isn't guaranteed instant), replaces it, relaunches with the
    /// same resume args this update started with, then deletes itself.
    /// </summary>
    internal static string BuildRelaunchScript(int oldPid, string newExePath, string targetExePath, string[] resumeArgv)
    {
        // Every interpolated path/arg goes through the same single-quote doubling PowerShell
        // expects inside a '...' literal — newExePath/targetExePath come from Path.GetTempPath()
        // and Environment.ProcessPath respectively, and an install directory containing an
        // apostrophe (e.g. a Windows profile path like C:\Users\O'Brien\...) would otherwise break
        // the generated script's quoting and silently strand the app mid-update (already exited,
        // never replaced/relaunched).
        var quotedNewExePath = EscapeForSingleQuotedPowerShellString(newExePath);
        var quotedTargetExePath = EscapeForSingleQuotedPowerShellString(targetExePath);
        var quotedResumeArgs = string.Join(", ", resumeArgv.Select(a => $"'{EscapeForSingleQuotedPowerShellString(a)}'"));
        var script = new StringBuilder();
        script.AppendLine($"Wait-Process -Id {oldPid} -Timeout 30 -ErrorAction SilentlyContinue");
        script.AppendLine("for ($i = 0; $i -lt 20; $i++) {");
        script.AppendLine($"    try {{ Move-Item -Path '{quotedNewExePath}' -Destination '{quotedTargetExePath}' -Force; break }}");
        script.AppendLine("    catch { Start-Sleep -Milliseconds 500 }");
        script.AppendLine("}");
        script.AppendLine($"Start-Process -FilePath '{quotedTargetExePath}' -ArgumentList @({quotedResumeArgs})");
        script.AppendLine($"Remove-Item -Path '{MyScriptPathToken}' -Force -ErrorAction SilentlyContinue");
        // $MyInvocation.MyCommand.Path can't be interpolated ahead of time (it only resolves once
        // the script is actually running), so it's substituted last via a token instead of $-escaping.
        return script.ToString().Replace(MyScriptPathToken, "$($MyInvocation.MyCommand.Path)");
    }

    private static string EscapeForSingleQuotedPowerShellString(string value) => value.Replace("'", "''");

    private const string MyScriptPathToken = "__SELF_PATH__";

    /// <summary>
    /// Launches <paramref name="fileName"/> with CREATE_BREAKAWAY_FROM_JOB set, so it (and whatever
    /// it in turn starts) is not a member of this process's kill-on-close job — see this file's own
    /// remarks for why that's required for a self-update relaunch to survive. Returns false if
    /// CreateProcess fails for any reason (caller surfaces this as an update failure, same tone as
    /// every other SelfUpdater failure path).
    /// </summary>
    private static bool StartProcessBreakingAwayFromJob(string fileName, string arguments)
    {
        var commandLine = $"\"{fileName}\" {arguments}";
        var startupInfo = new STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();

        var created = CreateProcess(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW,
            IntPtr.Zero,
            null,
            ref startupInfo,
            out var processInfo);

        if (!created)
            return false;

        CloseHandle(processInfo.hProcess);
        CloseHandle(processInfo.hThread);
        return true;
    }

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
