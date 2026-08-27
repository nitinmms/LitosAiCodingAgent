import { describe, it, expect, beforeAll } from "vitest";
import { getMcpPanelHtml } from "../mcpPanelContent";

// Same extraction approach as webviewComposer.test.ts's own comment explains: getMcpPanelHtml()
// generates a DOM-dependent <script> (acquireVsCodeApi, document.getElementById), so these tests
// scan the real generated source text and syntax-validate the inner script via `new Function`
// rather than executing it, which is the only reliable way to catch the outer/inner
// template-literal escaping bugs this pattern is prone to.

let html: string;
let innerScript: string;

beforeAll(() => {
    const fakeWebview: any = { cspSource: "vscode-webview:", asWebviewUri: (u: any) => u };
    const fakeExtensionUri: any = { fsPath: "/fake" };
    html = getMcpPanelHtml(fakeWebview, fakeExtensionUri);
    const scriptMatch = html.match(/<script>([\s\S]*?)<\/script>/);
    innerScript = scriptMatch![1];

    // eslint-disable-next-line no-new-func
    expect(() => new Function(innerScript)).not.toThrow();
});

describe("MCP panel — default permission (editable per-server)", () => {
    it("still has the add-form's permission dropdown with Deny/Ask/Full in enum order", () => {
        expect(html).toContain('<select id="f-permission">');
        const optionsMatch = html.match(/<select id="f-permission">([\s\S]*?)<\/select>/);
        expect(optionsMatch![1]).toBe(
            '<option value="0">Deny</option><option value="1">Ask</option><option value="2">Full</option>',
        );
    });

    it("renders a per-server permission <select>, not just static text", () => {
        expect(innerScript).toContain("const permissionSelect = document.createElement('select')");
        expect(innerScript).toContain("['Deny', 'Ask', 'Full'].forEach");
    });

    it("preselects the option matching the server's current defaultPermission", () => {
        expect(innerScript).toContain("if (value === server.defaultPermission) option.selected = true;");
    });

    it("posts a setDefaultPermission message with the server name and numeric permission on change", () => {
        expect(innerScript).toContain(
            "vscode.postMessage({ type: 'setDefaultPermission', name: server.name, defaultPermission: Number(permissionSelect.value) });",
        );
    });

    it("no longer prints the permission as static, non-editable detail text", () => {
        // Regression guard: the old implementation appended "— permission: X" straight into the
        // read-only .server-detail div, which is exactly the dead-end this feature replaces.
        expect(innerScript).not.toMatch(/permission:\s*['"]\s*\+/);
    });
});
