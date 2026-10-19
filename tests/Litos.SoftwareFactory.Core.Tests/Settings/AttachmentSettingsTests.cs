using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The attachments section and which models are shown images (attachments-architecture.md §1, §6).</summary>
public class AttachmentSettingsTests
{
    [Fact]
    public void Defaults_AreOn_FiveFilesAMessage_FiftyMegabytesAThread()
    {
        var settings = new AttachmentSettings();

        Assert.Equal((true, 5, 50L * 1024 * 1024), (settings.Enabled, settings.MaxFilesPerMessage, settings.MaxBytesPerThread));
        Assert.Empty(settings.Validate());
    }

    [Theory]
    [InlineData(0, 50L * 1024 * 1024, "Files per message")]
    [InlineData(21, 50L * 1024 * 1024, "Files per message")]
    [InlineData(5, 10L * 1024 * 1024, "total per thread")]
    [InlineData(5, 2L * 1024 * 1024 * 1024, "total per thread")]
    public void Validate_RefusesLimitsOutsideTheirRange(int files, long bytes, string named)
    {
        var errors = new AttachmentSettings { MaxFilesPerMessage = files, MaxBytesPerThread = bytes }.Validate();

        Assert.Contains(named, Assert.Single(errors));
    }

    [Theory]
    [InlineData("anthropic", null, true)]
    [InlineData("openai", null, true)]
    [InlineData("gemini", null, true)]
    [InlineData("openrouter", null, false)]
    [InlineData("mesh_api", null, false)]
    [InlineData("local", null, false)]
    [InlineData("openrouter", true, true)]
    [InlineData("anthropic", false, false)]
    public void AModelIsShownImages_AsAnAdminSet_OrAsItsProviderUsuallyDoes(string provider, bool? set, bool expected)
    {
        Assert.Equal(expected, AllowedModel.Accepts(provider, new AllowedModel("m", 128_000, set)));
    }

    [Fact]
    public void AModelNoLongerAllowed_IsShownImages_AsItsProviderUsuallyDoes()
    {
        Assert.True(AllowedModel.Accepts("anthropic", null));
        Assert.False(AllowedModel.Accepts("openrouter", null));
    }
}
