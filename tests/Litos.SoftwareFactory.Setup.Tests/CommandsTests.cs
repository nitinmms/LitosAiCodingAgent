namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>
/// The litos-factory commands against fake programs, machine and host (§4.1, §4.3): preflight and its
/// messages, the settings file, starting Docker as FACTORY_START_DOCKER says, and uninstall.
/// </summary>
public sealed class CommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-setup-commands-").FullName;
    private readonly FakeRunner _runner = new();
    private readonly FakeMachine _machine = new();
    private readonly FakeHost _host = new();
    private readonly FakeInterface _ui = new();
    private readonly StringWriter _output = new();
    private readonly FactoryPaths _paths;

    public CommandsTests()
    {
        _paths = new FactoryPaths(Path.Combine(_root, "install"), Path.Combine(_root, "config"), Path.Combine(_root, "local"));
        Directory.CreateDirectory(_paths.InstallDirectory);
        Directory.CreateDirectory(_paths.LocalAppData);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Commands Commands() => new(_paths, _runner, _machine, _ui, _output, _ => _host, startLimit: TimeSpan.FromMilliseconds(200));

    private string DataDirectory => Path.Combine(_paths.LocalAppData, FactoryPaths.ProductFolder);

    private Task<int> SetupAsync(bool fromInstaller = false) => Commands().SetupAsync(fromInstaller, CancellationToken.None);

    // ---- setup ----

    [Fact]
    public async Task Setup_WritesTheSettings_StartsTheDatabase_RegistersTheTask_StartsTheFactory_AndShowsThePassword()
    {
        _machine.TakenPorts.Add(5180);

        Assert.Equal(0, await SetupAsync());

        var env = FactoryEnv.Load(_paths.EnvFile);
        Assert.Equal("http://127.0.0.1:5181", env.Url);
        Assert.Equal("5433", env[FactoryEnv.DbPort]);
        Assert.Equal(DataDirectory.Replace('\\', '/'), env.DataDirectory);
        Assert.Equal(@"C:\Windows;C:\tools", env[FactoryEnv.ToolchainPath]);
        Assert.True(FactoryPaths.IsOurDataDirectory(DataDirectory));
        Assert.Contains(env[FactoryEnv.AdminPassword]!, File.ReadAllText(_paths.FirstSignInFile));

        Assert.Equal(1, _runner.Count("docker.exe compose -f"));
        Assert.Contains(_runner.Calls, c => c.StartsWith("docker.exe compose -f") && c.EndsWith("up -d --wait"));
        Assert.Contains($"Litos.SoftwareFactory.Host.exe --env-file {_paths.EnvFile} --migrate", _runner.Calls);
        Assert.Equal(1, _runner.Count("schtasks.exe /Create /TN Litos Software Factory"));
        Assert.Equal(1, _runner.Count("schtasks.exe /Run /TN Litos Software Factory"));
        Assert.Equal(["http://127.0.0.1:5181", _paths.FirstSignInFile], _machine.Opened);
        Assert.Empty(_ui.Failures);
    }

    [Fact]
    public async Task Setup_FromTheInstaller_OpensNothing_TheFinishPageDoes()
    {
        Assert.Equal(0, await SetupAsync(fromInstaller: true));

        Assert.Empty(_machine.Opened);
        Assert.True(File.Exists(_paths.FirstSignInFile));
    }

    [Fact]
    public async Task Setup_AgainKeepsTheSettings_TheDatabaseKeepsItsFirstPasswords()
    {
        await SetupAsync();
        var first = FactoryEnv.Load(_paths.EnvFile);

        _machine.TakenPorts.Add(5180);
        Assert.Equal(0, await SetupAsync());

        var second = FactoryEnv.Load(_paths.EnvFile);
        Assert.Equal(first["FACTORY_DB_RUNTIME_PASSWORD"], second["FACTORY_DB_RUNTIME_PASSWORD"]);
        Assert.Equal(first[FactoryEnv.AdminPassword], second[FactoryEnv.AdminPassword]);
        Assert.Equal(first.Url, second.Url);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task WithoutDocker_SetupSaysToInstallIt_AndFromTheInstallerDoesNotFailTheInstall(bool fromInstaller, int exitCode)
    {
        _machine.Docker = null;

        Assert.Equal(exitCode, await SetupAsync(fromInstaller));

        var (message, link) = Assert.Single(_ui.Failures);
        Assert.StartsWith("Docker isn't installed.", message);
        Assert.Equal(Setup.Commands.DockerInstallLink, link);
        Assert.False(File.Exists(_paths.EnvFile));
    }

    [Fact]
    public async Task WithoutGit_SetupSaysToInstallIt()
    {
        _runner.Answer("git --version", FakeRunner.Fails("'git' is not recognized"));

        Assert.Equal(1, await SetupAsync());

        Assert.Equal(Setup.Commands.GitInstallLink, Assert.Single(_ui.Failures).Link);
    }

    [Fact]
    public async Task ADatabaseThatWillNotStart_IsReported_AndNothingIsStarted()
    {
        _runner.Answer("docker.exe compose -f", FakeRunner.Fails("Bind for 127.0.0.1:5433 failed: port is already allocated"));

        Assert.Equal(1, await SetupAsync());

        Assert.Equal("The database didn't start: Bind for 127.0.0.1:5433 failed: port is already allocated", Assert.Single(_ui.Failures).Message);
        Assert.Equal(0, _runner.Count("schtasks.exe /Run"));
    }

    [Fact]
    public async Task AFactoryThatDoesNotBecomeReady_SaysWhereItsLogsAre()
    {
        _host.Ready = false;

        Assert.Equal(1, await SetupAsync());

        Assert.Contains(FactoryPaths.LogDirectory(FactoryEnv.Load(_paths.EnvFile).DataDirectory), Assert.Single(_ui.Failures).Message);
    }

    // ---- starting Docker ----

    [Fact]
    public async Task DockerNotRunning_SetupAsks_AndAlwaysIsRemembered()
    {
        _runner.Answer("docker.exe info", FakeRunner.Fails("cannot connect"), new ProcessResult(0, "28.0", ""));
        _ui.DockerAnswer = (true, true);

        Assert.Equal(0, await SetupAsync());

        Assert.Equal(1, _ui.DockerQuestions);
        Assert.Single(_machine.Started);
        Assert.Equal(StartDockerPolicy.Always, FactoryEnv.Load(_paths.EnvFile).StartDockerPolicy);
    }

    [Fact]
    public async Task DockerNotRunning_AndThePersonSaysNo_SetupSaysToStartIt()
    {
        _runner.Answer("docker.exe info", FakeRunner.Fails("cannot connect"));
        _ui.DockerAnswer = (false, false);

        Assert.Equal(1, await SetupAsync());

        Assert.Empty(_machine.Started);
        Assert.StartsWith("Docker isn't running.", Assert.Single(_ui.Failures).Message);
    }

    [Fact]
    public async Task DockerThatDoesNotAnswerAfterStarting_IsReported()
    {
        _runner.Answer("docker.exe info", FakeRunner.Fails("cannot connect"));
        _ui.DockerAnswer = (true, false);

        Assert.Equal(1, await SetupAsync());

        Assert.StartsWith("Docker Desktop was started but didn't answer", Assert.Single(_ui.Failures).Message);
    }

    [Theory]
    [InlineData("always", true, 0)]
    [InlineData("ask", false, 1)]
    [InlineData("never", false, 1)]
    public async Task Start_StartsDockerOnlyWhenTheSettingSaysAlways_AndNeverAsks(string setting, bool started, int exitCode)
    {
        await SetupAsync();
        var env = FactoryEnv.Load(_paths.EnvFile);
        env.Set(FactoryEnv.StartDocker, setting);
        env.Save(_paths.EnvFile);
        _runner.Answer("docker.exe info", FakeRunner.Fails("cannot connect"), new ProcessResult(0, "28.0", ""));

        Assert.Equal(exitCode, await Commands().StartAsync(CancellationToken.None));

        Assert.Equal(started, _machine.Started.Count == 1);
        Assert.Equal(0, _ui.DockerQuestions);
    }

    [Fact]
    public async Task Open_AsksBeforeStartingDocker_WhenTheSettingIsAsk()
    {
        await SetupAsync();
        _runner.Answer("docker.exe info", FakeRunner.Fails("cannot connect"), new ProcessResult(0, "28.0", ""));
        _ui.DockerAnswer = (true, false);

        Assert.Equal(0, await Commands().OpenAsync(CancellationToken.None));

        Assert.Equal(1, _ui.DockerQuestions);
        Assert.Single(_machine.Started);
        Assert.Equal(StartDockerPolicy.Ask, FactoryEnv.Load(_paths.EnvFile).StartDockerPolicy);
    }

    // ---- open ----

    [Fact]
    public async Task Open_BeforeSetup_SetsUp()
    {
        Assert.Equal(0, await Commands().OpenAsync(CancellationToken.None));

        Assert.True(File.Exists(_paths.EnvFile));
    }

    [Fact]
    public async Task Open_StopsShowingTheFirstPassword_OnceItHasBeenChanged()
    {
        await SetupAsync();
        _machine.Opened.Clear();
        _host.MustChangePassword = false;

        await Commands().OpenAsync(CancellationToken.None);

        Assert.Equal(["http://127.0.0.1:5180"], _machine.Opened);
        Assert.False(File.Exists(_paths.FirstSignInFile));
    }

    // ---- run ----

    [Fact]
    public async Task Run_WithoutSettings_SaysToSetUp_InsteadOfCrashing()
    {
        Assert.Equal(1, await Commands().RunAsync(CancellationToken.None));

        Assert.StartsWith("Litos Software Factory isn't set up yet.", Assert.Single(_ui.Failures).Message);
    }

    // ---- uninstall ----

    [Fact]
    public async Task Uninstall_RemovesTheStartAtSignIn_AndKeepsTheSettingsDataAndDatabase()
    {
        await SetupAsync();

        Assert.Equal(0, await Commands().UninstallAsync(purge: false, CancellationToken.None));

        Assert.Equal(1, _runner.Count("schtasks.exe /Delete /TN Litos Software Factory"));
        Assert.True(File.Exists(_paths.EnvFile));
        Assert.True(Directory.Exists(DataDirectory));
        Assert.DoesNotContain(_runner.Calls, c => c.EndsWith("down --volumes"));
        Assert.Contains(Setup.Commands.VolumeName, _output.ToString());
    }

    [Fact]
    public async Task Purge_RemovesTheDatabase_ThenTheDataAndSettings()
    {
        await SetupAsync();

        Assert.Equal(0, await Commands().UninstallAsync(purge: true, CancellationToken.None));

        Assert.Contains(_runner.Calls, c => c.EndsWith("down --volumes"));
        Assert.False(Directory.Exists(DataDirectory));
        Assert.False(Directory.Exists(_paths.ConfigDirectory));
    }

    [Fact]
    public async Task Purge_WhenTheDatabaseCannotBeRemoved_KeepsTheSettings_WhichHoldItsPasswords()
    {
        await SetupAsync();
        _runner.Answer("docker.exe compose -f", FakeRunner.Fails("Cannot connect to the Docker daemon"));

        Assert.Equal(1, await Commands().UninstallAsync(purge: true, CancellationToken.None));

        Assert.True(File.Exists(_paths.EnvFile));
        Assert.True(Directory.Exists(DataDirectory));
        Assert.Contains($"docker volume rm {Setup.Commands.VolumeName}", _output.ToString());
    }

    [Fact]
    public async Task Purge_KeepsADataFolderSetupDidNotCreate()
    {
        await SetupAsync();
        File.Delete(Path.Combine(DataDirectory, FactoryPaths.InstallMarker));

        Assert.Equal(0, await Commands().UninstallAsync(purge: true, CancellationToken.None));

        Assert.True(Directory.Exists(DataDirectory));
        Assert.False(Directory.Exists(_paths.ConfigDirectory));
        Assert.Contains("setup did not create it", _output.ToString());
    }
}
