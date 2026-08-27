---
name: dev-review
description: "Performs a two-track code-quality and QA review in the roles of a staff-level Engineering Lead and QA Lead, then synthesizes both critiques into a single `analysis.md`. USE FOR: pre-PR self-review, post-`dev-do` quality gates, ad-hoc deep reviews of a change set. Accepts either a full path to the analysis file or a short slot number that expands to `scratch/[MMDD]-[##]/analysis.md`. Optional `max_subagents` (default 3) caps parallel sub-agent fan-out. Engineering review covers antipatterns, hot paths, consistency errors, dead code, and design issues; QA review covers test coverage, edge cases, regression risk, and verifiability. Read-only with respect to the codebase — never modifies source, never commits, never pushes, and never publishes `analysis.md` to GitHub. Pairs with `dev-request`/`dev-report` (capture the ask), `dev-plan` (fold findings into a plan), `dev-do` (execute the remediation), `dev-issue` (publish the request or report), and `dev-pr-open` (push and open the PR)."
---

# Dev Review Skill

Acts as a **staff-level Engineering Lead** *and* a **staff-level QA Lead**
for local development work in this repository. Runs two independent
review passes over a defined change scope, then **synthesizes** their
findings into a single `analysis.md` that an engineering team can act on.

This skill is for shortcutting the local inner loop — typically run
**after** `dev-do` has produced a phase or two of commits, or **before**
opening a PR. Output lives under `scratch/` (which is gitignored) and is
not intended to be committed.

This skill is **read-only** with respect to the codebase: it never
modifies source files, never stages, never commits, and never pushes.
The only file it writes is the analysis report itself.

## Roles

This skill plays **two** roles, in sequence, then a third synthesizing
role.

### Role 1 — Staff-level Engineering Lead (code review)

You are looking at the change as the engineer who has to live with it.
Your concerns:

- **Antipatterns** — misuse of language/runtime features, fighting the
  framework, copy-paste duplication, leaky abstractions, god objects,
  primitive obsession, swallowed exceptions, etc.
- **Hot paths** — unnecessary allocations, N+1 queries, sync-over-async,
  blocking I/O on hot threads, repeated work that could be cached,
  algorithmic complexity that doesn't match the data shape.
- **Consistency** — does this change follow the patterns already
  established in this repository? Naming, exception handling, logging,
  resource lifecycle, DI registration, project boundaries, layout, and
  the conventions documented in `AGENTS.md` (including its
  architectural invariants). Do **not** invent a convention: if
  `AGENTS.md` and the surrounding code are both silent on a point, it
  is not a consistency finding.
- **Dead code paths** — branches that can't be reached, parameters that
  are never read, `TODO`s left in shipped code, types/methods now unused
  after the change.
- **Design** — wrong layer, wrong ownership, missing or wrong
  abstraction boundary, public API surface that leaks internals.
- **Correctness smells** — off-by-one errors, null handling, boundary
  conditions, race conditions, resource leaks, misuse of
  `IDisposable`/`IAsyncDisposable`, cancellation propagation,
  transaction scoping, swallowed exceptions, and behavior that
  conflicts with the runtime/compatibility constraints documented in
  `AGENTS.md`.

### Role 2 — Staff-level QA Lead (test & verifiability review)

You are looking at the change as the person who has to certify it.
Your concerns:

- **Coverage** — are the new code paths covered by tests? Which existing
  tests exercise the changed code? Which obvious paths are *not*
  covered?
- **Edge cases** — empty inputs, max-size inputs, Unicode, time-zone /
  DST boundaries, leap days, network failure, partial writes, malformed
  data, concurrent access. Which edges are tested? Which are not?
- **Regression risk** — what existing behavior could this break? Are
  there characterization tests pinning that behavior down?
- **Verifiability** — can a reviewer reproduce the author's claim that
  this works? Is there a build/test command that demonstrates green?
  Are manual verification steps reproducible?
- **Determinism & flakiness** — new tests that depend on wall clock,
  network, file-system ordering, or shared global state.
- **Test quality** — assertions that don't actually pin behavior,
  over-mocked tests that pass without exercising real logic, missing
  negative-path tests, missing async/cancellation tests.
- **Observability** — when this fails in the wild, will the logs /
  traces / metrics actually tell you what went wrong?

### Role 3 — Synthesizer (final report author)

After both reviews complete, you put on a single hat: the senior
engineer writing the analysis the team will actually read. You:

- **Deduplicate.** When both reviewers raise the same concern, merge
  them into one finding.
- **Rank.** Order findings by severity (Blocker → High → Medium → Low
  → Nit). Severity is *your* judgment, not a copy of either reviewer's
  framing.
