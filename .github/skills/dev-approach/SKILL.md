---
name: dev-approach
description: "Explores three competing solution shapes for one request in the roles of three isolated staff-level Engineering Leads, then has a fourth skeptical judge sub-agent select one on the record. USE FOR: deciding a solution's shape before any plan exists; regenerating, refining, or re-judging an existing set of approaches. Accepts either a full path to a `featurerequest.md` / `bugreport.md` or a short slot number that expands to `scratch/[MMDD]-[##]/` and auto-discovers the source there. Optional `max_subagents` (default 3) caps parallel sub-agent fan-out. The source is read-only; it owns `approach-a.md`, `approach-b.md`, `approach-c.md`, and `approach.md`, builds nothing, and never publishes to GitHub. Pairs with `dev-request`/`dev-report` (capture the ask), `dev-plan` (plan from the selection), `dev-do` (execute it), `dev-review` (review it), and `dev-pr-open` (push and open the PR)."
---

# Dev Approach Skill

Acts as **three isolated staff-level Engineering Leads** and one
**skeptical judge** for local development work in this repository.
Reads a `featurerequest.md` or a `bugreport.md`, produces three
independently-authored solution shapes — `approach-a.md`,
`approach-b.md`, `approach-c.md` — and then a judgment, `approach.md`,
that selects one of them on the record.

This skill sits between `dev-request` / `dev-report` and `dev-plan`. It
exists because the moment a solution's shape is cheapest to change is
*before* any plan exists, and because an agent that is about to write
the plan cannot credibly contest the shape it is about to detail.

The step is **optional**. A slot with no `approach.md` gets exactly the
`dev-plan` behavior it had before this skill existed.

This skill **builds nothing**. It writes no source, touches no branch,
runs no build or test command, and never stages, commits, or pushes.
Output lives under `scratch/` (which is gitignored) and is never
published to GitHub.

## Roles

This skill plays **four** roles: three authors who never see one
another, then a judge who never authors.

All four are **staff-level Engineering Leads** — the same role
`dev-plan` uses — and all four are bound by the conventions and
architectural invariants documented in `AGENTS.md`. An approach that
violates a documented invariant is not a valid approach, whatever axis
it was optimizing for.

### Author A — minimum change

You are looking for the **smallest change that satisfies the request**.
Your concerns:

- Fewest files touched, fewest new concepts introduced, least new
  surface area to maintain.
- Reuse of what already exists, even when the existing thing is an
  imperfect fit.
- Shipping today. A shape that lands in one sitting beats a shape that
  lands correctly next month.

You are **explicitly allowed to be inelegant**. Duplication, a
special-case branch, or a slightly wrong home for a piece of logic are
legitimate costs for you to pay — but you must **name them** in
*Costs & Trade-offs* rather than pretending they are free.

### Author B — cleanest architecture

You are looking for the **right layering and the right boundaries**.
Your concerns:

- Each responsibility lives where it belongs; no leaking of internals
  across a boundary.
- The abstraction the problem actually wants, not the one that happens
  to be nearby.
- A shape a new maintainer can reason about six months from now without
  reading the history.

You are **explicitly allowed to be larger**. A bigger diff, a new
component, or a refactor of adjacent code is a legitimate cost for you
to pay — but you must **name it**, and you must say what it buys.

### Author C — unconstrained

You are told **what A and B are optimizing for** — minimum change and
cleanest architecture respectively — so that you do not duplicate
either. You are **never shown their output**.

Your job is to find the shape neither constraint would reach: reframing
the problem, solving it somewhere else entirely, buying it instead of
building it, deleting something so the problem stops existing, or
solving a slightly different problem that makes the stated one moot.

You are bound by `AGENTS.md` exactly as A and B are. "Unconstrained"
means unconstrained by *their axes*, not by the repository's rules.

### The Judge — skeptical reader

You read the three approaches **as written** and select exactly one.
Your concerns:

- **Attack the claims.** Every approach self-reports its blast radius,
  complexity, risk, and reversibility. At least one of those
  self-assessments is optimistic. Find it and say so.
- **Judge against the request**, not against your taste. The selected
  approach is the one that best serves the source's *Goals* under its
  *Non-Goals*, at a cost the request justifies.
- **Justify comparatively.** "A is good" is not a verdict. "A over B,
  because B's cleanliness is paid for in a migration the request
  explicitly rules out" is.

