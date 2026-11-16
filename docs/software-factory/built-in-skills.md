# Software Factory — built-in skills

Built 2026-11-13. The factory ships seven development-practice skills in its library: **grill-the-request**, **tdd**, **root-cause-debugging**, **verify-before-submit**, **review-rubric**, **address-feedback** and **safe-refactor**. Before this, a new factory's library was empty.

Design and trial results (research, before/after for review and handoff, the live trial on garmin-mcp-server): https://claude.ai/artifact/697ytw11xPBFdQUxjgVkSX

## 1. Decisions (user, 2026-11-13)

- **These seven are the first set**, as tried on the installed factory (library revision 4 there). Three more are for later: lessons-writing, dependency-hygiene, spec-quality.
- **An Admin can edit any built-in skill, and reset it to the shipped text.** One an Admin removes can be added back, and is never added again by the factory on its own.
- **Skills aid the existing request-to-handoff workflow and never override it.** The turn's brief owns the process: tools, allowance, review depth, what counts as evidence, and how the turn finishes. A skill only adds craft inside it. Each skill's first lines name the turns it applies to and say that the brief wins.
- **The review skill stays in step with the review process.** `ReviewPlanner` still picks none, light or full; `review.md` and `review-light.md` still set the list and the allowance. review-rubric adds no checks and no reading; it grades findings and words them.

## 2. Defaults

| Skill | Applies in | Default for | Characters |
| --- | --- | --- | --- |
| grill-the-request | spec and scan turns | bug, feature, refactor | 1,829 |
| tdd | work turns | bug, feature | 2,147 |
| root-cause-debugging | bug work turns, repair turns | bug | 1,815 |
| verify-before-submit | before every `submit_work` | all four | 2,253 |
| review-rubric | review turns | all four | 1,849 |
| address-feedback | rework and review-repair turns | all four | 1,188 |
| safe-refactor | refactor work turns | refactor | 1,274 |

Defaults per type against a task's 12,000-character limit: bug 11,081, feature 9,266, refactor 8,393, chore 5,290.

## 3. How it works

- **Shipped text.** `src/Litos.SoftwareFactory.Core/Skills/BuiltIn/<name>.md`, embedded like the brief templates. Frontmatter: `name`, `version`, `description`, `defaultFor`. `BuiltInSkills` reads them. **Raise `version` whenever a skill's text changes**, or factories that already have it are not updated.
- **Library fields.** `LibrarySkill.BuiltIn` names the built-in a skill came from; `BuiltInVersion` and `BuiltInHash` say which shipped version it was stamped from and that version's content hash. `BuiltInSkills.ContentHash` covers description, instructions and default task types. Name and the on/off switch are an Admin's to change without the skill counting as edited. `SkillSettings.Offered` lists the built-ins this factory has offered; it only grows.
- **On every host start** (`SettingsSeeding.SyncBuiltInSkillsAsync`, then `BuiltInSkillSync.OnStart`):
    - a built-in not in `Offered` is added, enabled, with its defaults, except a default type it would push past 12,000 characters (the log says which);
    - a library skill of the same name is the Admin's and is left alone, unless its text is the built-in's exactly, when it is marked as that built-in (a factory that had the skills added by hand, as the trial factory did);
    - a built-in nobody edited moves to a newer shipped version; an edited one keeps its text.
- **On every save** (`PUT /api/admin/settings/skills`, `BuiltInSkillSync.OnSave`): a built-in that reads as shipped is stamped as the current version (that is how "Reset to shipped" lands); an edited one keeps the version it had. Version, hash and `Offered` come from the host, never from the request.
- **The Skills tab** gets the shipped texts in `GET /api/admin/settings` (`builtInSkills`). A built-in card shows **Built in** with **As shipped**, **Modified** or **Version N is available**, a **Reset to shipped** button and the shipped text to compare. Removed built-ins are listed under **Built-in skills not in the library**, with **Add back**.
- **The brief wins, in code.** `FactorySystemPromptProvider.BriefWins` is said before the task's skills in every system prompt, so no edit to a skill can remove the precedence rule.

## 4. Keeping skills in step with the process

The skills name the completion tools, their fields and brief headings: `submit_work`, `knownLimitations`, `settledBy`, under "Verification the factory will run". Nothing generates them from the code, so `tests/Litos.SoftwareFactory.Worker.Tests/BuiltInSkillsMatchTheProcessTests.cs` fails when a skill names a tool or field that no completion tool's schema has, or points to a brief heading that no template has. **Renaming a tool field or a brief heading means updating the skills that name it, and raising their version.**

## 5. Tests

- Core: `BuiltInSkillsTests` (catalog, defaults within the limit, every skill defers to the brief, parsing, hashing) and `BuiltInSkillSyncTests` (seeding, removal, adoption, upgrades, edits, the size limit, save stamping).
- Host: `BuiltInSkillsHostTests` (start, restart, save stamping and the catalog in the settings API).
- Worker: `BuiltInSkillsMatchTheProcessTests`, and the system prompt carries `BriefWins`.
- Web: `Skills.test.tsx`, "built-in skills on the Skills tab".

## 6. Open items

- Two lessons from the trial's bug task are drafted for version 2 of two skills, not shipped: address-feedback should not keep wrong behaviour "for tolerance", and verify-before-submit should put code, patterns and escapes in backticks in handoff text. See the design doc.
- Factory gaps the trial found (in the design doc's checklist), among them: `spec.md` has no "the code isn't here" exit; the work brief does not show a step's working folder or report paths; coverage reads 100% when no changed file is in any report; a one-line regex fix gets no review.
