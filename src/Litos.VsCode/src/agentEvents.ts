/**
 * TypeScript port of src/Litos.Api/examples/AngularChat/app.js's readAgentEvents/parseAgentEvent.
 * The wire format (see src/Litos.Agent/Streaming/AgentEvent.cs) carries no type discriminator —
 * each AgentEvent record serializes by its own PascalCase properties only — so events are
 * classified here by which properties are present, in the same order AngularChat's parser uses
 * (most-distinguishing property first). TextDelta and ReasoningDelta are genuinely indistinguishable
 * on the wire ({"Text":"..."} for both) and are both surfaced as "textDelta" here, same as the
 * example client.
 *
 * Two extra shapes on this stream have no AngularChat precedent, since Litos.Api doesn't have
 * them: PendingApprovalRequestedWireEvent/PendingApprovalResolvedWireEvent
 * (src/Litos.VsCodeHost/Approvals/PendingApprovalWireEvents.cs), merged onto the same SSE stream
 * by TurnsEndpoints.ToSseData for an MCP tool call gated Ask by McpAwareApprovalGate. Distinguished
 * by "ApprovalId", a property no real AgentEvent variant has.
 */

export type AgentEventParsed =
    | { type: "textDelta"; text: string }
    | { type: "toolCallStarted"; callId: string; toolName: string }
    | { type: "toolCallCompleted"; callId: string; toolName: string }
    | { type: "toolCallResult"; callId: string; toolName: string; success: boolean; resultText: string }
    | { type: "toolCallSkipped"; callId: string; reason: string }
    | { type: "messageCompleted"; usage: { inputTokens: number; outputTokens: number } }
    | { type: "error"; message: string }
    | { type: "compaction" }
    | { type: "approvalRequested"; approvalId: string; toolName: string; summary: string; diffOrCommand: string | null }
    | { type: "approvalResolved"; approvalId: string }
    | { type: "unknown"; raw: string };

export function parseAgentEvent(json: string): AgentEventParsed {
    let obj: any;
    try {
        obj = JSON.parse(json);
    } catch {
        return { type: "unknown", raw: json };
    }

    if (typeof obj.Text === "string") {
        return { type: "textDelta", text: obj.Text };
    }
    if (obj.Result !== undefined) {
        const result = obj.Result || {};
        return {
            type: "toolCallResult",
            callId: obj.CallId || "",
            toolName: obj.ToolName || "",
            success: !result.IsError,
            resultText: typeof result.Text === "string" ? result.Text : JSON.stringify(result),
        };
    }
    if (obj.Arguments !== undefined) {
        return { type: "toolCallCompleted", callId: obj.CallId || "", toolName: obj.ToolName || "" };
    }
    // ApprovalId is checked ahead of the Reason/CallId and ToolName/CallId cases below —
    // PendingApprovalRequestedWireEvent also carries a ToolName, and would otherwise be
    // misclassified as an ordinary toolCallStarted.
    if (obj.ApprovalId !== undefined && obj.ToolName !== undefined) {
        return {
            type: "approvalRequested",
            approvalId: obj.ApprovalId,
            toolName: obj.ToolName,
            summary: obj.Summary || "",
            diffOrCommand: obj.DiffOrCommand ?? null,
        };
    }
    if (obj.ApprovalId !== undefined) {
        return { type: "approvalResolved", approvalId: obj.ApprovalId };
    }
    if (obj.Reason !== undefined && obj.CallId !== undefined) {
        return { type: "toolCallSkipped", callId: obj.CallId, reason: obj.Reason };
    }
    if (obj.ToolName !== undefined && obj.CallId !== undefined) {
        return { type: "toolCallStarted", callId: obj.CallId, toolName: obj.ToolName };
    }
    if (obj.Message !== undefined && obj.Usage !== undefined) {
        return { type: "messageCompleted", usage: { inputTokens: obj.Usage.InputTokens, outputTokens: obj.Usage.OutputTokens } };
    }
    if (obj.Exception !== undefined) {
        return { type: "error", message: obj.Exception?.Message || "An error occurred." };
    }
    if (obj.TokensBefore !== undefined) {
        return { type: "compaction" };
    }
    return { type: "unknown", raw: json };
}

/**
 * Minimal SSE reader over a fetch Response's ReadableStream — Node's fetch (undici) exposes the
 * same Response/ReadableStream shape as the browser, so this ports directly from AngularChat's
 * readAgentEvents. Events are separated by a blank line; only "data:" lines are read.
 */
