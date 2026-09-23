# Improve the Litos VS Code Marketplace Listing

This document contains practical recommendations for improving the Litos Visual Studio Marketplace listing and converting more website visitors into extension users.

Marketplace listing:

<https://marketplace.visualstudio.com/items?itemName=litosai.litos-vscode>

## 1. Main problem

The Litos website presents the product as a capable AI coding agent, but the Marketplace currently introduces it mainly as a chat experience.

Current short description:

> Chat with the Litos AI coding agent inside VS Code.

This undersells the product. Litos is not merely an AI chat panel. It can understand a workspace, read and modify files, execute commands, use MCP servers, work through multi-step tasks, and verify its changes.

The Marketplace page should continue the same promise made by the website:

> Your AI coding agent. Inside VS Code. Powered by any model.

## 2. Recommended Marketplace short description

Use this:

> Open-source AI coding agent for VS Code. Reads and edits files, runs commands, uses MCP servers, and works with cloud or local models.

Shorter alternative:

> Open-source AI coding agent for VS Code, powered by cloud or local models.

The first option is stronger because it tells users what the agent can actually do.

## 3. Recommended opening section

The first section of the Marketplace README should be outcome-focused and easy to scan.

```markdown
# Litos for VS Code

Your open-source AI coding agent inside VS Code.

Litos understands your codebase, edits files, runs commands, fixes errors,
and completes multi-step development tasks without making you leave your editor.

- Use Claude, GPT, Gemini, OpenRouter, or local models
- Run local models through Ollama or LM Studio
- Read and edit files across your workspace
- Execute builds, tests, scripts, and shell commands
- Connect user-registered MCP servers
- Resume sessions across VS Code, Litos.GUI, and the Litos CLI
- First-class support for Windows and macOS

Free and open source under the Apache 2.0 license.
```

## 4. Add a strong hero screenshot

The first image should show the real Litos extension running inside VS Code—not a standalone GUI and not only the Litos logo.

The screenshot should contain:

- A real project open in VS Code.
- The Litos panel clearly visible.
- A meaningful user request.
- Litos inspecting or modifying multiple files.
- Visible task progress.
- A successful build or test result.
- A reviewable code diff if possible.

Recommended demonstration prompt:

> Add validation to this .NET API endpoint, return useful error responses, and create tests.

Avoid screenshots containing API keys, private repository names, usernames, file paths, or other sensitive information.

## 5. Recommended screenshot sequence

Add four to six images in this order.

### Screenshot 1: Complete a real coding task

Show Litos implementing a feature across multiple files and successfully running the tests.

Caption:

> Describe the outcome. Litos explores the project, edits the necessary files, and verifies the result.

### Screenshot 2: Review file changes

Show a native VS Code diff containing changes created by Litos.

Caption:

> Review every change using the VS Code workflow you already know.

### Screenshot 3: Choose a cloud or local model

Show model/provider selection including relevant supported options:

- Anthropic
- OpenAI
- Gemini
- OpenRouter
- Ollama
- LM Studio

Caption:

> Use a cloud provider or run a local model on your own machine.

### Screenshot 4: MCP server management

Show user-registered MCP servers being enabled or disabled.

Caption:

> Extend Litos with your own MCP servers and choose which integrations are active.

Do not imply that the internal tools can be enabled, disabled, or permission-gated. Litos's built-in coding tools are deliberately fixed, minimal, and auto-approved.

### Screenshot 5: Shared sessions

Show that a session created in one Litos client can be resumed in another.

Caption:

> Continue the same locally stored session across VS Code, Litos.GUI, and the Litos CLI.

### Screenshot 6: Attachments or AGENTS.md reflection

Show image/file attachment support or the `/reflect` command opening proposed `AGENTS.md` changes in a native diff.

## 6. Add a short animated demonstration

A short GIF or optimized video can communicate more than several paragraphs.

Recommended sequence:

1. Enter a real development task.
2. Litos explores the project.
3. Several files are modified.
4. Tests or the build are executed.
5. Litos fixes a failure if one occurs.
6. The final diff and passing result are shown.

Keep it approximately 20–40 seconds. Do not make the user watch a long introduction before seeing the agent work.

## 7. Explain the product accurately

The listing should keep these distinctions clear.

### Litos VS Code extension

- Runs the Litos coding agent inside VS Code.
- Uses the fixed, minimal internal coding toolset.
- Internal tools are auto-approved.
- Lets users register MCP servers.
- Lets users enable or disable registered MCP servers.
- Does not provide granular permission controls for internal tools.

### Litos.GUI

- Native desktop interface for the same Litos coding agent.
- Shares local sessions with other Litos clients.
- Lets users enable or disable registered MCP servers.

### Litos.Api

- A generic Litos AI agent exposed through a REST API.
- Lets developers integrate the agent into their own applications, interfaces, and workflows.
- Provides granular permission controls for tools exposed by registered MCP servers.
- Internal Litos tools remain fixed, minimal, and auto-approved.

## 8. Explain local-model support prominently

Local model support is an important differentiator and should not be hidden near the bottom.

Recommended copy:

> Use cloud models from Anthropic, OpenAI, Gemini, and OpenRouter—or connect Litos to local models running through Ollama, LM Studio, and compatible endpoints.

If local endpoints require a specific compatibility mode, configuration value, or base URL, add a short example under a separate “Local models” heading.

## 9. Clarify what “free” means

The extension and Litos source code are free, but cloud model providers may charge for API usage.

