---
name: artifact-design
description: Design rules and workflow for HTML pages published with the artifact tool (reports, dashboards, diagrams, explainers, small interactive tools). Load before writing or rewriting any artifact.
---
# Artifact design

An artifact is one self-contained HTML page that you write to a file and publish with the
`artifact` tool. The user opens it in their browser from the link in the tool result. Use it when a
page is clearer than chat text — a report, comparison, dashboard, diagram, explainer, prototype,
calculator or small game — or when the user asks for a page. Answer in chat instead when a few
paragraphs or a short table would do.

## Workflow

1. **Pick the file path.** Use the location the user names. Otherwise write to
   `<system temp directory>/litos-artifacts/<short-slug>.html` (on Windows `%TEMP%`, elsewhere
   `/tmp`), so the page doesn't land in the user's repository. The path must be absolute.
2. **Write the whole page** with `write_file`.
3. **Publish**: `artifact` with `action: "publish"` and `file_path`. Pass `title` only if the
   page's `<title>` isn't the name you want.
4. **Tell the user** in one or two sentences what the page shows. The link is already in the tool
   result; repeat it as a plain URL on its own line if you mention it.
5. **Changes**: edit the *same file* (`edit_file` for small changes) and publish the *same
   file_path* again. That adds a version at the same URL; tell the user to refresh the tab. Use a
   new file path only when the user wants a separate page. `action: "list"` finds earlier
   artifacts; `action: "read"` returns a published version's HTML when the source file is gone.

## Where the page runs (hard limits)

The page is served with a strict sandbox. Design within it — these fail silently otherwise:

- **No network.** `fetch`, XHR, WebSocket, EventSource and `sendBeacon` are blocked. Embed all
  data in the page (a `<script type="application/json" id="data">` block or a JS literal).
- **No storage.** The page has an opaque origin: `localStorage`, `sessionStorage`, IndexedDB and
  cookies throw or are unavailable. Wrap any use in `try/catch` and keep state in memory; it resets
  on refresh, and the page must work fully without it.
- **No form submission** and **no `eval`** / `new Function` / string `setTimeout`. Avoid libraries
  that need them (some template engines, Vega's default build).
- **No iframes, `<object>` or `<embed>`.**
- Interactivity is fine and encouraged where it helps: event handlers, tabs, filters, sorting,
  `<canvas>`, SVG, CSS and JS animation, `alert`/`confirm`, links opening in a new tab, and
  building a file for the user to download (`Blob` + `URL.createObjectURL` + `<a download>`).

## External resources

- Scripts may load only from `https://cdnjs.cloudflare.com` (preferred), `https://cdn.jsdelivr.net/npm/`,
  `https://unpkg.com`, `https://cdn.tailwindcss.com` or `https://code.jquery.com`.
- Stylesheets: inline, those CDNs, or Google Fonts (`https://fonts.googleapis.com`, files from
  `https://fonts.gstatic.com`).
- Images: inline SVG, `data:` URIs, or those CDNs. Not arbitrary image URLs — they are blocked.
- **Pin an exact version** in every URL (`chart.js@4.4.1`, never `chart.js` or `chart.js@4`), using
  a version you know has existed for a while.
- Everything else goes inline. Keep the page under 10 MB.

## Page contract

- `<!doctype html>`, `<html lang="en">`, `<meta charset="utf-8">`, and
  `<meta name="viewport" content="width=device-width, initial-scale=1">`.
- `<title>`: a name of two to four words ("Benchmark Dashboard"), not a sentence. It becomes the
  artifact's name.
- **Colour as tokens** on `:root` (background, surface, text, muted text, border, accent), with a
  dark set under `@media (prefers-color-scheme: dark)`. Give `body` an explicit background and text
  colour from the tokens — never rely on the browser default. Check contrast in both themes.
- **Layout works at phone width**: a 16px side gutter, `max-width` around 960–1100px for reading
  pages, no horizontal page scroll (wide tables and code scroll inside their own container), images
  and canvases `max-width: 100%`.
- System font stack (`system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`) unless a web font
  earns its place; monospace for code and numbers that should align.
- Semantic HTML (`header`, `main`, `section`, `h1`–`h3`, `table` with `th`), visible focus states,
  `aria-label` on icon-only controls, text alternatives for charts (a caption or a data table).

## Design effort

Match effort to the request. A quick report wants clean typography, clear hierarchy and good
spacing — not decoration. A dashboard or explainer the user will share deserves real design:

- Lead with the answer: title, a one-line summary, then the key numbers or the main visual.
- One accent colour, used for meaning (the thing to look at), not for decoration.
- Generous whitespace and a consistent spacing scale (4/8/12/16/24/32px).
- Charts: label axes and units, start bar axes at zero, direct-label series where possible instead
  of a distant legend, and keep colours distinguishable in both themes.
- Prefer showing the real mechanism (an actual diagram of the flow, the real numbers) over generic
  illustration. No stock-photo placeholders or lorem ipsum.

## Before publishing, check

- It renders with no network beyond the allowlisted CDNs, and nothing depends on storage.
- Light and dark both look intentional; nothing overflows horizontally at 375px wide.
- Every script URL has an exact pinned version.
- The `<title>` is a short name.
