using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The Budgets and limits tab (m3-architecture.md §5).</summary>
public class BudgetSettingsTests
{
    [Fact]
    public void TheDefaults_AreM2sBehaviour_AndValid()
    {
        var defaults = new BudgetSettings();

        Assert.Equal((300_000L, (long?)null, 2, 2, 0.5), (defaults.DefaultTaskBudget!.Value, defaults.MaximumTaskBudget, defaults.RepairCyclesPerRun, defaults.SlotCap, defaults.ReworkTopUpShare));
        Assert.Null(defaults.DailyUserQuota);
        Assert.Null(defaults.MonthlyUserQuota);
        Assert.Empty(defaults.Validate());
    }

    public static TheoryData<BudgetSettings, string> Invalid => new()
    {
        { new BudgetSettings { DefaultTaskBudget = 0 }, "default task budget must be positive" },
        { new BudgetSettings { MaximumTaskBudget = -1 }, "maximum task budget must be positive" },
        { new BudgetSettings { MaximumTaskBudget = 500_000, DefaultTaskBudget = null }, "the default cannot be \"no cap\"" },
        { new BudgetSettings { MaximumTaskBudget = 200_000, DefaultTaskBudget = 300_000 }, "cannot be above the maximum" },
        { new BudgetSettings { DailyUserQuota = 0 }, "quota must be positive" },
        { new BudgetSettings { DailyUserQuota = 2_000_000, MonthlyUserQuota = 1_000_000 }, "daily quota cannot be above the monthly" },
        { new BudgetSettings { RepairCyclesPerRun = -1 }, "Repair cycles per run must be between 0 and 10" },
        { new BudgetSettings { RepairCyclesPerRun = 11 }, "Repair cycles per run must be between 0 and 10" },
        { new BudgetSettings { SlotCap = 0 }, "Concurrent runs must be between 1 and 16" },
        { new BudgetSettings { SlotCap = 17 }, "Concurrent runs must be between 1 and 16" },
        { new BudgetSettings { ReworkTopUpShare = 11 }, "top-up must be between 0 and 10" },
        { new BudgetSettings { ReworkTopUpShare = double.NaN }, "top-up must be between 0 and 10" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnUnusableSetting_IsRefused_InWords(BudgetSettings settings, string reason) =>
        Assert.Contains(reason, Assert.Single(settings.Validate()));

    [Fact]
    public void Several_Problems_AreAllReported()
    {
        Assert.Equal(2, new BudgetSettings { SlotCap = 0, RepairCyclesPerRun = 99 }.Validate().Count);
    }

    [Fact]
    public void WithNoMaximum_AnyCapIsAllowed_IncludingNone()
    {
        var settings = new BudgetSettings();

        Assert.Null(settings.RefuseCap(10_000_000));
        Assert.Null(settings.RefuseCap(null));
    }

    [Fact]
    public void WithAMaximum_ACapAboveIt_OrNoCap_IsRefused()
    {
        var settings = new BudgetSettings { MaximumTaskBudget = 600_000 };

        Assert.Null(settings.RefuseCap(600_000));
        Assert.Contains("at most 600,000 tokens", settings.RefuseCap(600_001));
        Assert.Contains("must have a budget of at most 600,000", settings.RefuseCap(null));
    }
}

public class SecretNamesTests
{
    [Theory]
    [InlineData("github")]
    [InlineData("websearch:tavily")]
    [InlineData("provider:openrouter")]
    [InlineData("provider:mesh_api")]
    [InlineData("mcp:github-server:GITHUB_TOKEN")]
    public void TheFactorysOwnNames_AreValid(string name) => Assert.True(SecretNames.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("GITHUB")]
    [InlineData("provider:")]
    [InlineData("provider:Open Router")]
    [InlineData("mcp:server")]
    [InlineData("mcp:server:1BAD")]
    [InlineData("../keys")]
    [InlineData("anything")]
    public void AnythingElse_IsNotASecretTheFactoryKeeps(string name) => Assert.False(SecretNames.IsValid(name));

    [Fact]
    public void Names_AreBuiltTheSameWayTheyAreChecked()
    {
        Assert.Equal("provider:anthropic", SecretNames.Provider("anthropic"));
        Assert.Equal("mcp:github:TOKEN", SecretNames.Mcp("github", "TOKEN"));
        Assert.True(SecretNames.IsValid(SecretNames.Mcp("github", "TOKEN")));
    }
}
