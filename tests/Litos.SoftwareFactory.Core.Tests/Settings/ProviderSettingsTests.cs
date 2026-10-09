using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The Providers tab (m3-architecture.md §4).</summary>
public class ProviderSettingsTests
{
    private static ProviderEntry Entry(string name, params string[] models) => new()
    {
        Name = name,
        Enabled = true,
        Models = [.. models.Select(m => new AllowedModel(m, 128_000))],
        DefaultModel = models.FirstOrDefault(),
        BaseUrl = name == "local" ? "http://localhost:1234/v1" : null,
    };

    private static readonly ProviderSettings Several = new()
    {
        Providers =
        [
            Entry("openrouter", "deepseek/deepseek-v4.1-flash", "qwen/qwen3-coder"),
            Entry("anthropic", "claude-sonnet-5"),
            Entry("mesh_api", "mesh-large"),
            Entry("local", "qwen3"),
        ],
        DefaultProvider = "anthropic",
    };

    private static bool AllUsable(ProviderEntry _) => true;

    // ---- What a provider is ----

    [Fact]
    public void TheStrictProviders_HonourTheOutputLimitAndReportUsage_TheOthersAreEstimated()
    {
        Assert.Equal(
            ["anthropic", "openai", "gemini", "openrouter"],
            KnownProviders.All.Where(p => p.Precision == BudgetPrecision.Strict).Select(p => p.Name));
        Assert.Equal(BudgetPrecision.Estimated, KnownProviders.PrecisionOf("mesh_api"));
        Assert.Equal(BudgetPrecision.Estimated, KnownProviders.PrecisionOf("something-new"));
        Assert.Equal(["local"], KnownProviders.All.Where(p => p.UsesBaseUrl).Select(p => p.Name));
    }

    // ---- Validation ----

    [Fact]
    public void TheDefaults_OfferNothing_AreStrictOnly_AndValid()
    {
        var defaults = new ProviderSettings();

        Assert.Empty(defaults.Providers);
        Assert.True(defaults.StrictOnly);
        Assert.Empty(defaults.Validate());
        Assert.Empty(Several.Validate());
    }

