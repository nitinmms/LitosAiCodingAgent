using Litos.SoftwareFactory.Core.Lessons;

namespace Litos.SoftwareFactory.Core.Tests.Lessons;

/// <summary>What a lesson may be (m4-architecture.md §3.1).</summary>
public class LessonRulesTests
{
    private static LessonDraft Draft(string rule = "Money is stored as integer cents, never as floating point.", string[]? topics = null,
        string[]? paths = null, LessonSource source = LessonSource.Manual, Guid? sourceRef = null, string reason = "") =>
        LessonRules.Clean(new LessonDraft(rule, topics ?? ["money"], paths ?? [], source, sourceRef, reason));

    [Fact]
    public void Clean_Trims_LowerCasesAndDedupesTopics_AndUsesForwardSlashesInPaths()
    {
        var draft = LessonRules.Clean(new LessonDraft("  Use cents.\r\nAlways.  ", [" Money ", "money", "", "CSV"], [@"src\Orders\**", " "], LessonSource.Manual, null, "  why "));

        Assert.Equal("Use cents.\nAlways.", draft.Rule);
        Assert.Equal(["money", "csv"], draft.Topics);
        Assert.Equal(["src/Orders/**"], draft.Paths);
        Assert.Equal("why", draft.Reason);
    }

    [Fact]
    public void AWellFormedLesson_IsValid()
    {
        Assert.Empty(LessonRules.Validate(Draft()));
        Assert.Empty(LessonRules.Validate(Draft(source: LessonSource.Decision, sourceRef: Guid.NewGuid(), paths: ["src/Money/**"], reason: "The tester asked for it.")));
        Assert.Empty(LessonRules.Validate(Draft(new string('a', LessonRules.MaxRuleChars))));
    }

    public static TheoryData<LessonDraft, string> Invalid => new()
    {
        { Draft(" "), "Write the lesson" },
        { Draft(new string('a', 601)), "at most 600 characters; this one is 601" },
        { Draft(topics: []), "at least one topic" },
        { Draft(topics: ["a", "b", "c", "d", "e", "f", "g", "h", "i"]), "at most 8 topics" },
        { Draft(topics: [new string('t', 41)]), "longer than 40 characters" },
        { Draft(paths: ["../outside/**"]), "is not a usable path" },
        { Draft(paths: [.. Enumerable.Range(0, 11).Select(i => $"src/{i}/**")]), "at most 10 paths" },
        { Draft(reason: new string('r', 601)), "The reason can be at most 600" },
        { Draft(source: LessonSource.ReviewFinding), "Name what the lesson rests on" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnUnusableLesson_IsRefused_SayingWhy(LessonDraft draft, string why) =>
        Assert.Contains(LessonRules.Validate(draft), e => e.Contains(why, StringComparison.Ordinal));

    [Theory]
    [InlineData("Call the API with sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv.")]
    [InlineData("The token is ghp_0123456789abcdefghijABCDEFGHIJ0123.")]
    [InlineData("Use AKIAABCDEFGHIJKLMNOP for uploads.")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("Set password: hunter2hunter2 in the config.")]
    [InlineData("api_key = 'abcdefgh12345678'")]
    public void ALessonWithASecret_IsRefused(string rule) =>
        Assert.Contains(LessonRules.Validate(Draft(rule)), e => e.Contains("secret", StringComparison.Ordinal));

    [Theory]
    [InlineData("Avoid this:\n   at Orders.Export.Run() in C:\\src\\Export.cs:line 42")]
    [InlineData("Traceback (most recent call last):\n  File \"app.py\", line 3, in <module>")]
    [InlineData("at com.acme.Orders.run(Orders.java:42)")]
    public void ALessonThatIsAStackTrace_IsRefused(string rule) =>
        Assert.Contains(LessonRules.Validate(Draft(rule)), e => e.Contains("stack trace", StringComparison.Ordinal));

    [Fact]
    public void ARuleThatMentionsPasswordsOrStackTraces_IsNotASecretOrATrace()
    {
        Assert.Empty(LessonRules.Validate(Draft("Never log a user's password or token; mask them in error messages.")));
        Assert.Empty(LessonRules.Validate(Draft("Return a stack trace only in Development, never in Production.")));
    }

    [Fact]
    public void Normalize_IgnoresCase_PunctuationAndSpacing_ButNotWords()
    {
        var a = LessonRules.Normalize("Money is stored as integer cents.");
        Assert.Equal(a, LessonRules.Normalize("  money IS stored, as   integer cents!  "));
        Assert.Equal(a, LessonRules.Normalize("Money is stored as\ninteger cents"));
        Assert.NotEqual(a, LessonRules.Normalize("Money is stored as decimal cents."));
        Assert.Equal(64, a.Length);
    }
}
