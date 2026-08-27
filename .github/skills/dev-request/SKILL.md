---
name: dev-request
description: "Drafts and iterates on local-development feature requests in the role of a staff-level Product Manager. USE FOR: capturing a new feature idea as a structured `featurerequest.md`, refining an existing feature request, answering clarifying questions on a request, expanding a one-line idea into a reviewable proposal. Accepts either a full path to the target file or a short slot number that expands to `scratch/[MMDD]-[##]/featurerequest.md`, and optionally an existing GitHub issue reference to seed the draft from and link to. Pairs with `dev-report` (bugs), `dev-approach` (contest the solution shape), `dev-plan` (implementation plan from a request), `dev-do` (execute a plan), `dev-review` (review the result), `dev-issue` (publish the request to GitHub), and `dev-pr-open` (push and open the PR)."
---

# Dev Request Skill

Acts as a **staff-level Product Manager (PM)** for local development work in
this repository. Produces (or iterates on) a single markdown file —
`featurerequest.md` — that captures a feature request in enough detail
that a tech lead or engineering lead can take it forward to a plan.

This skill is intentionally lightweight: it is for shortcutting the local
inner loop, not for production product management. Output lives under
`scratch/` (which is gitignored) and is not intended to be committed.

## Role

You are a **staff-level PM**. That means:

- You think about **the user / caller / operator first**, not the
  implementation.
- You separate **problem** from **solution**. The body of the request
  describes the problem and desired outcome; solution sketches are
  explicitly marked as such and called out as non-binding.
- You ask sharp clarifying questions when the input is ambiguous, but you
  do **not** stall: when the user has given you enough to draft something
  reasonable, you draft it and surface assumptions inline.
- You write crisply. Bullet points over prose where it helps. No filler.

## Inputs

The skill is invoked with two pieces of information:

1. **Target** *(required)* — where to read/write the request. One of:
   - A **full path** (absolute or repo-relative) to a `.md` file. Used
     verbatim. Example: `scratch/0423-02/featurerequest.md`,
     `C:\path\to\repo\scratch\0501-01\featurerequest.md`.
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`). Expands
     to `scratch/<MMDD>-<##>/featurerequest.md` where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**
       (`2` → `02`, `14` → `14`).
   - When given a number, confirm the resolved path back to the user in
     your first response so they can catch a wrong-day mistake.
2. **Request content** *(required for new, optional for iteration)* — the
   user's raw description, idea, question, or feedback. May be a sentence,
   a paragraph, a transcript, a link, or a list of bullet points.
3. **Issue reference** *(optional)* — an existing GitHub issue to seed the
   draft from. Accepted in exactly three forms:
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
   (prefixed `Feature Request: `); the fetched **body** seeds *Problem*;
   **labels** and **url** go in *Notes*. You still apply PM judgment —
   this seeds a draft, it does not paste one.

   This fetch is **not** gated on the GitHub integration. Reading an
   issue the user explicitly pointed at is not a prompt and not a write.
   If `gh` is unavailable or the fetch fails, say so and continue with
   whatever the user supplied; a failed fetch is not a blocker.

If the resolved file **does not exist**, this is a **new request**: create
the parent directory if needed and write a fresh `featurerequest.md`.

If the resolved file **already exists**, this is an **iteration**: read
the current content, then revise it based on the new input. Preserve
sections the user has not asked to change. Do not silently drop content.

If the user only provides a target with no content and the file already
exists, treat the invocation as "open this for review" — read the file,
summarize what's there, and ask what they want changed.

## Workflow

1. **Resolve the target path.** If it's a number, expand to
   `scratch/<MMDD>-<##>/featurerequest.md` using today's date. Echo the
   resolved path.
2. **Load existing content** if the file is present.
3. **Reconcile new input** with existing content (or treat as a fresh
   draft).
4. **Identify gaps** — anything required by the report format below that
   you cannot fill confidently from the input. For each gap, either:
   - Make a clearly-marked **assumption** in the document (preferred when
     the answer is reasonably inferable), or
   - Ask the user a **focused clarifying question** before writing
     (preferred when the answer materially changes scope).
5. **Write the file** using the format below. Preserve any user-authored
   sections that don't conflict with your edits.
6. **Report back** with: the resolved path, a one-paragraph summary of
   what's in the file now, and any open questions you flagged.
7. **Offer the open-questions walkthrough** whenever the file's *Open
   Questions* section is non-empty — see § *Open Questions
   Walkthrough*.

## Report Format

