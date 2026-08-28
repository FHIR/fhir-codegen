# AGENTS.md

Canonical, machine-readable conventions for automated agents working in
**fhir-codegen**. This file is the single source of truth that the
`.github/skills/dev-*` skills read before naming any build, test, or lint
command.

**Precedence.** This file is authoritative for commands, conventions, and
invariants an agent must follow. [`README.md`](README.md) and
[`docs/`](docs/) are authoritative for rationale, configuration reference,
and operational detail, and are the place to look for the "why". If this
file contradicts the repository itself, the repository wins — fix this file.

---

## What this repository is

`fhir-codegen` ingests FHIR specification packages (NPM-style `hl7.fhir.*`
packages) and exports them into other languages and formats — TypeScript,
C#/Firely, OpenAPI, Ruby, SQLite, FHIR Shorthand, CQL, Info, and a
cross-version mapping pipeline.

The framing fact: this is a **code generator whose output is consumed by
other projects**. Output shape is the product. A change that alters
generated artifacts is a breaking change for downstream consumers even when
the C# compiles cleanly, so generation tests and their expected output carry
more weight here than they would in an ordinary library.

The libraries also ship as NuGet packages (see `fhir-codegen.props`), so
public API changes in `Fhir.CodeGen.*` are breaking changes.

---

## Repository layout

| Path | Contents |
|-|-|
| `src/fhir-codegen/` | `System.CommandLine`-based CLI (`OutputType=Exe`). |
| `src/fhir-codegen-shared/` | Shared Project (`.projitems`) imported by the CLI. |
| `src/Fhir.CodeGen.Common/` | Lightweight POCOs, shared models, polyfills. Dependency-light by design. |
| `src/Fhir.CodeGen.CrossVersionLoader/` | Load and reconcile artifacts across R2/R3/R4/R4B/R5. |
| `src/Fhir.CodeGen.MappingLanguage/` | FML (FHIR Mapping Language) parser/abstractions. |
| `src/Fhir.CodeGen.LangSQLite/`, `src/Fhir.CodeGen.SQLiteGenerator/` | SQLite export backend. |
| `src/Fhir.CodeGen.Lib/` | Core engine: loader → normalized model → language exporters. |
| `src/Fhir.CodeGen.Lib/Language/` | **The main extension point** — one `ILanguage` implementation per output format. |
| `src/Fhir.CodeGen.Lib/Packaging/` | The package boundary — codegen-owned types over `fhir-pkg-lib`. The **only** place permitted to name a `FhirPkg` type. |
| `src/Fhir.CodeGen.Comparison/` | Package/artifact diffing. |
| `src/Fhir.CodeGen.CrossVersionExporter/` | Produces cross-version artifacts. |
| `src/performance-test-cli/` | Standalone perf tooling. |
| `src/*.Tests/` | xUnit test projects, colocated with their target. |
| `src/Fhir.CodeGen.Lib.Tests/TestData/` | Test fixtures, copied to output via `PreserveNewest`. |
| `docs/articles/`, `docs/specs/` | Narrative docs and per-step pipeline specs. |
| `docfx/` | Documentation site generation. |
| `languageInput/` | Hand-maintained input assets for certain exporters. |

**Ignored paths** (see `.gitignore`): `/scratch`, `/generated`, `/temp`,
`/firely`, `/cytoscape`, `/fhirVersions` contents, `*.sqlite`. Nothing under
`/scratch` is ever committed.

---

## Toolchain pins

- **.NET 10 targeting pack is required.** There is **no `global.json`**, so
  the SDK version is a *floor*, not an exact pin — any SDK that can target
  `net10.0` works. CI pins `DOTNET_VERSION: '10'`
  (`.github/workflows/build-and-test.yml`); a .NET 10 SDK targeting `net10.0` is known-good locally.
- **Every project targets `net10.0`** except `Fhir.CodeGen.SQLiteGenerator`,
  which targets **`netstandard2.0`**. Do not "fix" that one to `net10.0`.
