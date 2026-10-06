using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

/// <summary>
/// The resume check (§16): what changed in the working copy between a run's last checkpoint and
/// the moment it resumes.
/// </summary>
public class WorkspaceDriftTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherHead = "fedcba9876543210fedcba9876543210fedcba98";

    private static WorkspaceSnapshot Snapshot(string head = Head, string branch = "factory/7f3a-csv-export", bool truncated = false, params (string Path, string Hash)[] files) =>
        new(branch, head, files.ToDictionary(f => f.Path, f => f.Hash), truncated);

    [Fact]
    public void SameSnapshot_IsNoDrift()
    {
        var snapshot = Snapshot(files: [("src/A.cs", "aa"), ("src/B.cs", WorkspaceSnapshot.Deleted)]);

        var drift = WorkspaceDrift.Compare(snapshot, snapshot with { });

        Assert.True(drift.IsEmpty);
        Assert.Equal("", drift.Describe());
    }

    [Fact]
    public void FileWithNewContent_IsEdited()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(files: [("src/A.cs", "aa")]), Snapshot(files: [("src/A.cs", "bb")]));

        Assert.Equal(["src/A.cs"], drift.Edited);
        Assert.Empty(drift.Added);
        Assert.Contains("- Changed since then: `src/A.cs`.", drift.Describe());
    }

    [Fact]
    public void FileThatDiffersOnlyNow_IsAdded()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(), Snapshot(files: [("src/New.cs", "aa")]));

        Assert.Equal(["src/New.cs"], drift.Added);
        Assert.Contains("- Newly changed: `src/New.cs`.", drift.Describe());
    }

    [Fact]
    public void FileThatNoLongerDiffers_IsReverted()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(files: [("src/A.cs", "aa")]), Snapshot());

        Assert.Equal(["src/A.cs"], drift.Reverted);
        Assert.Contains("No longer changed (back to the head commit): `src/A.cs`", drift.Describe());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileDeletedSinceTheCheckpoint_IsDeleted(bool changedBefore)
    {
        var before = changedBefore ? Snapshot(files: [("src/A.cs", "aa")]) : Snapshot();

        var drift = WorkspaceDrift.Compare(before, Snapshot(files: [("src/A.cs", WorkspaceSnapshot.Deleted)]));

        Assert.Equal(["src/A.cs"], drift.Deleted);
        Assert.Empty(drift.Edited);
        Assert.Empty(drift.Added);
        Assert.Contains("- Deleted: `src/A.cs`.", drift.Describe());
    }

    [Fact]
    public void FileRecreatedAfterBeingDeleted_IsEdited()
    {
        var drift = WorkspaceDrift.Compare(
            Snapshot(files: [("src/A.cs", WorkspaceSnapshot.Deleted)]), Snapshot(files: [("src/A.cs", "aa")]));

        Assert.Equal(["src/A.cs"], drift.Edited);
    }

    [Fact]
    public void HeadMoved_IsStatedWithShortCommits()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(), Snapshot(head: OtherHead));

        Assert.False(drift.IsEmpty);
        Assert.Equal("- The head commit moved from 0123456 to fedcba9.", drift.Describe());
    }

    [Fact]
    public void BranchChanged_IsStated()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(), Snapshot(branch: "main"));

        Assert.Equal("- The checked-out branch is `main`, not `factory/7f3a-csv-export`.", drift.Describe());
    }

    [Fact]
    public void Paths_AreListedInOrdinalOrder_WhateverOrderTheSnapshotHadThem()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(), Snapshot(files: [("src/b.cs", "1"), ("src/B.cs", "2"), ("src/a.cs", "3")]));

        Assert.Equal(["src/B.cs", "src/a.cs", "src/b.cs"], drift.Added);
    }

    [Fact]
    public void LongLists_ShowTheFirstPaths_AndCountTheRest()
    {
        var files = Enumerable.Range(0, 25).Select(i => ($"f{i:00}.cs", "x")).ToArray();

        var text = WorkspaceDrift.Compare(Snapshot(), Snapshot(files: files)).Describe(maxPaths: 3);

        Assert.Equal("- Newly changed: `f00.cs`, `f01.cs`, `f02.cs` and 22 more.", text);
    }

    [Fact]
    public void EveryKindOfChange_GetsItsOwnLine()
    {
        var before = Snapshot(files: [("edited.cs", "1"), ("reverted.cs", "2"), ("deleted.cs", "3")]);
        var after = Snapshot(head: OtherHead, files: [("edited.cs", "9"), ("added.cs", "4"), ("deleted.cs", WorkspaceSnapshot.Deleted)]);

        var lines = WorkspaceDrift.Compare(before, after).Describe().Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("- The head commit moved", lines[0]);
        Assert.StartsWith("- Changed since then: `edited.cs`", lines[1]);
        Assert.StartsWith("- Newly changed: `added.cs`", lines[2]);
        Assert.StartsWith("- Deleted: `deleted.cs`", lines[3]);
        Assert.StartsWith("- No longer changed", lines[4]);
    }

    /// <summary>A file left out of a truncated snapshot may still differ, so it is not reported as reverted.</summary>
    [Fact]
    public void TruncatedSnapshotNow_ReportsNothingAsReverted()
    {
        var drift = WorkspaceDrift.Compare(Snapshot(files: [("src/A.cs", "aa")]), Snapshot(truncated: true));

        Assert.Empty(drift.Reverted);
        Assert.True(drift.IsEmpty);
    }

    /// <summary>The snapshot is stored on the run as JSON and read back on resume.</summary>
    [Fact]
    public void Snapshot_SurvivesAJsonRoundTrip()
    {
        var snapshot = Snapshot(truncated: true, files: [("src/A.cs", "aa"), ("src/B.cs", WorkspaceSnapshot.Deleted)]);

        var restored = JsonSerializer.Deserialize<WorkspaceSnapshot>(JsonSerializer.Serialize(snapshot, FactoryWire.Json), FactoryWire.Json)!;

        Assert.True(WorkspaceDrift.Compare(snapshot, restored).IsEmpty);
        Assert.Equal(snapshot.Branch, restored.Branch);
        Assert.True(restored.Truncated);
        Assert.Equal(2, restored.Files.Count);
    }
}
