namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.IsOpenableHttpUrl and IsOpenableLocalFile, the pure validation behind
/// OpenUrl — the click handler wired to every MarkdownViewer.LinkClicked event so links rendered
/// from model output (e.g. web_search results, or a file:// link to a mermaid diagram the agent
/// rendered via ShellTool) actually open instead of being visually clickable but inert. Only
/// http/https URLs and local files that both exist and resolve under the session's working
/// directory are allowed through, since the URL is model-authored text handed straight to
/// Process.Start(UseShellExecute: true) — a hallucinated or malicious scheme/path must be
/// rejected rather than launched.
/// </summary>
public class MainWindowLinkTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?query=1")]
    public void IsOpenableHttpUrl_AcceptsHttpAndHttps(string url)
    {
        var result = MainWindow.IsOpenableHttpUrl(url, out var uri);

        Assert.True(result);
        Assert.Equal(url, uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    public void IsOpenableHttpUrl_RejectsNonHttpSchemes(string url)
    {
        var result = MainWindow.IsOpenableHttpUrl(url, out _);

        Assert.False(result);
    }

    [Fact]
    public void IsOpenableLocalFile_AcceptsExistingFileUnderWorkingDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);

            var result = MainWindow.IsOpenableLocalFile(new Uri(file).AbsoluteUri, dir.FullName, out var resolved);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(file), resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_AcceptsBareAbsolutePathUnderWorkingDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);

            var result = MainWindow.IsOpenableLocalFile(file, dir.FullName, out var resolved);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(file), resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_AcceptsNonFileSchemeThatResolvesToADrivePath()
    {
        // Observed in the wild: the model links a rendered mermaid PNG as "sandbox:/C:/..." rather
        // than a proper file:// URI. Uri still resolves LocalPath to a real drive path for this
        // (see the comment on IsOpenableLocalFile), so it must be accepted the same as file://.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);
            var url = "sandbox:/" + file.Replace('\\', '/');

            var result = MainWindow.IsOpenableLocalFile(url, dir.FullName, out var resolved);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(file), resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_AcceptsBareRelativeFilenameUnderWorkingDirectory()
    {
        // The form a model most often emits for a file it just wrote alongside the session, e.g.
        // "[PNG](dml-prevention.png)" — no scheme, not rooted, resolved against workingDirectory.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);

            var result = MainWindow.IsOpenableLocalFile("diagram.png", dir.FullName, out var resolved);

            Assert.True(result);
            Assert.Equal(Path.GetFullPath(file), resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_RejectsRelativePathEscapingWorkingDirectory()
    {
        var workingDir = Directory.CreateTempSubdirectory();
        var outsideDir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(outsideDir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);
            var relative = Path.GetRelativePath(workingDir.FullName, file);

            var result = MainWindow.IsOpenableLocalFile(relative, workingDir.FullName, out _);

            Assert.False(result);
        }
        finally
        {
            workingDir.Delete(recursive: true);
            outsideDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_RejectsFileThatDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "hallucinated.png");

            var result = MainWindow.IsOpenableLocalFile(file, dir.FullName, out _);

            Assert.False(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_RejectsPathOutsideWorkingDirectory()
    {
        var workingDir = Directory.CreateTempSubdirectory();
        var outsideDir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(outsideDir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);

            var result = MainWindow.IsOpenableLocalFile(file, workingDir.FullName, out _);

            Assert.False(result);
        }
        finally
        {
            workingDir.Delete(recursive: true);
            outsideDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_RejectsWhenWorkingDirectoryIsNull()
    {
        var result = MainWindow.IsOpenableLocalFile(@"C:\anything\diagram.png", null, out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("not a path or url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    public void IsOpenableLocalFile_RejectsNonLocalUrls(string url)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var result = MainWindow.IsOpenableLocalFile(url, dir.FullName, out _);

            Assert.False(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsOpenableLocalFile_RejectsFtpEvenWhenItResolvesUnderWorkingDirectory()
    {
        // ftp is excluded from the local-file scheme allow-list even though its LocalPath could
        // coincidentally resolve to a real path under the working directory — a non-file scheme
        // should read as "rejected", not "happened to resolve to nothing."
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "diagram.png");
            File.WriteAllBytes(file, [0]);
            var url = "ftp:/" + file.Replace('\\', '/');

            var result = MainWindow.IsOpenableLocalFile(url, dir.FullName, out _);

            Assert.False(result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
