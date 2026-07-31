using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class TelegramPairingTests
{
    [Fact]
    public void TryConsume_MatchingCode_SucceedsOnce()
    {
        var pairing = new TelegramPairing();
        var code = pairing.IssueCode();

        Assert.True(pairing.TryConsume(code));
        Assert.False(pairing.TryConsume(code)); // single-use: second attempt fails
    }

    [Fact]
    public void TryConsume_WrongCode_Fails()
    {
        var pairing = new TelegramPairing();
        pairing.IssueCode();

        Assert.False(pairing.TryConsume("not-the-real-code"));
    }

    [Fact]
    public void TryConsume_NoCodeIssued_Fails()
    {
        var pairing = new TelegramPairing();

        Assert.False(pairing.TryConsume("anything"));
    }

    [Fact]
    public void IssueCode_Twice_InvalidatesThePreviousCode()
    {
        var pairing = new TelegramPairing();
        var first = pairing.IssueCode();
        var second = pairing.IssueCode();

        Assert.False(pairing.TryConsume(first));
        Assert.True(pairing.TryConsume(second));
    }

    [Fact]
    public void IssueCode_ProducesDistinctCodesEachTime()
    {
        var pairing = new TelegramPairing();
        var codes = Enumerable.Range(0, 20).Select(_ => pairing.IssueCode()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }
}
