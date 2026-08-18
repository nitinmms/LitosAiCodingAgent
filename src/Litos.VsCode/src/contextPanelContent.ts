import * as vscode from "vscode";

/**
 * "View Context" breakdown panel — its own dedicated webview panel (opened via extension.ts's
 * openContextPanel, in turn triggered by clicking a chat panel's own context-usage row below its
 * composer — see webviewContent.ts's #contextUsage), mirroring Litos.Gui's ViewContextWindow
 * (double-click the status-bar context meter) and this extension's own McpServersWindow-equivalent
 * openMcpPanel. Renders a segmented color bar plus expandable category rows, fed by GET
 * /sessions/{id}/context/breakdown (ContextEndpoints.cs, wrapping the face-agnostic
 * Litos.Agent.Session.ContextBreakdown.Compute). One such panel per chat panel (PanelState.contextPanel),
 * not a single shared instance, since each chat panel is its own independent session.
 */
export function getContextPanelHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<style>
  :root { color-scheme: light dark; }
  body {
    margin: 0;
    padding: 16px;
    font-family: var(--vscode-font-family, sans-serif);
    font-size: var(--vscode-font-size, 13px);
    color: var(--vscode-foreground);
    background: var(--vscode-editor-background);
  }
  h2 { margin-top: 0; }
  #summary { opacity: 0.85; margin-bottom: 10px; }
  #bar {
    display: flex;
    height: 10px;
    border-radius: 5px;
    overflow: hidden;
    background: var(--vscode-widget-border, #444);
    margin-bottom: 16px;
  }
  #bar .segment { height: 100%; }
  #rows { display: flex; flex-direction: column; gap: 4px; }
  .row {
    background: var(--vscode-editorWidget-background, rgba(128,128,128,0.08));
    border-radius: 4px;
    padding: 6px 8px;
  }
  .row-header {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: default;
  }
  .row-header.expandable { cursor: pointer; }
  .dot { width: 10px; height: 10px; border-radius: 50%; flex: none; }
  .row-label { flex: 1; }
  .row-tokens, .row-percent { opacity: 0.75; }
  .row-percent { width: 44px; text-align: right; }
  .sub-list { margin: 4px 0 0 26px; display: flex; flex-direction: column; gap: 2px; }
  .sub-row { display: flex; justify-content: space-between; opacity: 0.75; }
  #caption { opacity: 0.6; font-size: 0.85em; margin-top: 14px; }
  #empty { opacity: 0.7; padding: 20px 0; display: none; }
</style>
</head>
<body>
<h2>Context usage</h2>
<div id="summary"></div>
<div id="bar"></div>
<div id="rows"></div>
<div id="empty">No context data yet — send a message first.</div>
<div id="caption"></div>
<script>
(function () {
  const vscode = acquireVsCodeApi();
  const summaryEl = document.getElementById('summary');
  const barEl = document.getElementById('bar');
  const rowsEl = document.getElementById('rows');
  const emptyEl = document.getElementById('empty');
  const captionEl = document.getElementById('caption');

  // One fixed color per category, matching Litos.Gui's ViewContextWindow.CategoryBrushes exactly
  // (same hex values) so the two clients render a given category the same way.
  const CATEGORY_COLORS = {
    SystemPrompt: '#569CD6',
    ToolSchemas: '#4EC9B0',
    Memory: '#C586C0',
    Skills: '#DCDCAA',
    History: '#CE9178',
    ToolResults: '#9CDCFE',
    Images: '#D7BA7D',
    CompactionSummary: '#808080',
  };

  const expandedCategories = new Set();
  let currentBreakdown = null;

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  function fmt(n) {
    return n.toLocaleString();
  }

  function render(breakdown) {
    currentBreakdown = breakdown;
    const total = breakdown.totalEstimatedTokens;

    emptyEl.style.display = total === 0 ? 'block' : 'none';

    const fraction = breakdown.contextLength ? total / breakdown.contextLength : 0;
    summaryEl.textContent = breakdown.contextLength
      ? fmt(total) + ' / ' + fmt(breakdown.contextLength) + ' tokens (' + Math.round(fraction * 100) + '%)'
      : fmt(total) + ' tokens';

    barEl.innerHTML = '';
    breakdown.entries.forEach((entry) => {
      if (total === 0 || entry.estimatedTokens <= 0) return;
      const seg = document.createElement('div');
      seg.className = 'segment';
      seg.style.width = (100 * entry.estimatedTokens / total) + '%';
      seg.style.background = CATEGORY_COLORS[entry.category] || '#888';
      seg.title = entry.label + ': ' + fmt(entry.estimatedTokens) + ' tokens';
      barEl.appendChild(seg);
    });

    rowsEl.innerHTML = '';
    breakdown.entries.forEach((entry) => rowsEl.appendChild(buildRow(entry, total)));

    captionEl.textContent = breakdown.lastRealUsageTokens != null
      ? 'Estimated from message content, scaled to match last real usage: ' + fmt(breakdown.lastRealUsageTokens) + ' tokens.'
      : 'Estimated from message content; no real usage reported yet this session.';
  }

  function buildRow(entry, total) {
    const wrapper = document.createElement('div');
    wrapper.className = 'row';

    const hasSubItems = entry.subItems && entry.subItems.length > 0;
    const expanded = hasSubItems && expandedCategories.has(entry.category);
    const percent = total === 0 ? 0 : (100 * entry.estimatedTokens / total);

    const header = document.createElement('div');
    header.className = 'row-header' + (hasSubItems ? ' expandable' : '');

    const dot = document.createElement('div');
    dot.className = 'dot';
    dot.style.background = CATEGORY_COLORS[entry.category] || '#888';
    header.appendChild(dot);

    const label = document.createElement('div');
    label.className = 'row-label';
    label.textContent = entry.label + (hasSubItems ? (expanded ? ' ▾' : ' ▸') : '');
    header.appendChild(label);

    const tokens = document.createElement('div');
    tokens.className = 'row-tokens';
    tokens.textContent = fmt(entry.estimatedTokens) + ' tokens';
    header.appendChild(tokens);

    const pct = document.createElement('div');
    pct.className = 'row-percent';
    pct.textContent = percent.toFixed(1) + '%';
    header.appendChild(pct);

    if (hasSubItems) {
      header.addEventListener('click', () => {
        if (expandedCategories.has(entry.category)) expandedCategories.delete(entry.category);
        else expandedCategories.add(entry.category);
        render(currentBreakdown);
      });
    }
    wrapper.appendChild(header);

    if (expanded) {
      const subList = document.createElement('div');
      subList.className = 'sub-list';
      entry.subItems.forEach((sub) => {
        const subRow = document.createElement('div');
        subRow.className = 'sub-row';
        subRow.innerHTML = '<span>' + escapeHtml(sub.label) + '</span><span>' + fmt(sub.estimatedTokens) + ' tokens</span>';
        subList.appendChild(subRow);
      });
      wrapper.appendChild(subList);
    }

    return wrapper;
  }

  window.addEventListener('message', (event) => {
    const message = event.data;
    if (message.type === 'breakdown') {
      render(message.breakdown);
    } else if (message.type === 'error') {
      summaryEl.textContent = message.text;
    }
  });

  vscode.postMessage({ type: 'refresh' });
})();
</script>
</body>
</html>`;
}
