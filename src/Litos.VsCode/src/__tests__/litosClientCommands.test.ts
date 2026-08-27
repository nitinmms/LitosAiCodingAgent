import { describe, it, expect, afterEach } from "vitest";
import { LitosClient } from "../agentEvents";

// Mirrors the existing resolveApproval tests' fetch-mocking pattern: capture the request
// (url/method/body) a client method actually makes, without needing a real Litos.VsCodeHost
// process running — these tests exercise the request-construction logic (URL, HTTP method, JSON
// body shape/casing) each new slash-command backing method added for parity, not the server.
let originalFetch: typeof fetch;
let capturedRequests: { url: string; init?: RequestInit }[] = [];

function mockFetch(response: () => Response) {
    originalFetch = globalThis.fetch;
    capturedRequests = [];
    globalThis.fetch = (async (url: string, init?: RequestInit) => {
        capturedRequests.push({ url, init });
        return response();
    }) as typeof fetch;
}

afterEach(() => {
    if (originalFetch) globalThis.fetch = originalFetch;
});

const client = new LitosClient("http://127.0.0.1:12345");

describe("LitosClient — /settings", () => {
    it("getSettings issues a plain GET", async () => {
        mockFetch(() => new Response(JSON.stringify({ providerName: "anthropic", model: "claude", availableProviders: ["anthropic"] }), { status: 200 }));

        const result = await client.getSettings();

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/settings");
        expect(result.providerName).toBe("anthropic");
    });

    it("listModels passes the provider as a query parameter", async () => {
        mockFetch(() => new Response(JSON.stringify([]), { status: 200 }));

        await client.listModels("openrouter");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/settings/models?provider=openrouter");
    });

    it("switchProvider POSTs the PascalCase Provider field", async () => {
        mockFetch(() => new Response(JSON.stringify({}), { status: 200 }));

        await client.switchProvider("openai");

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Provider: "openai" });
    });

    it("setModel POSTs the PascalCase Model field", async () => {
        mockFetch(() => new Response(JSON.stringify({}), { status: 200 }));

        await client.setModel("gpt-5");

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Model: "gpt-5" });
    });
});

describe("LitosClient — /branch, /compact, /reflect", () => {
    it("getBranchPoints issues a GET to the session-scoped path", async () => {
        mockFetch(() => new Response(JSON.stringify([]), { status: 200 }));

        await client.getBranchPoints("session-1");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/branch-points");
    });

    it("branchSession POSTs the PascalCase EntryIndex field", async () => {
        mockFetch(() => new Response(JSON.stringify({ newSessionId: "abc" }), { status: 200 }));

        const result = await client.branchSession("session-1", 3);

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ EntryIndex: 3 });
        expect(result.newSessionId).toBe("abc");
    });

    it("compactSession POSTs with an empty body", async () => {
        mockFetch(() => new Response(JSON.stringify({ compacted: true }), { status: 200 }));

        const result = await client.compactSession("session-1");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/compact");
        expect(result.compacted).toBe(true);
    });

    it("reflect POSTs the PascalCase ExistingAgentsMd field, null when absent", async () => {
        mockFetch(() => new Response(JSON.stringify({ proposed: "# AGENTS.md" }), { status: 200 }));

        await client.reflect("session-1", null);

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ ExistingAgentsMd: null });
    });
});

describe("LitosClient — /skills", () => {
    it("listSkills passes cwd as a query parameter", async () => {
        mockFetch(() => new Response(JSON.stringify([]), { status: 200 }));

        await client.listSkills("c:/some/workspace");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/skills?cwd=c%3A%2Fsome%2Fworkspace");
    });

    it("loadSkill encodes both the skill name and cwd", async () => {
        mockFetch(() => new Response(JSON.stringify({ name: "my skill", content: "body" }), { status: 200 }));

        await client.loadSkill("my skill", "c:/ws");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/skills/my%20skill?cwd=c%3A%2Fws");
    });
});

describe("LitosClient — attachments", () => {
    it("attachFromPath POSTs the PascalCase Path field", async () => {
        mockFetch(() => new Response(JSON.stringify({ kind: "document", fileName: "a.txt" }), { status: 200 }));

        await client.attachFromPath("c:/a.txt");

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Path: "c:/a.txt" });
    });

    it("attachFromBytes POSTs Base64Data/MimeType/FileName", async () => {
        mockFetch(() => new Response(JSON.stringify({ kind: "image", fileName: "x.png" }), { status: 200 }));

        await client.attachFromBytes("AAAA", "image/png", "x.png");

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Base64Data: "AAAA", MimeType: "image/png", FileName: "x.png" });
    });
});

