namespace Litos.Mail;

/// <summary>
/// The mailbox could not be reached, or is not connected at all. The tools turn this into an
/// ordinary <c>ToolResult.Error</c>, so a model reads "Gmail isn't connected" rather than a stack
/// trace, and a routine that needs mail fails with a sentence the user can act on.
///
/// Nothing here is ever thrown for "the user said no": a refused approval is a normal answer, and
/// the tools return it as a successful result saying nothing was changed (see
/// <see cref="MailBroker"/>'s tools). This type is for "we could not ask", not "we were told no".
/// </summary>
public sealed class MailAccessException(string message, Exception? innerException = null)
    : Exception(message, innerException);
