namespace Litos.Tools.Attachments;

/// <summary>Size limits shared by every face that accepts or shares files, so they can't drift.</summary>
public static class AttachmentLimits
{
    /// <summary>The most one attached, pasted or shared file may be: 20MB. Used by Litos.Api's
    /// TurnsEndpoints and ShareFileTool, Litos.VsCodeHost's /attachments endpoints, Litos.Hosting's
    /// share_file, and Litos.Gui's AttachHandler.</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;
}
