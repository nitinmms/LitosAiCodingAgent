using System.Text;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Tests;

/// <summary>A scratch directory deleted when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("litos-factory-infra-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, string content)
    {
        var path = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        // Git marks its object files read-only, which blocks a plain recursive delete on Windows.
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A process killed a moment ago can still hold a handle; the OS temp cleaner gets it.
        }
    }
}

/// <summary>Shell one-liners for tests that need a real process with a particular behaviour.</summary>
public static class Shell
{
    public static ProcessRequest Request(string script, string workingDirectory) => OperatingSystem.IsWindows()
        ? new ProcessRequest("cmd.exe", ["/d", "/c", script], workingDirectory)
        : new ProcessRequest("/bin/sh", ["-c", script], workingDirectory);

    /// <summary>A command that takes about this long and prints nothing.</summary>
    public static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

    /// <summary>Writes an executable script and returns its path.</summary>
    public static string WriteScript(string directory, string name, string windowsBody, string unixBody)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(path, "@echo off\r\n" + windowsBody.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
            return path;
        }

        var unixPath = Path.Combine(directory, name + ".sh");
        File.WriteAllText(unixPath, "#!/bin/sh\n" + unixBody.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(unixPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return unixPath;
    }
}

/// <summary>Reads the real reports captured from the two M1 evaluation repositories.</summary>
public static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Serves queued responses and records every request, including its headers.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public void Enqueue(System.Net.HttpStatusCode status, string body = "", string contentType = "application/json") =>
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    public void EnqueueFailure() => _responses.Enqueue(() => throw new HttpRequestException("Simulated connection failure."));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new CapturedRequest(
            request.Method, request.RequestUri!, body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No response queued for {request.Method} {request.RequestUri}.");
        return _responses.Dequeue()();
    }
}

public sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);
