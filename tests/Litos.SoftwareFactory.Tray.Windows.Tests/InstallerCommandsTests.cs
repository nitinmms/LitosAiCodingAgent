namespace Litos.SoftwareFactory.Tray.Windows.Tests;

public sealed class InstallerCommandsTests
{
    private sealed class FakeDesktop : IDesktop
    {
        public bool StartAtSignIn { get; private set; }

        public bool IsBusy() => false;

        public void Open(Uri page) { }

        public void Play(AlertSound sound) { }

        public bool StartsAtSignIn() => StartAtSignIn;

        public void SetStartAtSignIn(bool on) => StartAtSignIn = on;
    }

    [Fact]
    public void ANewInstall_StartsAtSignIn()
    {
        var desktop = new FakeDesktop();

        InstallerCommands.Register(desktop, new TraySettings());

        Assert.True(desktop.StartAtSignIn);
    }

    [Fact]
    public void AnUpgrade_KeepsAStartAtSignInThePersonTurnedOff()
    {
        var desktop = new FakeDesktop();

        InstallerCommands.Register(desktop, new TraySettings { StartAtSignIn = false });

        Assert.False(desktop.StartAtSignIn);
    }

    [Fact]
    public void Uninstalling_StopsTheStartAtSignIn()
    {
        var desktop = new FakeDesktop();
        desktop.SetStartAtSignIn(true);

        InstallerCommands.Unregister(desktop);

        Assert.False(desktop.StartAtSignIn);
    }
}

public sealed class PurgeTests : IDisposable
{
    private readonly CredentialStore _tokens = new($"Litos Factory Tray purge test {Guid.NewGuid():n}: ");
    private readonly string _folder = Directory.CreateTempSubdirectory("litos-tray-purge-").FullName;

    public void Dispose()
    {
        _tokens.DeleteAll();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void APurge_RemovesTheSettings_AndEveryFactorysToken()
    {
        var settings = Path.Combine(_folder, "tray.json");
        new TraySettings { Address = "https://factory.acme.internal/" }.Save(settings);
        _tokens.Write(new Uri("https://factory.acme.internal/"), "lsf_one");
        _tokens.Write(new Uri("http://127.0.0.1:5181/"), "lsf_two");

        InstallerCommands.Purge(settings, _tokens);

        Assert.False(File.Exists(settings));
        Assert.Null(_tokens.Read(new Uri("https://factory.acme.internal/")));
        Assert.Null(_tokens.Read(new Uri("http://127.0.0.1:5181/")));
    }

    [Fact]
    public void APurge_AfterTheFactorysOwn_FindsNothingLeft_AndIsFine()
    {
        // litos-factory uninstall --purge removed the config folder, tray.json with it.
        Directory.Delete(_folder, recursive: true);

        InstallerCommands.Purge(Path.Combine(_folder, "tray.json"), _tokens);

        Assert.Equal(0, _tokens.DeleteAll());
    }

    [Fact]
    public void DeleteAll_TakesOnlyItsOwnEntries()
    {
        var other = new CredentialStore($"Litos Factory Tray purge test {Guid.NewGuid():n}: ");
        try
        {
            other.Write(new Uri("http://127.0.0.1:5180/"), "lsf_keep");
            _tokens.Write(new Uri("http://127.0.0.1:5180/"), "lsf_go");

            Assert.Equal(1, _tokens.DeleteAll());

            Assert.Equal("lsf_keep", other.Read(new Uri("http://127.0.0.1:5180/")));
        }
        finally
        {
            other.DeleteAll();
        }
    }
}

public sealed class TrayInstanceTests
{
    private readonly string _mutex = $@"Local\LitosFactoryTrayTest{Guid.NewGuid():n}";
    private readonly string _quit = $@"Local\LitosFactoryTrayTest{Guid.NewGuid():n}.Quit";

    [Fact]
    public void Quitting_WhenNoneRuns_IsDone()
    {
        Assert.False(TrayInstance.IsRunning(_mutex));
        Assert.True(TrayInstance.Quit(TimeSpan.FromSeconds(1), _mutex, _quit));
    }

    [Fact]
    public async Task Quitting_AsksTheRunningCopy_AndWaitsUntilItHasGone()
    {
        var running = new TaskCompletionSource();
        var copy = new Thread(() =>
        {
            // What Program.cs does: hold the mutex, wait for the quit event, let go.
            using var mutex = new Mutex(true, _mutex, out _);
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, _quit);
            running.SetResult();
            quit.WaitOne(TimeSpan.FromSeconds(10));
            Thread.Sleep(200);
            mutex.ReleaseMutex();
        });
        copy.Start();
        await running.Task;
        Assert.True(TrayInstance.IsRunning(_mutex));

        Assert.True(TrayInstance.Quit(TimeSpan.FromSeconds(10), _mutex, _quit));

        Assert.False(TrayInstance.IsRunning(_mutex));
        copy.Join();
    }

    [Fact]
    public async Task ACopyThatWillNotQuit_IsReported()
    {
        var running = new TaskCompletionSource();
        var release = new ManualResetEventSlim();
        var copy = new Thread(() =>
        {
            // Holds the mutex, and never listens for the quit event.
            using var mutex = new Mutex(true, _mutex, out _);
            running.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            mutex.ReleaseMutex();
        });
        copy.Start();
        await running.Task;

        Assert.False(TrayInstance.Quit(TimeSpan.FromMilliseconds(300), _mutex, _quit));

        release.Set();
        copy.Join();
    }
}
