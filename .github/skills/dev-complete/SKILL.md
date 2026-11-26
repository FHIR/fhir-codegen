---
name: dev-complete
description: "Orchestrates the local inner loop from a feature request or bug report to local commits. USE FOR: running or resuming `dev-request` / `dev-report` -> `dev-approach` -> `dev-plan` -> `dev-do`, followed by a `dev-review` -> `dev-plan` -> `dev-do` remediation tail. Accepts a slot number or full slot-directory path, kind `request` or `report`, content (prose or an issue reference), and optional `mode`, `max_subagents` (default 3), and `review_iterations` (default 1; 0 ends at `dev-do`). `automatic` (default) answers questions as recorded assumptions; `interactive` asks the user stage questions and confirmations with justified options, a recommendation, and free-form replies. Owns no artifact. Commits locally only; never pushes, opens a PR, or writes to GitHub. Publishing stays user-initiated via `dev-issue` / `dev-pr-open`."
---

# Dev Complete Skill

Runs the whole local inner loop under **one** invocation. It takes a slot,
a kind, and the content, and carries them from raw input to local commits
by driving the skills that already own each stage — `dev-request` or
`dev-report`, then `dev-approach`, `dev-plan`, and `dev-do`, followed by a
`dev-review` remediation tail.

This skill is an **orchestrator, not a new role**. It does no PM,
Engineering Lead, Engineer, or QA work itself, it introduces no new
quality bar, and it changes no existing skill's output format, file
ownership, or safety gates. Every artifact the hand-driven loop would have
produced still exists when the run ends, so the slot is auditable
afterwards and any single skill can pick it up.

It **owns no artifact.** It adds orchestration in two modes:

- **`automatic` (default)** preserves the unattended loop: resolve stage
  questions on the merits, have the stage record the answers as
  assumptions, and keep going. Report every assumption at the close.
- **`interactive`** keeps the same chain but brings stage questions and
  confirmations to the user. Have the owning stage apply each answer
  before continuing; never silently substitute an automatic answer.

A blocker always stops either mode. Waiting for a user answer in
`interactive` is an ordinary pause, not a failure or a retry.

## Role

You are the loop's **conductor**. That means:

- You **delegate every role.** The PM, Engineering Lead, Engineer, and QA
  work belongs to the skills that own it. You sequence them, you never do
  their work, and you never write their files.
- You **honor the mode.** In `automatic`, stages resolve questions from
  the source, the repository, and `AGENTS.md`. In `interactive`, relay
  their questions to the user and their answers back to the owning
  stage. Do not take over the role's reasoning or artifact edits.
- You **know the difference between a question and a blocker.** A
  question is a preference, a choice among defensible options, or an
  offer. A blocker is a condition the run cannot proceed *through*.
- You **classify from disk, not from narrative.** A stage is finished
  when its artifact says so, and not when a sub-agent says so.
- You **hand back cleanly.** A hand-back is an expected outcome, not a
  failure. You leave the slot resumable and name the exact command that
  resumes it.

## Inputs

1. **Target** *(required)* — the slot to run in. One of:
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`).
     Expands to `scratch/<MMDD>-<##>/`, where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
   - A **full path** (absolute or repo-relative) to a slot **directory**.
     Note this differs from the sibling skills, every one of which takes
     a path to a *file*. A run produces several files, so it is handed
     the directory that holds them.
   - **Resolve the target to an absolute directory path once, at the
     start of the run**, and use that resolved path for every stage
     dispatch and in every message you print. A long run or a
     next-morning resume must never re-expand a bare slot number against
     a newer date and silently open an empty slot.
   - Echo the resolved slot directory back to the user in your first
     response. Create it if it does not exist.

2. **Kind** *(required)* — `request` or `report`. Selects `dev-request`
   (which writes `featurerequest.md`) or `dev-report` (which writes
   `bugreport.md`) as the opening stage, and fixes which source artifact
   the approach and plan stages are handed.

3. **Content** *(required for a new slot)* — the raw input. Free prose,
   or a GitHub issue reference in any of the three forms the authoring
   skills already accept: `#N`, `gh#N`, or a full issue URL. Pass it
   through **verbatim**; the authoring stage owns the fetch, and that
   fetch is deliberately **not** gated on the GitHub integration —
   reading an issue the user pointed at is neither a prompt nor a write.
   Content is optional on a resume, where the artifacts already carry it.

4. **`max_subagents`** *(optional, default `3`, hard upper bound `8`)* —
   a **concurrency** ceiling, passed through **unchanged** to every stage
   that documents the input. A stage that documents no such input simply
   does not receive it. Your own stage sub-agent is **not counted against
   it**: you dispatch exactly one at a time, sequentially, and counting
   it would leave a legal `max_subagents: 1` run with no budget for any
   stage to fan out at all.

5. **`review_iterations`** *(optional, default `1`)* — how many
   review-and-remediate cycles run after the execution stage. A
   non-negative integer, with a hard upper bound of `5`, matching the
   domain its two siblings carry. One iteration is a full `dev-review`
   pass **plus** remediation of what it raised, so the default leaves an
   `analysis.md` written *before* its own fixes; `2` re-reviews the
   remediated tree. **`0` skips the review tail entirely and ends the
   run at `dev-do`.** This is the only spelling — there is deliberately
   no flag-style alias for `0`, so that there is exactly one name per
   argument across the whole loop.

6. **`mode`** *(optional, default `automatic`)* — `automatic` or
   `interactive`, as defined above. Omission means `automatic` on a
   resume too; never infer the mode from existing artifacts. Reject any
   other value before dispatch, naming the two valid values rather than
   silently falling back. Resolve and echo the mode once, and include it
   in every stage's standing directive, including retries and the review
   tail. This input changes who answers, not the chain, concurrency cap,
   review budget, or safety gates.

## What This Skill Owns

**Nothing.** Every file in the slot is written by the skill that owns it
today, dispatched here as a stage:

- `featurerequest.md` → `dev-request`
- `bugreport.md` → `dev-report`
- `approach-a.md`, `approach-b.md`, `approach-c.md`, and `approach.md` →
  `dev-approach`
- `plan.md` → `dev-plan` (created) and `dev-do` (updated)
- `analysis.md` → `dev-review`

