# Litos Chat Example (Blazor Server)

A minimal Blazor Server chat client for [`Litos.Api`](../../) — shows how a completely external
app talks to a running Litos.Api container over plain HTTP: send text, attach files, and stream
the agent's response (including tool-call activity) back into the page.

This app has **no project reference** to `Litos.Api`/`Litos.Agent`. It calls the container purely
over HTTP, the same way any third-party app would, per
[ReadMe_AgentDesign.md §10.3](../../../../ReadMe_AgentDesign.md).

> This project is intentionally not part of `LitosAiAgent.slnx` / root `dotnet build`/`dotnet test`
> — it's a standalone sample meant to be run on its own, against a Litos.Api instance you already
> have running.

## ⚠️ Before you point this at a real workspace

Turns started through `Litos.Api`'s HTTP endpoint run under **auto-approval** — any tool call the
agent makes (shell commands, file writes, etc., if such tools are registered) executes
immediately, with no human-in-the-loop confirmation step. There is currently no approval UI for
this path (approvals only exist for the Telegram bridge and MCP "Ask"-mode tools). Only run this
example against a container whose mounted `/workspace` you're comfortable letting the agent act on
unsupervised.

## 1. Run Litos.Api

If you don't already have a Litos.Api container running:

```bash
# From the repository root
docker build -f src/Litos.Api/Dockerfile -t litos-api .

cp src/Litos.Api/.env.example src/Litos.Api/.env   # fill in a provider API key + ADMIN_TOKEN

docker run --env-file src/Litos.Api/.env -p 127.0.0.1:8080:8080 \
  -v ~/litos-workspace:/workspace -v ~/.litos-docker:/data litos-api:dev
```

See the root [README.md](../../../../README.md#run-litosapi-http-api-host) and
[ReadMe_HeadlessServiceTool.md](../../../../ReadMe_HeadlessServiceTool.md) for the full picture
(non-Docker `dotnet run`, volume/env details, security notes).

## 2. Configure this app

Set `LitosApi:BaseUrl` and `LitosApi:AdminToken` to match the container from step 1. The token
must equal the `ADMIN_TOKEN` in `src/Litos.Api/.env` — don't commit a real token to
`appsettings.json`. Preferred: user-secrets or an environment variable.

```bash
cd src/Litos.Api/examples/BlazorChat
dotnet user-secrets init
dotnet user-secrets set "LitosApi:AdminToken" "<your ADMIN_TOKEN>"
```

or via environment variable:

```bash
export LitosApi__AdminToken=<your ADMIN_TOKEN>
export LitosApi__BaseUrl=http://localhost:8080   # default; only needed if your container differs
```

## 3. Run

> **Use `dotnet publish` + run the DLL, not plain `dotnet run`.** This app's framework-provided
> static assets (`_framework/blazor.web.js`, the script that opens the SignalR circuit powering
> every interactive control) only exist as physical files after a publish — running straight from
> `dotnet build`/`dotnet run` output 404s that script, which leaves the whole page permanently
> non-interactive (bound inputs and buttons never update). `Litos.Api`'s own Dockerfile publishes
> for the same reason.

```bash
dotnet publish src/Litos.Api/examples/BlazorChat -c Release -o out/blazorchat
dotnet out/blazorchat/Litos.Examples.BlazorChat.dll
```

Open the URL the app prints (typically `http://localhost:5000`, or set `ASPNETCORE_URLS` to
choose one) and start chatting. Each
browser session gets its own randomly generated conversation id, sent as the `{id}` segment of
`POST /sessions/{id}/turns` — Litos.Api has no server-issued session concept, so the id is entirely
this app's choice. Chat history lives only in that page's server-side circuit state; refreshing
the page starts a new conversation.

## What this example demonstrates

- **`LitosClient/LitosApiClient.cs`** — posts JSON (text-only) or `multipart/form-data`
  (text + attachments) to `POST /sessions/{id}/turns`, and branches on the response: a fresh SSE
  stream (`Content-Type: text/event-stream`) when a new turn starts, or a `202 Accepted`
  acknowledgment when the message was steered into or queued behind an already-running turn.
- **`LitosClient/AgentEventStreamParser.cs`** — a small hand-rolled SSE reader, since .NET has no
  built-in client-side SSE parser.
- **`LitosClient/AgentEventDto.cs`** — mirrors the JSON shape of `Litos.Agent`'s `AgentEvent`
  hierarchy without referencing it; the wire format carries no type discriminator, so each event is
  classified by which properties are present.
- **`Components/Pages/Chat.razor`** — the chat UI: a text box, a multi-file picker (mirroring the
  server's 5-file / 20MB-per-file limits so bad uploads are rejected client-side before they hit
  the network), and a transcript that renders assistant text and tool-call activity
  (`ToolCallStarted` → `ToolCallResult`/`ToolCallSkipped`) as they stream in.

## What it doesn't cover

- **Multi-user isolation.** Every direct HTTP caller shares one owner namespace
  (`SessionOwner.Local`) server-side — this example avoids collisions only by picking a random
  session id per circuit. Two people running two copies of this app against the same container get
  independent conversations by luck of the GUID, not because the server tracks caller identity.
- **Persisted history.** There's no `GET` endpoint to fetch past turns for a session, so a page
  refresh loses the transcript. A production client would need its own storage.
- **Reasoning/thinking display.** `TextDelta` and `ReasoningDelta` share an identical wire shape
  (`{"Text": "..."}`), so this example can't tell them apart and renders both as ordinary assistant
  text.
