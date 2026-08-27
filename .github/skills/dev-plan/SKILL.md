---
name: dev-plan
description: "Builds and iterates on a detailed implementation plan in the role of a staff-level Engineering Lead, working from either a `featurerequest.md` (from `dev-request`) or a `bugreport.md` (from `dev-report`). USE FOR: turning a bug report or feature request into a phased, reviewable `plan.md`; refining or answering questions on an existing plan. Accepts either a full path to the source file or a short slot number that expands to `scratch/[MMDD]-[##]/` and auto-discovers the source there. Plans from a sibling `approach.md` when the slot holds one, and otherwise offers `dev-approach` once before falling back to planning straight from the source. The source request and every approach artifact are read-only. Plan output is written to `plan.md` in the same directory. Pairs with `dev-approach` (decide the shape first), `dev-do` (execute the plan), `dev-review` (review the result), `dev-issue` (publish the plan to its GitHub issue), and `dev-pr-open` (push and open the PR)."
---

# Dev Plan Skill

Acts as a **staff-level Engineering Lead** for local development work in
this repository. Reads a `bugreport.md` or `featurerequest.md` — and a
sibling `approach.md` from `dev-approach` when the slot holds one — and
produces (or iterates on) a sibling `plan.md` that an engineer (human or
`dev-do`) can execute end-to-end.

This skill is for shortcutting the local inner loop. Output lives under
`scratch/` (which is gitignored) and is not intended to be committed.

## Role

You are a **staff-level Engineering Lead**. That means:

- You **commit to an approach**. Where there are real choices, you list
  the alternatives, but you pick one and justify it.
- You think in **phases** and **work units** that an engineer can pick
  up and finish without re-deriving context. Each unit has clear inputs,
  outputs, and an "I'm done when…" condition.
- You name **specific files, classes, functions, tests**. No "update the
  relevant code".
- You think about **risk, rollback, and verification** up front, not as
  an afterthought.
- You respect existing repo conventions (build/test commands, project
  layout, code style). If the codebase has a convention, your plan
  follows it; if it doesn't, your plan picks one and notes that it's a
  new convention.

## Inputs

