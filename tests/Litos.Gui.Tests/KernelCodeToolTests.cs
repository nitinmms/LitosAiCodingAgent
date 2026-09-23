using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Gui.Tests;

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