Recommended wording:

> Litos is free and open source. Bring your own provider API key, or use a compatible model running locally on your machine. Cloud-provider usage charges may apply.

This prevents users from interpreting “Free” as free hosted access to Claude, GPT, or Gemini.

## 10. Make installation extremely simple

Recommended section:

```markdown
## Getting started

1. Install **Litos** from the Visual Studio Marketplace.
2. Open the Litos icon in the VS Code activity bar.
3. Select a cloud provider or configure a local model.
4. Open a project and describe the change you want.

No Docker, database, or Litos account is required for the VS Code extension.
```

If the user must restart VS Code, download a component, or approve an operating-system security prompt, document that step explicitly.

## 11. State platform support clearly

Recommended copy:

> **First-class support:** Windows and macOS, including Intel and Apple Silicon Macs. Linux support is planned but is not currently available.

Keep this statement consistent across:

- The Marketplace listing
- `litosai.dev`
- GitHub README
- Release notes
- Installation documentation

## 12. Improve trust and privacy messaging

Keep the current localhost/privacy explanation, but make it easier to discover.

Recommended section:

```markdown
## Local by design

The bundled Litos process listens only on `127.0.0.1`. It is not exposed to
other computers on your network. Your prompts and relevant code are sent only
to the model provider or local endpoint you configure.

Sessions are stored locally under `~/.litos/sessions`.
```

Also link directly to:

- Source repository
- License
- Privacy documentation
- Security reporting instructions
- Issue tracker

## 13. Improve Marketplace categorization and keywords

The extension currently appears under the general “Other” category. If the Marketplace manifest supports a more relevant category, select the closest available AI, machine-learning, programming, or developer-tools category.

Recommended keywords:

```json
[
  "ai",
  "ai-agent",
  "coding-agent",
  "code-assistant",
  "mcp",
  "ollama",
  "lm-studio",
  "local-llm",
  "open-source",
  "csharp",
  "dotnet",
  "claude",
  "openai",
  "gemini"
]
```

Use only keywords accepted by the VS Code extension manifest and Marketplace publishing rules.

## 14. Add real social proof carefully

The listing is new, so low install and review counts are expected. Do not manufacture testimonials or ratings.

Recommended early approach:

1. Ask genuine testers to install the Marketplace version.
2. Ask them to report installation or onboarding problems.
3. After they have used it, invite them to leave an honest Marketplace rating.
4. Convert useful feedback into GitHub issues and public fixes.
5. Publish frequent, clear release notes.

The first five to ten authentic reviews will significantly reduce hesitation for new visitors.

## 15. Add the real demonstration videos

Include a compact “See Litos in action” section linking to:

- [Building a WinForms 15-puzzle game](https://www.youtube.com/watch?v=em8w0SwgT5Q)
- [Adding features to an existing PostgreSQL application](https://www.youtube.com/watch?v=j-Gx-7LZaso)
- [Driving Litos through Telegram](https://www.youtube.com/watch?v=S2Sn_kwCRjE)

Do not embed all full videos at the top of the listing. First show a screenshot or short GIF, then place video links lower down as supporting evidence.

## 16. Recommended Marketplace README structure

Use this order:

1. Product headline
2. One-sentence value proposition
3. Install button or installation instruction
4. Hero screenshot or short GIF
5. Key capabilities
6. Cloud and local model support
7. Getting started
8. Screenshots
9. MCP support
10. Shared sessions
11. Supported operating systems
12. Local architecture and privacy
13. Demonstration videos
14. Feedback and issue reporting
15. License

## 17. Recommended feature list

```markdown
## What Litos can do

- Understand and work across your open workspace
- Read, create, and modify files
- Run builds, tests, scripts, and shell commands
- Work through multi-step development tasks
- Use Claude, GPT, Gemini, OpenRouter, or local models
- Connect to user-registered MCP servers
- Attach files and paste images into a session
- Maintain multiple independent chat sessions
- Resume sessions across Litos clients
- Reflect session learnings into `AGENTS.md` through a reviewable diff
```

## 18. Pre-publish checklist

- [ ] Replace the “Chat with Litos” short description with agent-focused copy.
- [ ] Add a real VS Code hero screenshot.
- [ ] Add four to six captioned screenshots.
- [ ] Add a short real-task GIF or video.
- [ ] Mention Ollama and LM Studio near the top.
- [ ] Explain that users bring their own API key or local model.
- [ ] State first-class Windows and macOS support.
- [ ] State that Linux is planned, not currently supported.
- [ ] Clarify internal tools versus user-registered MCP servers.
- [ ] Describe Litos.Api as an embeddable REST API agent.
- [ ] Add source, issue tracker, license, privacy, and security links.
- [ ] Review Marketplace category and extension keywords.
- [ ] Test installation on a clean Windows machine.
- [ ] Test installation on Intel and Apple Silicon Macs.
- [ ] Verify that every screenshot matches the current extension release.
- [ ] Publish accurate version history and release notes.

## 19. Highest-priority improvements

If time is limited, complete these five tasks first:

1. Rewrite the short Marketplace description.
2. Add one excellent screenshot of Litos completing a real task inside VS Code.
3. Add a cloud/local model screenshot mentioning Ollama and LM Studio.
4. Clarify free/BYOK/local-model pricing expectations.
5. Obtain the first authentic user reviews after real usage.

These changes will make the Marketplace page feel like a continuation of the Litos website rather than a weaker second step in the installation journey.