- `fhir-codegen.props` (imported by the project files) sets
  **`LangVersion 14.0`**, **`Nullable enable`**, **`ImplicitUsings enable`**
  solution-wide. Change these there, not per-project.
- Versions are declared **per-project** in each `.csproj`. There is no
  central package management and no lock file, so a dependency bump must be
  applied consistently across every project that references the package
  (the `Hl7.Fhir.*` family is currently `5.13.3` everywhere).
- **Warnings are not errors.** No project sets `TreatWarningsAsErrors`,
  `EnforceCodeStyleInBuild`, `AnalysisLevel`, or `AnalysisMode`.
- Accepted framework-adjacent package baselines after the .NET 10 upgrade: `Microsoft.Extensions.*` and `Microsoft.Data.Sqlite` are `10.0.11`, `System.CommandLine` is `2.0.11`, retained `System.Text.Json` references are `10.0.11`, and SQLite generator Roslyn packages are `5.9.0`. FHIR package acquisition comes from `fhir-pkg-lib 2026.803.800`, referenced only by `Fhir.CodeGen.Lib`. The FHIR family remains deferred at `5.13.3`, and `Microsoft.OpenApi` remains deferred at `1.6.29`.
- Tests need the **FHIR package cache** populated — see "Test" below.

---

## Build

```powershell
dotnet build fhir-codegen.sln -c Release
```

Scoped to a single project:

```powershell
dotnet build src/Fhir.CodeGen.Lib/Fhir.CodeGen.Lib.csproj -c Release
```

The expected baseline is **0 errors, 1 warning**. The one warning is
pre-existing and unrelated to any current work:

```
src/Fhir.CodeGen.Lib/SqlOnFhir/ViewDefinition.cs(159,40): warning CS3021:
'ViewDefinition.ConstantComponent.Value' does not need a CLSCompliant
attribute because the assembly does not have a CLSCompliant attribute
```

Anything beyond that should be investigated before it is attributed —
confirm against a clean checkout or `HEAD` before calling it a regression.

There is a **single build track**. `fhir-codegen` and `performance-test-cli`
are `Exe`; everything else is a library. No AOT, native, or publish-only
check exists.

---

## Test

**xUnit 2.9.3** with **Shouldly 4.3.0** for assertions — *not*
FluentAssertions. The runner is **VSTest**
(`Microsoft.NET.Test.Sdk 18.9.0` + `xunit.runner.visualstudio 4.0.0`);
there is no `global.json` `"runner"` entry and no `OutputType=Exe` test
project, so **Microsoft.Testing.Platform is not in use**.

That means **`dotnet test --filter <expression>` is the valid filter
syntax**, supporting `FullyQualifiedName~Substring`,
`FullyQualifiedName=Exact`, and trait expressions such as
`RequiresExternalRepo!=true`. Do not use MTP's `-class` / `-method` flags.

### Full suite

```powershell
dotnet test --configuration Release --framework net10.0 --filter "RequiresExternalRepo!=true"
```

This is exactly what CI runs. **Always keep the
`RequiresExternalRepo!=true` filter**: tests carrying
`[Trait("RequiresExternalRepo", "true")]` clone the HL7 cross-version IG
repositories and are skipped in CI.

### Scoped — one project

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "RequiresExternalRepo!=true"
```

Test projects: `Fhir.CodeGen.Lib.Tests`,
`Fhir.CodeGen.MappingLanguage.Tests`, `fhir-codegen.Tests`.

### Focused — one class or one test

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName~GenerationTests"
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.GenerationTests.MyTest"
```

**Prefer the smallest command that covers the change.** Escalate to the full
suite only when the focused run indicates you need to.

### Required setup — the FHIR package cache

Most tests load real FHIR core packages from **`~/.fhir`** and will fail if
the cache is empty. Populate it once, either by running `fhir-codegen`
against the packages, or with `firely.terminal` the way CI does:

