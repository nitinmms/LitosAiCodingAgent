# Litos AI Agent

A minimal, transparent AI coding agent written in pure .NET. Multi-provider (Anthropic,
OpenAI, Google Gemini, OpenRouter), multi-face (console, desktop GUI, and an HTTP/API host with
web UI), with tool use (file edit, shell, web search), MCP client support, and channel
integrations (Telegram, WhatsApp, Rocket.Chat, email).

Inspired by [Tau](https://twotimespi.dev/)'s "separate the brain, the environment, and the
face" architecture, and by [pi](https://github.com/earendil-works/pi)'s session-as-working-
directory model — reimplemented idiomatically in C#/.NET 10.

## Architecture

- **The brain** (`Litos.Agent`) — the harness: messages, tool-call state machine, transcript,
  context accounting. Knows nothing about the console, the filesystem, or any specific LLM
  vendor.
- **The environment** (`Litos.Tools`, `Litos.Providers.*`) — concrete capabilities: file I/O,
  shell execution, attachment conversion, and the LLM API calls themselves.
- **The face** (`Litos.Console`, `Litos.Gui`, `Litos.Api`) — thin UI shells. Every face drives
  the same `AgentLoop` through `Litos.Host` and implements the same tool-approval seam in
  whatever way fits its medium.

See [ReadMe_AgentDesign.md](ReadMe_AgentDesign.md) for the full design document, and
[ReadMe_Extensibility.md](ReadMe_Extensibility.md) for how to add a new provider, tool, or face.

## Projects

| Project | What it is |
|---|---|
| `Litos.Agent` | Provider-neutral, UI-neutral agent core |
| `Litos.Tools` | File, shell, and web-search tools |
| `Litos.Tools.Mcp` | Model Context Protocol client integration |
| `Litos.Providers.Anthropic` / `.Gemini` / `.OpenAI` / `.OpenRouter` | LLM provider adapters |
| `Litos.Persistence` | JSONL transcript storage |
| `Litos.Host` | Shared composition root (DI, provider factory, tool wiring) for all faces |
| `Litos.Console` | Terminal UI face (Terminal.Gui v2) |
| `Litos.Gui` | Desktop UI face (Avalonia) |
| `Litos.Api` | HTTP API + web UI host, with Docker support, Telegram/WhatsApp/Rocket.Chat/email bridges, and MCP server integration |

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/nitinmms/LitosAiAgent1.0.git
cd LitosAiAgent1.0
dotnet build
```

Run the console face:

```bash
dotnet run --project src/Litos.Console
```

Run the desktop GUI face:

```bash
dotnet run --project src/Litos.Gui
```

Run the API host (see [ReadMe_HeadlessServiceTool.md](ReadMe_HeadlessServiceTool.md) for the
full Docker/deployment walkthrough):

```bash
cp src/Litos.Api/.env.example src/Litos.Api/.env   # fill in your provider API key(s)
dotnet run --project src/Litos.Api
```

At least one LLM provider API key (Anthropic, OpenAI, Gemini, or OpenRouter) is required to use
the agent. Configure it via the `.env` file (API host) or the Settings screen (console/GUI).

## Further documentation

- [ReadMe_AgentDesign.md](ReadMe_AgentDesign.md) — full architecture and design rationale
- [ReadMe_Extensibility.md](ReadMe_Extensibility.md) — adding providers, tools, and faces
- [ReadMe_HeadlessServiceTool.md](ReadMe_HeadlessServiceTool.md) — running as a headless/Docker service, admin UI
- [ReadMe_LitosApi_Mcp.md](ReadMe_LitosApi_Mcp.md) — MCP server integration
- [ReadMe_DeployToAWS.md](ReadMe_DeployToAWS.md) — AWS deployment guide
- [ReadMe_TelegramIntegrationTool.md](ReadMe_TelegramIntegrationTool.md) — Telegram bridge
- [ReadMe_WhatsAppIntegrationTool.md](ReadMe_WhatsAppIntegrationTool.md) — WhatsApp bridge
- [ReadMe_RocketChatTool.md](ReadMe_RocketChatTool.md) — Rocket.Chat bridge
- [ReadMe_EmailIntegrationTool.md](ReadMe_EmailIntegrationTool.md) — Email integration

## Tests

```bash
dotnet test
```

## License

Apache License 2.0 — see [LICENSE.txt](LICENSE.txt). Third-party dependency licenses are listed
in [NOTICE](NOTICE).