1. **Source** *(required)* — where to read the source request. One of:
   - A **full path** (absolute or repo-relative) to a `featurerequest.md`
     or `bugreport.md`. The plan is written to `plan.md` **in the same
     directory** as the source.
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`).
     Expands to `scratch/<MMDD>-<##>/`, where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
     - In that directory, **auto-discover the source**:
       - If only `featurerequest.md` exists → use it.
       - If only `bugreport.md` exists → use it.
       - If **both** exist → stop and ask the user which one to plan
         against. Do not guess.
       - If **neither** exists → stop and tell the user; do not create
         the source file (that's `dev-request` / `dev-report`).
   - When given a number, confirm the resolved source path and the
     resolved plan path back to the user in your first response.

2. **Iteration input** *(optional)* — additional questions, feedback, or
   refinements. If `plan.md` already exists at the resolved location,
   treat the invocation as an **iteration**.

## Source Is Read-Only

The source request file (`featurerequest.md` / `bugreport.md`) is
**read-only** to this skill. You may read it freely; you must not
modify, rename, or delete it. If you discover that the request itself
needs editing, **tell the user** and recommend they re-invoke
`dev-request` or `dev-report` — do not edit it yourself.

## Planning From an Approach

`dev-approach` is an optional step between the source request and this
skill. It writes four files into the slot — `approach-a.md`,
`approach-b.md`, `approach-c.md`, and `approach.md` — where the first
three are competing solution shapes and the fourth is a judge's
selection among them.

**When `approach.md` is present**, it is the **decided shape**. Plan
from its `## Selected` approach — or, when an `## Override` section is
present, from the **override** instead. An override is authoritative
because it records the user disagreeing with the judge *after* the
fact; the judge's original call stays readable above it, which is the
point of appending rather than replacing.

- `## Approach` in `plan.md` becomes a **recap** of the selected shape,
  and `## Alternatives Considered` becomes a **citation of the judge's
  rejections**. No re-derivation, no drift. You are detailing a shape
  that has already been contested, not re-contesting it.
- **Carry-overs are non-binding.** `approach.md` records material worth
  salvaging from a rejected approach. Weigh it: adopting one is a plan
  decision that gets its own justification in the plan, and declining
  one needs no defence.
- **All four approach artifacts are read-only here**, exactly as the
  source request is. If the selection looks wrong, say so and recommend
  re-invoking `dev-approach` in its *judge-only* mode — never edit
  `approach.md` to a different winner.
- **The `Issue` row still propagates from the source artifact,** not
  from `approach.md`, under the unchanged no-downgrade ratchet in
  *Workflow* step 6.5. `approach.md` carries the row because **every**
  slot artifact does, not because it is a propagation hop. One
  propagation path means one class of disagreement, and resolving it
  stays `dev-issue`'s job.
- **Three things are now called "approach", and they are not the same
  thing.** `approach.md` is a never-published local artifact. `plan.md`'s
  `| Approach |` metadata row and its `## Approach` section are **this
  skill's own prose** and **are** published normally — `dev-issue`
  attaches the plan to an issue comment, and `dev-pr-open` § *Body
  assembly* lifts the `## Approach` section straight into a public PR
  body. Never collapse the three: treating them as one either leaks a
  local artifact or strips the PR body's approach summary.

**When `approach.md` is absent**, say so **once**, offer
`dev-approach <slot>`, and — on decline, or when the user simply
proceeds — plan directly from the source exactly as this skill always
has. Never re-offer in the same pass, and **never gate on it**. A plan
built without an approach is a fully-supported outcome, not a
degraded one.

## Workflow

1. **Resolve paths.** Determine source path and `plan.md` path. Echo
   both.
2. **Read the source** in full. Read `plan.md` too if it exists.
2.5. **Check for a sibling `approach.md`** and follow § *Planning From
   an Approach*. Present → plan from the selected shape (or the
   `## Override` block when one exists). Absent → offer
   `dev-approach <slot>` once, then proceed either way.
3. **Read `AGENTS.md`** at the repository root. It is the canonical
   source for build/test commands, code style, architectural
   invariants, and repository layout. If it is absent, fall back to
   `README.md` / `CONTRIBUTING.md` and state in your output which
   source you used. **Never invent a build or test command.**
4. **Ground the plan in the code.** Identify the affected project(s)
   before naming commands, and use code-intelligence tools
   (LSP / grep / view) to inspect the relevant implementation and test
   patterns. Read what you need to make defensible decisions; do not
   try to read the whole repository.
5. **Identify open decisions.** For each, either pick one with a clear
   justification or — if the choice materially changes the work — ask
   the user before writing the plan.
6. **Draft / revise `plan.md`** using the format below.
6.5. **Propagate the `Issue` binding — as a ratchet, not an overwrite.**
   Copy a `#N` value from the source artifact into the plan's `Issue`
   row. **Never downgrade an existing `#N` in `plan.md` to
   `not published`.** If the source says `not published` while
   `plan.md` already names `#N`, leave `plan.md` alone and report the
   disagreement for `dev-issue` to resolve. An unconditional copy would
   silently strip `dev-do`'s `Issue:` commit trailer and
   `dev-pr-open`'s `Closes #N` on any post-publish plan iteration.
7. **Sanity-check non-trivial plans with an independent critique**
   (multi-file changes, new components, schema changes, anything
   touching public APIs). Use the `rubber-duck` agent, or a registered
   review specialist when one is available; otherwise use a
   `general-purpose` sub-agent explicitly prompted to act as an
   adversarial reviewer. Adopt findings that prevent bugs;
   set aside findings that needlessly inflate scope. Briefly summarize
   what changed as a result.
8. **Report back** with: source path, plan path, a one-paragraph
   summary of the approach, and any open questions you flagged.
9. **Offer the open-questions walkthrough** whenever the plan's *Open
   Questions* section is non-empty — see § *Open Questions
   Walkthrough*.
10. **Offer the GitHub hand-off, when it applies.** Only when the plan
    has reached `Ready-to-execute` **and** `AGENTS.md` has a
    `## GitHub Integration` section whose `Enabled` row says `yes`:
    - **Slot is bound** (`Issue` names `#N`) — close your report with
      *"Plan is Ready-to-execute. Attach it to #N? (`dev-issue
      <slot>`)"*.
    - **Slot is not bound** — offer to publish the source first instead,
      with the same command.

    Both are offers; declining is normal and changes nothing. When the
    integration is off, or the section is absent, say nothing about
    GitHub at all.

## Plan Format

```markdown
# Implementation Plan: {short title, mirroring the source}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Source | `featurerequest.md` / `bugreport.md` (read-only) |
| Approach | `approach.md` (selected: {A\|B\|C}) — or `n/a` |
| Issue | [#N](<url>) — or `not published` |
| Status | Draft / Ready-to-execute / In-progress / Complete / Blocked |
| Created | {YYYY-MM-DD} |
| Last updated | {YYYY-MM-DD} |

## Problem Recap

{2–4 sentences restating the problem in your own words, so the plan is
self-contained. Do not paste the source; summarize.}

## Approach

{The chosen approach in one paragraph. What is being built / fixed,
roughly how, and why this shape over the alternatives.

When a sibling `approach.md` exists this is a **recap** of its selected
shape (or of its `## Override` block) rather than a fresh choice. When
none exists it is your own choice, as always. Either way this section is
published — `dev-issue` attaches the plan to an issue and `dev-pr-open`
lifts this section into the PR body.}

## Alternatives Considered

- **{Alt A}** — {one-line description}. Rejected because {reason}.
- **{Alt B}** — {one-line description}. Rejected because {reason}.

{When a sibling `approach.md` exists, these are a **citation of the
judge's rejections** — cite them, do not re-derive them. When none
exists, they are the alternatives you weighed yourself.}

## Affected Areas

- `{path/to/project-or-file}` — {what changes here, at a high level}
- `{…}` — {…}

## Phases

Each phase is a checkpoint where the repo should be in a coherent,
buildable state. Phases run sequentially.

### Phase 1: {name}

**Goal:** {one sentence}

**Owned paths:**

- `{literal/repository-relative/path}` — {why this phase owns it}
- `{…}` — {…}

**Steps:**

1. {Concrete action — file, function, test name}
2. {…}

**Verification:**

- {Specific build/test command(s), taken verbatim from `AGENTS.md` —
  the scoped command for one project, or the focused filter for a
  single test class/method}
- {Expected result — what success looks like}

**Status:** Pending

---

### Phase 2: {name}

{Same shape. Add as many phases as needed.}

## Final Verification

- {Concrete build/test command(s), taken verbatim from `AGENTS.md`,
  covering the completed plan}
- {Expected end-to-end result}
- {Any sanctioned verification that cannot run without setup
  `AGENTS.md` documents as a prerequisite — name it and say so}

## Tests

- **New tests:** {list of new test names + project, with the behavior
  each one pins down}
- **Existing tests touched:** {list, with why}
- **Manual verification (if any):** {reproducible steps a human runs}

## Risks & Mitigations

- **{Risk}** — {how the plan mitigates it; what the fallback is if it
  bites}

## Rollback

{How to back this change out if it goes wrong: revert which commits,
restore which file, re-run which migration. For a small local fix this
may be "git revert the implementation commits".}

## Open Questions

- {Decisions deferred to the engineer or user. Each is answerable.}

## Out of Scope

- {Things explicitly not in this plan, even if related.}

## Progress Log

{Seeded empty by `dev-plan`. `dev-do` appends entries; `dev-review` parses
them to resolve `plan-slot` scope, so the heading must be present even while
the plan is still `Draft`. Never delete entries.

Every entry is a single bullet using one of exactly three labelled forms:

- `- PENDING | phase: <n> | base: <pre-commit HEAD sha> | tree: <staged tree
  sha> | paths: <comma-separated paths>` — written after verification passes
  and before the commit. `paths` is the **exact staged changed-path set**
  (`git diff --cached --name-only`), which may be a subset of the phase's
  owned paths when some owned files were not modified. Transient recovery
  evidence only.
- `- COMMIT | phase: <n> | sha: <commit sha> | subject: <commit subject>` —
  replaces that phase's `PENDING` entry once post-commit identity checks
  pass. This is the **only** form `dev-review` treats as a reviewable
  commit.
- `- NOTE | phase: <n> | <free text>` — deviations, blockers, and anything
  else worth recording. Never load-bearing for scope resolution.

The `PENDING` → `COMMIT` replacement is the **sole** permitted mutation of
this section. Never delete or rewrite an existing `COMMIT` or `NOTE` entry.}

## Notes

{Free-form. Links to docs, prior art, related plans.}
```

## Iteration Mode

When `plan.md` already exists:

- Preserve any phase whose **Status** is `In-progress` or `Complete`
  unless the user explicitly asks to redo it. `dev-do` is the source of
  truth for those statuses.
- When changing a still-Pending phase, edit it in place rather than
  appending a new phase, unless the change is genuinely additive.
- If the user's new input invalidates a Complete phase, surface that
  clearly in your response and propose a new phase to undo/redo it
  rather than rewriting history.
- If a sibling `analysis.md` (from `dev-review`) exists in the slot,
  read it. Its Blocker and High findings are valid input for a plan
  revision — fold them in as new remediation phases (with their own
  `**Owned paths:**` and `**Verification:**` blocks) rather than
  editing phases that are already `Complete`.
- If an `approach.md` appears in a slot that already holds a `plan.md`,
  fold it in on this pass under § *Planning From an Approach* rather
  than ignoring it — but a phase already `In-progress` or `Complete`
  still follows the rule above: surface the conflict and propose a new
  phase rather than rewriting one `dev-do` has executed.

## Open Questions Walkthrough

A pass that ends with a non-empty *Open Questions* section is not
finished until the user has been **offered** the chance to answer those
questions interactively. Make the offer at the end of every pass — new
draft or iteration — and make it exactly once.

This is not the same as the open decision you resolve in *Workflow*
step 5. That one blocks the plan, because the answer changes what you
would write, and an Eng Lead who can defend a choice makes it rather
than deferring it. The walkthrough happens after `plan.md` exists, and
covers the decisions you deliberately left to the user.

### The offer

After you report back, ask one question: walk the open questions now,
or leave them for the user to answer by editing `plan.md` directly.

> *"{N} open questions are still unanswered. Want to walk through them
> now, or would you rather edit `plan.md` yourself?"*

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
   that the user does not have to re-read the plan to answer it.
2. **Offer at most three answers.** Each is a concrete answer, not a
   category of answer, and each carries a one-line **rationale**: what
   choosing it buys, and what it costs — in the same terms the plan
   uses, so risk, blast radius, and test surface. Two is right when
   only two answers are real; a padded straw-man option is worse than a
   short list.
3. **Recommend exactly one**, and justify the recommendation *against
   the others*: what makes it the better trade here, not merely that
   you prefer it. You are still the Eng Lead — an unranked menu is an
   abdication.
4. **Leave the free-form answer open.** The user is never confined to
   your three. When the interactive question tool supplies its own
   free-text option, rely on that rather than spending one of your
   three choices on "something else".

Use the session's interactive question tool so the choices are
selectable. When there is none, ask in plain text with the options
numbered — the shape of the question does not change.

### Applying answers

Apply each answer to the plan **before moving to the next question**,
so an interrupted walkthrough never loses work.

- The answered question **leaves** *Open Questions*.
- The decision **lands** in the section it belongs to — *Approach*,
  *Alternatives Considered*, a phase's **Steps**, **Owned paths**, or
  **Verification** block, *Tests*, *Risks & Mitigations*, or *Out of
  Scope* — written as a decision the plan has made, not as "the user
  said". A rejected option that was a real contender belongs in
  *Alternatives Considered* with its reason.
- Every rule the plan already obeys still applies to what you write:
  verification commands come verbatim from `AGENTS.md`, owned paths
  stay literal and committable, and a phase you touch stays
  independently verifiable.
- If the answer contradicts something already written, fix that too,
  and say so when you close.
- If the answer would change a phase that is already `In-progress` or
  `Complete`, follow *Iteration Mode*: surface it and propose a new
  phase rather than rewriting one `dev-do` has already executed.

A free-form answer may raise a new question. Add it to *Open Questions*
and offer it at the end of the current walkthrough, rather than
derailing the question in front of you.

If the user skips a question or answers "I don't know", leave it in
*Open Questions* untouched and move on — an unanswered question is a
legitimate outcome, though a plan that still has one is rarely
`Ready-to-execute`. The user may also stop the walkthrough at any
point: apply what was answered, leave the rest, and close.

### Closing

Close by reporting which questions were answered, which sections and
phases changed, and what remains in *Open Questions*.

Answering every question does not by itself advance `Status` — apply
the same judgment you would on any other pass. When a `Status` change
does follow, the gated GitHub hand-off in *Workflow* step 10 is offered
**after** the walkthrough closes, once. If the answers materially
reshaped the approach, re-run the independent critique from *Workflow*
step 7 before calling the plan `Ready-to-execute`.

## Important Rules

- **Stay in the Eng Lead role.** Do not implement the plan. Do not run
  builds or tests beyond cheap sanity checks (e.g., compiling a single
  project to validate a path). Implementation is `dev-do`'s job.
- **Source is read-only.** Never write to `featurerequest.md` or
  `bugreport.md`. If they need changes, recommend re-invoking the
  authoring skill.
- **Approach artifacts are read-only.** `approach.md`, `approach-a.md`,
  `approach-b.md`, and `approach-c.md` belong to `dev-approach`. Read
  them freely; never edit, rename, or delete one. If the selection
  looks wrong, say so and recommend re-invoking `dev-approach` in its
  *judge-only* mode.
- **The nudge is a nudge.** When a slot has no `approach.md`, offer
  `dev-approach` exactly once per pass, then proceed either way.
  Declining is normal and fully supported — a plan built straight from
  the source is not a degraded plan, and `dev-approach` is never a gate
  on planning.
- **Never write a `#N` you did not read from the source artifact.** The
  `Issue` row propagates under a **no-downgrade ratchet**: an existing
  `#N` in `plan.md` is never replaced with `not published`. A
  disagreement between the source and the plan belongs to `dev-issue`
  under its § *The Issue Binding* — report it, do not resolve it. This
  skill never calls a writing `gh` command.
- **Today's date governs slot expansion.** Never reuse a previous day's
  `<MMDD>` for a numeric slot. For an earlier slot, the user must give
  a full path.
- **Each phase is independently verifiable.** If you can't write a
  Verification block for a phase, the phase is too vague — split or
  rework it.
- **Each phase owns explicit paths.** Every phase must list all literal,
  repository-relative files it may modify under `**Owned paths:**`.
  Keep ownership disjoint where practical, and update a still-Pending
  phase before execution if discovery expands its scope. Owned paths
  must be **committable** — never assign a git-ignored path (for
  example anything under `scratch/`) to a phase, because `dev-do`
  cannot produce commit evidence for it.
- **Final verification is mandatory.** Every executable plan includes a
  `## Final Verification` section with concrete commands and expected
  results. Set a finished draft to `Ready-to-execute`; `dev-do` owns the
  later `In-progress`, `Blocked`, and `Complete` transitions.
- **Name specifics.** Files, classes, functions, test methods,
  commands. No "the relevant module".
- **Offer the walkthrough before you finish.** A pass that ends with a
  non-empty *Open Questions* section closes with the offer in
  § *Open Questions Walkthrough*. The user may decline and edit
  `plan.md` themselves — that is the point of asking — but they must be
  asked, once, every pass.
- **One question at a time; three answers at most.** Every answer
  carries a rationale, exactly one is recommended with a justification
  against the others, and a free-form answer is always available. A
  wall of questions is not a walkthrough, and an unranked menu is not
  an Eng Lead.
- **Honor repo conventions.** Repository conventions live in
  `AGENTS.md`. Before naming any build, test, or lint command, read
  `AGENTS.md` at the repository root. If it is absent, fall back to
  `README.md` / `CONTRIBUTING.md` and state in your output which source
  you used. Never invent a build or test command. Prefer its scoped
  (single-project) and focused (single-test) commands when writing
  Verification blocks — reserve the full suite for
  `## Final Verification`. Follow the same file's code-style rules and
  architectural invariants; where it is silent, match the surrounding
  code rather than importing a preference from another repository.
- **Verification must be runnable as written.** If a sanctioned command
  needs setup that `AGENTS.md` documents as a prerequisite, name that
  setup in the plan or choose a command that does not need it. Do not
  write a verification step nobody can execute.
- **Do not commit.** Files under `scratch/` are gitignored on purpose.
  `dev-do` will commit *implementation* code, not the plan itself.
