using System.Text;

namespace Litos.Console.Terminal;

/// <summary>
/// The pure, terminal-agnostic state machine behind <see cref="Composer"/>: buffer/cursor
/// editing, @-mention detection, and the submit-vs-steer/follow-up/abort decision. Kept free of
/// any Terminal.Gui type so it can be driven and asserted in plain unit tests without a View or a
/// real/fake terminal driver — the same "isolate the state machine from the terminal" principle
/// the old IKeyInput-seam tests followed (see ReadMe_AgentDesign.md §7.5).
///
/// This single state machine replaces two separate Spectre-console-era components:
/// MentionInputPrompt (the primary '>' prompt between turns) and SteeringKeyWatcher (the
/// mid-turn poll loop) — what changes is only whether "submit" means "start a new turn" or
/// "steer/follow-up the running one," decided by <see cref="IsTurnInFlight"/>.
/// </summary>
public sealed class ComposerState
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Whether a turn is currently running — determines what Enter/Alt+Enter/Esc mean.</summary>
    public bool IsTurnInFlight { get; set; }

    public string Text => _buffer.ToString();

    public int Cursor { get; private set; }

    public int Length => _buffer.Length;

    public void SetText(string text)
    {
        _buffer.Clear();
        _buffer.Append(text);
        Cursor = _buffer.Length;
    }

    public void Clear()
    {
        _buffer.Clear();
        Cursor = 0;
    }

    public void InsertChar(char ch)
    {
        _buffer.Insert(Cursor, ch);
        Cursor++;
    }

    public void InsertText(string text)
    {
        _buffer.Insert(Cursor, text);
        Cursor += text.Length;
    }

    public bool BackspaceAtCursor()
    {
        if (Cursor <= 0)
            return false;
        _buffer.Remove(Cursor - 1, 1);
        Cursor--;
        return true;
    }

    public bool DeleteAtCursor()
    {
        if (Cursor >= _buffer.Length)
            return false;
        _buffer.Remove(Cursor, 1);
        return true;
    }

    public void MoveLeft()
    {
        if (Cursor > 0)
            Cursor--;
    }

    public void MoveRight()
    {
        if (Cursor < _buffer.Length)
            Cursor++;
    }

    public void MoveHome() => Cursor = 0;

    public void MoveEnd() => Cursor = _buffer.Length;

    public void SetCursor(int position) => Cursor = Math.Clamp(position, 0, _buffer.Length);

    /// <summary>
    /// If the cursor sits inside an in-progress @token (an '@' with no whitespace between it and
    /// the cursor), returns the token's start index (the index of '@' itself) and its text after
    /// '@'. Ported verbatim from the old MentionInputPrompt.FindActiveMention.
    /// </summary>
    public MentionMatch? FindActiveMention()
    {
        var text = Text;
        var at = text.LastIndexOf('@', Math.Max(0, Cursor - 1));
        if (at < 0)
            return null;

        for (var i = at + 1; i < Cursor; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                return null;
        }

        return new MentionMatch(at, text[(at + 1)..Cursor]);
    }

    /// <summary>
    /// Splices <paramref name="picked"/> into the buffer in place of the partial token that
    /// follows the '@' at <paramref name="mentionStart"/>, WITHOUT deleting the '@' itself — this
    /// is the fix for the historical dropped-'@' splice bug (ReadMe_AgentDesign.md §6.1.1):
    /// removing starting at mentionStart (the '@' index itself, rather than mentionStart + 1)
    /// used to delete the '@' along with the partial token, so MentionParser (which only
    /// recognizes text starting with '@') never saw the accepted suggestion as a mention at all.
    /// </summary>
    public void SpliceMention(int mentionStart, string picked)
    {
        var removeFrom = mentionStart + 1;
        var removeCount = Cursor - removeFrom;
        if (removeCount > 0)
            _buffer.Remove(removeFrom, removeCount);
        _buffer.Insert(removeFrom, picked);
        Cursor = removeFrom + picked.Length;
    }

    /// <summary>
    /// Decides what Enter should do right now. Bare Enter (nothing typed) while idle is still a
    /// submit-attempt (caller ignores empty submits); bare Enter mid-turn shows the same
    /// "type something first" hint the old SteeringKeyWatcher did instead of silently doing
    /// nothing.
    /// </summary>
    public ComposerAction OnEnter(bool altHeld)
    {
        if (!IsTurnInFlight)
            return _buffer.Length == 0 ? ComposerAction.None : ComposerAction.Submit;

        if (_buffer.Length == 0)
            return ComposerAction.EmptyEnterHint;

        return altHeld ? ComposerAction.FollowUp : ComposerAction.Steer;
    }

    /// <summary>
    /// Esc mid-turn aborts and hands back any typed-but-unsent text for restoration (matching
    /// pi's abort() semantics per ReadMe_AgentDesign.md §7.2); Esc while idle is a no-op here
    /// (the caller may still use it to close an open mention dropdown).
    /// </summary>
    public string? OnEscapeAbort()
    {
        if (!IsTurnInFlight)
            return null;

        var leftover = _buffer.Length > 0 ? _buffer.ToString() : null;
        Clear();
        return leftover;
    }
}

public sealed record MentionMatch(int Start, string Token);

public enum ComposerAction
{
    /// <summary>Nothing to do (e.g. bare Enter while idle with an empty buffer).</summary>
    None,

    /// <summary>Idle Enter with typed text: start a new turn with this text.</summary>
    Submit,

    /// <summary>Mid-turn Enter with typed text: steer the running turn immediately.</summary>
    Steer,

    /// <summary>Mid-turn Alt+Enter with typed text: queue as a follow-up for after the turn ends.</summary>
    FollowUp,

    /// <summary>Mid-turn bare Enter: nothing was typed to steer/follow-up with.</summary>
    EmptyEnterHint,
}
