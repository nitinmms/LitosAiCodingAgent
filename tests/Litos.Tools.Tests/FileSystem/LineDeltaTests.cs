using Litos.Tools.FileSystem;

namespace Litos.Tools.Tests.FileSystem;

public class LineDeltaTests
{
    [Fact]
    public void Count_NullOldText_AllNewLinesAdded_NothingRemoved()
    {
        var (added, removed) = LineDelta.Count(null, "line1\nline2");

        Assert.Equal(2, added);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Count_EmptyNewText_NothingAdded()
    {
        var (added, removed) = LineDelta.Count("old", "");

        Assert.Equal(0, added);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Count_SingleLineReplace_OneAddedOneRemoved()
    {
        var (added, removed) = LineDelta.Count("target", "replaced");

        Assert.Equal(1, added);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Count_MultiLineOldAndNew_CountsEachSideIndependently()
    {
        var (added, removed) = LineDelta.Count("a\nb\nc", "x\ny");

        Assert.Equal(2, added);
        Assert.Equal(3, removed);
    }
}
