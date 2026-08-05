# Litos AI Agent

A minimal, transparent AI coding agent written in pure .NET. Multi-provider (Anthropic,
OpenAI, Google Gemini, OpenRouter), multi-face (console, desktop GUI, and an HTTP/API host with
web UI), with tool use (file edit, shell, web search) and MCP client support.

Built to learn how a coding agent actually works, from the inside, in C#/.NET — the stack I've
worked in for 23+ years — rather than treating one as a black box.

Primarily inspired by Mario Zechner's [pi](https://pi.dev), particularly its session-as-working-
directory model, and secondarily by [alejandro-ao](https://github.com/alejandro-ao)'s
[Tau](https://github.com/alejandro-ao/tau) and its "separate the brain, the environment, and the
face" architecture.

## Videos

- [Built an AI Coding Agent in Pure C#/.NET (No Python!) — Watch It Build a WinForms 15-Puzzle Game](https://www.youtube.com/watch?v=em8w0SwgT5Q)
- [Built an AI Agent in Pure C#/.NET— Here it is working through Telegram creating Mermaid Diagrams](https://www.youtube.com/watch?v=S2Sn_kwCRjE&t=8s)

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

On first run, open Settings to add at least one provider API key (see
[Environment variables](#environment-variables) below).

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
copied from [src/Litos.Api/.env.example](src/Litos.Api/.env.example)) or the Settings screen
(`Litos.Gui`). An env var always wins over any value stored in `Litos.Api`'s mounted config file.

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