export async function* readAgentEvents(response: Response): AsyncGenerator<AgentEventParsed> {
    const reader = response.body!.getReader();
    const decoder = new TextDecoder("utf-8");
    let buffer = "";
    let dataLines: string[] = [];
    let done = false;

    function flushEvent(): AgentEventParsed | null {
        if (dataLines.length === 0) return null;
        const evt = parseAgentEvent(dataLines.join("\n"));
        dataLines = [];
        return evt;
    }

    while (true) {
        const newlineIndex = buffer.indexOf("\n");
        if (newlineIndex >= 0) {
            const line = buffer.slice(0, newlineIndex).replace(/\r$/, "");
            buffer = buffer.slice(newlineIndex + 1);

            if (line.length === 0) {
                const evt = flushEvent();
                if (evt) yield evt;
                continue;
            }
            if (line.startsWith("data:")) {
                dataLines.push(line.slice(5).replace(/^\s/, ""));
            }
            continue;
        }

        if (done) {
            // The stream ended with a final, unterminated line still sitting in buffer (no
            // trailing "\n") — process it exactly like a normal line above before flushing,
            // otherwise a response ending right after "data: {...}" with no blank line after it
            // would silently lose that last event entirely (a real, not just theoretical, case:
            // HTTP chunked responses commonly end without a trailing newline).
            const line = buffer.replace(/\r$/, "");
            if (line.startsWith("data:")) {
                dataLines.push(line.slice(5).replace(/^\s/, ""));
            }
            buffer = "";

            const finalEvt = flushEvent();
            if (finalEvt) yield finalEvt;
            return;
        }

        const result = await reader.read();
        if (result.done) {
            done = true;
            buffer += decoder.decode();
        } else {
            buffer += decoder.decode(result.value, { stream: true });
        }
    }
}

export type SendTurnOutcome = { kind: "started"; events: AsyncGenerator<AgentEventParsed> } | { kind: "steered"; message: string };

// keyStatus maps each provider name (plus "localBaseUrl") to "env" | "config" | "unset" — see
// ConfigEndpoints.cs's BuildKeyStatus for what each value means and why the real secret is never
// included.
export type ConfigStatus = {
    configured: boolean;
    availableProviders: string[];
    keyStatus?: Record<string, string>;
    // True once the user has explicitly run /model at least once — see ConfigEndpoints.cs's
    // /config/status handler for why this can't be derived from AgentWorker.Model instead.
    defaultModelSet?: boolean;
};

export class LitosClient {
    constructor(private readonly baseUrl: string) {}

    async getConfigStatus(): Promise<ConfigStatus> {
        const response = await fetch(`${this.baseUrl}/config/status`, { signal: AbortSignal.timeout(15_000) });
        if (!response.ok) throw new Error(`Litos host returned ${response.status}`);
        return response.json();
    }

