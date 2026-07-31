# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Avalonia (`Litos.Gui`)

- **Prefer `Margin` on a `ScrollViewer`'s direct child over `Padding` on the `ScrollViewer` itself.** In this project's Avalonia version, `ScrollViewer.Padding` is applied visually but not correctly folded into the scrollable extent calculation — content within the padding region at the trailing edge renders but is permanently unreachable by scrolling (observed as roughly the last `Padding` amount of content, e.g. one line of text, left as a clipped sliver no resize or layout-invalidation could recover). See `ReadMe_AgentDesign.md` §7.7 for the full investigation.
  - **Diagnostic tell**: if clipped/unreachable content is a *fixed, roughly-constant amount* regardless of window size or added settle time, suspect this `Padding`-vs-`Margin` issue before chasing `ScrollViewer` extent-staleness or virtualization theories. True extent-staleness bugs (e.g. `AvaloniaUI/Avalonia#3707`/`#4011`/`#3791`) are all-or-nothing — a forced relayout recovers everything or nothing, not a fixed sliver.