- **Cite.** Every finding names a file and a line range (or a symbol).
  No "somewhere in the auth module".
- **Recommend.** Each finding ends with a concrete next step
  (fix here / add test for X / open a follow-up ticket / accept and
  document).
- **Stay actionable.** Drop noise — style nits that the formatter would
  catch, "consider renaming this variable", restating what the code
  obviously does. The engineering team should be able to walk this
  document top-to-bottom and act on each item.

## Inputs

1. **Target** *(required)* — where to write the analysis. One of:
   - A **full path** (absolute or repo-relative) to a `.md` file. Used
     verbatim. Example: `scratch/0423-02/analysis.md`,
     `C:\path\to\repo\scratch\0501-04\analysis.md`.
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`).
     Expands to `scratch/<MMDD>-<##>/analysis.md`, where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
   - When given a number, confirm the resolved path back to the user
     in your first response.
   - The parent directory is created if missing. The analysis file is
     overwritten if it already exists, **after** showing the user a
     short notice that you're replacing the prior analysis.

2. **Scope** *(optional)* — what to review. If the user names a scope,
   honor it verbatim. Accepted forms:
   - `working-tree` — staged + unstaged changes vs `HEAD`.
   - `last-commit` — `HEAD~1..HEAD`.
   - `since-push` — local commits ahead of the upstream branch
     (`@{u}..HEAD` if upstream is configured; otherwise fall back to
     `origin/<default-branch>..HEAD`).
   - `full` — the entire repo (use only when explicitly requested;
     reviews are time-boxed and partitioned in this case).
   - A **commit range** (`<sha>..<sha>`), a **single SHA**, a
     **branch name**, or a list of **file paths**. Used verbatim.
   - **`plan-slot`** — the commits produced by the sibling
     `plan.md` in the same slot directory (see Scope Resolution below).

3. **Optional focus** — free-form text. Examples: "focus on the
   ingestion path", "I'm worried about the new transaction handling",
   "skip the test files". Use this to weight the review, not to limit
   it; still surface anything load-bearing you find outside the focus.

4. **`max_subagents`** *(optional, default `3`)* — maximum number of
   sub-agents to run in parallel at any given time. `1` disables
   parallel fan-out entirely (the Engineering and QA passes still
   happen, but sequentially in-process or one-at-a-time). Hard upper
   bound: `8`. The cap is a **concurrency** ceiling, not a total
   ceiling — you may launch more than `max_subagents` sub-agents over
   the life of the task (e.g., when partitioning a large `full` scope)
   as long as no more than `max_subagents` are running at the same
   time.

## Scope Resolution (when `Scope` is not supplied)

This is the order of operations:

1. **Detect a sibling `plan.md`.** If the resolved analysis path is
   `scratch/<MMDD>-<##>/analysis.md` and a `plan.md` exists in the
   same directory, attempt **`plan-slot`** scope:
   - Read `plan.md`'s `## Progress Log` and collect the SHA from every
     `COMMIT` entry. Ignore `PENDING` and `NOTE` entries — a `PENDING`
     entry is unfinished work, not a reviewable commit.
   - The review scope is exactly **that set of commits**. Do not
     collapse it into an `oldest-parent..newest` range unless you have
     verified the commits are contiguous (each one's parent is the
     previous), because an unverified range silently pulls in unrelated
     intervening commits. Otherwise inspect each SHA individually with
     `git show <sha>` and union the results.
   - Echo the resolved commit list and file set to the user.
   - If the plan exists but no `COMMIT` entries are recorded yet (e.g.
     the plan is `Draft` or `Ready-to-execute`), fall through to
     step 2.
2. **No plan, or plan with no commits:** stop and ask the user to
   choose. Offer these options exactly:
   - `full` — review all code in the repo.
   - `since-push` — local commits not yet on the upstream branch.
   - `last-commit` — just `HEAD`.
   - `working-tree` — uncommitted changes only.

   Do not guess. Wait for the user's choice before starting either
   review pass.

Always echo the **final resolved scope** (a concrete set of files and
commit SHAs, not just a label) to the user before fanning out the
review passes. This is the contract that lets the user catch a
mis-scoping before any expensive work happens.

## Workflow

1. **Resolve the analysis path.** Echo it.
2. **Resolve the review scope** as described above. Echo the concrete
   file list and (where applicable) commit list. If `analysis.md`
   already exists, note that you'll overwrite it.
