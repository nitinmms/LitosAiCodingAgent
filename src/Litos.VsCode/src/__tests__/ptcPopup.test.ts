import { describe, it, expect, beforeAll } from "vitest";
import * as path from "path";
import { getWebviewHtml } from "../webviewContent";

// Same extraction approach as keysPopup.test.ts/defaultModelHint.test.ts: getWebviewHtml() has no
// vscode-free export and the generated inner <script> is DOM-dependent, so these tests scan the
// real generated source text rather than executing it.

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

describe("/ptc slash command", () => {
    it("is offered in the command menu", () => {
        expect(html).toContain("{ name: 'ptc', desc: 'Programmatic Tool Calling' }");
    });
});

describe("PTC popup markup", () => {
    it("renders the popup and its overlay, keeping the chat area visible underneath", () => {
        expect(html).toContain('id="ptcPopup"');
        expect(html).toContain('id="ptcPopupOverlay"');
        expect(html).not.toMatch(/#chatArea\s*\{[^}]*display:\s*none/);
    });

    it("exposes the toggle as a real checkbox so it stays keyboard- and screen-reader-accessible", () => {
        expect(html).toContain('type="checkbox" id="ptcSwitchInput"');
        expect(html).toContain('aria-label="Programmatic Tool Calling"');
    });

    // The consent wording is the whole reason this popup exists rather than a bare toggle: PTC
    // reduces gating in ways the toggle's name alone does not convey.
    it("states that kernel code runs ungated with full local permissions", () => {
        expect(html).toContain("full local permissions");
        expect(html).toMatch(/not gated by an approval prompt/i);
    });

    it("states plainly that MCP Ask is not prompted while Deny is still honored", () => {
        expect(html).toMatch(/MCP tools are not prompted for approval/i);
        expect(html).toMatch(/<strong>Ask<\/strong>[\s\S]*?called without asking/i);
        expect(html).toMatch(/<strong>Deny<\/strong>\s*stay blocked/i);
    });

    it("carries a distinct message for a build with no kernel binary, hidden by default", () => {
        expect(html).toContain('id="ptcUnavailable"');
        expect(html).toMatch(/#ptcUnavailable\s*\{[^}]*display:\s*none/);
    });
});

describe("PTC indicator placement", () => {
    it("lives on the model/provider row, pinned right", () => {
        const modelRow = html.slice(html.indexOf('<div id="modelInfo"'), html.indexOf("</div>", html.indexOf('<div id="modelInfo"')));
        expect(modelRow).toContain('id="ptcIndicator"');
        // #modelInfo is a flex row: without margin-left:auto the indicator would sit immediately
        // after the model text instead of at the trailing edge.
        expect(html).toMatch(/#ptcIndicator\s*\{[^}]*margin-left:\s*auto/);
    });

    // Matches the #contextUsage/#workingDir convention: a status row's presence must never itself
    // be a signal, so the indicator reserves its space and only changes visibility.
    it("hides via visibility, not display, so the row's height never shifts", () => {
        expect(html).toMatch(/#ptcIndicator\s*\{[^}]*visibility:\s*hidden/);
        expect(html).not.toMatch(/#ptcIndicator\s*\{[^}]*display:\s*none/);
    });
});

describe("PTC open/close and state wiring", () => {
    it("opens on an openPtcPopup message from the extension host", () => {
        expect(innerScript).toContain("message.type === 'openPtcPopup'");
        expect(innerScript).toContain("openPtcPopup()");
    });

    it("Close, Escape, and clicking the overlay all close it", () => {
        expect(innerScript).toContain("ptcPopupCloseEl.addEventListener('click', closePtcPopup)");
        expect(innerScript).toContain("ptcPopupOverlayEl.addEventListener('click', closePtcPopup)");
        expect(innerScript).toContain("event.key === 'Escape' && ptcPopupEl.classList.contains('visible')");
    });

    it("re-reads state on open rather than trusting whatever was cached", () => {
        const openFn = innerScript.slice(innerScript.indexOf("function openPtcPopup"), innerScript.indexOf("function closePtcPopup"));
        expect(openFn).toContain("type: 'getPtc'");
    });

    // The webview must never decide PTC state locally: the host persists it, and the reply is what
    // updates the UI. Otherwise a failed write would leave the indicator lying about the session.
    it("round-trips every flip through the host instead of setting state locally", () => {
        expect(innerScript).toContain("vscode.postMessage({ type: 'setPtc', enabled: ptcSwitchInputEl.checked })");
        expect(innerScript).toContain("message.type === 'ptcState'");
        expect(innerScript).toContain("renderPtc(message.state)");
    });

    it("disables the switch and refuses to turn on when the host reports PTC unavailable", () => {
        const renderFn = innerScript.slice(innerScript.indexOf("function renderPtc"), innerScript.indexOf("function openPtcPopup"));
        expect(renderFn).toContain("ptcSwitchInputEl.disabled = !ptcAvailable");
        expect(renderFn).toContain("ptcUnavailableEl.classList.toggle('visible', !ptcAvailable)");

        const changeHandler = innerScript.slice(innerScript.indexOf("ptcSwitchInputEl.addEventListener('change'"));
        expect(changeHandler.slice(0, 300)).toContain("if (!ptcAvailable)");
    });

    it("lights the indicator only while PTC is enabled", () => {
        const renderFn = innerScript.slice(innerScript.indexOf("function renderPtc"), innerScript.indexOf("function openPtcPopup"));
        expect(renderFn).toContain("ptcIndicatorEl.classList.toggle('visible', enabled)");
    });
});
