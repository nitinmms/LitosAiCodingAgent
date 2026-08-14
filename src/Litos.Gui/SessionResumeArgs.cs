namespace Litos.Gui;

/// <summary>
/// Command-line handoff from an old Litos.Gui process to the one a self-update relaunches, so the
/// new process resumes the same session/directory instead of starting fresh (SelfUpdater passes
/// ToArgv()'s output to the relaunched process; Program.Main calls Parse on its own args). Pure
/// string[] in/out — no Avalonia, no filesystem — so it's testable with plain arrays, same as every
/// other pure decision in this project (ResolveStartupWorkingDirectory, ResolveResumeWorkingDirectory).
/// Deliberately not persisted anywhere (no new LitosConfig field): the session id is already durable
/// as the transcript's own .jsonl filename (JsonlTranscriptStore appends per-message, not only on
/// exit), so passing it via argv for one relaunch is all a resume across an update needs.
/// </summary>
public sealed record SessionResumeArgs(string SessionId, string WorkingDirectory)
{
    private const string SessionFlag = "--resume-session";
    private const string DirectoryFlag = "--resume-dir";

    public string[] ToArgv() => [SessionFlag, SessionId, DirectoryFlag, WorkingDirectory];

    public static SessionResumeArgs? Parse(string[] args)
    {
        string? sessionId = null;
        string? workingDirectory = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == SessionFlag)
                sessionId = args[i + 1];
            else if (args[i] == DirectoryFlag)
                workingDirectory = args[i + 1];
        }

        return sessionId is not null && workingDirectory is not null
            ? new SessionResumeArgs(sessionId, workingDirectory)
            : null;
    }
}
