import * as cp from "child_process";
import * as path from "path";
import * as os from "os";
import * as fs from "fs";

/**
 * Spawns the bundled Litos.VsCodeHost binary and resolves once it reports the loopback port it
 * bound. Litos.VsCodeHost.csproj publishes self-contained per-RID (see that project's remarks);
 * the extension bundles one such publish per platform under bin/<rid>/ and picks the matching one
 * here, so the end user never needs a separate .NET install.
 */
export class LitosHostProcess {
    private child: cp.ChildProcess | undefined;

    async start(extensionPath: string, cwd: string): Promise<{ port: number }> {
        const binaryPath = resolveBinaryPath(extensionPath);
        if (!fs.existsSync(binaryPath)) {
            throw new Error(
                `Litos host binary not found for this platform at ${binaryPath}. ` +
                    "This build of the extension may not include a binary for your OS/architecture.",
            );
        }

        // dotnet publish sets the executable bit locally, but packaging into a .vsix (a zip) and
        // re-extracting on install can lose it — a known zip/vsce gotcha, not guaranteed by the
        // packaging step alone. Defensively re-assert it on every start rather than relying on
        // that being preserved end-to-end; a no-op if it's already set. Windows has no such bit.
        if (process.platform !== "win32") {
            try {
                fs.chmodSync(binaryPath, 0o755);
            } catch {
                // Fall through to spawn — if this genuinely can't be made executable, the spawn
                // below fails with a clearer, OS-native error than swallowing it here would give.
            }
        }

        this.child = cp.spawn(binaryPath, [], { cwd, stdio: ["ignore", "pipe", "pipe"] });

        const port = await new Promise<number>((resolve, reject) => {
            let buffer = "";
            const onStdout = (chunk: Buffer) => {
                buffer += chunk.toString("utf8");
                let newlineIndex: number;
                // Kestrel's own startup logs ("Now listening on...", "Application started...")
                // also go to stdout ahead of the handshake line, so each line is tried as JSON
                // rather than assuming the handshake is the first line written.
                while ((newlineIndex = buffer.indexOf("\n")) >= 0) {
                    const line = buffer.slice(0, newlineIndex).trim();
                    buffer = buffer.slice(newlineIndex + 1);
                    const port = tryParsePort(line);
                    if (port !== undefined) {
                        this.child?.stdout?.off("data", onStdout);
                        resolve(port);
                        return;
                    }
                }
            };

            this.child!.stdout!.on("data", onStdout);
            this.child!.once("error", reject);
            this.child!.once("exit", (code) => reject(new Error(`Litos host exited early (code ${code}) before reporting a port.`)));
        });

        return { port };
    }

    stop(): void {
        // v1: no graceful-shutdown handshake with the host process — just terminate it directly.
        this.child?.kill();
        this.child = undefined;
    }
}

function tryParsePort(line: string): number | undefined {
    if (!line.startsWith("{")) return undefined;
    try {
        const parsed = JSON.parse(line) as { port?: number };
        return typeof parsed.port === "number" ? parsed.port : undefined;
    } catch {
        return undefined;
    }
}

function resolveBinaryPath(extensionPath: string): string {
    const rid = resolveRid();
    const exeName = process.platform === "win32" ? "Litos.VsCodeHost.exe" : "Litos.VsCodeHost";
    return path.join(extensionPath, "bin", rid, exeName);
}

function resolveRid(): string {
    const platform = process.platform;
    const arch = os.arch();

    if (platform === "win32") return "win-x64";
    if (platform === "darwin") return arch === "arm64" ? "osx-arm64" : "osx-x64";
    if (platform === "linux") return arch === "arm64" ? "linux-arm64" : "linux-x64";

    throw new Error(`Unsupported platform for Litos: ${platform}/${arch}`);
}
