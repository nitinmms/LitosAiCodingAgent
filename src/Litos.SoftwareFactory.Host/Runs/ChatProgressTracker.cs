using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// What a chat answer in progress has done, kept from its worker's events, so the person waiting
/// can see it working (m2-architecture.md §5). Safe to call from the turn's stream while
/// <see cref="ChatExecutor"/> reads it on its own timer.
/// </summary>
public sealed class ChatProgressTracker(Guid runId, DateTimeOffset startedAt)
{
    internal const string Fetching = "Getting the latest code";
    internal const string Starting = "Starting";
    internal const string Thinking = "Thinking";
    internal const string RunningCode = "Running code over the repository";

    private readonly Lock _lock = new();
    private int _modelCalls;
    private int _toolCalls;
    private string _activity = Fetching;

    /// <summary>Goes up with every change, so a reader can tell whether there is anything new.</summary>
    public int Version { get; private set; }

    public ChatProgress Snapshot
    {
        get { lock (_lock) return new ChatProgress(runId, startedAt, _modelCalls, _toolCalls, _activity); }
    }

    /// <summary>A step of the host's own, before the turn starts.</summary>
    public void Set(string activity)
    {
        lock (_lock)
        {
            _activity = activity;
            Version++;
        }
    }

    public void Apply(TurnProgress progress)
    {
        lock (_lock)
        {
            switch (progress.Kind)
            {
                case TurnProgressKind.ToolCall:
                    _activity = Describe(progress);
                    break;
                case TurnProgressKind.ToolResult:
                    // Tool results go back to the model, which is what runs next.
                    _toolCalls++;
                    _activity = Thinking;
                    break;
                case TurnProgressKind.ModelReply:
                    _modelCalls++;
                    break;
            }

            Version++;
        }
    }

    /// <summary>"Read src/Orders.cs", "Search "Export" in src": the same words the other Litos faces use.</summary>
    internal static string Describe(TurnProgress call)
    {
        if (call.ToolName is null)
            return Thinking;
        if (call.ToolName == ReservedToolNames.KernelCode)
            return RunningCode;

        var described = ToolCallSummary.DescribeCall(call.ToolName, call.Arguments).Trim();
        const int MaxLength = 120;
        return described.Length > MaxLength ? described[..MaxLength] + "..." : described;
    }
}
