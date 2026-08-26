import { describe, it, expect, beforeAll } from "vitest";
import * as path from "path";
import { getWebviewHtml } from "../webviewContent";

// Same extraction approach as webviewMarkdown.test.ts's own comment explains: getWebviewHtml()
// has no vscode-free export, and the generated inner <script> is DOM-dependent (acquireVsCodeApi,
// document.getElementById), so these tests scan the real generated source text for the composer
// usability features (ReadMe_VsCodeExtension.md §7.10) rather than executing it. This is also the
// only reliable way to catch a repeat of §7.6's outer/inner template-literal escaping bug — a
// literal '×'-style escape written for the chip's remove icon would need to survive the outer
// template literal intact to remain valid in the inner script, which is exactly the class of bug
// that was invisible from source alone last time.

let html: string;
let innerScript: string;

beforeAll(() => {
    const fakeWebview: any = { cspSource: "vscode-webview:", asWebviewUri: (u: any) => u };
    const fakeExtensionUri: any = { fsPath: path.resolve(__dirname, "../..") };
    html = getWebviewHtml(fakeWebview, fakeExtensionUri);
    const scriptBlocks = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
    innerScript = scriptBlocks[1];

    // The inner script must be syntactically valid on its own — parsing it (without running the
    // DOM-touching IIFE body) is exactly what would have caught §7.6's corrupted-regex bug.
    // eslint-disable-next-line no-new-func
    expect(() => new Function(innerScript)).not.toThrow();
});

describe("attachment thumbnail CSP (ReadMe_VsCodeExtension.md §7.10)", () => {
    // Real bug found live: the page's CSP was `default-src 'none'` with no img-src directive,
    // which silently blocks even data: URIs — the pasted-image thumbnail's <img src="data:..."> is
    // exactly that case. No console-visible error inside the chip itself, no exception either
    // (CSP violations don't throw), just a permanently blank thumbnail — the SVG file-type icons
    // worked fine alongside it because inline <svg> injected via innerHTML isn't a CSP img-src
    // resource load at all, which is what made this easy to miss from source alone.
    it("explicitly allows data: images so attachment thumbnails aren't silently blocked", () => {
        const cspMatch = html.match(/<meta http-equiv="Content-Security-Policy" content="([^"]*)">/);
        expect(cspMatch).toBeTruthy();
        expect(cspMatch![1]).toMatch(/img-src[^;]*data:/);
    });
});

describe("composer auto-grow (ReadMe_VsCodeExtension.md §7.10)", () => {
    it("starts the textarea at 4 rows, not the old 2", () => {
        expect(html).toContain('<textarea id="composerInput" rows="4"');
    });

    it("wires input-driven auto-grow and a reset back to the minimum height", () => {
        expect(innerScript).toContain("function autoGrowComposer");
        expect(innerScript).toContain("function resetComposerHeight");
        expect(innerScript).toContain("inputEl.style.height = inputEl.scrollHeight + 'px'");
    });

    it("resets the height on both send() and selectCommand()", () => {
        const sendFn = innerScript.slice(innerScript.indexOf("function send()"), innerScript.indexOf("sendButton.addEventListener"));
        expect(sendFn).toContain("resetComposerHeight()");
        const selectCommandFn = innerScript.slice(innerScript.indexOf("function selectCommand"), innerScript.indexOf("function autoGrowComposer"));
        expect(selectCommandFn).toContain("resetComposerHeight()");
    });
});

describe("slash-command button (ReadMe_VsCodeExtension.md §7.10)", () => {
    it("renders a dedicated button in the composer row", () => {
        expect(html).toContain('<button id="slashCommandButton"');
    });

    it("clicking it seeds the composer with '/' and opens the existing command menu", () => {
        expect(innerScript).toContain("slashCommandButton.addEventListener('click'");
        const handler = innerScript.slice(
            innerScript.indexOf("slashCommandButton.addEventListener('click'"),
            innerScript.indexOf("inputEl.addEventListener('input'")
        );
        expect(handler).toContain("inputEl.value = '/'");
        expect(handler).toContain("updateCommandMenu()");
    });
});

