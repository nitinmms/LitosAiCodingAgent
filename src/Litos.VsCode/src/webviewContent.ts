import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";

/**
 * Styled HTML/CSS transcript, not a terminal emulator (see project decision: xterm.js was
 * evaluated and rejected — it only relocates the rendering-engine cost rather than avoiding it).
 * Uses VS Code's own theme CSS variables so it matches light/dark/high-contrast automatically.
 */
export function getWebviewHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    // Vendored (not npm-installed into the packaged extension, not loaded from a CDN — the CSP
    // below has no external script-src) at media/marked.min.js, MIT licensed
    // (https://github.com/markedjs/marked). Inlined as its own <script> block ahead of this
    // file's own script so `marked` is a ready global by the time it runs — the same "everything
    // self-contained, no <script src>" shape every other part of this webview already follows.
    const markedSource = fs.readFileSync(path.join(extensionUri.fsPath, "media", "marked.min.js"), "utf8");

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:;">
<style>
  :root {
    color-scheme: light dark;
  }
  body {
    margin: 0;
    display: flex;
    flex-direction: column;
    height: 100vh;
    font-family: var(--vscode-editor-font-family, monospace);
    font-size: var(--vscode-editor-font-size, 13px);
    color: var(--vscode-foreground);
    background: var(--vscode-editor-background);
  }
  #transcript {
    flex: 1;
    overflow-y: auto;
    padding: 12px;
  }
  .entry {
    margin-bottom: 12px;
    white-space: pre-wrap;
    word-break: break-word;
  }
  .entry .label {
    font-weight: 600;
    opacity: 0.7;
    font-size: 0.85em;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-bottom: 2px;
  }
  .share-link {
    color: var(--vscode-textLink-foreground);
    text-decoration: underline;
    cursor: pointer;
  }
  .share-link:hover { color: var(--vscode-textLink-activeForeground); }
  /* Assistant bodies hold marked's real block HTML (h1-h6/ul/ol/pre/blockquote/table) once
     finalizeAssistantBubble/renderHistory swap in renderMarkdown's output — pre-wrap (inherited
     from .entry, right for every other plain-text entry kind) would double up on the natural
     block spacing those elements already have, so it's turned back off here. */
  .entry.assistant .entry-body { white-space: normal; }
  .entry.assistant .entry-body > *:first-child { margin-top: 0; }
  .entry.assistant .entry-body > *:last-child { margin-bottom: 0; }
  .entry.assistant .entry-body pre {
    background: var(--vscode-textCodeBlock-background);
    border-radius: 4px;
    padding: 8px 10px;
    overflow-x: auto;
  }
  .entry.assistant .entry-body code {
    font-family: var(--vscode-editor-font-family, monospace);
    background: var(--vscode-textCodeBlock-background);
    border-radius: 3px;
    padding: 1px 4px;
  }
  .entry.assistant .entry-body pre code { background: none; padding: 0; }
  .entry.assistant .entry-body blockquote {
    margin: 4px 0;
    padding-left: 10px;
    border-left: 3px solid var(--vscode-widget-border, #444);
    opacity: 0.85;
  }
  .entry.assistant .entry-body table { border-collapse: collapse; }
  .entry.assistant .entry-body th,
  .entry.assistant .entry-body td {
    border: 1px solid var(--vscode-widget-border, #444);
    padding: 4px 8px;
  }
  .entry.assistant .entry-body ul,
  .entry.assistant .entry-body ol { padding-left: 1.5em; }
  .copy-button {
    background: transparent;
    border: none;
    padding: 4px 0 0;
    margin: 4px 0 0;
    color: var(--vscode-descriptionForeground);
    font-size: 0.85em;
    cursor: pointer;
    display: block;
  }
  .copy-button:hover { color: var(--vscode-foreground); }
  .entry.user { border-left: 3px solid var(--vscode-textLink-foreground); padding-left: 8px; }
  .entry-attachments {
    color: var(--vscode-descriptionForeground);
    font-size: 0.85em;
    font-style: italic;
    margin-bottom: 4px;
  }
  .entry.assistant { border-left: 3px solid var(--vscode-charts-green, #3fb950); padding-left: 8px; }
  .entry.system { color: var(--vscode-descriptionForeground); font-style: italic; }
  .entry.tool {
    border: 1px solid var(--vscode-widget-border, #444);
    border-radius: 4px;
    padding: 6px 8px;
    font-size: 0.9em;
    background: var(--vscode-textCodeBlock-background);
  }
  .entry.tool .status-running { color: var(--vscode-charts-yellow, #d29922); }
  .entry.tool .status-succeeded { color: var(--vscode-charts-green, #3fb950); }
  .entry.tool .status-failed { color: var(--vscode-errorForeground); }
  .entry.tool .status-skipped { color: var(--vscode-descriptionForeground); }
  .entry.approval {
    border: 1px solid var(--vscode-charts-yellow, #d29922);
    border-radius: 4px;
    padding: 8px;
    font-size: 0.9em;
    background: var(--vscode-textCodeBlock-background);
  }
  .entry.approval .approval-summary { margin-bottom: 6px; }
  .entry.approval .approval-detail {
    opacity: 0.75;
    margin: 4px 0 8px;
    font-family: var(--vscode-editor-font-family, monospace);
    white-space: pre-wrap;
  }
  .entry.approval .approval-actions { display: flex; gap: 8px; }
  .entry.approval button {
    border: none;
    border-radius: 4px;
    padding: 4px 12px;
    cursor: pointer;
  }
  .entry.approval .approve-btn { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
  .entry.approval .deny-btn { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  .entry.approval button:disabled { opacity: 0.5; cursor: default; }
  .entry.approval .approval-outcome { font-style: italic; opacity: 0.8; }
  #pendingAttachments {
    display: none;
    flex-wrap: wrap;
    gap: 6px;
    padding: 0 8px;
  }
  #pendingAttachments.visible { display: flex; }
  #pendingAttachments .chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    background: var(--vscode-badge-background);
    color: var(--vscode-badge-foreground);
    border-radius: 10px;
    padding: 2px 6px 2px 10px;
    font-size: 0.85em;
  }
  #pendingAttachments .chip:has(.chip-thumb) { padding-left: 2px; }
  #pendingAttachments .chip .chip-thumb {
    width: 20px;
    height: 20px;
    border-radius: 4px;
    object-fit: cover;
    display: block;
    flex: none;
    /* White, not the chip's own badge-blue background, in both themes: an image thumbnail can
       have its own transparent/light corners, and the file-type glyphs below are drawn assuming a
       light backing (matches common OS file-icon conventions) — both read poorly sitting directly
       on the badge color. */
    background: #fff;
  }
  #pendingAttachments .chip .chip-icon {
    display: flex;
    align-items: center;
    justify-content: center;
  }
  #pendingAttachments .chip .chip-remove {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    cursor: pointer;
    opacity: 0.75;
    line-height: 1;
  }
  #pendingAttachments .chip .chip-remove:hover { opacity: 1; background: rgba(0,0,0,0.2); }
  #composer {
    display: flex;
    align-items: flex-end;
    gap: 8px;
    padding: 8px;
    border-top: 1px solid var(--vscode-widget-border, #444);
  }
  #composerInput {
    flex: 1;
    resize: none;
    overflow-y: auto;
    max-height: 22em;
    font-family: inherit;
    font-size: inherit;
    line-height: 1.4;
    background: var(--vscode-input-background);
    color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent);
    border-radius: 4px;
    padding: 6px 8px;
  }
  #slashCommandButton {
    background: var(--vscode-button-secondaryBackground);
    color: var(--vscode-button-secondaryForeground);
    border: none;
    border-radius: 4px;
    padding: 0 12px;
    align-self: stretch;
    cursor: pointer;
    font-family: var(--vscode-editor-font-family, monospace);
    font-weight: 600;
  }
  #sendButton {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
    border: none;
    border-radius: 4px;
    padding: 0 16px;
    align-self: stretch;
    cursor: pointer;
  }
  #sendButton:disabled { opacity: 0.5; cursor: default; }

  /* Context usage row — sits below the composer, mirrors Litos.Gui's status-bar context meter
     (MainWindow.axaml's ContextUsagePanel) but scoped to this one panel's own session instead of
     a window-global indicator (see ReadMe_VsCodeExtension.md's design notes on why a shared
     status-bar item couldn't unambiguously represent "which session" with multiple chat panels
     open at once). */
  #contextUsage {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 2px 10px 6px;
    font-size: 0.8em;
    color: var(--vscode-descriptionForeground);
    cursor: pointer;
  }
  #contextUsage:hover { color: var(--vscode-foreground); }
  #contextUsage.level-Warning { color: var(--vscode-charts-yellow, #d29922); }
  #contextUsage.level-Critical { color: var(--vscode-errorForeground); }
  /* Codicon-shaped inline SVGs (dashboard/graph for context usage, root-folder for working
     directory) so these rows read as native VS Code chrome rather than bespoke iconography — the
     glyph paths mirror @vscode/codicons (MIT) but are hand-inlined, matching this webview's
     no-external-asset CSP (see getWebviewHtml's comment) instead of shipping the codicon font. */
  .statusIcon {
    display: inline-flex;
    width: 14px;
    height: 14px;
    flex: none;
  }
  .statusIcon svg { width: 100%; height: 100%; fill: currentColor; }
  #contextUsageBar {
    width: 60px;
    height: 4px;
    border-radius: 2px;
    background: var(--vscode-widget-border, #444);
    overflow: hidden;
  }
  #contextUsageBar div {
    height: 100%;
    background: currentColor;
  }

  /* Working-directory row — always shown (not conditionally hidden like #contextUsage's .visible
     toggle) so the row itself is never a signal of whether the extension is behaving correctly;
     only its text changes, between a placeholder and a real path. Refreshed on panel open and
     after any command/picker selection that can change the active session (see extension.ts's
     refreshWorkingDirectory) since a resumed/branched session's own WorkingDirectory can differ
     from the shared host's spawn-time cwd. */
  #workingDir {
    display: flex;
    align-items: center;
    gap: 6px;
    /* Bottom padding here is the *only* thing standing between this text and the webview's
       viewport edge — #workingDir is the last child in the flex column and body has none of its
       own (see body's rule above) — so this is deliberately generous, not decorative, to avoid a
       clipped-descender sliver at the bottom edge. */
    padding: 0 10px 10px;
    font-size: 0.8em;
    color: var(--vscode-descriptionForeground);
  }
  #workingDirText {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  #composer { position: relative; }
  #commandMenu {
    display: none;
    position: absolute;
    bottom: 100%;
    left: 8px;
    right: 8px;
    max-height: 240px;
    overflow-y: auto;
    background: var(--vscode-dropdown-background, var(--vscode-editor-background));
    border: 1px solid var(--vscode-widget-border, #444);
    border-radius: 4px;
    margin-bottom: 4px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.3);
  }
  #commandMenu.visible { display: block; }
  #commandMenu .command-item {
    padding: 6px 10px;
    cursor: pointer;
  }
  #commandMenu .command-item .command-name { font-weight: 600; }
  #commandMenu .command-item .command-desc { opacity: 0.7; font-size: 0.85em; margin-left: 8px; }
  #commandMenu .command-item.active { background: var(--vscode-list-activeSelectionBackground); color: var(--vscode-list-activeSelectionForeground); }

  #picker {
    display: none;
    position: fixed;
    top: 10%;
    left: 50%;
    transform: translateX(-50%);
    width: min(560px, 90vw);
    max-height: 70vh;
    overflow-y: auto;
    background: var(--vscode-dropdown-background, var(--vscode-editor-background));
    border: 1px solid var(--vscode-widget-border, #444);
    border-radius: 6px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.4);
    z-index: 100;
  }
  #picker.visible { display: block; }
  #pickerHeader { padding: 10px; border-bottom: 1px solid var(--vscode-widget-border, #444); }
  #pickerHeader input {
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
  #pickerList .picker-item {
    padding: 8px 10px;
    cursor: pointer;
    border-bottom: 1px solid var(--vscode-widget-border, #333);
  }
  #pickerList .picker-item:hover, #pickerList .picker-item.active {
    background: var(--vscode-list-hoverBackground);
  }
  #pickerList .picker-item .picker-title { font-weight: 600; }
  #pickerList .picker-item .picker-subtitle { opacity: 0.7; font-size: 0.85em; }
  #pickerEmpty { padding: 12px; opacity: 0.7; text-align: center; }
  #pickerOverlay {
    display: none;
    position: fixed;
    inset: 0;
    background: rgba(0,0,0,0.3);
    z-index: 99;
  }
  #pickerOverlay.visible { display: block; }

  #firstRun {
    display: none;
    flex: 1;
    overflow-y: auto;
    padding: 24px;
    max-width: 480px;
  }
  #firstRun.visible { display: block; }
  #firstRun h2 { margin-top: 0; }
  #firstRun p { color: var(--vscode-descriptionForeground); }
  #firstRun .field { margin-bottom: 10px; }
  #firstRun label { display: block; font-size: 0.85em; opacity: 0.8; margin-bottom: 2px; }
  #firstRun input[type="password"],
  #firstRun input[type="text"] {
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
  #firstRunError { color: var(--vscode-errorForeground); margin-top: 8px; display: none; }
  #firstRunError.visible { display: block; }
  #firstRunSave {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
    border: none;
    border-radius: 4px;
    padding: 6px 16px;
    cursor: pointer;
    margin-top: 8px;
  }
  #firstRunSave:disabled { opacity: 0.5; cursor: default; }
  #chatArea { display: none; flex-direction: column; flex: 1; min-height: 0; }
  #chatArea.visible { display: flex; }