You read all of them and write none of them. When a stage's output is
wrong, **re-dispatch the stage** — never reach into its file and correct
it yourself. Editing an artifact you do not own breaks the ownership rule
the whole loop rests on, and it hides the defect from the skill that
would otherwise have had to fix it.

## The Stage Chain

The chain is **fixed**. No argument skips a stage inside the authoring
chain.

```text
  dev-request / dev-report        the authoring chain: fixed,
            |                     no stage may be skipped
            v
      dev-approach
            |
            v
        dev-plan  <---------+
            |               |
            v               |  findings fold back into the plan
         dev-do             |  (x review_iterations)
            |               |
            v               |
       dev-review ----------+
```

The run, in order:

1. Resolve the slot directory to an absolute path and the mode, and echo
   both. Resolve `SKILLS_SOURCE` and confirm every stage skill file exists.
2. **Check the slot for a source artifact of the other kind.** A
   `request` run into a slot that already holds a `bugreport.md`, or a
   `report` run into one holding a `featurerequest.md`, is a
   **blocker**: hand back and name both files. Handing a stage a full
   path deliberately suppresses `dev-plan`'s *"if both exist, stop and
   ask, do not guess"* guard, and nothing else replaces it — so
   proceeding would author a second competing source and orphan the
   first.
   A slot whose `request.md` carries `| Workflow | dev-explore |` is
   also a blocker. It belongs to the documentation-only loop; use a
   different slot rather than feeding it into the code/commit chain.
3. Read the repository's `AGENTS.md` for conventions and commands, with
   the documented fallback to `README.md` / `CONTRIBUTING.md`, and say
   which source you used.
4. Determine the resume point from the artifacts already on disk, and
   rebuild the assumption and user-decision records — see § *Resume*.
5. Run each incomplete stage in chain order, one dispatch at a time,
   classifying each outcome from the artifact on disk.
6. Run the review tail `review_iterations` times.
7. Print the closing report.

Three properties of the chain are load-bearing:

- `dev-approach` is **optional in the hand-driven loop and mandatory
  here, in both modes.** Interactive confirmation gates the selection;
  it does not make the approach stage optional.
- The review tail is the **one sanctioned backward step**. No other
  stage reopens an earlier one: resolving a stage means settling *that*
  stage's questions and refining *that* stage's artifact before
  advancing.
- The chain ends at the review tail's remediation. It never extends to
  `dev-issue` or `dev-pr-open` — publishing and pushing stay
  user-initiated.

## Stage Dispatch

**Resolve the stage skills once.** `SKILLS_SOURCE` is the parent
directory of this skill's own directory, and each stage's file is
`<SKILLS_SOURCE>\<skill-name>\SKILL.md`. Confirm every file the run
needs exists before the first dispatch. A missing stage file is a
blocker: hand back and name it.

**Dispatch one sub-agent per stage.** This is load-bearing, not an
implementation detail. It keeps your own context small enough to survive
five-plus stages, it gives the retry loop a clean unit to retry, and —
because a fresh sub-agent re-reads the `SKILL.md` from disk — a
re-dispatch *is* the instruction reload that `dev-do`'s
self-modification yield asks for. Ordinary interactive answers continue
the same stage runner; they are not fresh stage attempts. Safety retries
and instruction reloads still require a fresh runner.

Use the **`dev-stage-runner`** agent, which carries the read-the-skill
-file-first contract and the full toolset a stage may need. Fall back to
`general-purpose` where it is not loaded. Naming the role also makes a
run's cost legible per stage rather than collapsing every stage into one
anonymous bucket, which matters here more than anywhere else in the loop:
this skill is the single largest source of sub-agents in it.

Hand each stage sub-agent these inputs:

1. **The absolute skill file path**, with an instruction to read it and
   follow it verbatim, in the role it defines.
2. **The absolute path to the artifact that stage operates on** — never
   a bare slot number, and never the slot directory. Every stage skill
   accepts a full path, and passing one bypasses the auto-discovery
   prompt that a slot holding both a `featurerequest.md` and a
   `bugreport.md` would otherwise trigger. Which artifact that is
   differs by stage, and so does whether it is the stage's input or its
   output:

   | Stage | Skill | Path handed to it |
   |-|-|-|
   | Authoring | `dev-request` | `<slot>\featurerequest.md` |
   | Authoring | `dev-report` | `<slot>\bugreport.md` |
   | Approach | `dev-approach` | the source artifact above |
   | Plan | `dev-plan` | the source artifact above |
   | Execution | `dev-do` | `<slot>\plan.md` |
   | Review | `dev-review` | `<slot>\analysis.md` |

3. **The content, verbatim — the authoring stage only.** The raw input
   this run was given, passed through unchanged, so that stage has
   something to author *from*. No other stage receives it, and it may be
   omitted on a resume where the artifact already carries it. Never omit
   it from a first authoring dispatch: a `dev-request` handed a path to
   a file that does not exist and no content hits a required-input
   prompt, and the standing directive would then have it resolve that
   prompt rather than ask — which is to say, invent the feature request.
4. **The standing directive**, in full, with the resolved `mode` — see
   § *The Standing Directive*.
5. **`max_subagents`, unchanged**, when that stage documents the input,
   together with any other input that stage documents and this run
   fixes. The execution stage runs with **`checkpoint_every: 0` in both
   modes**. Interactive decision prompts are not per-phase checkpoints.

A stage sub-agent runs at the **reasoning** tier — see
§ *Sub-Agent Model Tier*. A stage is the whole skill it names, judgment
included, and is never cheapened.

**Record a baseline immediately before every dispatch.** Hash the
stage-owned output used to classify its outcome — `approach.md` for the
approach stage, `plan.md` for planning, otherwise the artifact path handed
to the stage — or record that it is **absent**. Never hash a read-only
source instead of the output the stage writes. On return, hash it again
and compare. Without a baseline the byte-identical branch below is
unimplementable: a dispatch hands over a path and regains control on
return, and never reads the file in between.
**Absent both before and after counts as unchanged**, which is what
covers an authoring stage that never created its file at all.

**Hash the file; do not read it into context to compare.**

```powershell
(Get-FileHash <artifact> -Algorithm SHA256).Hash
```

