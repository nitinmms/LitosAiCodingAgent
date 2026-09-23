namespace Litos.Kernel;

/// <summary>
/// Raised against every eval still awaiting a result when the kernel subprocess's stdout reaches
/// EOF or its reader loop throws — i.e. the interpreter died and no answer can ever arrive.
///
/// A distinct type (rather than a bare InvalidOperationException) so KernelSession.RunAsync can
/// tell "the kernel died under this eval" apart from an ordinary failure and report it as a
/// descriptive ToolResult.Error the model can actually act on, instead of letting it surface as an
/// unhandled exception or — as before this existed — as a hard-timeout stall.
/// </summary>
public sealed class KernelProcessDiedException(string message) : Exception(message);
