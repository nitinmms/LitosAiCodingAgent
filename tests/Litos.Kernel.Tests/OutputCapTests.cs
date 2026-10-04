using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// What an eval prints stays in the conversation and is paid for again on every later call. A
/// factory task's context reached 67,000 tokens from ten whole files printed while exploring, and
/// a brief asking for less did not change that. A session can cap what one eval returns: the full
/// text goes to a scratch file, and the model sees the start and where the rest is.
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
    public async Task OutputPastTheCap_IsCut_AndTheWholeTextIsSavedForLater()
    {
        var (session, scratch) = NewSession(cap: 1_000);
        await using var _ = session;

        var result = await session.RunAsync("System.Console.Write(new string('x', 5000) + \"END\");", CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith(new string('x', 1_000) + "\n...[This script printed 5,003 characters; only the first 1,000 are shown.", result.Text);
        Assert.Contains("find code with search_code", result.Text);
        Assert.Contains("read_file's offset and limit", result.Text);
        Assert.DoesNotContain("END", result.Text);

        var saved = Assert.Single(Directory.GetFiles(scratch, "eval-*-full.txt"));
        Assert.Contains(saved, result.Text);
        Assert.EndsWith("END", File.ReadAllText(saved).TrimEnd());
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
    public async Task AFailedEval_IsCappedToo_AndStaysAnError()
    {
        var (session, _) = NewSession(cap: 500);
        await using var __ = session;

        var result = await session.RunAsync("System.Console.Write(new string('e', 3000)); throw new System.InvalidOperationException(\"boom\");", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("only the first 500 are shown", result.Text);
    }
}
