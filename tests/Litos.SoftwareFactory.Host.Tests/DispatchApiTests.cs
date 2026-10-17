using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Infrastructure.Backup;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Pausing dispatch (m4-architecture.md §8.1): nothing new starts until an Admin resumes it.</summary>
public sealed class DispatchApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<JsonElement> SetAsync(bool paused, string? reason = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        _host.PostAsync("api/admin/dispatch", new { paused, reason }, expected);

    [Fact]
    public async Task WhilePaused_ADelegatedTaskWaitsAndSaysWhy_AndStartsOnceResumed()
    {
        var paused = await SetAsync(true, "A backup is being taken.");
        Assert.False(paused.GetProperty("wasPaused").GetBoolean());
        Assert.Empty(paused.GetProperty("active").EnumerateArray());

        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        var waiting = await WaitForReasonAsync(threadId, "Dispatch is paused by an Admin: A backup is being taken.");
        Assert.Equal(LifecycleState.Queued, waiting.State);
        Assert.Equal(RunStatus.Queued, (await _host.ThreadAsync(threadId)).LatestRun!.Status);

        var resumed = await SetAsync(false);
        Assert.True(resumed.GetProperty("wasPaused").GetBoolean());
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
    }

    [Fact]
    public async Task Pausing_IsAuditedWithTheReason_KeptOverAReload_AndAskingAgainChangesNothing()
    {
        await SetAsync(true, "Backup.");
        var again = await SetAsync(true, "Backup.");
        Assert.True(again.GetProperty("wasPaused").GetBoolean());

        var audit = await _host.Store.ListAuditAsync(null, 20, default);
        Assert.Single(audit, a => a.Action == AuditActions.DispatchPause);

        // As after a restart: read again from the database.
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();
        await settings.LoadAsync(default);
        Assert.Equal((true, "Backup.", await _host.AdminIdAsync()), (settings.Dispatch.Paused, settings.Dispatch.Reason, settings.Dispatch.ChangedBy));

        var view = await _host.GetAsync("api/admin/dispatch");
        Assert.True(view.GetProperty("paused").GetBoolean());
        var counters = await _host.GetAsync("api/admin/counters");
        Assert.Equal(("Backup.", "admin"), (counters.GetProperty("dispatch").GetProperty("reason").GetString(), counters.GetProperty("dispatch").GetProperty("changedBy").GetString()));

        await SetAsync(false);
        Assert.Single(await _host.Store.ListAuditAsync(null, 20, default), a => a.Action == AuditActions.DispatchResume);
        Assert.Equal(JsonValueKind.Null, (await _host.GetAsync("api/admin/dispatch")).GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task AReasonTooLong_IsRefused()
    {
        var refused = await SetAsync(true, new string('x', DispatchSettings.MaxReasonChars + 1), HttpStatusCode.BadRequest);

        Assert.Contains("longer than", refused.GetProperty("error").GetString());
        Assert.False((await _host.GetAsync("api/dispatch")).GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task AMember_SeesWhetherItIsPaused_ButCannotChangeIt_OrSeeWhatIsWorking()
    {
        await _host.CreateMemberAsync("maria", await _host.RegisterProjectAsync());
        var maria = await _host.SignedInAsync("maria");
        await SetAsync(true, "Backup.");

        var seen = await maria.GetFromJsonAsync<JsonElement>("api/dispatch");
        Assert.Equal((true, "Backup."), (seen.GetProperty("paused").GetBoolean(), seen.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.String, seen.GetProperty("since").ValueKind);

        using var change = await maria.PostAsJsonAsync("api/admin/dispatch", new { paused = false });
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
        using var view = await maria.GetAsync("api/admin/dispatch");
        Assert.Equal(HttpStatusCode.Forbidden, view.StatusCode);
        Assert.True(_host.App.Services.GetRequiredService<FactorySettings>().Dispatch.Paused);
    }

    [Fact]
    public async Task AnyoneSignedOut_CannotAsk()
    {
        using var anonymous = _host.NewClient();

        using var response = await anonymous.GetAsync("api/dispatch");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<TaskThread> WaitForReasonAsync(Guid threadId, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var thread = (await _host.ThreadAsync(threadId)).Thread;
            if (thread.StateReason == reason)
                return thread;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The thread says \"{thread.StateReason}\" ({thread.State}), not \"{reason}\".");
            await Task.Delay(50);
        }
    }
}

/// <summary>A host started on data restored from a backup checks it against the backup (m4-architecture.md §8.3).</summary>
public sealed class RestoreCheckTests
{
    /// <summary>Writes a data directory, backs it up, and leaves the backup's manifest in it as a restore does.</summary>
    private static void Restored(FactoryOptions options, Action<string>? after = null)
    {
        var data = options.DataDirectory;
        Directory.CreateDirectory(Path.Combine(data, "runs", "r1"));
        File.WriteAllText(Path.Combine(data, "runs", "r1", "worker.log"), "started");
        Directory.CreateDirectory(Path.Combine(data, "projects", "p1"));
        File.WriteAllText(Path.Combine(data, "projects", "p1", "baseline.log"), "ok");

        var backup = Directory.CreateTempSubdirectory("litos-factory-backup-").FullName;
        File.WriteAllText(Path.Combine(backup, FactoryBackup.DumpName), "dump");
        FactoryBackup.WriteAsync(backup, data, "1.0.0+test", "M4Reflection", DateTimeOffset.UtcNow, default).GetAwaiter().GetResult();
        File.Copy(Path.Combine(backup, FactoryBackup.ManifestName), Path.Combine(data, FactoryBackup.RestoredManifestName));
        Directory.Delete(backup, recursive: true);
        after?.Invoke(data);
    }

    [Fact]
    public async Task OnDataNotRestored_NothingIsReported()
    {
        await using var host = await TestHost.StartAsync();

        Assert.Null(host.App.Services.GetRequiredService<RestoreCheck>().Report);
        Assert.Equal(JsonValueKind.Null, (await host.GetAsync("api/admin/counters")).GetProperty("restore").ValueKind);
    }

    [Fact]
    public async Task OnIntactRestoredData_ItReportsEveryFileHere()
    {
        await using var host = await TestHost.StartAsync(o => Restored(o));

        var restore = (await host.GetAsync("api/admin/counters")).GetProperty("restore");

        Assert.Equal(2, restore.GetProperty("filesChecked").GetInt32());
        Assert.Equal("1.0.0+test", restore.GetProperty("backupHostVersion").GetString());
        Assert.Empty(restore.GetProperty("problems").EnumerateArray());
        Assert.Empty(restore.GetProperty("unreadableSecrets").EnumerateArray());
    }

    [Fact]
    public async Task OnRestoredDataWithAFileMissing_AndASecretItsKeyRingCannotRead_ItSaysWhich_AndTheHostStillStarts()
    {
        await using var host = await TestHost.StartAsync(o => Restored(o, data => File.Delete(Path.Combine(data, "runs", "r1", "worker.log"))));
        // A secret protected with another key ring, as when the keys folder was not restored.
        await host.Store.SetSecretAsync(SecretNames.GitHub, "CfDJ8-not-from-this-key-ring", null, DateTimeOffset.UtcNow, default);
        await host.App.Services.GetRequiredService<FactorySettings>().LoadAsync(default);
        await host.App.Services.GetRequiredService<RestoreCheck>().RunAsync(default);

        var restore = (await host.GetAsync("api/admin/counters")).GetProperty("restore");

        Assert.Equal(["runs/r1/worker.log is missing"], restore.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal([SecretNames.GitHub], restore.GetProperty("unreadableSecrets").EnumerateArray().Select(p => p.GetString()));
    }
}

/// <summary>The host's backup commands, as the deploy scripts run them (m4-architecture.md §8).</summary>
public sealed class BackupCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-factory-backup-commands-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<string> BackupAsync()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(data, "keys"));
        File.WriteAllText(Path.Combine(data, "keys", "key.xml"), "<key/>");
        var backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, FactoryBackup.DumpName), "dump");
        await FactoryBackup.WriteAsync(backup, data, BackupCommands.HostVersion, "M4Reflection", DateTimeOffset.UtcNow, default);
        return backup;
    }

    [Fact]
    public async Task VerifyBackup_OfAnIntactBackup_SaysSo_AndExitsZero()
    {
        var backup = await BackupAsync();
        var output = new StringWriter();

        Assert.Equal(0, await BackupCommands.VerifyBackupAsync(backup, output, new StringWriter(), default));
        Assert.Contains("matches its manifest: the dump and 1 file(s)", output.ToString());
        Assert.Contains("M4Reflection", output.ToString());
    }

    [Fact]
    public async Task VerifyBackup_OfAChangedBackup_ListsWhat_AndExitsOne()
    {
        var backup = await BackupAsync();
        File.WriteAllText(Path.Combine(backup, "data", "keys", "key.xml"), "<other/>");
        var error = new StringWriter();

        Assert.Equal(1, await BackupCommands.VerifyBackupAsync(backup, new StringWriter(), error, default));
        Assert.Contains("keys/key.xml is 8 bytes, not 6", error.ToString());
    }

    [Fact]
    public async Task VerifyBackup_OfAFolderWithNoManifest_ExitsOne()
    {
        var error = new StringWriter();

        Assert.Equal(1, await BackupCommands.VerifyBackupAsync(_root, new StringWriter(), error, default));
        Assert.Contains("could not be checked", error.ToString());
    }

    [Fact]
    public async Task BackupData_WithoutTheHostsSettings_ExitsTwo()
    {
        var error = new StringWriter();

        Assert.Equal(2, await BackupCommands.BackupDataAsync(_root, new FactoryOptions(), new StringWriter(), error, default));
        Assert.Contains("--env-file", error.ToString());
    }
}
