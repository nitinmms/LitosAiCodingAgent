using Litos.Console;
using ModelContextProtocol.Protocol;

namespace Litos.Console.Tests;

/// <summary>
/// Covers McpPromptArguments' pure tokenizing/binding/hint-formatting logic behind an MCP
/// prompt command's typed argument text (e.g. "/github__pr_review 42 verbose") — mirrors
/// Litos.Gui's McpPromptArgumentsTests exactly, since the type was copied verbatim.
/// </summary>
public class McpPromptArgumentsTests
{
    private static PromptArgument Arg(string name, bool? required) =>
        new() { Name = name, Required = required };

    [Fact]
    public void Tokenize_EmptyOrWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(McpPromptArguments.Tokenize(""));
        Assert.Empty(McpPromptArguments.Tokenize("   "));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace_CollapsingMultipleSpaces()
    {
        var tokens = McpPromptArguments.Tokenize("42   verbose  third");

        Assert.Equal(["42", "verbose", "third"], tokens);
    }

    [Fact]
    public void Tokenize_QuotedValue_KeepsInternalWhitespaceAsOneToken()
    {
        var tokens = McpPromptArguments.Tokenize("\"hello world\" second");

        Assert.Equal(["hello world", "second"], tokens);
    }

    [Fact]
    public void Tokenize_UnterminatedQuote_RunsToEndOfStringWithoutThrowing()
    {
        var tokens = McpPromptArguments.Tokenize("\"unterminated value");

        Assert.Equal(["unterminated value"], tokens);
    }

    [Fact]
    public void Tokenize_MixOfQuotedAndBareTokens_ParsesEachCorrectly()
    {
        var tokens = McpPromptArguments.Tokenize("42 \"quoted value\" bare");

        Assert.Equal(["42", "quoted value", "bare"], tokens);
    }

    [Fact]
    public void Bind_AllRequiredArgumentsSupplied_ReturnsBoundDictionaryNoError()
    {
        var promptArgs = new[] { Arg("owner", true), Arg("repo", true) };

        var (arguments, error) = McpPromptArguments.Bind(["octocat", "hello-world"], promptArgs);

        Assert.Null(error);
        Assert.Equal("octocat", arguments["owner"]);
        Assert.Equal("hello-world", arguments["repo"]);
    }

    [Fact]
    public void Bind_MissingRequiredArgument_ReturnsMissingRequiredError_WithCorrectName()
    {
        var promptArgs = new[] { Arg("owner", true), Arg("repo", true) };

        var (_, error) = McpPromptArguments.Bind(["octocat"], promptArgs);

        Assert.NotNull(error);
        Assert.Equal(McpPromptArguments.BindErrorKind.MissingRequired, error!.Kind);
        Assert.Equal(["repo"], error.Names);
    }

    [Fact]
    public void Bind_MultipleMissingRequiredArguments_ListsAllInDeclaredOrder()
    {
        var promptArgs = new[] { Arg("a", true), Arg("b", true), Arg("c", true) };

        var (_, error) = McpPromptArguments.Bind([], promptArgs);

        Assert.NotNull(error);
        Assert.Equal(["a", "b", "c"], error!.Names);
    }

    [Fact]
    public void Bind_OptionalArgumentOmitted_IsAbsentFromDictionary_NotNull()
    {
        var promptArgs = new[] { Arg("required", true), Arg("optional", false) };

        var (arguments, error) = McpPromptArguments.Bind(["value"], promptArgs);

        Assert.Null(error);
        Assert.False(arguments.ContainsKey("optional"));
    }

    [Fact]
    public void Bind_MoreTokensThanDeclaredArguments_ReturnsTooManyValuesError()
    {
        var promptArgs = new[] { Arg("only", true) };

        var (_, error) = McpPromptArguments.Bind(["one", "two"], promptArgs);

        Assert.NotNull(error);
        Assert.Equal(McpPromptArguments.BindErrorKind.TooManyValues, error!.Kind);
    }

    [Fact]
    public void Bind_ZeroDeclaredArguments_ZeroTokens_ReturnsEmptyDictionaryNoError()
    {
        var (arguments, error) = McpPromptArguments.Bind([], []);

        Assert.Null(error);
        Assert.Empty(arguments);
    }

    [Fact]
    public void Bind_ZeroDeclaredArguments_AnyTokens_ReturnsTooManyValuesError()
    {
        var (_, error) = McpPromptArguments.Bind(["unexpected"], []);

        Assert.NotNull(error);
        Assert.Equal(McpPromptArguments.BindErrorKind.TooManyValues, error!.Kind);
    }

    [Fact]
    public void FormatArgumentHint_RequiredAndOptionalMixed_RendersAngleAndSquareBrackets()
    {
        var promptArgs = new[] { Arg("owner", true), Arg("verbose", false) };

        var hint = McpPromptArguments.FormatArgumentHint(promptArgs);

        Assert.Equal("<owner> [verbose]", hint);
    }

    [Fact]
    public void FormatArgumentHint_NoArguments_ReturnsEmptyString()
    {
        Assert.Equal("", McpPromptArguments.FormatArgumentHint([]));
    }

    [Fact]
    public void FormatArgumentHint_NullArgumentsList_ReturnsEmptyString()
    {
        Assert.Equal("", McpPromptArguments.FormatArgumentHint(null));
    }
}