    public static TheoryData<ProviderSettings, string> Invalid => new()
    {
        { new() { Providers = [Entry("bedrock", "m")] }, "\"bedrock\" is not a provider the factory knows" },
        { new() { Providers = [Entry("openai", "gpt-5"), Entry("openai", "gpt-5")] }, "\"openai\" is listed more than once" },
        { new() { Providers = [Entry("openai") with { DefaultModel = null }] }, "OpenAI: an enabled provider needs at least one allowed model" },
        { new() { Providers = [Entry("openai", "gpt-5") with { DefaultModel = "gpt-4" }] }, "OpenAI: the default model gpt-4 is not one of its allowed models" },
        { new() { Providers = [Entry("openai", "gpt-5") with { DefaultModel = null }] }, "OpenAI: choose which of its models is the default" },
        { new() { Providers = [Entry("openai", "gpt-5") with { Models = [new("gpt-5", 1_000)] }] }, "gpt-5's context length must be between 4,096 and 10,000,000" },
        { new() { Providers = [Entry("openai", " gpt-5") with { DefaultModel = " gpt-5" }] }, "OpenAI: a model id must be 1 to 200 characters" },
        { new() { Providers = [Entry("openai", "gpt-5", "gpt-5")] }, "OpenAI: gpt-5 is allowed more than once" },
        { new() { Providers = [Entry("local", "qwen3") with { BaseUrl = "localhost:1234" }] }, "Local server: its address must be an http or https URL" },
        { new() { Providers = [Entry("openai", "gpt-5") with { BaseUrl = "http://x" }] }, "OpenAI is not reached at an address of its own" },
        { new() { Providers = [Entry("openai", "gpt-5") with { Enabled = false }], DefaultProvider = "openai" }, "default provider must be one that is enabled" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnUnusableSetting_IsRefused_InWords(ProviderSettings settings, string reason) =>
        Assert.Contains(settings.Validate(), e => e.Contains(reason));

    [Fact]
    public void ADisabledProvider_MayKeepItsModels_AndHaveNoAddressYet()
    {
        var settings = new ProviderSettings { Providers = [Entry("local", "qwen3") with { Enabled = false, BaseUrl = null }] };

        Assert.Empty(settings.Validate());
    }

    // ---- What a person is offered ----

    [Fact]
    public void AMember_UnderStrictOnly_IsOfferedOnlyStrictProviders()
    {
        Assert.Equal(["openrouter", "anthropic"], Several.Offered(isAdmin: false, AllUsable).Select(p => p.Name));
    }

    [Fact]
    public void AnAdmin_IsOfferedEveryEnabledProvider_EvenUnderStrictOnly()
    {
        Assert.Equal(["openrouter", "anthropic", "mesh_api", "local"], Several.Offered(isAdmin: true, AllUsable).Select(p => p.Name));
    }

    [Fact]
    public void WithStrictOnlyOff_AMemberIsOfferedEveryEnabledProvider()
    {
        Assert.Equal(4, (Several with { StrictOnly = false }).Offered(isAdmin: false, AllUsable).Count);
    }

    [Fact]
    public void AProviderThatIsDisabled_OrCannotBeCalled_IsNotOffered()
    {
        var settings = Several with { Providers = [Several.Providers[0] with { Enabled = false }, .. Several.Providers.Skip(1)] };

        Assert.Equal(["mesh_api", "local"], settings.Offered(isAdmin: true, p => p.Name != "anthropic").Select(p => p.Name));
    }

    // ---- Choosing for a new thread ----

    [Fact]
    public void NoChoice_GetsTheDefaultProvider_AndItsDefaultModel()
    {
        var chosen = Several.Choose(null, null, isAdmin: false, AllUsable, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(("anthropic", "claude-sonnet-5", 128_000), (chosen!.Value.Provider.Name, chosen.Value.Model.Id, chosen.Value.Model.ContextLength));
    }

    [Fact]
    public void WhenTheDefaultProviderCannotBeCalled_TheFirstOfferedOneIsUsed()
    {
        var chosen = Several.Choose(null, null, isAdmin: false, p => p.Name != "anthropic", out _);

        Assert.Equal(("openrouter", "deepseek/deepseek-v4.1-flash"), (chosen!.Value.Provider.Name, chosen.Value.Model.Id));
    }

    [Fact]
    public void AProviderAlone_GetsItsDefaultModel_AndAModelCanBeChosen()
    {
        Assert.Equal("deepseek/deepseek-v4.1-flash", Several.Choose("openrouter", null, false, AllUsable, out _)!.Value.Model.Id);
        Assert.Equal("qwen/qwen3-coder", Several.Choose("openrouter", "qwen/qwen3-coder", false, AllUsable, out _)!.Value.Model.Id);
    }

    [Fact]
    public void AModelThatIsNotAllowed_IsRefused_InWords()
    {
        Assert.Null(Several.Choose("openrouter", "openai/gpt-5", false, AllUsable, out var refusal));
        Assert.Equal("\"openai/gpt-5\" is not a model you can choose on OpenRouter.", refusal);
    }

    [Fact]
    public void AnEstimatedProvider_IsRefusedToAMember_UnderStrictOnly_ButNotToAnAdmin()
    {
        Assert.Null(Several.Choose("mesh_api", null, isAdmin: false, AllUsable, out var refusal));
        Assert.Contains("\"mesh_api\" is not a provider you can choose. Choose one of: openrouter, anthropic.", refusal);
        Assert.NotNull(Several.Choose("mesh_api", null, isAdmin: true, AllUsable, out _));
    }

    [Fact]
    public void WithNothingOffered_TheRefusalSaysWhatAnAdminMustDo()
    {
        Assert.Null(new ProviderSettings().Choose(null, null, isAdmin: true, AllUsable, out var refusal));
        Assert.Contains("An Admin enables one, and sets its key, under Settings", refusal);
    }
}
