# Litos AI Agent

**[litosai.dev](https://litosai.dev/)**

A minimal, transparent AI coding agent written in pure .NET. Multi-provider (Anthropic,
OpenAI, Google Gemini, OpenRouter), multi-face (console, desktop GUI, and an HTTP/API host with
web UI), with tool use (file edit, shell, web search) and MCP client support.

Built to learn how a coding agent actually works, from the inside, in C#/.NET — the stack I've
worked in for 23+ years — rather than treating one as a black box.

Primarily inspired by Mario Zechner's [pi](https://pi.dev), particularly its session-as-working-
directory model, and secondarily by [alejandro-ao](https://github.com/alejandro-ao)'s
[Tau](https://github.com/alejandro-ao/tau) and its "separate the brain, the environment, and the
face" architecture.

## Install `Litos.Gui`

`Litos.Gui` is the default desktop face of Litos — a .NET AI coding agent (like Claude Code, but
built from scratch in C#), with a chat window, file/shell/web tools, and MCP support.

**Windows** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\Programs\Litos`, adds it to your PATH, and creates a Start Menu
shortcut. No admin rights needed. The build is unsigned, so SmartScreen may warn on first launch
— click **More info → Run anyway**.

**macOS** (Terminal):

```bash
curl -fsSL https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install.sh | bash
```

Installs `Litos.app` to `/Applications` (Apple Silicon and Intel both supported). This build is
signed and notarized, so Gatekeeper won't block it.

Re-running either command upgrades an existing install in place. On first launch, if no provider
API key is found, Litos opens a dialog to enter one — see
[Environment variables](#environment-variables) below. Keys can be added or changed later from
inside the app with `/keys`.

## Videos

- [Built an AI Coding Agent in Pure C#/.NET (No Python!) — Watch It Build a WinForms 15-Puzzle Game](https://www.youtube.com/watch?v=em8w0SwgT5Q)
- [Built an AI Agent in Pure C#/.NET— Here it is working through Telegram creating Mermaid Diagrams](https://www.youtube.com/watch?v=S2Sn_kwCRjE&t=8s)
- [Litos Coding Agent Building Features into an Existing Postgres Query Application](https://www.youtube.com/watch?v=j-Gx-7LZaso)

## Slash commands

Slash commands are typed directly into the chat input. Each face (`Litos.Console`, `Litos.Gui`,
and `Litos.Api`'s Telegram bridge) implements its own command dispatch, so support varies:

| Command | Description | Console | Gui | Api (Telegram) |
|---|---|---|---|---|
| `/new` | Start a new session | Yes | Yes | Yes |
| `/resume [id]` | Resume a previous session (picker if no id given) | Yes | Yes | Yes |
| `/attach <path\|url>` | Attach a file, folder, or URL to the conversation | Yes | Yes | No |
| `/provider [name]` | Switch the active LLM provider | Yes | Yes | No |
| `/model [id]` | Switch the active model | Yes | Yes | No |
| `/skills` | List available skills | Yes | Yes | Yes |
| `/skill <name>` | Load a skill by name | No | Yes | Yes |
| `/branch <msgIndex>` | Rewind the session to an earlier message | Yes | Yes | Yes |
| `/compact` | Force-summarize/compact the conversation to free context | Yes | Yes | Yes |
| `/export <path>` | Export the transcript to a file | Yes | No | No |
| `/reflect` | Distill the session into an `AGENTS.md` | No | Yes | No |
| `/mcp` | Manage MCP servers (add/browse/run prompts) | No | Yes | No |
| `/keys` | Add or update provider API keys | No | Yes | No |

`Litos.Gui` also supports dynamic `/mcp__<server>__<prompt>` commands for running prompts exposed
by a connected MCP server.

## Status

Two faces are working end to end today:

- **`Litos.Gui`** — the Avalonia desktop app. Verified working on **Windows**; not yet tested on
  macOS. Includes MCP server support (add/manage servers, browse their tools, run their prompts).
- **`Litos.Api`** — the HTTP/API host with web UI, Docker support, a working **Telegram** bridge,
  and MCP server support.

`Litos.Console` and the WhatsApp/Rocket.Chat/email channel bridges are a **work in progress** —
those three bridges currently exist only as design docs, not shipped features.

Of the four LLM providers, only **OpenAI** and **OpenRouter** have been explicitly exercised
end-to-end. Anthropic and Google Gemini adapters exist and should work, but are unconfirmed.

## Getting started

Already installed `Litos.Gui` via the one-line command above? Just launch it. The rest of this
section is for building from source or running the other faces (`Litos.Api`, `Litos.Console`).

### Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/nitinmms/LitosAiCodingAgent.git
cd LitosAiCodingAgent
dotnet build
```

### Run `Litos.Gui` (desktop app)

```bash
dotnet run --project src/Litos.Gui
```

### Run `Litos.Api` (HTTP/API host)

```bash
cp src/Litos.Api/.env.example src/Litos.Api/.env   # fill in your provider API key(s)
dotnet run --project src/Litos.Api
```

Then open `http://localhost:8080` and sign in with the `ADMIN_TOKEN` you set in `.env`. For
Docker/Telegram deployment, see [ReadMe_HeadlessServiceTool.md](ReadMe_HeadlessServiceTool.md) and
[ReadMe_TelegramIntegrationTool.md](ReadMe_TelegramIntegrationTool.md).

## Environment variables

At least one LLM provider API key is required. Configure these via the `.env` file (`Litos.Api`,
copied from [src/Litos.Api/.env.example](src/Litos.Api/.env.example)) or, for `Litos.Gui`, the
first-run dialog or `/keys` command (writes a Windows user environment variable on Windows, or
`~/.litos/config.json` on macOS). An env var always wins over any value stored in `Litos.Api`'s
mounted config file or `Litos.Gui`'s config.json.

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | Anthropic (Claude) provider |
| `OPENAI_API_KEY` | OpenAI provider |
| `GEMINI_API_KEY` (or `GOOGLE_API_KEY` as fallback) | Google Gemini provider |
| `OPENROUTER_API_KEY` | OpenRouter provider |
| `TAVILY_API_KEY` | Web search tool (optional — not a chat provider) |
| `ADMIN_TOKEN` | Login password for `Litos.Api`'s admin UI (Bearer/cookie) |
| `TELEGRAM_BOT_TOKEN` | Bot token from [@BotFather](https://t.me/BotFather); enables the Telegram bridge (also settable from the `/telegram` admin page — the env var always wins). Off by default even when set. |

## Tests

```bash
dotnet test
```

## Further documentation

- [ReadMe_Architecture.md](ReadMe_Architecture.md) — brain/environment/face layering, project list, and how context is assembled each turn
- [ReadMe_AgentDesign.md](ReadMe_AgentDesign.md) — full architecture and design rationale
- [ReadMe_Extensibility.md](ReadMe_Extensibility.md) — adding providers, tools, and faces
- [ReadMe_HeadlessServiceTool.md](ReadMe_HeadlessServiceTool.md) — running `Litos.Api` as a headless/Docker service, admin UI
- [ReadMe_TelegramIntegrationTool.md](ReadMe_TelegramIntegrationTool.md) — Telegram bridge (`Litos.Api`)
- [ReadMe_LitosApi_Mcp.md](ReadMe_LitosApi_Mcp.md) — MCP server integration in `Litos.Api`
- [ReadMe_MCPSupportInLitosGUI.md](ReadMe_MCPSupportInLitosGUI.md) — MCP server integration in `Litos.Gui`
- [ReadMe_DeployToAWS.md](ReadMe_DeployToAWS.md) — AWS deployment guide

Design docs only — no implementation exists yet, read as proposals:

- [ReadMe_WhatsAppIntegrationTool.md](ReadMe_WhatsAppIntegrationTool.md) — WhatsApp bridge
- [ReadMe_RocketChatTool.md](ReadMe_RocketChatTool.md) — Rocket.Chat bridge
- [ReadMe_EmailIntegrationTool.md](ReadMe_EmailIntegrationTool.md) — Email integration

## License

Apache License 2.0 — see [LICENSE.txt](LICENSE.txt). Third-party dependency licenses are listed
in [NOTICE](NOTICE).