```powershell
dotnet tool install -g firely.terminal
fhir config inflate off
fhir config regenerate off
fhir install hl7.fhir.r2.core 1.0.2 ;  fhir install hl7.fhir.r2.expansions 1.0.2
fhir install hl7.fhir.r3.core 3.0.2 ;  fhir install hl7.fhir.r3.expansions 3.0.2
fhir install hl7.fhir.r4.core 4.0.1 ;  fhir install hl7.fhir.r4.expansions 4.0.1
fhir install hl7.fhir.r4b.core 4.3.0 ; fhir install hl7.fhir.r4b.expansions 4.3.0
fhir install hl7.fhir.r5.core 5.0.0 ;  fhir install hl7.fhir.r5.expansions 5.0.0
```

---

## Lint / format

No separate lint step; there is no linter target, no `Makefile`, and no
formatter script. `.editorconfig` is **editor-enforced only** —
`EnforceCodeStyleInBuild` is not set anywhere, so `dotnet build` will not
fail on a style violation.

`stylecop.json` is present and defines the copyright-header text and using
ordering, but **StyleCop.Analyzers is not referenced by any project**, so
those rules are conventions rather than build-enforced gates. Honor them
anyway; do not add the analyzer package unless asked.

---

## Run

```powershell
dotnet run --project src/fhir-codegen/fhir-codegen.csproj -- generate TypeScript -p hl7.fhir.r4.core --output-path ./R4.ts
```

Top-level commands: `generate`, `compare`, `xver`, `docs`. Global options
include `-p/--package`, `--output-path`, `--fhir-cache`, `--fhir-version`,
`--offline`, `--resolve-dependencies`. Run with `--help`, or
`generate --help`, to see the current set.

No environment variables are required. The package cache defaults to the
user's `.fhir` directory; override with `--fhir-cache`.

---

## Code style

The authoritative source is the repo-root **`.editorconfig`**
(`root = true`), supplemented by `stylecop.json`. Neither is enforced by the
build — see "Lint / format".

- UTF-8, **CRLF** line endings, final newline required, trailing whitespace
  trimmed, **4-space** indent (`tab_width = 4`, no tabs).
- **Prefer explicit types.** `csharp_style_var_for_built_in_types`,
  `csharp_style_var_when_type_is_apparent`, and `csharp_style_var_elsewhere`
  are all `false`. This is the opposite of the wider .NET default — use
  `var` only where the type is unmistakable from the right-hand side.
- **Accessibility modifiers are required** on non-interface members
  (`dotnet_style_require_accessibility_modifiers =
  for_non_interface_members:warning`).
- Prefer **collection expressions** (`[]`) over `new List<T>()` / `new()`
  for empty and simple initializers.
- `using` directives go **outside** the namespace, `System.*` first
  (`stylecop.json` `orderingRules`).
- New `.cs` files carry the copyright header:
  ```csharp
  // <copyright file="Foo.cs" company="Microsoft Corporation">
  //     Copyright (c) Microsoft Corporation. All rights reserved.
  //     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
  // </copyright>
  ```
  Coverage is currently partial (roughly 272 of 414 files). **Add it to new
  files; do not open a review finding or a bulk-edit PR over the files that
  lack it.**
- Comment only what needs clarification. This codebase is deliberately light
  on inline commentary; do not add narration.
- Match the surrounding file. Consistency with neighbouring code beats any
  general preference.

### Architectural invariants

These are decisions, not preferences. Violating one is a review Blocker.

- **Language exporters are auto-discovered.** Every exporter lives under
  `src/Fhir.CodeGen.Lib/Language/{Name}.cs` (or `Language/{Name}/`) and
  implements `ILanguage`. `LanguageManager` finds them by reflection at
  static-init time — **never register one manually**. Adding the type is the
  registration.
- **Exporter options must stay in sync in two places.** Each exporter
  exposes a nested `*Options : ConfigGenerate`. Every option needs both a
  `[ConfigOption(ArgName = "--foo", Description = "…")]` attribute *and* a
  matching `ConfigurationOption` static holding a
  `System.CommandLine.Option<T>`. Changing one without the other silently
  breaks the CLI surface. See `Language/TypeScript.cs` and the `Firely/`,
  `OpenApi/`, `Info/`, `Ruby/`, `SQLite/`, `Shorthand/`, `Cql/` directories
  as canonical examples.
