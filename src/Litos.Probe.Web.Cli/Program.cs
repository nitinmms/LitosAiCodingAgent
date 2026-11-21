using System.CommandLine;
using System.Text;
using Litos.Probe.Web.Cli;

// litos-probe-web (README_Litos.Probe.Web.md §9). Exit codes: 0 passed, 1 failed,
// 2 environment failure, 3 bad contract.
Console.OutputEncoding = Encoding.UTF8;

var repo = new Argument<DirectoryInfo>("repo") { Description = "The repository (working copy) to check." };
var contract = new Option<FileInfo?>("--contract") { Description = "The contract file. Default: <repo>/litos.test.yaml." };
var steps = new Option<string>("--steps") { Description = "Gate steps to run, e.g. 1-8, 1,2,4 or 1-4,8.", DefaultValueFactory = _ => "1-8" };
var profile = new Option<string?>("--profile") { Description = "The stack profile to try. Default: the one detected." };
var dir = new Option<string?>("--dir") { Description = "The app's folder within the repository, for a monorepo. Default: the detected one." };
var headed = new Option<bool>("--headed") { Description = "Show the browser." };
var output = new Option<DirectoryInfo?>("--out") { Description = "Where report.json and the artifacts go. Default: ./.probe-web/<timestamp>." };
var timeout = new Option<int>("--timeout") { Description = "The whole run's limit, in minutes.", DefaultValueFactory = _ => 15 };
var profiles = new Option<DirectoryInfo?>("--profiles") { Description = "A folder of stack-profile JSON files that replace or add to the built-in ones." };

Command WithCommon(Command command)
{
    command.Options.Add(headed);
    command.Options.Add(output);
    command.Options.Add(timeout);
    command.Options.Add(profiles);
    return command;
}

CliSettings Settings(ParseResult r) => new(
    r.GetValue(headed),
    r.GetValue(output)?.FullName,
    TimeSpan.FromMinutes(Math.Max(1, r.GetValue(timeout))),
    r.GetValue(profiles)?.FullName);

var detect = new Command("detect", "Which stack profile fits the repository, with the candidates in a monorepo.") { repo, profiles };
detect.SetAction(r => new CliCommands(Console.Out, Settings(r)).Detect(r.GetValue(repo)!.FullName));

var dryRun = WithCommon(new Command("dry-run", "Try a stack profile's defaults on a repository with no contract, and list the real gaps.") { repo, profile, dir });
dryRun.SetAction((r, ct) => new CliCommands(Console.Out, Settings(r)).DryRunAsync(r.GetValue(repo)!.FullName, r.GetValue(profile), r.GetValue(dir), ct));

var gate = WithCommon(new Command("gate", "Run the Testability Gate: is this app testable?") { repo, contract, steps });
gate.SetAction((r, ct) => new CliCommands(Console.Out, Settings(r)).GateAsync(r.GetValue(repo)!.FullName, r.GetValue(contract)?.FullName, r.GetValue(steps)!, smoke: false, ct));

var smoke = WithCommon(new Command("smoke", "Run Gate steps 1-6, as the factory does on every task.") { repo, contract });
smoke.SetAction((r, ct) => new CliCommands(Console.Out, Settings(r)).GateAsync(r.GetValue(repo)!.FullName, r.GetValue(contract)?.FullName, "1-6", smoke: true, ct));

var scenarioFiles = new Option<string[]>("--scenarios")
{
    Description = "Scenario JSON files, folders of them, or a pattern such as scenarios/*.json.",
    Required = true,
    AllowMultipleArgumentsPerToken = true,
};
var scenarios = WithCommon(new Command("scenarios", "Run fixed-step scenarios: does this change work?") { repo, contract, scenarioFiles });
scenarios.SetAction((r, ct) => new CliCommands(Console.Out, Settings(r)).ScenariosAsync(r.GetValue(repo)!.FullName, r.GetValue(contract)?.FullName, r.GetValue(scenarioFiles)!, ct));

var root = new RootCommand("Deterministic browser verification of web apps (Litos.Probe.Web).") { detect, dryRun, gate, smoke, scenarios };
return await root.Parse(args).InvokeAsync();