describe("LitosClient — /sessions/{id}/mentions", () => {
    it("getFileMentions issues a GET with the query and fallbackWorkingDirectory as query parameters", async () => {
        mockFetch(() => new Response(JSON.stringify(["src/Foo.ts", "src/Bar.ts"]), { status: 200 }));

        const result = await client.getFileMentions("session-1", "Foo", "c:/repo");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/mentions?query=Foo&fallbackWorkingDirectory=c%3A%2Frepo");
        expect(result).toEqual(["src/Foo.ts", "src/Bar.ts"]);
    });

    it("getFileMentions encodes special characters in the query", async () => {
        mockFetch(() => new Response(JSON.stringify([]), { status: 200 }));

        await client.getFileMentions("session-1", "a b&c", "c:/repo");

        expect(capturedRequests[0].url).toContain("query=a%20b%26c");
    });

    it("getFileMentions passes an empty query through for the initial (no-token-yet) suggestion list", async () => {
        mockFetch(() => new Response(JSON.stringify(["a.ts"]), { status: 200 }));

        await client.getFileMentions("session-1", "", "c:/repo");

        expect(capturedRequests[0].url).toContain("query=&fallbackWorkingDirectory=");
    });

    it("getFileMentions always passes fallbackWorkingDirectory — a brand-new session has no WorkingDirectory on its transcript until its first turn completes, so the host can't derive one on its own", async () => {
        mockFetch(() => new Response(JSON.stringify(["src/Foo.ts"]), { status: 200 }));

        await client.getFileMentions("session-1", "Foo", "c:/GenAI/pi-story-writer");

        expect(capturedRequests[0].url).toContain("fallbackWorkingDirectory=c%3A%2FGenAI%2Fpi-story-writer");
    });
});

describe("LitosClient — /sessions/{id}/cancel", () => {
    it("cancelTurn POSTs to the session-scoped cancel path", async () => {
        mockFetch(() => new Response(null, { status: 200 }));

        await client.cancelTurn("session-1");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/cancel");
        expect(capturedRequests[0].init!.method).toBe("POST");
    });

    it("cancelTurn does not throw on 404 — the turn already finished on its own, not an error", async () => {
        mockFetch(() => new Response("No turn is currently running for this session.", { status: 404 }));

        await expect(client.cancelTurn("session-1")).resolves.toBeUndefined();
    });

    it("cancelTurn throws on other non-ok statuses, including the response body", async () => {
        mockFetch(() => new Response("host error", { status: 500 }));

        await expect(client.cancelTurn("session-1")).rejects.toThrow(/500.*host error/);
    });
});

describe("LitosClient — /sessions/{id}/context", () => {
    it("getContextUsage issues a GET to the session-scoped usage path", async () => {
        mockFetch(() => new Response(JSON.stringify({ usedTokens: 100, contextLength: 1000, fraction: 0.1, level: "Normal", isStale: false }), { status: 200 }));

        const result = await client.getContextUsage("session-1");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/context/usage");
        expect(result).toEqual({ usedTokens: 100, contextLength: 1000, fraction: 0.1, level: "Normal", isStale: false });
    });

    it("getContextUsage passes through isStale: true when the host flags the estimate as unreliable", async () => {
        mockFetch(() => new Response(JSON.stringify({ usedTokens: 500_000, contextLength: 200_000, fraction: 1, level: "Critical", isStale: true }), { status: 200 }));

        const result = await client.getContextUsage("session-1");

        expect(result!.isStale).toBe(true);
    });

    it("getContextUsage returns null when the host has no usage snapshot yet", async () => {
        mockFetch(() => new Response("null", { status: 200 }));

        const result = await client.getContextUsage("session-1");

        expect(result).toBeNull();
    });

    it("getContextBreakdown issues a GET to the session-scoped breakdown path", async () => {
        const breakdown = { totalEstimatedTokens: 500, lastRealUsageTokens: null, contextLength: 1000, entries: [] };
        mockFetch(() => new Response(JSON.stringify(breakdown), { status: 200 }));

        const result = await client.getContextBreakdown("session-1");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/sessions/session-1/context/breakdown");
        expect(result).toEqual(breakdown);
    });
});

