using System.Diagnostics;
using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>Folder grants (Implement_Buzzies.md, Phase 3): a grant widens what a Buzzy's file tools
/// can reach, but only by exactly what the user granted — never by a traversal, a link, a nested
/// grant borrowed sideways, or a write through a read-only grant.</summary>
public class BuzzyGrantTests
{
    private static (string Root, string Workspace, string Shared, string Other) Layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "litos-grant-" + Guid.NewGuid().ToString("n"));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        return (root, workspace, shared, other);
    }

    [Fact]
    public void GrantedFolder_IsReadable()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            File.WriteAllText(Path.Combine(shared, "invoice.txt"), "x");
            var grants = new[] { new BuzzyGrant(shared, CanWrite: false) };

            var result = WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, "invoice.txt"), WorkspacePathGuard.Access.Read);

            Assert.True(result.IsAllowed, result.Error);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void ReadOnlyGrant_RefusesWritesAndEdits()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var grants = new[] { new BuzzyGrant(shared, CanWrite: false) };

            Assert.False(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, "new.txt"), WorkspacePathGuard.Access.Write).IsAllowed);
            Assert.False(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, "existing.txt"), WorkspacePathGuard.Access.Change).IsAllowed);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void ReadAndWriteGrant_AllowsWrites()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var grants = new[] { new BuzzyGrant(shared, CanWrite: true) };

            Assert.True(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, "new.txt"), WorkspacePathGuard.Access.Write).IsAllowed);
        }
        finally { CleanUp(root); }
    }

    [Theory]
    [InlineData("../other/secret.txt")]
    [InlineData("..")]
    public void GrantDoesNotReachOutsideItsFolder(string requested)
    {
        var (root, workspace, shared, other) = Layout();
        try
        {
            File.WriteAllText(Path.Combine(other, "secret.txt"), "x");
            var grants = new[] { new BuzzyGrant(shared, CanWrite: true) };

            var result = WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, requested), WorkspacePathGuard.Access.Read);

            Assert.False(result.IsAllowed);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void SiblingFolderSharingTheGrantPrefix_IsNotCovered()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var sharedEvil = Directory.CreateDirectory(shared + "-evil").FullName;
            var grants = new[] { new BuzzyGrant(shared, CanWrite: true) };

            Assert.False(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(sharedEvil, "x.txt"), WorkspacePathGuard.Access.Read).IsAllowed);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void WithoutAGrant_TheWorkspaceIsStillTheOnlyPlace()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var result = WorkspacePathGuard.Resolve(workspace, [], Path.Combine(shared, "x.txt"), WorkspacePathGuard.Access.Read);

            Assert.False(result.IsAllowed);
            Assert.Contains("no folder grant", result.Error);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void ACriticalNestedGrant_WinsOverTheParent()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var inner = Directory.CreateDirectory(Path.Combine(shared, "readonly")).FullName;
            var grants = new[]
            {
                new BuzzyGrant(shared, CanWrite: true),
                new BuzzyGrant(inner, CanWrite: false),
            };

            Assert.False(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(inner, "x.txt"), WorkspacePathGuard.Access.Write).IsAllowed);
            Assert.True(WorkspacePathGuard.Resolve(workspace, grants, Path.Combine(shared, "y.txt"), WorkspacePathGuard.Access.Write).IsAllowed);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void CanonicalGrant_RefusesAFolderInsideTheBuzzysOwnFolder()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var (path, error) = WorkspacePathGuard.CanonicalGrant(workspace, workspace);

            Assert.Null(path);
            Assert.Contains("own folder", error);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void CanonicalGrant_RefusesAFolderThatDoesNotExist()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var (path, error) = WorkspacePathGuard.CanonicalGrant(Path.Combine(shared, "missing"), workspace);

            Assert.Null(path);
            Assert.Contains("isn't a folder", error);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void CanonicalGrant_StoresTheCanonicalPath()
    {
        var (root, workspace, shared, _) = Layout();
        try
        {
            var (path, error) = WorkspacePathGuard.CanonicalGrant(Path.Combine(shared, "sub", "..") + Path.DirectorySeparatorChar, workspace);

            Assert.Null(error);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(shared)), path);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void CanonicalGrant_RefusesAJunctionOnTheWayIn()
    {
        var (root, workspace, shared, other) = Layout();
        try
        {
            var link = Path.Combine(root, "door");
            if (!TryCreateDirectoryLink(link, other))
                return; // This machine can't make links; nothing to test.

            var (path, error) = WorkspacePathGuard.CanonicalGrant(link, workspace);

            Assert.Null(path);
            Assert.Contains("link", error);

            // Remove the link first: an ordinary recursive delete can't (Phase 0's finding).
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
        }
        finally { CleanUp(root); }
    }

    /// <summary>Deletes the scratch tree, removing any link as a link so a junction can't make the
    /// delete fail or reach outside the tree (the same rule BuzzyStore.Delete follows).</summary>
    private static void CleanUp(string root)
    {
        try
        {
            BuzzyStore.DeleteTreeWithoutFollowingLinks(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind is harmless.
        }
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