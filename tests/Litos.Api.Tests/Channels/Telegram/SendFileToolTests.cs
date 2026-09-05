using System.Text.Json;
using Litos.Api.Channels;
using Litos.Api.Channels.Telegram;
using Litos.Api.Tests.Fakes;
using Litos.Tools.Shell;

namespace Litos.Api.Tests.Channels.Telegram;

public class SendFileToolTests : IDisposable
{
    private sealed class StubApprovalGate : IToolApprovalGate
    {
        public ApprovalDecision DecisionToReturn { get; set; } = ApprovalDecision.Approve;
        public ToolInvocationPreview? LastPreview { get; private set; }

        public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
        {
            LastPreview = preview;
            return Task.FromResult(DecisionToReturn);
        }
    }

    private const long ChatId = 555;
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}.txt");

    private static Task RunAsTelegramAsync(Func<Task> action) => ChannelContext.RunAsAsync("telegram", ChatId.ToString(), action);

    private static JsonElement Args(string path, string? caption = null) =>
        JsonSerializer.SerializeToElement(new { path, caption });

    [Fact]
    public async Task InvokeAsync_OutsideTelegramTurn_ReturnsError_WithoutSendingOrPrompting()
    {
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);

        var result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("only available in a Telegram chat", result.Text);
        Assert.Empty(bot.SentDocuments);
        Assert.Null(approvalGate.LastPreview);
    }

    [Fact]
    public async Task InvokeAsync_FileDoesNotExist_ReturnsError_WithoutPromptingForApproval()
    {
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);
        var missingPath = Path.Combine(Path.GetTempPath(), $"litos-missing-{Guid.NewGuid():n}.txt");

        var result = default(Litos.Agent.Tools.ToolResult);
        await RunAsTelegramAsync(async () => result = await tool.InvokeAsync(Args(missingPath), CancellationToken.None));

        Assert.True(result!.IsError);
        Assert.Contains("not found", result.Text);
        Assert.Null(approvalGate.LastPreview);
    }

    [Fact]
    public async Task InvokeAsync_ApprovalDenied_ReturnsError_WithoutSending()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate { DecisionToReturn = ApprovalDecision.Deny };
        var tool = new SendFileTool(bot, approvalGate);

        var result = default(Litos.Agent.Tools.ToolResult);
        await RunAsTelegramAsync(async () => result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None));

        Assert.True(result!.IsError);
        Assert.Contains("denied", result.Text);
        Assert.Empty(bot.SentDocuments);
    }

    [Fact]
    public async Task InvokeAsync_Approved_SendsDocumentToTheOriginatingChat_WithCaption()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);

        var result = default(Litos.Agent.Tools.ToolResult);
        await RunAsTelegramAsync(async () =>
            result = await tool.InvokeAsync(Args(_tempFile, "here's the report"), CancellationToken.None));

        Assert.False(result!.IsError);
        var sent = Assert.Single(bot.SentDocuments);
        Assert.Equal(ChatId, sent.ChatId.Identifier);
        Assert.Equal(Path.GetFileName(_tempFile), sent.FileName);
        Assert.Equal("here's the report", sent.Caption);
    }

    [Fact]
    public async Task InvokeAsync_Approved_RequestsApprovalWithToolNameAndPath()
    {
        await File.WriteAllTextAsync(_tempFile, "hello");
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);

        await RunAsTelegramAsync(() => tool.InvokeAsync(Args(_tempFile), CancellationToken.None));

        Assert.Equal("send_file", approvalGate.LastPreview!.ToolName);
        Assert.Contains(_tempFile, approvalGate.LastPreview.Summary);
    }

    [Fact]
    public async Task InvokeAsync_FileExceedsTelegramLimit_ReturnsError_WithoutPromptingForApproval()
    {
        // Writing a real 50MB+ file would slow the suite down for no benefit — the size check
        // reads FileInfo.Length before ever opening the file, so a sparse/truncated file (whose
        // reported Length still reflects the requested size) exercises the same code path.
        using (var fs = new FileStream(_tempFile, FileMode.Create))
            fs.SetLength(51 * 1024 * 1024);

        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);

        var result = default(Litos.Agent.Tools.ToolResult);
        await RunAsTelegramAsync(async () => result = await tool.InvokeAsync(Args(_tempFile), CancellationToken.None));

        Assert.True(result!.IsError);
        Assert.Contains("50MB", result.Text);
        Assert.Null(approvalGate.LastPreview);
    }

    [Fact]
    public async Task InvokeAsync_MissingPathArgument_ReturnsError()
    {
        // A local model's tool-call JSON omitting a required argument is a real, model-driven
        // failure mode — must degrade to a clean ToolResult.Error, not throw.
        var bot = new FakeTelegramBotClient();
        var approvalGate = new StubApprovalGate();
        var tool = new SendFileTool(bot, approvalGate);
        var emptyArgs = JsonSerializer.SerializeToElement(new { });

        var result = default(Litos.Agent.Tools.ToolResult);
        await RunAsTelegramAsync(async () => result = await tool.InvokeAsync(emptyArgs, CancellationToken.None));

        Assert.True(result!.IsError);
        Assert.Equal("The 'path' argument is required.", result.Text);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }
}
