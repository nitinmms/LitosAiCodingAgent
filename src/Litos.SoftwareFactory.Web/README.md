# Litos Software Factory — web app

The browser app for the software factory: React, Vite and TypeScript. It talks only to `Litos.SoftwareFactory.Host` and is served by it in production.

## Screens (M1)

- Sign-in.
- Projects: the registered GitHub repositories, and registration for an admin.
- Threads: the thread list by project, and for the open thread the stage rail, the conversation with decision and handoff cards, the composer with `@factory`, and the budget, verification and changed-files panels.

The visual tokens and components follow the UX prototype in `docs/software-factory/prototype`.

## Run it

Start the host first (see `.env.factory.example` at the repository root), then:

```sh
npm install
npm run dev
```

The dev server runs on http://localhost:5173 and proxies `/api` to the host at `http://127.0.0.1:5180`. Set `FACTORY_HOST_URL` to point it at a host elsewhere.

## Build it into the host

```sh
npm run build
```

The build goes to `../Litos.SoftwareFactory.Host/wwwroot`, which the host serves at `/`. That folder is build output and is not committed, so build the app before publishing the host.

## Test it

```sh
npm test
npm run typecheck
```

The tests run the whole app against `src/test/fakeHost.ts`, an in-memory host with the same routes, status codes and state rules as the real one. When the host's API changes, change the fake with it.

## How it stays current

- The thread snapshot (`GET /api/threads/{id}`) carries an `eventCursor`. The app opens the thread's event stream from that cursor, so it hears everything after the snapshot and replays nothing before it.
- `state` and `usage` events carry the thread's state and budget figures and are applied directly. A `message` event, or a change of state, makes the app fetch the thread again.
- Every thread copy carries the server's `revision`; the app always keeps the newer one, so a late response can never undo a live event.
- Only the open thread has a stream. The thread list is refreshed every 15 seconds.
- The pull request label ("Draft PR #12", "PR #12 merged") comes from `GET /api/threads/{id}/pull-request`, asked when the task changes state and once a minute: merging and closing happen on GitHub, not in the factory.

## Things to know

- After a handoff, an `@factory` message is a **change request** and starts a rework run; the composer says so and its button reads "Send change request". The factory cannot answer questions in M1. A change request can be taken back with "Withdraw change request" whenever its run is not executing.
- Routes live in the URL fragment (`#/threads/<id>`, `#/projects`), so the host only ever serves `index.html`.
- The session is an HTTP-only, same-site cookie; the app holds no token. Every state-changing request sends the `X-Factory-Request` header the host requires.
- `parseMention` in `src/domain/task.ts` mirrors the host's `FactoryMention.TryParse`, and the rules for what each state allows mirror `TaskLifecycle` and the host's dispatch. The tests on both sides pin the same cases; change them together.
- Fonts are bundled with the app. It makes no request to any other site.
