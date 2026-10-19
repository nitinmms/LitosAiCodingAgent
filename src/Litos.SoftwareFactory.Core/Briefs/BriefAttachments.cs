using System.Globalization;
using System.Text;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Briefs;

/// <summary>
/// One file a turn is given (attachments-architecture.md §4): where its copy is in the working
/// copy, and for a document or text file the text included in the brief.
/// </summary>
/// <param name="Path">Relative to the working copy, with forward slashes.</param>
/// <param name="New">Sent since the last handoff: on a rework, only these are shown again.</param>
/// <param name="Text">The file's text, converted for a document; null for an image, for a file
/// not shown again, or one that could not be read (then <paramref name="Problem"/> says why).</param>
public sealed record BriefAttachment(
    string FileName, string Path, AttachmentKind Kind, bool New, string? Text = null, bool Truncated = false, string? Problem = null);

public static partial class BriefComposer
{
    /// <summary>The most of one file's text a brief carries; past it the brief points to the file.</summary>
    public const int MaxAttachmentChars = 50_000;

    /// <summary>The most of all files' text one brief carries.</summary>
    public const int MaxAttachmentsChars = 100_000;

    /// <summary>
    /// The Attachments section of a run, rework, spec or chat brief; empty when there are no files.
    /// </summary>
    /// <param name="seesImages">Whether the thread's model is shown images: otherwise the section says
    /// it cannot see them, so the agent does not assume it has.</param>
    public static string Attachments(IReadOnlyList<BriefAttachment> files, bool seesImages)
    {
        if (files.Count == 0)
            return "";

        var list = new StringBuilder();
        foreach (var file in files)
        {
            list.Append(CultureInfo.InvariantCulture, $"- `{file.Path}` ({KindName(file)}");
            if (!file.New)
                list.Append(", attached earlier");
            list.Append(')');
            if (file.Problem is { } problem)
                list.Append(CultureInfo.InvariantCulture, $": {problem}");
            list.AppendLine();
        }

        var shownImages = files.Count(f => f.Kind == AttachmentKind.Image && f.New);
        var images = shownImages == 0 ? ""
            : seesImages
                ? $"\nThe {(shownImages == 1 ? "image is" : $"{shownImages} images are")} shown to you after this brief, in the order listed. What you see will not survive compaction: note what you need from {(shownImages == 1 ? "it" : "them")} early.\n"
                : "\nThis task's model cannot see images: you have the files only. Say so in your summary if the task depended on what they show.\n";

        var contents = new StringBuilder();
        foreach (var file in files.Where(f => f.Text is not null))
        {
            contents.AppendLine();
            contents.AppendLine(CultureInfo.InvariantCulture, $"<attachment name=\"{Attribute(file.FileName)}\" path=\"{Attribute(file.Path)}\">");
            // A file cannot close its own fence early.
            contents.AppendLine(file.Text!.Replace("</attachment", "</ attachment", StringComparison.OrdinalIgnoreCase).TrimEnd());
            contents.AppendLine("</attachment>");
            if (file.Truncated)
                contents.AppendLine(CultureInfo.InvariantCulture, $"(Only the start of {file.FileName} is above; read the rest from `{file.Path}`.)");
        }

        return Render("attachments", new()
        {
            ["files"] = list.ToString().TrimEnd(),
            ["images"] = images,
            ["contents"] = contents.ToString().TrimEnd(),
        }).TrimEnd();
    }

    /// <summary>
    /// Fits each file's text to <see cref="MaxAttachmentChars"/> and all of them to
    /// <see cref="MaxAttachmentsChars"/>, in order: a file that does not fit is left to be read
    /// from its path.
    /// </summary>
    public static IReadOnlyList<BriefAttachment> FitAttachmentText(IReadOnlyList<BriefAttachment> files)
    {
        var left = MaxAttachmentsChars;
        var fitted = new List<BriefAttachment>(files.Count);
        foreach (var file in files)
        {
            if (file.Text is not { } text)
            {
                fitted.Add(file);
                continue;
            }

            var room = Math.Min(MaxAttachmentChars, left);
            if (room <= 0)
            {
                fitted.Add(file with { Text = null, Problem = "not included here, since the brief's room for attachments is used up; read it from its path" });
                continue;
            }

            var cut = text.Length > room;
            var kept = cut ? text[..room] : text;
            left -= kept.Length;
            fitted.Add(file with { Text = kept, Truncated = file.Truncated || cut });
        }

        return fitted;
    }

    private static string KindName(BriefAttachment file) => (file.Kind, file.Text is not null) switch
    {
        (AttachmentKind.Image, _) => "image",
        (AttachmentKind.Document, true) => "document, converted to text below",
        (AttachmentKind.Document, false) => "document",
        (_, true) => "text, below",
        _ => "text",
    };

    private static string Attribute(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
}
