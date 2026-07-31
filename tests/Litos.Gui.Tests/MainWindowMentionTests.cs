namespace Litos.Gui.Tests;

/// <summary>
/// Covers MainWindow.FindActiveMentionStart, the pure "@"-token detection behind the mention
/// popup (see UpdateMentionPopupFromTypedText/OnMentionAccepted in MainWindow.axaml.cs) —
/// extracted the same way FindBranchPoints is, so it's testable without an Avalonia control tree.
/// </summary>
public class MainWindowMentionTests
{
    [Fact]
    public void FindActiveMentionStart_ReturnsAtIndex_ForBareAtSignAtStart()
    {
        Assert.Equal(0, MainWindow.FindActiveMentionStart("@", 1));
    }

    [Fact]
    public void FindActiveMentionStart_ReturnsAtIndex_WhileTypingAPartialToken()
    {
        Assert.Equal(6, MainWindow.FindActiveMentionStart("hello @src/Fo", 13));
    }

    [Fact]
    public void FindActiveMentionStart_ReturnsMinusOne_WhenNoAtSignPrecedesCaret()
    {
        Assert.Equal(-1, MainWindow.FindActiveMentionStart("hello world", 11));
    }

    [Fact]
    public void FindActiveMentionStart_ReturnsMinusOne_WhenWhitespaceEndsTheMention()
    {
        // Typing a space after "@foo" ends the in-progress mention — the popup should close
        // rather than keep filtering on stale text.
        Assert.Equal(-1, MainWindow.FindActiveMentionStart("@foo bar", 8));
    }

    [Fact]
    public void FindActiveMentionStart_ReturnsMinusOne_WhenAtSignIsPrecededByAWordCharacter()
    {
        // "foo@bar" must never trigger the popup (e.g. an email address typed into the message).
        Assert.Equal(-1, MainWindow.FindActiveMentionStart("foo@bar", 7));
    }

    [Fact]
    public void FindActiveMentionStart_AllowsAtSign_AtStartOfMessage()
    {
        Assert.Equal(5, MainWindow.FindActiveMentionStart("look @readme", 12));
    }

    [Fact]
    public void FindActiveMentionStart_AllowsAtSign_ImmediatelyAfterWhitespace()
    {
        Assert.Equal(5, MainWindow.FindActiveMentionStart("see  @foo", 9));
    }

    [Fact]
    public void FindActiveMentionStart_UsesTheNearestAtSign_WhenTwoPrecedeTheCaret()
    {
        Assert.Equal(7, MainWindow.FindActiveMentionStart("@a.txt @b.tx", 12));
    }
}
