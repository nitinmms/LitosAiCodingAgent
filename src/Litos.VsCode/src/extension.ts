import * as vscode from "vscode";
import * as crypto from "crypto";
import * as path from "path";
import * as fs from "fs";
import { LitosHostProcess } from "./hostProcess";
import { LitosClient, AttachedContent } from "./agentEvents";
import { getWebviewHtml } from "./webviewContent";
import { getMcpPanelHtml } from "./mcpPanelContent";
import { getContextPanelHtml } from "./contextPanelContent";
import { extractMentionPaths, expandMentionCandidates } from "./mentionParser";

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
let sharedHost: { process: LitosHostProcess; client: LitosClient; cwd: string; baseUrl: string } | undefined;

/**
 * Set once the user dismisses the "no default model set yet" onboarding hint (openDefaultModelHint,
 * see initializeChatSurface) — suppresses it for every panel/surface opened for the rest of this
 * extension activation (this VS Code window), the same scope sharedHost itself lives for. Reset
 * only by a window reload or extension restart, which re-runs this module from scratch.
 */
let defaultModelHintDismissed = false;

/**
 * The minimal shape every chat message-handler function actually needs — satisfied structurally by
 * both vscode.WebviewPanel (an editor-tab chat, see openChatPanel) and vscode.WebviewView (the
 * activity-bar sidebar chat, see LitosChatViewProvider). Letting PanelState hold this instead of a
 * concrete WebviewPanel means handlePanelMessage/runSlashCommand/refreshContextUsage/etc. work
 * unchanged for either surface — the sidebar is not a separate, duplicated code path.
 */
interface ChatSurface {
    webview: vscode.Webview;
}

/** Per-panel session state — sessionId is mutable (unlike the earlier const) since /new and
 * /resume and /branch all change which session a panel is pointed at without closing the panel. */
type PanelState = { panel: ChatSurface; sessionId: string; pendingAttachments: AttachedContent[]; contextPanel?: vscode.WebviewPanel };
const openPanels = new Map<ChatSurface, PanelState>();

let mcpPanel: vscode.WebviewPanel | undefined;

