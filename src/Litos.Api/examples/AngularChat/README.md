# Litos Chat Example (AngularJS, static HTML)

A buildless, plain HTML + AngularJS chat client for [`Litos.Api`](../../) — like
[`../BlazorChat`](../BlazorChat), it's a completely external app that talks to a running
Litos.Api container over plain HTTP, but with **no server-side component at all**: this is
`index.html` + `app.js` + `style.css`, served by any static file server, calling Litos.Api
directly from the browser.

This app has **no build step and no project reference** to `Litos.Api`/`Litos.Agent`. It calls
the container purely over HTTP, from client-side JavaScript, per
[ReadMe_AgentDesign.md §10.3](../../../../ReadMe_AgentDesign.md).

> This folder is intentionally not part of `LitosAiAgent.slnx` / root `dotnet build`/`dotnet
> test` — there's nothing to build. It's a standalone sample meant to be run on its own, against
> a Litos.Api instance you already have running.

## ⚠️ Before you point this at a real workspace

Turns started through `Litos.Api`'s HTTP endpoint run under **auto-approval** — any tool call the
agent makes (shell commands, file writes, etc., if such tools are registered) executes
immediately, with no human-in-the-loop confirmation step. There is currently no approval UI for
this path (approvals only exist for the Telegram bridge and MCP "Ask"-mode tools). Only run this
example against a container whose mounted `/workspace` you're comfortable letting the agent act on
unsupervised.

## ⚠️ How this differs from BlazorChat, security-wise

BlazorChat keeps the signed-in user's JWT pair server-side (an httpOnly cookie on its own app,
never exposed to JS) specifically because it's a real server with a session concept. This example
has no server, so there's nothing to hold that cookie — the JWT access/refresh token pair is kept
in the browser's `sessionStorage` instead (cleared when the tab closes, but readable by any script
running on this page's origin). That's a reasonable trade-off for a local demo/reference client,
but don't reuse this token-storage approach for a production app without thinking through XSS
exposure for your own origin.

## 1. Run Litos.Api

If you don't already have a Litos.Api container running:

```bash
# From the repository root
docker build -f src/Litos.Api/Dockerfile -t litos-api .

cp src/Litos.Api/.env.example src/Litos.Api/.env   # fill in a provider API key + ADMIN_TOKEN + JWT_SIGNING_KEY

docker run --env-file src/Litos.Api/.env -p 127.0.0.1:8080:8080 \
  -v ~/litos-workspace:/workspace -v ~/.litos-docker:/data litos-api:dev
```

See the root [README.md](../../../../README.md#run-litosapi-http-api-host) and
[ReadMe_HeadlessServiceTool.md](../../../../ReadMe_HeadlessServiceTool.md) for the full picture
(non-Docker `dotnet run`, volume/env details, security notes). You'll need a per-user account to
sign into this example (see `POSTGRES_CONNECTION_STRING`/`JWT_SIGNING_KEY` in `.env.example`) —
`ADMIN_TOKEN` alone isn't enough, since this example's login form always posts to `POST
/auth/token` (username/password), not the `ADMIN_TOKEN` bearer scheme.

## 2. Allow this page's origin via CORS

Litos.Api has **no CORS policy by default** — a browser page on a different origin (a different
host, port, or scheme than the API itself) can't call it until you opt in. Add the origin(s)
you'll serve this page from to `CORS_ALLOWED_ORIGINS` in `src/Litos.Api/.env` (comma-separated),
e.g.:

```bash
CORS_ALLOWED_ORIGINS=http://localhost:5500
```

then restart the container. If instead you serve this folder *from Litos.Api itself* (copy it
into `src/Litos.Api/wwwroot/`, served by the API's own `UseStaticFiles()`), it's already
same-origin and no CORS configuration is needed at all — that's the simplest way to try this
example with zero extra steps.

## 3. Serve this folder

Any static file server works — there's nothing to build or publish. For example:

```bash
cd src/Litos.Api/examples/AngularChat
npx serve -l 5500
# or: python -m http.server 5500
```

Open the URL it prints (e.g. `http://localhost:5500`) — no query parameter needed for the
standard local setup. `app.js`'s `apiBaseUrl` constant defaults to `http://localhost:8080`
(Litos.Api's usual local port) whenever this page isn't itself served from Litos.Api's own
origin, so a plain `npx serve`/`python -m http.server` against the default `docker run`/`docker
compose` port just works. If your container listens somewhere else, either edit that fallback
in `app.js` or override it per-visit with a query parameter:

```
http://localhost:5500/?api=http://your-custom-host:1234
```

## 4. Sign in and chat

Each browser tab keeps its own randomly generated conversation id, sent as the `{id}` segment of
`POST /sessions/{id}/turns` — Litos.Api has no server-issued session concept, so the id is
entirely this app's choice, same as BlazorChat. Conversation history for *other* sessions is
fetched from `GET /sessions` / `GET /sessions/{id}/history` (populated across page loads, since
that's server-side state); the composer/transcript for whichever session you're actively viewing
lives only in this page's JS state and is lost on refresh.

## What this example demonstrates

- **`app.js`'s `authService`** — posts form-urlencoded credentials to `POST /auth/token`, stores
  the returned access/refresh token pair in `sessionStorage`, and refreshes via `POST
  /auth/token/refresh`.
- **`app.js`'s `litosApiClient`** — posts JSON (text-only) or `multipart/form-data` (text +
  attachments) to `POST /sessions/{id}/turns` using the Fetch API (not Angular's `$http`, which
  buffers the whole response body — defeating streaming), and branches on the response: a fresh
  SSE stream (`Content-Type: text/event-stream`) when a new turn starts, or a `202 Accepted`
  acknowledgment when the message was steered into or queued behind an already-running turn.
- **`readAgentEvents` (inside `app.js`)** — a small hand-rolled SSE reader over `fetch`'s
  `ReadableStream`, since browsers have no built-in SSE parser that supports POST bodies or custom
  headers (the native `EventSource` API is GET-only and can't attach an `Authorization` header).
- **`parseAgentEvent` (inside `app.js`)** — mirrors the JSON shape of `Litos.Agent`'s `AgentEvent`
  hierarchy; the wire format carries no type discriminator, so each event is classified by which
  properties are present, same as BlazorChat's `AgentEventDto.Parse`.
- **`index.html` + `ChatController`** — the chat UI: a text box, a multi-file picker (mirroring
  the server's 5-file / 20MB-per-file limits so bad uploads are rejected client-side before they
  hit the network), a session sidebar with search, and a transcript that renders assistant text
  and tool-call activity (`ToolCallStarted` → `ToolCallResult`/`ToolCallSkipped`) as they stream
  in.

## What it doesn't cover

- **Multi-user isolation across this app's own users.** Each signed-in user's turns run under
  their own `SessionOwner` server-side (Litos.Api's per-user accounts), same as any other JWT
  client — this example doesn't add anything beyond what the API already enforces.
- **Persisted transcript for the active session.** There's a `GET .../history` endpoint (used when
  you switch to a *previous* session from the sidebar), but the actively-open conversation's
  streamed transcript is only in this page's JS state; a hard refresh re-fetches history from
  scratch for whichever session you land back on.
- **Reasoning/thinking display.** `TextDelta` and `ReasoningDelta` share an identical wire shape
  (`{"Text": "..."}`), so this example can't tell them apart and renders both as ordinary assistant
  text.
- **Older browsers.** Uses `fetch` + `ReadableStream` + `async`/`await`-free async iterators,
  `FormData`, and `URLSearchParams` — all standard in any current evergreen browser, not IE11.
