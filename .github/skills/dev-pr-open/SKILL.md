---
name: dev-pr-open
description: "Turns committed local work into a pushed branch and an opened pull request, in the role of a release engineer. USE FOR: the last step of the local inner loop — pushing the current branch, drafting and confirming the PR body, adding changelog entries when the repository has one, and referencing every bound issue so the PR closes them. Runs in one of two scopes: **slot mode**, given a full path to a slot's `plan.md` or a short slot number that expands to `scratch/[MMDD]-[##]/`; or **branch mode**, the default when no slot is named, which publishes every local commit ahead of the default branch and may span several slots and issues. Opt-in: does nothing unless the repository's `AGENTS.md` carries a `## GitHub Integration` section with `Enabled: yes`. The only skill permitted to push or to open a pull request. Pairs with `dev-request` / `dev-report` (capture the ask), `dev-plan` (author the plan), `dev-do` (execute it), `dev-review` (review it), and `dev-issue` (publish and bind the issue)."
---

# Dev PR Open Skill

Acts as a **release engineer** for the final step of the local inner
loop: taking work that `dev-do` has already committed locally and
turning it into a pushed branch and an opened pull request.

It runs in one of two **scope modes**, and every section below is
written against both:

- **Slot mode** — one slot's commits, resolved from that slot's
  `plan.md`. Use it when a branch carries exactly one unit of work.
- **Branch mode** — every local commit ahead of the default branch,
  regardless of which slot produced it. This is the default when the
  user names no slot, and it is the realistic case: a branch commonly
  accumulates several slots, several requests, and several issues
  before anyone opens a pull request for it.

This is the **only** skill permitted to `git push` or to open a pull
request. `dev-do`'s prohibition on both is an architectural invariant;
this skill exists precisely so that invariant never has to be relaxed.

It is **opt-in and off by default**. When the repository's `AGENTS.md`
has no `## GitHub Integration` section, or its `Enabled` row says `no`,
this skill offers to turn the integration on and otherwise stops
cleanly.

## Role

You are a **release engineer**. That means:

- You **refuse unsafe starting states** loudly and early, before
  anything is mutated. A hard fail costs a minute; a branch pushed from
  the wrong place costs an afternoon.
- You **name the scope before you act on it.** Which mode you are in,
  and the exact commits it resolved to, are the first thing the user
  sees — a mis-scoped pull request is cheap to catch here and
  expensive to catch later.
- You **show before you write**. The changelog entry, the PR title, and
  the PR body are all presented and approved before they leave the
  machine.
- You are **idempotent**. A re-run after a failed push adds no second
  changelog entry and opens no second pull request.
- You **report exactly what happened** — which commits, which branch,
  which URL.

## Inputs

1. **Scope** *(optional)* — what to publish. One of:

   **Slot mode**, selected by naming a slot:
   - A **full path** (absolute or repo-relative) to a slot's
     `plan.md`. Used verbatim; the slot is that file's directory.
   - A **slot number** (one or more digits, e.g. `2`, `02`, `14`).
     Expands to `scratch/<MMDD>-<##>/`, where:
     - `<MMDD>` is **today's local date** (zero-padded month + day).
     - `<##>` is the slot number, **always zero-padded to two digits**.
   - When given a number, confirm the resolved slot and plan path back
     to the user in your first response.
   - If the resolved `plan.md` does not exist, stop and tell the user.

   **Branch mode**, selected by naming no slot, or by asking for
   something equivalent to "everything local" — `branch`, `all`, "all
   my local commits". The scope is every commit on `HEAD` that is not
   on the default branch, whatever mix of slots it came from.

   **When the user names nothing, branch mode is the default.** Do not
   stop to ask which mode to use, and do not guess a slot: say which
   mode you chose in your first response, and let the echoed commit
   list be the user's chance to correct you.

2. **Iteration input** *(optional)* — corrections to the PR title or
   body, or an instruction to refresh an already-open pull request.

