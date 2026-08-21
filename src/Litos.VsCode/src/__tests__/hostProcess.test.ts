import { describe, it, expect, afterEach } from "vitest";
import * as fs from "fs";
import * as path from "path";
import * as os from "os";
import { LitosHostProcess } from "../hostProcess";

// tryParsePort/resolveRid/resolveBinaryPath are private to hostProcess.ts by design (no reason to
// widen the module's public surface just for testability) — so these tests exercise
// LitosHostProcess.start()'s real, observable behavior end-to-end by spawning a stand-in for the
// .NET host through a real bin/<rid>/ layout on disk. This also covers what actually matters here
// (the "scan every line, not just line 1" behavior) more faithfully than unit-testing
// tryParsePort in isolation would.
//
// The stand-in "binary" is a *copy* of the running Node executable itself, not a batch/shell
// wrapper: cp.spawn (as hostProcess.ts calls it, no `shell: true`) needs a real, directly
// executable file — Windows in particular refuses to spawn a .cmd/.bat script merely renamed to
// .exe (confirmed: throws "spawn UNKNOWN", since Windows resolves executability from the PE
// header, not the extension). A copied node.exe run with zero CLI args would normally drop into
// the REPL, so NODE_OPTIONS="--require <script>" is used to run the fake host's script before
// that happens — this works identically on every platform and needs no shebang/chmod handling.
function ridForThisPlatform(): string {
    if (process.platform === "win32") return "win-x64";
    if (process.platform === "darwin") return process.arch === "arm64" ? "osx-arm64" : "osx-x64";
    return process.arch === "arm64" ? "linux-arm64" : "linux-x64";
}

function makeFakeExtension(script: string): { extensionPath: string; cleanup: () => void } {
    const extensionPath = fs.mkdtempSync(path.join(os.tmpdir(), "litos-hostprocess-test-"));
    const rid = ridForThisPlatform();
    const binDir = path.join(extensionPath, "bin", rid);
    fs.mkdirSync(binDir, { recursive: true });

    const scriptPath = path.join(binDir, "fake-host.js");
    fs.writeFileSync(scriptPath, script);

    const exeName = process.platform === "win32" ? "Litos.VsCodeHost.exe" : "Litos.VsCodeHost";
    const exePath = path.join(binDir, exeName);
    fs.copyFileSync(process.execPath, exePath);
    if (process.platform !== "win32") fs.chmodSync(exePath, 0o755);

    // cp.spawn in hostProcess.ts passes no explicit `env`, so the child inherits this test
    // process's environment (Node's default) — setting NODE_OPTIONS here is what makes the copied
    // node.exe run fake-host.js instead of dropping into a REPL when spawned with zero CLI args.
    const previousNodeOptions = process.env.NODE_OPTIONS;
    process.env.NODE_OPTIONS = `--require ${JSON.stringify(scriptPath)}`;

    return {
        extensionPath,
        cleanup: () => {
            process.env.NODE_OPTIONS = previousNodeOptions;
            // maxRetries/retryDelay: on Windows, child.kill() (called by the test's own
            // host.stop() in afterEach, just before this runs) returns before the OS has
            // necessarily released its handle on the copied .exe — an immediate unlink can throw
            // EPERM/EBUSY as a result. fs.rmSync's built-in retry covers that race without a
            // manual delay/poll here.
            fs.rmSync(extensionPath, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
        },
    };
}

describe("LitosHostProcess.start", () => {
    let cleanup: (() => void) | undefined;
    let host: LitosHostProcess | undefined;

    afterEach(() => {
        host?.stop();
        cleanup?.();
        host = undefined;
        cleanup = undefined;
    });

    it("resolves the port from the first line that parses as {\"port\":N}", async () => {
        const fake = makeFakeExtension(`console.log(JSON.stringify({ port: 54321 }));\nsetTimeout(() => {}, 10000);\n`);
        cleanup = fake.cleanup;
        host = new LitosHostProcess();

        const result = await host.start(fake.extensionPath, process.cwd());

        expect(result.port).toBe(54321);
    });

    it("skips non-JSON lines (Kestrel startup logs) ahead of the handshake line", async () => {
        const fake = makeFakeExtension(
            [
                "console.log('info: Microsoft.Hosting.Lifetime[14]');",
                "console.log('      Now listening on: http://127.0.0.1:54321');",
                "console.log('info: Microsoft.Hosting.Lifetime[0]');",
                "console.log('      Application started. Press Ctrl+C to shut down.');",
                "console.log(JSON.stringify({ port: 54321 }));",
                "setTimeout(() => {}, 10000);",
            ].join("\n"),
        );
        cleanup = fake.cleanup;
        host = new LitosHostProcess();

        const result = await host.start(fake.extensionPath, process.cwd());

        expect(result.port).toBe(54321);
    });

    it("ignores a JSON line with no numeric port field before finding the real handshake", async () => {
        const fake = makeFakeExtension(
            [
                "console.log(JSON.stringify({ status: 'starting' }));",
                "console.log(JSON.stringify({ port: 9999 }));",
                "setTimeout(() => {}, 10000);",
            ].join("\n"),
        );
        cleanup = fake.cleanup;
        host = new LitosHostProcess();

        const result = await host.start(fake.extensionPath, process.cwd());

        expect(result.port).toBe(9999);
    });

    it("rejects if the process exits before ever reporting a port", async () => {
        const fake = makeFakeExtension(`console.log('no handshake here');\nprocess.exit(1);\n`);
        cleanup = fake.cleanup;
        host = new LitosHostProcess();

        await expect(host.start(fake.extensionPath, process.cwd())).rejects.toThrow(/exited early/);
    });

    it("merges extraEnv into the spawned process's environment, on top of the inherited one", async () => {
        // start() only resolves { port }, not anything about the child's own env — so the fake
        // host instead writes what it saw to a file the test can read once the process is up,
        // rather than trying to smuggle it through the handshake line's shape.
        const sawEnvPath = path.join(os.tmpdir(), `litos-hostprocess-saw-env-${process.pid}-${Date.now()}.json`);
        const fake = makeFakeExtension(
            [
                `const fs = require('fs');`,
                `fs.writeFileSync(${JSON.stringify(sawEnvPath)}, JSON.stringify({ key: process.env.OPENROUTER_API_KEY || null }));`,
                "console.log(JSON.stringify({ port: 54321 }));",
                "setTimeout(() => {}, 10000);",
            ].join("\n"),
        );
        cleanup = fake.cleanup;
        host = new LitosHostProcess();

        try {
            const result = await host.start(fake.extensionPath, process.cwd(), { OPENROUTER_API_KEY: "sk-or-test-123" });

            expect(result.port).toBe(54321);
            const seen = JSON.parse(fs.readFileSync(sawEnvPath, "utf8"));
            expect(seen.key).toBe("sk-or-test-123");
        } finally {
            fs.rmSync(sawEnvPath, { force: true });
        }
    });

    it("throws a clear error when no binary exists for this platform", async () => {
        const extensionPath = fs.mkdtempSync(path.join(os.tmpdir(), "litos-hostprocess-test-empty-"));
        try {
            host = new LitosHostProcess();
            await expect(host.start(extensionPath, process.cwd())).rejects.toThrow(/binary not found/);
        } finally {
            fs.rmSync(extensionPath, { recursive: true, force: true });
        }
    });
});
