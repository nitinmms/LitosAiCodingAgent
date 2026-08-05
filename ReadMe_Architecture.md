# Architecture

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

## The agent loop

`AgentLoop.RunTurnAsync` (`src/Litos.Agent/AgentLoop.cs`) is the one loop every face drives. A
"turn" starts with one user message and keeps going — model response, tool calls, tool results,
repeat — until the model responds with no tool calls left to make.

```mermaid
flowchart TD
    START(["User sends a message"]) --> APPEND["Append message to transcript"]
    APPEND --> REQUEST["Build ChatRequest
    (system prompt + transcript + tool schemas)
    and send to the LLM provider"]
    REQUEST --> STREAM["Stream the response
    (text deltas + zero or more tool calls)"]
    STREAM --> CHECK{"Did the model
    call any tools?"}
    CHECK -- "No" --> DONE(["Turn ends —
    response shown to the user"])
    CHECK -- "Yes" --> RUN["Run each tool call,
    append its result to the transcript"]
    RUN --> REQUEST

    style START fill:#2d6a4f,color:#fff
    style DONE fill:#2d6a4f,color:#fff
```

Every round trip — request, stream, run tools — appends to the same `Transcript`, so the next
round's request always includes everything that happened before it. The loop only stops once a
model response comes back with no tool calls to run.

## How context comes together for a turn

Every turn sends one `ChatRequest` (system prompt + full message history + tool schemas) to the
provider. The system prompt itself is built once per session, not re-built per turn; the message
history grows every turn and is periodically compacted to keep it inside the model's context
window.

```mermaid
flowchart TD
    subgraph SP["System prompt — built once per session, on first turn"]
        direction TB
        SP1["① Identity line — fixed"]
        SP2["② Available tools — live ToolRegistry schemas
        (read_file, write_file, edit_file, shell, skill, web_search, mcp__*, ...)"]
        SP3["③ Guidelines — fixed steering rules"]
        SP4["④ Skill catalog — name + description index only
        (full SKILL.md body loads later, only if the model calls the skill tool)"]
        SP5["⑤ Project instructions — full body, eager
        every discovered AGENTS.md / CLAUDE.md"]
        SP6["⑥ Date / working-directory footer"]
        SP1 --> SP2 --> SP3 --> SP4 --> SP5 --> SP6
    end

    subgraph MH["Message history — grows every turn, persisted to JSONL"]
        direction TB
        MH1["Prior user / assistant / tool-call / tool-result messages"]
        MH2["Compaction summary block
        (replaces old messages once the context window fills — /compact or automatic)"]
        MH3["This turn's new user message
        + any attachments (files, URLs, images) and steering messages folded in"]
        MH1 -.->|"compacted into"| MH2
        MH2 --> MH3
    end

    SP6 --> REQ
    MH3 --> REQ

    REQ["ChatRequest
    { SystemPrompt, Messages, Tools, Model }"]
    REQ --> PROVIDER["IChatProvider.StreamAsync
    (Anthropic / OpenAI / Gemini / OpenRouter)"]
    PROVIDER --> RESP["Streamed response:
    text deltas, tool calls"]
    RESP -->|"tool call → ToolResult"| MH1
    RESP -->|"assistant message"| MH1

    style SP4 stroke-dasharray: 4 3
    style MH2 stroke-dasharray: 4 3
```

- **System prompt** — assembled once by `LitosSystemPromptProvider.BuildAsync` when the session's
  `AgentLoop` starts; editing a `SKILL.md` or `AGENTS.md`/`CLAUDE.md` file requires a new session
  (`/new`) to take effect, not just a saved file. Skills are *progressive disclosure* (index now,
  full body only on demand); project instructions are the opposite — eager, full-body, every turn
  — since standing rules only steer the model if they're always present.
- **Message history** — every user/assistant/tool message is appended to an in-memory `Transcript`
  and persisted to `%USERPROFILE%\.litos\sessions\{owner}\{sessionId}.jsonl` as it happens.
  Compaction (`Compactor`) runs at the start of a turn, before the system prompt is rebuilt:
  once the transcript crosses a token threshold (or the user runs `/compact`), older messages are
  summarized by a plain call to the same provider/model and replaced with one summary block —
  everything after the cut point is kept verbatim.
- **`ChatRequest`** — `ContextAccountant.BuildRequest` assembles the two into one request per
  turn: `transcript.Messages` (full history plus this turn's new user content), `ToolRegistry`'s
  current tool schemas, the model name, and the system prompt string.

## Projects

| Project | What it is |
|---|---|
| `Litos.Agent` | Provider-neutral, UI-neutral agent core |
| `Litos.Tools` | File, shell, and web-search tools |
| `Litos.Tools.Mcp` | Model Context Protocol client integration — wired up in `Litos.Api` and `Litos.Gui`; `Litos.Console` integration is pending |
| `Litos.Providers.Anthropic` / `.Gemini` / `.OpenAI` / `.OpenRouter` | LLM provider adapters |
| `Litos.Persistence` | JSONL transcript storage |
| `Litos.Host` | Shared composition root (DI, provider factory, tool wiring) for all faces |
| `Litos.Console` | Terminal UI face (Terminal.Gui v2) — work in progress |
| `Litos.Gui` | Desktop UI face (Avalonia) — **working** |
| `Litos.Api` | HTTP API + web UI host, with Docker support and a working Telegram bridge — **working**. WhatsApp/Rocket.Chat/email bridges are design docs only (no implementation yet) |
