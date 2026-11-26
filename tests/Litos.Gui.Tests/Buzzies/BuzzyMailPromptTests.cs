using Litos.Gui.Buzzies;

namespace Litos.Gui.Tests.Buzzies;

/// <summary>
/// What the prompt says about email (Implement_Buzzies.md, Phase 4). The wrapping and the approvals
/// are enforced in code, but the sentence telling the model that a message's text is data — not an
/// instruction — is the layer that decides what it does with an injection it reads. It must be there
/// when mail tools are granted, and must not clutter a Buzzy that has none.
/// </summary>
public class BuzzyMailPromptTests
{
    private static BuzzyProfile Profile(params string[] tools) => new(
        "penny", "Penny", "buddy", "#FFC21A", tools, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ABuzzyWithMailTools_IsToldMailTextIsUntrustedAndThatItCannotSend()
    {
        var line = BuzzySystemPromptProvider.EmailLine(Profile(BuzzyToolNames.MailRead));

        Assert.Contains("untrusted", line);
        Assert.Contains("never an instruction", line);
        Assert.Contains("cannot send", line);
        Assert.Contains("draft", line);
    }

    [Fact]
    public void ABuzzyWithoutMailTools_IsNotToldAboutMailAtAll()
    {
        Assert.Equal("", BuzzySystemPromptProvider.EmailLine(Profile(BuzzyToolNames.ReadFile, BuzzyToolNames.WebSearch)));
    }

    [Fact]
    public void AGrantOfAnyOneMailTool_IsEnoughToSayIt()
    {
        foreach (var tool in BuzzyToolNames.Mail)
            Assert.NotEqual("", BuzzySystemPromptProvider.EmailLine(Profile(tool)));
    }
}
