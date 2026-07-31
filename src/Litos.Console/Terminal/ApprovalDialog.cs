using Litos.Tools.Shell;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// Implements IToolApprovalGate as a real Terminal.Gui modal Dialog, replacing
/// ConsoleApprovalGate's Spectre.Console SelectionPrompt/plain-text prompt.
///
/// IToolApprovalGate.RequestAsync is awaited from inside tool execution, i.e. from the
/// background Task driving AgentLoop.RunTurnAsync's IAsyncEnumerable — not from Terminal.Gui's
/// own UI thread. Every Terminal.Gui call (constructing/running the dialog) must therefore be
/// marshaled onto the UI thread via Application.Invoke, and the awaited Task&lt;ApprovalDecision&gt;
/// is bridged back via a TaskCompletionSource, per ReadMe_AgentDesign.md §7.5's concurrency
/// model note. "Approve Always" is remembered for the rest of the process, same as the old gate.
/// </summary>
public sealed class ApprovalDialog(IApplication app) : IToolApprovalGate
{
    private volatile bool _approveAll;

    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        if (_approveAll)
            return Task.FromResult(ApprovalDecision.Approve);

        var tcs = new TaskCompletionSource<ApprovalDecision>();

        app.Invoke(() =>
        {
            try
            {
                var decision = ShowDialog(app, preview);
                if (decision == ApprovalDecision.ApproveAlways)
                    _approveAll = true;
                tcs.SetResult(decision);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static ApprovalDecision ShowDialog(IApplication app, ToolInvocationPreview preview)
    {
        var dialog = new Dialog<ApprovalDecision>
        {
            Title = "Approve this action?",
            Width = Dim.Percent(80),
            Height = Dim.Percent(80),
            // Never silently approve a tool invocation the user didn't explicitly confirm —
            // closing the dialog without picking a button (e.g. Esc) defaults to Deny.
            Result = ApprovalDecision.Deny,
        };

        var summary = new Label { Text = preview.Summary, X = 0, Y = 0, Width = Dim.Fill() };
        dialog.Add(summary);

        if (preview.DiffOrCommand is { } content)
        {
            var looksLikeDiff = content.StartsWith("---") || content.Contains("\n---");
            View previewView = looksLikeDiff
                ? new DiffView(content) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 1 }
                : new TextView
                {
                    Text = content,
                    ReadOnly = true,
                    X = 0,
                    Y = 1,
                    Width = Dim.Fill(),
                    Height = Dim.Fill() - 1,
                };
            dialog.Add(previewView);
        }

        void Choose(ApprovalDecision decision)
        {
            dialog.Result = decision;
            dialog.RequestStop();
        }

        var approveButton = new Button { Text = "Approve", IsDefault = true };
        approveButton.Accepting += (_, _) => Choose(ApprovalDecision.Approve);
        var approveAlwaysButton = new Button { Text = "Approve Always" };
        approveAlwaysButton.Accepting += (_, _) => Choose(ApprovalDecision.ApproveAlways);
        var denyButton = new Button { Text = "Deny" };
        denyButton.Accepting += (_, _) => Choose(ApprovalDecision.Deny);

        dialog.AddButton(approveButton);
        dialog.AddButton(approveAlwaysButton);
        dialog.AddButton(denyButton);

        app.Run(dialog, null);

        return dialog.Result;
    }
}
