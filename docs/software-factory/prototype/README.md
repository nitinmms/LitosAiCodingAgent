# Litos Software Factory — UX prototype

A clickable prototype of the factory described in [ReadMe_LitosSoftwareFactory_V1.md](../../../ReadMe_LitosSoftwareFactory_V1.md). It is the agreed reference for how the factory should look and behave while it is being built. Where the prototype and the blueprint disagree, the blueprint wins; fix the prototype.

Everything in it is simulated: people, repositories, model calls, GitHub, test results and the database. It never touches a real repository or model.

## Open it

- **In the repo:** open [litos-factory.html](litos-factory.html) in a browser. It loads React and fonts from public CDNs, so it needs an internet connection but no build step or server.
- **Published copy:** https://claude.ai/artifact/1K234LD5hWywg1qd6Q9kox (private; share it from the page's Share menu).

The walkthrough panel (bottom right) steps through the whole loop:

1. Admin setup and factory settings.
2. A thread, a spec and delegation.
3. A second task queued on the same repository.
4. A decision and verification with a bounded repair.
5. Handoff to a GitHub branch.
6. Rework and a budget pause.
7. Acceptance, a project lesson, and the board.

## What it covers

| Area | Blueprint |
| --- | --- |
| Sign-in, roles, invitations | §13.3 |
| Threads, chat before `@factory`, spec stage, decisions, handoff, rework | §4, §5, §11 |
| Stage rail, board, whose-turn labels, flow metrics | §5.1, §7.1, §12.1 |
| One run per repository, parallel runs across repositories, queueing | §6.3 |
| Per-call token reservation, budget pause, strict/estimated providers | §9 |
| Stack-agnostic verification profiles and presets | §10 |
| Factory settings: providers and model catalog, MCP servers, skills, tools, limits, presets | §8.1–§8.4 |
| Run settings snapshot | §8.2, §14 |
| Project lessons | §21–§22 |

## Changing it

The page is a single self-contained file:

- React 18 with `htm` templates, no build step.
- A reducer-based simulation engine: `schedule`, `stepThread` and the scripted runs `implSteps`/`reworkSteps`.
- The components.
- The walkthrough (`steps`).

After editing, run the checks:

```sh
cd docs/software-factory/prototype/checks
npm install
npm run check
```

`check.js` runs the full walkthrough headlessly and renders every screen at every step with React's server renderer. It also asserts the key behaviors:

- budget reservation stays within the cap;
- running tasks keep their settings snapshot;
- unapproved repository skills are skipped;
- retired models are withheld from members.

Republishing the Artifact is done from a Claude Code session. The published page is this file without the standalone wrapper: the `<!doctype>`, `<html>`, `<head>` and `<body>` tags added at the top and bottom.
