# Litos Buzzies — demo run sheet

**Chosen path:** Option 1. I quit the stale tray process, back up your Buzzy store, build Release,
relaunch, and then **you** drive while I watch and fix anything that breaks.

**Total time:** about 40 minutes. Demos A–E cover Phases 0–3 in order and each one ends in something
you can *see*. F–I are optional (models, workflows, the new editor, routines).

**Read this first — three things will bite you otherwise:**

1. **The running app is stale.** PID 16552 has been in the tray since 15:18 and holds the *Debug*
   DLLs from before Phase 3. It cannot show the editor, `/buzzy` or folder grants. It has to be quit
   before we start.
2. **`/buzzy` is the only way to create a Buzzy from a sentence.** That is deliberate: the plain chat
   is also the coding agent, so inferring "I want a Buzzy" from a sentence hijacked real coding
   requests. Typing *add an assistant to the checkout page* must reach the model as normal chat.
   Demo D proves that.
3. **A routine only ever gets read-only folder grants.** Nobody is watching when it runs, so its
   writes outside the workspace are refused. Demo E5 shows that on purpose.

---

## Words to say out loud

> Each Buzzy is a persistent coworker with its own identity, memory, private workspace and history —
> not a chat that forgets. Everything you see it do is a real turn against the model.

---

## Before we start (I do this)

*Checked already: your two routines — Scout's "Tick test" and "Model report (phase 2 check)" — are
both **switched off**, so nothing is scheduled and no run is mid-flight. Nothing will be lost by
quitting, and nothing will fire while we demo.*

- Quit PID 16552 from the tray: **Quit Litos (stops routines)**, or tell me to stop it.
- Back up `~/.litos/buzzies` to `buzzies-backup-<date>` (folders **and** `buzzies.db`) and record the
  file count and hash, so we can prove the backup is whole.
- Build Release and relaunch, so what runs is the code we just wrote.

**Your store today:** buddy, builder, scout, plus `buzzies.db`. Everything we make is new; demo J
deletes its own Buzzy at the end.

---

## The demos

### A. The sidebar, and who you are talking to — *Phase 0*
1. Look at the left column: **Litos chat** (plain coding agent), then **MY BUZZIES**: Buddy, Builder, Scout.
2. Click **Scout**. The title becomes `LitosAiAgent — Scout`, the transcript is Scout's own, the PTC
   button reads **PTC: off for Buzzies** and is disabled, and the sidebar marks Scout as selected.
3. Say: *Who are you, and what can you do that the plain chat can't?*
   → It answers as Scout, in Scout's voice, from its own SOUL.md.

**Say:** the session that opened is Scout's, not the plain chat's. Come back tomorrow and it is still Scout's.

### B. Two Buzzies, two memories (and it survives a restart) — *Phase 0*
1. Select **Buddy**, say: *Remember that I like everything as short bullet lists.*
2. Select **Scout**, say: *Remember that I care most about licence terms.*
3. **Close the window and relaunch.** (Closing only hides to the tray if a routine is
   *switched on* — `HasEnabledRoutines` — so with none on, Close really quits. If it vanishes
   instead, the tray icon is holding it: right-click it and choose **Quit Litos (stops routines)**.)
4. Ask Buddy *How would you summarise a long report for me?* → bullet lists.
   Ask Scout the same → prose that leads with licence terms, and it does not know Buddy's preference.
5. Right-click either → **Open memory file** → plain dated Markdown, e.g. `- 2026-11-26 (chat): …`.

**Say:** two memories, two files, and they outlive the process.

### C. The workspace wall — *Phase 0, and the guard everything else builds on*
1. Scout: *Read C:\Windows\win.ini and tell me what is in it.*
   → refused, with the guard's message naming the workspace it was allowed to use.
2. Right-click Scout → **Open workspace folder**. Drop any file in.
3. Scout: *List your workspace and read the file you find.* → it reads it.

**Say:** the refusal is the product, not a bug. Every file tool is wrapped by the same guard, and that
is what makes an unattended Buzzy safe to leave running.

### D. It will not guess — *Phase 3, the change you asked for*
1. In the **plain Litos chat**, type exactly: `add an assistant to the checkout page`
   → a normal coding reply. **No creation card appears.** *(This is the sentence that used to be hijacked.)*
2. Now type `/buzzy` with nothing after it and press Enter → it says what to type, and names
   **+ Create a Buzzy**.
