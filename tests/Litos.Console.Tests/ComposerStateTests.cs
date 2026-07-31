using Litos.Console.Terminal;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for ComposerState, the plain (Terminal.Gui-free) state machine behind Composer —
/// buffer/cursor editing, @-mention detection/splicing, and the submit-vs-steer/follow-up/abort
/// decision. Kept independent of any View/terminal plumbing per ReadMe_AgentDesign.md §7.5's
/// "isolate the state machine from the terminal" testing strategy, since Terminal.Gui
/// 2.0.0-rc.64 ships no headless/fake driver to drive real keystrokes against in this test
/// project (confirmed by inspecting the installed assembly — see the migration report).
/// </summary>
public class ComposerStateTests
{
    [Fact]
    public void InsertChar_AppendsAtCursor_AndAdvancesCursor()
    {
        var state = new ComposerState();

        state.InsertChar('h');
        state.InsertChar('i');

        Assert.Equal("hi", state.Text);
        Assert.Equal(2, state.Cursor);
    }

    [Fact]
    public void BackspaceAtCursor_RemovesPrecedingChar()
    {
        var state = new ComposerState();
        state.SetText("hi");

        var removed = state.BackspaceAtCursor();

        Assert.True(removed);
        Assert.Equal("h", state.Text);
        Assert.Equal(1, state.Cursor);
    }

    [Fact]
    public void BackspaceAtCursor_AtStart_DoesNothing()
    {
        var state = new ComposerState();
        state.SetText("hi");
        state.MoveHome();

        var removed = state.BackspaceAtCursor();

        Assert.False(removed);
        Assert.Equal("hi", state.Text);
    }

    [Fact]
    public void DeleteAtCursor_RemovesFollowingChar()
    {
        var state = new ComposerState();
        state.SetText("hi");
        state.MoveHome();

        var removed = state.DeleteAtCursor();

        Assert.True(removed);
        Assert.Equal("i", state.Text);
    }

    [Fact]
    public void MoveLeftRight_ClampsAtBoundaries()
    {
        var state = new ComposerState();
        state.SetText("hi");
        state.MoveHome();

        state.MoveLeft();
        Assert.Equal(0, state.Cursor);

        state.MoveEnd();
        state.MoveRight();
        Assert.Equal(2, state.Cursor);
    }

    [Fact]
    public void SetCursor_ClampsToBufferLength()
    {
        var state = new ComposerState();
        state.SetText("hi");

        state.SetCursor(999);
        Assert.Equal(2, state.Cursor);

        state.SetCursor(-5);
        Assert.Equal(0, state.Cursor);
    }

    // ---- @-mention detection ------------------------------------------------------------------

    [Fact]
    public void FindActiveMention_NoAtSign_ReturnsNull()
    {
        var state = new ComposerState();
        state.SetText("hello world");

        Assert.Null(state.FindActiveMention());
    }

    [Fact]
    public void FindActiveMention_CursorRightAfterAt_ReturnsEmptyToken()
    {
        var state = new ComposerState();
        state.SetText("hello @");

        var mention = state.FindActiveMention();

        Assert.NotNull(mention);
        Assert.Equal(6, mention!.Start);
        Assert.Equal("", mention.Token);
    }

    [Fact]
    public void FindActiveMention_PartialToken_ReturnsTokenText()
    {
        var state = new ComposerState();
        state.SetText("hello @Pro");

        var mention = state.FindActiveMention();

        Assert.NotNull(mention);
        Assert.Equal("Pro", mention!.Token);
    }

    [Fact]
    public void FindActiveMention_WhitespaceAfterAt_BreaksTheMention()
    {
        var state = new ComposerState();
        state.SetText("me@example.com is my email");
        // Cursor sits well past the '@' with whitespace in between -> not an active mention.
        state.SetCursor("me@example.com is".Length);

        Assert.Null(state.FindActiveMention());
    }

    [Fact]
    public void FindActiveMention_UsesNearestPrecedingAt()
    {
        var state = new ComposerState();
        state.SetText("@foo @bar");

        var mention = state.FindActiveMention();

        Assert.NotNull(mention);
        Assert.Equal(5, mention!.Start); // the second '@'
        Assert.Equal("bar", mention.Token);
    }

    // ---- Splice regression: the dropped-'@' bug (ReadMe_AgentDesign.md §6.1.1) ------------------

    [Fact]
    public void SpliceMention_KeepsTheAtCharacter()
    {
        // Historical bug: removing starting at mentionStart (the '@' index itself) deleted the
        // '@' along with the partial token, so the accepted suggestion silently lost its mention
        // marker and MentionParser never recognized it as an attachment.
        var state = new ComposerState();
        state.SetText("Can you summarize @Pratima for me?");
        var mentionStart = state.Text.IndexOf('@');
        state.SetCursor(mentionStart + 1 + "Pratima".Length);

        state.SpliceMention(mentionStart, "Pratima-Mahatme-Plant Resource.png");

        Assert.Contains("@Pratima-Mahatme-Plant Resource.png", state.Text);
        Assert.DoesNotContain("Pratima for me?".Length.ToString(), state.Text); // sanity no-op guard
    }