    // See ConfigEndpoints.cs's own remarks: the response reflects what was written to disk/env,
    // not what THIS already-running process will use — restartRequired is always true on success,
    // and the caller (extension.ts) must kill and respawn the host for a saved key to take effect.
    async saveKeys(entries: { provider: string; apiKey: string }[], localBaseUrl?: string): Promise<ConfigStatus> {
        const response = await fetch(`${this.baseUrl}/config/keys`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                Entries: entries.map((e) => ({ Provider: e.provider, ApiKey: e.apiKey })),
                LocalBaseUrl: localBaseUrl || null,
            }),
        });

        if (!response.ok) {
            const body = await response.text();
            throw new Error(`Litos host returned ${response.status}${body ? `: ${body}` : ""}`);
        }

        return response.json();
    }

    async listSessions(): Promise<any[]> {
        const response = await fetch(`${this.baseUrl}/sessions`);
        if (!response.ok) throw new Error(`Litos host returned ${response.status}`);
        return response.json();
    }

    async getHistory(sessionId: string): Promise<any[]> {
        const response = await fetch(`${this.baseUrl}/sessions/${encodeURIComponent(sessionId)}/history`);
        if (!response.ok) throw new Error(`Litos host returned ${response.status}`);
        return response.json();
    }

    async sendTurn(sessionId: string, text: string, attachments?: AttachedContent[]): Promise<SendTurnOutcome> {
        const response = await fetch(`${this.baseUrl}/sessions/${encodeURIComponent(sessionId)}/turns`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ input: text, attachments: attachments && attachments.length > 0 ? attachments : undefined }),
        });

        if (!response.ok) {
            const body = await response.text();
            throw new Error(`Litos host returned ${response.status}${body ? `: ${body}` : ""}`);
        }

        if (response.status === 202) {
            const message = await response.text();
            return { kind: "steered", message };
        }

        return { kind: "started", events: readAgentEvents(response) };
    }

    // Backs the composer's Stop/Cancel button — explicitly aborts a stuck or long-running turn
    // server-side (AgentWorker.CancelTurn), rather than relying on the SSE fetch's own connection
    // teardown, which the extension never triggers on its own (sendTurn's fetch has no
    // AbortController). 404 means the turn already finished on its own (a normal race with the
    // user clicking Cancel right as the last event arrives) — not an error worth surfacing.
    async cancelTurn(sessionId: string): Promise<void> {
        const response = await fetch(`${this.baseUrl}/sessions/${encodeURIComponent(sessionId)}/cancel`, { method: "POST" });
        if (!response.ok && response.status !== 404) {
            const body = await response.text();
            throw new Error(`Litos host returned ${response.status}${body ? `: ${body}` : ""}`);
        }
    }

    // Numeric, not string — src/Litos.Tools/Shell/IToolApprovalGate.cs's ApprovalDecision enum
    // (Approve = 0, ApproveAlways = 1, Deny = 2) has no JsonStringEnumConverter configured on
    // either this endpoint's request binding or TurnsEndpoints' own serialization, so
    // System.Text.Json's default numeric enum representation applies both ways.
    async resolveApproval(sessionId: string, approvalId: string, decision: "approve" | "deny"): Promise<void> {
        const response = await fetch(
            `${this.baseUrl}/sessions/${encodeURIComponent(sessionId)}/approvals/${encodeURIComponent(approvalId)}/resolve`,
            {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ Decision: decision === "approve" ? 0 : 2 }),
            },
        );

        if (!response.ok) {
            const body = await response.text();
            throw new Error(`Litos host returned ${response.status}${body ? `: ${body}` : ""}`);
        }
    }

    // --- /provider, /model ---

    async getSettings(): Promise<{ providerName: string; model: string | null; availableProviders: string[]; contextLength: number | null }> {
        return this.getJson(`/settings`);
    }

    async listModels(provider: string): Promise<{ id: string; displayName: string; isDefault: boolean }[]> {
        return this.getJson(`/settings/models?provider=${encodeURIComponent(provider)}`);
    }

    async switchProvider(provider: string): Promise<void> {
        await this.postJson(`/settings/provider`, { Provider: provider });
    }

    async setModel(model: string): Promise<void> {
        await this.postJson(`/settings/model`, { Model: model });
    }

    // --- /branch, /compact ---

    async getBranchPoints(sessionId: string): Promise<{ entryIndex: number; timestamp: string; text: string }[]> {
        return this.getJson(`/sessions/${encodeURIComponent(sessionId)}/branch-points`);
    }

    async branchSession(sessionId: string, entryIndex: number): Promise<{ newSessionId: string }> {
        return this.postJson(`/sessions/${encodeURIComponent(sessionId)}/branch`, { EntryIndex: entryIndex });
    }

    async compactSession(sessionId: string): Promise<{ compacted: boolean }> {
        return this.postJson(`/sessions/${encodeURIComponent(sessionId)}/compact`, {});
    }

    // --- /sessions/{id}/context (status-bar meter + click-through breakdown panel) ---

    async getContextUsage(sessionId: string): Promise<ContextUsage | null> {
        return this.getJson(`/sessions/${encodeURIComponent(sessionId)}/context/usage`);
    }

    async getContextBreakdown(sessionId: string): Promise<ContextBreakdown> {
        return this.getJson(`/sessions/${encodeURIComponent(sessionId)}/context/breakdown`);
    }

    // Null until the session's first turn actually runs (Transcript.WorkingDirectory is only set
    // once AgentWorker falls back to Directory.GetCurrentDirectory() — see AgentWorker.cs).
    async getWorkingDirectory(sessionId: string): Promise<{ workingDirectory: string | null }> {
        return this.getJson(`/sessions/${encodeURIComponent(sessionId)}/working-directory`);
    }

    // --- /reflect ---

    async reflect(sessionId: string, existingAgentsMd: string | null): Promise<{ proposed: string }> {
        return this.postJson(`/sessions/${encodeURIComponent(sessionId)}/reflect`, { ExistingAgentsMd: existingAgentsMd });
    }

    // --- /skills, /skill ---

    async listSkills(cwd: string): Promise<{ name: string; description: string }[]> {
        return this.getJson(`/skills?cwd=${encodeURIComponent(cwd)}`);
    }

    async loadSkill(name: string, cwd: string): Promise<{ name: string; content: string }> {
        return this.getJson(`/skills/${encodeURIComponent(name)}?cwd=${encodeURIComponent(cwd)}`);
    }

    // --- /sessions/{id}/mentions ("@"-mention dropdown) ---

    // fallbackWorkingDirectory is required: a brand-new session's transcript has no
    // WorkingDirectory until its first turn completes, so the host can't derive one on its own
    // for the (very common) case of "@"-mentioning a file in the first message — see
    // FilesEndpoints.cs's own remarks. The caller already resolves this the same way
    // resolveMentionsAsync does (getWorkingDirectory, falling back to sharedHost.cwd).
    async getFileMentions(sessionId: string, query: string, fallbackWorkingDirectory: string): Promise<string[]> {
        return this.getJson(
            `/sessions/${encodeURIComponent(sessionId)}/mentions?query=${encodeURIComponent(query)}&fallbackWorkingDirectory=${encodeURIComponent(fallbackWorkingDirectory)}`,
        );
    }

    // --- /attach + clipboard paste-to-attach ---

    async attachFromPath(path: string): Promise<AttachedContent> {
        return this.postJson(`/attachments/from-path`, { Path: path });
    }

    async attachFromBytes(base64Data: string, mimeType: string, fileName?: string): Promise<AttachedContent> {
        return this.postJson(`/attachments/from-bytes`, { Base64Data: base64Data, MimeType: mimeType, FileName: fileName });
    }

    // --- /mcp ---

    async listMcpServers(): Promise<McpServerStatus[]> {
        return this.getJson(`/mcp/servers`);
    }

    async addMcpServer(server: AddMcpServerRequest): Promise<void> {
        await this.postJson(`/mcp/servers`, server);
    }

    async setMcpServerEnabled(name: string, enabled: boolean): Promise<void> {
        const response = await fetch(`${this.baseUrl}/mcp/servers/${encodeURIComponent(name)}/enabled`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ Enabled: enabled }),
        });
        if (!response.ok) throw new Error(`Litos host returned ${response.status}`);
    }

    async removeMcpServer(name: string): Promise<void> {
        const response = await fetch(`${this.baseUrl}/mcp/servers/${encodeURIComponent(name)}`, { method: "DELETE" });
        if (!response.ok) throw new Error(`Litos host returned ${response.status}`);
    }

    async refreshMcpServers(): Promise<void> {
        await this.postJson(`/mcp/refresh`, {});
    }

    private async getJson<T>(path: string): Promise<T> {
        const response = await fetch(`${this.baseUrl}${path}`);
        if (!response.ok) {
            const body = await response.text();
            throw new Error(`Litos host returned ${response.status}${body ? `: ${body}` : ""}`);
        }
        return response.json();
    }

    private async postJson<T>(path: string, body: unknown): Promise<T> {
        const response = await fetch(`${this.baseUrl}${path}`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
        if (!response.ok) {
            const errorBody = await response.text();
            throw new Error(`Litos host returned ${response.status}${errorBody ? `: ${errorBody}` : ""}`);
        }
        return response.status === 200 || response.status === 201 ? response.json() : (undefined as T);
    }
}

