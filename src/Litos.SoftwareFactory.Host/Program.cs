using Litos.SoftwareFactory.Host;
using Litos.SoftwareFactory.Infrastructure.Persistence;

// The software factory host. Settings come from the environment, or from a private settings
// file named with --env-file (see .env.factory.example):
//
//   Litos.SoftwareFactory.Host --env-file .env.factory --migrate    apply schema migrations, then exit
//   Litos.SoftwareFactory.Host --env-file .env.factory              run the host
var hostArgs = new List<string>(args);
var migrate = hostArgs.Remove("--migrate");

string? envFile = null;
var envFileIndex = hostArgs.IndexOf("--env-file");
if (envFileIndex >= 0 && envFileIndex + 1 < hostArgs.Count)
{
    envFile = hostArgs[envFileIndex + 1];
    hostArgs.RemoveRange(envFileIndex, 2);
}

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
