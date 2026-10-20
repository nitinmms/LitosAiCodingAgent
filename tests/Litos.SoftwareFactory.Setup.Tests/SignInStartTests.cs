using System.Xml.Linq;

namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>The start at sign-in (§5.3): a Scheduled Task, or the Run key when Windows refuses the task.</summary>
public sealed class SignInStartTests
{
    [Fact]
    public void TheScheduledTask_StartsRunAtThisUsersSignIn_AndRestartsAfterAFailure()
    {
        var xml = SignInStart.TaskXml(@"PC\R&D <user>", @"C:\Users\me\AppData\Local\Programs\LitosSoftwareFactory\litos-factory-w.exe", @"C:\install");

        var task = XDocument.Parse(xml).Root!;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var logon = task.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!;
        Assert.Equal((@"PC\R&D <user>", "PT30S"), (logon.Element(ns + "UserId")!.Value, logon.Element(ns + "Delay")!.Value));

        var principal = task.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal(("InteractiveToken", "LeastPrivilege"), (principal.Element(ns + "LogonType")!.Value, principal.Element(ns + "RunLevel")!.Value));

        var settings = task.Element(ns + "Settings")!;
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("5", settings.Element(ns + "Priority")!.Value);
        Assert.Equal("3", settings.Element(ns + "RestartOnFailure")!.Element(ns + "Count")!.Value);

        var exec = task.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal((@"C:\Users\me\AppData\Local\Programs\LitosSoftwareFactory\litos-factory-w.exe", "run"), (exec.Element(ns + "Command")!.Value, exec.Element(ns + "Arguments")!.Value));
    }

    [Fact]
    public async Task ARefusedTask_FallsBackToTheRunKey_AndAnAcceptedOneClearsIt()
    {
        var runner = new FakeRunner();
        var machine = new FakeMachine();
        var start = new SignInStart(runner, machine, new FactoryPaths(@"C:\install", @"C:\config", @"C:\local"));

        runner.Answer("schtasks.exe /Create", new ProcessResult(1, "", "ERROR: Access is denied."));
        Assert.Equal((SignInStart.Kind.RunKey, "ERROR: Access is denied."), await start.RegisterAsync(CancellationToken.None));
        Assert.Equal(@"""C:\install\litos-factory-w.exe"" run", machine.RunKeys[SignInStart.TaskName]);
        runner.Answer("schtasks.exe /Query", new ProcessResult(1, "", "not found"));
        Assert.Equal(SignInStart.Kind.RunKey, await start.FindAsync(CancellationToken.None));

        runner.Answer("schtasks.exe /Create", new ProcessResult(0, "SUCCESS", ""));
        Assert.Equal((SignInStart.Kind.ScheduledTask, null), await start.RegisterAsync(CancellationToken.None));
        Assert.Empty(machine.RunKeys);
    }
}
