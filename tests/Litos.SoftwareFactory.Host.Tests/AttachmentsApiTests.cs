using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Upload, download and sending files with a message (attachments-architecture.md §3).</summary>
public sealed class AttachmentsApiTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private TestHost _host = null!;
    private Guid _projectId;
    private Guid _threadId;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _projectId = await _host.RegisterProjectAsync();
        _threadId = await _host.CreateThreadAsync(_projectId);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid threadId, string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync($"api/threads/{threadId}/attachments", form);
    }

    private async Task<JsonElement> UploadedAsync(string fileName, byte[] bytes, HttpClient? client = null)
    {
        using var response = await UploadAsync(client ?? _host.Client, _threadId, fileName, bytes);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private Task SetAttachmentsAsync(AttachmentSettings value)
    {
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();
        return settings.SaveAsync(SettingsSections.Attachments, value, settings.RevisionOf(SettingsSections.Attachments), null, default);
    }

    [Theory]
    [InlineData("screen.png", "Image", "image/png")]
    [InlineData("orders.csv", "Text", "text/csv")]
    [InlineData("Program.cs", "Text", "text/plain")]
    [InlineData("note.pdf", "Document", "application/pdf")]
    public async Task Upload_ClassifiesByTheVsCodeAgentsRule_AndKeepsTheBytesInTheDataFolder(string fileName, string kind, string mediaType)
    {
        var bytes = kind == "Image" ? Png : "id,total\n1,2\n"u8.ToArray();

        var uploaded = await UploadedAsync(fileName, bytes);

        Assert.Equal((fileName, kind, mediaType), (uploaded.GetProperty("fileName").GetString(), uploaded.GetProperty("kind").GetString(), uploaded.GetProperty("mediaType").GetString()));
        var id = uploaded.GetProperty("id").GetGuid();
        var path = _host.App.Services.GetRequiredService<AttachmentFiles>().PathOf(_threadId, id);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(Path.Combine(_host.Options.DataDirectory, "attachments"), path);
        var stored = (await _host.Store.GetAttachmentAsync(id, default))!;
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), stored.Sha256);
    }

    [Fact]
    public async Task Upload_OfAnUnknownBinary_IsRefused_WithTheVsCodeAgentsReason()
    {
        using var response = await UploadAsync(_host.Client, _threadId, "tool.exe", [0x4D, 0x5A, 0, 0, 3]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("'tool.exe' is a binary file with no known document format — not attachable.", await ErrorOf(response));
        Assert.Empty(await _host.Store.ListAttachmentsAsync(_threadId, includeUnsent: true, default));
    }

    [Fact]
    public async Task Upload_Over20MB_IsRefused()
    {
        using var response = await UploadAsync(_host.Client, _threadId, "big.png", new byte[AttachmentTypes.MaxFileBytes + 1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("over the 20 MB limit", await ErrorOf(response));
    }

    [Fact]
    public async Task Upload_OverTheThreadsTotal_IsRefused_AndLeavesNoFile()
    {
        await SetAttachmentsAsync(new AttachmentSettings { MaxBytesPerThread = AttachmentTypes.MaxFileBytes });
        await UploadedAsync("a.png", new byte[15 * 1024 * 1024]);

        using var response = await UploadAsync(_host.Client, _threadId, "b.png", new byte[10 * 1024 * 1024]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("over its limit of 20 MB", await ErrorOf(response));
        Assert.Single(Directory.GetFiles(Path.Combine(_host.Options.DataDirectory, "attachments", _threadId.ToString("n"))));
    }

    [Fact]
    public async Task Upload_WhenAnAdminTurnedAttachmentsOff_IsForbidden()
    {
        await SetAttachmentsAsync(new AttachmentSettings { Enabled = false });

        using var response = await UploadAsync(_host.Client, _threadId, "a.png", Png);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("An Admin has turned attachments off.", await ErrorOf(response));
    }

    [Fact]
    public async Task Upload_WithoutTheCsrfHeader_IsRefused()
    {
        var client = _host.NewClient();
        await _host.SignInAsync(client);
        client.DefaultRequestHeaders.Remove(Litos.SoftwareFactory.Host.Auth.FactoryAuth.CsrfHeader);

        using var response = await UploadAsync(client, _threadId, "a.png", Png);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _host.Store.ListAttachmentsAsync(_threadId, includeUnsent: true, default));
    }

    [Fact]
    public async Task Upload_ToAThreadOutsideTheMembersProjects_IsNotFound()
    {
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("mia", other);
        var mia = await _host.SignedInAsync("mia");

        using var response = await UploadAsync(mia, _threadId, "a.png", Png);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_AnImageIsServedAsItsType_AnythingElseAsBytesToSave_NeitherSniffed()
    {
        var image = await UploadedAsync("a.png", Png);
        var text = await UploadedAsync("notes.html", "<script>alert(1)</script>"u8.ToArray());

        using var shown = await _host.Client.GetAsync(image.GetProperty("url").GetString()!.TrimStart('/'));
        using var saved = await _host.Client.GetAsync(text.GetProperty("url").GetString()!.TrimStart('/'));

        Assert.Equal("image/png", shown.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await shown.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", saved.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", saved.Content.Headers.ContentDisposition!.DispositionType);
        Assert.All([shown, saved], r => Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single()));
    }

    /// <summary>Until it is sent, a file is its uploader's; afterwards anyone on the project may read it.</summary>
    [Fact]
    public async Task Download_ByAnotherMember_OnlyOnceTheFileIsSent()
    {
        await _host.CreateMemberAsync("max", _projectId);
        var max = await _host.SignedInAsync("max");
        var uploaded = await UploadedAsync("a.png", Png);
        var url = uploaded.GetProperty("url").GetString()!.TrimStart('/');

        using (var before = await max.GetAsync(url))
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        await _host.PostAsync($"api/threads/{_threadId}/messages",
            new { messageId = "m-1", text = "@factory Fix the button in the screenshot.", attachmentIds = new[] { uploaded.GetProperty("id").GetGuid() } },
            HttpStatusCode.Accepted);
        using var after = await max.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Download_OutsideTheMembersProjects_IsNotFound()
    {
        var uploaded = await UploadedAsync("a.png", Png);
        await _host.PostAsync($"api/threads/{_threadId}/messages",
            new { messageId = "m-1", text = "Here is the screen.", attachmentIds = new[] { uploaded.GetProperty("id").GetGuid() } }, HttpStatusCode.Accepted);
        var other = await _host.RegisterProjectAsync("other");
        await _host.CreateMemberAsync("mia", other);
        var mia = await _host.SignedInAsync("mia");

        using var response = await mia.GetAsync(uploaded.GetProperty("url").GetString()!.TrimStart('/'));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Message_WithFiles_ShowsThemOnTheMessage_InTheThreadDetails()
    {
        var a = await UploadedAsync("screen.png", Png);
        var b = await UploadedAsync("orders.csv", "id\n1\n"u8.ToArray());

        await _host.PostAsync($"api/threads/{_threadId}/messages", new
        {
            messageId = "m-1",
            text = "@factory Import these orders like the screen shows.",
            attachmentIds = new[] { a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid() },
        }, HttpStatusCode.Accepted);

        var details = await _host.GetAsync($"api/threads/{_threadId}");
        var message = details.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("text").GetString()!.StartsWith("Import"));
        Assert.Equal(["screen.png", "orders.csv"], message.GetProperty("attachments").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()));
        Assert.All(await _host.Store.ListAttachmentsAsync(_threadId, includeUnsent: false, default), f => Assert.Equal("m-1", f.MessageKey));
    }

    [Fact]
    public async Task Message_WithMoreFilesThanTheLimit_IsRefused_AndSendsNothing()
    {
        await SetAttachmentsAsync(new AttachmentSettings { MaxFilesPerMessage = 1 });
        var a = await UploadedAsync("a.png", Png);
        var b = await UploadedAsync("b.png", Png);

        var refused = await _host.PostAsync($"api/threads/{_threadId}/messages", new
        {
            messageId = "m-1", text = "@factory Look.", attachmentIds = new[] { a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid() },
        }, HttpStatusCode.BadRequest);

        Assert.Equal("A message can carry at most 1 files.", refused.GetProperty("error").GetString());
        Assert.Empty(await _host.Store.ListAttachmentsAsync(_threadId, includeUnsent: false, default));
        Assert.Empty((await _host.ThreadAsync(_threadId)).Messages);
    }

    [Fact]
    public async Task Message_WithFiles_ToARunningTask_IsRefused()
    {
        var a = await UploadedAsync("a.png", Png);
        await using (var db = await _host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync())
        {
            var thread = await db.Threads.SingleAsync(t => t.Id == _threadId);
            thread.State = LifecycleState.Running;
            await db.SaveChangesAsync();
        }

        var refused = await _host.PostAsync($"api/threads/{_threadId}/messages",
            new { messageId = "m-1", text = "Also this.", attachmentIds = new[] { a.GetProperty("id").GetGuid() } }, HttpStatusCode.Conflict);

        Assert.Contains("while it is working", refused.GetProperty("error").GetString());
        Assert.Empty(await _host.Store.ListAttachmentsAsync(_threadId, includeUnsent: false, default));
    }

    [Fact]
    public async Task Message_WithSomeoneElsesFile_IsRefused()
    {
        await _host.CreateMemberAsync("max", _projectId);
        var max = await _host.SignedInAsync("max");
        var his = await UploadedAsync("his.png", Png, max);

        var refused = await _host.PostAsync($"api/threads/{_threadId}/messages",
            new { messageId = "m-1", text = "@factory Use his.", attachmentIds = new[] { his.GetProperty("id").GetGuid() } }, HttpStatusCode.Conflict);

        Assert.Contains("only they can send it", refused.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sweep_RemovesFilesNeverSentWithinADay_AndTheirBytes()
    {
        var files = _host.App.Services.GetRequiredService<AttachmentFiles>();
        var clock = _host.App.Services.GetRequiredService<IClock>();
        var old = await _host.Store.AddAttachmentAsync(new ThreadAttachment
        {
            ThreadId = _threadId, FileName = "old.png", MediaType = "image/png", Kind = AttachmentKind.Image, SizeBytes = 3,
            Sha256 = new string('0', 64), UploadedBy = await _host.AdminIdAsync(), UploadedAt = clock.UtcNow.AddDays(-2),
        }, AttachmentTypes.MaxFileBytes, default);
        Directory.CreateDirectory(Path.GetDirectoryName(files.PathOf(old))!);
        await File.WriteAllBytesAsync(files.PathOf(old), [1, 2, 3]);
        var fresh = await UploadedAsync("fresh.png", Png);

        var removed = await new AttachmentSweeper(_host.Store, files, clock, NullLogger<AttachmentSweeper>.Instance).SweepAsync(default);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(files.PathOf(old)));
        Assert.NotNull(await _host.Store.GetAttachmentAsync(fresh.GetProperty("id").GetGuid(), default));
    }

    [Fact]
    public async Task Settings_TheComposerIsToldTheLimits_AndAnAdminCanChangeThem()
    {
        var revision = (await _host.GetAsync("api/admin/settings")).GetProperty("attachments").GetProperty("revision").GetInt64();

        using (var bad = await _host.Client.PutAsJsonAsync("api/admin/settings/attachments", new { revision, settings = new { enabled = true, maxFilesPerMessage = 0, maxBytesPerThread = 50L * 1024 * 1024 } }))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var good = await _host.Client.PutAsJsonAsync("api/admin/settings/attachments", new { revision, settings = new { enabled = true, maxFilesPerMessage = 3, maxBytesPerThread = 40L * 1024 * 1024 } }))
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var told = (await _host.GetAsync("api/settings")).GetProperty("attachments");
        Assert.Equal((true, 3, 40L * 1024 * 1024, AttachmentTypes.MaxFileBytes),
            (told.GetProperty("enabled").GetBoolean(), told.GetProperty("maxFilesPerMessage").GetInt32(), told.GetProperty("maxBytesPerThread").GetInt64(), told.GetProperty("maxFileBytes").GetInt64()));
    }
}