// Mirrors Litos.Agent.Session.ContextUsageSnapshot/ContextBreakdownSnapshot (see ContextEndpoints.cs) —
// Level/category serialize as their C# enum member names (Normal/Warning/Critical,
// SystemPrompt/ToolSchemas/Memory/Skills/History/ToolResults/Images/CompactionSummary) since
// neither endpoint's response goes through a JsonStringEnumConverter-free numeric path like
// ApprovalDecision/McpTransportKind do — these are plain anonymous objects with .ToString()'d enums.
export type ContextUsage = {
    usedTokens: number;
    contextLength: number;
    fraction: number;
    level: "Normal" | "Warning" | "Critical";
    isStale: boolean;
};

export type ContextBreakdownSubItem = { label: string; estimatedTokens: number };

export type ContextBreakdownEntry = {
    category: string;
    label: string;
    estimatedTokens: number;
    subItems: ContextBreakdownSubItem[] | null;
};

export type ContextBreakdown = {
    totalEstimatedTokens: number;
    lastRealUsageTokens: number | null;
    contextLength: number | null;
    entries: ContextBreakdownEntry[];
};

export type AttachedContent = {
    kind: "image" | "document";
    fileName: string;
    mimeType?: string | null;
    base64Data?: string | null;
    documentText?: string | null;
};

// Numeric enums, no JsonStringEnumConverter configured anywhere on this endpoint (same as
// ApprovalDecision) — confirmed against the real declarations: McpTransportKind (Stdio=0,
// Http=1, src/Litos.Tools.Mcp/McpConfig.cs) and ToolPermission (Deny=0, Ask=1, Full=2,
// src/Litos.Tools/Shell/ToolPermission.cs).
export type AddMcpServerRequest = {
    Name: string;
    Transport: 0 | 1;
    Command?: string | null;
    Args?: string[] | null;
    Env?: Record<string, string> | null;
    Url?: string | null;
    DefaultPermission: 0 | 1 | 2;
};

export type McpServerStatus = {
    name: string;
    transport: number;
    command: string | null;
    args: string[] | null;
    url: string | null;
    enabled: boolean;
    defaultPermission: number;
    status: string;
    error: string | null;
    toolCount: number;
    promptCount: number;
};