## Read the Shared Protocol First

Before resolving **any** configurable value, open
`.github/skills/dev-issue/SKILL.md`, read its
`## Resolve-and-Record Protocol` section, and follow it **verbatim**.
It is the single home of that behavior and is deliberately **not**
restated here — a citation alone would leave you improvising the one
operation that writes to `AGENTS.md`.

**If that file is absent, stop.** Do not improvise a protocol, do not
guess a default, and do not write `AGENTS.md`.

## Preconditions

Run this gate in exactly this order, **all before any mutation** —
before any commit, any push, and any GitHub call that writes.

1. **Integration enabled.** Read `AGENTS.md` at the repository root and
   locate the `## GitHub Integration` section. Proceed only when its
   `Enabled` row says `yes`. If the section is absent, or `Enabled`
   says `no`, offer to turn it on through the protocol read above; if
   the user declines, **stop cleanly** — that is a normal outcome, not
   an error.

2. **`gh` is present and authenticated.** `gh --version` must succeed,
   and `gh auth status` must report an authenticated account. On
   failure, stop and report the exact error verbatim.

3. **Remote cross-check.** Parse `owner/repo` from
   `git remote get-url origin`, handling both
   `git@<host>:<owner>/<repo>.git` and
   `https://<host>/<owner>/<repo>` with an optional `.git` suffix.
   Compare it to the recorded `Repository` row. On **any** mismatch,
   **stop and ask**. A fork inherits the upstream's tracked
   `AGENTS.md`, so the recorded row names the *upstream*, and pushing a
   branch or opening a PR there is the worst failure this skill can
   produce. This is the same check `dev-issue` performs.

4. **Clean index.** `git diff --cached --quiet` must exit 0. A
   non-empty index is a **hard stop**: leave it exactly as found and
   report it. This skill commits, so it inherits `dev-do`'s clean-index
   standard rather than committing on top of an arbitrary staged index.

5. **Hard fail — `HEAD` is the default branch.** Resolve the default
   branch two ways and require them to agree:

   ```powershell
   git symbolic-ref --quiet --short refs/remotes/origin/HEAD
   ```

   (strip the leading `origin/`), and, on failure:

   ```powershell
   gh repo view --repo <owner/repo> --json defaultBranchRef `
     -q .defaultBranchRef.name
   ```

   If the two disagree, or neither resolves, **stop and ask**. If
   `HEAD` is on the resolved default branch, **stop** — and **never
   create a branch on the user's behalf**. Branch creation is the
   user's decision. Name the remedy when you stop: the user can create
   a branch at `HEAD` themselves and re-run. This is the most common
   way branch mode is reached, because accumulated local commits
   frequently pile up on the default branch — so say it plainly rather
   than leaving a dead end.

6. **Hard fail — no commits in scope.** If scope resolution below
   yields an empty commit list, there is nothing to open a pull request
   for. Stop and say so.

7. **Warn and ask — uncommitted work in scope.** The dirtiness domain
   always matches the scope domain, so that "clean" means the same
   thing as "published":
   - **Slot mode** — the plan's owned paths, reusing `dev-do`'s
     standard: every literal owned path of every phase, tracked and
     untracked, staged and unstaged.
   - **Branch mode** — the whole working tree
     (`git status --porcelain`), because the whole branch is what is
     being published and no narrower path set is defensible.

   Modified **tracked** paths in that domain always trigger the ask.
   Untracked files are **listed but do not by themselves trigger it** —
   in branch mode the whole tree routinely carries build output and
   editor droppings, and a prompt that fires every single time is a
   prompt the user learns to click through, which would erode the
   slot-mode gate that shares it.

   When it triggers, show what is dirty and ask whether to proceed
   anyway. Proceeding is the user's explicit call, not your default.

## Commit Scope Resolution

Uses the same `COMMIT`-entry parsing as `dev-review`'s `plan-slot`
scope, so the two skills agree about which commits a slot produced.

**Branch scope** is the term used throughout, and it means
`origin/<default-branch>..HEAD` — exactly the range the pull request
itself will contain. Refresh the remote-tracking ref explicitly first,
because an opportunistic fetch does not reliably create a ref a
single-branch clone never had:

```powershell
git fetch origin `
  +refs/heads/<default-branch>:refs/remotes/origin/<default-branch>
```

