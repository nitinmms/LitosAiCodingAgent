using Litos.Api.Channels.Telegram;
using Litos.Tools.Shell;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramConfigStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");
    private string StateFilePath => Path.Combine(_tempDir, "telegram.json");

    [Fact]
    public void Constructor_NoFileOnDisk_StartsEmpty()
    {
        var store = new TelegramConfigStore(StateFilePath);

        Assert.False(store.Current.Enabled);
        Assert.Empty(store.Current.ToolPermissions);
        Assert.Empty(store.Current.LinkedChats);
    }

    [Fact]
    public void Update_PersistsToDisk_AndIsReloadedByANewStore()
    {
        var store = new TelegramConfigStore(StateFilePath);

        store.Update(c => c with
        {
            Enabled = true,
            ToolPermissions = new Dictionary<string, ToolPermission> { ["shell"] = ToolPermission.Ask },
            LinkedChats = [new LinkedChat(123456789, "session-1", DateTimeOffset.UtcNow)],
        });

        Assert.True(File.Exists(StateFilePath));

        var reloaded = new TelegramConfigStore(StateFilePath);
        Assert.True(reloaded.Current.Enabled);
        Assert.Equal(ToolPermission.Ask, reloaded.Current.PermissionFor("shell"));
        Assert.Single(reloaded.Current.LinkedChats);
        Assert.Equal(123456789, reloaded.Current.LinkedChats[0].ChatId);
    }

    [Fact]
    public void Constructor_OldAllowedToolsShapeOnDisk_MigratesToAskPermissions_PreservingLinkedChats()
    {
        // Pre-ToolPermission telegram.json files (AllowedTools: string[]) must still load after
        // upgrade rather than silently reset to Empty (which would also drop LinkedChats — losing
        // an already-paired Telegram device, not just the tool settings).
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(StateFilePath, """
            {
              "Enabled": true,
              "AllowedTools": ["shell", "write_file"],
              "LinkedChats": [{"ChatId": 123456789, "SessionId": "session-1", "LinkedAt": "2026-01-01T00:00:00+00:00"}]
            }
            """);

        var store = new TelegramConfigStore(StateFilePath);

        Assert.True(store.Current.Enabled);
        Assert.Equal(ToolPermission.Ask, store.Current.PermissionFor("shell"));
        Assert.Equal(ToolPermission.Ask, store.Current.PermissionFor("write_file"));
        Assert.Equal(ToolPermission.Deny, store.Current.PermissionFor("edit_file"));
        Assert.Single(store.Current.LinkedChats);
        Assert.Equal(123456789, store.Current.LinkedChats[0].ChatId);
    }

    [Fact]
    public void Update_MalformedFileOnDisk_FallsBackToEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(StateFilePath, "{ not valid json");

        var store = new TelegramConfigStore(StateFilePath);

        Assert.False(store.Current.Enabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
