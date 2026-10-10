using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The Tools tab (m3-architecture.md §6).</summary>
public class ToolSettingsTests
{
    [Fact]
    public void TheDefaults_AreM2s_PtcOnAFiveMinuteShellAndNoWebSearch_AndValid()
    {
        var defaults = new ToolSettings();

        Assert.True(defaults.PtcByDefault);
        Assert.True(defaults.MembersMayChoosePtc);
        Assert.Equal(300, defaults.ShellTimeoutSeconds);
        Assert.False(defaults.WebSearchEnabled);
        Assert.Equal(WebSearchAccess.Off, defaults.WebSearch(keySet: true));
        Assert.Empty(defaults.Validate());
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3_601)]
    public void AShellLimitOutOfRange_IsRefused(int seconds)
    {
        Assert.Equal(
            ["The shell command time limit must be between 30 and 3600 seconds."],
            new ToolSettings { ShellTimeoutSeconds = seconds }.Validate());
    }

    [Fact]
    public void WebSearchOnReadOnlyTurns_NeedsWebSearchOn()
    {
        Assert.Equal(
            ["Web search on read-only turns needs web search to be on."],
            new ToolSettings { WebSearchOnReadOnlyTurns = true }.Validate());
        Assert.Empty(new ToolSettings { WebSearchEnabled = true, WebSearchOnReadOnlyTurns = true }.Validate());
    }

    [Theory]
    [InlineData(false, false, true, WebSearchAccess.Off)]
    [InlineData(true, false, false, WebSearchAccess.Off)] // on, but no key: nothing to search with
    [InlineData(true, false, true, WebSearchAccess.WorkTurns)]
    [InlineData(true, true, true, WebSearchAccess.AllTurns)]
    public void WebSearch_IsWhatTheSettingsAllow_AndOnlyWithAKey(bool enabled, bool readOnlyTurns, bool keySet, WebSearchAccess expected)
    {
        var settings = new ToolSettings { WebSearchEnabled = enabled, WebSearchOnReadOnlyTurns = readOnlyTurns };

        Assert.Equal(expected, settings.WebSearch(keySet));
    }

    [Fact]
    public void NoPtcChoice_GetsTheDefault()
    {
        Assert.True(new ToolSettings().ChoosePtc(null, isAdmin: false, out _));
        Assert.False(new ToolSettings { PtcByDefault = false }.ChoosePtc(null, isAdmin: false, out _));
    }

    [Fact]
    public void AMember_MayChooseOtherwise_WhenAllowed()
    {
        Assert.False(new ToolSettings().ChoosePtc(false, isAdmin: false, out var refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenMembersMayNotChoose_AMemberAskingForTheDefaultIsFine_ButNotOtherwise_WhileAnAdminAlwaysMay()
    {
        var settings = new ToolSettings { MembersMayChoosePtc = false };

        Assert.True(settings.ChoosePtc(true, isAdmin: false, out _));
        Assert.Null(settings.ChoosePtc(false, isAdmin: false, out var refusal));
        Assert.Equal("Programmatic Tool Calling is on for every new thread; only an Admin can change that.", refusal);
        Assert.False(settings.ChoosePtc(false, isAdmin: true, out _));
    }
}