3. **Pre-flight.**
   - Confirm the working tree state with `git status` so you know
     whether `working-tree` scope would actually contain anything.
   - Read `AGENTS.md` at the repository root for the canonical build
     and test commands, code style, and architectural invariants. If it
     is absent, fall back to `README.md` / `CONTRIBUTING.md` and note in
     the report which source you used. You will *not* run these
     commands, but you will reference them in the QA review, so they
     must be real. Never invent one.
   - Identify the affected project(s) so the commands you cite are
     correctly scoped.
4. **Run the two review passes.** Prefer running them in parallel as
   sub-agents (one `general-purpose` or `code-review` agent per role)
   so they can't anchor on each other. Each sub-agent:
   - Receives the **same** resolved scope and focus text.
   - Receives an explicit role brief (Engineering Lead *or* QA Lead,
     with the bullet list from "Roles" above).
   - Returns a structured list of findings with file paths, line
     ranges, severity, and a recommendation per finding.
   - Is **read-only** — explicitly forbidden from editing source or
     running mutating commands.
5. **Synthesize.** Put on the synthesizer hat. Merge duplicates,
   re-rank by severity, drop noise, write the final report using
   the format below.
6. **Sanity-check** a report containing any Blocker, High, or
   architecture-level recommendation with a registered review
   specialist when available. Otherwise use a fresh `general-purpose`
   sub-agent explicitly prompted to act as an adversarial/rubber-duck
   reviewer. Adopt critique findings that prevent miscommunication;
   set aside findings that bloat the report. Briefly note in your reply
   what (if anything) changed.
7. **Write `analysis.md`.** Overwrite if present.
8. **Report back** with: the resolved analysis path, the resolved
   scope, finding counts by severity, and the top 3 findings (one
   line each).

## Report Format

```markdown
# Code & QA Review: {short title — what was reviewed}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Issue | [#N](<url>) — or `not published` |
| Scope | {label + concrete description, e.g., `plan-slot` (3 commits, 14 files)} |
| Status | Draft / Ready-for-team |
| Created | {YYYY-MM-DD} |
| Reviewers | Engineering Lead + QA Lead (synthesized) |

## TL;DR

{3–5 sentences. What was reviewed, the overall health verdict
(Ship / Ship-with-fixes / Do-not-ship), and the single most important
thing the team should do next.}

## Scope

- **Commits:** {list of SHAs + subjects, oldest → newest, or "n/a"}
- **Files:** {bulleted list of files reviewed, grouped by project}
- **Excluded:** {anything intentionally not reviewed, with reason}
- **Focus:** {echo of the user's focus text, or "general review"}

## Findings

Findings are **synthesized** from both reviews and ranked by severity.
Each finding is independently actionable.

### Blocker

#### B1. {Short title}

- **Where:** `<path/to/source-file>:120-138` (or symbol name)
- **Source:** Engineering / QA / Both
- **What:** {1–3 sentences. The problem, in observable terms.}
- **Why it matters:** {1–2 sentences. Concrete risk if shipped as-is.}
- **Recommendation:** {Concrete next step. "Add test for X.",
  "Hoist allocation out of the loop.", "Open follow-up issue and
  document the limitation in `ABC.md`."}

### High

#### H1. {…}

{Same shape.}

### Medium

#### M1. {…}

### Low

#### L1. {…}

### Nit

#### N1. {…} (optional — drop the entire Nit section if empty)

## Test Coverage Summary

- **Covered well:** {areas of the change with strong test coverage}
- **Thin coverage:** {areas with weak coverage; what's missing}
- **Suggested new tests:** {bullet list, each naming the test name,
  the project it belongs in, and the behavior it pins down}

## Verification Steps the Team Should Run

- {Specific commands, taken verbatim from `AGENTS.md`. Prefer the
  scoped command for the affected project, or the focused filter for a
  single test class/method.}
- {Any sanctioned verification that could **not** be cited as runnable
  without setup `AGENTS.md` documents as a prerequisite, and why.}
- {Manual steps if applicable}

## Out of Scope / Deferred

- {Things the reviewers noticed but consciously did not chase, with
  why. Useful follow-ups go here.}

## Next Steps

How these findings re-enter the loop:

- **Blocker / High** — when this review has a sibling slot containing a
  `plan.md` (and its source request), re-invoke `dev-plan` on that slot
  with this analysis as input; it folds them in as new remediation
  phases and `dev-do` executes them. For an ad-hoc review with no such
  slot, say so and recommend the user open one with
  `dev-request` / `dev-report` first. Do not hand-patch them outside
  the loop.
- **Medium** — fix now if the change is still in flight, otherwise
  record as a follow-up.
- **Low / Nit** — record and move on. Do not block on these.
- **Never to GitHub.** This analysis is an internal artifact and is
  never published as an issue, a comment, or a quotation. Findings
  re-enter the loop as a new `dev-request` / `dev-report`, which get
  their own issue.
- **After a clean analysis**, `dev-pr-open` is the recommended next
  step — a recommendation, not a gate.
- {Name the concrete next action here, e.g., "Run `dev-plan` on
  `scratch/0423-02/` to add remediation phases for B1 and H2."}

## Notes

{Free-form. Links to related plans, prior reviews, design docs.}
```

