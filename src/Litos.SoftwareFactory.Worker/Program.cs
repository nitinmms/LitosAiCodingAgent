using Litos.SoftwareFactory.Worker;

// The software factory's per-run worker. The factory host starts one of these per run, inside
// the run's working copy, with the launch secret, host URL and run id in its environment and
// the model in its arguments (see WorkerOptions). It reports its port as the first line on
// stdout, and exits when the host tells it to or when the host itself is gone.
WorkerOptions options;
try
{
    options = WorkerOptions.Parse(args, Environment.GetEnvironmentVariable);
}
catch (WorkerOptionsException ex)
{
    Console.Error.WriteLine($"[worker] {ex.Message}");
    return 2;
}

WorkerLaunchVariables.RemoveFromEnvironment(Environment.SetEnvironmentVariable);

// The worker's own arguments are not ASP.NET configuration, so none are passed on.
WebApplication app;
try
{
    app = WorkerApp.Build(options, []);
}
catch (WorkerOptionsException ex)
{
    // A file the host wrote for the run, such as its skills, could not be read.
    Console.Error.WriteLine($"[worker] {ex.Message}");
    return 2;
}

await WorkerApp.StartAsync(app);
await app.WaitForShutdownAsync();
return 0;
