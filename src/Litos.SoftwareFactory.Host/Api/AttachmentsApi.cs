using System.Security.Claims;
using System.Security.Cryptography;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Settings;
using Litos.Tools.Attachments;
using Microsoft.AspNetCore.StaticFiles;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// Where an attachment's bytes are kept: the data folder's attachments/&lt;thread&gt;/&lt;id&gt;,
/// never the database or wwwroot (attachments-architecture.md §3). The M4 backup copies the folder.
/// </summary>
public sealed class AttachmentFiles(FactoryOptions options)
{
    public string Root => Path.Combine(options.DataDirectory, "attachments");

    public string PathOf(Guid threadId, Guid attachmentId) => Path.Combine(Root, threadId.ToString("n"), attachmentId.ToString("n"));

    public string PathOf(ThreadAttachment attachment) => PathOf(attachment.ThreadId, attachment.Id);

    public void Delete(ThreadAttachment attachment)
    {
        try
        {
            File.Delete(PathOf(attachment));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind, it costs only disk; the record is already gone.
        }
    }
}

/// <summary>
/// Upload and download (attachments-architecture.md §3). Types are the Litos VS Code agent's:
/// images, the documents MarkItDown converts, and any text file; an unknown binary is refused
/// with that agent's reason. A State-changing request already needs the factory's CSRF header,
/// and the session cookie is SameSite=Strict, so the form is read directly rather than bound,
/// which would ask for antiforgery middleware the host does not use.
/// </summary>
public static class AttachmentsApi
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapFactoryAttachments(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/threads/{id:guid}/attachments", async (
            Guid id, HttpRequest request, ClaimsPrincipal user, IFactoryStore store, FactorySettings settings, AttachmentFiles files,
            IClock clock, CancellationToken ct) =>
        {
            var limits = settings.Attachments;
            if (!limits.Enabled)
                return Refuse("An Admin has turned attachments off.", StatusCodes.Status403Forbidden);
            if (!request.HasFormContentType)
                return Refuse("Send the file as multipart/form-data, in a field named file.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files is not [var file])
                return Refuse("Send one file per request.");
            if (file.Length == 0)
                return Refuse($"{file.FileName} is empty.");
            if (file.Length > AttachmentTypes.MaxFileBytes)
                return Refuse($"{file.FileName} is {file.Length / (1024 * 1024)} MB, over the {AttachmentTypes.MaxFileBytes / (1024 * 1024)} MB limit.");
            if (CleanFileName(file.FileName) is not { } fileName)
                return Refuse("The file needs a name.");

            var attachmentId = Guid.NewGuid();
            var path = files.PathOf(id, attachmentId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            AttachmentKind kind;
            string mediaType;
            string sha256;
            await using (var upload = file.OpenReadStream())
            {
                var classified = Classify(fileName, upload);
                if (classified is null)
                    return Refuse($"'{fileName}' is a binary file with no known document format — not attachable.");
                (kind, mediaType) = classified.Value;

                await using var stored = File.Create(path);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81_920];
                int read;
                while ((read = await upload.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await stored.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            try
            {
                var saved = await store.AddAttachmentAsync(new ThreadAttachment
                {
                    Id = attachmentId,
                    ThreadId = id,
                    FileName = fileName,
                    MediaType = mediaType,
                    Kind = kind,
                    SizeBytes = file.Length,
                    Sha256 = sha256,
                    UploadedBy = user.UserId(),
                    UploadedAt = clock.UtcNow,
                }, limits.MaxBytesPerThread, ct);
                return Results.Created($"/api/attachments/{saved.Id}", View(saved));
            }
            catch (Exception ex) when (ex is StoreConflictException or StoreNotFoundException)
            {
                File.Delete(path);
                return ex is StoreNotFoundException ? Results.NotFound() : Refuse(ex.Message, StatusCodes.Status409Conflict);
            }
        // Kestrel's default 30 MB request limit holds a 20 MB file and the form around it.
        }).RequireProjectAccess(ProjectScoped.Thread);

        // The file itself. An image is sent as its type so the app can show it; anything else as
        // bytes to save, never to render: no upload is ever served as a page.
        api.MapGet("/attachments/{id:guid}", async (
            Guid id, ClaimsPrincipal user, IFactoryStore store, AttachmentFiles files, HttpResponse response, CancellationToken ct) =>
        {
            if (await store.GetAttachmentAsync(id, ct) is not { } attachment
                || (attachment.MessageKey is null && attachment.UploadedBy != user.UserId())
                || !File.Exists(files.PathOf(attachment)))
                return Results.NotFound();

            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            var image = attachment.Kind == AttachmentKind.Image;
            return Results.File(
                files.PathOf(attachment), image ? attachment.MediaType : "application/octet-stream",
                image ? null : attachment.FileName, enableRangeProcessing: false);
        }).RequireProjectAccess(ProjectScoped.Attachment);

        return app;
    }

    /// <summary>What the app is told about a file.</summary>
    public static object View(ThreadAttachment a) => new
    {
        a.Id,
        a.FileName,
        a.MediaType,
        Kind = a.Kind.ToString(),
        a.SizeBytes,
        a.UploadedAt,
        a.SentAt,
        a.MessageKey,
        Url = $"/api/attachments/{a.Id}",
    };

    /// <summary>The VS Code agent's rule: an image or a document by extension, else text if it has no NUL byte.</summary>
    internal static (AttachmentKind Kind, string MediaType)? Classify(string fileName, Stream content)
    {
        if (AttachmentTypes.TryGetImageMediaType(fileName, out var image))
            return (AttachmentKind.Image, image);
        if (AttachmentTypes.IsDocument(fileName))
            return (AttachmentKind.Document, ContentTypes.TryGetContentType(fileName, out var document) ? document : "application/octet-stream");
        if (PlainTextMedia.IsBinary(content))
            return null;
        return (AttachmentKind.Text, ContentTypes.TryGetContentType(fileName, out var text) && text.StartsWith("text/", StringComparison.Ordinal) ? text : "text/plain");
    }

    /// <summary>The name without any folder, control characters or surrounding space, at most 255 characters; null when nothing is left.</summary>
    internal static string? CleanFileName(string? name)
    {
        var bare = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/')[^1]);
        var clean = new string(bare.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0 || clean is "." or "..")
            return null;
        return clean.Length <= ThreadAttachment.MaxFileNameLength ? clean : clean[..ThreadAttachment.MaxFileNameLength];
    }

    private static IResult Refuse(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);
}

/// <summary>
/// Removes files uploaded and never sent with a message, a day after their upload
/// (attachments-architecture.md §3): they count against the thread's limit until then.
/// </summary>
public sealed class AttachmentSweeper(IFactoryStore store, AttachmentFiles files, IClock clock, ILogger<AttachmentSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan KeptUnsent = TimeSpan.FromDays(1);

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var removed = await store.RemoveUnsentAttachmentsAsync(clock.UtcNow - KeptUnsent, ct);
        foreach (var attachment in removed)
            files.Delete(attachment);
        return removed.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SweepAsync(stoppingToken) is > 0 and var count)
                    logger.LogInformation("Removed {Count} attachments uploaded more than a day ago and never sent.", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The unsent-attachment sweep failed; it runs again in an hour.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
