import { describe, it, expect, beforeAll } from "vitest";
import * as path from "path";
import { getWebviewHtml } from "../webviewContent";

// Same extraction approach as keysPopup.test.ts: getWebviewHtml() has no vscode-free export and the
// generated inner <script> is DOM-dependent, so these tests scan the real generated source text
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

describe("default-model onboarding hint markup", () => {
    it("renders the popup and its overlay, keeping the chat area visible underneath", () => {
        expect(html).toContain('id="defaultModelHint"');
        expect(html).toContain('id="defaultModelHintOverlay"');
        expect(html).not.toMatch(/#chatArea\s*\{[^}]*display:\s*none/);
    });

    it("mentions /model as the way to fix it", () => {
        expect(html).toContain("/model");
    });
});

describe("default-model hint open/close wiring", () => {
    it("opens on an openDefaultModelHint message from the extension host", () => {
        expect(innerScript).toContain("message.type === 'openDefaultModelHint'");
        expect(innerScript).toContain("openDefaultModelHint()");
    });

    it("Close, Escape, and clicking the overlay all close it and notify the extension host", () => {
        expect(innerScript).toContain("defaultModelHintCloseButton.addEventListener('click', closeDefaultModelHint)");
        expect(innerScript).toContain("defaultModelHintOverlayEl.addEventListener('click', closeDefaultModelHint)");
        expect(innerScript).toContain("event.key === 'Escape' && defaultModelHintEl.classList.contains('visible')");

        const closeFn = innerScript.slice(
            innerScript.indexOf("function closeDefaultModelHint"),
            innerScript.indexOf("defaultModelHintCloseButton.addEventListener")
        );
        expect(closeFn).toContain("vscode.postMessage({ type: 'dismissDefaultModelHint' })");
    });
});
