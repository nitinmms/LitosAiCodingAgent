using System.Diagnostics;
using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

public class WorkspacePathGuardTests
{
    [Theory]
    [InlineData("notes.md")]
    [InlineData("reports/2026/today.md")]
    [InlineData("./reports/../notes.md")]
    [InlineData(".")]
    public void RelativePathsInside_AreResolvedAgainstTheWorkspace(string requested)
    {
        using var temp = new TempDir();

        var result = WorkspacePathGuard.Resolve(temp.Path, requested);

        Assert.True(result.IsAllowed, result.Error);
        Assert.StartsWith(Path.GetFullPath(temp.Path), result.FullPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsolutePathInside_IsAllowed()
    {
        using var temp = new TempDir();
        var inside = Path.Combine(temp.Path, "a.txt");

        Assert.Equal(Path.GetFullPath(inside), WorkspacePathGuard.Resolve(temp.Path, inside).FullPath);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../sibling.txt")]
    [InlineData("reports/../../escape.txt")]
    [InlineData("..\\..\\x")]
    public void Traversal_IsRefused(string requested)
    {
        using var temp = new TempDir();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "workspace")).FullName;

        var result = WorkspacePathGuard.Resolve(workspace, requested);

        Assert.False(result.IsAllowed);
        Assert.Contains("outside this Buzzy's workspace", result.Error);
    }

    [Fact]
    public void SiblingFolderSharingThePrefix_IsRefused()
    {
        using var temp = new TempDir();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "workspace")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "workspace-evil"));

        Assert.False(WorkspacePathGuard.Resolve(workspace, Path.Combine(temp.Path, "workspace-evil", "x.txt")).IsAllowed);
    }

    [Fact]
    public void AbsolutePathElsewhere_IsRefused()
    {
        using var temp = new TempDir();
        var elsewhere = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";

        Assert.False(WorkspacePathGuard.Resolve(temp.Path, elsewhere).IsAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_IsRefused(string requested)
    {
        using var temp = new TempDir();

        Assert.False(WorkspacePathGuard.Resolve(temp.Path, requested).IsAllowed);
    }

    [Fact]
    public void UncPath_IsRefused()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TempDir();

        Assert.False(WorkspacePathGuard.Resolve(temp.Path, @"\\server\share\x.txt").IsAllowed);
    }

    [Fact]
    public void AlternateDataStream_IsRefused()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TempDir();

        var result = WorkspacePathGuard.Resolve(temp.Path, "notes.txt:hidden");

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void JunctionInsideTheWorkspace_PointingOutside_IsRefused()
    {
        using var temp = new TempDir();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "workspace")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(workspace, "door");
        if (!TryCreateDirectoryLink(link, outside))
            return; // No way to create a link on this machine; nothing to test.

        var throughLink = WorkspacePathGuard.Resolve(workspace, "door/secret.txt");
        var theLinkItself = WorkspacePathGuard.Resolve(workspace, "door");

        Assert.False(throughLink.IsAllowed);
        Assert.Contains("is a link", throughLink.Error);
        Assert.False(theLinkItself.IsAllowed);
    }

    [Fact]
    public void NotYetExistingFolders_AreAllowed_SoWritesCanCreateThem()
    {
        using var temp = new TempDir();

        Assert.True(WorkspacePathGuard.Resolve(temp.Path, "new/deeper/file.md").IsAllowed);
    }

    /// <summary>A junction on Windows (no admin rights needed), a symlink elsewhere.</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }

            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
