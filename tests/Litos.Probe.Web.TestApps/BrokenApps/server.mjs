// One small web app with many ways to be broken, for Litos.Probe.Web's integration tests.
// Each folder beside this file is one broken app: its litos.test.yaml runs
// `node ../server.mjs <mode>`, and the Gate must fail it with the right message.
// No dependencies: node:http only.
import http from "node:http";

const mode = process.argv[2] ?? "healthy";
const port = Number(process.env.PORT ?? 3000);
const password = process.env.APP_PASSWORD ?? "correct-horse";
const production = process.env.NODE_ENV === "production";
let sometimesCalls = 0;

const page = (title, body, script = "") => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>${title}</title></head>
<body><main><h1>${title}</h1>${body}</main>${script ? `<script>${script}</script>` : ""}</body></html>`;

const loginForm = (labelled) => labelled
  ? `<form method="post" action="/login">
       <label for="user">Email</label><input id="user" name="user">
       <label for="pass">Password</label><input id="pass" name="pass" type="password">
       <button type="submit">Log in</button></form>`
  : `<form method="post" action="/login">
       <input name="user" placeholder="you@example.com"><input name="pass" type="password">
       <button type="submit">Go</button></form>`;

function send(res, status, html, headers = {}) {
  res.writeHead(status, { "content-type": "text/html; charset=utf-8", ...headers });
  res.end(html);
}

function role(req) {
  const match = /(?:^|;\s*)sid=(\w+)/.exec(req.headers.cookie ?? "");
  return match ? match[1] : null;
}

function readBody(req) {
  return new Promise((resolve) => {
    let body = "";
    req.on("data", (chunk) => (body += chunk));
    req.on("end", () => resolve(new URLSearchParams(body)));
  });
}

// wrong-port ignores PORT, as an app with a hard-coded port does.
const listenOn = mode === "wrong-port" ? 8787 : port;

if (mode === "crash-on-start") {
  console.error("Error: Cannot find module './config.json'");
  process.exit(3);
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://localhost");
  const path = url.pathname;

  if (mode === "https-redirect")
    return send(res, 302, "", { location: `https://localhost:${port}${req.url}` });
  if (mode === "root-500" && path === "/")
    return send(res, 500, page("Error", "<p>Something went wrong.</p>"));

  // slow-reset's reset endpoint takes longer than the engine's 10s HTTP timeout.
  if (mode === "slow-reset" && path === "/__test/reset") {
    await new Promise((resolve) => setTimeout(resolve, 12_000));
    return send(res, 200, "ok");
  }

  // Test-only endpoints: registered only outside Production, except in the mode that forgets to.
  if (path.startsWith("/__test/") && req.method === "POST" && (!production || mode === "test-endpoint-in-production"))
    return send(res, 200, "ok");

  if (path === "/login" && req.method === "GET")
    return send(res, 200, page("Log in", loginForm(mode !== "unlabelled-login")));
  if (path === "/login" && req.method === "POST") {
    const form = await readBody(req);
    if (form.get("pass") !== password)
      return send(res, 200, page("Log in", `<p role="alert">Invalid login attempt.</p>${loginForm(true)}`));
    return send(res, 302, "", { location: "/", "set-cookie": `sid=${form.get("user") === "admin@test" ? "admin" : "user"}; Path=/; HttpOnly` });
  }

  if (path === "/admin") {
    const who = role(req);
    if (!who && mode !== "not-protected")
      return send(res, 302, "", { location: "/login?returnUrl=/admin" });
    if (who === "user" && mode !== "denied-opened")
      return send(res, 403, page("Access denied", "<p>You can't open this page.</p>"));
    return send(res, 200, page("Admin", "<p>Users and settings.</p>"));
  }

  if (path === "/api/data") {
    res.writeHead(mode === "api-500" ? 500 : 200, { "content-type": "application/json" });
    return res.end(mode === "api-500" ? '{"error":"boom"}' : '{"items":[1,2,3]}');
  }

  if (path === "/sometimes") {
    sometimesCalls++;
    return send(res, sometimesCalls === 1 ? 500 : 200, page("Sometimes", "<p>Fine on the second try.</p>"));
  }

  if (path === "/") {
    if (mode === "server-log-error")
      console.error("ERROR: could not load the dashboard widgets");
    const body = {
      "page-error": `<p>Home</p>`,
      "api-500": `<p id="data">Loading…</p>`,
      "unnamed-button": `<p>Home</p><button class="icon-only"><svg width="16" height="16"></svg></button>`,
      "error-marker": `<p>Home</p><div id="error-ui">An error has occurred. Reload</div>`,
    }[mode] ?? `<p>Home</p><a href="/admin">Admin</a>`;
    const script = {
      "page-error": "window.addEventListener('load', () => { null.boom(); });",
      "api-500": "fetch('/api/data').then(r => r.json()).then(d => { document.getElementById('data').textContent = JSON.stringify(d); });",
    }[mode] ?? "";
    return send(res, 200, page("Home", body, script));
  }

  send(res, 404, page("Not found", "<p>There is no such page.</p>"));
});

server.listen(listenOn, "127.0.0.1", () => console.log(`broken app (${mode}) on http://localhost:${listenOn}`));
