using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;

namespace Litos.Gui;

/// <summary>
/// MCP server management popup: server list (name, transport summary, live status, enable/
/// disable, per-server refresh, remove) plus an add-server form — an Avalonia port of
/// McpServers.razor's field set and behavior, minus the Deny/Ask/Full permission controls (never
/// shown here: Litos.Gui's GuiApprovalGate auto-approves everything unconditionally, so surfacing
/// permission controls that are never consulted would be misleading, see ReadMe_MCPSupportInLitosGUI.md §0).
/// Plain Window opened via a static async factory, mirroring ListPickerWindow's convention — no
/// ViewModel, no IDialogService, this codebase has neither.
///
/// Live status (Connecting…/Connected/Unreachable) is only ever refreshed right after a button
/// click's Task completes — there's no background poller in Litos.Gui (§2.4/§3.3), so nothing
/// changes while the window sits idle between clicks.
/// </summary>
public sealed partial class McpServersWindow : Window
{
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush ErrorTextBrush = new SolidColorBrush(Color.Parse("#F48771"));
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));
    private static readonly IBrush RowBackgroundBrush = new SolidColorBrush(Color.Parse("#2D2D30"));

    private McpConfigStore _store = null!;
    private McpToolProvider _provider = null!;
    // Keyed by server name (not by row control, which RenderServerList discards and rebuilds on
    // every refresh) so a server's expanded/collapsed tool list survives Refresh/Add/Remove/Toggle.
    private readonly HashSet<string> _expandedServers = [];

    public McpServersWindow()
    {
        InitializeComponent();
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    public static async Task ShowAsync(Window owner, McpConfigStore store, McpToolProvider provider)
    {
        var window = new McpServersWindow
        {
            _store = store,
            _provider = provider,
        };
        window.RefreshAllButton.Click += async (_, _) => await window.RefreshAllAsync();
        window.AddServerButton.Click += async (_, _) => await window.AddServerAsync();
        window.NewTransportCombo.SelectionChanged += (_, _) => window.UpdateTransportFieldsVisibility();

        window.RenderServerList();
        await window.ShowDialog(owner);
    }

    private bool IsStdioTransportSelected => NewTransportCombo.SelectedIndex == 0;

    private void UpdateTransportFieldsVisibility()
    {
        StdioFieldsPanel.IsVisible = IsStdioTransportSelected;
        HttpFieldsPanel.IsVisible = !IsStdioTransportSelected;
    }

    private async Task RefreshAllAsync()
    {
        RefreshAllButton.IsEnabled = false;
        try
        {
            await _provider.RefreshAsync(McpRefreshTimeout, CancellationToken.None);
        }
        finally
        {
            RefreshAllButton.IsEnabled = true;
        }

        RenderServerList();
    }

    // Same perServerTimeout McpToolProvider.RefreshAsync bounds every (re)connect attempt with —
    // matches Litos.Api's mcpHandshakeTimeout (Program.cs), not the ~30s startup constant used
    // there, since a user-triggered click already implies they're willing to wait a moment; kept
    // distinct from Program.cs's McpHandshakeTimeout only in name, not value.
    private static readonly TimeSpan McpRefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pure form-values-to-definition mapping, split out so it's testable without an Avalonia
    /// control tree (same reasoning as MainWindow.DecideEnterAction / CommandMenuPopup.Filter).
    /// DefaultPermission is always Full and never a form field — MCP tool calls in Litos.Gui
    /// aren't gated at all, so DefaultPermission/ToolOverrides are stored but never consulted
    /// (ReadMe_MCPSupportInLitosGUI.md §0). Returns null and a validation message when the name is
    /// blank; McpConfigStore.Update itself catches the remaining cases (duplicate name, "__").
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

    private async Task AddServerAsync()
    {
        var definition = BuildDefinition(
            NewNameBox.Text, IsStdioTransportSelected, NewCommandBox.Text, NewArgsBox.Text, NewUrlBox.Text,
            NewEnabledCheckBox.IsChecked ?? true, out var validationError);
        if (definition is null)
        {
            ShowMessage(validationError!);
            return;
        }

        try
        {
            _store.Update(cfg => cfg with { Servers = [.. cfg.Servers, definition] });
        }
        catch (McpConfigValidationException ex)
        {
            ShowMessage(ex.Message);
            return;
        }

        NewNameBox.Text = "";
        NewCommandBox.Text = "";
        NewArgsBox.Text = "";
        NewUrlBox.Text = "";
        HideMessage();

        await RefreshAllAsync();
    }

    private async Task RemoveServerAsync(string name)
    {
        _store.Update(cfg => cfg with { Servers = [.. cfg.Servers.Where(s => s.Name != name)] });
        await RefreshAllAsync();
    }

    private async Task ToggleEnabledAsync(string name)
    {
        _store.Update(cfg => cfg with
        {
            Servers = [.. cfg.Servers.Select(s => s.Name == name ? s with { Enabled = !s.Enabled } : s)],
        });
        await RefreshAllAsync();
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.IsVisible = true;
    }

    private void HideMessage() => MessageText.IsVisible = false;

    private void RenderServerList()
    {
        ServerListPanel.Children.Clear();

        var config = _store.Current;
        if (config.Servers.Count == 0)
        {
            ServerListPanel.Children.Add(new TextBlock
            {
                Text = "No MCP servers configured yet.",
                Foreground = DimTextBrush,
            });
            return;
        }

        var connections = _provider.Connections;
        foreach (var server in config.Servers)
        {
            var connection = connections.FirstOrDefault(c => c.ServerName == server.Name);
            ServerListPanel.Children.Add(BuildServerRow(server, connection));
        }
    }

    private Control BuildServerRow(McpServerDefinition server, McpServerConnection? connection)
    {
        var summary = server.Transport == McpTransportKind.Stdio
            ? $"{server.Command} {string.Join(' ', server.Args ?? [])}"
            : server.Url;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = server.Name, FontWeight = FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = StatusLabel(connection?.Status), Foreground = StatusBrush(connection?.Status) });
        header.Children.Add(new TextBlock
        {
            Text = server.Enabled ? "Enabled" : "Disabled",
            Foreground = DimTextBrush,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        var refreshButton = new Button { Content = "Refresh" };
        // McpToolProvider.RefreshAsync's own diff logic already only (re)connects servers that
        // are new/changed/due for retry (DefinitionsMatch/backoff checks), so refreshing "all" to
        // refresh "one" is not wasteful — see ReadMe_MCPSupportInLitosGUI.md §3.3.
        refreshButton.Click += async (_, _) => await RefreshAllAsync();
        var toggleButton = new Button { Content = server.Enabled ? "Disable" : "Enable" };
        toggleButton.Click += async (_, _) => await ToggleEnabledAsync(server.Name);
        var removeButton = new Button { Content = "Remove" };
        removeButton.Click += async (_, _) => await RemoveServerAsync(server.Name);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(toggleButton);
        buttons.Children.Add(removeButton);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = summary, Foreground = DimTextBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (connection is { Status: McpConnectionStatus.Unreachable, Error: { } error })
            panel.Children.Add(new TextBlock { Text = $"Unreachable: {error}", Foreground = ErrorTextBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (connection is { Status: McpConnectionStatus.Connected })
            panel.Children.Add(BuildToolsSection(server.Name, connection));
        panel.Children.Add(buttons);

        return new Border
        {
            Background = RowBackgroundBrush,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(10, 8),
            Child = panel,
        };
    }

    /// <summary>
    /// "N tools ▸" (collapsed) / "N tools ▾" (expanded) toggle, same click-to-expand mechanic as
    /// ToolCallRow in the transcript. Only rendered for a Connected server — Connecting/Unreachable/
    /// not-started servers have no Tools list worth summarizing.
    /// </summary>
    private Control BuildToolsSection(string serverName, McpServerConnection connection)
    {
        var expanded = _expandedServers.Contains(serverName);

        var toggle = new TextBlock
        {
            Text = ToolsSummaryLabel(connection.Tools.Count, expanded),
            Foreground = DimTextBrush,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        toggle.PointerPressed += (_, _) =>
        {
            if (!_expandedServers.Add(serverName))
                _expandedServers.Remove(serverName);
            RenderServerList();
        };

        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(toggle);

        if (expanded)
        {
            var toolList = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(16, 2, 0, 0) };
            foreach (var tool in connection.Tools)
            {
                toolList.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = tool.Name, FontWeight = FontWeight.Bold },
                        new TextBlock
                        {
                            Text = string.IsNullOrEmpty(tool.Description) ? "(no description)" : tool.Description,
                            Foreground = DimTextBrush,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    },
                });
            }

            section.Children.Add(toolList);
        }

        return section;
    }

    /// <summary>Pure label formatting for the tools expand/collapse toggle, split out so it's
    /// testable without an Avalonia control tree (same reasoning as BuildDefinition/StatusLabel).</summary>
    internal static string ToolsSummaryLabel(int toolCount, bool expanded) =>
        $"{toolCount} tool{(toolCount == 1 ? "" : "s")} {(expanded ? "▾" : "▸")}";

    /// <summary>Pure status-to-label mapping, mirroring McpServers.razor's StatusLabel exactly (same four cases).</summary>
    internal static string StatusLabel(McpConnectionStatus? status) => status switch
    {
        McpConnectionStatus.Connected => "Connected",
        McpConnectionStatus.Connecting => "Connecting…",
        McpConnectionStatus.Unreachable => "Unreachable",
        _ => "Not started",
    };

    private static IBrush StatusBrush(McpConnectionStatus? status) => status switch
    {
        McpConnectionStatus.Connected => ConnectedBrush,
        McpConnectionStatus.Connecting => ConnectingBrush,
        McpConnectionStatus.Unreachable => ErrorTextBrush,
        _ => DimTextBrush,
    };
}
