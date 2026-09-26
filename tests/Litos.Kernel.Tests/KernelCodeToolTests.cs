using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// KernelCodeTool is the schema-only ITool exposed while the kernel toggle is ON
/// (ReadMe_PTCPersistentKernel.md §8.2). Its InvokeAsync is meant to be unreachable — AgentLoop
/// intercepts ReservedToolNames.KernelCode by name before ToolRegistry.Resolve is ever reached —
/// so the invariant this test suite actually cares about is that the canary body stays a clean
/// error, not a crash, if that invariant is ever violated by a future change.
/// </summary>
public sealed class KernelCodeToolTests
{
    private static ToolSchema Schema(string name, string description) =>
        new(name, description, JsonSerializer.SerializeToElement(new { type = "object" }));

    [Fact]
    public void Name_IsTheReservedKernelCodeName()
    {
        var tool = new KernelCodeTool([]);

        Assert.Equal(ReservedToolNames.KernelCode, tool.Name);
    }

    [Fact]
    public void ParameterSchema_IsOpaque_JustACodeStringProperty()
    {
        var tool = new KernelCodeTool([]);

        var schemaJson = tool.ParameterSchema.GetRawText();
        using var doc = JsonDocument.Parse(schemaJson);
        var properties = doc.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("code", out var codeProp));
        Assert.Equal("string", codeProp.GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvokeAsync_IsUnreachableInPractice_ButReturnsACleanErrorNotAnException()
    {
        var tool = new KernelCodeTool([]);

        var result = await tool.InvokeAsync(JsonDocument.Parse("""{"code":"1+1"}""").RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("internal routing error", result.Text);
    }

    [Fact]
    public void Description_ListsEveryBridgedTool_ByNameAndSummary()
    {
        var bridged = new List<ToolSchema>
        {
            Schema("read_file", "Reads a file from disk."),
            Schema("shell", "Runs a shell command."),
        };

        var tool = new KernelCodeTool(bridged);

        Assert.Contains("read_file", tool.Description);
        Assert.Contains("Reads a file from disk.", tool.Description);
        Assert.Contains("shell", tool.Description);
        Assert.Contains("Runs a shell command.", tool.Description);
    }

    // ---- Signatures: the model is shown each tool's real argument names ----

    private static ToolSchema SchemaWith(string name, string description, string parameterJson) =>
        new(name, description, JsonDocument.Parse(parameterJson).RootElement.Clone());

    /// <summary>
    /// Every calling-convention error seen in live sessions traced to this line reading
    /// "read_file(params name, value pairs)" — the convention stated, the argument names withheld, so
    /// the model had to infer that list_directory's argument is "path" and shell's is "command". When
    /// it could not, the shortest plausible guess was positional: list_directory(".").
    /// </summary>
    [Fact]
    public void Description_RendersEachToolsRealArgumentNames_NotAnAbstractConvention()
    {
        var tool = new KernelCodeTool([
            SchemaWith("read_file", "Reads a file.", """
                {"type":"object",
                 "properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},
                 "required":["path"]}
                """),
        ]);

        Assert.Contains("""- read_file("path", <string>[, "offset", <int>][, "limit", <int>]) -> Task<string>: Reads a file.""", tool.Description);
        Assert.DoesNotContain("read_file(params name, value pairs)", tool.Description);
    }

    [Fact]
    public void Description_RequiredArgumentsComeFirst_InTheOrderTheSchemaDeclaresThemRequired()
    {
        var tool = new KernelCodeTool([
            SchemaWith("write_file", "Writes a file.", """
                {"type":"object",
                 "properties":{"path":{"type":"string"},"content":{"type":"string"}},
                 "required":["path","content"]}
                """),
        ]);

        Assert.Contains("""- write_file("path", <string>, "content", <string>) -> Task<string>""", tool.Description);
    }

    [Fact]
    public void Description_AllOptionalArguments_AreAllBracketed()
    {
        var tool = new KernelCodeTool([
            SchemaWith("search", "Searches.", """{"type":"object","properties":{"glob":{"type":"string"},"deep":{"type":"boolean"}}}"""),
        ]);

        Assert.Contains("""- search(["glob", <string>][, "deep", <bool>]) -> Task<string>""", tool.Description);
    }

    /// <summary>
    /// A schema with nothing to name has no template to offer, so the abstract form remains the
    /// honest fallback rather than rendering an empty argument list.
    /// </summary>
    [Fact]
    public void Description_SchemaWithNoProperties_FallsBackToTheAbstractForm()
    {
        var tool = new KernelCodeTool([SchemaWith("refresh", "Refreshes.", """{"type":"object"}""")]);

        Assert.Contains("- refresh(params name, value pairs) -> Task<string>", tool.Description);
    }

    /// <summary>
    /// `required` naming a property the schema never declares is the schema's own bug; it must not
    /// leak an untyped placeholder into the template.
    /// </summary>
    [Fact]
    public void Description_RequiredNamingAnUndeclaredProperty_IsSkipped()
    {
        var tool = new KernelCodeTool([
            SchemaWith("odd", "Odd.", """{"type":"object","properties":{"real":{"type":"string"}},"required":["ghost","real"]}"""),
        ]);

        Assert.Contains("""- odd("real", <string>) -> Task<string>""", tool.Description);
        Assert.DoesNotContain("ghost", tool.Description);
    }

    /// <summary>
    /// THE CACHING GUARD, and the reason Signature() walks JsonElement in document order and uses a
    /// List rather than a HashSet for required-name lookups.
    ///
    /// This Description is rebuilt on every turn and sits at the front of the provider's cached
    /// prompt prefix. Byte-identical output means the prefix is reused; a single byte of variation
    /// between turns would miss the ENTIRE cache on EVERY turn, which costs far more than the failed
    /// evals the signatures exist to prevent. Any future edit that introduces a timestamp, a GUID, a
    /// dictionary/set enumeration, or a culture-sensitive format into this block breaks that silently
    /// — nothing else in the suite would notice, and the symptom is a bill, not a failure.
    ///
    /// Schemas are rebuilt from source here rather than reused, so this exercises the real per-turn
    /// path (ToolRegistryFactory.Create() constructs fresh tools every turn) and not just a second
    /// read of one cached JsonElement.
    /// </summary>
    [Fact]
    public void Description_IsByteIdenticalAcrossRebuilds_SoThePromptPrefixStaysCached()
    {
        // Mirrors how MCP tool schemas really arrive: parsed from a server's JSON, document order.
        static IReadOnlyList<ToolSchema> FreshSchemas() =>
        [
            SchemaWith("read_file", "Reads a file.", """{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"integer"}},"required":["path"]}"""),
            SchemaWith("mcp__srv__query", "Queries.", """{"type":"object","properties":{"sql":{"type":"string"},"rows":{"type":"integer"},"raw":{"type":"boolean"}},"required":["sql"]}"""),
            SchemaWith("refresh", "Refreshes.", """{"type":"object"}"""),
        ];

        var first = new KernelCodeTool(FreshSchemas()).Description;
        var second = new KernelCodeTool(FreshSchemas()).Description;

        Assert.Equal(first, second);
        // Same instance read twice must also not drift (Description is a computed property).
        var tool = new KernelCodeTool(FreshSchemas());
        Assert.Equal(tool.Description, tool.Description);
    }

    // ---- Calling-convention guidance ----

    /// <summary>
    /// The clause that makes a positional call visibly wrong rather than merely plausible: a lone
    /// string binds to the raw-arguments-JSON overload, so shell("pwd") is a malformed JSON document
    /// and not a positional argument.
    /// </summary>
    [Fact]
    public void Description_ExplainsThatASingleStringIsTheRawJsonOverload_NotAPositionalArgument()
    {
        var tool = new KernelCodeTool([]);

        Assert.Contains("not a positional call", tool.Description);
        Assert.Contains("""read_file("path", "a.txt")""", tool.Description);
    }

    /// <summary>
    /// Batching independent calls is the whole point of Programmatic Tool Calling, and a live session
    /// reached for Task.WhenAll unprompted — worth stating outright now that System.Threading.Tasks
    /// is actually imported (see ScriptSession.Imports).
    /// </summary>
    [Fact]
    public void Description_EncouragesConcurrentToolCalls_WithTaskWhenAll()
    {
        var tool = new KernelCodeTool([]);

        Assert.Contains("Task.WhenAll", tool.Description);
        Assert.Contains("CONCURRENTLY", tool.Description);
    }

    /// <summary>
    /// Ordering matters: the rule and the signatures it governs must be read together. The rule used
    /// to sit several paragraphs below, after the output-brevity guidance.
    /// </summary>
    [Fact]
    public void Description_CallingConvention_AppearsImmediatelyAfterTheToolList_NotBelowOtherGuidance()
    {
        var tool = new KernelCodeTool([
            SchemaWith("read_file", "Reads a file.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        ]);
        var description = tool.Description;

        var signatureAt = description.IndexOf("""- read_file("path", <string>)""", StringComparison.Ordinal);
        var conventionAt = description.IndexOf("Call a tool exactly as its signature above shows", StringComparison.Ordinal);
        var brevityAt = description.IndexOf("Keep your script's own printed output", StringComparison.Ordinal);

        Assert.True(signatureAt >= 0 && conventionAt >= 0 && brevityAt >= 0, "expected all three sections to be present");
        Assert.True(signatureAt < conventionAt, "the convention must follow the signatures it refers to");
        Assert.True(conventionAt < brevityAt, "the convention must come before the output-brevity guidance, not after it");
    }

    [Fact]
    public void Description_AlwaysMentionsTheFixedGlobals_RegardlessOfBridgedToolList()
    {
        var tool = new KernelCodeTool([]);

        Assert.Contains("SCRATCH_DIR", tool.Description);
        Assert.Contains("KernelState.List", tool.Description);
        Assert.Contains("KernelState.Describe", tool.Description);
    }

    [Fact]
    public void Description_WarnsAgainstRoundTrippingReadFileOutputIntoWriteFile()
    {
        // A real failure observed end-to-end: a kernel script read a file via read_file, edited
        // its text, and wrote it back via write_file without stripping read_file's "123\t"
        // line-number display prefixes — corrupting the file. A kernel script can chain
        // read -> transform -> write far more naturally than the sequential path can (it has to
        // hand-write the round trip instead of just calling edit_file), so this guidance belongs
        // here even though ReadFileTool's own Description also carries it now.
        var tool = new KernelCodeTool([]);

        Assert.Contains("write_file", tool.Description);
        Assert.Contains("edit_file", tool.Description);
    }

    /// <summary>
    /// A real failure observed end-to-end: mid-script, writing a multi-line JavaScript function into
    /// a string, the model reached for JavaScript's backtick template literal — `var new2 = `function
    /// getAudio() {...`` — giving CS1056 and a cascade of parse errors as the remainder was read as
    /// code. It knew "..." cannot span lines and picked the wrong multi-line syntax. The raw-string
    /// guidance already here did not cover it: that was framed around EMBEDDING a file, config or
    /// patch, and this was simply a multi-line string.
    /// </summary>
    [Fact]
    public void Description_StatesThatCSharpHasNoBacktickString_ForAnyMultiLineString()
    {
        var tool = new KernelCodeTool([]);

        Assert.Contains("no backtick string", tool.Description);
        Assert.Contains("CS1056", tool.Description);
        Assert.Contains("EVERY multi-line string", tool.Description);
    }

    [Fact]
    public void Description_WarnsAgainstNestingRawStringsAroundInterpolatedStrings()
    {
        // A real failure observed end-to-end: a kernel script wrapped a "\"\"\"...\"\"\"" raw
        // string literal around text containing an escaped "$\"...\"" interpolated string —
        // Roslyn failed to parse the nesting (CS8997/CS1002). The model self-recovered by switching
        // to a plain verbatim string on its very next attempt, but that was a lucky first guess, not
        // something to rely on — the same guaranteed-to-be-seen description is the right place for
        // this, same as the read_file/write_file warning above.
        var tool = new KernelCodeTool([]);

        Assert.Contains("raw string", tool.Description);
        Assert.Contains("verbatim string", tool.Description);
    }

    [Fact]
    public void Description_IsRecomputedLive_NotCachedAtConstruction()
    {
        // §8.2: "KernelCodeTool cannot be a static, schema-fixed-at-registration singleton — its
        // Description is recomputed... from whatever tools/MCP servers are actually bridged for
        // that session." Description is a property (computed fresh on read), not a field baked in
        // by the constructor — this is what makes that true; a regression to a cached field would
        // silently defeat the "never drifts from the bridge's actual contents" guarantee.
        var bridgedTools = new List<ToolSchema> { Schema("read_file", "Reads a file.") };
        var tool = new KernelCodeTool(bridgedTools);

        Assert.Contains("read_file", tool.Description);

        bridgedTools.Add(Schema("new_mcp_tool", "A newly enabled MCP tool."));

        Assert.Contains("new_mcp_tool", tool.Description);
    }
}
