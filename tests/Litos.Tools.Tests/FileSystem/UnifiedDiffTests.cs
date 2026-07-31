using Litos.Tools.FileSystem;

namespace Litos.Tools.Tests.FileSystem;

public class UnifiedDiffTests
{
    [Fact]
    public void Render_IncludesFileHeaderLines()
    {
        var diff = UnifiedDiff.Render("old", "new", "foo.txt");

        Assert.Contains("--- foo.txt", diff);
        Assert.Contains("+++ foo.txt", diff);
    }

    [Fact]
    public void Render_PrefixesEveryOldLineWithMinus_AndEveryNewLineWithPlus()
    {
        var diff = UnifiedDiff.Render("line1\nline2", "line3", "foo.txt");

        Assert.Contains("-line1", diff);
        Assert.Contains("-line2", diff);
        Assert.Contains("+line3", diff);
    }

    [Fact]
    public void Render_NullOldText_TreatedAsEmptyString_StillEmitsOneMinusLine()
    {
        // oldText ?? string.Empty -> "".Split('\n') -> [""], so a single "-" line (for the
        // empty old content) is emitted even though there was technically no old text.
        var diff = UnifiedDiff.Render(null, "new content", "foo.txt");

        Assert.Contains($"-{Environment.NewLine}", diff);
    }

    [Fact]
    public void Render_EmptyNewText_StillEmitsOnePlusLine()
    {
        var diff = UnifiedDiff.Render("old", "", "foo.txt");

        Assert.Contains($"+{Environment.NewLine}", diff);
    }

    [Fact]
    public void Render_IsNotARealDiff_EmitsAllOldLinesThenAllNewLines_NoLcsMatching()
    {
        // Documents that this is a whole-file dump, not a minimal diff: even an unchanged
        // line appears in both the "-" and "+" sections rather than being omitted.
        var diff = UnifiedDiff.Render("unchanged\nold line", "unchanged\nnew line", "foo.txt");

        Assert.Contains("-unchanged", diff);
        Assert.Contains("+unchanged", diff);
        Assert.Contains("-old line", diff);
        Assert.Contains("+new line", diff);
    }

    [Fact]
    public void Render_PathAppearsInBothHeaderLines_EvenWithSpecialCharacters()
    {
        var diff = UnifiedDiff.Render("a", "b", @"C:\repo\my file.txt");

        Assert.Contains(@"--- C:\repo\my file.txt", diff);
        Assert.Contains(@"+++ C:\repo\my file.txt", diff);
    }
}
