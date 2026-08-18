import * as vscode from "vscode";
import * as crypto from "crypto";
import { LitosHostProcess } from "./hostProcess";
import { LitosClient, AttachedContent } from "./agentEvents";
import { getWebviewHtml } from "./webviewContent";
import { getMcpPanelHtml } from "./mcpPanelContent";
import { getContextPanelHtml } from "./contextPanelContent";

/**
 * One Litos.VsCodeHost process is shared by every chat panel opened in this extension activation
 * (= this VS Code window, since activation is per-window) — not one process per panel. Multiple
 * "Litos: Open Chat" invocations are independent *sessions* (own sessionId, own transcript) that
 * all talk to the same running host, matching how AgentWorker already supports concurrent turns
 * keyed by (SessionOwner, sessionId) — the backend needed no changes for this, only this file's
 * own panel-management did. This mirrors the shared-server-many-sessions shape real prior art in
 * this space uses (e.g. OpenCode's server/session split, confirmed via its own docs) rather than
 * spawning a redundant ~75MB process per open panel, which an earlier version of this file did.
 */
let sharedHost: { process: LitosHostProcess; client: LitosClient; cwd: string } | undefined;

/** Per-panel session state — sessionId is mutable (unlike the earlier const) since /new and
 * /resume and /branch all change which session a panel is pointed at without closing the panel. */
type PanelState = { panel: vscode.WebviewPanel; sessionId: string; pendingAttachments: AttachedContent[]; contextPanel?: vscode.WebviewPanel };
const openPanels = new Map<vscode.WebviewPanel, PanelState>();

let mcpPanel: vscode.WebviewPanel | undefined;

export function activate(context: vscode.ExtensionContext) {
    context.subscriptions.push(
        vscode.commands.registerCommand("litos.openChat", () => openChatPanel(context)),
    );
}

export function deactivate() {
    sharedHost?.process.stop();
    sharedHost = undefined;
}

/**
 * Pushes a fresh context-usage reading into one panel's own webview — rendered as a small row
 * below its composer (see webviewContent.ts's #contextUsage), not a shared/global indicator.
 * Each panel is its own session, so tracking "the active panel" (view-state, focus) isn't needed:
 * this is called with that panel's own state right after whatever changed its usage (a completed
 * turn, /new, /resume, /branch, /compact, /provider, /model — see call sites below).
 */
async function refreshContextUsage(state: PanelState): Promise<void> {
    if (!sharedHost) return;

    try {
        const usage = await sharedHost.client.getContextUsage(state.sessionId);
        state.panel.webview.postMessage({ type: "contextUsage", usage });
    } catch {
        // Host not ready yet (e.g. still starting) or transient error — leave the row as it was
        // rather than flashing an error state for something the user didn't take action on.
    }
}

async function openChatPanel(context: vscode.ExtensionContext) {
    const panel = vscode.window.createWebviewPanel("litosChat", "Litos", vscode.ViewColumn.Beside, {
        enableScripts: true,
        retainContextWhenHidden: true,
    });
    panel.webview.html = getWebviewHtml(panel.webview, context.extensionUri);

    const state: PanelState = { panel, sessionId: crypto.randomBytes(16).toString("hex"), pendingAttachments: [] };
    openPanels.set(panel, state);

    // cwd is captured once, at whichever moment the shared host first gets spawned (the first
    // panel opened in this window) — vscode.workspace.workspaceFolders[0], the same "first folder
    // in a possibly multi-root workspace" caveat noted throughout. A later panel reuses the
    // already-running host's cwd even if the open workspace changes afterward; picking up a
    // workspace-folder change without restarting the host is out of scope (see
    // ReadMe_VsCodeExtension.md's non-goals).
    const cwd = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();

    try {
        await ensureSharedHost(context, cwd);
    } catch (err: any) {
        vscode.window.showErrorMessage(`Litos: failed to start the agent host — ${err.message}`);
        openPanels.delete(panel);
        return;
    }

    const status = await sharedHost!.client.getConfigStatus();
    panel.webview.postMessage({ type: status.configured ? "showChat" : "showFirstRun" });
    void refreshContextUsage(state);

    panel.webview.onDidReceiveMessage((message) => handlePanelMessage(context, state, message));

    panel.onDidDispose(() => {
        openPanels.delete(panel);
        state.contextPanel?.dispose();
        // The shared host keeps running for any other still-open panel. Only deactivate() (the
        // whole extension shutting down, i.e. this VS Code window closing) tears it down —
        // closing one panel must never affect another panel's still-live session.
    });
}

