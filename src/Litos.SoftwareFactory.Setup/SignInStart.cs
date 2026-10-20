using System.Security;
using System.Security.Principal;

namespace Litos.SoftwareFactory.Setup;

/// <summary>
/// Starts the factory when this user signs in (§5.3): a per-user Scheduled Task running
/// <c>litos-factory-w.exe run</c> 30 s after sign-in, restarted if it fails. Windows refuses an
/// at-logon task to some accounts without elevation; the fallback is the user's Run key, which
/// starts the same command with no delay or restart (run waits for Docker and supervises the host
/// itself, so neither is essential). Not a Windows Service: on a desktop the factory must run as
/// the signed-in user, with their PATH, Docker and git (§5.2).
/// </summary>
public sealed class SignInStart(IProcessRunner runner, IMachine machine, FactoryPaths paths)
{
    public const string TaskName = "Litos Software Factory";

    public enum Kind { None, ScheduledTask, RunKey }

    /// <summary>Registers the start, replacing an earlier one; says which kind it became.</summary>
    public async Task<(Kind Kind, string? Problem)> RegisterAsync(CancellationToken ct)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"litos-factory-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks reads task XML as UTF-16, as the declaration says.
            await File.WriteAllTextAsync(xml, TaskXml(WindowsIdentity.GetCurrent().Name, paths.WindowlessExecutable, paths.InstallDirectory), System.Text.Encoding.Unicode, ct);
            var created = await runner.RunAsync("schtasks.exe", ["/Create", "/TN", TaskName, "/XML", xml, "/F"], TimeSpan.FromSeconds(30), ct);
            if (created.Succeeded)
            {
                machine.SetRunKey(TaskName, null);
                return (Kind.ScheduledTask, null);
            }

            machine.SetRunKey(TaskName, $"\"{paths.WindowlessExecutable}\" run");
            return (Kind.RunKey, created.Message);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    public async Task<Kind> FindAsync(CancellationToken ct)
    {
        if ((await runner.RunAsync("schtasks.exe", ["/Query", "/TN", TaskName], TimeSpan.FromSeconds(30), ct)).Succeeded)
            return Kind.ScheduledTask;
        return machine.HasRunKey(TaskName) ? Kind.RunKey : Kind.None;
    }

    /// <summary>Starts the supervisor now through the task, so it is nobody's child: started by the
    /// installer directly, it would belong to the installer's job and could end with it.</summary>
    public async Task<bool> RunNowAsync(CancellationToken ct) =>
        (await runner.RunAsync("schtasks.exe", ["/Run", "/TN", TaskName], TimeSpan.FromSeconds(30), ct)).Succeeded;

    public async Task RemoveAsync(CancellationToken ct)
    {
        await runner.RunAsync("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], TimeSpan.FromSeconds(30), ct);
        machine.SetRunKey(TaskName, null);
    }

    /// <summary>
    /// The task: at this user's sign-in after 30 s, as this user without elevation and only while
    /// they are signed in; restarted 3 times a minute apart after a failure; no time limit; normal
    /// priority (a task's default, 7, would run every build below normal).
    /// </summary>
    internal static string TaskXml(string userId, string executable, string workingDirectory)
    {
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Litos Software Factory when you sign in, once Docker is running.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(executable)}</Command>
                  <Arguments>run</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(workingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