describe("removable attachment chips (ReadMe_VsCodeExtension.md §7.10)", () => {
    it("gives each chip a remove control that posts removeAttachment with its index", () => {
        expect(innerScript).toContain("chip-remove");
        expect(innerScript).toContain("vscode.postMessage({ type: 'removeAttachment', index })");
    });

    it("uses a literal multiplication-sign character for the remove icon, not an escape sequence prone to §7.6's outer/inner template-literal corruption", () => {
        expect(innerScript).toContain("removeEl.textContent = '×'");
    });

    it("renders an image thumbnail when the extension supplies one, and tolerates chips with none", () => {
        // extension.ts only sends thumbnailDataUri for image attachments (attachmentThumbnail()
        // returns undefined for document attachments, e.g. /skill's loaded skill text) — the
        // webview must render a thumb when present without erroring when it's absent.
        expect(innerScript).toContain("function addAttachmentChip(label, thumbnailDataUri, iconKind)");
        expect(innerScript).toContain("thumb.src = thumbnailDataUri");
        expect(innerScript).toContain("chip-thumb");
        expect(innerScript).toContain("addAttachmentChip(message.fileName, message.thumbnailDataUri, message.iconKind)");
    });

    it("falls back to a generic file icon for an unrecognized iconKind, and has an entry for every extension.ts-classified kind", () => {
        // extension.ts's attachmentIconKind() only ever emits these five values (pdf/word/excel/
        // powerpoint/text) plus its own "generic" fallback for anything unmapped — every one needs
        // a real entry here so ATTACHMENT_ICON_SVG[iconKind] is never undefined for a value the
        // extension can actually send.
        expect(innerScript).toContain("ATTACHMENT_ICON_SVG[iconKind] || ATTACHMENT_ICON_SVG.generic");
        ["pdf", "word", "excel", "powerpoint", "text", "generic"].forEach((kind) => {
            expect(innerScript).toContain(kind + ":");
        });
    });
});

describe("Stop/Cancel affordance for a stuck or long-running turn", () => {
    // Backs the fix for a turn that hangs mid-tool-call (e.g. an MCP CallToolAsync with no
    // response) with the working indicator stuck on forever and no way to recover short of
    // reloading the window. sendButton relabels to Cancel (mirroring Litos.Gui's own
    // SendButton.Content = "Cancel") and posts a 'cancel' message instead of send()'ing.

    it("relabels sendButton to Cancel when a turn starts, and back to Send when it ends", () => {
        expect(innerScript).toContain("function showWorkingIndicator");
        expect(innerScript).toContain("function hideWorkingIndicator");
        const showFn = innerScript.slice(innerScript.indexOf("function showWorkingIndicator"), innerScript.indexOf("function hideWorkingIndicator"));
        expect(showFn).toContain("sendButton.textContent = 'Cancel'");
        const hideFn = innerScript.slice(innerScript.indexOf("function hideWorkingIndicator"), innerScript.indexOf("function hideWorkingIndicator") + 300);
        expect(hideFn).toContain("sendButton.textContent = 'Send'");
    });

    it("clicking sendButton while a turn is in progress posts 'cancel' instead of calling send()", () => {
        const handler = innerScript.slice(
            innerScript.indexOf("sendButton.addEventListener('click'"),
            innerScript.indexOf("slashCommandButton.addEventListener('click'")
        );
        expect(handler).toContain("if (turnInProgress)");
        expect(handler).toContain("vscode.postMessage({ type: 'cancel' })");
        expect(handler).toContain("send();");
    });

    it("typing Enter still steers the in-progress turn — only the button's own click branches on turn state", () => {
        // send() itself has no turnInProgress check — Enter-to-send/steer must behave exactly as
        // it did before the Cancel button existed.
        const sendFn = innerScript.slice(innerScript.indexOf("function send()"), innerScript.indexOf("sendButton.addEventListener"));
        expect(sendFn).not.toContain("turnInProgress");
    });
});

