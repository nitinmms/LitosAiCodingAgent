using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>What a run is given of its thread's files (attachments-architecture.md §4).</summary>
public sealed class RunAttachmentsTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7];

    private TestHost _host = null!;
    private Guid _threadId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>The thread's model, marked as reading images or not, as an Admin would on the Providers tab.</summary>
    private async Task ModelSeesImagesAsync(bool sees)
    {
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();
        var thread = (await _host.ThreadAsync(_threadId)).Thread;
        var providers = settings.Providers;
        var entry = providers.Entry(thread.Provider)!;
        var models = entry.Models.Select(m => m.Id == thread.Model ? m with { AcceptsImages = sees } : m).ToList();
        if (models.All(m => m.Id != thread.Model))
            models.Add(new AllowedModel(thread.Model, 128_000, sees));
        var changed = providers with { Providers = [.. providers.Providers.Select(p => p.Name == entry.Name ? p with { Models = models } : p)] };
        await settings.SaveAsync(SettingsSections.Providers, changed, settings.RevisionOf(SettingsSections.Providers), null, default);
    }

    private async Task<Guid> UploadAsync(string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var response = await _host.Client.PostAsync($"api/threads/{_threadId}/attachments", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private Task SendAsync(string text, params Guid[] ids) =>
        _host.PostAsync($"api/threads/{_threadId}/messages", new { messageId = Guid.NewGuid().ToString(), text, attachmentIds = ids }, HttpStatusCode.Accepted);

    [Fact]
    public async Task TheFirstImplementationTurn_GetsTheFilesInItsBrief_TheImagesAfterIt_AndCopiesInTheWorkingCopy()
    {
        await ModelSeesImagesAsync(true);
        var shot = await UploadAsync("screen.png", Png);
        var csv = await UploadAsync("orders.csv", "id,total\n1,9.50\n"u8.ToArray());

        await SendAsync("@factory Import these orders, laid out as in the screenshot.", shot, csv);
        await _host.WaitForStateAsync(_threadId, LifecycleState.AwaitingHumanTesting);

        var implement = _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement);
        Assert.Contains("## Attachments", implement.Brief);
        Assert.Contains("(image)", implement.Brief);
        Assert.Contains("<attachment name=\"orders.csv\"", implement.Brief);
        Assert.Contains("id,total\n1,9.50", implement.Brief);
        var image = Assert.Single(implement.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.StartsWith(".factory/attachments/", image.Path);
        Assert.EndsWith("-screen.png", image.Path);

        var workspace = _host.Workers.WorkspaceOf(implement.Worker);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(workspace.Path, image.Path)));

        // Review judges the diff: no files.
        Assert.All(_host.Workers.Turns.Where(t => t.Kind == TurnKind.Review), review =>
        {
            Assert.Empty(review.Images);
            Assert.DoesNotContain("## Attachments", review.Brief);
        });
        var messages = (await _host.ThreadAsync(_threadId)).Messages;
        Assert.Contains(messages, m => m.Text == "Gave the agent 2 attached files: screen.png, orders.csv.");
    }

    [Fact]
    public async Task AModelThatDoesNotReadImages_GetsTheFileAndIsToldItCannotSeeIt()
    {
        await ModelSeesImagesAsync(false);
        var shot = await UploadAsync("screen.png", Png);

        await SendAsync("@factory Fix the button in the screenshot.", shot);
        await _host.WaitForStateAsync(_threadId, LifecycleState.AwaitingHumanTesting);

        var implement = _host.Workers.Turns.First(t => t.Kind == TurnKind.Implement);
        Assert.Empty(implement.Images);
        Assert.Contains("This task's model cannot see images", implement.Brief);
        Assert.Contains((await _host.ThreadAsync(_threadId)).Messages, m => m.Text.EndsWith("This task's model does not read images, so it has the image files only."));
    }

    /// <summary>A change request with a screenshot of what is wrong: only it is shown again; the first files are listed by path.</summary>
    [Fact]
    public async Task ARework_ShowsOnlyTheFilesSentWithTheChangeRequest()
    {
        await ModelSeesImagesAsync(true);
        var first = await UploadAsync("before.png", Png);
        await SendAsync("@factory Build the form.", first);
        await _host.WaitForStateAsync(_threadId, LifecycleState.AwaitingHumanTesting);
        var second = await UploadAsync("wrong.png", Png);

        await SendAsync("@factory The label is cut off, see the screenshot.", second);
        await _host.WaitForStateAsync(_threadId, LifecycleState.AwaitingHumanTesting);

        var rework = _host.Workers.Turns.First(t => t.Kind == TurnKind.Rework);
        Assert.EndsWith("-wrong.png", Assert.Single(rework.Images).Path);
        Assert.Contains("-before.png` (image, attached earlier)", rework.Brief);
    }

    /// <summary>A plain question about a screenshot: chat answers in a new session, so it is given every file.</summary>
    [Fact]
    public async Task AChatAnswer_IsGivenTheThreadsFiles()
    {
        await ModelSeesImagesAsync(true);
        var shot = await UploadAsync("screen.png", Png);
        _host.Workers.Script.Enqueue(call => Task.FromResult(new Core.Ports.TurnStreamResult(true, 0, null, "It is the orders page.")));

        await SendAsync("What page is this?", shot);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!_host.Workers.Turns.Any(t => t.Kind == TurnKind.Chat) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var chat = _host.Workers.Turns.Single(t => t.Kind == TurnKind.Chat);
        Assert.EndsWith("-screen.png", Assert.Single(chat.Images).Path);
        Assert.Contains("## Attachments", chat.Brief);
    }

    [Fact]
    public async Task AThreadWithoutFiles_GetsNoSection()
    {
        await SendAsync("@factory Add CSV export for Orders.");
        await _host.WaitForStateAsync(_threadId, LifecycleState.AwaitingHumanTesting);

        Assert.All(_host.Workers.Turns, t =>
        {
            Assert.DoesNotContain("## Attachments", t.Brief);
            Assert.Empty(t.Images);
        });
    }

    [Fact]
    public void CopyName_KeepsTheNameReadable_AndCannotEscapeTheFolder()
    {
        var attachment = new ThreadAttachment
        {
            Id = Guid.Parse("0123456789abcdef0123456789abcdef"), FileName = "../a:b?.png", MediaType = "image/png", Sha256 = "",
        };

        Assert.Equal("01234567-_a_b_.png", RunAttachments.CopyName(attachment));
    }
}
