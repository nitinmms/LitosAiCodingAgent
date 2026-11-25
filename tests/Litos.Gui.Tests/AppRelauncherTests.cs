using Litos.Gui;

namespace Litos.Gui.Tests;

public class AppRelauncherTests
{
    [Fact]
    public void QuoteWindowsArguments_QuotesEachArgument_KeepingSpaces() =>
        Assert.Equal("""
            "--resume-session" "abc" "--resume-dir" "C:\My Repo"
            """,
            AppRelauncher.QuoteWindowsArguments(["--resume-session", "abc", "--resume-dir", @"C:\My Repo"]));

    [Fact]
    public void QuoteWindowsArguments_DoublesATrailingBackslash_SoItCannotEscapeTheQuote() =>
        Assert.Equal("\"C:\\\\\"", AppRelauncher.QuoteWindowsArguments([@"C:\"]));

    [Fact]
    public void QuoteWindowsArguments_NoArguments_IsEmpty() =>
        Assert.Equal("", AppRelauncher.QuoteWindowsArguments([]));

    [Fact]
    public void QuotedArguments_ParseBackToTheSameResumeArgs()
    {
        // What the relaunched process's Main receives, after Windows' own command-line splitting.
        var resume = new SessionResumeArgs("abc123", @"C:\Users\O'Brien\repo");
        var commandLine = AppRelauncher.QuoteWindowsArguments(resume.ToArgv());
        var reparsed = SplitLikeWindows(commandLine);

        Assert.Equal(resume, SessionResumeArgs.Parse(reparsed));
    }

    // Minimal CommandLineToArgvW for space-separated, double-quoted arguments with no embedded
    // quotes: backslashes are literal, except a doubled pair right before a closing quote.
    private static string[] SplitLikeWindows(string commandLine) =>
        commandLine[1..^1]
            .Split("\" \"")
            .Select(a => a.EndsWith(@"\\", StringComparison.Ordinal) ? a[..^1] : a)
            .ToArray();
}
