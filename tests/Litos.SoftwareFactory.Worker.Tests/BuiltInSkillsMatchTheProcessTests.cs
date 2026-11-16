using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Settings;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// The built-in skills are written against the factory's own process: they name its completion
/// tools, the fields those tools take and the briefs' headings (docs/software-factory/built-in-skills.md §3).
/// Nothing keeps a skill's words in step with a renamed tool or field, or a retitled brief section,
/// so this does: a change that breaks a built-in fails here, not silently in a run.
/// </summary>
public sealed partial class BuiltInSkillsMatchTheProcessTests
{
    /// <summary>A word in backticks that names a tool or a field: `submit_work`, `knownLimitations`.</summary>
    [GeneratedRegex("`([A-Za-z_][A-Za-z0-9_]*)`")]
    private static partial Regex Identifier();

    /// <summary>A brief section a skill points to: under "Verification the factory will run".</summary>
    [GeneratedRegex("under \"([^\"]+)\"")]
    private static partial Regex Heading();

    private static IReadOnlyList<CompletionTool> Tools()
    {
        var host = TestOptions.HostClient(new FakeHttpMessageHandler());
        return
        [
            new SubmitWorkTool(host, "s"), new RequestDecisionTool(host, "s"), new SubmitReviewTool(host, "s"),
            new SubmitSpecTool(host, "s"), new SubmitPlanTool(host, "s"), new SubmitLessonsTool(host, "s"),
        ];
    }

    /// <summary>Every tool name, and every property name anywhere in their schemas.</summary>
    private static HashSet<string> Known()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in Tools())
        {
            known.Add(tool.Name);
            Collect(tool.ParameterSchema, known);
        }

        return known;
    }

    private static void Collect(JsonElement schema, HashSet<string> into)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                into.Add(property.Name);
                Collect(property.Value, into);
            }
        }

        if (schema.TryGetProperty("items", out var items))
            Collect(items, into);
    }

    private static string Templates()
    {
        var assembly = typeof(BriefComposer).Assembly;
        return string.Join("\n", assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Briefs.Templates.", StringComparison.Ordinal))
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd().ReplaceLineEndings("\n");
            }));
    }

    public static TheoryData<string> Skills() => [.. BuiltInSkills.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryToolAndFieldABuiltInNames_IsOneTheFactoryHas(string name)
    {
        var known = Known();
        var skill = BuiltInSkills.Find(name)!;

        var unknown = Identifier().Matches(skill.Body + "\n" + skill.Description).Select(m => m.Groups[1].Value)
            .Where(id => !known.Contains(id)).Distinct().ToList();

        Assert.True(unknown.Count == 0, $"{name} names {string.Join(", ", unknown)}, which no completion tool has. Known: {string.Join(", ", known.Order())}.");
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryBriefSectionABuiltInPointsTo_IsAHeadingOfABrief(string name)
    {
        var templates = Templates();
        foreach (var heading in Heading().Matches(BuiltInSkills.Find(name)!.Body).Select(m => m.Groups[1].Value))
            Assert.True(templates.Contains("\n## " + heading + "\n", StringComparison.Ordinal), $"{name} points to the brief section \"{heading}\", which no brief has.");
    }

    [Fact]
    public void TheSkillsDoNameTheProcess_SoTheChecksAboveHaveSomethingToCheck()
    {
        var all = string.Join("\n", BuiltInSkills.All.Select(s => s.Body));

        Assert.Contains("`submit_work`", all);
        Assert.Contains("`request_decision`", all);
        Assert.Contains("`knownLimitations`", all);
        Assert.Matches(Heading(), all);
    }

    [Fact]
    public void ARenamedField_WouldBeCaught()
    {
        var known = Known();

        Assert.Contains("knownLimitations", known);
        Assert.DoesNotContain("knownIssues", known);
        Assert.Contains("settledBy", known);
    }
}
