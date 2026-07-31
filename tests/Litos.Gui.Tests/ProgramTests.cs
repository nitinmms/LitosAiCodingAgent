namespace Litos.Gui.Tests;

/// <summary>
/// Covers ResolveStartupWorkingDirectory, the decision behind setting Environment.CurrentDirectory
/// at GUI startup — see the fix in Program.cs for why this matters: file/shell tools resolve
/// relative paths against the real process CWD, not against the status bar's tracked working
/// directory, so the two must be kept in sync.
/// </summary>
public class ProgramTests
{
    [Fact]
    public void ResolveStartupWorkingDirectory_UsesLastWorkingDirectory_WhenItStillExists()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: @"C:\repo",
            directoryExists: dir => dir == @"C:\repo",
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\repo", result);
    }

    [Fact]
    public void ResolveStartupWorkingDirectory_FallsBackToCurrentDirectory_WhenLastWorkingDirectoryNoLongerExists()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: @"C:\deleted-repo",
            directoryExists: _ => false,
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\fallback", result);
    }

    [Fact]
    public void ResolveStartupWorkingDirectory_FallsBackToCurrentDirectory_WhenNoLastWorkingDirectoryConfigured()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: null,
            directoryExists: _ => throw new InvalidOperationException("Should not check existence of a null path."),
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\fallback", result);
    }
}
