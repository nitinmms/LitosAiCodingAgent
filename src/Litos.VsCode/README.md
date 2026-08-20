# Litos for VS Code

Chat with the Litos AI coding agent from a panel inside VS Code — no Docker, no database, no
account to create. Litos runs a small local process on your machine and talks to it over
`localhost` only.

## Supported platforms

**Windows and macOS** (Intel and Apple Silicon), both fully supported and code-signed. Linux
support is planned but not yet available.

## Getting started

1. Install the extension.
2. Click the Litos icon in the activity bar, or run **Litos: Open Chat** from the Command Palette.
3. On first run, you'll be asked for an API key for your preferred model provider (Anthropic,
   OpenAI, Gemini, OpenRouter, or a local model). The key is stored once and shared with any other
   Litos client on your machine (CLI, desktop app) — you won't need to enter it twice.
4. Start chatting. Litos can read and edit files in your open workspace, run shell commands, and
   use any MCP servers you've configured.

## What's inside

- **Chat panel** with streaming responses, Markdown rendering, and multiple independent sessions —
  open as many chat panels as you like; they all share one lightweight background process per VS
  Code window.
- **Slash commands** matching the Litos desktop app: `/new`, `/resume`, `/provider`, `/model`,
  `/skills`, `/skill`, `/attach`, `/branch`, `/compact`, `/reflect`, `/mcp`.
- **Attachments** — attach a file via `/attach` or paste an image straight from your clipboard.
- **MCP support** — connect, manage, and use Model Context Protocol servers from a dedicated panel.
- **File sharing** — when Litos writes a file for you to download, click the link right in the
  chat.
- **`/reflect`** proposes updates to your project's `AGENTS.md` and opens them in VS Code's native
  diff view, so you can review before applying.

## Sessions are shared across Litos clients

Litos stores your conversation history locally under `~/.litos/sessions`, the same place used by
the Litos CLI and desktop app. A session started in one client can be resumed from another.

## Privacy

The bundled background process only listens on `127.0.0.1` (loopback) — nothing outside your
machine can reach it. Your code and messages go only to the model provider you've configured.

## Feedback

Found a bug or have a feature request? Open an issue on the
[project repository](https://github.com/nitinmms/LitosAiCodingAgent).
