using Litos.Console.Terminal;
using Litos.Host;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for ApiKeysDialog.MergeConfig, the pure config-merge logic split out so it's testable
/// without a Terminal.Gui control tree or real filesystem I/O (mirrors Litos.Gui's
/// McpServersWindow.BuildDefinition split). Covers the two bugs the plan flagged in the original
/// SetupWizardDialog: LocalBaseUrl/ShellCommandTimeoutSeconds silently dropped on every save, and
/// blank fields not preserving existing values.
/// </summary>
public class ApiKeysDialogTests
{
    private static LitosConfig ExistingConfig() => new(
        DefaultProvider: "anthropic",
        DefaultModel: "claude-sonnet-5",
        LastWorkingDirectory: "/some/dir",
        ApiKeys: new Dictionary<string, string> { ["anthropic"] = "existing-key" },
        LocalBaseUrl: "http://localhost:1234/v1",
        ShellCommandTimeoutSeconds: 120);

    [Fact]
    public void MergeConfig_PreservesLocalBaseUrlAndShellTimeout_WhenNotTouched()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(current, new Dictionary<string, string>(), enteredLocalBaseUrl: null, defaultProvider: null);

        Assert.Equal("http://localhost:1234/v1", merged.LocalBaseUrl);
        Assert.Equal(120, merged.ShellCommandTimeoutSeconds);
    }

    [Fact]
    public void MergeConfig_BlankEnteredKey_KeepsExistingKey()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(current, new Dictionary<string, string>(), enteredLocalBaseUrl: null, defaultProvider: null);

        Assert.Equal("existing-key", merged.ApiKeys["anthropic"]);
    }

    [Fact]
    public void MergeConfig_NonBlankEnteredKey_OverwritesExistingKey()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(
            current, new Dictionary<string, string> { ["anthropic"] = "new-key" }, enteredLocalBaseUrl: null, defaultProvider: null);

        Assert.Equal("new-key", merged.ApiKeys["anthropic"]);
    }

    [Fact]
    public void MergeConfig_NewProviderKey_IsAddedAlongsideExisting()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(
            current, new Dictionary<string, string> { ["openai"] = "sk-openai" }, enteredLocalBaseUrl: null, defaultProvider: null);

        Assert.Equal("existing-key", merged.ApiKeys["anthropic"]);
        Assert.Equal("sk-openai", merged.ApiKeys["openai"]);
    }

    [Fact]
    public void MergeConfig_NonBlankLocalBaseUrl_OverwritesExisting()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(current, new Dictionary<string, string>(), enteredLocalBaseUrl: "http://new-host:8080/v1", defaultProvider: null);

        Assert.Equal("http://new-host:8080/v1", merged.LocalBaseUrl);
    }

    [Fact]
    public void MergeConfig_NullDefaultProvider_KeepsExistingDefaultProvider()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(current, new Dictionary<string, string>(), enteredLocalBaseUrl: null, defaultProvider: null);

        Assert.Equal("anthropic", merged.DefaultProvider);
    }

    [Fact]
    public void MergeConfig_GivenDefaultProvider_OverwritesExisting()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(current, new Dictionary<string, string>(), enteredLocalBaseUrl: null, defaultProvider: "openai");

        Assert.Equal("openai", merged.DefaultProvider);
    }

    [Fact]
    public void MergeConfig_NeverTouchesLastWorkingDirectoryOrDefaultModel()
    {
        var current = ExistingConfig();

        var merged = ApiKeysDialog.MergeConfig(
            current, new Dictionary<string, string> { ["anthropic"] = "new-key" }, enteredLocalBaseUrl: "http://x/v1", defaultProvider: "openai");

        Assert.Equal("/some/dir", merged.LastWorkingDirectory);
        Assert.Equal("claude-sonnet-5", merged.DefaultModel);
    }
}
