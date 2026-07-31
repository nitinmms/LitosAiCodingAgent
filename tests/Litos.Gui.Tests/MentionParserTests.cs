namespace Litos.Gui.Tests;

/// <summary>
/// Covers MentionParser's extraction/candidate-shrinking logic used by
/// MainWindow.ResolveMentionsAsync at send time. Ported alongside MentionParser itself from
/// Litos.Console.MentionParser — these cases mirror that project's own coverage.
/// </summary>
public class MentionParserTests
{
    [Fact]
    public void ExtractPaths_ReturnsEmpty_WhenTextHasNoMention()
    {
        Assert.Empty(MentionParser.ExtractPaths("hello world"));
    }

    [Fact]
    public void ExtractPaths_ExtractsASimplePath()
    {
        var paths = MentionParser.ExtractPaths("explain @src/Foo.cs please");

        Assert.Equal(["src/Foo.cs please"], paths);
    }

    [Fact]
    public void ExtractPaths_ExtractsMultipleDistinctMentions()
    {
        var paths = MentionParser.ExtractPaths("compare @a.txt and @b.txt");

        Assert.Equal(2, paths.Count);
        Assert.Contains("a.txt and", paths);
    }

    [Fact]
    public void ExtractPaths_DoesNotMatch_EmailLikeAtSign()
    {
        // (?<![\w@]) excludes an "@" immediately preceded by a word character.
        Assert.Empty(MentionParser.ExtractPaths("contact foo@bar.com"));
    }

    [Fact]
    public void ExpandCandidates_YieldsShrinkingPrefixes_LongestFirst()
    {
        var candidates = MentionParser.ExpandCandidates("Plant Resource.png please describe it").ToList();

        Assert.Equal("Plant Resource.png please describe it", candidates[0]);
        Assert.Equal("Plant Resource.png please describe", candidates[1]);
        Assert.Equal("Plant", candidates[^1]);
    }

    [Fact]
    public void ExpandCandidates_TrimsTrailingSentencePunctuation()
    {
        var candidates = MentionParser.ExpandCandidates("readme.md.").ToList();

        Assert.Equal("readme.md", candidates[0]);
    }

    [Fact]
    public void ExpandCandidates_SingleWord_YieldsOneCandidate()
    {
        var candidates = MentionParser.ExpandCandidates("readme.md").ToList();

        Assert.Equal(["readme.md"], candidates);
    }
}