- **Multi-version FHIR types coexist via `extern alias`.** The
  `Hl7.Fhir.*` assemblies for different FHIR versions define colliding type
  names, so `.csproj` files apply MSBuild aliases in an `AddPackageAliases`
  target: `coreR3` / `coreR4` / `coreR4B` / `coreR5` in
  `src/fhir-codegen/fhir-codegen.csproj`, and `stu3` / `r4` / `r4b` in
  `src/Fhir.CodeGen.Lib.Tests/`. In files that touch these, use
  `extern alias` and fully qualify. **Never add a bare top-level
  `using Hl7.Fhir.Model;`** in an aliased context.
- **`DISABLE_XML` is defined in `Fhir.CodeGen.Lib` and
  `Fhir.CodeGen.LangSQLite`, in both Debug and Release.** Do not introduce
  code paths that require XML serialization without guarding them or
  extending the define.
- **`NETSTANDARD2_0` branches are load-bearing.** `Fhir.CodeGen.SQLiteGenerator`
  targets `netstandard2.0`, and `CodeGenCommonPolyfill.cs` is linked in from
  `Fhir.CodeGen.Common`. Preserve those branches when editing.
- **`Fhir.CodeGen.Common` stays dependency-light.** Everything else depends
  on it; adding a heavy dependency there propagates everywhere.
- **Dependency direction is one-way:** `Common` ←
  `CrossVersionLoader` / `MappingLanguage` / `LangSQLite` ← `Lib` ← CLI.
  Do not introduce a cycle or make `Common` depend upward.
- **The package library is quarantined behind a seam.** FHIR package
  acquisition comes from the `fhir-pkg-lib` NuGet package, and
  `src/Fhir.CodeGen.Lib/Packaging/` is the **only** place permitted to name a
  `FhirPkg` type. Everything above it works in the codegen-owned types
  (`PackageIdentity`, `CodeGenPackage`, `CodeGenPackageManifest`,
  `CodeGenPackageIndex`, `CodeGenPackageDirective`).
  `PackageSeamTests.PublicSurfaceExposesNoUpstreamPackageTypes` reflects over
  the exported surface of `Fhir.CodeGen.Lib` and enforces this.
- **Tests do not write generated artifacts to disk.**
  `GenerationTests.WriteGeneratedFiles` is `false`
  (`src/Fhir.CodeGen.Lib.Tests/GenerationTests.cs:29`). Toggle it locally
  when debugging; **never commit it as `true`.**
- For **new** SQLite interaction code the stated preference is the
  `cslightdbgen.sqlitegen` NuGet package over hand-rolled ADO. No project
  references it today — the existing SQLite backend uses
  `Microsoft.Data.Sqlite` — so introducing it adds a dependency and needs
  the user's go-ahead.

---

## Commit conventions

- **Conventional commits**: `<type>(<scope>): <subject>`. Types in active
  use: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`. Subject in the
  imperative, target ≤ 72 characters. **Scope is optional but strongly
  encouraged** and widely used here — e.g. `docs(xver):`, `fix(cli):`,
  `fix(tests):`, `chore(deps):`.
- Required trailer, verbatim, when an agent contributed to the change:
  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```
- One logical change per commit.
- Run the test suite with the `RequiresExternalRepo!=true` filter before
  pushing.
- When the GitHub integration below is on **and** the slot carries an
  `Issue` binding, `dev-do` adds an `Issue: #N` trailer to each phase
  commit, in addition to every trailer required above. When the
  integration is off or the slot is unbound, nothing is added.
- Agents **do not push** and **do not open pull requests** unless the user
  explicitly asks.

---

## GitHub Integration

**Off by default, in two independent ways.** A repository whose
`AGENTS.md` has **no** `## GitHub Integration` section is off. A section
whose `Enabled` row says **`no`** is equally off. In either case no skill
prompts about GitHub, and the `dev-*` loop behaves exactly as it did
before this feature existed.