async function handlePanelMessage(context: vscode.ExtensionContext, state: PanelState, message: any): Promise<void> {
    const { panel } = state;

    if (message.type === "send") {
        try {
            const outcome = await sharedHost!.client.sendTurn(state.sessionId, message.text, state.pendingAttachments);
            state.pendingAttachments = [];
            if (outcome.kind === "steered") {
                panel.webview.postMessage({ type: "system", text: outcome.message });
                return;
            }

            // approvalRequested/approvalResolved arrive interleaved on this same stream
            // (TurnsEndpoints.ToSseData merges PendingApprovalRelay onto the turn's own
            // AgentEvent channel) — postMessage forwards every event uniformly, and
            // webviewContent.ts's handleAgentEvent switch renders each kind appropriately.
            for await (const evt of outcome.events) {
                panel.webview.postMessage({ type: "agentEvent", event: evt });
            }

            // Turn finished — refresh this panel's own context-usage row (mirrors Litos.Gui's
            // RefreshContextUsage being called after every completed turn). A cheap re-fetch
            // rather than reading tokens off the stream's own messageCompleted events, since
            // ContextUsage.Compute needs the whole transcript, not just the latest turn's usage
            // number.
            void refreshContextUsage(state);
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error: ${err.message}` });
        }
        return;
    }

    if (message.type === "resolveApproval") {
        try {
            await sharedHost!.client.resolveApproval(state.sessionId, message.approvalId, message.decision);
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error resolving approval: ${err.message}` });
        }
        return;
    }

    if (message.type === "saveKeys") {
        try {
            await sharedHost!.client.saveKeys(message.entries, message.localBaseUrl);

            // ConfigEndpoints.cs's own remarks: LitosHostBuilder.AddLitosAgent gates each keyed
            // IChatProvider registration once, at DI-container-build time — there's no seam to
            // swap a provider into an already-built IServiceProvider, so the freshly saved key
            // only takes effect in a NEW process. Kill-and-respawn here is the automated
            // equivalent of Litos.Gui's ApiKeysWindow first-run flow telling the user "Litos will
            // then close so you can restart it." Because the host is shared, this respawn affects
            // every open panel, not just this one — respawnSharedHost broadcasts the outcome to
            // all of them.
            await respawnSharedHost(context, sharedHost!.cwd);
        } catch (err: any) {
            panel.webview.postMessage({ type: "saveKeysError", text: err.message });
        }
        return;
    }

    if (message.type === "runCommand") {
        await runSlashCommand(context, state, message.command, message.arg || "");
        // /new, /branch, /compact, /provider (open), /model (open) can all change this panel's
        // context usage; harmless no-op refresh for commands that don't (e.g. /skills).
        void refreshContextUsage(state);
        return;
    }

    if (message.type === "pickerSelected") {
        try {
            await handlePickerSelection(state, message.context, message.itemId);
            // /resume, /provider, /model, /branch selections all land here — same reasoning as
            // runCommand above.
            void refreshContextUsage(state);
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error: ${err.message}` });
        }
        return;
    }

    if (message.type === "showContextBreakdown") {
        openContextPanel(context, state);
        return;
    }

    if (message.type === "openLink") {
        // The one place a share_file link (or any http(s) URL the model writes in prose) is
        // actually opened — see webviewContent.ts's linkify()/delegated click-handler comments
        // for why this is routed through the extension instead of a plain <a target="_blank">.
        // openExternal hands off to the OS the same way every time, including a real Save-As
        // dialog for a Content-Disposition: attachment response like share_file's own.
        vscode.env.openExternal(vscode.Uri.parse(message.url));
        return;
    }

    if (message.type === "pasteAttach") {
        try {
            const content = await sharedHost!.client.attachFromBytes(message.base64Data, message.mimeType, "pasted-image.png");
            state.pendingAttachments.push(content);
            panel.webview.postMessage({ type: "attachmentAdded", fileName: "Pasted image" });
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error attaching pasted image: ${err.message}` });
        }
    }
}

