using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The Presets tab's rules (m3-architecture.md §8).</summary>
public class PresetSettingsTests
{
    private static readonly string Usable = new VerificationProfile(
        VerificationProfile.CurrentVersion,
        [new VerificationStep("app", UnitTests: [new UnitTestCommand("pytest", [], 300, new TestReportSpec(TestReportFormat.Junit, "junit.xml"))])]).ToJson();

    private static VerificationPreset Preset(string name = "python", string displayName = "Python", string? profile = null) =>
        new() { Name = name, DisplayName = displayName, Profile = profile ?? Usable };

    [Fact]
    public void UsablePresets_AreValid_AndFoundByName()
    {
        var presets = new PresetSettings { Presets = [Preset(), Preset("go-1", "Go")] };

        Assert.Empty(presets.Validate());
        Assert.Equal("Go", presets.Find("go-1")!.DisplayName);
        Assert.Null(presets.Find("Go-1"));
    }

    public static TheoryData<VerificationPreset, string> Invalid => new()
    {
        { Preset("Python"), "is not a usable preset name" },
        { Preset("-x"), "is not a usable preset name" },
        { Preset("has space"), "is not a usable preset name" },
        { Preset(new string('a', 41)), "is not a usable preset name" },
        { Preset(displayName: " "), "python: give it a name people see." },
        { Preset(profile: ""), "python: its profile is empty." },
        { Preset(profile: "{ nope"), "python: its profile is not valid JSON" },
        { Preset(profile: new string('x', PresetSettings.MaxProfileChars + 1)), "can be at most 50,000 characters" },
        { Preset(profile: """{"profileVersion":1,"steps":[]}"""), "python: profileVersion must be 2" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void APresetThatCannotBeUsed_IsRefused_WithTheReason(VerificationPreset preset, string expected)
    {
        Assert.Contains(new PresetSettings { Presets = [preset] }.Validate(), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPresetsOfOneName_NoneAtAll_OrTooMany_AreRefused()
    {
        Assert.Contains(new PresetSettings { Presets = [Preset(), Preset(displayName: "Again")] }.Validate(), e => e.Contains("two presets named \"python\""));
        Assert.Contains(new PresetSettings().Validate(), e => e.Contains("at least one preset"));
        Assert.Contains(
            new PresetSettings { Presets = [.. Enumerable.Range(0, PresetSettings.MaxPresets + 1).Select(i => Preset($"p{i}"))] }.Validate(),
            e => e.Contains("at most 30 presets"));
    }
}
