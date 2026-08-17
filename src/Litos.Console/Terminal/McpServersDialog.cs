using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Litos.Console.Terminal;

/// <summary>
/// "/mcp" server management dialog: an embedded add-server form (name, stdio/HTTP transport
/// toggle, command+args or URL, enabled checkbox) plus a server list with status, enable/disable,
/// remove, and refresh, flattened into ListView rows via a pure, testable BuildRows(...) function
/// (mirroring the pure/UI-free split Litos.Gui's McpServersWindow.BuildDefinition already uses)
/// since Terminal.Gui has no native expandable-list widget. Tool/prompt browsing per server is
/// click-to-expand (ListView's Accepting event on the selected row) rather than a separate
/// details pane.
///
/// The riskiest dialog in the Console parity plan: it's the first with async work inside it
/// (RefreshAsync after add/remove/toggle) — strictly fire-and-forget, with the re-render
/// marshaled back via app.Invoke, never blocking, to avoid the deadlock shape AttachDialog's own
/// doc comment warns about. Reached from active key dispatch, so this follows the same
/// Begin/Iteration/TaskCompletionSource shape as every other new dialog in this plan.
/// </summary>
public static class McpServersDialog
{
    // Matches McpServersWindow's own McpRefreshTimeout — a user-triggered click already implies
    // willingness to wait a moment, unlike the fire-and-forget startup connect's shorter budget.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>One flattened, renderable row — either a server header or one of its expanded tool/prompt lines.</summary>
    internal sealed record ServerRow(string Text, string? ServerName, bool IsHeader)
    {
        public override string ToString() => Text;
    }

    public static Task ShowAsync(IApplication app, McpConfigStore store, McpToolProvider provider)
    {
        var dialog = new Dialog<bool>
        {
            Title = "MCP Servers",
            Width = Dim.Percent(90),
            Height = Dim.Percent(85),
        };

        var expandedServers = new HashSet<string>();
        var listView = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 12 };
        dialog.Add(listView);

        // Kept alongside ListView.Source (rather than reading rows back through
        // IListDataSource, which has no typed indexer in this Terminal.Gui version) purely so
        // button handlers can resolve "which server is selected" from listView.SelectedItem.
        var currentRows = new List<ServerRow>();

        void Render()
        {
            currentRows = [.. BuildRows(store.Current, provider.Connections, expandedServers)];
            listView.SetSource(new System.Collections.ObjectModel.ObservableCollection<ServerRow>(currentRows));
        }

        async Task RefreshAllAsync()
        {
            await provider.RefreshAsync(RefreshTimeout, CancellationToken.None);
            app.Invoke(Render);
        }

        ServerRow? SelectedRow() =>
            listView.SelectedItem is { } selected && selected >= 0 && selected < currentRows.Count ? currentRows[selected] : null;

        listView.Accepting += (_, e) =>
        {
            if (SelectedRow() is not { IsHeader: true, ServerName: { } name })
                return;

            if (!expandedServers.Add(name))
                expandedServers.Remove(name);
            Render();
            e.Handled = true;
        };

        var isStdioTransport = true;
        var nameField = new TextField { X = 0, Y = Pos.Bottom(listView) + 1, Width = Dim.Percent(30) };
        var transportCheck = new CheckBox { X = Pos.Right(nameField) + 2, Y = Pos.Y(nameField), Text = "Stdio (unchecked = HTTP)", Value = CheckState.Checked };
        transportCheck.ValueChanged += (_, _) => isStdioTransport = transportCheck.Value == CheckState.Checked;
        var commandField = new TextField { X = 0, Y = Pos.Bottom(nameField) + 1, Width = Dim.Percent(45) };
        var argsField = new TextField { X = Pos.Right(commandField) + 2, Y = Pos.Y(commandField), Width = Dim.Fill() };
        var urlField = new TextField { X = 0, Y = Pos.Bottom(commandField) + 1, Width = Dim.Fill() };
        var enabledCheck = new CheckBox { X = 0, Y = Pos.Bottom(urlField) + 1, Text = "Enabled", Value = CheckState.Checked };
        var messageLabel = new Label { X = 0, Y = Pos.Bottom(enabledCheck) + 1, Width = Dim.Fill(), Text = string.Empty };

