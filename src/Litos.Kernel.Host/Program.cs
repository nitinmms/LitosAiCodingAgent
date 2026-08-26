using Litos.Kernel;
using Litos.Kernel.Host;

// This process's real Console.Out is reserved for the wire protocol (KernelWireMessage lines) —
// a script's own Console.WriteLine must never reach it directly, or it corrupts the next protocol
// message with raw text (ReadMe_PTCPersistentKernel.md §8.2's stdout-capture detail). RunLoop
// swaps Console.Out for a captured buffer before any eval and restores the real stdout writer
// only around each protocol-message write.
return await RunLoop.RunAsync(Console.In, Console.Out, CancellationToken.None);