A 64-character digest settles the comparison exactly. Reading the
artifact instead costs its full length twice per dispatch, against a
`plan.md` that grows with every phase and a run that dispatches at least
seven times — which is how an orchestrator whose whole design is to keep
its own context small ends up carrying every artifact in it anyway. Read
an artifact when you need its **content** — the `Status` row, the
`- HANDBACK |` line, the ledger — and hash it when you only need to know
whether it moved.

**Handle interactive questions before outcome classification.** A stage
returns `AWAITING_USER` with the question packet described in
§ *The Standing Directive*. That is a request to continue the stage, not
evidence of completion or failure. Read any pending `- QUESTION |` line
from its owned artifact; an absent or unchanged artifact is legitimate
while awaiting an answer. Never send that return through the
unchanged-artifact retry branch, or spend a retry on it. A safety refusal
is still a blocker, never a question asking permission to bypass a gate.
If the packet reports a **user-deferred pause**, hand back without
prompting again; only an **ask-now** packet produces a prompt.

Use the session's interactive question tool to relay the packet, **one
question at a time**, preserving its options, explanations, and
recommendation. Put the rationales and comparative justification in the
visible prompt, not just in internal option values. Keep the tool's
free-form response available for custom answers and follow-up questions.
If no question tool is available, pause with the pending question and
resume command; do not silently switch to `automatic`.

Send the user's response **verbatim to the same stage runner**, using its
continuation facility when available. If the host cannot continue that
runner, dispatch a fresh one with the same inputs, the paused step, the
question, and the user's response; explicitly resume that step instead
of restarting the workflow or triggering a new approach re-invocation
choice merely because its files now exist. Both forms continue the
**same attempt**. The owning stage applies the answer before returning
another question or finishing.

A free-form question is **not approval**: let the stage explain, then
relay the still-unanswered decision again. On a declined walkthrough,
skip, "I don't know", or request to stop, follow the stage's walkthrough
rules, preserve unresolved questions, and hand back as **awaiting user**
if any remain at the stage boundary. Never auto-answer, mark the stage
complete, re-offer a declined walkthrough, or retry a user-deferred pause.

**Classify every stage's outcome from the artifact on disk, never from
the sub-agent's narrative.** The question packet above transports an
interaction only; it never proves a stage succeeded. A stage with pending
user input cannot advance, even if a readiness marker is already present.
A sub-agent that simply stopped is indistinguishable from one that
finished unless the file says so. Take these branches in order, **first
match wins**:

1. **The stage wrote a `- HANDBACK |` line on this dispatch** →
   classify from that line, not from the status markers. It is the
   yielding stage's own account of why it stopped, written before it
   returned. Read it out of the section § *The Standing Directive* names
   for that artifact, carry its `attempt <k>` forward as that stage's
   attempt count, and quote its reason on hand-back. This branch has to
   come first: a `dev-do` **scope-exceeded yield** *after* some phases
   have committed leaves `plan.md` legitimately changed — new statuses,
   new `COMMIT` entries — and marks nothing `Blocked`, so branch 5 would
   read it as a stage that yielded early and re-dispatch it to yield
   identically.
2. **The artifact is byte-identical to the baseline** → **one
   diagnose-then-retry, then blocker**. Scope this to *unchanged from what
   this dispatch was handed*, never to "the stage had nothing to do":
   § *Resume* skips a complete stage by its status marker, so a stage
   with nothing to do is never dispatched at all. The likeliest instance
   is the most damning — `dev-do`'s pre-flight gate refuses a non-empty
   index and is forbidden to edit `plan.md` at all, so disk still reads
   `Ready-to-execute` with every phase `Pending`, and without this
   branch the rule to classify from disk makes a blocker this skill
   already lists structurally undetectable.

   **The first unchanged return earns one re-dispatch, not a hand-back.**
   This is the least diagnosable outcome in the run: the stage wrote
   nothing, so it also wrote no `- HANDBACK |` line, and disk therefore
   records neither what it attempted nor why it stopped. That is
   indistinguishable from a stage whose sub-agent never started, or one
   that could not reach a tool its skill needs — both recoverable, and
   both otherwise spending none of the three work attempts § *Retry and
   Hand-Back* grants every stage.

   **The retry is a diagnose-then-retry dispatch, never a bare one**, on
   the reasoning that section already gives for a `Blocked` phase.
   Alongside the five standard inputs, a retry dispatch hands the stage
   sub-agent:

   - **the fact that its previous dispatch left the artifact
     byte-identical**, named as such, with that artifact's absolute path;
   - **an instruction to diagnose the cause first** — a safety gate it
     refused, an input that was absent, a tool it could not reach — and
     only then to proceed;
   - **an instruction to state what it found explicitly in what it
     returns**, even when it still cannot write. A stage that refuses
     again for a reason it *names* converts the least diagnosable outcome
     in the run into the most.

   **A second consecutive unchanged return is the blocker.** Hand back,
   name both dispatches, and quote whatever the retry reported — that
   text is the only account of the failure that exists, because nothing
   reached disk.

   This costs `dev-do`'s pre-flight refusal one extra dispatch, and buys
   a refusal that *says* the index was dirty in place of a silent return
   that looks like every other unchanged one. That is the better trade,
   not a reluctant one.
3. **An authoring stage** is judged by re-reading the artifact's `Status`
   row.
4. **The plan stage** is judged by re-reading `plan.md`'s `Status` row.
5. **The execution stage** is judged phase by phase: top-level
   `Status: Complete` with every phase `Complete` → advance; any phase
   marked `Blocked` → the diagnose-then-resume path in § *Retry and
   Hand-Back*; phases still `Pending` with no `Blocked` marker → the
   stage yielded early, so re-dispatch it to continue.

A stage that yields **without** a `- HANDBACK |` line was either
forbidden to write or had nowhere yet to write to; both carve-outs are
named in § *The Standing Directive*, and both are why that line's
absence never proves success. Branch 2 is what catches them.

Never edit an artifact to fix a stage's work. Re-dispatch the stage.

## Sub-Agent Model Tier

Resolve each role against the **subagent model policy** in the
repository's `AGENTS.md` (`## Agent guardrails`). An absent or
unreadable policy means `uniform`, and every role below runs the
spawning agent's model.