describe("LitosClient — /mcp", () => {
    it("listMcpServers issues a GET", async () => {
        mockFetch(() => new Response(JSON.stringify([]), { status: 200 }));

        await client.listMcpServers();

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/mcp/servers");
    });

    it("addMcpServer POSTs the server object as-is (already PascalCase from the caller)", async () => {
        // Results.Ok() on the host returns a genuinely empty body (not "{}"), which is what
        // triggered "Unexpected end of JSON input" — see agentEvents.ts's postJson.
        mockFetch(() => new Response(null, { status: 200 }));
        const server = { Name: "s1", Transport: 0 as const, Command: "npx", Args: ["-y", "pkg"], DefaultPermission: 1 as const };

        await client.addMcpServer(server);

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual(server);
    });

    it("setMcpServerEnabled POSTs to the name-scoped path with the PascalCase Enabled field", async () => {
        mockFetch(() => new Response(null, { status: 200 }));

        await client.setMcpServerEnabled("my server", false);

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/mcp/servers/my%20server/enabled");
        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Enabled: false });
    });

    it("setMcpServerDefaultPermission POSTs to the name-scoped permission path with the PascalCase DefaultPermission field", async () => {
        mockFetch(() => new Response(null, { status: 200 }));

        await client.setMcpServerDefaultPermission("my server", 2);

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/mcp/servers/my%20server/permission");
        expect(capturedRequests[0].init!.method).toBe("POST");
        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ DefaultPermission: 2 });
    });

    it("removeMcpServer issues a DELETE to the name-scoped path", async () => {
        mockFetch(() => new Response(null, { status: 200 }));

        await client.removeMcpServer("my server");

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/mcp/servers/my%20server");
        expect(capturedRequests[0].init!.method).toBe("DELETE");
    });

    it("refreshMcpServers POSTs with an empty body", async () => {
        mockFetch(() => new Response(null, { status: 200 }));

        await client.refreshMcpServers();

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/mcp/refresh");
        expect(capturedRequests[0].init!.method).toBe("POST");
    });
});

describe("LitosClient — /config", () => {
    it("getConfigStatus issues a plain GET and passes through keyStatus and defaultModelSet", async () => {
        mockFetch(() => new Response(JSON.stringify({
            configured: true,
            availableProviders: ["anthropic"],
            keyStatus: { anthropic: "env", openai: "unset" },
            defaultModelSet: false,
        }), { status: 200 }));

        const result = await client.getConfigStatus();

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/config/status");
        expect(result.configured).toBe(true);
        expect(result.keyStatus).toEqual({ anthropic: "env", openai: "unset" });
        expect(result.defaultModelSet).toBe(false);
    });

    it("saveKeys POSTs PascalCase Entries/LocalBaseUrl, null when no local base URL was entered", async () => {
        mockFetch(() => new Response(JSON.stringify({ configured: true, availableProviders: ["anthropic"] }), { status: 200 }));

        await client.saveKeys([{ provider: "anthropic", apiKey: "sk-abc" }]);

        expect(capturedRequests[0].url).toBe("http://127.0.0.1:12345/config/keys");
        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Entries: [{ Provider: "anthropic", ApiKey: "sk-abc" }], LocalBaseUrl: null });
    });

    it("saveKeys passes a non-empty local base URL through as-is", async () => {
        mockFetch(() => new Response(JSON.stringify({ configured: true, availableProviders: [] }), { status: 200 }));

        await client.saveKeys([], "http://localhost:1234/v1");

        const body = JSON.parse(capturedRequests[0].init!.body as string);
        expect(body).toEqual({ Entries: [], LocalBaseUrl: "http://localhost:1234/v1" });
    });
});

describe("LitosClient — error handling is consistent across the new methods", () => {
    it("a non-ok GET throws including the response body", async () => {
        mockFetch(() => new Response("workspace not found", { status: 400 }));

        await expect(client.listSkills("bad")).rejects.toThrow(/400.*workspace not found/);
    });

    it("a non-ok POST throws including the response body", async () => {
        mockFetch(() => new Response("no such provider", { status: 400 }));

        await expect(client.switchProvider("nope")).rejects.toThrow(/400.*no such provider/);
    });
});
