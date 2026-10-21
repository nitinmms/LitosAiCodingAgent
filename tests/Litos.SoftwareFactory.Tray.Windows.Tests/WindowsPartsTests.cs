using System.Drawing;

namespace Litos.SoftwareFactory.Tray.Windows.Tests;

public sealed class TrayIconsTests
{
    private static bool Has(Bitmap bitmap, Color color) =>
        Enumerable.Range(0, bitmap.Width).Any(x => Enumerable.Range(0, bitmap.Height).Any(y =>
        {
            var pixel = bitmap.GetPixel(x, y);
            return pixel.A > 200 && Math.Abs(pixel.R - color.R) < 12 && Math.Abs(pixel.G - color.G) < 12 && Math.Abs(pixel.B - color.B) < 12;
        }));

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void EachState_IsDrawnAtTheSizeAsked_WithItsColour(int size)
    {
        using var idle = TrayIcons.Draw(new IconState(IconKind.Idle), size);
        using var awaiting = TrayIcons.Draw(new IconState(IconKind.Awaiting, 3), size);
        using var offline = TrayIcons.Draw(new IconState(IconKind.Offline), size);
        using var signIn = TrayIcons.Draw(new IconState(IconKind.SignIn), size);

        Assert.Equal((size, size), (idle.Width, idle.Height));
        Assert.True(Has(idle, TrayIcons.Petrol));
        Assert.False(Has(idle, TrayIcons.Amber));
        Assert.True(Has(awaiting, TrayIcons.Amber));
        Assert.True(Has(offline, TrayIcons.Idle));
        Assert.False(Has(offline, TrayIcons.Petrol));
        Assert.True(Has(signIn, TrayIcons.Bad));
        // The corners stay transparent, so the icon sits on any taskbar.
        Assert.Equal(0, idle.GetPixel(0, size - 1).A);
    }

    [Fact]
    public void Icons_AreMadeOncePerStateAndSize()
    {
        using var icons = new TrayIcons();

        var first = icons.For(new IconState(IconKind.Awaiting, 2), 16);

        Assert.Same(first, icons.For(new IconState(IconKind.Awaiting, 2), 16));
        Assert.NotSame(first, icons.For(new IconState(IconKind.Awaiting, 3), 16));
        Assert.NotSame(first, icons.For(new IconState(IconKind.Awaiting, 2), 32));
        // Ten or more share the "9+" icon.
        Assert.Same(icons.For(new IconState(IconKind.Awaiting, 12), 16), icons.For(new IconState(IconKind.Awaiting, 40), 16));
    }
}

public sealed class CredentialStoreTests : IDisposable
{
    // A prefix of its own, so a run never touches a real tray app's token.
    private readonly CredentialStore _store = new($"Litos Factory Tray test {Guid.NewGuid():n}: ");
    private readonly Uri _address = new("https://factory.acme.internal/");

    public void Dispose() => _store.Delete(_address);

    [Fact]
    public void AToken_IsKeptPerAddress_AndRemoved()
    {
        Assert.Null(_store.Read(_address));

        _store.Write(_address, "lsf_4b1e0c7a9d2f88aa31-Ω");

        Assert.Equal("lsf_4b1e0c7a9d2f88aa31-Ω", _store.Read(_address));
        Assert.Null(_store.Read(new Uri("https://other.acme.internal/")));

        _store.Write(_address, "lsf_replaced");
        Assert.Equal("lsf_replaced", _store.Read(_address));

        _store.Delete(_address);
        Assert.Null(_store.Read(_address));
        // Deleting what is not there is not an error.
        _store.Delete(_address);
    }

    [Fact]
    public void TheEntry_IsNamedForThePerson_InCredentialManager()
    {
        Assert.Equal("Litos Factory Tray: https://factory.acme.internal/", new CredentialStore().TargetOf(new Uri("https://factory.acme.internal/#/board")));
    }
}

public sealed class WindowsDesktopTests
{
    [Theory]
    [InlineData(1, false)] // not present: locked, or the screen saver
    [InlineData(2, true)] // busy: a full-screen app
    [InlineData(3, true)] // Direct3D full screen
    [InlineData(4, true)] // presentation mode
    [InlineData(5, false)] // accepting notifications
    [InlineData(6, true)] // quiet time
    [InlineData(7, true)] // an app holding notifications back
    public void Busy_IsWhenWindowsHoldsNotificationsBack(int state, bool busy)
    {
        Assert.Equal(busy, WindowsDesktop.IsBusyState(state));
    }

    [Fact]
    public void StartAtSignIn_IsAValueInTheUsersRunKey()
    {
        var desktop = new WindowsDesktop($"LitosFactoryTrayTest{Guid.NewGuid():n}");
        try
        {
            Assert.False(desktop.StartsAtSignIn());
            desktop.SetStartAtSignIn(true);
            Assert.True(desktop.StartsAtSignIn());
            Assert.EndsWith("\" --background", WindowsDesktop.StartCommand);
        }
        finally
        {
            desktop.SetStartAtSignIn(false);
        }

        Assert.False(desktop.StartsAtSignIn());
    }
}

public sealed class LocalFactoryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("litos-tray-local-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private LocalFactory Local => new(_folder, Path.Combine(_folder, "factory.env"));

    [Fact]
    public void ItIsInstalled_WhenLitosFactorySitsBesideTheTrayApp()
    {
        Assert.False(Local.IsInstalled);

        File.WriteAllText(Path.Combine(_folder, "litos-factory.exe"), "");

        Assert.True(Local.IsInstalled);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS=http://127.0.0.1:5181", "http://127.0.0.1:5181/")]
    [InlineData("ASPNETCORE_URLS=\"http://127.0.0.1:5180/;http://[::1]:5180\"", "http://127.0.0.1:5180/")]
    [InlineData("FACTORY_DB_PORT=5434", null)]
    public void ItsAddress_ComesFromFactoryEnv(string line, string? expected)
    {
        File.WriteAllLines(Path.Combine(_folder, "factory.env"), ["# generated by litos-factory setup", line]);

        Assert.Equal(expected, Local.Address());
    }

    [Fact]
    public void NoFactoryEnv_NoAddress()
    {
        Assert.Null(Local.Address());
    }

    [Fact]
    public void StartAndStop_RunWindowless_StatusAndDoctor_InAConsoleThatStaysOpen()
    {
        var start = Local.Command(MenuAction.Start);
        Assert.Equal((Path.Combine(_folder, "litos-factory-w.exe"), "start", false), (start.FileName, start.Arguments, start.UseShellExecute));
        Assert.Equal("stop", Local.Command(MenuAction.Stop).Arguments);

        var doctor = Local.Command(MenuAction.Doctor);
        Assert.EndsWith("cmd.exe", doctor.FileName);
        Assert.Equal($"/k \"\"{Path.Combine(_folder, "litos-factory.exe")}\" doctor\"", doctor.Arguments);
        Assert.EndsWith(" status\"", Local.Command(MenuAction.Status).Arguments);
        Assert.Throws<ArgumentOutOfRangeException>(() => Local.Command(MenuAction.Quit));
    }
}