async function runSlashCommand(context: vscode.ExtensionContext, state: PanelState, command: string, arg: string): Promise<void> {
    const { panel } = state;
    const client = sharedHost!.client;

    try {
        switch (command) {
            case "new": {
                state.sessionId = crypto.randomBytes(16).toString("hex");
                state.pendingAttachments = [];
                panel.webview.postMessage({ type: "sessionReset" });
                panel.webview.postMessage({ type: "system", text: "Started a new session." });
                break;
            }

            case "resume": {
                const sessions: any[] = await client.listSessions();
                panel.webview.postMessage({
                    type: "openPicker",
                    context: "resume",
                    items: sessions.map((s) => ({
                        id: s.sessionId,
                        title: s.firstUserMessagePreview || "(empty session)",
                        subtitle: `${s.messageCount} messages — ${new Date(s.lastUpdatedAt).toLocaleString()}`,
                    })),
                });
                break;
            }

            case "provider": {
                const settings = await client.getSettings();
                panel.webview.postMessage({
                    type: "openPicker",
                    context: "provider",
                    items: settings.availableProviders.map((p) => ({ id: p, title: p, subtitle: p === settings.providerName ? "current" : "" })),
                });
                break;
            }

            case "model": {
                const settings = await client.getSettings();
                const models = await client.listModels(settings.providerName);
                panel.webview.postMessage({
                    type: "openPicker",
                    context: "model",
                    items: models.map((m) => ({ id: m.id, title: m.displayName || m.id, subtitle: m.id === settings.model ? "current" : "" })),
                });
                break;
            }

            case "skills": {
                const skills = await client.listSkills(sharedHost!.cwd);
                if (skills.length === 0) {
                    panel.webview.postMessage({ type: "system", text: "No skills discovered in this workspace." });
                    break;
                }
                panel.webview.postMessage({
                    type: "openPicker",
                    context: "skill",
                    items: skills.map((s) => ({ id: s.name, title: s.name, subtitle: s.description })),
                });
                break;
            }

            case "skill": {
                if (!arg.trim()) {
                    await runSlashCommand(context, state, "skills", "");
                    break;
                }
                await loadSkillIntoComposer(state, arg.trim());
                break;
            }

            case "attach": {
                const picked = await vscode.window.showOpenDialog({ canSelectMany: false, openLabel: "Attach" });
                if (!picked || picked.length === 0) break;
                const content = await client.attachFromPath(picked[0].fsPath);
                state.pendingAttachments.push(content);
                panel.webview.postMessage({ type: "attachmentAdded", fileName: content.fileName });
                break;
            }

            case "branch": {
                const points = await client.getBranchPoints(state.sessionId);
                if (points.length === 0) {
                    panel.webview.postMessage({ type: "system", text: "Nothing to branch from yet — send a message first." });
                    break;
                }
                panel.webview.postMessage({
                    type: "openPicker",
                    context: "branch",
                    items: points.map((p) => ({
                        id: String(p.entryIndex),
                        title: p.text.length > 80 ? p.text.slice(0, 80) + "…" : p.text,
                        subtitle: new Date(p.timestamp).toLocaleString(),
                    })),
                });
                break;
            }

            case "compact": {
                const result = await client.compactSession(state.sessionId);
                panel.webview.postMessage({
                    type: "system",
                    text: result.compacted ? "Context compacted." : "Nothing to compact yet.",
                });
                break;
            }

            case "reflect": {
                await runReflect(state);
                break;
            }

            case "mcp": {
                openMcpPanel(context);
                break;
            }

            default:
                panel.webview.postMessage({ type: "system", text: `Unknown command: /${command}` });
        }
    } catch (err: any) {
        panel.webview.postMessage({ type: "system", text: `Error: ${err.message}` });
    }
}

/** Handles a picker selection the webview reports back — separate from runSlashCommand since it
 * fires from a "pickerSelected" message, not a "runCommand" one (see webviewContent.ts). */
async function handlePickerSelection(state: PanelState, pickerContext: string, itemId: string): Promise<void> {
    const { panel } = state;
    const client = sharedHost!.client;

    switch (pickerContext) {
        case "resume": {
            state.sessionId = itemId;
            state.pendingAttachments = [];
            const history = await client.getHistory(itemId);
            panel.webview.postMessage({ type: "sessionReset" });
            panel.webview.postMessage({ type: "historyLoaded", history });
            break;
        }
        case "provider": {
            await client.switchProvider(itemId);
            panel.webview.postMessage({ type: "system", text: `Switched to provider: ${itemId}` });
            break;
        }
        case "model": {
            await client.setModel(itemId);
            panel.webview.postMessage({ type: "system", text: `Switched to model: ${itemId}` });
            break;
        }
        case "skill": {
            await loadSkillIntoComposer(state, itemId);
            break;
        }
        case "branch": {
            const result = await client.branchSession(state.sessionId, Number(itemId));
            state.sessionId = result.newSessionId;
            state.pendingAttachments = [];
            panel.webview.postMessage({ type: "sessionReset" });
            const history = await client.getHistory(result.newSessionId);
            panel.webview.postMessage({ type: "historyLoaded", history });
            panel.webview.postMessage({ type: "system", text: `Branched into new session ${result.newSessionId}.` });
            break;
        }
    }
}