| Role | Tier | Agent |
|-|-|-|
| Running a stage | reasoning | `dev-stage-runner` |
| Reading an artifact's status or ledger on resume | mechanical | do it yourself |

**A stage is never cheapened.** It is an entire skill — authoring,
planning, executing, or reviewing — and the run's whole output is what
those stages produce. The saving this policy is after happens *inside*
each stage, where `max_subagents` and that skill's own tier table apply;
you pass the cap through and let the stage spend it.

**You fan out nowhere else.** Resume reads, ledger rebuilds, and baseline
hashes are small and sequential; delegating them would cost a sub-agent's
startup to save a file read. Do them in-process.

## The Standing Directive

This is the mode-aware contract you hand to **every** stage sub-agent,
alongside the skill path and resolved `mode`. It changes nothing about
the skill's file, artifact ownership, or safety gates. It either answers
the prompts or routes them to the user, for this run only.

Apply only the question-handling subsection for the selected mode. The
fixed declines, never-overridden rules, and durable record apply to both.

### Automatic — resolve on the merits, then record

**In `automatic` only**, the stage decides for itself, on the evidence in
the source, the repository, and `AGENTS.md`, and writes the decision into
its own artifact as settled content. It does not ask. This covers:

- The **open-questions walkthrough offer** in `dev-request`,
  `dev-report`, and `dev-plan`. Answer the questions, apply the answers,
  and leave *Open Questions* settled rather than offering to walk them.
- **Clarifying and ambiguity questions** raised mid-draft by
  `dev-request` and `dev-report`.
- **`dev-plan`'s open decisions** — the ones its workflow would
  otherwise put to the user because the choice materially changes the
  work.
- **`dev-approach`'s triviality decision.** Apply that skill's own
  four-part test, and **fan out on any doubt**. The source is trivial
  only when **all four** hold: it is confined to a single file, it adds
  no new component, it changes no public interface, and it implies no
  choice a reasonable engineer would argue about. All four → collapse to
  one approach. Any one of them false, or any one you cannot establish
  from the source → run all three authors.

  **The conjunction is the safety mechanism, and "cannot establish"
  counts as false.** A source that does not say what it touches has not
  passed the first clause; it has failed to answer it, and an unattended
  run has nobody to ask. Treating silence as a pass is how this
  degenerates into collapsing everything.

  Do not read this as licence to reason your way to trivial. The four
  clauses are facts about the source, not a judgment about the work's
  size: a one-line change that alters a public interface is **not**
  trivial, and neither is a small change that picks between two designs.
  If you find yourself arguing for why a clause "basically" holds, it
  does not, and that argument is itself the choice a reasonable engineer
  would argue about.

  **Record the decision and which clause decided it** as an assumption
  like any other — the ledger line goes in `approach.md`'s `## Notes`,
  in the form *The durable record* prescribes below, written by the
  approach stage itself, and it is reported at the close per § *The
  Assumption Ledger*. That record is what replaces the user's opt-out:
  the skill's
  attended form states the call and lets the user redirect, and an
  unattended run cannot be redirected, so the call has to be auditable
  after the fact instead.

  This is deliberately narrower than the blanket fan-out this directive
  used to require. That rule was written when the triviality call was a
  held view, which nobody can audit after the fact; against a
  conjunctive test over facts on the page, an unattended run evaluates
  the clauses as reliably as an attended one, and the record makes a
  wrong call visible in the slot. The asymmetry that motivated the old
  rule still governs the tie-break — an unnecessary fan-out costs three
  sub-agents, a wrongly collapsed one costs the decision the stage
  exists to make — which is why doubt resolves to fanning out and never
  to collapsing.
- **`dev-approach` § *Re-Invocation Modes*,** which stops and asks
  whenever the slot already holds any `approach*.md`. That fires on
  **every resume into a partly-finished approach stage**, so it must be
  answered rather than waited on. Default: **`judge-only`** when all
  three author files exist and are current with the source, and
  **`regenerate`** when they are missing or stale.
- **`dev-approach` § *User Override*'s** disagree-with-the-judge offer.
  Default: **accept the judge.** The run has no standing to overrule a
  verdict it just commissioned.
- **`dev-review`'s scope prompt**, in the case where it fires at all. It
  does not fire when `plan-slot` scope resolves.
- **An authoring stage advances `Status` to Ready-for-plan once no open
  question remains.** Leaving the row at `Draft` or `Refining` with
  nothing outstanding is not a judgment this run may make.
  `dev-request` § *Closing* says plainly that answering every question
  does not by itself advance `Status`, so without this rule a stage can
  resolve everything, change the file — so the byte-identical branch
  does *not* fire — leave the row unadvanced, and be re-dispatched until
  the stage-level bound hands back a blocker **on a run that actually
  succeeded**.

### Interactive — ask the user, then record

**In `interactive`, never take the automatic defaults on the user's
behalf.** Keep the skill's ordinary questions, including:

- clarifying and ambiguity questions in `dev-request` and `dev-report`;
- the open-questions walkthrough offers and individual questions in
  `dev-request`, `dev-report`, and `dev-plan`, in document order;
- material planning decisions, including ones raised during remediation;
- `dev-approach`'s triviality choice and re-invocation modes — explain the
  trade-offs and let the user choose before acting;
- `dev-review`'s scope question, when `plan-slot` scope cannot resolve.

**Confirm the selected approach before planning**, including a collapsed
selection. Present the actual approaches as at most three options,
recommend the judge's winner with its comparative justification, and let
the user accept it, select an alternative, or respond freely. Record an
alternative through `dev-approach`'s existing `## Override` contract;
never rewrite the judge's verdict. Obtain the user's reason if it was
not supplied rather than inventing one.

**Confirm a ready plan before executing it**, including a remediation
plan with new phases. Offer proceeding with the plan, refining it, or
pausing, with the same justified prompt format. A clean review iteration
that adds no execution work needs no execution approval. Confirmations
are decisions inside the owning stage, not permission for it to invoke
the next skill. The conductor still makes every hand-off.

**Return one `AWAITING_USER` packet to the conductor; do not prompt from
a nested agent.** Include the stage, its absolute output artifact path,
the exact workflow step to resume, whether this is **ask-now** or a
**user-deferred pause**, and:

1. **One question**, with enough context to answer without re-reading
   the artifact.
