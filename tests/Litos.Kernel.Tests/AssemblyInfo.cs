using Xunit;

// ScriptSession.EvalAsync redirects the process-wide Console.Out/Console.In for the duration of
// each eval — correct and safe in Litos.Kernel.Host's real deployment (a dedicated subprocess that
// never does anything else concurrently), but unsafe if two InProcessKernelHostFixture instances
// run RunLoop.RunAsync concurrently in this same test process, since they'd race on the same
// global Console state. Disabling parallelization here avoids testing an artifact of the harness
// instead of the real cross-process behavior.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