async function loadSkillIntoComposer(state: PanelState, name: string): Promise<void> {
    const client = sharedHost!.client;
    const skill = await client.loadSkill(name, sharedHost!.cwd);
    state.pendingAttachments.push({ kind: "document", fileName: `${skill.name}.md`, documentText: `### Skill: ${skill.name}\n\n${skill.content}` });
    state.panel.webview.postMessage({ type: "attachmentAdded", fileName: `Skill: ${skill.name}` });
}

/**
 * /reflect — VS Code's native diff editor (vscode.diff), not a custom webview diff view. VS Code
 * already has a strictly better tool for showing a diff than any webview reimplementation would;
 * this is the one place this design deliberately steps outside "everything in-webview" (see
 * ReadMe_VsCodeExtension.md's design decisions). Writing AGENTS.md itself happens here in the
 * extension (real filesystem access via vscode.workspace.fs), not on the host side — the host's
 * /reflect endpoint only ever produces the proposed text, never writes to disk.
 */
async function runReflect(state: PanelState): Promise<void> {
    const client = sharedHost!.client;
    const agentsMdPath = vscode.Uri.file(`${sharedHost!.cwd}/AGENTS.md`);

    let existing: string | null = null;
    try {
        existing = Buffer.from(await vscode.workspace.fs.readFile(agentsMdPath)).toString("utf8");
    } catch {
        // No existing AGENTS.md — Reflector.ReflectAsync treats null as "produce a new file."
    }

    const { proposed } = await client.reflect(state.sessionId, existing);

    // Diff against a scratch in-memory copy of the proposed content — untitled: URIs need no file
    // on disk, and diffing against the real path (rather than a temp file) keeps "Save" in the
    // resulting diff/editor view meaningful if the user opens the proposed side directly.
    const proposedUri = vscode.Uri.parse(`untitled:AGENTS.md (proposed)`);
    const proposedDoc = await vscode.workspace.openTextDocument(proposedUri);
    const edit = new vscode.WorkspaceEdit();
    edit.insert(proposedUri, new vscode.Position(0, 0), proposed);
    await vscode.workspace.applyEdit(edit);

    if (existing === null) {
        await vscode.window.showTextDocument(proposedDoc);
        state.panel.webview.postMessage({
            type: "system",
            text: "No AGENTS.md exists yet. Review the proposed content in the opened editor, then save it manually to create AGENTS.md.",
        });
        return;
    }

    await vscode.commands.executeCommand("vscode.diff", agentsMdPath, proposedUri, "AGENTS.md ↔ Proposed");
    state.panel.webview.postMessage({
        type: "system",
        text: "Reviewing proposed AGENTS.md changes in the diff editor. Copy what you want into AGENTS.md and save — nothing is written automatically.",
    });
}

function openMcpPanel(context: vscode.ExtensionContext): void {
    if (mcpPanel) {
        mcpPanel.reveal();
        return;
    }

    mcpPanel = vscode.window.createWebviewPanel("litosMcp", "Litos: MCP Servers", vscode.ViewColumn.Beside, { enableScripts: true });
    mcpPanel.webview.html = getMcpPanelHtml(mcpPanel.webview, context.extensionUri);

    mcpPanel.webview.onDidReceiveMessage(async (message) => {
        const client = sharedHost!.client;
        try {
            switch (message.type) {
                case "refresh": {
                    const servers = await client.listMcpServers();
                    mcpPanel!.webview.postMessage({ type: "servers", servers });
                    break;
                }
                case "add":
                    await client.addMcpServer(message.server);
                    await client.refreshMcpServers();
                    mcpPanel!.webview.postMessage({ type: "servers", servers: await client.listMcpServers() });
                    break;
                case "setEnabled":
                    await client.setMcpServerEnabled(message.name, message.enabled);
                    await client.refreshMcpServers();
                    mcpPanel!.webview.postMessage({ type: "servers", servers: await client.listMcpServers() });
                    break;
                case "remove":
                    await client.removeMcpServer(message.name);
                    mcpPanel!.webview.postMessage({ type: "servers", servers: await client.listMcpServers() });
                    break;
                case "refreshServers":
                    await client.refreshMcpServers();
                    mcpPanel!.webview.postMessage({ type: "servers", servers: await client.listMcpServers() });
                    break;
            }
        } catch (err: any) {
            mcpPanel!.webview.postMessage({ type: "error", text: err.message });
        }
    });

    mcpPanel.onDidDispose(() => {
        mcpPanel = undefined;
    });

    loadInitialMcpServerList();

    async function loadInitialMcpServerList() {
        try {
            const servers = await sharedHost!.client.listMcpServers();
            mcpPanel!.webview.postMessage({ type: "servers", servers });
        } catch (err: any) {
            mcpPanel!.webview.postMessage({ type: "error", text: err.message });
        }
    }
}

