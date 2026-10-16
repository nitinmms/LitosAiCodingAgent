namespace Litos.SoftwareFactory.Core.Lessons;

/// <summary>Where a lesson stands (ReadMe_LitosSoftwareFactory_V1.md §21, m4-architecture.md §3).</summary>
public enum LessonStatus
{
    /// <summary>Proposed or written, never approved: never execution context.</summary>
    Suggested,

    /// <summary>It has an approved revision, which runs may be given while it is enabled.</summary>
    Approved,

    /// <summary>Turned down before it was ever approved.</summary>
    Dismissed,
}

/// <summary>What a lesson rests on: what a person corrected, decided or found, or only its author.</summary>
public enum LessonSource
{
    Manual,

    /// <summary>A change request on a handed-off task: a message on its thread.</summary>
    HumanCorrection,

    /// <summary>An answered decision.</summary>
    Decision,

    /// <summary>A review finding a person judged.</summary>
    ReviewFinding,

    /// <summary>A handoff's verification and evidence.</summary>
    Evidence,
}

/// <summary>
/// One project's lesson. Its revisions are immutable; <see cref="CurrentRevision"/> is the newest
/// and <see cref="ApprovedRevision"/> the one runs are given, so editing an approved lesson leaves
/// its guidance in place until an Admin approves the edit, and approving activates exactly the
/// revision that was read.
/// </summary>
public sealed class Lesson
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The short number people refer to it by ("L-12"), counted across the factory.</summary>
    public long Number { get; set; }

    public LessonStatus Status { get; set; } = LessonStatus.Suggested;

    /// <summary>Whether runs may be given its approved revision. A disabled lesson is kept.</summary>
    public bool Enabled { get; set; } = true;

    public int CurrentRevision { get; set; } = 1;
    public int? ApprovedRevision { get; set; }

    /// <summary>The approved lesson that replaced it; set when it was superseded, which also disables it.</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>The reflection job that proposed it; null for one a person wrote.</summary>
    public Guid? ProposedByJobId { get; set; }

    /// <summary>Who wrote it; null for one an agent proposed.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>An edit waits for approval: the newest revision is not the approved one.</summary>
    public bool HasPendingRevision => Status == LessonStatus.Approved && CurrentRevision != ApprovedRevision;
}

/// <summary>One immutable revision of a lesson.</summary>
public sealed class LessonRevision
{
    public Guid Id { get; set; }
    public Guid LessonId { get; set; }
    public int Revision { get; set; }
    public required string Rule { get; set; }

    /// <summary>The topics retrieval matches against a task's words, as a JSON array of strings.</summary>
    public string TopicsJson { get; set; } = "[]";

    /// <summary>Path globs the lesson applies to, as a JSON array of strings.</summary>
    public string PathsJson { get; set; } = "[]";

    public LessonSource SourceKind { get; set; }

    /// <summary>The message, decision, finding or handoff it rests on; null for a manual lesson.</summary>
    public Guid? SourceRef { get; set; }

    /// <summary>The thread <see cref="SourceRef"/> belongs to, found when the revision was written, so the
    /// app can link to it.</summary>
    public Guid? SourceThreadId { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>The rule normalized and hashed (<see cref="LessonRules.Normalize"/>), for exact duplicates.</summary>
    public required string NormalizedHash { get; set; }

    /// <summary>Who wrote this revision; null for an agent's proposal.</summary>
    public Guid? AuthorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>What a person or a reflection writes as a lesson's content.</summary>
public sealed record LessonDraft(
    string Rule, IReadOnlyList<string> Topics, IReadOnlyList<string> Paths, LessonSource Source, Guid? SourceRef, string Reason);

/// <summary>A lesson with its newest revision, and its approved revision when it has one.</summary>
public sealed record LessonDetails(Lesson Lesson, LessonRevision Current, LessonRevision? Approved);