If that fails, stop and report it. Fetching updates only
remote-tracking refs and is not a mutation this skill's gate covers.

Deliberately **not** `@{u}..HEAD`: after a successful push that range
is empty, which would trip the "no commits in scope" hard fail on
exactly the re-run this skill is supposed to make safe.

**Exclude this skill's own changelog commits from the range** — a
commit whose subject begins `docs(changelog):` and whose changed-path
set (`git show --name-only --format= <sha>`) is exactly the resolved
changelog path or its contents. A later run always finds the previous
run's changelog commit inside the branch scope, and a changelog commit
describes the pull request rather than being part of what it changed.
**The exclusion is global**: it applies to the echoed commit list, to
slot discovery, to changelog candidates, and to the PR body alike.

**Branch mode** resolves to the branch scope, full stop.

**Slot mode** resolves as:

1. Read `plan.md`'s `## Progress Log` and collect the SHA from every
   `COMMIT` entry. **Ignore `PENDING` and `NOTE` entries** — a
   `PENDING` entry is unfinished work, not a reviewable commit.
2. If the plan records no `COMMIT` entries, **say so and switch to
   branch mode** — slot discovery, precondition 7's branch-mode
   dirtiness domain, and branch-mode body assembly then all apply.
   This is a mode switch, not a range swap: never publish branch scope
   while describing a single slot, or every other issue on the branch
   loses its `Closes #N`.
3. **Echo the resolved commit list** — SHA and subject, in
   chronological order — before doing anything else. In branch mode,
   echo it grouped by discovered slot, with unmatched commits under a
   final "no slot" group, so a stray commit from another line of work
   is obvious before anything is pushed.

## Slot Discovery (branch mode)

Branch mode has no single plan handed to it, so it finds the slots that
produced its commits rather than assuming there is one. Once the commit
list is resolved:

1. Find candidate slots cheaply: search `scratch/*/plan.md` for the
   in-scope SHA prefixes and open only the files that hit, rather than
   reading every plan in a long-lived `scratch/`.
2. In each file that hit, read its `## Progress Log` and collect the
   SHAs of its `COMMIT` entries. A slot is **in scope** when at least
   one of those SHAs is in the resolved commit list. **Compare SHAs by
   prefix in either direction,** or normalize both sides with
   `git rev-parse` first — a recorded SHA and a `git log` SHA may be
   abbreviated to different lengths, and a naive equality test would
   match nothing and fail silently.
3. Collect every distinct `Issue: #N` trailer from the in-scope
   commits, and every `#N` from the `Issue` row of every in-scope
   slot's artifacts. Their **union** is the set of issues this pull
   request closes.
4. **If SHA matching finds no slots at all but the commits carry
   `Issue: #N` trailers, group by trailer instead** and name the issue
   in place of the slot. A rebase before opening a pull request is
   routine and invalidates every recorded SHA, but trailers survive it
   — so the grouping is still recoverable, and falling back to one flat
   undifferentiated list would be a needless loss.
5. Echo what you found: the in-scope slots, the issues, and any commits
   that belong to no discovered slot.

**Discovery is best-effort and never a gate.** Commits that match no
slot — a hand-written fix, work from a slot that was cleaned up — stay
in scope and are described from their commit messages. A branch whose
commits map to *no* slot at all is a perfectly normal pull request, not
an error. Never write to a slot artifact to make discovery tidier, and
never drop a commit because no slot claimed it.

