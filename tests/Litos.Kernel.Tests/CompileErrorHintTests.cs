using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// CS8997 ("Unterminated raw string literal") was the dominant compile failure in a real PTC
/// session, and it has exactly one cause worth automating against: C# requires a MULTI-LINE raw
/// string's content to begin on the line after its opening quotes. Probed against the real kernel,
/// only that form fails —
///
///   var x = """import math\nprint(1)\n""";   -> CS8997
///   var y = """\nimport math\nprint(1)\n""";  -> OK
///   ...payload containing \"\"\" escaped       -> OK
///   ...payload containing """ with a """" fence -> OK
///
/// so the fix is a targeted hint on the one broken shape, not general advice about quoting. These
/// run through a real subprocess because the hint is only useful if it reaches the model attached
/// to the actual diagnostic.
/// </summary>
public sealed class CompileErrorHintTests
{
    private static KernelSession NewSession(string name)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-hint-tests", name, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        return new KernelSession(
            sessionId: name,
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry([]),
            mcpToolProvider: null,
            hardTimeout: TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task InlineOpenedRawString_FailsWithCS8997_AndCarriesTheCorrection()
    {
        await using var session = NewSession("inline");

        // Exactly the shape the model wrote when embedding a whole Python file.
        var result = await session.RunAsync("var x = \"\"\"import math\nprint(1)\n\"\"\";\nx.Length", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("CS8997", result.Text, StringComparison.Ordinal);
        Assert.Contains("Content starts on the line AFTER", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression for a half-correction observed live: after a CS8997 hint that mentioned only the
    /// opening rule, the model opened the next literal correctly and still closed it inline,
    /// failing immediately with CS9000. The hint therefore states all three rules at once, and this
    /// pins that CS9000 reaches it too.
    /// </summary>
    [Fact]
    public async Task ClosingDelimiterNotOnItsOwnLine_CarriesTheSameCorrection()
    {
        await using var session = NewSession("cs9000");

        var result = await session.RunAsync("var x = \"\"\"\nimport math\nprint(1)\"\"\";\nx.Length", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("closing quotes sit on their OWN line", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other new one from the same session: a content line indented less than the closing line.
    /// </summary>
    [Fact]
    public async Task ContentLineUnderIndentedVersusClosingLine_CarriesTheSameCorrection()
    {
        await using var session = NewSession("cs8999");

        var result = await session.RunAsync("var x = \"\"\"\nimport math\n    \"\"\";\nx.Length", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("indented at least as much", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The root cause behind the CS8999 seen live: the embedded text contained its own run of three
    /// double quotes, so the fence closed early and the remainder was parsed as code. The hint has
    /// to name this, because the fix is a heavier fence rather than anything about indentation.
    /// The payload happened to be Python that round, but nothing here is language-specific — the
    /// rule is about quote runs in the text, whatever produced it.
    /// </summary>
    [Fact]
    public async Task HintNamesTheTripleQuotedPayloadCase()
    {
        await using var session = NewSession("docstring");

        var result = await session.RunAsync("var x = \"\"\"import math\nprint(1)\n\"\"\";\nx.Length", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("LONGER than the longest run of double quotes", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectlyOpenedRawString_Compiles_AndNeedsNoHint()
    {
        await using var session = NewSession("correct");

        var result = await session.RunAsync("var y = \"\"\"\nimport math\nprint(1)\n\"\"\";\ny.Length", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("Hint:", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single-line raw string is legal and opens/closes on one line, so the detector must not
    /// mistake it for the malformed multi-line form.
    /// </summary>
    [Fact]
    public async Task SingleLineRawString_Compiles_AndNeedsNoHint()
    {
        await using var session = NewSession("singleline");

        var result = await session.RunAsync("var s = \"\"\"has \"quotes\" inside\"\"\";\ns.Length", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("Hint:", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrelated compile error must not pick up the raw-string hint — a misfiring hint is worse
    /// than none, since it sends the model looking in the wrong place.
    /// </summary>
    [Fact]
    public async Task UnrelatedCompileError_GetsNoRawStringHint()
    {
        await using var session = NewSession("unrelated");

        var result = await session.RunAsync("undefinedThing.DoStuff();", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("raw string literal has three rules", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// CS8997 is also raised for an ordinary unterminated "..." string, where the raw-string rules
    /// are irrelevant and would misdirect. Requiring a 3+ quote run in the source keeps the hint
    /// off that case.
    /// </summary>
    [Fact]
    public async Task UnterminatedOrdinaryString_GetsNoRawStringHint()
    {
        await using var session = NewSession("ordinary");

        var result = await session.RunAsync("var s = \"oops;\ns.Length", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("raw string literal has three rules", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text legitimately containing a triple-quote run, fenced with four, still compiles — confirms
    /// the detector does not fire on a valid heavier fence. Nothing about the rule is tied to a
    /// particular language; a run of three quotes is a run of three quotes.
    /// </summary>
    [Fact]
    public async Task FourQuoteFenceAroundTripleQuotedPayload_Compiles()
    {
        await using var session = NewSession("fence");

        var result = await session.RunAsync("var w = \"\"\"\"\nheader\n    \"\"\"inner run\"\"\"\n    tail\n\"\"\"\";\nw.Length", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("Hint:", result.Text, StringComparison.Ordinal);
    }
}
