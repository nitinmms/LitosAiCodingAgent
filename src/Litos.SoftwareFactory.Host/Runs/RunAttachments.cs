using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Settings;
using Litos.Tools.Attachments;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>What a turn is given of its thread's files: the brief's section and the images to show.</summary>
public sealed record TurnAttachments(string Section, IReadOnlyList<TurnImage> Images, IReadOnlyList<BriefAttachment> Files)
{
    public static readonly TurnAttachments None = new("", [], []);

    /// <summary>The brief with the section after it, or the brief alone when there are no files.</summary>
    public string AppendTo(string brief) => Section.Length == 0 ? brief : $"{brief.TrimEnd()}\n\n{Section}";
}

/// <summary>
/// Gives a run, rework, spec or chat turn the files sent on its thread (attachments-architecture.md
/// §4): each is copied into the working copy's .factory/attachments/, never committed; documents
/// are converted to text, and text files read, into the brief; images go to the model after the
/// brief when it reads images. Review, scan and reflection turns are not given any.
/// </summary>
public sealed class RunAttachments(IFactoryStore store, AttachmentFiles files, IAttachmentConverter converter, FactorySettings settings, ILogger<RunAttachments> logger)
{
    public const string Folder = ".factory/attachments";

    /// <param name="workingCopy">The working copy the turn's worker runs in.</param>
    /// <param name="newSince">Files sent after this are new; older ones are listed by path only,
    /// since the session has seen them. Null: all are new.</param>
    public async Task<TurnAttachments> PrepareAsync(TaskThread thread, string workingCopy, DateTimeOffset? newSince, CancellationToken ct)
    {
        var sent = await store.ListAttachmentsAsync(thread.Id, includeUnsent: false, ct);
        if (sent.Count == 0)
            return TurnAttachments.None;

        var seesImages = AllowedModel.Accepts(thread.Provider, settings.Providers.Entry(thread.Provider)?.Model(thread.Model));
        var folder = Path.Combine(workingCopy, ".factory", "attachments");
        Directory.CreateDirectory(folder);

        var given = new List<BriefAttachment>();
        var images = new List<TurnImage>();
        foreach (var attachment in sent)
        {
            var name = CopyName(attachment);
            var relative = $"{Folder}/{name}";
            var isNew = newSince is null || attachment.SentAt > newSince;
            var source = files.PathOf(attachment);
            if (!File.Exists(source))
            {
                given.Add(new BriefAttachment(attachment.FileName, relative, attachment.Kind, isNew, Problem: "its file is missing from the factory's data folder"));
                continue;
            }

            var copy = Path.Combine(folder, name);
            File.Copy(source, copy, overwrite: true);
            if (!isNew)
            {
                given.Add(new BriefAttachment(attachment.FileName, relative, attachment.Kind, New: false));
                continue;
            }

            if (attachment.Kind == AttachmentKind.Image)
            {
                given.Add(new BriefAttachment(attachment.FileName, relative, attachment.Kind, New: true));
                if (seesImages)
                    images.Add(new TurnImage(relative, attachment.MediaType));
                continue;
            }

            given.Add(await ReadTextAsync(attachment, copy, relative, ct));
        }

        var fitted = BriefComposer.FitAttachmentText(given);
        return new TurnAttachments(BriefComposer.Attachments(fitted, seesImages), images, fitted);
    }

    private async Task<BriefAttachment> ReadTextAsync(ThreadAttachment attachment, string copy, string relative, CancellationToken ct)
    {
        try
        {
            var text = attachment.Kind == AttachmentKind.Document
                ? (await converter.ConvertAsync(new FilePathSource(copy), ct)).Markdown
                : await File.ReadAllTextAsync(copy, ct);
            return new BriefAttachment(attachment.FileName, relative, attachment.Kind, New: true, Text: text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Attachment {AttachmentId} ({FileName}) could not be read into the brief.", attachment.Id, attachment.FileName);
            return new BriefAttachment(attachment.FileName, relative, attachment.Kind, New: true,
                Problem: $"it could not be converted to text ({ex.Message}); open the file itself");
        }
    }

    /// <summary>The thread's note when a work run is given files: which, and whether the model sees the images.</summary>
    public static string Describe(TurnAttachments given)
    {
        var shown = given.Files.Where(f => f.New).Select(f => f.FileName).ToList();
        var earlier = given.Files.Count - shown.Count;
        var text = shown.Count == 0
            ? $"The agent has the {given.Files.Count} files attached earlier, in .factory/attachments/."
            : $"Gave the agent {(shown.Count == 1 ? "1 attached file" : $"{shown.Count} attached files")}: {string.Join(", ", shown)}"
              + (earlier > 0 ? $", and {earlier} attached earlier." : ".");
        var unseen = given.Files.Count(f => f.New && f.Kind == AttachmentKind.Image) - given.Images.Count;
        return unseen > 0 ? $"{text} This task's model does not read images, so it has the image files only." : text;
    }

    /// <summary>
    /// The copy's name: the start of its id, so two files of one name do not collide, then its
    /// own name with anything a file system refuses replaced.
    /// </summary>
    internal static string CopyName(ThreadAttachment attachment)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(attachment.FileName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return $"{attachment.Id.ToString("n")[..8]}-{(safe.Length == 0 ? "file" : safe)}";
    }
}