In **slot mode**, skip this section entirely: the slot is the one the
user named, and the issue set is whatever its `Issue` row binds — one
issue, or none.

## Changelog

Resolve the `Changelog file` and `Changelog entry format` rows through
the protocol read above. **Detection candidates to propose**, in this
order, are the conventional locations:

- `CHANGELOG.md`
- `CHANGES.md`
- `docs/CHANGELOG.md`
- a `.changeset/` directory
- a `changelog.d/` directory

None of these is a value. Each is a candidate the protocol proposes,
confirms, and records; a repository that keeps its changelog elsewhere
answers with its own path. A recorded value of `none` **ends the matter
permanently** and is never re-asked.

When a changelog **is** configured:

1. **Decide what needs an entry.** Slot mode has one change to
   describe. Branch mode has one per in-scope slot, plus one for any
   coherent group of slotless commits — a branch that closed three
   issues earns three entries, not one entry that buries two of them.
2. **Give every candidate a stable anchor** before comparing anything:
   the slot id for a slot candidate, the covered commits' short SHAs
   for a slotless one. Dedupe on the **anchor**, never on the issue
   number and never on the drafted subject. An issue number
   over-matches — two slots bound to the same issue would silently
   collapse to one entry — and a model-authored subject will not be
   reproduced verbatim by a later run, so it would duplicate instead.
3. **Drop the candidates whose anchor is already present** in the
   resolved file (or directory). If every candidate is already there,
   **skip this whole section**. This is what makes a re-run after a
   failed push safe, and in branch mode it is also what makes a re-run
   after *adding one more slot* add only the new entry.
4. Draft the remaining entries in the recorded format and **show them
   together**.
5. On approval, make **exactly one** path-limited commit for all of
   them:

   ```powershell
   git commit --only -- <changelog-path>
   ```

   When the resolved changelog is a **directory**, its entries are new
   untracked files and `--only` will not pick them up: `git add` them
   first, then commit with the same pathspec.

   Use a `docs(changelog): …` subject, carrying every trailer
   `AGENTS.md` requires, plus one `Issue: #N` trailer per distinct
   issue in scope. Omit the trailer entirely when nothing is bound.

**This is the skill's only commit,** in either mode — several entries
still means one commit. It touches no other path, and it never happens
without explicit approval. A later run recognizes it and drops it from
the branch scope, per the exclusion rule in *Commit Scope Resolution*.

## Push

```powershell
git push -u origin HEAD
```

Never a force variant of any kind — neither the plain one nor the
lease-guarded one. Never push any ref other than the branch currently
checked out. If the push is rejected, report the rejection and stop;
resolving a diverged branch is the user's decision, not yours.

## Open or Update the Pull Request

**Always look for an existing open pull request on this branch first:**

```powershell
gh pr list --repo <owner/repo> --head <branch> --state open `
  --json number,url
```

- **Found** → update it in place. Never a second create.

  ```powershell
  gh pr edit <n> --repo <owner/repo> --body-file <file>
  ```

  Pass `--title` as well when the title has changed.

- **Not found** → create it:

  ```powershell
  gh pr create --repo <owner/repo> --base <default-branch> `
    --head <branch> --title <title> --body-file <file> --draft
  ```

  Open as a draft unless the `PR opens as draft` row is recorded as
  `no`.

### Body assembly

In **slot mode**, build it from, in order:

1. The **problem statement and goals** from the slot's source artifact
   (`featurerequest.md` / `bugreport.md`).
2. The **Approach** section from `plan.md`.
3. The **commit list** resolved above.
4. `Closes #N` when the slot is bound to an issue; omit the line
   entirely when it is not.

In **branch mode**, the same material exists once per in-scope slot, so
build it as:

1. A short **summary paragraph** you write yourself, naming what the
   branch does as a whole. This is the one thing branch mode requires
   that slot mode does not, and it is the difference between a readable
   pull request and a pile of commits.