</style>
</head>
<body>
<div id="firstRun">
  <h2>Set up an API key</h2>
  <p>No API key was found for any provider. Enter at least one below — saved keys are shared with
     Litos.Console and Litos.Gui too. Saving restarts the local Litos agent process.</p>
  <div class="field"><label for="key-anthropic">Anthropic (ANTHROPIC_API_KEY)</label><input type="password" id="key-anthropic"></div>
  <div class="field"><label for="key-openai">OpenAI (OPENAI_API_KEY)</label><input type="password" id="key-openai"></div>
  <div class="field"><label for="key-gemini">Gemini (GEMINI_API_KEY)</label><input type="password" id="key-gemini"></div>
  <div class="field"><label for="key-openrouter">OpenRouter (OPENROUTER_API_KEY)</label><input type="password" id="key-openrouter"></div>
  <div class="field"><label for="key-local-url">Local server URL (optional, e.g. LM Studio/Ollama)</label><input type="text" id="key-local-url" placeholder="http://localhost:1234/v1"></div>
  <div class="field"><label for="key-local">Local server API key (optional)</label><input type="password" id="key-local"></div>
  <div id="firstRunError"></div>
  <button id="firstRunSave">Save and continue</button>
</div>
<div id="chatArea">
<div id="transcript"></div>
<div id="pendingAttachments"></div>
<div id="composer">
  <div id="commandMenu"></div>
  <button id="slashCommandButton" title="Slash commands">/</button>
  <textarea id="composerInput" rows="4" placeholder="Message Litos... (type / for commands, paste an image to attach it)"></textarea>
  <button id="sendButton">Send</button>