        dialog.Add(
            new Label { Text = "Name:", X = 0, Y = Pos.Top(nameField) - 1 }, nameField, transportCheck,
            new Label { Text = "Command:", X = 0, Y = Pos.Top(commandField) - 1 }, commandField, argsField,
            new Label { Text = "URL (HTTP transport):", X = 0, Y = Pos.Top(urlField) - 1 }, urlField,
            enabledCheck, messageLabel);

        var addButton = new Button { Text = "Add" };
        addButton.Accepting += (_, _) =>
        {
            var definition = BuildDefinition(
                nameField.Text?.ToString(), isStdioTransport, commandField.Text?.ToString(), argsField.Text?.ToString(),
                urlField.Text?.ToString(), enabledCheck.Value == CheckState.Checked, out var error);

            if (definition is null)
            {
                messageLabel.Text = error ?? string.Empty;
                return;
            }

            try
            {
                store.Update(cfg => cfg with { Servers = [.. cfg.Servers, definition] });
            }
            catch (McpConfigValidationException ex)
            {
                messageLabel.Text = ex.Message;
                return;
            }

            nameField.Text = string.Empty;
            commandField.Text = string.Empty;
            argsField.Text = string.Empty;
            urlField.Text = string.Empty;
            messageLabel.Text = string.Empty;
            _ = RefreshAllAsync();
        };

        var refreshButton = new Button { Text = "Refresh all" };
        refreshButton.Accepting += (_, _) => _ = RefreshAllAsync();

        var toggleButton = new Button { Text = "Enable/Disable selected" };
        toggleButton.Accepting += (_, _) =>
        {
            if (SelectedRow() is not { ServerName: { } name })
                return;
            store.Update(cfg => cfg with { Servers = [.. cfg.Servers.Select(s => s.Name == name ? s with { Enabled = !s.Enabled } : s)] });
            _ = RefreshAllAsync();
        };

        var removeButton = new Button { Text = "Remove selected" };
        removeButton.Accepting += (_, _) =>
        {
            if (SelectedRow() is not { ServerName: { } name })
                return;
            store.Update(cfg => cfg with { Servers = [.. cfg.Servers.Where(s => s.Name != name)] });
            _ = RefreshAllAsync();
        };

        var closeButton = new Button { Text = "Close", IsDefault = true };
        closeButton.Accepting += (_, _) => dialog.RequestStop();

        dialog.AddButton(addButton);
        dialog.AddButton(refreshButton);
        dialog.AddButton(toggleButton);
        dialog.AddButton(removeButton);
        dialog.AddButton(closeButton);

        Render();

        var tcs = new TaskCompletionSource();
        var token = app.Begin(dialog);
        if (token is null)
        {
            tcs.SetResult();
            return tcs.Task;
        }

        void OnIteration(object? sender, EventArgs e)
        {
            if (!dialog.StopRequested)
                return;

            app.Iteration -= OnIteration;
            app.End(token);
            tcs.SetResult();
        }