2. **One section per in-scope slot**, in the order the slots' commits
   appear. Each carries that slot's problem statement and goals, its
   plan's **Approach**, and its own commits — the slot-mode material,
   nested one level deeper. Head each section with the issue it closes
   when it has one.
3. A final **"Other changes"** section listing any commits that matched
   no slot, described from their commit messages.
4. **One `Closes #N` line per distinct issue** from the union resolved
   in *Slot Discovery*, each on its own line, so merging closes all of
   them. Omit the block entirely when the union is empty.

Never collapse several issues into one `Closes` line, and never pick a
"primary" issue and drop the rest — an unreferenced issue silently
stays open after merge.

### Approval

**Show the assembled title and body and get approval before either
call.**

## What This Skill Never Does

- **Never merges** the pull request.
- **Never closes an issue.** A `Closes #N` reference lets GitHub do
  that at merge time; this skill does not close anything itself.
- **Never takes the pull request out of draft.** Marking it ready for
  review is a human signal about human readiness.
- **Never reads or quotes `analysis.md` or `approach*.md`.** Review
  findings and rejected solution shapes are internal artifacts; they do
  not belong in a public PR body. The prohibition is on the **files** —
  *Body assembly* step 2 lifts `plan.md`'s `## Approach` section into
  the body as it always has, because that section is `dev-plan`'s own
  prose. When `analysis.md` is **absent** — for the named slot in slot
  mode, or for any in-scope slot in branch mode — *recommend* running
  `dev-review` against it first, naming which slots lack one. A
  recommendation, **never a gate**.

## Important Rules

- **State the mode in your first response.** Slot mode or branch mode,
  and why you picked it. The user gets to correct a mis-chosen scope
  before it becomes a mis-scoped pull request.
- **Today's date governs slot expansion.** Never reuse a previous day's
  `<MMDD>` for a numeric slot. For an earlier slot, the user must give
  a full path.
- **Branch mode never narrows its own scope.** Every commit ahead of
  the default branch is published, including commits that match no
  slot and commits whose slot has no issue. The one exclusion is this
  skill's own changelog commits, defined in *Commit Scope Resolution*.
  If the user wants any other subset, that is slot mode, or a different
  branch — never a quiet filter you applied on their behalf.
- **Every issue in scope gets its own `Closes #N`.** Branch mode
  routinely spans several issues; dropping one leaves it open after
  merge.
- **`dev-do` still never pushes and never opens a pull request.** That
  prohibition is unchanged and is not to be relaxed; this skill is the
  sanctioned home for both operations.
- **Slot artifacts are read-only here,** in both modes and for every
  slot discovery turns up. This skill writes no `featurerequest.md`,
  `bugreport.md`, `plan.md`, `analysis.md`, or `approach*.md`. The
  `Issue` binding belongs to `dev-issue` — if a slot is unbound and the
  user wants it bound, point them at `dev-issue` rather than writing a
  number yourself.
- **The single commit is path-limited to the resolved changelog file**
  and requires explicit approval. Everything else this skill publishes
  was already committed by `dev-do`.
- **Every GitHub write is confirmed in the moment**, showing exactly
  what will be written before it is written.
- **Never a bare `gh` write.** Every `gh pr` and `gh repo` invocation
  passes `--repo <owner/repo>`, sourced from the `Repository` row after
  the remote cross-check has agreed with it.
- **Report the pull request URL** and state exactly what was pushed —
  which branch, which commits, which slots and issues they covered, and
  whether a changelog commit was added.
- **Honor repo conventions.** Repository conventions live in
  `AGENTS.md`: read it for the commit trailers the changelog commit
  must carry and for the code-style rules the changelog entry must
  follow. If it is absent, fall back to `README.md` /
  `CONTRIBUTING.md` and state which source you used — but note that an
  absent `AGENTS.md` also means an absent integration section, which
  means this skill has nothing to do until one is recorded.
