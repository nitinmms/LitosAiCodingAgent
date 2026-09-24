using System.Text.Json;
using Litos.Kernel.Host;

namespace Litos.Kernel.Tests;

/// <summary>
/// KernelArgs backs the name/value-pair tool wrappers, which exist so a script never hand-writes
/// arguments JSON inside a C# string literal. See KernelArgs' own doc comment for the measured
/// failure that motivated it.
/// </summary>
public sealed class KernelArgsTests
{
    private static string Get(string json, string property) =>
        JsonDocument.Parse(json).RootElement.GetProperty(property).GetString()!;

    [Fact]
    public void Json_NoArguments_IsAnEmptyObject()
    {
        Assert.Equal("{}", KernelArgs.Json());
    }

    /// <summary>
    /// The whole point: a Windows path survives verbatim. Written by hand as
    /// "{\"path\":\"c:\\temp\\x\"}" this is invalid JSON (a lone backslash is not a legal escape),
    /// which is exactly the error seen in a real session.
    /// </summary>
    [Fact]
    public void Json_WindowsPath_IsEscapedCorrectlyAndRoundTrips()
    {
        var json = KernelArgs.Json("path", @"c:\temp\snake1\snake.py");

        Assert.Equal(@"c:\temp\snake1\snake.py", Get(json, "path"));
    }

    [Fact]
    public void Json_ContentWithQuotesNewlinesAndUnicode_RoundTrips()
    {
        var content = "line1\r\n\ttab \"quoted\" 'single' \\ backslash — em dash";

        var json = KernelArgs.Json("content", content);

        Assert.Equal(content, Get(json, "content"));
    }

    [Fact]
    public void Json_MultiplePairs_AreAllPresent()
    {
        var json = KernelArgs.Json("path", @"c:\a\b.txt", "content", "hello");

        Assert.Equal(@"c:\a\b.txt", Get(json, "path"));
        Assert.Equal("hello", Get(json, "content"));
    }

    [Fact]
    public void Json_NonStringValues_KeepTheirJsonTypes()
    {
        var json = KernelArgs.Json("limit", 40, "recursive", true, "missing", null);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal(40, root.GetProperty("limit").GetInt32());
        Assert.True(root.GetProperty("recursive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("missing").ValueKind);
    }

    /// <summary>
    /// A single string reaching the params overload is already-built JSON (the raw-JSON overload is
    /// still supported), so it must pass through rather than become {"{...}": null}.
    /// </summary>
    [Fact]
    public void Json_SingleStringArgument_IsTreatedAsAlreadyBuiltJson()
    {
        const string raw = """{"path":"a.txt"}""";

        Assert.Equal(raw, KernelArgs.Json(raw));
    }

    [Fact]
    public void Json_OddNumberOfValues_ThrowsWithAUsefulExample()
    {
        var ex = Assert.Throws<ArgumentException>(() => KernelArgs.Json("path", "a.txt", "content"));

        Assert.Contains("name, value pairs", ex.Message);
        Assert.Contains("write_file", ex.Message);
    }

    [Fact]
    public void Json_NonStringName_ThrowsNamingThePosition()
    {
        var ex = Assert.Throws<ArgumentException>(() => KernelArgs.Json(42, "a.txt"));

        Assert.Contains("position 0", ex.Message);
    }
}
