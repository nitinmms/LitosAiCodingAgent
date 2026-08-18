import { describe, it, expect, beforeAll } from "vitest";
import * as fs from "fs";
import * as path from "path";
import { getWebviewHtml } from "../webviewContent";

// getWebviewHtml() builds the entire webview page — including its Markdown rendering setup — as
// one big string with no `vscode`-free export to unit test directly (see hostProcess.test.ts's
// own comment on the same tradeoff for LitosHostProcess.start). These tests instead take the same
// approach the manual verification during development used: extract the real generated <script>
// content and execute it in a minimal sandbox, so what's tested is the actual bytes the webview
// would receive, not a hand-copied re-implementation that could silently drift from the source.
//
// Two things specifically need covering here, both real bugs found and fixed during this feature:
// 1. A stray backtick inside a comment *inside* getWebviewHtml's own outer template literal once
//    prematurely closed/reopened it and broke TypeScript compilation outright — tsc itself catches
//    that class of bug (a plain `npx tsc` run would fail), so no dedicated test for it here, but it's
//    why the assertions below check the actual generated <script> TEXT for the exact override
//    variable names (markedRenderer.link/.html), not just "no exception was thrown" — a silent
//    rename/typo in that wiring (a real mistake made once already while writing it) would
//    otherwise only surface as a live webview quietly falling back to marked's default <a>-tag
//    link rendering, exactly the unreliable-inside-a-webview behavior this override exists to avoid.
// 2. marked's default `renderer.link`/`renderer.html` needed overriding: links must become
//    data-share-link spans (not real <a> — see webviewContent.ts's own comment on why raw anchor
//    navigation inside a VS Code webview was unreliable), and raw HTML in model-authored Markdown
//    must render as inert escaped text, not execute/pass through.

let markedGlobal: any;

beforeAll(() => {
    const fakeWebview: any = { cspSource: "vscode-webview:", asWebviewUri: (u: any) => u };
    const fakeExtensionUri: any = { fsPath: path.resolve(__dirname, "../..") };
    const html = getWebviewHtml(fakeWebview, fakeExtensionUri);

    const scriptBlocks = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
    expect(scriptBlocks.length).toBe(2); // vendored marked.min.js, then this extension's own script

    // Run marked's own source (script block 0) in isolation to get a real `marked` — mirrors how
    // the webview loads it (a global, no module system) rather than importing the npm package,
    // which would test a possibly-different version than what's actually vendored on disk.
    const sandbox: any = {};
    // eslint-disable-next-line no-new-func
    new Function("globalThis", scriptBlocks[0])(sandbox);
    markedGlobal = sandbox.marked;
    expect(markedGlobal).toBeDefined();

    // The extension's own script (block 1) is an IIFE that calls acquireVsCodeApi() and touches
    // DOM APIs (document.getElementById etc.) that don't exist in this Node test environment, so
    // it is deliberately NOT executed here — only its *source text* is scanned below to confirm
    // the marked configuration this test suite is meant to guard is actually present in the
    // generated output, not just in intent.
    expect(scriptBlocks[1]).toContain("markedRenderer.link");
    expect(scriptBlocks[1]).toContain("markedRenderer.html");
    expect(scriptBlocks[1]).toContain("marked.setOptions");

    // Copy button (mirrors Litos.Gui's MainWindow.axaml.cs NewAssistantBubbleContent): copies the
    // raw markdown source via the browser clipboard API directly, wired into both the live
    // finalizeAssistantBubble() swap and history replay (renderHistory) so it can't drift out of
    // sync between the two construction sites.
    expect(scriptBlocks[1]).toContain("function addCopyButton");
    expect(scriptBlocks[1]).toContain("navigator.clipboard.writeText");
    expect(scriptBlocks[1]).toContain("addCopyButton(currentAssistantBubble.el, currentAssistantBubble.rawText)");
    expect(scriptBlocks[1]).toContain("addCopyButton(bubble.el, entry.text)");
});

describe("marked (vendored) is present and MIT-licensed", () => {
    it("media/marked.min.js exists and identifies itself as marked, MIT licensed", () => {
        const src = fs.readFileSync(path.resolve(__dirname, "../../media/marked.min.js"), "utf8");
        expect(src.slice(0, 200)).toMatch(/marked v\d+\.\d+\.\d+/);
        expect(src.slice(0, 200)).toMatch(/MIT Licensed/);
    });
});

// The remaining tests reconstruct the exact renderer configuration webviewContent.ts's own script
// defines (link -> data-share-link span, html -> escaped text) against the real vendored `marked`
// extracted above, to verify the *behavior* those overrides are meant to produce — same content,
// re-created here rather than eval'd from the DOM-dependent extension script (see beforeAll).
function configureMarked() {
    function escapeHtml(text: string): string {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
    }
    const renderer = new markedGlobal.Renderer();
    renderer.link = function (href: string, _title: string, text: string) {
        return '<span class="share-link" data-share-link="' + href + '">' + text + "</span>";
    };
    renderer.html = function (htmlText: string) {
        return escapeHtml(htmlText);
    };
    markedGlobal.setOptions({ renderer, headerIds: false, mangle: false });
}

describe("Markdown rendering behavior", () => {
    beforeAll(() => configureMarked());

    it("renders headers, bold, and lists as real block HTML", () => {
        const out = markedGlobal.parse("## Heading\n\nSome **bold** text.\n\n- one\n- two", { async: false });
        expect(out).toContain("<h2>Heading</h2>");
        expect(out).toContain("<strong>bold</strong>");
        expect(out).toContain("<li>one</li>");
        expect(out).toContain("<li>two</li>");
    });

    it("renders a bare share_file URL as a data-share-link span, not a real anchor", () => {
        const out = markedGlobal.parse("Shared file.txt: http://127.0.0.1:54455/files/abc123", { async: false });
        expect(out).toContain('<span class="share-link" data-share-link="http://127.0.0.1:54455/files/abc123">');
        expect(out).not.toContain("<a href");
    });

    it("renders a Markdown [label](url) share_file link as a data-share-link span with clean label text", () => {
        const out = markedGlobal.parse("[Download README.md](http://127.0.0.1:54455/files/cfcf9ab1e19e4fc6)", { async: false });
        expect(out).toContain('data-share-link="http://127.0.0.1:54455/files/cfcf9ab1e19e4fc6"');
        expect(out).toContain(">Download README.md<");
        expect(out).not.toContain("[Download README.md]"); // no leaked bracket syntax
        expect(out).not.toContain("<a href");
    });

    it("escapes raw HTML in model output instead of passing it through", () => {
        const out = markedGlobal.parse('<img src=x onerror="alert(1)">', { async: false });
        expect(out).not.toContain("<img");
        expect(out).toContain("&lt;img");
    });
});