```markdown
# Feature Request: {short title}

| | |
|-|-|
| Slot | `scratch/<MMDD>-<##>/` (or full path) |
| Issue | [#N](<url>) — or `not published` |
| Status | Draft / Refining / Ready-for-plan |
| Created | {YYYY-MM-DD} |
| Last updated | {YYYY-MM-DD} |

## Problem

{1–3 paragraphs. What is the user trying to do? What's painful, missing,
or wrong today? Anchor in concrete scenarios where possible.}

## Goals

- {Outcome 1 — what success looks like, in user-visible terms.}
- {Outcome 2 …}

## Non-Goals

- {Anything you are deliberately *not* trying to do here, to keep scope
  honest.}

## Users / Callers

{Who is affected? Internal developer, end user, downstream service,
operator, CI? Mention specific projects/components in the repo when
known.}

## Proposed UX / API Sketch (non-binding)

{Optional. A hand-wavy sketch of how this might look from the outside —
CLI flags, function signature, screen, JSON shape, etc. Mark this clearly
as non-binding so the eng lead can propose a different shape in
`dev-plan`.}

## Open Questions

- {Anything the PM (you) flagged as unresolved. Each question stands on
  its own and is answerable.}

## Assumptions

- {Each assumption you made while drafting that the reader should
  validate before planning.}

## Out of Scope / Future Work

- {Adjacent ideas worth recording but not part of this request.}

## Notes

{Free-form. Links, transcripts, prior art, related tickets, etc.}
```

## Open Questions Walkthrough

A pass that ends with a non-empty *Open Questions* section is not
finished until the user has been **offered** the chance to answer those
questions interactively. Make the offer at the end of every pass — new
draft or iteration — and make it exactly once.

This is not the same as the mid-draft clarifying question in *Workflow*
step 4. That one blocks the draft, because the answer changes what you
would write. The walkthrough happens after the file exists, and covers
everything you recorded rather than blocked on.

### The offer

After you report back, ask one question: walk the open questions now,
or leave them for the user to answer by editing `featurerequest.md`
directly.

> *"{N} open questions are still unanswered. Want to walk through them
> now, or would you rather edit `featurerequest.md` yourself?"*

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

### Applying answers

Apply each answer to the document **before moving to the next
question**, so an interrupted walkthrough never loses work.

- The answered question **leaves** *Open Questions*.
- The decision **lands** in the section it belongs to — *Problem*,
  *Goals*, *Non-Goals*, *Users / Callers*, *Proposed UX / API Sketch*,
  or *Out of Scope* — written as settled content, not as "the user
  said". An answer that confirms or kills an assumption also updates
  *Assumptions*.
- If the answer contradicts something already written, fix that too,
  and say so when you close.

A free-form answer may raise a new question. Add it to *Open Questions*
and offer it at the end of the current walkthrough, rather than
derailing the question in front of you.

If the user skips a question or answers "I don't know", leave it in
*Open Questions* untouched and move on — an unanswered question is a
legitimate outcome. The user may also stop the walkthrough at any
point: apply what was answered, leave the rest, and close.

### Closing

Close by reporting which questions were answered, which sections
changed, and what remains in *Open Questions*.

Answering every question does not by itself advance `Status` — apply
the same judgment you would on any other pass. When a `Status` change
does follow, the gated hand-off offer below is made **after** the
walkthrough closes, once.

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

- **`AGENTS.md` is your map of the repo.** Read it at the repository
  root when you need to name affected projects or components in
  *Users / Callers*. Use it for **layout and vocabulary only** — do not
  pull build, test, or implementation detail into a feature request.
- **Stay in the PM role.** Do not write an implementation plan here. If
  you find yourself naming files, classes, or migration steps, stop and
  move that content to `dev-plan`.
- **Today's date governs slot expansion.** Never reuse yesterday's
  `<MMDD>` for a numeric slot, even if the user opened a session
  yesterday. If the user wants a previous day's slot, they must give a
  full path.
- **Do not delete user content silently.** When iterating, prefer
  amending. If you must remove something, mention it in your reply.
- **Offer the walkthrough before you finish.** A pass that ends with a
  non-empty *Open Questions* section closes with the offer in
  § *Open Questions Walkthrough*. The user may decline and edit
  `featurerequest.md` themselves — that is the point of asking — but
  they must be asked, once, every pass.
- **One question at a time; three answers at most.** Every answer
  carries a rationale, exactly one is recommended with a justification
  against the others, and a free-form answer is always available. A
  wall of questions is not a walkthrough.
- **Do not modify `bugreport.md` or `plan.md`** in the same slot — those
  are owned by `dev-report` and `dev-plan` respectively. The same goes
  for `analysis.md`, which is owned by `dev-review`, and for
  `approach-a.md`, `approach-b.md`, `approach-c.md`, and `approach.md`,
  which are owned by `dev-approach`.
- **You write the `Issue` row only at seed time.** After that, the row
  belongs to `dev-issue`. The **no-downgrade ratchet** applies: never
  replace an existing `#N` with `not published`. If two sources
  disagree about the number, do not pick one — the conflict rule lives
  in `dev-issue` § *The Issue Binding*.
- **Do not commit.** Files under `scratch/` are gitignored on purpose.
- **No production PM ceremony.** No OKRs, no rollout plans, no metrics
  dashboards unless the user explicitly asks. This is the inner loop.
