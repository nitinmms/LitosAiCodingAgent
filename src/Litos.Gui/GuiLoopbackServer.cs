using Litos.Artifacts;
using Litos.Hosting.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Litos.Gui;

/// <summary>
/// A minimal in-process loopback web server that serves only what the artifact and share_file
/// tools link to — /artifacts/{id}[/v/{n}] and /files/{token} — so their links open in the user's
/// browser the same way they do from the VS Code extension (where Litos.VsCodeHost serves them).
/// Nothing else is mapped: the Gui still runs its turns in-process, not over HTTP.
///
/// Binds 127.0.0.1 on an OS-assigned port, like Litos.VsCodeHost, so the port differs on every
/// launch; links persisted into older sessions are re-pointed at the current port when clicked
/// (HostLinks.RewriteStale, see MainWindow.OpenUrl). Its own small container holds just the two
/// stores the routes need — the same instances the tools in the main container write to.
/// </summary>
public sealed class GuiLoopbackServer
{
    private WebApplication? _app;

    /// <summary>"http://127.0.0.1:{port}" once started; empty before that, or if starting failed.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>Why the server isn't running, when <see cref="Start"/> failed; null otherwise.</summary>
    public string? StartError { get; private set; }

    /// <summary>
    /// Starts listening. Blocks briefly (binding a loopback port takes milliseconds) so BaseUrl is
    /// set before the window — and so any turn — exists. Never throws: a failure leaves BaseUrl
    /// empty and is reported through <see cref="StartError"/>, so the rest of the app still runs.
    /// </summary>
    public void Start(ArtifactStore artifacts, SharedFileStore sharedFiles)
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0 = OS-assigned free loopback port
            builder.Logging.ClearProviders(); // WinExe: no console to log to
            builder.Services.AddSingleton(artifacts);
            builder.Services.AddSingleton(sharedFiles);

            var app = builder.Build();
            app.MapArtifactEndpoints();
            app.MapSharedFileEndpoints();
            app.StartAsync().GetAwaiter().GetResult();

            _app = app;
            BaseUrl = $"http://127.0.0.1:{new Uri(app.Urls.First()).Port}";
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
        }
    }

    /// <summary>Fire-and-forget from Window.Closing, same as MCP shutdown.</summary>
    public async Task StopAsync()
    {
        if (_app is null)
            return;

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