You **never author a fourth design**. You do not average the three, do
not split the difference, and do not blend them. Anything worth
salvaging from a rejected approach is recorded as a **non-binding
carry-over** for `dev-plan` to weigh — it is material, not part of the
selection. Your skepticism only stays honest while you have no design
of your own to defend.

## Inputs

1. **Source** *(required)* — where to read the request. One of:
   - A **full path** (absolute or repo-relative) to a
     `featurerequest.md` or `bugreport.md`. The approach files are
     written **in the same directory** as the source.
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`).
     Expands to `scratch/<MMDD>-<##>/`, where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
     - In that directory, **auto-discover the source**:
       - If only `featurerequest.md` exists → use it.
       - If only `bugreport.md` exists → use it.
       - If **both** exist → stop and ask the user which one to work
         from. Do not guess.
       - If **neither** exists → stop and tell the user; do not create
         the source file (that's `dev-request` / `dev-report`).
   - When given a number, confirm the resolved source path and all four
     resolved output paths back to the user in your first response.

2. **`max_subagents`** *(optional, default `3`)* — maximum number of
   sub-agents to run in parallel at any given time. This is a
   **concurrency** ceiling, not a total ceiling. `1` serializes the
   three authors — they run one after another, still as independent
   invocations that never see one another's work. Values above `3`
   have no effect, because the author fan-out is fixed at three.

3. **Optional focus** — free-form constraints or context from the user
   ("this has to ship this week", "assume the storage layer is being
   replaced anyway"). Give it to **all three authors equally**,
   weighted the same way. Focus text steers the space the authors
   search; it never pre-selects a winner, and it is never given to one
   author and withheld from another.

## Source Is Read-Only

The source request file (`featurerequest.md` / `bugreport.md`) is
**read-only** to this skill. You may read it freely; you must not
modify, rename, or delete it. If you discover that the request itself
needs editing, **tell the user** and recommend they re-invoke
`dev-request` or `dev-report` — do not edit it yourself.

The same applies to any sibling `plan.md` or `analysis.md`. This skill
writes exactly four files and no others.

## Workflow

1. **Resolve paths.** Determine the source path and all four output
   paths (`approach-a.md`, `approach-b.md`, `approach-c.md`,
   `approach.md`). Echo every one of them.
2. **Read the source in full**, then read `AGENTS.md` at the repository
   root. `AGENTS.md` is the canonical source for repository
   conventions, code style, and architectural invariants; every author
   and the judge are bound by it. If it is absent, fall back to
   `README.md` / `CONTRIBUTING.md` and state in your output which
   source you used. **Never invent a build, test, or lint command** —
   and note that this skill does not run one in any case.
3. **Re-invocation check.** If any `approach*.md` already exists in the
   slot, stop and follow § *Re-Invocation Modes* before doing anything
   else. Do not overwrite silently.
4. **Write `approach.md`'s skeleton, then run the triviality check.**
   Before the triviality proposal is resolved and before any fan-out,
   write `approach.md` carrying nothing but its metadata table — with
   `| Selected | {TBD: judgment pending} |` — an empty `## Notes`
   section, and the in-progress line § *Judgment File Format*
   prescribes. Record the step-3 re-invocation-mode decision and this
   step's triviality decision into that `## Notes` section as each one
   is made. Then form a view on whether the request warrants three
   approaches, and follow § *Triviality Check*. That view is a proposal
   to the user, never a decision you make alone.

   The skeleton is written here because both decisions this stage makes
   happen at steps 3 and 4, while their designated home is not written
   until step 7. A hand-back in between — an author or the judge
   failing, which is exactly the outcome a fan-out designs for — loses
   both from disk, and leaves a resumed run under-reporting what it had
   already decided.
5. **Fan out the three authors** as isolated sub-agents, honoring
   `max_subagents`. Each writes its own file directly. See
   § *Sub-Agent Use*.
6. **Run the judge** as a separate sub-agent, only after all three
   authors have finished. The judge reads the three files from disk and
   returns a verdict; it does not write anything.
7. **Fill in `approach.md`** yourself, transcribing the judge's verdict
   into the format below **on top of the skeleton step 4 already
   wrote**: replace the `{TBD: judgment pending}` row with the
   selection, drop the in-progress line, add the judgment sections, and
   **preserve whatever `## Notes` already holds** by appending to it
   rather than writing the file from nothing. The orchestrator owns
   this file — the metadata table, the `Issue` row under its
   no-downgrade ratchet, and the placement of any later `## Override`
   block are contracts the judge is not responsible for.
8. **Offer the user an override.** See § *User Override*. Declining is
   the normal outcome and changes nothing.
9. **Report back** with: the resolved source path, the four output
   paths, the selected approach and a one-line reason, the count of
   disbelieved claims, and any carry-overs. Close by offering
   `dev-plan <slot>` as the next step.

## Approach File Format

Each author writes exactly one file — `approach-a.md`, `approach-b.md`,
or `approach-c.md` — in this format:

```markdown
# Approach {A|B|C}: {short title naming the shape, not the request}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Source | `featurerequest.md` / `bugreport.md` (read-only) |
| Issue | [#N](<url>) — or `not published` |
| Optimizing for | minimum change / cleanest architecture / unconstrained |
| Created | {YYYY-MM-DD} |

## Shape

{2–4 sentences. The idea, stated so a reader can hold it in their head.
If it takes a page to say what the shape is, it is not yet a shape.}

## How It Works

{The mechanism. Concrete enough to argue with: which components, which
boundaries, what talks to what, what the flow looks like end to end.}

## What Changes

- `{path/to/file-or-area}` — {what changes here, at a high level}
- `{…}` — {…}

{Name real paths. "The relevant module" is not a shape, it is a hope.}

## Claims

- **Blast radius:** {how much of the repository this touches, and what
  is downstream of it}
- **Complexity:** {what a maintainer has to understand that they do not
  have to understand today}
- **Risk:** {what is most likely to go wrong, and how it would show up}
- **Reversibility:** {how hard this is to back out once shipped}

## Costs & Trade-offs

- {What this approach knowingly pays. Name it plainly — the judge will
  find it anyway, and an unnamed cost reads as an oversold claim.}

## What This Rules Out

- {Options this shape forecloses, and options it keeps open. This is
  where a large-but-flexible shape earns its size.}
```

The `## Claims` list is **load-bearing and fixed**: exactly those four
bullets, in that order, in every author file. They are the falsifiable
self-assessments the judge attacks. An author that omits one, or
replaces it with a vaguer heading, has removed the judge's grip on it.

The `Issue` row appears on **all three** author files. `dev-issue`
defines the binding as belonging to *every* slot artifact and names
itself the only step that back-fills it, so carrying the row makes
these files covered by that existing rule with no change to
`dev-issue`. Stamp it from the source under the **no-downgrade
ratchet**: copy an existing `#N`, never replace a `#N` with
`not published`, and never invent one.

## Judgment File Format

You — the orchestrator, not the judge — write `approach.md`:

```markdown
# Approach Selection: {short title, mirroring the source}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Source | `featurerequest.md` / `bugreport.md` (read-only) |
| Issue | [#N](<url>) — or `not published` |
| Selected | {A / B / C} — {short title of the selected approach} |
| Mode | contested / collapsed |
| Created | {YYYY-MM-DD} |

## Selected

{One paragraph. Which approach won and what shape the plan is therefore
being built on. A reader who stops here should know what happens next.}

## Why This One

{Justified **against** the other two, one comparison at a time. Not
"A is simple" but "A over B because…" and "A over C because…". If a
comparison cannot be made, the approaches were not different enough,
and that is worth saying.}

## Claims I Did Not Believe

- **{Approach} — {which claim}:** {why the self-assessment was
  optimistic, and what the honest version looks like.}

{At least one entry. Three approaches that all assessed themselves
accurately is a finding in itself — say so explicitly rather than
leaving the section empty.}

## Carry-Overs (non-binding)

- **From {approach}:** {what is worth keeping, and why it survives the
  rejection of the approach it came from.}

{**Non-binding.** These are material for `dev-plan` to weigh, not part
of the selection. `dev-plan` adopting one is a plan decision that gets
its own justification; declining one needs no defence.}

## Rejected

### Approach {X}: {title}

{One paragraph. What it got right, and the specific reason it lost —
stated so a later "why didn't we just do X?" has a written answer.}

### Approach {Y}: {title}

{Same shape.}

## Notes

{Free-form. Ordering effects, an approach that was closer than it
looks, anything the judge flagged that does not fit above.}

## Override

{**Present only when the user disagrees with the selection.** Appended
below the judge's call, never in place of it.

- **User selected:** {A / B / C}
- **Reason:** {the user's reason, in their terms}
- **Recorded:** {YYYY-MM-DD}

When this section is present it is **authoritative** — `dev-plan` plans
from it — and everything above it stays readable, so the disagreement
survives on the record.}
```

**A file with no `## Selected` section is an in-progress skeleton, not
a selection.** Workflow step 4 writes that skeleton before any fan-out,
so this stage's decisions have a durable home from the moment they are
made; step 7 fills the judgment in on top of it. The skeleton carries
the metadata table with `| Selected | {TBD: judgment pending} |`, an
empty `## Notes` section, and — immediately under the title, where no
reader can miss it — this line:

```text
> **In progress.** The judge has not run yet. This file is not a
> selection; do not plan from it.
```

The marker is not decoration. `dev-plan` tests for the **presence** of
`approach.md` to decide it has a decided shape to plan from, so a
skeleton left behind by a hand-back between steps 4 and 7 would
otherwise read as a selection with nothing to select. The missing
`## Selected` heading is the machine-readable half of the answer; the
line above is the half a human sees first.

## Sub-Agent Use

- **The three authors run as isolated sub-agents**, in parallel where
  `max_subagents` allows. Each one is given: the full source text, the
  conventions and architectural invariants from `AGENTS.md`, the user's
  focus text if any, its own axis, and its own output path — and
  nothing else.
- **Each author is explicitly forbidden** from reading, globbing for,
  listing, or writing any `approach*.md` other than its own. This is
  the whole point of fanning out: if the authors collapse into one, you
  get one idea with the illusion of three.
- **Author C is steered by exclusion, not by example.** Tell it what A
  and B are optimizing for so it does not duplicate them. Never show it
  their output, and never paraphrase their output to it.
- **Each author writes its own file directly.** You do not relay
  drafts, and you do not edit an author's file into shape afterwards —
  an orchestrator who rewrites the three has authored a fourth.
- **The judge is a separate sub-agent in every case**, including when
  `max_subagents` is `1`. It runs only after all three authors have
  finished; it reads the three files from disk; it is **not told which
  axis produced which file**, so it must attack the claims each file
  makes about itself rather than the label on it.
- **The judge never writes.** It returns a verdict; you transcribe it
  into `approach.md`. A judge that owns the file is one prompt away
  from editing its own verdict into a fourth design.
- **Honor `max_subagents`.** Never run more than `max_subagents`
  sub-agents concurrently. Serializing changes the wall clock, never
  the isolation.

## Triviality Check

Before fanning out, read the source and form a view on whether it
warrants three approaches.

When you judge it trivial, **say so once, with your reason**, and offer
to produce a single approach instead. This is a **proposal, never a
decision**: the user accepts or declines, and declining runs the full
three-way fan-out. Never collapse silently, and never re-offer in the
same pass.

The known cost is that a triviality call made *before* any design work
is itself a claim about the solution's shape. Keeping the decision with
the user, and requiring the reason to be stated, is what bounds it.

A collapsed slot has the **same file shape** as a contested one:

- `approach-a.md` is still written, in the full format above.
- `approach-b.md` and `approach-c.md` are **not** written.
- The judge still runs, and trivially selects the one approach it was
  given.
- `approach.md` records `Mode: collapsed`, names the stated reason in
  its *Notes*, and leaves `## Rejected` empty with a one-line note that
  there was nothing to reject.

Downstream skills therefore have one contract, not two, and the
triviality claim lands where a later reader can see it was a choice
rather than an oversight.

## Re-Invocation Modes

**First, rule out an interrupted first run.** An `approach.md` with no
`## Selected` section and no author files beside it is the skeleton
workflow step 4 writes, left behind by a hand-back before the judge
ran. That is an **interrupted first run**, not a re-invocation:
continue from step 4 without prompting. None of the three modes below
describes it, because all three presuppose that `approach-a.md`,
`approach-b.md`, and `approach-c.md` already exist.

Otherwise, when the slot already holds one or more `approach*.md`
files, **do not guess what the user meant**. Report what you found —
which files exist, what `approach.md` currently selects — and offer
exactly three modes:

- **regenerate** — discard the existing three and author them from
  scratch. For when a new constraint arrived and the old space is the
  wrong space. Full isolation, as on a first run.
- **refine** — revise the existing three in place. For when an approach
  was thin rather than wrong. State plainly that this **spends part of
  the isolation guarantee**, because a refining author sees its own
  prior draft. The mode exists because preserving hand edits is
  sometimes worth that, not because the trade is free.
- **judge-only** — re-run the judge against the existing three,
  unchanged. For when the authors were fine and the judge called it
  wrong.

Every mode rewrites the **judgment** in `approach.md`, and every mode
**preserves** its `## Notes` section. That section is where this
stage's own decisions are recorded as they are made, so a rewrite that
discarded it would destroy the record a resumed run rebuilds from. An
existing `## Override` block is different, and does **not** survive a
re-judgment: the user's disagreement was with a verdict that no longer
exists. Say so before you overwrite, and re-offer the override
afterwards.

This follows the precedent `dev-plan` sets for a slot holding both a
`featurerequest.md` and a `bugreport.md`: stop and ask.

## User Override

After writing `approach.md`, offer the user the chance to disagree.
Make the offer once, in one sentence, naming the selection and the
alternatives:

> *"Selected {X}. If you'd rather build on {Y} or {Z}, say which and
> why and I'll record it."*

Declining is the normal, fully-supported outcome. When the user
declines, name the file path and stop.

When the user does disagree, **append** an `## Override` section below
the judge's call recording their choice and their reason. Never edit
the judge's selection, never rewrite `## Why This One` to agree, and
never delete the reasoning that led to the rejected verdict. When an
`## Override` block is present it is **authoritative** and `dev-plan`
follows it, while the judge's original call stays readable above it —
so the disagreement survives on the record rather than being
overwritten by it.

An override is a selection among the three approaches **as written**. A
user who wants a fourth shape wants a *regenerate*, and a user whose
reason is really a new constraint wants a revised source request — say
so and offer the right tool rather than recording a fourth design in an
`## Override` block.

## Important Rules

- **Source is read-only.** Never write to `featurerequest.md` or
  `bugreport.md`. If the request itself needs changing, recommend
  re-invoking `dev-request` / `dev-report`. The same holds for a
  sibling `plan.md` or `analysis.md` — this skill writes four files and
  no others.
- **Today's date governs slot expansion.** Never reuse a previous day's
  `<MMDD>` for a numeric slot. For an earlier slot, the user must give
  a full path.
- **Three isolated authors, then a judge.** The isolation is the
  product. An author that sees a sibling's file, or a judge that reads
  the three with the axis labels attached, produces the appearance of a
  contest without the contest.
- **Isolation is never traded away silently.** *refine* mode is the
  only mode that spends part of it, and it says so out loud before the
  user picks it.
- **The judge never authors a fourth design.** It selects one of the
  three as written. It does not average them, blend them, split the
  difference, or send one back for revision.
- **Carry-overs are non-binding.** They are material for `dev-plan` to
  weigh. Adopting one is a plan decision that gets its own
  justification; declining one needs no defence.
- **The orchestrator owns `approach.md`.** The judge returns a verdict;
  you transcribe it. The metadata table, the `Issue` row, and the
  placement of any `## Override` block are yours.
- **An override is appended, never substituted.** Both calls stay
  readable, and the one below wins.
- **A collapsed slot has the same file shape as a contested one.**
  `approach-a.md`, a real judgment, and `Mode: collapsed` with the
  stated reason. Downstream skills get one contract, not two.
- **`approach*.md` is never published to GitHub.** Not as an issue, not
  as a comment, not as a quotation in a PR body. These are internal
  artifacts, exactly like `analysis.md`. A shape worth publishing
  reaches GitHub through `plan.md`, which `dev-issue` attaches.
- **Populate the `Issue` row, never invent it.** Read it from the
  source artifact under the **no-downgrade ratchet**: never replace an
  existing `#N` with `not published`. Report a disagreement rather than
  resolving it — that belongs to `dev-issue` under its § *The Issue
  Binding*. This skill never calls a writing `gh` command.
- **Nothing is built, staged, committed, or pushed.** No source is
  written, no branch is touched, no build or test command is run.
  Output lives under `scratch/`, which is gitignored on purpose.
- **The step is optional.** A user who skips it gets the `dev-plan`
  behavior they had before this skill existed. Never present
  `dev-approach` as a gate on planning.
- **Honor repo conventions.** Repository conventions live in
  `AGENTS.md`. Read it before any author or the judge starts, and bind
  all four roles to its code-style rules and architectural invariants.
  If it is absent, fall back to `README.md` / `CONTRIBUTING.md` and
  state in your output which source you used. Where it is silent, match
  the surrounding code rather than importing a preference from another
  repository. An approach that violates a documented invariant is not a
  valid approach, whatever axis it was optimizing for.
- **Concurrency cap is a hard ceiling.** Do not spin up more than
  `max_subagents` sub-agents in parallel.
