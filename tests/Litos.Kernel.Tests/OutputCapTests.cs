using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// What an eval prints stays in the conversation and is paid for again on every later call, so a
/// session can cap what one eval returns. Past the cap the model sees the start and the end, with a
/// note between them saying how much was left out and where the whole text is. A cap that kept only
/// the start cut five of seven files from one factory script, and hid the summary a test run prints
/// last; the agent then spent extra calls reading the lost files again.
/// </summary>
public sealed class OutputCapTests
{
    private static (KernelSession Session, string Scratch) NewSession(int? cap)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-output-cap", Guid.NewGuid().ToString("n"), "scratch");
        Directory.CreateDirectory(scratch);
        return (new KernelSession("cap", Path.GetTempPath(), scratch, () => new ToolRegistry([]), outputCapChars: cap), scratch);
    }

    [Fact]
    public async Task OutputPastTheCap_KeepsItsStartAndItsEnd_AndSaysWhatWasLeftOut()
    {
        var (session, scratch) = NewSession(cap: 1_000);
        await using var _ = session;

        var result = await session.RunAsync(
            "System.Console.Write(\"START\" + new string('x', 5000) + \"Tests: 3 failed, 40 passed\");", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("START" + new string('x', 495), result.Text);
        Assert.EndsWith("Tests: 3 failed, 40 passed", result.Text.TrimEnd());
        Assert.Contains("...[4,031 of 5,031 characters left out here.", result.Text);
        Assert.Contains("find code with search_code", result.Text);
        Assert.Contains("read_file's offset and limit", result.Text);

        var saved = Assert.Single(Directory.GetFiles(scratch, "eval-*-full.txt"));
        Assert.Contains(saved, result.Text);
        Assert.EndsWith("Tests: 3 failed, 40 passed", File.ReadAllText(saved).TrimEnd());
        Assert.Equal(5_031, File.ReadAllText(saved).TrimEnd().Length);
    }

    [Fact]
    public async Task OutputWithinTheCap_IsUnchanged()
    {
        var (session, scratch) = NewSession(cap: 1_000);
        await using var _ = session;

        var result = await session.RunAsync("System.Console.Write(new string('y', 999));", CancellationToken.None);

        Assert.Equal(new string('y', 999), result.Text.Trim());
        Assert.Empty(Directory.GetFiles(scratch, "eval-*-full.txt"));
    }

    [Fact]
    public async Task WithNoCap_LongOutputIsUnchanged()
    {
        var (session, _) = NewSession(cap: null);
        await using var __ = session;

        var result = await session.RunAsync("System.Console.Write(new string('z', 20000));", CancellationToken.None);

        Assert.Equal(new string('z', 20_000), result.Text.Trim());
    }

    [Fact]
    public async Task AFailedEval_IsCappedToo_AndStaysAnError_WithItsErrorStillShown()
    {
        var (session, _) = NewSession(cap: 600);
        await using var __ = session;

        var result = await session.RunAsync(
            "System.Console.Write(new string('e', 3000)); throw new System.InvalidOperationException(\"boom at the end\");", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("characters left out here", result.Text);
        Assert.Contains("boom at the end", result.Text);
    }
}
