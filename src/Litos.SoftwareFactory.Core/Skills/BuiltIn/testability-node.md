---
name: testability-node
version: 1
description: Use in a Make-testable task on a Node web app (Express serving a built SPA, Vite, Next): a production-like build and start, PORT, NODE_ENV=test endpoints, a resettable data store, and the traps check_testability reports.
defaultFor: testability
---

Applies when the detected stack is `node-express-spa` or another Node web app. The brief comes first; this is how to meet it on Node.

## Build and start
- The factory tests a production-like build, never a dev server: `npm run build` must produce what `npm start` serves (for example `vite build` into `dist/`, which Express serves). Dev servers compile on first request and hold sockets open, which makes tests flaky.
- `npm start` must read the port from `process.env.PORT`. An app that hard-codes a port fails the Gate's first step with "listened on 8787 instead".
- Bind to 127.0.0.1 or localhost, read from `process.env.HOST`, never only to a public interface.
- If the app calls a paid or external API (an AI provider, a payment service), add a fake chosen by an environment variable (`STORY_PROVIDER=fake`) that the contract sets, so a test run needs no key and costs nothing.

## The test environment
- The profile sets `NODE_ENV=test`. Register test-only routes (`POST /__test/reset`, `POST /__test/clock`) only when `process.env.NODE_ENV === 'test'`. The Gate checks they are gone with `NODE_ENV=production`.
- Express's catch-all for the SPA must not swallow `/__test/*` or `/api/*`: put those routes first, or exclude them from the fallback.

## Data and reset
- No data at all: `reset: { strategy: none }`.
- A SQLite file: `strategy: sqlite-snapshot` with a seed script (`npm run seed`) that writes the file; the factory copies it back before each start.
- A server database: a service container (postgres, mysql, mongo, redis) and a reset command or endpoint.

## Sign-in and pages
- Form login: labelled fields (`<label for>`), a submit button whose text is its name, and a URL change after success (`success_when: { url_not_contains: /login }`). A JWT kept in localStorage works as is.
- Seed test users from variables in test only (TEST_ADMIN_PASS, say); the contract passes `{secret:TEST_ADMIN_PASS}`.
- Every control needs an accessible name or a `data-testid`; give each SPA route a real URL so the Gate can visit it directly.
- Uncaught errors, `console.error` and failed API calls fail a page: fix them, don't hide them.