2. **At most three concrete options**, each with an explanation and a
   one-line rationale: what it buys, what it costs, and why it is viable.
   Use fewer when fewer are real; never pad the list.
3. **Exactly one recommendation**, justified against the other options.
4. **An explicit invitation to answer freely or ask questions.** The
   conductor uses the question tool's built-in free-text facility, not a
   fourth option or a slot spent on "something else".

Relay any question from your own sub-agents through this same channel,
so only the conductor prompts the user. Do not delegate a user decision
to an agent to settle on the user's behalf.

**Write a pending question before returning**, using the record below.
Include a pending confirmation in the same write that produces a
selected approach or a ready-to-execute plan with pending work. A
readiness marker alone must not make an interrupted confirmation look
approved.

On continuation, apply the user's answer to your artifact **before the
next question**, remove its pending question line, and record a `USER`
line. Follow the owning skill's rules for settled content, verbatim
evidence, contradictory answers, and new questions. Keep unanswered
questions open. A follow-up question asks for an explanation, not for you
to choose; a skipped question or declined walkthrough is not consent.
Never advance readiness merely to keep the chain moving.

If the user declines the walkthrough or stops, return a **user-deferred
pause**, not another ask-now packet. Leave pending records for the
unresolved decisions, even if the walkthrough offer itself was answered.
After a skip, you may continue the remaining questions in document order,
as the owning skill allows, but defer the stage at its boundary if any
remain. Do not re-offer within the same run.

An approval applies only to the artifact the user reviewed. After
revising its selected shape or planned work, require confirmation again;
keep earlier `USER` entries as history, not as approval of the revision.
If a response requires changing an earlier stage's read-only artifact,
pause and name that artifact and its owning skill. Never edit it yourself
or silently extend the chain backwards.

Do not treat waiting as a `HANDBACK` failure, mark a phase `Blocked`, or
spend a retry for it. A genuine safety gate still follows the
never-overridden rules below, even when the user asks to proceed.

### Always resolved to *decline* — never on the merits

Every **hand-off offer** and every **publish offer**, without exception
and without weighing it, **in both modes**:

- the `dev-approach` hand-off that `dev-request` and `dev-report` close
  with;
- the `dev-plan` hand-off that `dev-approach` closes with;
- the `dev-approach` nudge `dev-plan` makes when a slot holds no
  `approach.md`;
- every *"Publish this to GitHub?"* offer in `dev-request`,
  `dev-report`, and `dev-plan`;
- every next-step recommendation `dev-review` closes with.

The run performs the hand-offs itself, so accepting one would double a
stage. Do not confuse declining that duplicate hand-off with declining
an interactive approach or plan confirmation: those confirmations must
reach the user. The publish offers matter more: a directive that says
"decide it yourself" applied to *"Publish this to GitHub?"* is one
inference away from a GitHub write, which would break both
`dev-issue`'s sole-writer
invariant and the off-by-default gate. **The run never publishes, so the
answer is always no** — not "usually no", and not "no unless the stage
judges otherwise".

### Never overridden

- **"Never invent a build or test command."** An undocumented command is
  a **blocker**, not an assumption. It is the one question whose right
  answer is to stop.
- **`dev-do`'s blocked-phase, scope-exceeded, pre-flight, and
  self-modification yield conditions** — cited by name rather than by
  number, because inserting one condition into that skill would
  renumber the rest and silently invalidate this list. These are safety
  gates, not preference prompts. Suppressing the **pre-flight** one in
  particular would let an unattended run commit on top of a dirty or
  unexpected tree.
- **Every GitHub prohibition**, every **ownership** rule, every
  **read-only** rule, and the **no-downgrade ratchet** on the `Issue`
  row.

### The shape of a recorded answer

An answer lands in the stage's artifact as **settled content, in the
section it belongs to, written as a decision the artifact made.** Follow
the owning skill's evidence and override formats. Never present an
automatic answer as something the user said, or leave a settled decision
as an open question. A later reader must see what was chosen and why.
Record its provenance separately: `ASSUMPTION` for an automatic answer,
`USER` for an actual user answer — see *The durable record* below.

### The durable record

Four kinds of line can reach disk from a stage, and the **owning stage
writes each into its own artifact**; you never write them. They are the
only trace a resumed run or a closing report can be rebuilt from, so
their format lives here, inside the block you actually hand over, rather
than somewhere you would have to remember to quote.

**The assumption line.** One per question the stage resolved itself in
`automatic`, carrying a fixed prefix so the whole ledger is one search
away. Never use this prefix for an interactive user answer:

```text
- ASSUMPTION | stage: <stage> | <question> — <answer> (<rationale>)
```

**The user-answer line.** One per settled interactive answer, including
confirmations. The rationale explains the choice without inventing
anything the user did not say:

```text
- USER | stage: <stage> | <question> — <answer> (<rationale>)
```

**The pending-question line.** Write it before returning `AWAITING_USER`,
and remove it only when that question is settled and applied. Preserve
it on a pause or skipped question; it is not a failure marker:

```text
- QUESTION | stage: <stage> | step: <workflow step> | <question>
```

The returned packet carries the options and recommendation; the durable
line identifies the decision the owning stage must reconstruct on
resume. Include the review iteration in the stage label when applicable,
so an earlier plan approval cannot approve new remediation work.

**The hand-back line.** One per failure or safety yield, written by the
**yielding stage** before it returns, into the same section of the same
artifact:

```text
- HANDBACK | stage: <stage> | attempt <k> | <reason>
```

All follow the labelled-entry convention `plan.md`'s `## Progress Log`
already uses, **including the leading `- `**. All go in a named section
of the stage's own artifact:

| Artifact | Section |
|-|-|
| `featurerequest.md` | `## Assumptions` |
| `bugreport.md` | `## Notes` |
| `approach.md` | `## Notes` |
| `plan.md` | `## Notes` |
| `analysis.md` | `## Notes` |

`featurerequest.md` is the only slot artifact with an `## Assumptions`
section; the rest carry a `## Notes` section and no assumptions section,
so that is where their lines go. The **prefix**, not the heading, is
what makes them findable. Do not collapse the table to a single
`plan.md` destination: `plan.md` does not exist during the authoring and
approach stages, `dev-review` may not write it, and `dev-approach` may
not write the source artifact.

