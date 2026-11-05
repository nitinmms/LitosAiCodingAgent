namespace Litos.Artifacts;

/// <summary>
/// One artifact's <c>meta.json</c>. Versions <see cref="OldestVersion"/>..<see cref="CurrentVersion"/>
/// are on disk as <c>v{n}.html</c>; older ones have been pruned (see ArtifactStore.MaxRetainedVersions).
/// </summary>
/// <param name="SourcePath">The fully-qualified file the latest version was published from — how a
/// republish of the same file finds this artifact again. Null only if a caller published raw HTML.</param>
public sealed record ArtifactMeta(
    string Id,
    string Title,
    string? SourcePath,
    int CurrentVersion,
    int OldestVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Created">True when this publish made a new artifact, false when it added a version.</param>
public sealed record ArtifactPublishResult(ArtifactMeta Meta, bool Created);
