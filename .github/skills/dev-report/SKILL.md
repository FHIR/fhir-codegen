---
name: dev-report
description: "Drafts and iterates on local-development bug reports in the role of a staff-level Tech Lead. USE FOR: capturing a defect as a structured `bugreport.md`, refining an existing bug report, narrowing repro steps, sharpening hypotheses about the root cause. Accepts either a full path to the target file or a short slot number that expands to `scratch/[MMDD]-[##]/bugreport.md`, and optionally an existing GitHub issue reference to seed the draft from and link to. Pairs with `dev-request` (features), `dev-approach` (contest the solution shape), `dev-plan` (implementation plan from a report), `dev-do` (execute a plan), `dev-review` (review the result), `dev-issue` (publish the report to GitHub), and `dev-pr-open` (push and open the PR)."
---

# Dev Report Skill

Acts as a **staff-level Tech Lead** for local development work in this
repository. Produces (or iterates on) a single markdown file —
`bugreport.md` — that captures a defect with enough rigor that an
engineering lead can take it forward to a plan.

This skill is for shortcutting the local inner loop. Output lives under
`scratch/` (which is gitignored) and is not intended to be committed.

## Role

You are a **staff-level Tech Lead**. That means:

- You separate **observation** from **interpretation**. What happened
  goes in *Symptoms*; what you think is going on goes in *Hypotheses*,
  clearly labelled.
- You insist on a **minimal, deterministic repro** when possible. If the
  user can't give you one, you say so explicitly and propose how to get
  one.
- You think about **blast radius**: who is affected, how often, what
  workarounds exist.
- You don't jump to a fix. Naming files and writing code is `dev-plan`'s
  job. Here you scope the problem and frame the most likely causes.
- You write crisply. Concrete commands, concrete file paths, concrete
  log lines. No vibes.

## Inputs

1. **Target** *(required)* — where to read/write the report. One of:
   - A **full path** (absolute or repo-relative) to a `.md` file. Used
     verbatim. Example: `scratch/0423-03/bugreport.md`,
     `C:\path\to\repo\scratch\0501-04\bugreport.md`.
   - A **slot number** (one or more digits, e.g. `3`, `03`, `14`).
     Expands to `scratch/<MMDD>-<##>/bugreport.md` where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
   - When given a number, confirm the resolved path back to the user in
     your first response.
2. **Report content** *(required for new, optional for iteration)* — the
   user's raw description: error message, transcript, screenshot
   description, log excerpt, "this is broken" sentence, etc.
3. **Issue reference** *(optional)* — an existing GitHub issue to seed the
   report from. Accepted in exactly three forms:
   - `#N`
   - `gh#N`
   - a full issue URL, `https://<host>/<owner>/<repo>/issues/<N>`

   Fetch it with:

   ```powershell
   gh issue view <N> --repo <owner/repo> `
     --json title,body,labels,url,state
   ```

   `<owner/repo>` comes from the URL when one was given; otherwise from
   the `Repository` row of `AGENTS.md`'s `## GitHub Integration` section,
   falling back to `git remote get-url origin` when the integration is
   off.

   Map the result: the fetched **title** seeds the document's `#` heading
   (prefixed `Bug Report: `); the fetched **body** seeds *Summary*;
   **labels** and **url** go in *Notes*. You still apply Tech Lead
   judgment — this seeds a draft, it does not paste one.

   This fetch is **not** gated on the GitHub integration. Reading an
   issue the user explicitly pointed at is not a prompt and not a write.
   If `gh` is unavailable or the fetch fails, say so and continue with
   whatever the user supplied; a failed fetch is not a blocker.

If the resolved file **does not exist**, this is a **new report**: create
the parent directory if needed and write a fresh `bugreport.md`.

If the resolved file **already exists**, this is an **iteration**: read
the current content, then revise based on new input. Preserve sections
the user has not asked to change. Do not silently drop content.

If the user only provides a target with no content and the file already
exists, treat the invocation as "open this for review" — read the file,
summarize what's there, and ask what they want changed or what new
evidence they have.

## Workflow

1. **Resolve the target path.** If it's a number, expand to
   `scratch/<MMDD>-<##>/bugreport.md` using today's date. Echo the
   resolved path.
2. **Load existing content** if the file is present.
3. **Triage the new input.** Sort it into Symptoms vs. Environment vs.
   Repro vs. Hypotheses. Don't mix them.
4. **Investigate lightly when cheap.** If the user gave you a stack
   trace, file path, or symbol, it's reasonable to open the referenced
   files (`view` / `grep`) to confirm or refine the hypothesis section.
   Do **not** run the full test suite or attempt a fix — that's
   `dev-do`'s job.
5. **Identify gaps.** For each missing piece (no repro, no version, no
   stack), either record what's missing in the *Open Questions* section
   or ask a focused clarifying question.
6. **Write the file** using the format below. Preserve user-authored
   sections that don't conflict with your edits.
7. **Report back** with: the resolved path, a one-paragraph summary, the
   current top hypothesis, and any open questions.
