using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Litos.Gui;

/// <summary>
/// Assigns this process to a Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so every
/// MCP stdio child process (spawned indirectly via ModelContextProtocol.Client.StdioClientTransport,
/// which on Windows launches "cmd.exe /c &lt;command&gt;" — see McpServerConnection.BuildStdioTransport)
/// is force-killed by the OS the instant this process ends, for ANY reason — graceful exit, crash,
/// Task Manager "End Task", taskkill — not just the cooperative Window.Closing path. Job membership
/// propagates automatically to every descendant Process.Start spawns afterward; nothing per-connection
/// needs to track a PID, and no cooperation from the child process is required.
///
/// This is the actual fix for the "orphaned semble.exe survives Litos.Gui closing" bug: the previous
/// Window.Closing handler could only ever be a best-effort cooperative cleanup (see its own remarks)
/// because it depends on this process staying alive long enough to run it — a hard kill skips it
/// entirely. A Job Object closes that gap at the OS level instead of racing a timeout against it.
///
/// Windows-only: there is no equivalent single-syscall OS primitive on macOS (confirmed via
/// investigation — process groups don't propagate a parent's SIGKILL to children, and the only
/// mechanisms that do survive a SIGKILL'd parent require a separate long-lived helper process/IPC,
/// out of scope here). Call sites must guard with OperatingSystem.IsWindows(); on other platforms
/// this class is simply not called, and the previous best-effort Closing-based cleanup remains
/// whatever protection exists there.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32JobObject
{
    /// <summary>
    /// Creates an unnamed job object, sets KILL_ON_JOB_CLOSE, and assigns the current process to it.
    /// Intentionally leaks the job handle for the lifetime of the process (never closed) — the whole
    /// point is for it to close only when Windows tears down this process, which is what triggers the
    /// kill. Safe to call once at startup; does nothing observable if it silently fails (see remarks
    /// on AssignProcessToJobObject below) beyond leaving orphan-proofing unavailable, same as before.
    /// </summary>
    public static void AssignCurrentProcessWithKillOnClose()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return;

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        };
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info,
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var extendedInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, extendedInfoPtr, (uint)length))
                return;

            // Deliberately not checked for failure beyond this point (e.g. this process is already in
            // another job that disallows nesting — possible under certain sandboxes/CI runners): if
            // assignment fails, MCP children just keep today's best-effort cleanup, no worse off.
            AssignProcessToJobObject(job, System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