3. Type: `/buzzy Penny reads my invoices folder and writes a weekly summary.`
   → **Create this Buzzy?** card: name Penny, a template, the tools it chose, a soul it wrote, and the
   folders it *would like*. Note the line **Nothing is saved until you press Create.**
4. Change the name, or click a tool chip off and on, then press **Create Penny**.

**Say:** the model drafts, you decide. It can only offer tools from the grantable list — no MCP, no code
kernel, ever — and it cannot grant itself a folder: a path it invents is only a description until you
pick the real folder in the dialog.

### E. Grants: read is not write, and routines get less — *Phase 3*
1. Ask Penny to read a file from **your Documents** (a folder she was not granted) → refused.
2. Right-click Penny → **Folders and model…** → **Grant a folder…** → pick that folder, leave it
   read-only → **Done**.
3. Ask again → she reads it.
4. Ask her to *save a change* in that folder → refused, because the grant is read-only.
5. *(optional, strongest)* Give Penny a routine that writes into that folder. The run reports it could
   not: a routine only ever gets read-only grants.

**Say:** one guard, two rule sets. Interactive work gets what you granted; unattended work gets less,
because nobody is watching.

### F. Two Buzzies, two models — *Phase 3*
1. Right-click **Builder** → **Folders and model…** → pick a specific model → **Done**.
2. Leave **Scout** on **Follow the app's current model (/model)**.
3. Switch between them: the status bar shows the model that will *actually* be used.
4. Change `/model` in the plain chat → Builder keeps its own; Scout moves with it.

### G. Learn this workflow — *Phase 2*
*Buddy is used here because action row is Buzzy-only, and this one publishes a link (do H first if
you would rather watch the editor with a skill already in it).*
1. **Buddy**: *Write a haiku about bees, save it as poems/bees.md, and publish it as an artifact.*
2. When it finishes, a row of actions appears under the reply: **Open artifact**, **Remember this
   workflow**, **Repeat every…**.
3. Click **Open artifact** → the page opens in your browser.
4. Click **Remember this workflow** → **Remember this workflow — Buddy** shows a drafted SKILL.md
   (Inputs, Steps, Tools, Output, Checks, Limitations) → press **Save workflow**.

**Say:** it drafted a reusable skill from what it just did — and it never saw the tool *results*, so a
web page cannot write instructions into a skill that later runs unattended.

### H. Editing soul, memory and skills — *Phase 3, new*
1. Right-click Buddy → **Soul, memory and skills…** → title **Buddy: soul, memory and skills**, with
   three tabs: **Soul**, **Memory**, **Skills**.
2. **Skills** tab → the skill you just saved is listed → click it, change a step, press **Save**.
3. **Soul** tab → change a line, press **Save**, then ask Buddy something and hear the voice change.
   Press **Revert** to put it back.
4. With an unsaved edit, press **Close** → it warns before discarding.

**Say:** these are the same plain files the Buzzy reads — no database, no lock-in. Editing a skill is
*not* an approval: if a routine is pinned to version 1, version 1 is what it keeps running.

### I. Routines, the tray and the catch-up rule — *Phase 1* *(optional, ~10 min)*
1. Right-click Scout → **Routines…** → new routine, **every 2 minutes**: *Write a short note in
   reports/ with the time and one thing you checked.* → **Create routine**.
2. **Run now** → it runs; the wings beat; the history fills in.
3. Close the window → **it hides to the tray**, with a notification explaining why rather than quitting.
4. Wait for the next tick → a notification appears when the run finishes; click it to open that run's
   conversation.
5. Set the routine's limit low (e.g. 10 thousand tokens) and give it a big job → it stops itself with
   *Stopped: the run used more than its budget*.

### J. Clean up
- Delete Penny and the demo routine (right-click → **Delete…**), or keep them.
- I restore the backup if anything looked wrong.
- Tray → **Quit Litos (stops routines)** when we are done, so nothing is left running.

---

## What to watch for, and what to tell me

Tell me immediately if any of these happens — each one is a bug I want, not an interruption:

- A creation card appears when you did **not** type `/buzzy`.
- A file tool succeeds outside the workspace, or a read-only grant allows a write.
- **Soul, memory and skills…** shows an empty box for a file that has content.
- **Save** produces a skill the Buzzy then cannot load.
- A routine runs with a write grant.
- Anything is written into `~/.litos/buzzies` that you did not ask for.

## What this demo does not cover

- **Gmail** (Phase 4, not built).
- **Vision and OCR on attachments**, and the beta hardening items in Phase 5.
- **Two Buzzies working at the same time** — intentionally serialised.