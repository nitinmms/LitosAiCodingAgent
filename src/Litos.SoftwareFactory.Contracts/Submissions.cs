using System.Text.Json.Serialization;

namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// What a completion tool reports. These are the only way an agent turn tells the host it has a
/// result (ReadMe_LitosSoftwareFactory_V1.md §8.5): the host acts on these callbacks, never on
/// the agent's prose or on the turn's event stream.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WorkSubmission), "work")]
[JsonDerivedType(typeof(DecisionSubmission), "decision")]
[JsonDerivedType(typeof(ReviewSubmission), "review")]
[JsonDerivedType(typeof(SpecSubmission), "spec")]
public abstract record Submission;

/// <summary>submit_work: an implement, repair or rework turn is finished and ready to verify.</summary>
public sealed record WorkSubmission(
    string Summary,
    IReadOnlyList<CriterionCoverage> Criteria,
    IReadOnlyList<string> TestsAdded,
    IReadOnlyList<string> KnownLimitations,
    IReadOnlyList<string> ManualTestSteps) : Submission;

/// <summary>One acceptance criterion and the tests that cover it, or a statement that it can
/// only be checked by hand.</summary>
public sealed record CriterionCoverage(string Criterion, IReadOnlyList<string> Tests, bool ManualOnly = false);

/// <summary>request_decision: the agent cannot reasonably infer a material choice.</summary>
public sealed record DecisionSubmission(
    string Question,
    string WhyItBlocks,
    IReadOnlyList<string> Options,
    string? Recommendation = null,
    string? Impact = null) : Submission;

/// <summary>submit_review: the review turn's findings. An empty list is a clean review.</summary>
public sealed record ReviewSubmission(IReadOnlyList<ReviewFinding> Findings) : Submission;

public enum FindingSeverity
{
    Blocking,
    Minor,
}

public sealed record ReviewFinding(FindingSeverity Severity, string File, int? Line, string Text);

/// <summary>submit_spec: a spec turn's proposed specification, for the user to approve.</summary>
public sealed record SpecSubmission(
    string Summary,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> AffectedAreas,
    string TestPlan,
    IReadOnlyList<string> OpenQuestions) : Submission;

/// <summary>Body of POST /internal/runs/{runId}/submissions.</summary>
public sealed record SubmissionRequest(string SessionId, Submission Submission);

/// <summary>
/// The host's answer, relayed to the model as the tool result. When the host refuses a
/// submission (for example submit_review outside a review turn) Message says why, so the model
/// can correct itself.
/// </summary>
public sealed record SubmissionResponse(bool Accepted, string Message);