The block below is **machine-managed**. This section is the **normative
definition** of both sentinel strings: every skill that reads or writes
the block reproduces the opener and the closer byte-for-byte from here,
and no skill re-derives, paraphrases, or reformats them.

<!-- >>> dev-* github integration (managed by dev-* skills) >>> -->
| Setting | Value |
|-|-|
| Enabled | no |
| Repository | n/a |
| Label — feature request | n/a |
| Label — bug report | n/a |
| Label — docs-only (additive) | n/a |
| Changelog file | n/a |
| Changelog entry format | n/a |
| PR opens as draft | n/a |
<!-- <<< dev-* github integration (managed by dev-* skills) <<< -->

**These sentinels are not `dev-setup`'s ignore-file sentinels.** The
ignore-file block that `dev-setup` maintains in `.gitignore` or
`.git/info/exclude` is delimited by
`# >>> dev-* skills (managed by dev-setup) >>>` and
`# <<< dev-* skills (managed by dev-setup) <<<`. That is a **different
block in a different file**, with a `#` comment prefix rather than an
HTML comment. Do not conflate the two, and never substitute one pair for
the other.

Rules for the block:

- Only `dev-setup`, `dev-issue`, and `dev-pr-open` may rewrite it, and
  only **in place** — never a second copy, never appended to the end of
  the file.
- Hand-written text outside the sentinels is never touched. Everything a
  human writes in this section survives every rewrite.
- A recorded value of `no`, `none`, or `n/a` is a **resolved answer**, not
  a missing one. It must never re-trigger a prompt on a later run.
- When `Enabled` is `no`, every other row is `n/a`.

---

## Scratch / slot convention

Local inner-loop work is organized into **slots** under `scratch/`:

```
scratch/<MMDD>-<##>/
  featurerequest.md    # authored by the dev-request skill
  bugreport.md         # authored by the dev-report skill
  approach-a.md        # authored by dev-approach (minimum change)
  approach-b.md        # authored by dev-approach (cleanest architecture)
  approach-c.md        # authored by dev-approach (unconstrained)
  approach.md          # authored by dev-approach (the judge's selection)
  plan.md              # authored by dev-plan, updated by dev-do
  analysis.md          # authored by dev-review
```

- `<MMDD>` is the local date (zero-padded month + day); `<##>` is a
  zero-padded two-digit slot number.
- `scratch/` is **ignored** — `/scratch` in `.gitignore`. Nothing in it is
  ever committed. Do not `git add -f` scratch contents, do not relocate
  scratch artifacts into tracked paths, and do not remove `/scratch` from
  `.gitignore`.
- Because the slot is ignored, **no plan phase may declare a `scratch/` path
  as an owned path.** `plan.md` is a control file that `dev-do` edits
  continuously and never stages or commits.

---

## Agent guardrails

- Read this file before proposing any build, test, or lint command. **Never
  invent a command.** If something you need is not documented here, say so
  rather than guessing.
- Subagents must use the same model configuration as the spawning agent.
- Do not add new linting, building, or testing tooling without being asked.
  In particular, do not add StyleCop.Analyzers or set
  `EnforceCodeStyleInBuild` on your own initiative.
- Prefer the smallest targeted verification that covers the change; escalate
  to the full suite only when the targeted run indicates it is needed.
- **`--filter "RequiresExternalRepo!=true"` is not optional** on a full test
  run. Dropping it makes the suite try to clone external HL7 repositories.
- **Treat generated output as the contract.** When a change moves generated
  artifacts, say so explicitly and show a sample diff of the output, not
  just the C# diff.
- `.github/copilot-instructions.md` covers much of the same ground for the
  GitHub Copilot coding agent. If the two disagree, they are both wrong —
  reconcile them rather than picking one.
- The `docs/specs/` tree documents the cross-version (`xver`) pipeline step
  by step. Consult it before changing anything under
  `Fhir.CodeGen.CrossVersionLoader` or `Fhir.CodeGen.CrossVersionExporter`.
