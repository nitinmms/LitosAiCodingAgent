import { describe, it, expect, beforeAll } from "vitest";
import * as path from "path";
import { getWebviewHtml } from "../webviewContent";

// Same extraction approach as webviewComposer.test.ts's own comment explains: getWebviewHtml() has
// no vscode-free export and the generated inner <script> is DOM-dependent, so these tests scan the
// real generated source text for the /keys popup (replacing the old always-inline #firstRun page)
// rather than executing it.

let html: string;
let innerScript: string;

beforeAll(() => {
    const fakeWebview: any = { cspSource: "vscode-webview:", asWebviewUri: (u: any) => u };
    const fakeExtensionUri: any = { fsPath: path.resolve(__dirname, "../..") };
    html = getWebviewHtml(fakeWebview, fakeExtensionUri);
    const scriptBlocks = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
    innerScript = scriptBlocks[1];

    expect(() => new Function(innerScript)).not.toThrow();
});

describe("/keys is a real slash command", () => {
    it("is listed in the command-menu registry", () => {
        expect(innerScript).toContain("{ name: 'keys', desc: 'Add or update API keys' }");
    });
});

describe("keys popup markup", () => {
    it("renders one field per provider Litos.Gui's ApiKeysWindow has, including Tavily", () => {
        ["key-anthropic", "key-openai", "key-gemini", "key-openrouter", "key-mesh_api", "key-local-url", "key-local", "key-tavily"].forEach((id) => {
            expect(html).toContain(`id="${id}"`);
        });
    });

    it("masks every secret field but leaves the local base URL as plain text", () => {
        ["key-anthropic", "key-openai", "key-gemini", "key-openrouter", "key-mesh_api", "key-local", "key-tavily"].forEach((id) => {
            const fieldMatch = html.match(new RegExp(`<input[^>]*id="${id}"[^>]*>`));
            expect(fieldMatch).toBeTruthy();
            expect(fieldMatch![0]).toContain('type="password"');
        });
        const urlFieldMatch = html.match(/<input[^>]*id="key-local-url"[^>]*>/);
        expect(urlFieldMatch).toBeTruthy();
        expect(urlFieldMatch![0]).toContain('type="text"');
    });

    it("labels Tavily as websearch, matching the requested wording", () => {
        expect(html).toContain(">Tavily (Websearch)<");
    });

    it("no longer ships the old always-inline first-run page", () => {
        expect(html).not.toContain('id="firstRun"');
        expect(html).not.toContain('id="firstRunSave"');
    });

    it("keeps the chat area itself always visible — the popup now overlays it instead of replacing it", () => {
        expect(html).not.toMatch(/#chatArea\s*\{[^}]*display:\s*none/);
    });
});

describe("keys popup open/close wiring", () => {
    it("opens on an openKeysPopup message from the extension host, carrying isFirstRun and keyStatus", () => {
        expect(innerScript).toContain("message.type === 'openKeysPopup'");
        expect(innerScript).toContain("openKeysPopup(!!message.isFirstRun, message.keyStatus)");
    });

    it("first-run mode hides the Cancel button so the popup can't be dismissed without saving", () => {
        const openFn = innerScript.slice(innerScript.indexOf("function openKeysPopup"), innerScript.indexOf("function closeKeysPopup"));
        expect(openFn).toContain("keysPopupCancelButton.style.display = 'none'");
    });

    it("closeKeysPopup is a no-op while in first-run mode — mirrors ApiKeysWindow having no dismiss-without-saving path there", () => {
        const closeFn = innerScript.slice(innerScript.indexOf("function closeKeysPopup"), innerScript.indexOf("keysPopupCancelButton.addEventListener"));
        expect(closeFn).toContain("if (keysPopupIsFirstRun) return;");
    });

    it("Escape and clicking the overlay both close the popup", () => {
        expect(innerScript).toContain("keysPopupOverlayEl.addEventListener('click', closeKeysPopup)");
        expect(innerScript).toContain("event.key === 'Escape' && keysPopupEl.classList.contains('visible')");
    });

    it("a successful save shows a reload prompt instead of auto-restarting the host and closing", () => {
        expect(innerScript).toContain("message.type === 'saveKeysSuccess'");
        const successHandler = innerScript.slice(
            innerScript.indexOf("message.type === 'saveKeysSuccess'"),
            innerScript.indexOf("message.type === 'saveKeysError'")
        );
        expect(successHandler).toContain("keysPopupSuccessEl.classList.add('visible')");
        expect(successHandler).not.toContain("closeKeysPopup()");
    });

    it("the reload button asks the extension host to reload the window", () => {
        expect(innerScript).toContain("keysPopupReloadButton.addEventListener('click'");
        const reloadHandler = innerScript.slice(
            innerScript.indexOf("keysPopupReloadButton.addEventListener('click'"),
        );
        expect(reloadHandler).toContain("vscode.postMessage({ type: 'reloadWindow' })");
    });
});

describe("keys popup validation and save", () => {
    it("requires at least one key or a local base URL before posting saveKeys", () => {
        const saveHandler = innerScript.slice(
            innerScript.indexOf("keysPopupSaveButton.addEventListener('click'"),
            innerScript.indexOf("let currentAssistantBubble")
        );
        expect(saveHandler).toContain("if (entries.length === 0 && !localBaseUrl)");
        expect(saveHandler).toContain("vscode.postMessage({ type: 'saveKeys', entries, localBaseUrl, isFirstRun: keysPopupIsFirstRun })");
    });

    it("collects all seven provider entries under KEY_FIELDS, not just the original four", () => {
        expect(innerScript).toContain("['anthropic', 'key-anthropic'");
        expect(innerScript).toContain("['openai', 'key-openai'");
        expect(innerScript).toContain("['gemini', 'key-gemini'");
        expect(innerScript).toContain("['openrouter', 'key-openrouter'");
        expect(innerScript).toContain("['mesh_api', 'key-mesh_api'");
        expect(innerScript).toContain("['local', 'key-local'");
        expect(innerScript).toContain("['tavily', 'key-tavily'");
    });
});

describe("keys popup hint rendering — matches Litos.Gui's ApiKeysWindow: status shown as the field's own placeholder, not a separate label", () => {
    it("sets the placeholder (not a sibling element) for an env-set field, including the real env var name", () => {
        const renderFn = innerScript.slice(innerScript.indexOf("function renderKeyHints"), innerScript.indexOf("function openKeysPopup"));
        expect(renderFn).toContain("status[provider] === 'env'");
        expect(renderFn).toContain("envVar + ' — already set'");
        expect(renderFn).toContain("inputEl2.placeholder =");
    });

    it("distinguishes an env-set field from a config.json-set field", () => {
        const renderFn = innerScript.slice(innerScript.indexOf("function renderKeyHints"), innerScript.indexOf("function openKeysPopup"));
        expect(renderFn).toContain("status[provider] === 'config'");
        expect(renderFn).toContain("'Already set — leave blank to keep'");
    });

    it("falls back to the plain env var name (or a helpful note for local/tavily) when nothing is set", () => {
        expect(innerScript).toContain("['local', 'key-local', 'LOCAL_API_KEY', \"Most local servers (e.g. LM Studio) don't need one\"]");
        expect(innerScript).toContain("['tavily', 'key-tavily', 'TAVILY_API_KEY', 'TAVILY_API_KEY — enables web search']");
    });

    it("gives the local base URL field the same already-set-vs-unset placeholder convention", () => {
        const renderFn = innerScript.slice(innerScript.indexOf("function renderKeyHints"), innerScript.indexOf("function openKeysPopup"));
        expect(renderFn).toContain("status.localBaseUrl === 'config'");
        expect(renderFn).toContain("'http://localhost:1234/v1'");
    });

    it("first-run mode renders with no keyStatus (nothing can be already-set on first run)", () => {
        const openFn = innerScript.slice(innerScript.indexOf("function openKeysPopup"), innerScript.indexOf("function closeKeysPopup"));
        expect(openFn).toContain("renderKeyHints(null)");
    });
});
