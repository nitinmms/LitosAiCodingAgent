import * as vscode from "vscode";

/**
 * /mcp management UI — its own dedicated webview panel (opened via extension.ts's openMcpPanel),
 * mirroring Litos.Gui's McpServersWindow being its own window rather than folding server
 * management into the chat panel. In-webview custom UI, same rationale as the chat panel's own
 * command menu/pickers.
 */
export function getMcpPanelHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
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
  #toolbar { display: flex; gap: 8px; margin-bottom: 16px; }
  button {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
    border: none;
    border-radius: 4px;
    padding: 6px 14px;
    cursor: pointer;
  }
  button.secondary { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  button.danger { background: var(--vscode-errorForeground); color: white; }
  #error { color: var(--vscode-errorForeground); margin-bottom: 12px; display: none; }
  #error.visible { display: block; }
  .server {
    border: 1px solid var(--vscode-widget-border, #444);
    border-radius: 6px;
    padding: 12px;
    margin-bottom: 10px;
  }
  .server-header { display: flex; justify-content: space-between; align-items: center; }
  .server-name { font-weight: 600; }
  .server-status { font-size: 0.85em; padding: 2px 8px; border-radius: 10px; }
  .server-status.Connected { background: var(--vscode-charts-green, #3fb950); color: #000; }
  .server-status.Connecting { background: var(--vscode-charts-yellow, #d29922); color: #000; }
  .server-status.Unreachable, .server-status.Disconnected { background: var(--vscode-errorForeground); color: #fff; }
  .server-detail { opacity: 0.75; font-size: 0.85em; margin-top: 6px; }
  .server-actions { display: flex; gap: 8px; margin-top: 10px; }
  #empty { opacity: 0.7; padding: 20px 0; }
  #addForm {
    display: none;
    border: 1px solid var(--vscode-widget-border, #444);
    border-radius: 6px;
    padding: 12px;
    margin-bottom: 16px;
  }
  #addForm.visible { display: block; }
  #addForm .field { margin-bottom: 8px; }
  #addForm label { display: block; font-size: 0.85em; opacity: 0.8; margin-bottom: 2px; }
  #addForm input, #addForm select {
    width: 100%;
    box-sizing: border-box;
    background: var(--vscode-input-background);
    color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent);
    border-radius: 4px;
    padding: 6px 8px;
    font-family: inherit;
    font-size: inherit;
  }
</style>
</head>
<body>
<h2>MCP Servers</h2>
<div id="error"></div>
<div id="toolbar">
  <button id="addToggleBtn">Add server</button>
  <button class="secondary" id="refreshBtn">Refresh all</button>
</div>
<div id="addForm">
  <div class="field"><label>Name</label><input type="text" id="f-name" placeholder="my-server"></div>
  <div class="field"><label>Transport</label>
    <select id="f-transport"><option value="0">Stdio</option><option value="1">HTTP</option></select>
  </div>
  <div class="field" id="f-command-field"><label>Command</label><input type="text" id="f-command" placeholder="npx"></div>
  <div class="field" id="f-args-field"><label>Args (space-separated)</label><input type="text" id="f-args" placeholder="-y @modelcontextprotocol/server-everything"></div>
  <div class="field" id="f-url-field" style="display:none"><label>URL</label><input type="text" id="f-url" placeholder="https://example.com/mcp"></div>
  <div class="field"><label>Default permission</label>
    <select id="f-permission"><option value="0">Deny</option><option value="1">Ask</option><option value="2">Full</option></select>
  </div>
  <div class="server-actions">
    <button id="f-submitBtn">Add</button>
    <button class="secondary" id="f-cancelBtn">Cancel</button>
  </div>
</div>
<div id="servers"></div>
<div id="empty" style="display:none">No MCP servers configured yet.</div>
<script>
(function () {
  const vscode = acquireVsCodeApi();
  const errorEl = document.getElementById('error');
  const serversEl = document.getElementById('servers');
  const emptyEl = document.getElementById('empty');
  const addFormEl = document.getElementById('addForm');
  const transportEl = document.getElementById('f-transport');

  document.getElementById('addToggleBtn').addEventListener('click', () => {
    addFormEl.classList.toggle('visible');
  });
  document.getElementById('f-cancelBtn').addEventListener('click', () => {
    addFormEl.classList.remove('visible');
  });
  document.getElementById('refreshBtn').addEventListener('click', () => {
    vscode.postMessage({ type: 'refreshServers' });
  });
  transportEl.addEventListener('change', () => {
    const isHttp = transportEl.value === '1';
    document.getElementById('f-command-field').style.display = isHttp ? 'none' : 'block';
    document.getElementById('f-args-field').style.display = isHttp ? 'none' : 'block';
    document.getElementById('f-url-field').style.display = isHttp ? 'block' : 'none';
  });

  document.getElementById('f-submitBtn').addEventListener('click', () => {
    const name = document.getElementById('f-name').value.trim();
    if (!name) return;
    const transport = Number(transportEl.value);
    const server = {
      Name: name,
      Transport: transport,
      Command: transport === 0 ? (document.getElementById('f-command').value.trim() || null) : null,
      Args: transport === 0 ? document.getElementById('f-args').value.trim().split(/\\s+/).filter(Boolean) : null,
      Url: transport === 1 ? (document.getElementById('f-url').value.trim() || null) : null,
      DefaultPermission: Number(document.getElementById('f-permission').value),
    };
    vscode.postMessage({ type: 'add', server });
    addFormEl.classList.remove('visible');
  });

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  function renderServers(servers) {
    serversEl.innerHTML = '';
    emptyEl.style.display = servers.length === 0 ? 'block' : 'none';

    servers.forEach((server) => {
      const el = document.createElement('div');
      el.className = 'server';

      const header = document.createElement('div');
      header.className = 'server-header';
      header.innerHTML = '<span class="server-name">' + escapeHtml(server.name) + '</span>' +
        '<span class="server-status ' + escapeHtml(server.status) + '">' + escapeHtml(server.status) + '</span>';
      el.appendChild(header);

      const detail = document.createElement('div');
      detail.className = 'server-detail';
      const permissionNames = ['Deny', 'Ask', 'Full'];
      const transportLabel = server.transport === 1 ? (server.url || '') : (server.command || '') + ' ' + (server.args || []).join(' ');
      detail.textContent = transportLabel + ' — ' + server.toolCount + ' tools, ' + server.promptCount + ' prompts — permission: ' +
        (permissionNames[server.defaultPermission] || server.defaultPermission) +
        (server.error ? ' — ' + server.error : '');
      el.appendChild(detail);

      const actions = document.createElement('div');
      actions.className = 'server-actions';

      const toggleBtn = document.createElement('button');
      toggleBtn.className = 'secondary';
      toggleBtn.textContent = server.enabled ? 'Disable' : 'Enable';
      toggleBtn.addEventListener('click', () => {
        vscode.postMessage({ type: 'setEnabled', name: server.name, enabled: !server.enabled });
      });
      actions.appendChild(toggleBtn);

      const removeBtn = document.createElement('button');
      removeBtn.className = 'danger';
      removeBtn.textContent = 'Remove';
      removeBtn.addEventListener('click', () => {
        vscode.postMessage({ type: 'remove', name: server.name });
      });
      actions.appendChild(removeBtn);

      el.appendChild(actions);
      serversEl.appendChild(el);
    });
  }

  window.addEventListener('message', (event) => {
    const message = event.data;
    if (message.type === 'servers') {
      errorEl.classList.remove('visible');
      renderServers(message.servers);
    } else if (message.type === 'error') {
      errorEl.textContent = message.text;
      errorEl.classList.add('visible');
    }
  });

  vscode.postMessage({ type: 'refresh' });
})();
</script>
</body>
</html>`;
}