</div>
<div id="contextUsage" title="Click for a breakdown of what's using your context">
  <span class="statusIcon" id="contextUsageIcon"></span>
  <div id="contextUsageBar"><div id="contextUsageFill" style="width:0%"></div></div>
  <span id="contextUsageText">Context usage unavailable</span>
</div>
<div id="workingDir"><span class="statusIcon" id="workingDirIcon"></span><span id="workingDirText">Working directory: (loading...)</span></div>
</div>
<div id="pickerOverlay"></div>
<div id="picker">
  <div id="pickerHeader"><input type="text" id="pickerSearch" placeholder="Search..."></div>
  <div id="pickerList"></div>
  <div id="pickerEmpty" style="display:none">No matches.</div>
</div>
<script>${markedSource}</script>
<script>
(function () {
  const vscode = acquireVsCodeApi();
  const firstRunEl = document.getElementById('firstRun');
  const chatAreaEl = document.getElementById('chatArea');
  const firstRunErrorEl = document.getElementById('firstRunError');
  const firstRunSaveButton = document.getElementById('firstRunSave');
  const transcriptEl = document.getElementById('transcript');
  const pendingAttachmentsEl = document.getElementById('pendingAttachments');
  const inputEl = document.getElementById('composerInput');
  const sendButton = document.getElementById('sendButton');
  const slashCommandButton = document.getElementById('slashCommandButton');
  const contextUsageEl = document.getElementById('contextUsage');
  const contextUsageFillEl = document.getElementById('contextUsageFill');
  const contextUsageTextEl = document.getElementById('contextUsageText');
  const contextUsageIconEl = document.getElementById('contextUsageIcon');
  const workingDirEl = document.getElementById('workingDir');
  const workingDirTextEl = document.getElementById('workingDirText');
  const workingDirIconEl = document.getElementById('workingDirIcon');

  // Codicon-shaped glyphs (graph-line, root-folder), hand-inlined as 16x16 currentColor paths —
  // see .statusIcon's CSS comment for why these are inlined rather than the codicon font.
  contextUsageIconEl.innerHTML = '<svg viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M1.5 1v13.5H15v1H1v-1H.5V1h1zm12.15 2.15l.7.7L10 8.21l-2.5-2.5-3.65 3.65-.7-.7L7.5 4.29 10 6.79l3.65-3.64z"/>' +
    '</svg>';
  workingDirIconEl.innerHTML = '<svg viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M14.5 4H8.71l-.85-.85L7.51 3H1.5l-.5.5v9l.5.5h13l.5-.5v-8L14.5 4zM14 13H2V4h5.29l.85.85.36.15H14v8z"/>' +
    '</svg>';

  contextUsageEl.addEventListener('click', () => {
    vscode.postMessage({ type: 'showContextBreakdown' });
  });

  function renderWorkingDir(cwd) {
    const text = cwd ? 'Working directory: ' + cwd : 'Working directory: (not yet started)';
    workingDirTextEl.textContent = text;
    workingDirEl.title = cwd || '';
  }

  // usage is ContextUsage | null (GET /sessions/{id}/context/usage — null before any real usage
  // has been reported yet, e.g. before the session's first turn completes). The row itself always
  // stays visible (see #contextUsage's CSS comment) — a null usage just falls back to placeholder text.
  function renderContextUsage(usage) {
    if (!usage) {
      contextUsageEl.className = '';
      contextUsageFillEl.style.width = '0%';
      contextUsageTextEl.textContent = 'Context usage unavailable';
      return;
    }
    const pct = Math.round(usage.fraction * 100);
    contextUsageEl.className = 'level-' + usage.level;
    contextUsageFillEl.style.width = Math.min(100, pct) + '%';
    contextUsageTextEl.textContent = pct + '% of context (' + usage.usedTokens.toLocaleString() + ' / ' + usage.contextLength.toLocaleString() + ' tokens)';
  }

  // Delegated click handler for every .share-link the linkify() function emits (tool-result rows,
  // assistant prose bubbles, history replay) — see linkify's own comment for why these are spans
  // with a data attribute instead of plain <a> elements. openExternal is the extension-side
  // handler (see extension.ts's handlePanelMessage) and is what actually produces a consistent
  // OS-level Save-As dialog / default-browser handoff, the thing raw anchor navigation inside a
  // VS Code webview could not reliably do.
  transcriptEl.addEventListener('click', (event) => {
    const target = event.target.closest('.share-link');
    if (!target) return;
    vscode.postMessage({ type: 'openLink', url: target.getAttribute('data-share-link') });
  });

  function showFirstRun() {
    firstRunEl.classList.add('visible');
    chatAreaEl.classList.remove('visible');
  }
  function showChat() {
    firstRunEl.classList.remove('visible');
    chatAreaEl.classList.add('visible');
  }

  firstRunSaveButton.addEventListener('click', () => {
    const entries = [];
    [['anthropic', 'key-anthropic'], ['openai', 'key-openai'], ['gemini', 'key-gemini'], ['openrouter', 'key-openrouter'], ['local', 'key-local']]
      .forEach(([provider, id]) => {
        const value = document.getElementById(id).value.trim();
        if (value) entries.push({ provider, apiKey: value });
      });
    const localBaseUrl = document.getElementById('key-local-url').value.trim();

    if (entries.length === 0 && !localBaseUrl) {
      firstRunErrorEl.textContent = 'Enter at least one API key or a local server URL.';
      firstRunErrorEl.classList.add('visible');
      return;
    }

    firstRunErrorEl.classList.remove('visible');
    firstRunSaveButton.disabled = true;
    firstRunSaveButton.textContent = 'Saving and restarting...';
    vscode.postMessage({ type: 'saveKeys', entries, localBaseUrl });
  });

  let currentAssistantBubble = null;
  const toolEntriesByCallId = {};
  const approvalEntriesById = {};

  // --- Generic picker modal (reused by /resume, /provider, /model, /branch, /skills) ---
  const pickerOverlayEl = document.getElementById('pickerOverlay');
  const pickerEl = document.getElementById('picker');
  const pickerSearchEl = document.getElementById('pickerSearch');
  const pickerListEl = document.getElementById('pickerList');
  const pickerEmptyEl = document.getElementById('pickerEmpty');
  let pickerItems = [];
  let pickerOnSelect = null;
  let pickerActiveIndex = -1;

  function openPicker(items, onSelect) {
    pickerItems = items;
    pickerOnSelect = onSelect;
    pickerActiveIndex = items.length > 0 ? 0 : -1;
    pickerSearchEl.value = '';
    pickerOverlayEl.classList.add('visible');
    pickerEl.classList.add('visible');
    renderPickerList('');
    pickerSearchEl.focus();
  }

  function closePicker() {
    pickerOverlayEl.classList.remove('visible');
    pickerEl.classList.remove('visible');
    pickerOnSelect = null;
  }

  function renderPickerList(query) {
    const q = query.toLowerCase();
    const filtered = pickerItems.filter((item) =>
      !q || item.title.toLowerCase().includes(q) || (item.subtitle || '').toLowerCase().includes(q));
    pickerListEl.innerHTML = '';
    pickerEmptyEl.style.display = filtered.length === 0 ? 'block' : 'none';
    filtered.forEach((item, index) => {
      const el = document.createElement('div');
      el.className = 'picker-item' + (index === pickerActiveIndex ? ' active' : '');
      const title = document.createElement('div');
      title.className = 'picker-title';
      title.textContent = item.title;
      el.appendChild(title);
      if (item.subtitle) {
        const subtitle = document.createElement('div');
        subtitle.className = 'picker-subtitle';
        subtitle.textContent = item.subtitle;
        el.appendChild(subtitle);
      }
      el.addEventListener('click', () => {
        const onSelect = pickerOnSelect;
        closePicker();
        if (onSelect) onSelect(item);
      });
      pickerListEl.appendChild(el);
    });
    return filtered;
  }

  pickerSearchEl.addEventListener('input', () => {
    pickerActiveIndex = 0;
    renderPickerList(pickerSearchEl.value);
  });
  pickerSearchEl.addEventListener('keydown', (event) => {
    const filtered = renderPickerList(pickerSearchEl.value);
    if (event.key === 'Escape') {
      event.preventDefault();
      closePicker();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      pickerActiveIndex = Math.min(pickerActiveIndex + 1, filtered.length - 1);
      renderPickerList(pickerSearchEl.value);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      pickerActiveIndex = Math.max(pickerActiveIndex - 1, 0);
      renderPickerList(pickerSearchEl.value);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (pickerActiveIndex >= 0 && filtered[pickerActiveIndex]) {
        const item = filtered[pickerActiveIndex];
        const onSelect = pickerOnSelect;
        closePicker();
        if (onSelect) onSelect(item);
      }
    }
  });
  pickerOverlayEl.addEventListener('click', closePicker);

  // --- Slash-command menu ---
  // In-webview custom UI, not native VS Code showQuickPick — see ReadMe_VsCodeExtension.md's own
  // design-decisions section for why (a persistent, revisitable panel of chat-adjacent state fits
  // better staying part of this same visual surface than a one-shot command-palette popup).
  const SLASH_COMMANDS = [
    { name: 'new', desc: 'Start a new session' },
    { name: 'resume', desc: 'Resume a previous session' },
    { name: 'provider', desc: 'Switch chat provider' },
    { name: 'model', desc: 'Switch model' },
    { name: 'skills', desc: 'List available skills' },
    { name: 'skill', desc: 'Load a skill by name' },
    { name: 'attach', desc: 'Attach a file' },
    { name: 'branch', desc: 'Branch from an earlier message' },
    { name: 'compact', desc: 'Compact the conversation now' },
    { name: 'reflect', desc: 'Distill this session into AGENTS.md' },
    { name: 'mcp', desc: 'Manage MCP servers' },
  ];

  const commandMenuEl = document.getElementById('commandMenu');
  let commandMenuActiveIndex = 0;
  let commandMenuMatches = [];

  function commandMenuQueryFromInput() {
    // Only triggers when "/" starts the entire composer content — matches Litos.Gui's own
    // CommandMenuPopup trigger (a leading "/"), not a "/" appearing mid-message.
    const value = inputEl.value;
    const match = value.match(/^\\/([a-zA-Z]*)$/);
    return match ? match[1] : null;
  }

  function updateCommandMenu() {
    const query = commandMenuQueryFromInput();
    if (query === null) {
      commandMenuEl.classList.remove('visible');
      commandMenuMatches = [];
      return;
    }
    commandMenuMatches = SLASH_COMMANDS.filter((c) => c.name.startsWith(query.toLowerCase()));
    if (commandMenuMatches.length === 0) {
      commandMenuEl.classList.remove('visible');
      return;
    }
    commandMenuActiveIndex = Math.min(commandMenuActiveIndex, commandMenuMatches.length - 1);
    renderCommandMenu();
    commandMenuEl.classList.add('visible');
  }

  function renderCommandMenu() {
    commandMenuEl.innerHTML = '';
    commandMenuMatches.forEach((c, index) => {
      const el = document.createElement('div');
      el.className = 'command-item' + (index === commandMenuActiveIndex ? ' active' : '');
      el.innerHTML = '<span class="command-name">/' + escapeHtml(c.name) + '</span><span class="command-desc">' + escapeHtml(c.desc) + '</span>';
      el.addEventListener('click', () => selectCommand(c.name));
      commandMenuEl.appendChild(el);
    });
  }

  function selectCommand(name) {
    commandMenuEl.classList.remove('visible');
    inputEl.value = '';
    resetComposerHeight();
    runSlashCommand(name, '');
  }

  // Auto-grow the composer as the user types, up to the CSS max-height cap (beyond which it
  // scrolls internally) — 'auto' first so shrinking (e.g. after deleting text) is measured
  // correctly instead of the height only ever ratcheting upward.
  function autoGrowComposer() {
    inputEl.style.height = 'auto';
    inputEl.style.height = inputEl.scrollHeight + 'px';
  }
  function resetComposerHeight() {
    inputEl.style.height = 'auto';
  }

  function runSlashCommand(name, arg) {
    vscode.postMessage({ type: 'runCommand', command: name, arg: arg });
  }

  function scrollToBottom() {
    transcriptEl.scrollTop = transcriptEl.scrollHeight;
  }

  function addEntry(kind, text) {
    const el = document.createElement('div');
    el.className = 'entry ' + kind;
    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = kind === 'user' ? 'You' : kind === 'assistant' ? 'Litos' : kind;
    const body = document.createElement('div');
    body.className = 'entry-body';
    body.textContent = text;
    el.appendChild(label);
    el.appendChild(body);
    transcriptEl.appendChild(el);
    scrollToBottom();
    return { el, body };
  }

  // Sent attachments have no other trace in the transcript once the composer's own chips are
  // cleared on send — mirrors Litos.Gui's own AddUserBubble/BuildBubbleLabel split (MainWindow.axaml.cs)
  // for the identical reason: a 📎-prefixed dim line above the message text is the only remaining
  // record of what was actually attached to this turn, so it's worth showing even though (matching
  // Litos.Gui's own documented limitation) this is live-send-only — history replay after reopening
  // a session has no filenames to recover this from (ReadMe_VsCodeExtension.md §7.12).
  function addUserEntry(text, attachmentNames) {
    const entry = addEntry('user', text);
    if (!attachmentNames || attachmentNames.length === 0) return entry;
    const attachLine = document.createElement('div');
    attachLine.className = 'entry-attachments';
    attachLine.textContent = '📎 ' + attachmentNames.join(', ');
    entry.body.insertBefore(attachLine, entry.body.firstChild);
    return entry;
  }

  function addToolEntry(callId, toolName) {
    const el = document.createElement('div');
    el.className = 'entry tool';
    el.innerHTML = '<span class="status-running">● running</span> ' + escapeHtml(toolName);
    transcriptEl.appendChild(el);
    toolEntriesByCallId[callId] = { el, toolName };
    scrollToBottom();
  }

  function updateToolEntry(callId, status, detail) {
    const entry = toolEntriesByCallId[callId];
    if (!entry) return;
    const icon = status === 'succeeded' ? '✓' : status === 'failed' ? '✗' : '○';
    // linkify (not escapeHtml) for detail specifically — share_file's own tool-result text is a
    // bare http://127.0.0.1:PORT/files/{token} URL (Files/ShareFileTool.cs), and this is the row
    // it renders into.
    entry.el.innerHTML = '<span class="status-' + status + '">' + icon + ' ' + status + '</span> ' +
      escapeHtml(entry.toolName) + (detail ? '<div style="opacity:0.75;margin-top:4px">' + linkify(detail) + '</div>' : '');
    scrollToBottom();
  }

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // Renders links as clickable spans — both share_file's own bare-URL tool-result text
  // (Files/ShareFileTool.cs: "Shared file.txt: http://127.0.0.1:PORT/files/{token} (expires
  // ...)") and Markdown "[label](url)" syntax the model may write in its own prose. Escapes first
  // (same as escapeHtml above), so nothing else in the text is ever interpreted as HTML — only a
  // recognized link pattern becomes a real link element. Generated links are NOT plain <a
  // target="_blank"> — VS Code webviews handle raw anchor-click navigation inconsistently for
  // http(s) URLs (observed: some clicks correctly hand off to the OS default handler and produce
  // a real Save-As download, others instead open a blank pane next to the chat with no download,
  // and right-click "Copy Link" silently does nothing). Instead each link is a
  // data-share-link="URL" <span>; a delegated click listener below posts {type:'openLink', url}
  // to the extension, which calls vscode.env.openExternal — the one API guaranteed to hand off to
  // the OS the same way every time, download dialog included.
  // One combined pattern (markdown form as alternative 1, bare URL as alternative 2) evaluated in
  // a single pass, so a bare-URL match can never re-fire on text a markdown-link match already
  // consumed (the double-replace two-pass version this replaced had exactly that risk: the second
  // pass's URL pattern could match the URL sitting inside the first pass's own emitted
  // data-share-link="..." attribute).
  const LINK_PATTERN = /\\[([^\\]]+)\\]\\((https?:\\/\\/[^\\s)]+)\\)|(https?:\\/\\/[^\\s)]+)/g;
  function linkify(text) {
    return escapeHtml(text).replace(LINK_PATTERN, function (whole, mdLabel, mdUrl, bareUrl) {
      const url = mdUrl || bareUrl;
      const label = mdLabel || bareUrl;
      return '<span class="share-link" data-share-link="' + url + '">' + label + '</span>';
    });
  }

  // marked (vendored, media/marked.min.js — see getWebviewHtml's own comment) renders full
  // assistant prose: headers, bold/italic, lists, code blocks/spans, blockquotes, tables — not
  // just linkify's narrow bare-URL/markdown-link case. renderer.link is overridden so every
  // link marked produces — including a share_file download link the model wrote as ordinary
  // Markdown — becomes the same data-share-link="URL" <span> linkify's own links use, routed
  // through the same delegated #transcript click listener and openExternal round-trip, rather
  // than a real <a> marked would otherwise emit (which is the exact anchor-click-inside-a-webview
  // unreliability linkify was built to work around — see its own comment above).
  const markedRenderer = new marked.Renderer();
  markedRenderer.link = function (href, _title, text) {
    return '<span class="share-link" data-share-link="' + href + '">' + text + '</span>';
  };
  // marked v12 dropped the old sanitize option — by default it passes raw HTML found in the
  // source straight through unescaped. Model output is untrusted text, not authored HTML (same
  // posture as linkify's own escapeHtml-first approach elsewhere in this file), so both hooks
  // that can emit raw HTML are overridden to render it as inert escaped text instead: html (a
  // raw HTML block/inline span in the markdown source, e.g. a stray "<img onerror=...>" the
  // model wrote) and any accidental double-render risk from re-escaping is avoided by using
  // escapeHtml (defined above) rather than re-inventing escaping here.
  markedRenderer.html = function (htmlText) {
    return escapeHtml(htmlText);
  };
  marked.setOptions({
    renderer: markedRenderer,
    headerIds: false,
    mangle: false,
  });

  function renderMarkdown(text) {
    return marked.parse(text, { async: false });
  }

  function truncate(text, maxLength) {
    maxLength = maxLength || 500;
    return text.length <= maxLength ? text : text.slice(0, maxLength) + '…';
  }

  // Rendered as its own entry, distinct from the eventual toolCallStarted/toolCallResult rows —
  // an MCP tool call gated Ask suspends inside McpAwareApprovalGate before ToolCallStarted ever
  // fires, and PendingApprovalRequestedWireEvent's ApprovalId (a fresh Guid minted by
  // PendingApprovalStore.Add) has no relationship to the tool call's own CallId, so there's no
  // existing tool-entry row to merge this into.
  function addApprovalEntry(approvalId, toolName, summary, diffOrCommand) {
    const el = document.createElement('div');
    el.className = 'entry approval';

    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = 'Approval needed';
    el.appendChild(label);

    const summaryEl = document.createElement('div');
    summaryEl.className = 'approval-summary';
    summaryEl.textContent = summary ? (toolName + ' — ' + summary) : toolName;
    el.appendChild(summaryEl);

    if (diffOrCommand) {
      const detailEl = document.createElement('div');
      detailEl.className = 'approval-detail';
      detailEl.textContent = diffOrCommand;
      el.appendChild(detailEl);
    }

    const actions = document.createElement('div');
    actions.className = 'approval-actions';
    const approveBtn = document.createElement('button');
    approveBtn.className = 'approve-btn';
    approveBtn.textContent = 'Approve';
    const denyBtn = document.createElement('button');
    denyBtn.className = 'deny-btn';
    denyBtn.textContent = 'Deny';
    actions.appendChild(approveBtn);
    actions.appendChild(denyBtn);
    el.appendChild(actions);

    function resolve(decision) {
      approveBtn.disabled = true;
      denyBtn.disabled = true;
      vscode.postMessage({ type: 'resolveApproval', approvalId, decision });
    }
    approveBtn.addEventListener('click', () => resolve('approve'));
    denyBtn.addEventListener('click', () => resolve('deny'));

    transcriptEl.appendChild(el);
    approvalEntriesById[approvalId] = { el, actions, resolved: false };
    scrollToBottom();
  }

  // Handles both a resolution this webview itself triggered (Approve/Deny click) and one that
  // arrived independently — PendingApprovalStore's own 10-minute timeout auto-denies and fires
  // Resolved the same as a human decision, and PendingApprovalRelay forwards either one
  // identically as approvalResolved with no way to tell them apart from here.
  function markApprovalResolved(approvalId) {
    const entry = approvalEntriesById[approvalId];
    if (!entry || entry.resolved) return;
    entry.resolved = true;
    entry.actions.remove();
    const outcome = document.createElement('div');
    outcome.className = 'approval-outcome';
    outcome.textContent = 'Resolved.';
    entry.el.appendChild(outcome);
  }

  // Mirrors Litos.Gui's own NewAssistantBubbleContent copy button (MainWindow.axaml.cs): copies
  // the raw markdown SOURCE, not the rendered/selected HTML text, so the same content the model
  // actually produced is what ends up on the clipboard either way. navigator.clipboard is used
  // directly here (unlike share-links' postMessage/openExternal round-trip) because a webview is
  // a plain Chromium context for same-process clipboard writes — no OS-level handoff like
  // opening a URL externally is needed, so there's no equivalent reliability gap to work around.
  function addCopyButton(entryEl, rawText) {
    const button = document.createElement('button');
    button.className = 'copy-button';
    button.textContent = '⧉ Copy';
    let resetTimer = null;
    button.addEventListener('click', () => {
      navigator.clipboard.writeText(rawText).then(() => {
        if (resetTimer) clearTimeout(resetTimer);
        button.textContent = '✓ Copied';
        resetTimer = setTimeout(() => {
          button.textContent = '⧉ Copy';
          resetTimer = null;
        }, 1500);
      });
    });
    entryEl.appendChild(button);
  }

  // One-time full Markdown render, swapped in once an assistant message stops growing — mirrors
  // Litos.Gui's FinalizeAssistantText/MarkdownViewer-swap pattern (see textDelta's own comment
  // above). Safe to call when there is no in-progress bubble (toolCallStarted etc. can fire
  // without a preceding textDelta) since it's a no-op in that case.
  function finalizeAssistantBubble() {
    if (!currentAssistantBubble) return;
    currentAssistantBubble.body.innerHTML = renderMarkdown(currentAssistantBubble.rawText);
    addCopyButton(currentAssistantBubble.el, currentAssistantBubble.rawText);
    currentAssistantBubble = null;
  }

  function handleAgentEvent(evt) {
    switch (evt.type) {
      case 'textDelta':
        if (!currentAssistantBubble) {
          currentAssistantBubble = addEntry('assistant', '');
          currentAssistantBubble.rawText = '';
        }
        // linkify (cheap, bare-URL/markdown-link only) while still streaming — re-parsing full
        // Markdown into HTML on every token would be wasted work for text that's still growing,
        // same reasoning Litos.Gui's own AppendAssistantText comment gives for staying plain text
        // until FinalizeAssistantText's one-time MarkdownViewer swap. finalizeAssistantBubble
        // below is this webview's equivalent of that swap.
        currentAssistantBubble.rawText += evt.text;
        currentAssistantBubble.body.innerHTML = linkify(currentAssistantBubble.rawText);
        scrollToBottom();
        break;
      case 'toolCallStarted':
        finalizeAssistantBubble();
        addToolEntry(evt.callId, evt.toolName);
        break;
      case 'toolCallResult':
        updateToolEntry(evt.callId, evt.success ? 'succeeded' : 'failed', truncate(evt.resultText));
        break;
      case 'toolCallSkipped':
        updateToolEntry(evt.callId, 'skipped', evt.reason);
        break;
      case 'error':
        finalizeAssistantBubble();
        addEntry('system', 'Error: ' + evt.message);
        break;
      case 'compaction':
        addEntry('system', '(conversation history was compacted to stay within context limits)');
        break;
      case 'approvalRequested':
        finalizeAssistantBubble();
        addApprovalEntry(evt.approvalId, evt.toolName, evt.summary, evt.diffOrCommand);
        break;
      case 'approvalResolved':
        markApprovalResolved(evt.approvalId);
        break;
      case 'messageCompleted':
        finalizeAssistantBubble();
        break;
    }
  }

  // Matches a full "/command rest of the message" submission (e.g. "/skill my-skill",
  // "/branch" with no arg) — distinct from updateCommandMenu's own narrower "/partial-name-only"
  // pattern, which only matches while still typing the command name itself, before any space.
  const SLASH_COMMAND_NAMES = SLASH_COMMANDS.map((c) => c.name).join('|');
  const FULL_COMMAND_PATTERN = new RegExp('^/(' + SLASH_COMMAND_NAMES + ')(?:\\s+([\\s\\S]*))?$');

  function send() {
    const text = inputEl.value.trim();
    if (!text) return;

    const commandMatch = text.match(FULL_COMMAND_PATTERN);
    if (commandMatch) {
      inputEl.value = '';
      commandMenuEl.classList.remove('visible');
      runSlashCommand(commandMatch[1], commandMatch[2] || '');
      return;
    }

    addUserEntry(text, pendingAttachmentLabels.slice());
    inputEl.value = '';
    resetComposerHeight();
    currentAssistantBubble = null;
    clearAttachmentChips();
    vscode.postMessage({ type: 'send', text });
  }

  sendButton.addEventListener('click', send);
  slashCommandButton.addEventListener('click', () => {
    inputEl.value = '/';
    inputEl.focus();
    autoGrowComposer();
    updateCommandMenu();
  });
  inputEl.addEventListener('input', () => {
    updateCommandMenu();
    autoGrowComposer();
  });
  inputEl.addEventListener('keydown', (event) => {
    if (commandMenuEl.classList.contains('visible')) {
      if (event.key === 'ArrowDown') {
        event.preventDefault();
        commandMenuActiveIndex = Math.min(commandMenuActiveIndex + 1, commandMenuMatches.length - 1);
        renderCommandMenu();
        return;
      }
      if (event.key === 'ArrowUp') {
        event.preventDefault();
        commandMenuActiveIndex = Math.max(commandMenuActiveIndex - 1, 0);
        renderCommandMenu();
        return;
      }
      if (event.key === 'Tab' || (event.key === 'Enter' && commandMenuMatches.length > 0 && !event.shiftKey)) {
        event.preventDefault();
        selectCommand(commandMenuMatches[commandMenuActiveIndex].name);
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        commandMenuEl.classList.remove('visible');
        return;
      }
    }
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      send();
    }
  });

  // --- Pending attachments (from /attach's file picker, /skill, or clipboard paste) ---
  // Chips are rendered in the same order extension.ts's state.pendingAttachments is pushed onto,
  // so a chip's position among its siblings is a valid index into that array — removeAttachment
  // sends that index back and extension.ts splices the matching entry out.
  // Inline SVG per non-image attachment icon-kind (extension.ts's attachmentIconKind classifies
  // by file extension; anything unrecognized falls back to 'generic' here too, so an unmapped key
  // never renders nothing). Kept as a plain colored document-with-folded-corner glyph, one accent
  // color per kind mirroring common OS file-icon conventions (red=PDF, blue=Word, green=Excel,
  // orange=PowerPoint) — not real per-app branding/logos, just enough to distinguish file kinds at
  // a glance in a 20px chip.
  function fileGlyphSvg(accent, label) {
    return '<svg width="20" height="20" viewBox="0 0 20 20" xmlns="http://www.w3.org/2000/svg">' +
      '<path d="M4 1.5h8l4 4v13a.5.5 0 0 1-.5.5h-11a.5.5 0 0 1-.5-.5v-16a.5.5 0 0 1 .5-.5z" fill="' + accent + '" opacity="0.25"/>' +
      '<path d="M12 1.5v3.5a.5.5 0 0 0 .5.5H16" fill="none" stroke="' + accent + '" stroke-width="1"/>' +
      '<path d="M4 1.5h8l4 4v13a.5.5 0 0 1-.5.5h-11a.5.5 0 0 1-.5-.5v-16a.5.5 0 0 1 .5-.5z" fill="none" stroke="' + accent + '" stroke-width="1"/>' +
      (label ? '<text x="10" y="14.5" font-size="6" font-weight="700" text-anchor="middle" fill="' + accent + '">' + label + '</text>' : '') +
      '</svg>';
  }
  const ATTACHMENT_ICON_SVG = {
    pdf: fileGlyphSvg('#e5484d', 'PDF'),
    word: fileGlyphSvg('#2b6cb0', 'W'),
    excel: fileGlyphSvg('#2f9e44', 'X'),
    powerpoint: fileGlyphSvg('#e8590c', 'P'),
    text: fileGlyphSvg('var(--vscode-descriptionForeground, #888)', ''),
    generic: fileGlyphSvg('var(--vscode-descriptionForeground, #888)', ''),
  };

  // --- Pending attachments (from /attach's file picker, /skill, or clipboard paste) ---
  // Chips are rendered in the same order extension.ts's state.pendingAttachments is pushed onto,
  // so a chip's position among its siblings is a valid index into that array — removeAttachment
  // sends that index back and extension.ts splices the matching entry out. pendingAttachmentLabels
  // is kept as a parallel array (not re-derived from chip DOM text) so send() can hand the labels
  // off to addUserEntry for the 📎 line — see addUserEntry's own comment for why that's the only
  // remaining record of what was attached once these chips are cleared on send.
  const pendingAttachmentChips = [];
  const pendingAttachmentLabels = [];
  function addAttachmentChip(label, thumbnailDataUri, iconKind) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    if (thumbnailDataUri) {
      const thumb = document.createElement('img');
      thumb.className = 'chip-thumb';
      thumb.src = thumbnailDataUri;
      thumb.alt = '';
      chip.appendChild(thumb);
    } else {
      const icon = document.createElement('span');
      icon.className = 'chip-thumb chip-icon';
      icon.innerHTML = ATTACHMENT_ICON_SVG[iconKind] || ATTACHMENT_ICON_SVG.generic;
      chip.appendChild(icon);
    }
    const labelEl = document.createElement('span');
    labelEl.textContent = label;
    chip.appendChild(labelEl);
    const removeEl = document.createElement('span');
    removeEl.className = 'chip-remove';
    removeEl.textContent = '×';
    removeEl.title = 'Remove attachment';
    removeEl.addEventListener('click', () => {
      const index = pendingAttachmentChips.indexOf(chip);
      if (index === -1) return;
      chip.remove();
      pendingAttachmentChips.splice(index, 1);
      pendingAttachmentLabels.splice(index, 1);
      if (pendingAttachmentChips.length === 0) pendingAttachmentsEl.classList.remove('visible');
      vscode.postMessage({ type: 'removeAttachment', index });
    });
    chip.appendChild(removeEl);
    pendingAttachmentsEl.appendChild(chip);
    pendingAttachmentChips.push(chip);
    pendingAttachmentLabels.push(label);
    pendingAttachmentsEl.classList.add('visible');
  }
  function clearAttachmentChips() {
    pendingAttachmentChips.forEach((chip) => chip.remove());
    pendingAttachmentChips.length = 0;
    pendingAttachmentLabels.length = 0;
    pendingAttachmentsEl.classList.remove('visible');
  }

  function resetSessionUi() {
    transcriptEl.innerHTML = '';
    currentAssistantBubble = null;
    Object.keys(toolEntriesByCallId).forEach((k) => delete toolEntriesByCallId[k]);
    Object.keys(approvalEntriesById).forEach((k) => delete approvalEntriesById[k]);
    clearAttachmentChips();
  }

  function renderHistory(history) {
    history.forEach((entry) => {
      if (entry.role === 'user') {
        addEntry('user', entry.text + (entry.attachments > 0 ? ' [' + entry.attachments + ' attachment(s)]' : ''));
      } else if (entry.role === 'assistant' && entry.text && entry.text.trim()) {
        const bubble = addEntry('assistant', '');
        bubble.body.innerHTML = renderMarkdown(entry.text);
        addCopyButton(bubble.el, entry.text);
      }
    });
  }

  // --- Clipboard paste-to-attach ---
  // Standard browser Clipboard API — a webview is a Chromium context, so this needs no native
  // code, unlike Litos.Console's Win32 CF_DIBV5 reader or even Litos.Gui's Avalonia
  // IClipboard.TryGetBitmapAsync() (see ReadMe_VsCodeExtension.md's design decisions for why the
  // browser side is genuinely the easy part here — the real work was the backend attachment
  // endpoint, not this listener).
  inputEl.addEventListener('paste', (event) => {
    const items = event.clipboardData ? event.clipboardData.items : [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item.type.indexOf('image/') !== 0) continue;
      event.preventDefault();
      const blob = item.getAsFile();
      if (!blob) continue;
      const reader = new FileReader();
      reader.onload = () => {
        // reader.result is "data:image/png;base64,AAAA..." — strip the data-URL prefix, the
        // extension's LitosClient.attachFromBytes only wants the raw base64 payload.
        const base64 = String(reader.result).split(',')[1];
        vscode.postMessage({ type: 'pasteAttach', base64Data: base64, mimeType: item.type });
      };
      reader.readAsDataURL(blob);
      break;
    }
  });

  window.addEventListener('message', (event) => {
    const message = event.data;
    if (message.type === 'agentEvent') {
      handleAgentEvent(message.event);
    } else if (message.type === 'system') {
      addEntry('system', message.text);
    } else if (message.type === 'showFirstRun') {
      showFirstRun();
    } else if (message.type === 'showChat') {
      showChat();
    } else if (message.type === 'saveKeysError') {
      firstRunSaveButton.disabled = false;
      firstRunSaveButton.textContent = 'Save and continue';
      firstRunErrorEl.textContent = message.text;
      firstRunErrorEl.classList.add('visible');
    } else if (message.type === 'sessionReset') {
      resetSessionUi();
      renderContextUsage(null);
      renderWorkingDir(null);
    } else if (message.type === 'historyLoaded') {
      renderHistory(message.history);
    } else if (message.type === 'attachmentAdded') {
      addAttachmentChip(message.fileName, message.thumbnailDataUri, message.iconKind);
    } else if (message.type === 'contextUsage') {
      renderContextUsage(message.usage);
    } else if (message.type === 'workingDir') {
      renderWorkingDir(message.cwd);
    } else if (message.type === 'openPicker') {
      openPicker(message.items.map((item) => ({ title: item.title, subtitle: item.subtitle, id: item.id })), (picked) => {
        vscode.postMessage({ type: 'pickerSelected', context: message.context, itemId: picked.id });
      });
    }
  });
})();
</script>
</body>
</html>`;
}
