# LitosAiAgent — Extensibility Feasibility

Evaluates whether LitosAiAgent can support third-party **extensions** — user- or vendor-authored
code that adds tools, commands, or behavior without modifying core source — in the spirit of
[pi.dev](https://pi.dev)'s extension system. Written before any implementation; no code has
changed as a result of this document. See `ReadMe_AgentDesign.md` for the architecture this
builds on, especially §2 (design philosophy), §4.4 (Skills), and §9 (composition root).

## 1. What pi.dev does, briefly

Pi's coding agent supports three content/code mechanisms, in increasing order of power:

| | Format | Can add | State |
|---|---|---|---|
| Prompt Templates | Markdown, parameterized | Reusable prompt snippets | none |
| Skills | `SKILL.md` + assets | Domain workflows, invoked as `/skill:name` | none |
| **Extensions** | TypeScript module | Tools, commands, shortcuts, event hooks, custom rendering, providers | persistent, via `appendEntry`/`session_start` |

An extension is a `.ts` file (or `dir/index.ts`) auto-discovered from `~/.pi/agent/extensions/`
(global) or `.pi/extensions/` (project), loaded at startup via `jiti` (TypeScript executed
directly, no build step), exporting a default factory that receives an `ExtensionAPI`:

```typescript
export default function (pi: ExtensionAPI) {
  pi.registerTool({ name: "my_tool", parameters: Type.Object({...}), async execute(...) {...} });
  pi.registerCommand("stats", { handler: async (args, ctx) => {...} });
  pi.on("tool_call", (event, ctx) => {...});   // intercept/block/mutate before execution
  pi.on("before_agent_start", (event, ctx) => {...});  // inject messages, edit system prompt
}
```

The `ExtensionAPI` exposes ~20 lifecycle events (session start/shutdown, tool call/result,
agent turn boundaries, provider request/response, message streaming), registration methods for
tools/commands/shortcuts/renderers/providers, and session state helpers. Extensions are
hot-reloadable via `/reload` and run **in-process**, with full system permissions — pi's own
docs flag this as a trust boundary ("only load from trusted sources").

This is easy in a JS host because dynamically-loaded, uncompiled code *is* the normal execution
model. That single fact is the crux of the feasibility gap below.

## 2. Where Litos stands today

Confirmed by direct inspection and repo-wide search — no partial infrastructure exists:

- **No dynamic code loading of any kind.** Zero uses of `AssemblyLoadContext`, `Assembly.Load`,
  MEF (`[ImportMany]`/`CompositionContainer`), or any third-party plugin-loader package.
- **Every tool is a compile-time C# type**, registered by hand, once, in
  `src/Litos.Host/LitosHostBuilder.cs:35-51` (`services.AddSingleton<ITool, WebSearchTool>()`
  and seven siblings). Adding a built-in tool means writing the class and adding one line here,
  then recompiling.
- **Every slash command is a hardcoded switch**, duplicated (and manually kept in sync) between
  `src/Litos.Console/Program.cs:495` and `src/Litos.Gui/SlashCommand.cs` / `MainWindow.axaml.cs`.
- **`AgentLoop` has no hook/event-interception seam.** It calls `tools.Resolve(name).InvokeAsync(...)`
  directly; nothing can observe or mutate a tool call, a provider request, or the system prompt
  from outside `Litos.Agent`/`Litos.Host` today.
- **`LitosConfig`/`~/.litos/config.json`** is a closed record (provider, model, API keys) — no
  extensions-enable list or plugin manifest concept.

What *does* exist, and matters a great deal for feasibility, is one genuinely dynamic,
filesystem-driven discovery mechanism: **Skills**. `SkillDiscovery`
(`src/Litos.Tools/Skills/SkillDiscovery.cs`) walks three roots — `.litos/skills/` from cwd
upward (gitignore-style ancestor walk), `~/.litos/skills/`, `~/.claude/skills/` — parses each
`SKILL.md`'s frontmatter, and exposes an index (name + description) in the system prompt via
`LitosSystemPromptProvider`. The full body loads only when the model calls the single `skill`
tool (`SkillTool`) with a chosen name — "progressive disclosure." Design doc §4.4 states the
guiding principle directly: *"a skill is just another `ITool`, registered once, discovered
dynamically."* This is a **content**-plugin system (adds instructions the model can choose to
read), not a **code**-plugin system (cannot add a new tool or intercept behavior) — but its
directory-walk shape, collision rule (closer root wins), and progressive-disclosure pattern are
the most directly reusable precedent for anything built here.

