using Litos.Kernel;

namespace Litos.Kernel.Tests;

/// <summary>
/// Exercises Litos.Kernel.Host's RunLoop/ScriptSession end to end over an in-memory duplex pipe
/// (InProcessKernelHostFixture) — covers persistence across evals, the StateDelta push mechanism
/// (§4.1's "push, don't rely on pull" fix), KernelState.List/Describe, output-size ceilings (§8.2),
/// return-value serialization semantics, and the stdout-capture isolation that keeps a script's own
/// Console.WriteLine from corrupting the protocol stream.
/// </summary>
public sealed class KernelHostEvalTests
{
    [Fact]
    public async Task Eval_SimpleExpression_ReturnsValueAsReturnValueText()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("1 + 1");

        Assert.False(result.IsError);
        Assert.Equal("2", result.ReturnValueText);
    }

    [Fact]
    public async Task Eval_VariableDeclaredInOneCall_IsVisibleInTheNext()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("var x = 41;");
        var result = await fixture.EvalAsync("x + 1");

        Assert.False(result.IsError);
        Assert.Equal("42", result.ReturnValueText);
    }

    [Fact]
    public async Task Eval_FunctionDeclaredInOneCall_IsCallableInTheNext()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("int Square(int n) => n * n;");
        var result = await fixture.EvalAsync("Square(6)");

        Assert.False(result.IsError);
        Assert.Equal("36", result.ReturnValueText);
    }

    [Fact]
    public async Task Eval_ConsoleWriteLine_IsCapturedAsOutput_NotLeakedToTheProtocolStream()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("Console.WriteLine(\"hello from script\");");

        Assert.False(result.IsError);
        Assert.Contains("hello from script", result.Output);

        // If stdout capture leaked, this second, unrelated eval would fail to deserialize (the
        // protocol stream would have raw script text interleaved with JSON lines) rather than
        // returning a clean result — the real assertion is that the pipe is still healthy.
        var followUp = await fixture.EvalAsync("2 + 2");
        Assert.False(followUp.IsError);
        Assert.Equal("4", followUp.ReturnValueText);
    }

    [Fact]
    public async Task Eval_CompileError_ReturnsIsErrorTrue_WithDiagnosticText()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("this is not valid C#;;;");

        Assert.True(result.IsError);
        Assert.False(string.IsNullOrEmpty(result.ReturnValueText));
    }

    [Fact]
    public async Task Eval_RuntimeException_ReturnsIsErrorTrue_AndSessionStaysUsableAfterward()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var thrown = await fixture.EvalAsync("throw new InvalidOperationException(\"boom\");");
        Assert.True(thrown.IsError);
        Assert.Contains("boom", thrown.ReturnValueText);

        // A runtime exception in one eval must not corrupt ScriptState for the next eval.
        var recovered = await fixture.EvalAsync("40 + 2");
        Assert.False(recovered.IsError);
        Assert.Equal("42", recovered.ReturnValueText);
    }

    // --- StateDelta: push, don't rely on pull (§4.1) ---

    [Fact]
    public async Task StateDelta_OnTheSameCallThatDeclaresAFunction_AlreadyNamesIt()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("int FindGreatest(int a, int b) => a > b ? a : b;");

        Assert.NotNull(result.StateDelta);
        Assert.Contains("FindGreatest", result.StateDelta);
        Assert.Contains("function", result.StateDelta);
    }

    [Fact]
    public async Task StateDelta_OnTheSameCallThatDeclaresAVariable_AlreadyNamesIt()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("var data = new List<int> { 1, 2, 3 };");

        Assert.NotNull(result.StateDelta);
        Assert.Contains("data", result.StateDelta);
        Assert.Contains("variable", result.StateDelta);
    }

    [Fact]
    public async Task StateDelta_OnAnUnrelatedLaterCall_DoesNotRementionEarlierUnchangedState()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("var data = new List<int> { 1, 2, 3 };");
        var result = await fixture.EvalAsync("1 + 1"); // reads nothing, declares nothing new

        Assert.Null(result.StateDelta);
    }

    [Fact]
    public async Task StateDelta_WhenBothReadingExistingAndDeclaringNew_OnlyReportsTheNewOne()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("var existing = 1;");
        var result = await fixture.EvalAsync("var fresh = existing + 1;");

        Assert.NotNull(result.StateDelta);
        Assert.Contains("fresh", result.StateDelta);
        Assert.DoesNotContain("existing", result.StateDelta);
    }

    [Fact]
    public async Task StateDelta_RedefiningAFunction_ReportsOnlyTheLatestSignature()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("int Square(int n) => n * n;");
        var result = await fixture.EvalAsync("int Square(int n) => n * n * n;"); // redefined: cube

        Assert.NotNull(result.StateDelta);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result.StateDelta!, "Square").Count;
        Assert.Equal(1, occurrences);
    }

    // --- KernelState.List()/Describe(): supplementary re-orientation, not the only mechanism (§4.1) ---

    [Fact]
    public async Task KernelStateList_LaterCall_AlsoReportsAFunctionDeclaredEarlier()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("int FindGreatest(int a, int b) => a > b ? a : b;");
        var result = await fixture.EvalAsync("string.Join(\"|\", KernelState.List())");

        Assert.False(result.IsError);
        Assert.Contains("FindGreatest", result.ReturnValueText);
    }

    [Fact]
    public async Task KernelStateList_ReportsBothVariablesAndFunctions()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        await fixture.EvalAsync("var count = 5;");
        await fixture.EvalAsync("int Double(int n) => n * 2;");
        var result = await fixture.EvalAsync("string.Join(\"|\", KernelState.List())");

        Assert.False(result.IsError);
        Assert.Contains("count", result.ReturnValueText);
        Assert.Contains("Double", result.ReturnValueText);
    }

    [Fact]
    public async Task KernelStateDescribe_UnknownName_ReturnsAFriendlyMessage_NotAnException()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("KernelState.Describe(\"doesNotExist\")");

        Assert.False(result.IsError);
        Assert.Contains("not a known kernel variable or function", result.ReturnValueText);
    }

    // --- SCRATCH_DIR injection (§4.5) ---

    [Fact]
    public async Task ScratchDir_IsInjectedAndWritable()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            "System.IO.File.WriteAllText(System.IO.Path.Combine(SCRATCH_DIR, \"probe.txt\"), \"hi\"); \"written\"");

        Assert.False(result.IsError);
        Assert.Equal("written", result.ReturnValueText);
        Assert.True(File.Exists(Path.Combine(fixture.ScratchDirectory, "probe.txt")));
        Assert.Equal("hi", await File.ReadAllTextAsync(Path.Combine(fixture.ScratchDirectory, "probe.txt")));
    }

    // --- Return-value serialization semantics (§8.2) ---

    [Fact]
    public async Task ReturnValue_Null_SerializesToNullReturnValueText()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("(string?)null");

        Assert.False(result.IsError);
        Assert.Null(result.ReturnValueText);
    }

    [Fact]
    public async Task ReturnValue_String_PassesThroughDirectly_NotJsonQuoted()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("\"plain text\"");

        Assert.Equal("plain text", result.ReturnValueText);
    }

    [Fact]
    public async Task ReturnValue_ListOfInts_SerializesAsJsonArray()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("new List<int> { 1, 2, 3 }");

        Assert.False(result.IsError);
        Assert.Equal("[1,2,3]", result.ReturnValueText);
    }

    [Fact]
    public async Task ReturnValue_LargeEnumerable_IsCappedAtMaxElements_NotFullyDrained()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("Enumerable.Range(0, 10_000)");

        Assert.False(result.IsError);
        Assert.Contains("truncated", result.ReturnValueText);
        // The cap is on element count in the serialized text, not full 10,000-element enumeration.
        var commaCount = result.ReturnValueText!.Count(c => c == ',');
        Assert.True(commaCount < 200, $"expected a capped element count, got {commaCount} separators");
    }

    [Fact]
    public async Task ReturnValue_UnserializableType_ReturnsADiagnostic_NotACrash()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("new System.Threading.CancellationTokenSource()");

        Assert.False(result.IsError); // the eval itself succeeded — only serializing the return value failed
        Assert.Contains("not serializable", result.ReturnValueText);
    }

    // --- Output-size ceilings (§8.2) ---

    [Fact]
    public async Task Output_ExceedingTheCap_IsTruncatedWithAnArtifactPathToTheFullContent()
    {
        await using var fixture = new InProcessKernelHostFixture();
        await fixture.InitializeAsync();

        // One line well past MaxOutputBytes (64 KiB).
        var result = await fixture.EvalAsync("Console.WriteLine(new string('x', 100_000));");

        Assert.False(result.IsError);
        Assert.True(result.Truncated);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
        var fullContent = await File.ReadAllTextAsync(result.ArtifactPath!);
        Assert.True(fullContent.Length >= 100_000);
    }
}