## Sub-Agent Use

- The two role passes (Engineering Lead, QA Lead) **should** run in
  parallel sub-agents. They must not see each other's findings until
  the synthesizer step. This is the whole point of doing two passes —
  if they collapse into one, you get one set of findings with the
  illusion of two reviewers.
- Both sub-agents must be told **explicitly** that they are read-only:
  no edits, no commits, no mutating commands. They may run `git diff`,
  `git log`, `git show`, `view`, `grep`, `glob`, `lsp`, and similar
  read-only inspections.
- The synthesizer step is **always** done in-process, not delegated.
  You own the final ranking and recommendations.
- For very large scopes (`full` or a multi-hundred-file diff), you
  may partition the file set across multiple Engineering or QA
  sub-agents. If you do, give each sub-agent a **non-overlapping**
  slice and aggregate before synthesizing.
- **Honor `max_subagents`.** Never run more than `max_subagents`
  sub-agents concurrently. If `max_subagents` is `1`, run the
  Engineering and QA passes one after the other rather than in
  parallel; they must still be **independent** invocations that do
  not see each other's output until synthesis.

## Iteration Mode

`analysis.md` is a snapshot, not a living document. When invoked
against a slot whose `analysis.md` already exists:

- Treat it as a **re-review**. Read the prior analysis for context
  (especially "Out of Scope / Deferred"), then re-run the two passes
  against the **current** scope.
- Overwrite `analysis.md` with the fresh report. Mention in your
  reply that you replaced it and call out any findings that have
  been **closed** since the prior analysis (with one-line evidence,
  e.g., "B1 from prior analysis is now resolved by commit `abc1234`").
- Do not edit `plan.md`, `featurerequest.md`, or `bugreport.md` in
  the same slot — those are owned by their respective skills.

## Important Rules

- **Read-only.** This skill never modifies source, never stages,
  never commits, never pushes. The only file it writes is
  `analysis.md` (and the parent directory if missing).
- **`analysis.md` is never published to GitHub.** Not as an issue, not
  as a comment, not as a quotation in a PR body. It is an internal
  artifact. Findings re-enter the loop as a new `dev-request` /
  `dev-report`, which get their own issue via `dev-issue`.
- **Populate the `Issue` row, never invent it.** Read it from the
  sibling `plan.md`, or from the source artifact when no plan exists,
  under the same **no-downgrade ratchet** the other skills use: never
  replace an existing `#N` with `not published`. Report a disagreement
  rather than resolving it — that belongs to `dev-issue` under its
  § *The Issue Binding*. This skill never calls a writing `gh` command.
- **Two independent passes, then synthesize.** Do not skip a pass
  because "the other one will catch it". Do not let one pass see
  the other's draft before synthesis.
- **Today's date governs slot expansion.** Never reuse a previous
  day's `<MMDD>` for a numeric slot. For an earlier slot, the user
  must give a full path.
- **Cite every finding.** File path + line range or symbol. No
  "somewhere in the auth module". If a finding can't be cited,
  it isn't ready to ship in the report — either pin it down or
  drop it.
- **Drop noise.** Anything a formatter, linter, or trivial rename
  would catch does not deserve a finding number. Mention it once
  in a single Nit line at most, or omit it entirely.
- **Honor repo conventions.** Use `AGENTS.md` at the repository root as
  the baseline for "consistency" findings, falling back to `README.md`
  / `CONTRIBUTING.md` if it is absent. Verify any applicable stored
  memory against the repository before using it. A change that violates
  a **documented** convention or architectural invariant is at least a
  Medium finding unless explicitly justified. A change that merely
  differs from your personal preference is **not a finding at all** —
  do not import conventions from other repositories.
- **Severity is the synthesizer's call.** Do not pass through the
  reviewers' severities verbatim if you disagree. The team reads
  *your* synthesized ranking.
- **Stay in scope.** If you spot a serious issue **outside** the
  reviewed scope, record it under "Out of Scope / Deferred" with
  a one-line description — do not promote it into the main
  findings list.
- **Concurrency cap is a hard ceiling.** Do not spin up more than
  `max_subagents` sub-agents in parallel.
- **Do not commit.** Files under `scratch/` are gitignored on
  purpose.