The other reusable precedent is `IChatProvider`'s **keyed DI** registration
(`services.AddKeyedSingleton<IChatProvider>("anthropic", ...)`, resolved by string key at
runtime via `ChatProviderFactory`) — the idiomatic .NET shape for "N implementations of an
interface, chosen by name at runtime." It's populated by hand today, but the resolution
mechanism itself needs no redesign to be populated dynamically instead.

Finally, the design doc's own §10 roadmap (not yet built) already anticipates callers outside
the current process: §10.2 proposes a `litos --stdio` JSON-lines mode for non-.NET callers,
explicitly deferred "until an actual caller shows up." This is structurally the same shape an
out-of-process extension protocol (§4.3 below) would need — building extensions on it means
building §10.2 first, not inventing a fourth calling convention.

## 3. Scope A — Tools only

**Definition:** third-party code can register new `ITool` implementations, discovered from a
filesystem location, without recompiling Litos. No custom commands, no event interception, no
custom rendering.

### 3.1 Why this scope is tractable

`ITool` (`src/Litos.Agent/Tools/ITool.cs`) is already minimal and self-describing:

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct);
}
```

A third-party tool that implements this interface is, from `ToolRegistry`'s perspective,
indistinguishable from a built-in one — `ToolRegistry` just needs *an* `IEnumerable<ITool>` to
build its `name -> ITool` dictionary and schema list (`src/Litos.Agent/Tools/ToolRegistry.cs`).
Nothing about the interface, the registry, or `AgentLoop`'s tool-call handling needs to change.
The only missing piece is **how new `ITool` instances get into that `IEnumerable` without being
written in `LitosHostBuilder.cs` by hand.**

### 3.2 Design sketch

Following the `SkillDiscovery` precedent almost exactly:

1. **Discovery.** A new `IExtensionDiscovery`/`ExtensionDiscovery` class walks the same kind of
   roots skills already use: `.litos/extensions/` from cwd upward, `~/.litos/extensions/`. Each
   subdirectory is one extension, containing a manifest (e.g. `extension.json` — name, version,
   entry assembly/script) and the extension's payload (see §3.3 for what that payload is).
2. **Loading.** Whatever loading strategy is chosen (§5), the end result is a set of `ITool`
   instances, constructed and validated (schema present, name doesn't collide with a built-in or
   another extension — collision rule can mirror skills' "closer root wins").
3. **Registration.** `LitosHostBuilder.AddLitosAgent` gains one new step, after the existing
   `services.AddSingleton<ITool, ...>()` calls: resolve `ExtensionDiscovery`, iterate discovered
   tools, `services.AddSingleton<ITool>(instance)` for each. This is additive — every existing
   registration line is untouched.
4. **System prompt / tool list.** No change needed — `ToolRegistry.Schemas` already flattens
   every registered `ITool`, built-in or not, into the list sent to the provider.
5. **Config.** `LitosConfig` gains an optional `Extensions` section (enable/disable by name,
   per-extension config values) — same fallback-from-env-then-file resolution order already used
   for API keys, extended rather than redesigned.
6. **Rendering.** Each face's tool-call panel (`ToolCallPanel` in console, the GUI equivalent)
   currently has some per-tool-name-aware rendering for built-ins. A third-party tool needs a
   sane generic fallback (name + JSON args/result) — faces don't need per-extension code, just a
   default case that isn't ad hoc.

### 3.3 What's genuinely new work

- The discovery/loading class itself (new, but structurally a sibling of `SkillDiscovery`).
- A manifest format and validation (name collisions, malformed schema, version compatibility).
- Whichever code-loading mechanism is chosen (§5) — this is where the real engineering is,
  independent of scope.
- A generic tool-call renderer fallback in both faces (small, additive).
- Trust/consent UX: unlike a built-in tool, a third-party tool is code the user didn't audit via
  code review of this repo. At minimum, a first-use confirmation ("extension X wants to register
  tool Y — allow?") analogous to `IToolApprovalGate`'s existing shell-command confirmation flow
  is warranted, not optional. `IToolApprovalGate` (`src/Litos.Tools/Shell/IToolApprovalGate.cs`)
  is the existing seam for "pause and ask the user before doing something with consequences" —
  extension *loading* (not just shell execution) plausibly belongs behind the same kind of gate.

### 3.4 Estimate shape

Small-to-medium. Reuses `ITool`, the `SkillDiscovery` walk/collision pattern, and the keyed-DI
registration idiom nearly as-is. The dominant cost is the code-loading mechanism (§5), not the
Litos-side wiring, which is genuinely additive per §2's design philosophy — no existing file
needs restructuring, only `LitosHostBuilder.cs` and `LitosConfig.cs` gain new, isolated code.
Realistic as 2-3 build-sequence milestones in the style of design-doc §11.

## 4. Scope B — Full pi.dev parity

**Definition:** everything in Scope A, plus user-definable commands, event hooks that can
observe/mutate tool calls and agent turns, custom message/entry rendering, hot-reload, and
persistent extension state across a session.

Everything in §3 is a strict prerequisite and subset of this scope; the additions below are
each independent axes of new surface area.

### 4.1 Commands

**Current state:** hardcoded, duplicated, "kept in sync by hand" per the codebase's own comment
in `SlashCommand.cs` — `src/Litos.Console/Program.cs:495`'s `switch` and
`src/Litos.Gui/SlashCommand.cs`'s metadata list plus `MainWindow.axaml.cs`'s separate dispatch.

**What's needed:** a `CommandRegistry` (name → handler + description + arg-completion, roughly
pi's `registerCommand` shape) that both faces consult instead of their own switch/list. This is
a **refactor of existing behavior**, not purely additive — every existing built-in slash command
(`/new`, `/resume`, `/attach`, `/provider`, `/model`, `/branch`, `/skills`, `/export`) would need
to move into the same registry so extension-provided and built-in commands are handled
uniformly. Both faces' command-dispatch code changes. Collision handling (two extensions
registering the same name) needs a policy — pi suffixes with `:1`/`:2`; Litos could do the same
or simply reject the second registration, consistent with its "transparent, no surprises"
philosophy (§2).

### 4.2 Event hooks

**Current state:** none. `AgentLoop.RunTurnAsync` (`src/Litos.Agent/AgentLoop.cs`) is a closed
loop: stream from `IChatProvider`, on `ToolCallCompleted` resolve and invoke the tool, append to
transcript, repeat. Nothing observes this from outside.

**What's needed:** the highest-leverage pi.dev hooks to reproduce would be `tool_call`
(pre-execution, can block/mutate args), `tool_result` (post-execution, can rewrite the result —
chainable like middleware), `before_agent_start`/`context` (inject messages or edit the system
prompt before a provider call), and `session_start`/`session_shutdown` (extension lifecycle).
Each requires a new extension point *inside* `Litos.Agent` — currently a zero-dependency,
deliberately minimal project per §2 ("the brain... knows nothing about the console, the
filesystem, or any specific LLM vendor"). An event-hook system doesn't violate that neutrality
in principle (hooks would be typed C# delegates, not filesystem/UI-aware), but it is new surface
area in the one project the design doc treats as the most stable, highest-trust layer. This is
the single change most likely to need careful design review before implementation, since a
poorly-scoped hook API tends to either (a) leak too much internal state and become a de facto
second public API to maintain compatibility for, or (b) be too narrow and force every
interesting extension back to forking.

### 4.3 Loading mechanism, hot-reload, and process boundary

Pi's hot-reload (`/reload`) works because TypeScript-via-`jiti` is interpreted, not compiled —
tearing down and re-running a factory function is cheap and safe. The .NET equivalent depends
entirely on which loading strategy is picked (§5):

- **Compiled DLL via `AssemblyLoadContext`**: reload is possible (`AssemblyLoadContext.Unload()`
  + reload), but is one of the more failure-prone corners of .NET — unload only succeeds if
  every reference to types/instances from that context is released first, which is easy to get
  wrong with long-lived singletons (`ITool` instances registered in a DI container are exactly
  the kind of long-lived reference that can pin an `AssemblyLoadContext` and silently defeat
  unload). Achievable, but needs deliberate lifetime management, not a drop-in.
- **Roslyn C# scripting**: reload is simpler (just re-run the script), at the cost of weaker
  isolation and slower each-load latency (JIT/compile per load, not cached like a shipped DLL).
- **Out-of-process (stdio, per design-doc §10.2)**: reload is trivial (restart the subprocess);
  isolation is strongest; but there is no in-process hook mechanism possible at all in this
  model — a subprocess extension can only sit at the `ITool` boundary (call in, get a result
  back), which caps it at Scope A regardless of protocol. Event hooks (§4.2) fundamentally
  require in-process code, since "intercept and mutate a tool call before it runs" only makes
  sense inside the same call stack as `AgentLoop`.

This is the key structural finding for Scope B: **event hooks and out-of-process isolation are
mutually exclusive.** Any design targeting full pi.dev parity is implicitly committing to
in-process loading (DLL or scripting), which reopens the isolation/trust questions §3.3 already
raised for tools alone, now with a larger blast radius (an event hook sees every tool call and
every message, not just its own invocations).

### 4.4 Custom rendering

Pi's `registerMessageRenderer`/`registerEntryRenderer` let an extension draw its own TUI
widgets. Litos's two faces use entirely different rendering stacks (Terminal.Gui v2 for console,
Avalonia/XAML for GUI) — a single extension-authored renderer cannot serve both without either
(a) a face-agnostic renderer abstraction neither face currently has, or (b) requiring extension
authors to write two renderers, one per face, breaking the "write once" premise. This is the
single most Litos-specific obstacle to parity: pi.dev only has one face (its TUI) to design a
renderer API against; Litos deliberately has two (§2, §7.7), and they don't share a UI toolkit.
Realistically, Scope B's rendering parity would need to be scoped down to "extensions can supply
a generic structured-data hint (e.g. title + key/value pairs) that each face renders in its own
idiom," rather than pi's arbitrary-widget model — closer to a contract than a canvas.

### 4.5 Persistent extension state

Pi's `appendEntry`/`session_start` reconstruction pattern needs a Litos analog: the transcript
(`ITranscriptStore`, JSONL) would need an extension-owned entry type that round-trips through
persistence without `Litos.Agent`/`Litos.Persistence` needing to know what's inside it — doable
(a generic `ExtensionEntry { Type, Data }` envelope alongside existing message types) but is
another schema surface to keep stable, since session JSONL files are already a durable,
resumable format (§8) — extension entry types become part of that durability contract too.

### 4.6 Estimate shape

Large. Every sub-area above (§4.1-4.5) is independently non-trivial, two of them (commands,
event hooks) require refactoring or extending code outside the purely-additive pattern the rest
of this codebase has followed so far (§7.7's "zero changes required" track record for the GUI
face would not hold here), and §4.3's isolation/hook tension forces an early, hard-to-reverse
architectural commitment. Realistic as its own multi-milestone epic, most sensibly sequenced
*after* Scope A ships and is used enough to reveal which of §4.1-4.5 actually matter in
practice — pi.dev's own breadth is a product of years of iteration against real extension
authors, not a spec to hit in one pass.

## 5. Loading mechanism — recommendation

Three realistic .NET options, evaluated against Litos's actual context: single-user desktop/console
tool (not multi-tenant), already-.NET codebase, design philosophy that consistently favors
transparency and minimal surface area over cleverness (§2).

| | Compiled DLL (`AssemblyLoadContext`) | Roslyn scripting | Out-of-process (stdio) |
|---|---|---|---|
| Author ergonomics | Needs a `.csproj`, `dotnet build` | Drop a `.csx`, no build step | Any language; needs a small protocol client |
| Isolation from host | Weak (same process, same AppDomain-equivalent) | Weak (same process) | Strong (separate process) |
| Perf per call | Native, fastest | Slower first run (compile), then fast | IPC overhead per call |
| Hot-reload | Possible, fragile (unload pinning, §4.3) | Simple (re-run script) | Trivial (restart process) |
| Enables event hooks (§4.2) | Yes | Yes | No — capped at tool-call boundary |
| Language-agnostic | No (.NET only) | No (.NET only) | Yes |
| Precedent in this codebase | None, but standard .NET pattern | None | §10.2 already on the roadmap, unbuilt |

**Recommendation: start with compiled DLLs via `AssemblyLoadContext`, scoped to Scope A
(tools only) first.**

Reasoning:
- Litos is a .NET codebase built by and for .NET developers today (no polyglot user base has
  been established); requiring `dotnet build` from an extension author is a reasonable bar,
  not a barrier, and it's the only option of the three that doesn't trade away performance or
  isolation to get there.
- It's the only in-process option compatible with *eventually* reaching Scope B's event hooks
  (§4.2) if that's ever pursued — choosing it doesn't foreclose Scope B the way starting with
  stdio would.
- Skip hot-reload initially (§4.3 flags real fragility here); Scope A tools don't need it as
  urgently as pi's rapid-iteration TUI workflow does — an extension author re-running `litos`
  after a rebuild is an acceptable cost for a first version, and hot-reload can be revisited once
  `AssemblyLoadContext` lifetime management is proven out.
- Defer the out-of-process/stdio route, but don't discard it — it's the right answer if and when
  non-.NET extension authors become a real, stated need, and design-doc §10.2 already earmarks
  the exact mechanism (`litos --stdio`) that would carry it. Building Scope A's DLL loader first
  doesn't block this; they're additive alternatives (an extension registry could support both
  "local DLL" and "stdio subprocess" as two `ExtensionSource` kinds later), not a fork in the
  road that has to be picked once and lived with forever.
- Roslyn scripting is the weakest fit: it inherits compiled-DLL's isolation weaknesses without
  compiled-DLL's performance, and its "no build step" ergonomic win matters most for rapid
  iteration workflows (à la pi's `/reload`) — which this recommendation is explicitly deferring
  anyway. Worth reconsidering only if hot-reload becomes a stated priority before Scope B.

## 6. Open questions for scoping the first milestone

- Trust model: should extension loading require an explicit user opt-in per extension (mirroring
  `IToolApprovalGate`'s consent pattern), or is "you put a DLL in `.litos/extensions/`" itself
  sufficient informed consent, the same way a project's own `AGENTS.md`/`CLAUDE.md` is trusted
  unconditionally today?
- Should extension `ITool`s be allowed to shadow/override a built-in tool name, or always reject
  on collision?
- Is there an actual near-term extension author in mind (you, internally) — worth knowing before
  investing in manifest format and config-section design, versus building the minimum that
  proves the `AssemblyLoadContext` loader works end-to-end first.
