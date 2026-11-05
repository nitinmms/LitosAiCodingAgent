namespace Litos.Artifacts.Tests;

/// <summary>A fresh directory under the system temp folder, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("litos-artifacts-test-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes <paramref name="content"/> to a file under this directory and returns its full path.</summary>
    public string WriteFile(string name, string content)
    {
        var path = Combine(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a test's open handle must not fail the test run.
        }
    }
}