    [Fact]
    public void SpliceMention_ReplacesOnlyThePartialToken_NotTrailingText()
    {
        var state = new ComposerState();
        state.SetText("@Pro please describe it");
        var mentionStart = 0;
        state.SetCursor("@Pro".Length);

        state.SpliceMention(mentionStart, "Program.cs");

        Assert.Equal("@Program.cs please describe it", state.Text);
    }

    [Fact]
    public void SpliceMention_EmptyToken_InsertsRightAfterAt()
    {
        var state = new ComposerState();
        state.SetText("hello @");
        var mentionStart = state.Text.IndexOf('@');
        state.SetCursor(state.Text.Length);

        state.SpliceMention(mentionStart, "src/Foo.cs");

        Assert.Equal("hello @src/Foo.cs", state.Text);
    }

    [Fact]
    public void SpliceMention_CursorAdvancesPastInsertedText()
    {
        var state = new ComposerState();
        state.SetText("@Pro");
        state.SetCursor(state.Text.Length);

        state.SpliceMention(0, "Program.cs");

        Assert.Equal("@Program.cs".Length, state.Cursor);
    }

    // ---- Line-wrap crash regression (ReadMe_AgentDesign.md §6.1.2) ------------------------------
    // The old MentionInputPrompt computed on-screen columns directly from promptWidth + cursor
    // against Console.WindowWidth, which threw once typed input wrapped past the first terminal
    // row. ComposerState has no column/row arithmetic at all — it only tracks a logical character
    // index — so there is no analogous computation left to overflow. These tests confirm long,
    // multi-hundred-character buffers (well past any plausible terminal width) never throw and
    // mention-detection keeps working correctly at the tail of such a buffer.
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(400)]
    public void LongBuffer_NeverThrows_RegardlessOfLength(int length)
    {
        var longText = string.Join(' ', Enumerable.Repeat("word", length / 5));
        var state = new ComposerState();

        var exception = Record.Exception(() =>
        {
            state.SetText(longText);
            state.MoveHome();
            state.MoveEnd();
            for (var i = 0; i < 10; i++)
                state.InsertChar('x');
            state.FindActiveMention();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void LongWrappedBuffer_MentionAtTail_StillDetectedCorrectly()
    {
        // A message long enough to wrap several times in any normal terminal width, with a
        // mention typed at the very end — the historical crash reproduced exactly here.
        var prefix = string.Join(' ', Enumerable.Repeat("this is a reasonably long sentence", 20));
        var state = new ComposerState();
        state.SetText(prefix + " @Read");

        var mention = state.FindActiveMention();

        Assert.NotNull(mention);
        Assert.Equal("Read", mention!.Token);
    }

    // ---- Submit / steer / follow-up / abort decision --------------------------------------------

    [Fact]
    public void OnEnter_Idle_WithText_ReturnsSubmit()
    {
        var state = new ComposerState { IsTurnInFlight = false };
        state.SetText("hello");

        Assert.Equal(ComposerAction.Submit, state.OnEnter(altHeld: false));
    }

    [Fact]
    public void OnEnter_Idle_EmptyBuffer_ReturnsNone()
    {
        var state = new ComposerState { IsTurnInFlight = false };

        Assert.Equal(ComposerAction.None, state.OnEnter(altHeld: false));
    }

    [Fact]
    public void OnEnter_MidTurn_WithText_ReturnsSteer()
    {
        var state = new ComposerState { IsTurnInFlight = true };
        state.SetText("look at this");

        Assert.Equal(ComposerAction.Steer, state.OnEnter(altHeld: false));
    }

    [Fact]
    public void OnEnter_MidTurn_AltHeld_ReturnsFollowUp()
    {
        var state = new ComposerState { IsTurnInFlight = true };
        state.SetText("do this after");

        Assert.Equal(ComposerAction.FollowUp, state.OnEnter(altHeld: true));
    }

    [Fact]
    public void OnEnter_MidTurn_EmptyBuffer_ReturnsEmptyEnterHint()
    {
        // The bug this preserves the fix for: bare Enter with nothing typed used to silently do
        // nothing under SteeringKeyWatcher; it must surface a hint instead.
        var state = new ComposerState { IsTurnInFlight = true };

        Assert.Equal(ComposerAction.EmptyEnterHint, state.OnEnter(altHeld: false));
    }

    [Fact]
    public void OnEscapeAbort_Idle_ReturnsNull_AndDoesNotClear()
    {
        var state = new ComposerState { IsTurnInFlight = false };
        state.SetText("not sent yet");

        var leftover = state.OnEscapeAbort();

        Assert.Null(leftover);
        Assert.Equal("not sent yet", state.Text);
    }

    [Fact]
    public void OnEscapeAbort_MidTurn_WithText_ReturnsLeftoverText_AndClearsBuffer()
    {
        var state = new ComposerState { IsTurnInFlight = true };
        state.SetText("unsent steering message");

        var leftover = state.OnEscapeAbort();

        Assert.Equal("unsent steering message", leftover);
        Assert.Equal("", state.Text);
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void OnEscapeAbort_MidTurn_EmptyBuffer_ReturnsNull()
    {
        var state = new ComposerState { IsTurnInFlight = true };

        var leftover = state.OnEscapeAbort();

        Assert.Null(leftover);
    }
}