**Two carve-outs on the hand-back line, both mandatory.**

The same no-write constraints apply to `QUESTION` and `USER` lines.
Never manufacture an invalid artifact just to hold a record. Before its
first permitted write, a stage keeps the pending question and any
answers in its continuation and writes them when it creates its
artifact. If the run pauses first, report those records as **undurable**;
do not claim they will survive a new session.

- **A stage whose own skill forbids writing at the moment it yields
  writes no `HANDBACK` line.** The case that matters is `dev-do`'s
  **pre-flight refusal**: on a non-empty index it must stop without
  editing `plan.md` at all. The never-overridden rule wins — this
  directive never buys a write past a safety gate, and obeying it here
  would mean writing to a plan on a dirty tree, which is exactly what
  that gate exists to prevent. The byte-identical branch in § *Stage
  Dispatch* is what catches this outcome instead.
- **A stage whose artifact does not yet exist has nowhere to write.**
  `dev-request` can yield before `featurerequest.md` exists, `dev-plan`
  before `plan.md`, and `dev-review` writes `analysis.md` wholesale at
  the end. Such a hand-back is **undurable**: require the stage to say
  so in what it returns, and report it as undurable in the closing
  report and on resume — never silently. This is the earliest and least
  diagnosable class of failure there is, and it is the one the line
  otherwise no-ops on.

**Durability differs by stage, because two artifacts are rewritten
wholesale by the skill that owns them.**

- **`dev-review` overwrites `analysis.md` on every pass.** Its scope
  prompt cannot fire when `plan-slot` scope resolves — the normal case
  here — so the review stage normally writes no line at all. Any
  assumption, user answer,
  pending question, or hand-back it *does* write must be **re-emitted**
  by the next pass, unless that pending question has since been settled.
  That keeps an overwrite from destroying the record.
- **Every `dev-approach` mode rewrites `approach.md`.** That skill
  preserves the section its lines live in across a rewrite, which is
  what gives this stage's decisions and pending questions a home
  surviving both a re-judgment and a hand-back.

## The Assumption Ledger

Every question the run answered on the user's behalf in `automatic` is
recorded with five things: the **stage**, the **question**, the **answer
chosen**, a **one-line rationale**, and the **artifact and section** the
answer landed in.

Two rules make the ledger survive a hand-back — which is an *expected*
outcome, and therefore cannot be allowed to lose the review record.

**1. Every assumption is written to a greppable, named location in the
artifact the owning skill already writes.** The owning stage writes it,
as part of the pass that made the decision; you never write it yourself.
Its exact line format and its destination table live in § *The Standing
Directive*, under *The durable record*, because that block is what a
stage sub-agent is actually handed — a directive excerpted without the
prefix would teach the stage nothing, nothing would reach disk, and a
resumed run would have nothing to rebuild from.

The ledger line is **in addition to** the settled content the answer
becomes, never a substitute for it. The content is what a reader of the
artifact needs; the line is what the ledger is rebuilt from.

**2. Resume rebuilds the ledger from disk** before continuing, by
reading those sections out of the artifacts that already exist. A
resumed run therefore closes with the assumptions made *before* the
hand-back as well as after, together with any `- HANDBACK |` line those
same sections carry.

**Keep actual user decisions separate.** Rebuild `USER` entries alongside
the assumptions, with their artifact locations, and read pending
`QUESTION` entries before choosing a resume point. Never relabel an
assumption as user-approved merely because the run resumes interactively,
or discard user decisions when it resumes automatically.

In `automatic`, this is the closing report's most prominent section.
In either mode, the assumption ledger is never summarized away, never
truncated, and never folded into a sentence about how the run "made some
assumptions along the way".

## Resume

A re-invocation with the same arguments **resumes**; it needs no resume
flag. The optional `mode` still defaults to `automatic` when omitted.
An explicitly changed mode applies to remaining decisions, not to work
already completed. Pick up at the first incomplete stage,
judged by **status markers, never by file presence** — a file that
exists proves a stage started, not that it finished.

**Pending user input takes precedence over readiness markers.** Read
`QUESTION` lines first and return to the owning stage's recorded step,
not to the start of its workflow. In `interactive`, reconstruct and
relay the question. In `automatic`, have that stage resolve it under
the automatic directive and record an assumption instead of a user
answer, removing the pending line once settled content is applied.
Genuine blockers are never converted to questions by a mode change.

**Check interactive approvals before consuming a ready artifact.** Before
an incomplete plan stage uses a selection, or an execution pass starts
pending phases, require a `USER` confirmation of the current approach
or plan respectively. If absent or no longer applicable, continue the
owning stage at confirmation only, without regenerating approaches or
repeating completed work. This also covers artifacts from an automatic
or hand-driven run. If you cannot establish that an approval still
applies, ask again rather than infer consent.

- **Authoring** is complete when the request's or report's `Status` row
  reads `Ready-for-plan`.
- **Approach** is complete when `approach.md` exists and carries a
  `## Selected` section.
- **Plan** is incomplete **only** when the `Status` row of `plan.md`
  reads `Draft`; any other value means the plan stage is done. One
  value, no ordering claim, and nothing to rot when `dev-plan` gains a
  status — where "`Ready-to-execute` or a later value" would assert an
  ordering that `Blocked` has no position in.
- **Execution** is complete when the plan's top-level `Status` reads
  `Complete` **and** every phase's `**Status:**` reads `Complete`.
- **Review tail** progress is the highest `<k>` carried by a
  `- REVIEW | iteration: <k> | complete` marker in `plan.md`'s
  `## Progress Log` — see § *The Review Tail*.

A `Blocked` marker means resume **re-enters** the stage that owns it
rather than skipping past it — and where `plan.md` is concerned, that
is scoped to the **phase** markers only. A plan whose *top-level*
`Status` reads `Blocked` because `dev-do` stopped mid-execution belongs
to the execution stage; re-entering the plan stage would re-dispatch
`dev-plan` against a plan that is already being executed. Rebuild the
assumption ledger and user-decision record before continuing, read any
`- HANDBACK |` line those same sections carry so the earlier attempt is
diagnosed rather than repeated, and say in your first response which
stage you resumed at and why. A stage that handed back **undurably**
left no line at all; say so rather than reporting a clean history.

