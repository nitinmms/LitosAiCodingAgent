namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.ResolveResumeWorkingDirectory, the pure decision logic behind
/// HandleResumeAsync's working-directory handling — extracted the same way
/// Program.ResolveStartupWorkingDirectory is, so it's testable without an Avalonia window or a
/// real filesystem.
///
/// Regression coverage for a real bug: resuming a session with no recorded WorkingDirectory (an
/// older session predating that field) used to leave WorkingDirectoryText/_mentionIndex
/// untouched by an early return, and a subsequent /new — which read _transcript.WorkingDirectory
/// directly — silently fell back to Directory.GetCurrentDirectory() (the process's own launch
/// directory) instead of staying in whatever directory the user was actually working in.
/// </summary>
public class MainWindowWorkingDirectoryTests
{
    [Fact]
    public void ResolveResumeWorkingDirectory_UsesRecordedDirectory_WhenItExists()
    {
        var result = MainWindow.ResolveResumeWorkingDirectory(
            "C:\\Orient", directoryExists: _ => true, getCurrentDirectory: () => "C:\\Fallback");

        Assert.Equal("C:\\Orient", result.Directory);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ResolveResumeWorkingDirectory_StaysInCurrentDirectory_WhenRecordedDirectoryIsNull()
    {
        // The bug scenario: an older/edge-case session with no recorded WorkingDirectory at all.
        var result = MainWindow.ResolveResumeWorkingDirectory(
            recordedWorkingDirectory: null, directoryExists: _ => true, getCurrentDirectory: () => "C:\\Orient");

        Assert.Equal("C:\\Orient", result.Directory);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ResolveResumeWorkingDirectory_StaysInCurrentDirectory_AndWarns_WhenRecordedDirectoryNoLongerExists()
    {
        var result = MainWindow.ResolveResumeWorkingDirectory(
            "C:\\Deleted", directoryExists: _ => false, getCurrentDirectory: () => "C:\\Orient");

        Assert.Equal("C:\\Orient", result.Directory);
        Assert.NotNull(result.Warning);
        Assert.Contains("C:\\Deleted", result.Warning);
        Assert.Contains("C:\\Orient", result.Warning);
    }

    [Fact]
    public void ResolveResumeWorkingDirectory_NoWarning_WhenRecordedDirectoryIsNull()
    {
        // A missing-on-disk directory warrants a warning (something disappeared); a session that
        // simply never recorded one doesn't — nothing was lost, there's nothing to warn about.
        var result = MainWindow.ResolveResumeWorkingDirectory(
            recordedWorkingDirectory: null, directoryExists: _ => false, getCurrentDirectory: () => "C:\\Orient");

        Assert.Null(result.Warning);
    }
}