describe("\"@\"-mention file dropdown, mirroring Litos.Gui's MentionPopup", () => {
    it("renders a dedicated dropdown element in the composer, separate from the / command menu", () => {
        expect(html).toContain('<div id="mentionMenu">');
    });

    it("detects an active @token the same way Litos.Gui's FindActiveMentionStart does: requires whitespace or start-of-string immediately before the @", () => {
        expect(innerScript).toContain("function findActiveMentionStart(text, caret)");
        const fn = innerScript.slice(
            innerScript.indexOf("function findActiveMentionStart"),
            innerScript.indexOf("function updateMentionMenu")
        );
        expect(fn).toContain("if (at > 0 && !WHITESPACE.test(text[at - 1])) return -1;");
    });

    it("double-escapes \\s so the outer template literal in getWebviewHtml doesn't silently drop the backslash — a §7.6-class bug this exact change introduced and caught", () => {
        // A bare "\s" in webviewContent.ts's source is embedded inside getWebviewHtml's own outer
        // template literal — an unrecognized template-literal escape, so the backslash is silently
        // dropped at build time, leaving a served regex of /s/ (matches the literal letter "s")
        // instead of /\s/ (matches whitespace). Both are syntactically valid, so only scanning the
        // REAL generated output (via getWebviewHtml(), same as innerScript itself is derived
        // below) can distinguish them — a check against webviewContent.ts's own source text
        // wouldn't catch the template literal having already eaten the backslash.
        expect(innerScript).toContain("const WHITESPACE = /\\s/;");
        expect(innerScript).not.toContain("const WHITESPACE = /s/;");
    });

    it("requests suggestions from the extension host (a real filesystem walk), not an in-memory list like the / menu", () => {
        expect(innerScript).toContain("vscode.postMessage({ type: 'getFileMentions', query: token, requestToken: myToken })");
    });

    it("guards against a stale, out-of-order response clobbering a newer keystroke's request", () => {
        const fn = innerScript.slice(
            innerScript.indexOf("function handleFileMentionsResult"),
            innerScript.indexOf("function renderMentionMenu")
        );
        expect(fn).toContain("if (requestToken !== mentionRequestToken || mentionStart < 0) return;");
    });

    it("splices the accepted path in after the @ without deleting the @ itself", () => {
        const fn = innerScript.slice(innerScript.indexOf("function acceptMention"), innerScript.indexOf("// Auto-grow"));
        expect(fn).toContain("const before = text.slice(0, start + 1); // includes the \"@\"");
        expect(fn).toContain("inputEl.value = before + relativePath + spacer + after;");
    });

    it("wires ArrowUp/ArrowDown/Tab/Enter/Escape for the mention menu, separately from the command menu's own handling", () => {
        const keydownHandler = innerScript.slice(
            innerScript.indexOf("inputEl.addEventListener('keydown'"),
            innerScript.lastIndexOf("if (event.key === 'Enter' && !event.shiftKey)")
        );
        expect(keydownHandler).toContain("mentionMenuEl.classList.contains('visible')");
        expect(keydownHandler).toContain("acceptMention(mentionMenuMatches[mentionMenuActiveIndex])");
    });

    it("closes/refreshes the mention menu on caret-only moves (click), not just on new keystrokes", () => {
        expect(innerScript).toContain("inputEl.addEventListener('click', updateMentionMenu)");
    });

    it("handles the fileMentionsResult response message from the extension host", () => {
        expect(innerScript).toContain("message.type === 'fileMentionsResult'");
        expect(innerScript).toContain("handleFileMentionsResult(message.matches, message.requestToken)");
    });
});

describe("attachment names shown on send (ReadMe_VsCodeExtension.md §7.12)", () => {
    // Mirrors Litos.Gui's own MainWindow.axaml.cs AddUserBubble/BuildBubbleLabel split — a 📎
    // line above the message is the only remaining record of what was attached once the composer's
    // own chips are cleared on send (see addUserEntry's own comment).
    it("wires send() to hand the pending attachment labels off to addUserEntry", () => {
        expect(innerScript).toContain("function addUserEntry(text, attachmentNames)");
        expect(innerScript).toContain("addUserEntry(text, pendingAttachmentLabels.slice())");
    });

    it("snapshots the labels before clearAttachmentChips empties them", () => {
        const sendFn = innerScript.slice(innerScript.indexOf("function send()"), innerScript.indexOf("sendButton.addEventListener"));
        const addCallIndex = sendFn.indexOf("addUserEntry(text, pendingAttachmentLabels.slice())");
        const clearCallIndex = sendFn.indexOf("clearAttachmentChips()");
        expect(addCallIndex).toBeGreaterThan(-1);
        expect(clearCallIndex).toBeGreaterThan(addCallIndex);
    });

    it("renders a 📎-prefixed line and skips it entirely when there are no attachments", () => {
        expect(innerScript).toContain("entry-attachments");
        expect(innerScript).toContain("'📎 ' + attachmentNames.join(', ')");
        expect(innerScript).toContain("if (!attachmentNames || attachmentNames.length === 0) return entry;");
    });
});
