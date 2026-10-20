using Litos.SoftwareFactory.Setup;

// litos-factory: sets up, starts, stops and removes an installed Litos Software Factory
// (SoftwareFactoryInstallation.md §4.1). The MSI runs `setup --from-installer`, `stop` and
// `uninstall`; the Scheduled Task runs `run`; the Start-menu shortcut runs `open`.
const string Usage = """
    Usage: litos-factory <command>

      setup [--from-installer] [--quiet]
                                set up, or repair, this user's factory, then open it
      open                      start Docker and the factory if needed, then open the browser
      start                     start the factory in the background and wait until it is ready
      stop [--all]              stop the factory (with --all, its database too)
      status                    what is running, and where things are
      doctor                    check everything the factory needs, and say how to fix it
      refresh-path              capture the toolchain PATH again (after installing a toolchain)
      upgrade-db                start the database and apply migrations
      uninstall [--purge]       remove the start at sign-in (with --purge, the data and database too)
      run                       the background supervisor the Scheduled Task runs
      --version                 the version
    """;

var command = args.FirstOrDefault()?.ToLowerInvariant();
var options = args.Skip(1).Select(a => a.ToLowerInvariant()).ToHashSet();
var paths = FactoryPaths.ForCurrentUser();

// Dialogs where there is nobody at a terminal: the installer and the Start-menu shortcut. A silent
// install (msiexec /qn, winget) passes --quiet: no dialogs, and nothing started that would be asked about.
IUserInterface ui = (command == "open" || options.Contains("--from-installer")) && !options.Contains("--quiet")
    ? new DialogInterface()
    : new ConsoleInterface(Console.Out);
var commands = new Commands(paths, new ProcessRunner(), new WindowsMachine(), ui, Console.Out);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return command switch
    {
        "setup" => await commands.SetupAsync(options.Contains("--from-installer"), cancel.Token),
        "open" => await commands.OpenAsync(cancel.Token),
        "start" => await commands.StartAsync(cancel.Token),
        "stop" => await commands.StopAsync(options.Contains("--all"), cancel.Token),
        "status" => await commands.StatusAsync(cancel.Token),
        "doctor" => await commands.DoctorAsync(cancel.Token),
        "refresh-path" => await commands.RefreshPathAsync(cancel.Token),
        "upgrade-db" => await commands.UpgradeDatabaseAsync(cancel.Token),
        "uninstall" => await commands.UninstallAsync(options.Contains("--purge"), cancel.Token),
        "run" => await commands.RunAsync(cancel.Token),
        "--version" or "version" => PrintVersion(),
        _ => PrintUsage(command is null or "help" or "--help" or "-h" ? 0 : 2),
    };
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    return 130;
}

static int PrintVersion()
{
    Console.WriteLine(Commands.Version);
    return 0;
}

int PrintUsage(int exitCode)
{
    Console.WriteLine(Usage);
    return exitCode;
}
