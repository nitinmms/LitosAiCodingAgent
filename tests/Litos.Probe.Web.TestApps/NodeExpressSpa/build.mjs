// "Builds" the SPA: copies public/ to dist/, as a bundler would emit it. The server serves only
// dist/, so a missing build is a real, visible failure.
import fs from "node:fs";
fs.rmSync("dist", { recursive: true, force: true });
fs.cpSync("public", "dist", { recursive: true });
console.log("built dist/");