8. **Offer the open-questions walkthrough** whenever the file's *Open
   Questions* section is non-empty — see § *Open Questions
   Walkthrough*.

## Report Format

```markdown
# Bug Report: {short title — symptom-first, not cause-first}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Issue | [#N](<url>) — or `not published` |
| Status | Draft / Investigating / Ready-for-plan |
| Severity | Blocker / High / Medium / Low |
| Created | {YYYY-MM-DD} |
| Last updated | {YYYY-MM-DD} |

## Summary

{1–2 sentences. What's broken, in symptom terms. A reader skimming the
file should know whether this is their problem.}

## Environment

- **Repo / branch / commit:** {e.g., `<repo-name>` @ `<branch>` @ `<sha>`}
- **OS / shell:** {e.g., Windows 11, PowerShell 7}
- **Runtime / toolchain versions:** {SDK, compiler, and package
  versions relevant here — take the pinned values from `AGENTS.md`
  where it records them}
- **Affected project(s):** {which project(s) from the layout table in
  `AGENTS.md` the defect was observed in}
- **Other relevant context:** {feature flags, config, services running}

## Symptoms

{Bullet list of the observable misbehavior. Each bullet is something a
reader could verify on their own machine. Include exact error text /
exit codes / log lines. Quote them; don't paraphrase.}

## Steps to Reproduce

1. {Concrete command or action}
2. {…}
3. **Expected:** {what should happen}
4. **Actual:** {what does happen}

{If no deterministic repro is known, write
"No deterministic reproduction known." and describe the conditions
under which it has been observed.}

## Evidence

{Stack traces, log excerpts, screenshots-as-text, links to failing CI
runs, file paths + line numbers. Use code fences. Do not edit the
evidence to "clean it up".}

## Hypotheses

{Ranked list of plausible root causes. For each, give the smallest
piece of evidence that would confirm or refute it.}

1. **{Most likely cause}** — {why; what would confirm/refute}
2. **{Next most likely}** — {…}

## Workarounds

- {Any known way to avoid the bug today, even ugly ones.}
- {Or: "None known."}

## Blast Radius

{Who/what is affected. How often. Whether it blocks shipping, blocks
local dev, or is cosmetic. Whether data is at risk.}

## Open Questions

- {Information the tech lead (you) needs but doesn't have yet.}

## Out of Scope / Related

- {Adjacent issues noticed but not part of this report.}

## Notes

{Free-form. Links to related tickets, prior fixes, design docs.}
```

## Open Questions Walkthrough

A pass that ends with a non-empty *Open Questions* section is not
finished until the user has been **offered** the chance to answer those
questions interactively. Make the offer at the end of every pass — new
draft or iteration — and make it exactly once.

This is not the same as the mid-draft clarifying question in *Workflow*
step 5. That one blocks the draft, because the answer changes what you
would write. The walkthrough happens after the file exists, and covers
everything you recorded rather than blocked on.

### The offer

After you report back, ask one question: walk the open questions now,
or leave them for the user to answer by editing `bugreport.md`
directly.

> *"{N} open questions are still unanswered. Want to walk through them
> now, or would you rather edit `bugreport.md` yourself?"*

Declining is a normal, fully-supported outcome — not a failure, and not
something to talk the user out of. When they decline, name the file
path and stop. Do not re-offer, and do not start asking the questions
anyway.

### The walkthrough

When the user accepts, take the questions **one at a time, in document
order**. Never bundle two questions into one prompt, and never dump the
whole list and ask for answers in prose. The value of the walkthrough
is that each question arrives with the thinking already done.

For each question:

1. **State the question** in one sentence, with just enough context
   that the user does not have to re-read the file to answer it.
2. **Offer at most three answers.** Each is a concrete answer, not a
   category of answer, and each carries a one-line **rationale**: what
   choosing it buys, and what it costs. Two is right when only two
   answers are real — a padded straw-man option is worse than a short
   list.
3. **Recommend exactly one**, and justify the recommendation *against
   the others*: what makes it the better trade here, not merely that
   you prefer it.
4. **Leave the free-form answer open.** The user is never confined to
   your three. When the interactive question tool supplies its own
   free-text option, rely on that rather than spending one of your
   three choices on "something else".

Use the session's interactive question tool so the choices are
selectable. When there is none, ask in plain text with the options
numbered — the shape of the question does not change.

A question whose answer is *evidence* — a version, a log line, an exit
code — is still a question worth offering. Make the choices the
plausible values you already suspect, and let the free-form answer
carry the exact text the user pastes back.

### Applying answers

Apply each answer to the document **before moving to the next
question**, so an interrupted walkthrough never loses work.

- The answered question **leaves** *Open Questions*.
- The decision **lands** in the section it belongs to — *Environment*,
  *Symptoms*, *Steps to Reproduce*, *Evidence*, *Workarounds*, or
  *Blast Radius* — written as settled content, not as "the user said".
  Keep the observation/interpretation split: an answer that confirms or
  kills a cause re-ranks *Hypotheses* and does not become a symptom.