export function activate(context: vscode.ExtensionContext) {
    context.subscriptions.push(
        vscode.commands.registerCommand("litos.openChat", () => openChatPanel(context)),
        vscode.window.registerWebviewViewProvider("litos.chatView", new LitosChatViewProvider(context)),
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
/**
 * Builds a data: URI thumbnail for an image attachment, or undefined for a non-image (document)
 * attachment — AttachedContent.base64Data/mimeType are already right here (no extra fetch), so the
 * webview just needs the raw data: URI to render straight into an <img src>. Kept as an explicit
 * whitelist on `kind === "image"` rather than checking mimeType's presence alone, since a document
 * attachment (from /attach on a non-image file) has no base64Data at all.
 */
function attachmentThumbnail(content: AttachedContent): string | undefined {
    if (content.kind !== "image" || !content.base64Data || !content.mimeType) return undefined;
    return `data:${content.mimeType};base64,${content.base64Data}`;
}

// File-extension -> icon-kind map for non-image attachments (document-kind AttachedContent, e.g.
// /attach on a PDF/Office file/plain text). webviewContent.ts owns the actual SVG glyph per kind
// (inline, no external assets — matches the rest of this webview's self-contained convention) —
// this only classifies, keeping icon rendering out of the extension host entirely. Anything not
// listed here falls back to "generic" in the webview.
const ATTACHMENT_ICON_KIND_BY_EXTENSION: Record<string, string> = {
    ".pdf": "pdf",
    ".doc": "word", ".docx": "word",
    ".xls": "excel", ".xlsx": "excel", ".csv": "excel",
    ".ppt": "powerpoint", ".pptx": "powerpoint",
    ".txt": "text", ".md": "text", ".markdown": "text", ".log": "text",
};

function attachmentIconKind(fileName: string): string {
    const ext = fileName.slice(fileName.lastIndexOf(".")).toLowerCase();
    return ATTACHMENT_ICON_KIND_BY_EXTENSION[ext] || "generic";
}

// share_file (ShareFileTool.cs) always emits "http://127.0.0.1:{port}/files/{token}", where token
// is RandomNumberGenerator.GetHexString(16, lowercase: true) — a stable file on disk under
// ~/.litos/shared-files, valid 24h regardless of which process minted it — but the port is only
// valid for the Litos.VsCodeHost.exe process that was running at share-time. Closing/reloading the
// VS Code window kills that process (deactivate()) and the next panel-open spawns a new one on a
// new OS-assigned port (Program.cs binds port 0), so a link persisted into an old session's
// transcript embeds a port nothing is listening on anymore even though the token/file are still
// good. Since the token itself carries all the information needed to resolve the file, any link
// matching this shape is rewritten to whichever host:port the *currently running* shared host
// actually reports, rather than trusting whatever host:port happened to be embedded when the link
// was generated.
const SHARE_LINK_PATH_PATTERN = /\/files\/[0-9a-f]{16,}$/;

function rewriteStaleShareLink(url: string): string {
    if (!sharedHost) return url;
    try {
        const parsed = new URL(url);
        if (!SHARE_LINK_PATH_PATTERN.test(parsed.pathname)) return url;
        return `${sharedHost.baseUrl}${parsed.pathname}`;
    } catch {
        return url;
    }
}

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

/** Refreshes the working-directory row for state.sessionId specifically — not sharedHost.cwd,
 * which only reflects wherever the shared host happened to be spawned. A resumed or branched
 * session carries its own WorkingDirectory (see Transcript.cs), which can differ from the host's
 * spawn-time cwd if the VS Code workspace folder changed since. Falls back to sharedHost.cwd for a
 * session that hasn't run a turn yet (WorkingDirectory is null until AgentWorker sets it). */
async function refreshWorkingDirectory(state: PanelState): Promise<void> {
    if (!sharedHost) return;

    try {
        const { workingDirectory } = await sharedHost.client.getWorkingDirectory(state.sessionId);
        state.panel.webview.postMessage({ type: "workingDir", cwd: workingDirectory ?? sharedHost.cwd });
    } catch {
        // Host not ready yet (e.g. still starting) or transient error — leave the row as it was.
    }
}

/**
 * Resolves every "@path" mention in the just-submitted text (the mention dropdown only ever
 * inserts text into the composer — see webviewContent.ts's acceptMention — nothing is attached
 * until send time) into AttachedContent, appended to the same array sendTurn's own attachments
 * argument already carries — mirrors Litos.Gui's MainWindow.ResolveMentionsAsync. Unlike
 * /attach's picker or clipboard paste, a mention leaves its text in the visible message (it reads
 * naturally as part of the sentence), so only the attachment side effect is added here.
 *
 * Directories aren't supported here (v1 — files only, matching the actual ask this shipped for);
 * a directory-shaped candidate found on disk logs a system message and is skipped rather than
 * silently doing nothing.
 */
async function resolveMentionsAsync(state: PanelState, text: string): Promise<AttachedContent[]> {
    if (!sharedHost) return [];

    const { workingDirectory } = await sharedHost.client.getWorkingDirectory(state.sessionId);
    const baseDir = workingDirectory ?? sharedHost.cwd;
    const resolved: AttachedContent[] = [];

    for (const rawMention of extractMentionPaths(text)) {
        // The raw capture can include trailing sentence words ("@Plant Resource.png please
        // describe it") since spaces are allowed in filenames — try the longest candidate first
        // and shrink until something on disk actually matches.
        const candidates = expandMentionCandidates(rawMention);
        const found = candidates
            .map((candidate) => (path.isAbsolute(candidate) ? candidate : path.join(baseDir, candidate)))
            .find((absolute) => fs.existsSync(absolute));

        if (!found) {
            state.panel.webview.postMessage({ type: "system", text: `@${rawMention}: no such file, ignoring mention.` });
            continue;
        }

        if (fs.statSync(found).isDirectory()) {
            state.panel.webview.postMessage({ type: "system", text: `@${rawMention}: mentioning a directory isn't supported yet — mention a file instead.` });
            continue;
        }

        try {
            resolved.push(await sharedHost.client.attachFromPath(found));
        } catch (err: any) {
            state.panel.webview.postMessage({ type: "system", text: `Could not read @${rawMention}: ${err.message}` });
        }
    }

    return resolved;
}

/** Refreshes the model/provider row. Unlike contextUsage/workingDir this isn't keyed off
 * state.sessionId at all — AgentWorker.ProviderName/Model are process-wide (switching provider
 * affects the *next* turn on any session, see AgentSettingsEndpoints.cs's remarks), so every open
 * panel shows the same value and there is nothing session-specific to pass here. */
async function refreshModelSettings(state: PanelState): Promise<void> {
    if (!sharedHost) return;

    try {
        const { providerName, model } = await sharedHost.client.getSettings();
        state.panel.webview.postMessage({ type: "modelSettings", providerName, model });
    } catch {
        // Host not ready yet (e.g. still starting) or transient error — leave the row as it was.
    }
}

async function openChatPanel(context: vscode.ExtensionContext) {
    const panel = vscode.window.createWebviewPanel("litosChat", "Litos", vscode.ViewColumn.Beside, {
        enableScripts: true,
        retainContextWhenHidden: true,
    });
    // Full-color icon.png, not the monochrome activity-icon.svg — editor-tab icons aren't
    // theme-masked the way activity-bar/view-title icons are, so the real navy/cyan Litos mark
    // can render here unlike those two (see the extension's own design notes on this split).
    panel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.png");
    panel.webview.html = getWebviewHtml(panel.webview, context.extensionUri);

    const state = await initializeChatSurface(context, panel);
    if (!state) return;

    panel.onDidDispose(() => {
        openPanels.delete(panel);
        state.contextPanel?.dispose();
        // The shared host keeps running for any other still-open panel. Only deactivate() (the
        // whole extension shutting down, i.e. this VS Code window closing) tears it down —
        // closing one panel must never affect another panel's still-live session.
    });
}

/**
 * Wires up a fresh chat session onto any ChatSurface — an editor-tab WebviewPanel (openChatPanel)
 * or the activity-bar sidebar's WebviewView (LitosChatViewProvider). Both need the identical
 * "start/reuse the shared host, show first-run or chat, start listening for messages" sequence;
 * only how each surface is created and disposed differs, which stays with the caller.
 */
async function initializeChatSurface(context: vscode.ExtensionContext, surface: ChatSurface): Promise<PanelState | undefined> {
    const state: PanelState = { panel: surface, sessionId: crypto.randomBytes(16).toString("hex"), pendingAttachments: [] };
    openPanels.set(surface, state);

    // cwd is captured once, at whichever moment the shared host first gets spawned (the first
    // chat surface opened in this window) — vscode.workspace.workspaceFolders[0], the same "first
    // folder in a possibly multi-root workspace" caveat noted throughout. A later surface reuses
    // the already-running host's cwd even if the open workspace changes afterward; picking up a
    // workspace-folder change without restarting the host is out of scope (see
    // ReadMe_VsCodeExtension.md's non-goals).
    const cwd = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();

    try {
        await ensureSharedHost(context, cwd);
    } catch (err: any) {
        vscode.window.showErrorMessage(`Litos: failed to start the agent host — ${err.message}`);
        openPanels.delete(surface);
        return undefined;
    }

    const status = await sharedHost!.client.getConfigStatus();
    if (!status.configured) {
        surface.webview.postMessage({ type: "openKeysPopup", isFirstRun: true, keyStatus: status.keyStatus });
    } else if (!status.defaultModelSet && !defaultModelHintDismissed) {
        surface.webview.postMessage({ type: "openDefaultModelHint" });
    }
    void refreshContextUsage(state);
    void refreshWorkingDirectory(state);
    void refreshModelSettings(state);

    surface.webview.onDidReceiveMessage((message) => handlePanelMessage(context, state, message));

    return state;
}

/**
 * Activity-bar sidebar chat — the primary Litos surface (matches Claude Code/OpenCode's docked
 * chat view). A WebviewView is created once by VS Code and reused across show/hide (unlike
 * WebviewPanel, there's no separate open command); resolveWebviewView fires again only if the view
 * is actually disposed (e.g. the extension host restarts), so state persists across sidebar
 * hide/show for free via retainContextWhenHidden below. Opening additional chats as editor panels
 * (litos.openChat) stays a fully separate ChatSurface/PanelState — the sidebar view and any number
 * of editor-tab panels are independent sessions side by side, per the litos.openChat title-bar
 * button on this view (see package.json's view/title menu contribution).
 */
class LitosChatViewProvider implements vscode.WebviewViewProvider {
    constructor(private readonly context: vscode.ExtensionContext) {}

    async resolveWebviewView(webviewView: vscode.WebviewView): Promise<void> {
        webviewView.webview.options = { enableScripts: true };
        webviewView.webview.html = getWebviewHtml(webviewView.webview, this.context.extensionUri);

        const state = await initializeChatSurface(this.context, webviewView);
        if (!state) return;

        webviewView.onDidDispose(() => {
            openPanels.delete(webviewView);
            state.contextPanel?.dispose();
        });
    }
}

async function handlePanelMessage(context: vscode.ExtensionContext, state: PanelState, message: any): Promise<void> {
    const { panel } = state;

    if (message.type === "send") {
        // Cleared up front, not after a successful sendTurn: the webview's own composer already
        // clears its attachment chips optimistically on send (see webviewContent.ts's send()), and
        // if sendTurn throws (e.g. the host 500s on a bad attachment), leaving the old code's
        // post-await clear unreached meant the same attachment silently re-sent itself on every
        // later turn on this session, forever, growing by one duplicate each time it failed again.
        const attachments = state.pendingAttachments;
        state.pendingAttachments = [];
        try {
            // Resolved fresh from this turn's own text every send — mentions aren't staged state
            // like the picker/paste attachments above, they're derived from whatever "@..." the
            // user actually typed this time (see resolveMentionsAsync's own remarks).
            attachments.push(...(await resolveMentionsAsync(state, message.text)));
            const outcome = await sharedHost!.client.sendTurn(state.sessionId, message.text, attachments);
            if (outcome.kind === "steered") {
                panel.webview.postMessage({ type: "system", text: outcome.message });
                return;
            }

            // outcome.kind === "started" here (the "steered" branch above already returned) —
            // this is the earliest point the extension can confirm a turn actually began, as
            // opposed to a message merely being appended to one already in flight.
            panel.webview.postMessage({ type: "turnStarted" });

            // approvalRequested/approvalResolved arrive interleaved on this same stream
            // (TurnsEndpoints.ToSseData merges PendingApprovalRelay onto the turn's own
            // AgentEvent channel) — postMessage forwards every event uniformly, and
            // webviewContent.ts's handleAgentEvent switch renders each kind appropriately.
            for await (const evt of outcome.events) {
                panel.webview.postMessage({ type: "agentEvent", event: evt });
            }
            panel.webview.postMessage({ type: "turnEnded" });

            // Turn finished — refresh this panel's own context-usage row (mirrors Litos.Gui's
            // RefreshContextUsage being called after every completed turn). A cheap re-fetch
            // rather than reading tokens off the stream's own messageCompleted events, since
            // ContextUsage.Compute needs the whole transcript, not just the latest turn's usage
            // number.
            void refreshContextUsage(state);
        } catch (err: any) {
            panel.webview.postMessage({ type: "turnEnded" });
            panel.webview.postMessage({ type: "system", text: `Error: ${err.message}` });
        }
        return;
    }

    if (message.type === "getFileMentions") {
        // Fire-and-forget from the webview's perspective (see updateMentionMenu's own comment) —
        // requestToken round-trips unchanged so the webview can discard a response that's no
        // longer the latest keystroke's request by the time it arrives.
        try {
            // Same fallback resolveMentionsAsync uses at send time — required here too, not just
            // there: a brand-new panel's session has no WorkingDirectory on its transcript until
            // its first turn completes, so without this every "@"-mention in a first message
            // always came back "no matching files" even though the file genuinely exists.
            const { workingDirectory } = await sharedHost!.client.getWorkingDirectory(state.sessionId);
            const matches = await sharedHost!.client.getFileMentions(state.sessionId, message.query ?? "", workingDirectory ?? sharedHost!.cwd);
            panel.webview.postMessage({ type: "fileMentionsResult", matches, requestToken: message.requestToken });
        } catch {
            panel.webview.postMessage({ type: "fileMentionsResult", matches: [], requestToken: message.requestToken });
        }
        return;
    }

    if (message.type === "cancel") {
        // Doesn't post turnEnded itself — CancelTurn ends the turn server-side, which unwinds the
        // "send" handler's own `for await` loop above (the channel completes rather than throwing),
        // so that handler's own turnEnded post already covers this. Fire-and-forget from the
        // webview's perspective: the Cancel button doesn't block on a response.
        try {
            await sharedHost!.client.cancelTurn(state.sessionId);
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error cancelling: ${err.message}` });
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
            // ConfigEndpoints.cs's own remarks: LitosHostBuilder.AddLitosAgent gates each keyed
            // IChatProvider registration once, at DI-container-build time — there's no seam to
            // swap a provider into an already-built IServiceProvider, so the freshly saved key
            // only takes effect in a NEW process. This used to kill-and-respawn sharedHost right
            // here and wait on the new process's own /config/status before telling the webview it
            // could close — but that respawn (a child process spawn racing a not-yet-dead old
            // process, then a status fetch with no visibility into the new process's stderr) had
            // no reliable timeout on some platforms and left the dialog's "Saving and
            // restarting..." button stuck forever when it stalled (seen on macOS even after adding
            // a fetch timeout to the status fetch itself — something upstream of that was also
            // hanging). Saving now only writes the key and reports the same "configured" check
            // saveKeys's own response already carries — no respawn or extra round-trip needed for
            // that. The webview then asks the user to reload the window (see the "reloadWindow"
            // handler below) to actually pick up the new key, which VS Code itself performs
            // reliably — tearing down and restarting the whole extension host, not just one child
            // process this extension spawned itself.
            const status = await sharedHost!.client.saveKeys(message.entries, message.localBaseUrl);
            panel.webview.postMessage(
                status.configured
                    ? { type: "saveKeysSuccess" }
                    : {
                          type: "saveKeysError",
                          text: "Saved, but no chat provider is configured yet. Enter a chat-provider key (Anthropic/OpenAI/Gemini/OpenRouter/MeshAPI) or a local server URL.",
                      },
            );
        } catch (err: any) {
            panel.webview.postMessage({ type: "saveKeysError", text: err.message });
        }
        return;
    }

    if (message.type === "reloadWindow") {
        await vscode.commands.executeCommand("workbench.action.reloadWindow");
        return;
    }

    if (message.type === "dismissDefaultModelHint") {
        defaultModelHintDismissed = true;
        return;
    }

    if (message.type === "runCommand") {
        await runSlashCommand(context, state, message.command, message.arg || "");
        // /new, /branch, /compact, /provider (open), /model (open) can all change this panel's
        // context usage; harmless no-op refresh for commands that don't (e.g. /skills). /new also
        // resets state.sessionId to a not-yet-run session, so the working-directory row falls back
        // to sharedHost.cwd until that session's first turn sets its own WorkingDirectory.
        void refreshContextUsage(state);
        void refreshWorkingDirectory(state);
        void refreshModelSettings(state);
        return;
    }

    if (message.type === "pickerSelected") {
        try {
            await handlePickerSelection(state, message.context, message.itemId);
            // /resume, /provider, /model, /branch selections all land here — same reasoning as
            // runCommand above. /resume and /branch are exactly the cases where the session's own
            // WorkingDirectory can differ from sharedHost.cwd, which is the whole point of refreshing it here.
            // /provider and /model selections are exactly the case refreshModelSettings exists for.
            void refreshContextUsage(state);
            void refreshWorkingDirectory(state);
            void refreshModelSettings(state);
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
        vscode.env.openExternal(vscode.Uri.parse(rewriteStaleShareLink(message.url)));
        return;
    }

    if (message.type === "pasteAttach") {
        // Some clipboard sources (observed with certain OS screenshot tools) report a
        // DataTransferItem with an image/* type prefix but no further subtype, or Chromium hands
        // back an empty string — webviewContent.ts's paste handler forwards whatever `item.type`
        // it got verbatim. An empty MimeType survives JSON round-tripping through
        // /attachments/from-bytes with no server-side validation and produces an ImageBlock no
        // provider can actually process, which 500s every future turn on this session until the
        // attachment is somehow cleared (see the "send" handler's own comment on the compounding
        // half of this bug). Default to image/png here — the one paste-to-attach already always
        // requests as its filename extension — rather than trusting an unreliable clipboard value.
        const mimeType = message.mimeType && message.mimeType.startsWith("image/") ? message.mimeType : "image/png";
        try {
            const content = await sharedHost!.client.attachFromBytes(message.base64Data, mimeType, "pasted-image.png");
            state.pendingAttachments.push(content);
            panel.webview.postMessage({ type: "attachmentAdded", fileName: "Pasted image", thumbnailDataUri: attachmentThumbnail(content), iconKind: attachmentIconKind("pasted-image.png") });
        } catch (err: any) {
            panel.webview.postMessage({ type: "system", text: `Error attaching pasted image: ${err.message}` });
        }
        return;
    }

    if (message.type === "removeAttachment") {
        // index is this chip's position among the webview's currently rendered chips, which is
        // always kept in the same order as pendingAttachments is pushed onto (send/pasteAttach/
        // the /attach picker/`/skill` all only ever push, never reorder) — so it's a valid splice
        // index here too.
        if (typeof message.index === "number" && message.index >= 0 && message.index < state.pendingAttachments.length) {
            state.pendingAttachments.splice(message.index, 1);
        }
        return;
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
                panel.webview.postMessage({ type: "attachmentAdded", fileName: content.fileName, thumbnailDataUri: attachmentThumbnail(content), iconKind: attachmentIconKind(content.fileName) });
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

            case "keys": {
                const status = await client.getConfigStatus();
                panel.webview.postMessage({ type: "openKeysPopup", isFirstRun: false, keyStatus: status.keyStatus });
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
    state.panel.webview.postMessage({ type: "attachmentAdded", fileName: `Skill: ${skill.name}`, iconKind: "text" });
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
    mcpPanel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.png");
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
                case "setDefaultPermission":
                    // No refreshMcpServers() here — McpAwareApprovalGate reads McpConfigStore fresh
                    // on every tool call (see McpToolProvider.DefinitionsMatch's own remarks), so a
                    // permission change applies live without needing a reconnect.
                    await client.setMcpServerDefaultPermission(message.name, message.defaultPermission);
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
        panel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.png");
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
): Promise<{ process: LitosHostProcess; client: LitosClient; cwd: string; baseUrl: string }> {
    if (sharedHost) return sharedHost;
    return spawnSharedHost(context, cwd);
}

async function spawnSharedHost(
    context: vscode.ExtensionContext, cwd: string,
): Promise<{ process: LitosHostProcess; client: LitosClient; cwd: string; baseUrl: string }> {
    const process_ = new LitosHostProcess();
    const { port } = await process_.start(context.extensionPath, cwd);
    const baseUrl = `http://127.0.0.1:${port}`;
    sharedHost = { process: process_, client: new LitosClient(baseUrl), cwd, baseUrl };
    return sharedHost;
}

