using Litos.SoftwareFactory.Host;
using Litos.SoftwareFactory.Infrastructure.Persistence;

// The software factory host. Settings come from the environment, or from a private settings
// file named with --env-file (see .env.factory.example):
//
//   Litos.SoftwareFactory.Host --env-file .env.factory --migrate    apply schema migrations, then exit
//   Litos.SoftwareFactory.Host --env-file .env.factory              run the host
//   Litos.SoftwareFactory.Host --env-file .env.factory --backup-data <folder>
//                                                                  copy the data beside a dump and write the manifest
//   Litos.SoftwareFactory.Host --verify-backup <folder>             check a backup against its manifest
// The backup commands are run by deploy/factory-backup.ps1 and deploy/factory-restore.ps1.
var hostArgs = new List<string>(args);
var migrate = hostArgs.Remove("--migrate");
var backupFolder = TakeValue(hostArgs, BackupCommands.BackupDataVerb);
if (TakeValue(hostArgs, BackupCommands.VerifyBackupVerb) is { } verifyFolder)
    return await BackupCommands.VerifyBackupAsync(verifyFolder, Console.Out, Console.Error, CancellationToken.None);

var envFile = TakeValue(hostArgs, "--env-file");

var configuration = new ConfigurationBuilder();
if (envFile is not null)
{
    if (!File.Exists(envFile))
    {
        Console.Error.WriteLine($"Settings file '{envFile}' was not found.");
        return 2;
    }

    configuration.AddInMemoryCollection(EnvFile.Read(envFile));
}

// The environment wins over the file, so one value can be overridden for a single start.
configuration.AddEnvironmentVariables();
var options = FactoryOptions.From(configuration.Build());

// A toolchain PATH set in the settings file reaches every worker and verification command the
// way the environment variable would (G6): ToolchainEnvironment reads it from the host's environment.
if (options.ToolchainPath is { } toolchainPath)
    Environment.SetEnvironmentVariable(FactoryOptions.ToolchainPathVariable, toolchainPath);

if (migrate)
{
    if (string.IsNullOrWhiteSpace(options.MigrationsConnectionString))
    {
        Console.Error.WriteLine("ConnectionStrings__FactoryMigrations is not set; it names the role that owns the schema.");
        return 2;
    }

    await FactoryDatabase.MigrateAsync(options.MigrationsConnectionString, CancellationToken.None);
    Console.WriteLine("The factory database is up to date.");
    return 0;
}

if (backupFolder is not null)
    return await BackupCommands.BackupDataAsync(backupFolder, options, Console.Out, Console.Error, CancellationToken.None);

var problems = options.Validate();
if (problems.Count > 0)
{
    Console.Error.WriteLine("The factory host cannot start:");
    foreach (var problem in problems)
        Console.Error.WriteLine($"  - {problem}");
    return 2;
}

var app = FactoryHostApp.Build([.. hostArgs], options);
if (await FactoryHostApp.PrepareAsync(app, options, CancellationToken.None) is { } blocker)
{
    Console.Error.WriteLine($"The factory host cannot start: {blocker}");
    return 2;
}

await app.RunAsync();
return 0;

// An option and the value after it, taken out of the arguments; null when the option is absent.
static string? TakeValue(List<string> arguments, string option)
{
    var index = arguments.IndexOf(option);
    if (index < 0 || index + 1 >= arguments.Count)
        return null;
    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}
