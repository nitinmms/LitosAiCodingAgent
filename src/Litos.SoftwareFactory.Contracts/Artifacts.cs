namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// A page a chat run publishes on its thread (ReadMe_ArtifactsFeature.md §11). The page travels
/// whole: a chat turn has no write tools, so there is no file to name.
/// </summary>
/// <param name="ArtifactId">An artifact already on the run's thread to add a version to; null for a new one.</param>
/// <param name="Title">The name the agent gave it; null to take the page's own &lt;title&gt;.</param>
public sealed record ArtifactPublishRequest(string Html, string? Title, Guid? ArtifactId);

/// <summary>The host's answer to a publish: what was stored, or why it was refused.</summary>
public sealed record ArtifactPublishResponse(bool Accepted, string? Error, ArtifactSummary? Artifact, bool Created)
{
    public static ArtifactPublishResponse Refused(string error) => new(false, error, null, false);
}

/// <summary>An artifact on a thread, as the worker's tool and the chat brief show it.</summary>
public sealed record ArtifactSummary(Guid Id, string Title, int LatestVersion);

/// <summary>One stored version of an artifact; <see cref="Html"/> is null when there is no such version.</summary>
public sealed record ArtifactReadResponse(ArtifactSummary? Artifact, int Version, string? Html);