## Retry and Hand-Back

**A failing phase gets three attempts in total — the first, plus two
retries.** The counter is **per phase**, not per stage, so one stubborn
phase cannot spend a long plan's whole budget. The bound is internal and
is deliberately **not** a caller knob: a phase that keeps failing is a
wrong phase, and retrying it harder will not make it right.

**A stage gets three work attempts in total, for the same reason.** These
are **two different counters**, and conflating them is what lets a run
spin. The per-phase bound is scoped to a *failing* `Blocked` phase, so
it counts nothing at all for a stage that yields early, hands back
without a `Blocked` marker, or returns unchanged — and those are exactly
the cases a re-dispatch loop is made of. Count work attempts per stage,
across resumes, using the `attempt <k>` on that stage's `- HANDBACK |`
line; a stage that exhausts three is a blocker. An interactive question
and its answer continuations belong to the same attempt, even when the
host needs a fresh runner to continue it. Three questions must never
exhaust a stage's failure budget.

**An unchanged-artifact retry spends one of those three work attempts.** It
opens no fourth counter: § *Stage Dispatch* bounds it at one retry, and
that retry starts a new work attempt. The count has one honest limit
— an unchanged return writes no `- HANDBACK |` line, so nothing records
it on disk and the tally is **in-session only**. A run resumed tomorrow
starts that stage's unchanged count at zero and may therefore repeat the
one retry. Bounded, cheap, and undurable: report it as undurable, in the
register the second carve-out in § *The Standing Directive* already
uses, rather than implying a count that survived the session.

**A retry is a diagnose-then-resume dispatch, never a bare
re-dispatch.** `dev-do` § *Iteration Mode (Recovery Path)* resumes a
`Blocked` phase only when the blocker is demonstrably resolved, and
otherwise reports it unchanged — so a bare re-dispatch would burn all
three attempts without ever retrying anything. Attempt *k* hands the
stage sub-agent the recorded `Blocked` reason **and** an explicit
instruction to diagnose and resolve the cause **first**, then resume the
phase. Without that the bound absorbs nothing — not the typo, not the
missing import, not the stale artifact it exists for.

**Each attempt is recorded durably by the stage itself**, using
`dev-do`'s existing log form:

```text
- NOTE | phase: <n> | attempt <k>: <what was tried>
```

That is what makes `plan.md` name the phase that failed *and what was
tried*, rather than leaving it in a transcript that dies with the
session.

**A stage that yields for failure or safety writes `- HANDBACK |`**, in the
form and destination § *The Standing Directive* fixes, before it
returns — so the reason survives the session that produced it, and so a
run resumed tomorrow can see that attempt 1 already failed the same way.
The two carve-outs there are the only exceptions, and the second of them
makes the hand-back **undurable**, which you report as undurable rather
than passing over in silence.

**A blocker is a condition the run cannot proceed *through*.** It is
never merely a question the run would prefer a human answered. These are
blockers:

- a build, test, or lint command the repository's `AGENTS.md` does not
  document;
- a failure the run cannot explain;
- a repository state it cannot safely act on, including anything
  `dev-do`'s pre-flight gate refuses;
- a stage that returns without pending user input and with its artifact
  byte-identical to the baseline recorded before the dispatch, on **two
  consecutive dispatches** — the first such return earns the
  diagnose-then-retry in § *Stage Dispatch*
  instead;
- a `dev-do` **scope-exceeded yield** — non-overridable, marking nothing
  `Blocked` and requiring no `NOTE`, so nothing else in this list would
  catch it;
- a phase or stage that exhausts its three work attempts;
- a missing stage skill file;
- a change the run made to **this** skill.

**On hand-back, report:** the stage and the phase, the attempts and what
each one tried, the evidence, the ledger rebuilt so far, whether the
stage's own `- HANDBACK |` line reached disk or the hand-back was
undurable, and **the exact command that resumes the run**. Quote that
command in **full-path form**, never as a bare slot number — a number
re-expands against the date of whatever day the user picks the work back
up, which is not necessarily today. Include the resolved `mode`,
`max_subagents`, and `review_iterations` explicitly so resuming cannot
silently change the run's interaction or budget.

**A user-deferred pause is not a blocker.** Report `awaiting user`, the
pending question and artifact, any undurable answers, and the same
full-path resume command. Preserve work already completed and spend no
failure attempt while the user decides, asks questions, or edits.

## The Review Tail

One **iteration** is a `dev-review` pass **plus** remediation of what it
raised. `review_iterations` counts iterations, not passes.

**Scope resolves itself.** `dev-review` derives `plan-slot` scope from
the plan's `COMMIT` entries without asking. When the plan carries **no**
`COMMIT` entries there is nothing to review. Skip the tail, and then
report it in the closing report, naming why.

**Remediation covers Blocker *and* High findings, not Blockers alone.**
That is not a choice this skill gets to make: `dev-plan` § *Iteration
Mode* names Blocker and High as the fold-back set, and `dev-review`
§ *Report Format* prescribes an `analysis.md` whose `## Next Steps`
section says the same. A narrower rule here would contradict the two
skills this stage dispatches. Lower severities are reported, not
remediated.

**Remediation runs as a `dev-plan` iteration pass** handed
`analysis.md`, which appends new phases to `plan.md`. Those phases
**keep `dev-plan`'s numbered heading** and carry the marker in the name:

```text
### Phase <n>: Remediation R<k> — <name>
```

`<n>` continues the plan's existing numbering and `<k>` is the 1-based
iteration index. The number is load-bearing: `dev-do`'s `PENDING` /
`COMMIT` / `NOTE` log entries are keyed on `phase: <n>`, so an
unnumbered heading would break the log form and change an existing
skill's output format. A `dev-do` pass then executes those phases under
its ordinary phase-commit protocol.

**Every iteration must leave a marker, clean or not.** A review that
raises no Blocker or High appends no phases, so remediation phases alone
cannot distinguish "iteration *k* ran clean" from "iteration *k* never
ran" — and guessing wrong burns a full two-pass review and overwrites
`analysis.md` again. The **`dev-plan` remediation stage** therefore
appends one line to `plan.md`'s `## Progress Log` on **every**
iteration, clean or not, before it returns **completed**:

