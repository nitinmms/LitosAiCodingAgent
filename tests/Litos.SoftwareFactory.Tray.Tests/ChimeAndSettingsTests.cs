using System.Buffers.Binary;
using System.Text;

namespace Litos.SoftwareFactory.Tray.Tests;

public sealed class ChimeTests
{
    private static short[] Samples(byte[] wav) =>
        [.. Enumerable.Range(0, (wav.Length - 44) / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)))];

    [Theory]
    [InlineData(AlertSound.Ding, 1.6)]
    [InlineData(AlertSound.DingDong, 2.0)]
    [InlineData(AlertSound.Beeps, 1.0)]
    public void EachSound_IsAPlayableWav_OfItsLength(AlertSound sound, double seconds)
    {
        var wav = Chime.Render(sound)!;

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal((1, 1, Chime.SampleRate, 16), (
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)), BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)),
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)), BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34))));
        Assert.Equal((int)(seconds * Chime.SampleRate) * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Theory]
    [InlineData(AlertSound.Ding)]
    [InlineData(AlertSound.DingDong)]
    [InlineData(AlertSound.Beeps)]
    public void EachSound_IsAudible_NeverClips_AndEndsInSilence(AlertSound sound)
    {
        var samples = Samples(Chime.Render(sound)!);

        var peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, short.MaxValue / 10, (int)(short.MaxValue * 0.91));
        // The last 50 ms are near silence: no click when it stops.
        Assert.All(samples[^(Chime.SampleRate / 20)..], s => Assert.InRange(Math.Abs((int)s), 0, short.MaxValue / 50));
    }

    [Fact]
    public void TheSounds_Differ_AndNoneIsSilence()
    {
        Assert.Null(Chime.Render(AlertSound.None));
        Assert.NotEqual(Chime.Render(AlertSound.Ding), Chime.Render(AlertSound.DingDong));
        Assert.Equal(Chime.Render(AlertSound.Ding), Chime.Render(AlertSound.Ding));
    }
}

public sealed class TraySettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("litos-tray-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FilePath => Path.Combine(_folder, "sub", "tray.json");

    [Fact]
    public void ANewInstall_HasTheAgreedDefaults()
    {
        var settings = TraySettings.Load(FilePath);

        Assert.Equal(new TraySettings(), settings);
        Assert.Equal((null, AlertSound.Ding, ReminderInterval.Never, true, true, false),
            (settings.Address, settings.Sound, settings.Reminder, settings.SilentWhenBusy, settings.StartAtSignIn, settings.AllowPlainHttp));
    }

    [Fact]
    public void Settings_AreSaved_AndReadBack_WithEnumsByName()
    {
        var saved = new TraySettings
        {
            Address = "https://factory.acme.internal/",
            Sound = AlertSound.DingDong,
            Reminder = ReminderInterval.Every15Minutes,
            MutedUntil = new DateTimeOffset(2026, 10, 21, 15, 32, 0, TimeSpan.Zero),
        };

        saved.Save(FilePath);

        Assert.Equal(saved, TraySettings.Load(FilePath));
        Assert.Contains("\"sound\": \"DingDong\"", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void AFileThatCannotBeRead_GivesTheDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Equal(new TraySettings(), TraySettings.Load(FilePath));
    }

    [Fact]
    public void Muting_ForAWhile_OrUntilUnmuted()
    {
        var now = new DateTimeOffset(2026, 10, 21, 14, 32, 0, TimeSpan.Zero);

        Assert.False(new TraySettings().IsMuted(now));
        Assert.True(new TraySettings { Muted = true }.IsMuted(now));
        Assert.True(new TraySettings { MutedUntil = now.AddHours(1) }.IsMuted(now));
        Assert.False(new TraySettings { MutedUntil = now.AddHours(1) }.IsMuted(now.AddHours(1)));
    }

    [Theory]
    [InlineData(ReminderInterval.Never, 120, 1, false)]
    [InlineData(ReminderInterval.Every15Minutes, 14, 1, false)]
    [InlineData(ReminderInterval.Every15Minutes, 15, 1, true)]
    [InlineData(ReminderInterval.Every15Minutes, 30, 0, false)]
    [InlineData(ReminderInterval.Hourly, 59, 2, false)]
    [InlineData(ReminderInterval.Hourly, 60, 2, true)]
    public void AReminder_IsDue_WhileSomethingWaits_AnIntervalAfterTheLastRing(ReminderInterval interval, int minutes, int awaiting, bool due)
    {
        var lastRing = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(due, new TraySettings { Reminder = interval }.ReminderDue(lastRing, lastRing.AddMinutes(minutes), awaiting));
    }
}

public sealed class FactoryAddressTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5180", "http://127.0.0.1:5180/")]
    [InlineData("  http://localhost:5181/#/board  ", "http://localhost:5181/")]
    [InlineData("https://factory.acme.internal/#/threads/42?x=1", "https://factory.acme.internal/")]
    [InlineData("https://acme.internal/factory", "https://acme.internal/factory/")]
    [InlineData("http://[::1]:5180/", "http://[::1]:5180/")]
    public void APastedAddress_IsTidied(string input, string expected)
    {
        var (address, error) = FactoryAddress.Parse(input, allowPlainHttp: false);

        Assert.Null(error);
        Assert.Equal(expected, address!.ToString());
    }

    [Theory]
    [InlineData("", "Enter the factory's address")]
    [InlineData("factory.acme.internal", "Start the address with https:// or http://.")]
    [InlineData("ftp://factory.acme.internal", "That is not a web address.")]
    [InlineData("http://factory.acme.internal", "factory.acme.internal is another machine, and plain http:// would send your token unencrypted.")]
    public void AnAddressThatCannotBeUsed_SaysWhy(string input, string reason)
    {
        var (address, error) = FactoryAddress.Parse(input, allowPlainHttp: false);

        Assert.Null(address);
        Assert.StartsWith(reason, error);
    }

    [Fact]
    public void PlainHttpToAnotherMachine_WorksOnceAllowed()
    {
        Assert.Equal("http://factory.acme.internal/", FactoryAddress.Parse("http://factory.acme.internal", allowPlainHttp: true).Address!.ToString());
    }

    [Fact]
    public void TheWebAppsPages_AreUnderTheAddress()
    {
        var address = new Uri("https://acme.internal/factory/");
        var id = Guid.Parse("4b1e0c7a-9d2f-48aa-a31b-000000000001");

        Assert.Equal("https://acme.internal/factory/#/threads/4b1e0c7a-9d2f-48aa-a31b-000000000001", FactoryAddress.TaskPage(address, id).ToString());
        Assert.Equal("https://acme.internal/factory/#/board", FactoryAddress.Board(address).ToString());
        Assert.Equal("https://acme.internal/factory/#/tray", FactoryAddress.TrayPage(address).ToString());
    }
}