/**
 * "View Context" breakdown panel for one chat panel's own session — opened by clicking that
 * panel's own context-usage row below its composer (see webviewContent.ts's #contextUsage). Each
 * chat PanelState owns at most one of these (state.contextPanel), so with several chat panels open
 * each can have its own breakdown panel open side by side, unlike the earlier single-shared-panel
 * design this replaced. Matching Litos.Gui's own "computed once when the modal opens"
 * ViewContextWindow semantics — the breakdown is a snapshot, not a live view; re-showing it (a
 * fresh click) re-fetches rather than tracking the underlying transcript live.
 */
function openContextPanel(context: vscode.ExtensionContext, state: PanelState): void {
    if (!sharedHost) return;
    const sessionId = state.sessionId;

    if (state.contextPanel) {
        state.contextPanel.reveal();
    } else {
        const panel = vscode.window.createWebviewPanel("litosContext", "Litos: Context Usage", vscode.ViewColumn.Beside, { enableScripts: true });
        state.contextPanel = panel;
        panel.webview.html = getContextPanelHtml(panel.webview, context.extensionUri);
        panel.webview.onDidReceiveMessage(async (message) => {
            if (message.type !== "refresh") return;
            try {
                const breakdown = await sharedHost!.client.getContextBreakdown(sessionId);
                panel.webview.postMessage({ type: "breakdown", breakdown });
            } catch (err: any) {
                panel.webview.postMessage({ type: "error", text: `Error: ${err.message}` });
            }
        });
        panel.onDidDispose(() => {
            if (state.contextPanel === panel) state.contextPanel = undefined;
        });
    }

    loadBreakdown();

    async function loadBreakdown() {
        try {
            const breakdown = await sharedHost!.client.getContextBreakdown(sessionId);
            state.contextPanel!.webview.postMessage({ type: "breakdown", breakdown });
        } catch (err: any) {
            state.contextPanel!.webview.postMessage({ type: "error", text: `Error: ${err.message}` });
        }
    }
}

async function ensureSharedHost(
    context: vscode.ExtensionContext, cwd: string,
): Promise<{ process: LitosHostProcess; client: LitosClient; cwd: string }> {
    if (sharedHost) return sharedHost;
    return spawnSharedHost(context, cwd);
}

async function spawnSharedHost(
    context: vscode.ExtensionContext, cwd: string,
): Promise<{ process: LitosHostProcess; client: LitosClient; cwd: string }> {
    const process_ = new LitosHostProcess();
    const { port } = await process_.start(context.extensionPath, cwd);
    sharedHost = { process: process_, client: new LitosClient(`http://127.0.0.1:${port}`), cwd };
    return sharedHost;
}

/** Kills and respawns the shared host, then tells every open panel to re-check status and resume showing chat — not just the panel whose saveKeys triggered this. */
async function respawnSharedHost(context: vscode.ExtensionContext, cwd: string): Promise<void> {
    sharedHost?.process.stop();
    const host = await spawnSharedHost(context, cwd);

    const status = await host.client.getConfigStatus();
    for (const state of openPanels.values()) {
        state.panel.webview.postMessage(
            status.configured
                ? { type: "showChat" }
                : {
                      type: "saveKeysError",
                      text: "Saved, but no chat provider is configured yet. Enter a chat-provider key (Anthropic/OpenAI/Gemini/OpenRouter) or a local server URL.",
                  },
        );
    }
}