```text
- REVIEW | iteration: <k> | complete
```

An interactive question return is not completion. Write this marker only
after the remediation plan's questions and any execution approval have
been settled, never merely because the stage returned `AWAITING_USER`.

**Iteration *k* is complete when a marker naming `<k>` is present**, and
the tail's progress is the highest `<k>` recorded. Keep `analysis.md`'s
`## Scope` **Commits** bullet as a cross-check on what the surviving
analysis actually saw — never as the completeness test. Remediation
appends new `COMMIT` entries to the plan by construction, so the two
sets diverge for every iteration that raised a finding, and any test
comparing them is satisfiable only in the clean case. The `| Scope |`
metadata row records a counted description rather than the SHAs
themselves, so it is a cross-check on the count.

Two properties of the marker are load-bearing. It carries **no
`phase: <n>` key**, because a clean iteration appends no phases and so
has no phase number to name, and because a writer that is not `dev-do`
must not inject a phase-keyed entry into a log `dev-do` § *Iteration
Mode (Recovery Path)* reads as recovery evidence. And it **names its
writer explicitly** — the remediation `dev-plan` pass — because "the
remediation pass" does not exist in the clean case the marker was
invented to disambiguate. The form is a fourth labelled entry in a log
`dev-plan` owns and documents, so it extends *that* skill's vocabulary
and changes nothing about `dev-do`'s.

**`dev-review` overwrites `analysis.md` on every pass.** With
`review_iterations: 2` the surviving file is the second pass's. That is
`dev-review`'s documented behavior and is not changed here — say in the
closing report which iteration the file reflects.

**Any finding still standing when the budget runs out is reported**
alongside the ledger, and just as prominently. Exhausting the iteration
budget is not a failure and not a hand-back; it is a result the user
reviews.

## Progress Output

Print one short line per stage entered, per artifact written, and per
assumption or user decision recorded. Name the mode at the start.
Automatic runs still have a reader; interactive runs also have question
boundaries, where you relay the stage's prompt and wait.

**Those lines land at stage boundaries**, because that is where you
regain control. The execution stage in particular is **atomic from your
side**: `dev-do` runs every phase and makes every commit before it
returns, so no line of yours can appear between two phases. Neither mode
adds per-phase checkpoints. For those, drive the loop by hand and invoke
`dev-do` with its `checkpoint_every` input. Interactive mode gates the
plan before execution, not each phase inside it.

Keep it to one line each. A stage's own output is that stage's business;
you are reporting the shape of the run, not narrating it.

## Closing Report

In this order:

1. **The slot and mode** — the resolved absolute path and `automatic` or
   `interactive`. Distinguish completion, awaiting user, and a blocker.
2. **The artifacts** — which ones exist, and one line on what each says.
3. **The commits** the execution stage made, SHA and subject, in
   chronological order.
4. **The assumption ledger** — every question the run answered on the
   user's behalf, in full: one entry per question, with the stage, the
   answer, the rationale, and where it landed. **This is the most
   prominent part of an automatic run's report, and must not be
   summarized away in either mode.** For automatic decisions this is the
   user's review point. Say explicitly when no assumptions were made.
5. **User decisions and pending questions** — report actual user answers
   separately, where the owning stages recorded them, and anything left
   unanswered. Never count a skipped question as an approval.
6. **Standing findings** — anything `dev-review` raised that the
   iteration budget did not close, at the same prominence as the
   ledger, plus which iteration the surviving `analysis.md` reflects —
   or state that the review tail did not run, and why.
7. **Next steps**, named as available to the **user** and never
   performed: `dev-issue` to publish the request or report and attach
   the plan, `dev-pr-open` to push the branch and open the pull request.
   State plainly that nothing was pushed and no pull request was opened.

## Important Rules

- **This skill owns no artifact.** Every slot file belongs to the skill
  that writes it. Read them all; write none of them. When a stage's
  output is wrong, re-dispatch the stage — never edit its file to fix
  its work.
- **Automatic resolves; interactive asks. A blocker stops both.**
  Default to `automatic` only when the mode is omitted, not when a user
  declines to answer. An interactive question is a pause within the
  stage, not a failure, an approval, or a reason to spend a retry.
- **Never invent a build, test, or lint command.** Commands come from
  the repository's `AGENTS.md`, with the documented fallback to
  `README.md` / `CONTRIBUTING.md` and an obligation to state which
  source was used. A command that is not documented is a blocker, and
  the standing directive never overrides that.
- **Every hand-off and publish offer resolves to *decline*.** Not on the
  merits, not "usually" — always. It is the one class of question the
  standing directive answers with a fixed value, because the alternative
  puts a GitHub write one inference away.
- **Local commits only.** No `git push`, no pull request, no commit
  outside `dev-do`'s phase-commit protocol, and no commit made by you
  directly. Pushing and opening a pull request belong to `dev-pr-open`,
  which the user invokes.
- **No GitHub writes at all**, and `analysis.md` and `approach*.md` are
  **never** published — not as an issue, not as a comment, not as a
  quotation in a pull request body. Fetching an issue the user named as
  content is a read, and is allowed.
- **Today's date governs slot expansion.** Never reuse a previous day's
  `<MMDD>` for a numeric slot. For an earlier slot the user must give a
  full path — and once resolved, the absolute path is what you use for
  the rest of the run.
- **The concurrency cap is a hard ceiling.** Pass `max_subagents`
  through unchanged and never exceed it. You dispatch one stage
  sub-agent at a time, and it is not counted against the cap.
- **Re-invocation resumes rather than restarts.** There is no flag naming
  a stage to start at; infer it from pending questions and status markers.
  A changed mode affects remaining decisions only. Always include the
  resolved mode in the full-path resume command.
- **Stop and hand back if the run modified this skill.** A stage
  sub-agent reloads its own instructions on the next dispatch, which is
  what makes `dev-do`'s self-modification yield safe. You cannot reload
  *yourself* mid-run. If a completed phase changed `dev-complete`,
  record the durable result and hand back, so that a fresh invocation
  loads the new instructions before anything else runs.
- **Honor repo conventions.** Repository conventions live in
  `AGENTS.md`. Follow its code style and architectural invariants, and
  where it is silent, match the surrounding code rather than importing a
  preference from another repository.
