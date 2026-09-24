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
        Assert.Contains("must begin on the line AFTER its opening quotes", result.Text, StringComparison.Ordinal);
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
        Assert.DoesNotContain("opening quotes", result.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The payload legitimately containing a triple-quote run, fenced with four, still compiles —
    /// confirms the hint's detector does not fire on a valid heavier fence.
    /// </summary>
    [Fact]
    public async Task FourQuoteFenceAroundTripleQuotedPayload_Compiles()
    {
        await using var session = NewSession("fence");

        var result = await session.RunAsync("var w = \"\"\"\"\ndef f():\n    \"\"\"doc\"\"\"\n    pass\n\"\"\"\";\nw.Length", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("Hint:", result.Text, StringComparison.Ordinal);
    }
}
