using System.Text.Json;
using Litos.SoftwareFactory.Infrastructure.Backup;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Backup;

/// <summary>The backup's manifest: what is kept, what each file was, and the checks on a backup and on restored data (m4-architecture.md §8).</summary>
public sealed class FactoryBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "factory-backup-tests", Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data-dir");
    private string Backup => Path.Combine(_root, "backup");

    public FactoryBackupTests()
    {
        Write(Data, "keys/key-1.xml", "<key/>");
        Write(Data, "skills/3/tdd/SKILL.md", "# tdd");
        Write(Data, "runs/abc/worker.log", "started");
        Write(Data, "runs/abc/sessions/local/transcript.jsonl", "{}");
        Write(Data, "projects/p1/baseline-r1-1234/01-build.log", "ok");
        Write(Data, "attachments/t1/a1", "png");
        // Left out: made again, or held by GitHub.
        Write(Data, "workspaces/p1/README.md", "code");
        Write(Data, "reading/p1/README.md", "code");
        Write(Data, "venvs/p1/pyvenv.cfg", "home");
        Write(Backup, FactoryBackup.DumpName, "PGDMP-dump");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private Task<BackupManifest> WriteBackupAsync() => FactoryBackup.WriteAsync(Backup, Data, "1.0.0+abc", "20261016095818_M4Reflection", Now, default);

    [Fact]
    public async Task Write_CopiesTheKeptFolders_AndListsEachFileWithItsSizeAndHash()
    {
        var manifest = await WriteBackupAsync();

        Assert.Equal(
            ["attachments/t1/a1", "keys/key-1.xml", "projects/p1/baseline-r1-1234/01-build.log", "runs/abc/sessions/local/transcript.jsonl", "runs/abc/worker.log", "skills/3/tdd/SKILL.md"],
            manifest.Data.Select(f => f.Path));
        Assert.True(File.Exists(Path.Combine(Backup, "data", "runs", "abc", "worker.log")));
        Assert.False(Directory.Exists(Path.Combine(Backup, "data", "workspaces")));
        Assert.False(Directory.Exists(Path.Combine(Backup, "data", "venvs")));
        Assert.False(Directory.Exists(Path.Combine(Backup, "data", "reading")));

        var log = manifest.Data.Single(f => f.Path == "runs/abc/worker.log");
        Assert.Equal(7, log.Size);
        // SHA-256, lower-case hex.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("started"u8)), log.Sha256);
        Assert.Equal((FactoryBackup.DumpName, 10L), (manifest.Dump.Path, manifest.Dump.Size));
        Assert.Equal((1, Now, "1.0.0+abc", "20261016095818_M4Reflection"), (manifest.Format, manifest.CreatedAt, manifest.HostVersion, manifest.LastMigration));
    }

    [Fact]
    public async Task Manifest_ReadsBackAsWritten()
    {
        var written = await WriteBackupAsync();

        var read = FactoryBackup.ReadManifest(Path.Combine(Backup, FactoryBackup.ManifestName));

        Assert.Equal(written.Data, read.Data);
        Assert.Equal((written.Dump, written.LastMigration, written.CreatedAt), (read.Dump, read.LastMigration, read.CreatedAt));
    }

    [Fact]
    public async Task Write_RefusesWithoutTheDump_OrIntoAFolderThatHoldsABackup()
    {
        await WriteBackupAsync();
        await Assert.ThrowsAsync<IOException>(WriteBackupAsync);

        Directory.CreateDirectory(Path.Combine(_root, "other"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            FactoryBackup.WriteAsync(Path.Combine(_root, "other"), Data, "v", null, Now, default));
    }

    [Fact]
    public async Task Write_OfADataDirectoryWithNothingKept_ListsOnlyTheDump()
    {
        var empty = Path.Combine(_root, "empty-data");
        Directory.CreateDirectory(empty);

        var manifest = await FactoryBackup.WriteAsync(Backup, empty, "v", null, Now, default);

        Assert.Empty(manifest.Data);
        Assert.Empty(await FactoryBackup.VerifyBackupAsync(Backup, default));
    }

    [Fact]
    public async Task Verify_OfAnUntouchedBackup_FindsNothing()
    {
        await WriteBackupAsync();

        Assert.Empty(await FactoryBackup.VerifyBackupAsync(Backup, default));
    }

    [Fact]
    public async Task Verify_FindsAMissingFile_AChangedOne_AResizedOne_AndAChangedDump()
    {
        await WriteBackupAsync();
        File.Delete(Path.Combine(Backup, "data", "keys", "key-1.xml"));
        File.WriteAllText(Path.Combine(Backup, "data", "runs", "abc", "worker.log"), "STARTED"); // same size, other bytes
        File.WriteAllText(Path.Combine(Backup, "data", "skills", "3", "tdd", "SKILL.md"), "# tdd, longer");
        File.WriteAllText(Path.Combine(Backup, FactoryBackup.DumpName), "PGDMP-dumq");

        var problems = await FactoryBackup.VerifyBackupAsync(Backup, default);

        Assert.Equal(
            [
                (FactoryBackup.DumpName, "has changed (its SHA-256 differs)"),
                ("keys/key-1.xml", "is missing"),
                ("runs/abc/worker.log", "has changed (its SHA-256 differs)"),
                ("skills/3/tdd/SKILL.md", "is 13 bytes, not 5"),
            ],
            problems.Select(p => (p.Path, p.Problem)));
    }

    [Fact]
    public async Task VerifyData_OfARestoredDataDirectory_IgnoresFilesAddedSince()
    {
        var manifest = await WriteBackupAsync();
        var restored = Path.Combine(_root, "restored");
        CopyAll(Path.Combine(Backup, "data"), restored);
        Write(restored, "runs/new-run/worker.log", "a run after the restore");

        Assert.Empty(await FactoryBackup.VerifyDataAsync(restored, manifest, default));

        File.Delete(Path.Combine(restored, "projects", "p1", "baseline-r1-1234", "01-build.log"));
        Assert.Equal("projects/p1/baseline-r1-1234/01-build.log", Assert.Single(await FactoryBackup.VerifyDataAsync(restored, manifest, default)).Path);
    }

    [Fact]
    public async Task Verify_RefusesAPathOutsideTheFolder_HoweverTheManifestWasEdited()
    {
        var manifest = await WriteBackupAsync();
        var edited = manifest with { Data = [.. manifest.Data, new BackupFile("../data-dir/keys/key-1.xml", 6, manifest.Data[0].Sha256)] };

        var problem = Assert.Single(await FactoryBackup.VerifyDataAsync(Path.Combine(Backup, "data"), edited, default));

        Assert.Equal("is outside the folder being checked", problem.Problem);
    }

    [Fact]
    public void ReadManifest_RefusesWhatIsNotOne_AndAFormatItDoesNotRead()
    {
        var path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, "not json");
        Assert.Throws<InvalidDataException>(() => FactoryBackup.ReadManifest(path));

        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => FactoryBackup.ReadManifest(path));

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = 2, createdAt = Now, hostVersion = "v", lastMigration = (string?)null, dump = new { path = "d", size = 1, sha256 = "00" }, data = Array.Empty<object>(),
        }));
        Assert.Contains("format 2", Assert.Throws<InvalidDataException>(() => FactoryBackup.ReadManifest(path)).Message);
    }

    private static void CopyAll(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }
}