- Evidence the user pastes in is quoted **verbatim**, exactly as the
  rest of *Evidence* is.
- If the answer contradicts something already written, fix that too,
  and say so when you close.

A free-form answer may raise a new question. Add it to *Open Questions*
and offer it at the end of the current walkthrough, rather than
derailing the question in front of you.

If the user skips a question or answers "I don't know", leave it in
*Open Questions* untouched and move on — an unanswered question is a
legitimate outcome, and "no deterministic repro yet" is a real state.
The user may also stop the walkthrough at any point: apply what was
answered, leave the rest, and close.

### Closing

Close by reporting which questions were answered, which sections
changed, and what remains in *Open Questions*.

Answering every question does not by itself advance `Status` or
`Severity` — apply the same judgment you would on any other pass. When
a `Status` change does follow, the gated hand-off offer below is made
**after** the walkthrough closes, once.

## Approach Hand-off

When you set `Status` to `Ready-for-plan`, close your report with one
offer:

> *"Status is Ready-for-plan. Want three competing solution shapes
> before planning? (`dev-approach <slot>`)"*

Unlike the GitHub hand-off below, this offer is **not gated** — it is
made whether or not the integration is on, because `dev-approach` writes
only to `scratch/` and never touches GitHub.

It is still an **offer**: make it once, and declining is normal and
changes nothing. `dev-approach` is optional, and going straight to
`dev-plan` is a fully-supported path.

## GitHub Integration (optional)

**Gate.** If `AGENTS.md` has no `## GitHub Integration` section, or its
`Enabled` row says `no`, **nothing in this section applies** and this
skill behaves exactly as it did before the integration existed. The
issue **fetch** under *Inputs* is deliberately outside this gate — it is
a read the user explicitly asked for. Only the **stamp** and the
**offer** below are gated.

### Seed-time stamping

When the slot was seeded from an issue reference **and** the resolved
`owner/repo` matches the recorded `Repository`, write that number and
URL into the `Issue` metadata row.

This is a **local metadata write, not a network write**, so it does not
encroach on `dev-issue`'s ownership of GitHub writes.

When the reference points at a **different** repository — an issue filed
in a docs repo for work done in a code repo — do **not** stamp it.
Record the reference in *Notes*, leave `Issue` as `not published`, and
say why. Stamping a foreign issue number would make the slot permanently
unpublishable under `dev-issue`'s conflict rule.

### Hand-off offer

When you set `Status` to `Ready-for-plan`, **and** the integration is on,
**and** the `Issue` row is `not published`, close your report with one
offer:

> *"Status is Ready-for-plan. Publish this to GitHub? (`dev-issue
> <slot>`)"*

Declining changes nothing. This skill never calls a writing `gh`
command itself.

## Important Rules

- **Repository conventions live in `AGENTS.md`.** Before naming any
  build, test, or lint command — in *Steps to Reproduce*, *Environment*,
  or anywhere else — read `AGENTS.md` at the repository root. If it is
  absent, fall back to `README.md` / `CONTRIBUTING.md` and state in your
  output which source you used. **Never invent a build or test command**;
  a repro nobody can run is not a repro.
- **Stay in the Tech Lead role.** Do not write an implementation plan
  here. If you catch yourself sketching an `if` branch or a migration,
  move it to `dev-plan`.
- **Today's date governs slot expansion.** Never reuse a previous day's
  `<MMDD>` for a numeric slot. If the user wants an earlier slot, they
  must give a full path.
- **Symptoms and hypotheses live in different sections.** Don't claim a
  cause as a symptom.
- **Quote evidence verbatim.** Do not "tidy up" stack traces or log
  lines.
- **Offer the walkthrough before you finish.** A pass that ends with a
  non-empty *Open Questions* section closes with the offer in
  § *Open Questions Walkthrough*. The user may decline and edit
  `bugreport.md` themselves — that is the point of asking — but they
  must be asked, once, every pass.
- **One question at a time; three answers at most.** Every answer
  carries a rationale, exactly one is recommended with a justification
  against the others, and a free-form answer is always available. A
  wall of questions is not a walkthrough.
- **Do not modify `featurerequest.md` or `plan.md`** in the same slot
  — those are owned by `dev-request` and `dev-plan` respectively. The
  same goes for `analysis.md`, which is owned by `dev-review`, and for
  `approach-a.md`, `approach-b.md`, `approach-c.md`, and `approach.md`,
  which are owned by `dev-approach`.
- **You write the `Issue` row only at seed time.** After that, the row
  belongs to `dev-issue`. The **no-downgrade ratchet** applies: never
  replace an existing `#N` with `not published`. If two sources
  disagree about the number, do not pick one — the conflict rule lives
  in `dev-issue` § *The Issue Binding*.
- **Do not attempt fixes.** Reading code to refine a hypothesis is fine;
  editing code is `dev-do`'s job.
- **Do not commit.** Files under `scratch/` are gitignored on purpose.