        app.Iteration += OnIteration;
        return tcs.Task;
    }

    /// <summary>
    /// Pure form-values-to-definition mapping, split out so it's testable without a Terminal.Gui
    /// control tree — mirrors Litos.Gui's McpServersWindow.BuildDefinition exactly, including
    /// always setting DefaultPermission to Full and never exposing Deny/Ask/Full controls: the
    /// field exists in McpServerDefinition but nothing ever consults it once AutoApprovalGate
    /// auto-approves everything (Slice 0.4), so surfacing permission controls that are never
    /// consulted would be misleading.
    /// </summary>
    internal static McpServerDefinition? BuildDefinition(
        string? name, bool isStdio, string? command, string? args, string? url, bool enabled, out string? error)
    {
        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0)
        {
            error = "A server name is required.";
            return null;
        }

        error = null;
        return new McpServerDefinition(
            Name: trimmedName,
            Transport: isStdio ? McpTransportKind.Stdio : McpTransportKind.Http,
            Command: isStdio ? (command?.Trim() ?? "") : null,
            Args: isStdio
                ? (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null,
            Env: null,
            Url: isStdio ? null : (url?.Trim() ?? ""),
            Enabled: enabled,
            DefaultPermission: ToolPermission.Full,
            ToolOverrides: null);
    }

    /// <summary>
    /// Flattens the current config + live connections into renderable rows: one header row per
    /// configured server (name, status, enabled/disabled, tool/prompt counts), followed by one
    /// row per tool/prompt when that server's name is in <paramref name="expandedServers"/>. Pure
    /// and Terminal.Gui-free, unit-testable without a real McpToolProvider connection.
    /// </summary>
    internal static IReadOnlyList<ServerRow> BuildRows(
        McpConfig config, IReadOnlyList<McpServerConnection> connections, IReadOnlySet<string> expandedServers)
    {
        if (config.Servers.Count == 0)
            return [new ServerRow("No MCP servers configured yet. Fill in the form below and click Add.", null, IsHeader: false)];

        var rows = new List<ServerRow>();
        foreach (var server in config.Servers)
        {
            var connection = connections.FirstOrDefault(c => c.ServerName == server.Name);
            var summary = server.Transport == McpTransportKind.Stdio
                ? $"{server.Command} {string.Join(' ', server.Args ?? [])}"
                : server.Url;
            var expanded = expandedServers.Contains(server.Name);
            var toolCount = connection?.Tools.Count ?? 0;
            var promptCount = connection?.Prompts.Count ?? 0;

            rows.Add(new ServerRow(
                $"{(expanded ? "▾" : "▸")} {server.Name}  [{StatusLabel(connection?.Status)}]  {(server.Enabled ? "Enabled" : "Disabled")}  {summary}  ({ToolsSummaryLabel(toolCount)}, {PromptsSummaryLabel(promptCount)})",
                server.Name, IsHeader: true));

            if (connection is { Status: McpConnectionStatus.Unreachable, Error: { } unreachableError })
                rows.Add(new ServerRow($"    Unreachable: {unreachableError}", server.Name, IsHeader: false));
            if (connection is { Status: McpConnectionStatus.Failed, Error: { } failedError })
                rows.Add(new ServerRow($"    Gave up after repeated failures: {failedError}. Disable and re-enable, or edit, to retry.", server.Name, IsHeader: false));

            if (!expanded)
                continue;

            foreach (var tool in connection?.Tools ?? [])
                rows.Add(new ServerRow($"    tool: {tool.Name} — {(string.IsNullOrEmpty(tool.Description) ? "(no description)" : tool.Description)}", server.Name, IsHeader: false));
            foreach (var prompt in connection?.Prompts ?? [])
                rows.Add(new ServerRow($"    prompt: {prompt.Name} — {(string.IsNullOrEmpty(prompt.Description) ? "(no description)" : prompt.Description)}", server.Name, IsHeader: false));
        }

        return rows;
    }

    /// <summary>Pure status-to-label mapping, mirroring McpServersWindow.StatusLabel exactly (same five cases).</summary>
    internal static string StatusLabel(McpConnectionStatus? status) => status switch
    {
        McpConnectionStatus.Connected => "Connected",
        McpConnectionStatus.Connecting => "Connecting…",
        McpConnectionStatus.Unreachable => "Unreachable",
        McpConnectionStatus.Failed => "Failed",
        _ => "Not started",
    };

    internal static string ToolsSummaryLabel(int toolCount) => $"{toolCount} tool{(toolCount == 1 ? "" : "s")}";

    internal static string PromptsSummaryLabel(int promptCount) => $"{promptCount} prompt{(promptCount == 1 ? "" : "s")}";
}
