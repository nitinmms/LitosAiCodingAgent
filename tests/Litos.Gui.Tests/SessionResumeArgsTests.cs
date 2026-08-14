namespace Litos.Gui.Tests;

/// <summary>
/// Covers SessionResumeArgs.Parse/ToArgv — the command-line handoff SelfUpdater's relaunch step
/// passes to a freshly-started Litos.Gui.exe so it resumes the same session/directory instead of
/// starting fresh. Pure string[] in/out, no Avalonia/filesystem involved.
/// </summary>
public class SessionResumeArgsTests
{
    [Fact]
    public void Parse_ReturnsArgs_WhenBothFlagsPresent()
    {
        var result = SessionResumeArgs.Parse(["--resume-session", "abc123", "--resume-dir", @"C:\repo"]);

        Assert.NotNull(result);
        Assert.Equal("abc123", result.SessionId);
        Assert.Equal(@"C:\repo", result.WorkingDirectory);
    }

    [Fact]
    public void Parse_IgnoresFlagOrder()
    {
        var result = SessionResumeArgs.Parse(["--resume-dir", @"C:\repo", "--resume-session", "abc123"]);

        Assert.NotNull(result);
        Assert.Equal("abc123", result.SessionId);
        Assert.Equal(@"C:\repo", result.WorkingDirectory);
    }

    [Fact]
    public void Parse_ReturnsNull_WhenNoArgs()
    {
        Assert.Null(SessionResumeArgs.Parse([]));
    }

    [Fact]
    public void Parse_ReturnsNull_WhenOnlyOneFlagPresent()
    {
        Assert.Null(SessionResumeArgs.Parse(["--resume-session", "abc123"]));
    }

    [Fact]
    public void ToArgv_RoundTripsThroughParse()
    {
        var original = new SessionResumeArgs("abc123", @"C:\repo");

        var roundTripped = SessionResumeArgs.Parse(original.ToArgv());

        Assert.Equal(original, roundTripped);
    }
}
