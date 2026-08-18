import { describe, it, expect } from "vitest";
import { parseAgentEvent, readAgentEvents, LitosClient } from "../agentEvents";

describe("parseAgentEvent", () => {
    it("classifies TextDelta/ReasoningDelta as textDelta (genuinely indistinguishable on the wire)", () => {
        expect(parseAgentEvent('{"Text":"hello"}')).toEqual({ type: "textDelta", text: "hello" });
    });

    it("classifies ToolCallStarted", () => {
        expect(parseAgentEvent('{"CallId":"c1","ToolName":"shell"}')).toEqual({
            type: "toolCallStarted",
            callId: "c1",
            toolName: "shell",
        });
    });

    it("classifies ToolCallCompleted", () => {
        expect(parseAgentEvent('{"CallId":"c1","ToolName":"shell","Arguments":{"command":"echo hi"}}')).toEqual({
            type: "toolCallCompleted",
            callId: "c1",
            toolName: "shell",
        });
    });

    it("classifies ToolCallResult success", () => {
        expect(
            parseAgentEvent('{"CallId":"c1","ToolName":"shell","Result":{"Text":"hi","IsError":false}}'),
        ).toEqual({
            type: "toolCallResult",
            callId: "c1",
            toolName: "shell",
            success: true,
            resultText: "hi",
        });
    });

    it("classifies ToolCallResult failure", () => {
        expect(
            parseAgentEvent('{"CallId":"c1","ToolName":"shell","Result":{"Text":"boom","IsError":true}}'),
        ).toEqual({
            type: "toolCallResult",
            callId: "c1",
            toolName: "shell",
            success: false,
            resultText: "boom",
        });
    });

    it("classifies ToolCallSkipped", () => {
        expect(parseAgentEvent('{"CallId":"c1","Reason":"denied"}')).toEqual({
            type: "toolCallSkipped",
            callId: "c1",
            reason: "denied",
        });
    });

    it("classifies MessageCompleted, carrying the Usage payload", () => {
        expect(
            parseAgentEvent('{"Message":{"Role":1,"Content":[]},"Usage":{"InputTokens":1,"OutputTokens":2}}'),
        ).toEqual({ type: "messageCompleted", usage: { inputTokens: 1, outputTokens: 2 } });
    });

    it("classifies ErrorOccurred", () => {
        expect(parseAgentEvent('{"Exception":{"Message":"bad thing"}}')).toEqual({
            type: "error",
            message: "bad thing",
        });
    });

    it("classifies CompactionOccurred", () => {
        expect(parseAgentEvent('{"TokensBefore":12345}')).toEqual({ type: "compaction" });
    });

    it("classifies PendingApprovalRequestedWireEvent ahead of the ToolName/CallId toolCallStarted case", () => {
        expect(
            parseAgentEvent(
                '{"ApprovalId":"a1","ToolName":"mcp__server__tool","Summary":"call a tool","DiffOrCommand":null}',
            ),
        ).toEqual({
            type: "approvalRequested",
            approvalId: "a1",
            toolName: "mcp__server__tool",
            summary: "call a tool",
            diffOrCommand: null,
        });
    });

    it("classifies PendingApprovalResolvedWireEvent", () => {
        expect(parseAgentEvent('{"ApprovalId":"a1"}')).toEqual({ type: "approvalResolved", approvalId: "a1" });
    });

    it("falls back to unknown for malformed JSON", () => {
        expect(parseAgentEvent("not json")).toEqual({ type: "unknown", raw: "not json" });
    });

    it("falls back to unknown for a shape matching no known variant", () => {
        expect(parseAgentEvent('{"SomethingElse":true}')).toEqual({ type: "unknown", raw: '{"SomethingElse":true}' });
    });
});

function sseResponse(body: string): Response {
    const stream = new ReadableStream<Uint8Array>({
        start(controller) {
            controller.enqueue(new TextEncoder().encode(body));
            controller.close();
        },
    });
    return new Response(stream);
}

describe("readAgentEvents", () => {
    it("parses multiple blank-line-separated SSE events from one chunk", async () => {
        const body = 'event: agent-event\ndata: {"Text":"hel"}\n\nevent: agent-event\ndata: {"Text":"lo"}\n\n';
        const events = [];
        for await (const evt of readAgentEvents(sseResponse(body))) events.push(evt);

        expect(events).toEqual([
            { type: "textDelta", text: "hel" },
            { type: "textDelta", text: "lo" },
        ]);
    });

    it("ignores non-data lines (event:, comments)", async () => {
        const body = ": keep-alive\nevent: agent-event\ndata: {"+'"TokensBefore":1}\n\n';
        const events = [];
        for await (const evt of readAgentEvents(sseResponse(body))) events.push(evt);

        expect(events).toEqual([{ type: "compaction" }]);
    });

    it("flushes a final event with no trailing blank line", async () => {
        const body = 'data: {"Text":"last"}';
        const events = [];
        for await (const evt of readAgentEvents(sseResponse(body))) events.push(evt);

        expect(events).toEqual([{ type: "textDelta", text: "last" }]);
    });
});

describe("LitosClient.resolveApproval", () => {
    it("sends the numeric ApprovalDecision (Approve=0), not a string", async () => {
        let capturedBody: string | undefined;
        const originalFetch = globalThis.fetch;
        globalThis.fetch = (async (_url: string, init?: RequestInit) => {
            capturedBody = init?.body as string;
            return new Response(null, { status: 200 });
        }) as typeof fetch;

        try {
            const client = new LitosClient("http://127.0.0.1:12345");
            await client.resolveApproval("session-1", "approval-1", "approve");
            expect(JSON.parse(capturedBody!)).toEqual({ Decision: 0 });

            await client.resolveApproval("session-1", "approval-1", "deny");
            expect(JSON.parse(capturedBody!)).toEqual({ Decision: 2 });
        } finally {
            globalThis.fetch = originalFetch;
        }
    });

    it("throws with the response body on a non-ok status", async () => {
        const originalFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response("no such approval", { status: 404 })) as typeof fetch;

        try {
            const client = new LitosClient("http://127.0.0.1:12345");
            await expect(client.resolveApproval("session-1", "approval-1", "approve")).rejects.toThrow(
                /404.*no such approval/,
            );
        } finally {
            globalThis.fetch = originalFetch;
        }
    });
});
