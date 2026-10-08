using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The handoff holds the work to its approved specification (m2-architecture.md §5): each
/// approved criterion the work did not report on is named for the tester.
/// </summary>
public class HandoffSpecCriteriaTests
{
    private static CriterionCoverage Reported(string criterion) => new(criterion, ["A_Test"]);

    [Fact]
    public void Unreported_NamesEachApprovedCriterionNoReportMatches_InOrder()
    {
        var unreported = HandoffComposer.Unreported(
            ["Administrators can export.", "Others get a 403.", "The file has a header row."],
            [Reported("Others get a 403.")]);

        Assert.Equal(["Administrators can export.", "The file has a header row."], unreported);
    }

    [Theory]
    [InlineData("administrators can export")]
    [InlineData("  Administrators   can export.  ")]
    [InlineData("ADMINISTRATORS CAN EXPORT.")]
    public void Unreported_DoesNotHoldCaseSpacingOrAFullStopAgainstTheWork(string reported)
    {
        Assert.Empty(HandoffComposer.Unreported(["Administrators can export."], [Reported(reported)]));
    }

    [Fact]
    public void Unreported_AReworded_Criterion_IsNotTakenAsReported()
    {
        Assert.Equal(
            ["Administrators can export."],
            HandoffComposer.Unreported(["Administrators can export."], [Reported("Admins are able to export.")]));
    }

    [Fact]
    public void Unreported_WithoutASpecification_IsEmpty()
    {
        Assert.Empty(HandoffComposer.Unreported([], [Reported("Anything.")]));
    }

    [Fact]
    public void Evidence_CarriesTheRevision_AndListsTheUnreportedCriteriaAsLimitations()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            LastSubmission = new WorkSubmission("Added export.", [Reported("Administrators can export.")], ["A_Test"], ["Large files are slow."], []),
        };

        var evidence = HandoffComposer.Evidence(state, new HandoffFacts("factory/x", "abc1234", null, null)
        {
            SpecificationRevision = 3,
            ApprovedCriteria = ["Administrators can export.", "Others get a 403."],
        });

        Assert.Equal(3, evidence.SpecificationRevision);
        Assert.Equal(["Large files are slow.", "Approved criterion not reported on: Others get a 403."], evidence.KnownLimitations);
    }

    [Fact]
    public void Evidence_WithoutASpecification_HasNoRevision()
    {
        var evidence = HandoffComposer.Evidence(RunOrchestrator.NewRun(RunKind.Implement), new HandoffFacts("factory/x", null, null, null));

        Assert.Null(evidence.SpecificationRevision);
        Assert.Empty(evidence.KnownLimitations);
    }
}
