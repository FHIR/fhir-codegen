# XVerProcessor.ExportOutcomes — Step 7 of 7

## Purpose

`ExportOutcomes` is the orchestration entry point for the final step of
the cross-version pipeline: turning the rows produced by
`GenerateOutcomes` (`DbValueSetOutcome`, `DbValueSetConceptOutcome`,
`DbStructureOutcome`, `DbElementOutcome`,
`DbElementOutcomeTarget`) into deployable on-disk FHIR Implementation
Guide packages. It instantiates `XVerExporter` with the open SQLite
connection plus the `ConfigXVer`, maps its `artifactFilter` argument to
the `processVocabulary` / `processStructures` boolean pair that
`XVerExporter.Export` understands, and dispatches.

The orchestration is intentionally thin. All real export logic lives in
[`XVerExporter`](./xver-exporter-export.md) and its five component
exporters (`IgExporter`, `VocabularyFhirExporter`,
`VocabularyPageExporter`, `StructureFhirExporter`,
`StructurePageExporter`). This spec covers what `ExportOutcomes`
decides; the linked exporter deep-dive covers what those decisions
produce on disk.

## Invocation & Preconditions

### Direct callers (from `ProcessCommand`)

- `export` — calls `ExportOutcomes()` with all defaults (`XVerProcessor.cs:418`).
- Default full-pipeline branch — calls `ExportOutcomes()` with all
  defaults (`XVerProcessor.cs:428`).
- `wip` — the developer scratch path currently contains a single
  uncommented `ExportOutcomes(includeIgScripts: true,
  specificPairs: specificPairs)` call (`XVerProcessor.cs:303`);
  treat this as not part of the documented pipeline.

The `export` subcommand runs the four `Load*` methods first only when
`_config.ReloadDatabase == true` (`XVerProcessor.cs:410-416`). The
default full-pipeline branch always runs `LoadDatabase` →
`LoadFhirCrossVersionMaps` → `LoadExtensionSubstitutions` →
`LoadFhirTypeValueSets` → `CompareInDatabase` → `GenerateOutcomes`
before `ExportOutcomes`.

### Preconditions

- A loaded DB. `ExportOutcomes` calls `LoadDatabase(false)` implicitly
  when `_db is null` (`XVerProcessor.cs:563-566`).
- The outcome tables (`DbStructureOutcome`, `DbValueSetOutcome`, etc.)
  populated by a prior `GenerateOutcomes` run. `ExportOutcomes`
  does **not** validate this; on an empty outcomes DB the exporters
  silently produce empty IG packages.
- The IG-support directory at
  `<CrossVersionMapSourcePath>/input/ig-support/` is consulted by
  `IgExporter` for `igParameters.json` and `xver-package-config.json`;
  missing files are silently ignored (defaults are used).

## Inputs

### Method signature

```csharp
public void ExportOutcomes(
    FhirArtifactClassEnum? artifactFilter = null,
    int? maxStepSize = null,
    HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null,
    bool? includeIgScripts = null)
```

(`XVerProcessor.cs:557-561`)

### Parameter behavior

| Parameter | Behavior |
|---|---|
| `artifactFilter` | Drives the `processVocabulary` / `processStructures` switch. `CodeSystem` / `ValueSet` → vocabulary only; `PrimitiveType` / `ComplexType` / `Resource` / `Profile` / `Extension` → structures only; default → both. (`XVerProcessor.cs:576-609`) |
| `maxStepSize` | Forwarded verbatim to `XVerExporter.Export`. Defaults to `null`, which the exporter then resolves to `_packages.Count - 1` (i.e. emit every package pair). |
| `specificPairs` | Forwarded verbatim. When non-null, only the listed `(source, target)` sequence pairs become cross-version IGs. |
| `includeIgScripts` | When the caller passes `null` (the common case), the value is taken from `_config.XverIncludeScripts` (`XVerProcessor.cs:581, 594, 603`). When non-null, the explicit value overrides the config. |

### Prior in-memory / database state

- `_db` (loaded by `LoadDatabase`, lazily refreshed on entry if null).
- `_config` (`ConfigXVer`) — supplies `OutputDirectory`,
  `CrossVersionMapSourcePath`, `XverArtifactVersion`,
  `XverIncludeScripts`, and the per-FHIR-version
  `ExportR2` / `ExportR3` / `ExportR4` / `ExportR4B` / `ExportR5` /
  `ExportR6` flags.

### Constants referenced

- `XVerProcessor._canonicalRootCrossVersion = "http://hl7.org/fhir/uv/xver/"`
  (`XVerProcessor.cs:104`). The exporters compose IG and IG-element
  canonical URLs against this root.
- `XVerProcessor._exclusionSet` (`XVerProcessor.cs:113-128`) — read by
  the content exporters to skip outcomes for excluded URLs even though
  those outcomes are present in the DB.

## Outputs

`ExportOutcomes` itself writes nothing. All output is performed by
`XVerExporter.Export` and its component exporters. The shape of that
output is documented in
[`xver-exporter-export.md`](./xver-exporter-export.md). At a
glance, each invocation produces, under `_config.OutputDirectory/fhir/`:

- One cross-version IG per `(source, target)` package pair allowed by
  `_allowedExportVersions` × `specificPairs` × `maxStepSize` —
  containing CodeSystem / ValueSet / ConceptMap JSON (vocabulary),
  Extension and Profile StructureDefinitions + structure ConceptMaps
  (structures), and markdown page content (`index-vs.md`,
  `lookup-vs-*.md`, `index-resources.md`, `index-types.md`,
  `lookup-{resource|type}-*.md`).
- One validation IG per allowed package, containing
  `ig.ini` + `ig.json` + `menu.xml` + a validation-example bundle.
- One log line `"Finished exporting; outputDirectory: \`{OutputDirectory}\`"`
  at the end (`XVerProcessor.cs:611`).

## Algorithm

Numbered against `XVerProcessor.cs:557-612`:

1. **Lazy DB load.** If `_db is null`, call `LoadDatabase(false)`
   (cs:563-566).
2. **Hard-stop guard.** If `_db is still null` after the lazy load
   attempt, throw `"Cannot export outcomes without a loaded database!"`
   (cs:568-571).
3. **Construct the exporter.** `XVerExporter exporter = new(_db.DbConnection, _config)`
   (cs:573-575). This is where `_crossDefinitionVersion`,
   `_versionSpecificExtBehavior`, and `_versionSpecificExport` are
   resolved from the config (see Decision Points below). Note that
   `_db.DbConnection` (the raw `IDbConnection`) is passed in, not the
   `ComparisonDatabase` wrapper — the exporter does not need the
   higher-level helpers.
4. **Dispatch on artifact filter** (cs:576-609):
   - `CodeSystem` / `ValueSet` →
     `exporter.Export(includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
     processVocabulary: true, processStructures: false, maxStepSize,
     specificPairs)`.
   - `PrimitiveType` / `ComplexType` / `Resource` / `Profile` /
     `Extension` →
     `exporter.Export(includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
     processVocabulary: false, processStructures: true, maxStepSize,
     specificPairs)`.
   - Default (`null` or anything else) →
     `exporter.Export(includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
     processVocabulary: true, processStructures: true, maxStepSize,
     specificPairs)`.
5. **Log completion** (cs:611).

The four content exporters (`VocabularyFhirExporter`,
`VocabularyPageExporter`, `StructureFhirExporter`,
`StructurePageExporter`) and the IG builder (`IgExporter`) are invoked
inside `XVerExporter.Export`. See
[`xver-exporter-export.md`](./xver-exporter-export.md) for the
internals; this spec does not repeat them.

## Decision Points

- **Rule:** The artifact-filter → `(processVocabulary, processStructures)`
  mapping is identical in shape to the mapping used by
  `CompareInDatabase` and `GenerateOutcomes`, so a single
  `--artifact-filter` value drives the whole pipeline coherently end
  to end.
  **Source:** `XVerProcessor.cs:576-609` (this method),
  `XVerProcessor.cs:687-715` (`CompareInDatabase`),
  `XVerProcessor.cs:629-660` (`GenerateOutcomes`).
  **Rationale:** cited from code shape — keeping the three switches
  identical is what makes a vocabulary-only run viable without
  re-running the structure pipeline.

- **Rule:** `includeIgScripts` defaults to `_config.XverIncludeScripts`
  when the parameter is `null`; an explicit non-null argument wins.
  **Source:** `XVerProcessor.cs:581, 594, 603`.
  **Rationale:** cited — gives the developer a per-invocation override
  while letting `appsettings`-style configuration set the team default.

- **Rule:** `_canonicalRootCrossVersion = "http://hl7.org/fhir/uv/xver/"`
  is the canonical-URL prefix for every cross-version artifact
  emitted by the pipeline (IGs, Extension StructureDefinitions, etc.).
  **Source:** `XVerProcessor.cs:104` (the constant); composed into
  package URLs at `IgExporter.cs:41-42`
  (`PackageUrl => $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{PackageId}"`).
  **Rationale:** cited — matches the HL7 UV (Universal) IG namespace
  convention for cross-version content under
  `http://hl7.org/fhir/uv/xver/`.

- **Rule:** `_crossDefinitionVersion` resolves with three layers of
  precedence: (a) explicit `config.XverArtifactVersion`, (b) the
  `PackageVersion` from `<CrossVersionMapSourcePath>/input/ig-support/xver-package-config.json`
  when (a) is empty, (c) `"0.1.0"` as a final default.
  **Source:** `XVerExporter.cs:60` (constructor; `"0.1.0"` default and
  `XverArtifactVersion` read); `IgExporter.cs:497-504` (override from
  on-disk config when `XverArtifactVersion` is empty).
  **Rationale:** cited — keeps the version SSOT for a build under the
  cross-version source repo, while still allowing a CLI override.

- **Rule:** Per-pair packaging is the default. Each `(source, target)`
  pair in the allowed cross-product produces its own IG directory and
  `package.json`. Comprehensive / "all-in-one" packages are **not**
  produced by this orchestrator.
  **Source:** `IgExporter.cs:610-684` (`CreateInitialXVerIgs` — one
  `XVerIgExportTrackingRecord` per pair, plus one
  `ValidationIgExportTrackingRecord` per allowed package).
  **Rationale:** cited from code shape — each cross-version IG is
  independently publishable.

- **Rule:** Only packages whose `DefinitionFhirSequence` is in
  `_allowedExportVersions` participate in either cross-version IGs or
  validation IGs. The set is built from `config.ExportR2` …
  `config.ExportR6` flags at `IgExporter` construction time.
  **Source:** `IgExporter.cs:460-488, 631-675`.
  **Rationale:** `AI Guess:` the per-version export gates exist because
  emitting an IG for a FHIR release for which the on-disk
  ig-support templates are missing (or known-broken) produces an
  invalid package; the flags let the maintainer disable individual
  releases without dropping them from the comparison DB.

- **Rule:** `XVerExporter._versionSpecificExtBehavior = ShortVersion` and
  `_versionSpecificExport = TargetVersion` are hard-coded in the
  exporter constructor and not surfaced as configuration.
  **Source:** `XVerExporter.cs:45-46`.
  **Rationale:** `AI Guess:` these encode the current "best known"
  cross-version emission style — the enums exist (with `None` /
  `TargetVersion` / `ShortVersion` / `CurrentVersion` cases) to permit
  future experimentation, but the live pipeline pins them so the
  emitted artifacts are stable.

- **Rule:** `ExportOutcomes` passes `_db.DbConnection` (raw
  `IDbConnection`) to `XVerExporter`, not the `ComparisonDatabase`
  wrapper. The exporter therefore cannot reach `ComparisonDatabase`
  helpers like `TryLoadExtensionSubstitutions`; it operates strictly
  against the SQLite tables produced by the prior pipeline steps.
  **Source:** `XVerProcessor.cs:573-575`; `XVerExporter` constructor at
  `XVerExporter.cs:48-61`.
  **Rationale:** cited from code shape — by the time the export step
  runs, all required content is in the DB, so the wrapper's
  schema-loading helpers are not needed.

- **Rule:** Excluded URLs are re-applied at export time. The content
  exporters (`VocabularyFhirExporter`, `VocabularyPageExporter`) skip
  outcome rows whose `SourceCanonicalVersioned` /
  `SourceCanonicalUnversioned` is in `XVerProcessor._exclusionSet`,
  even though those outcomes never made it into the DB at load time
  (`LoadDatabase` already excluded them).
  **Source:** `VocabularyFhirExporter.cs:94-95, 117-118`;
  `VocabularyPageExporter.cs:89-93, 240-244`.
  **Rationale:** `AI Guess:` defense-in-depth — even if a future
  load-step change starts admitting the excluded URLs, the export step
  still skips them, keeping the published IGs free of the rotating
  vocabularies.

- **Rule:** The `wip` subcommand's body is a developer scratchpad —
  almost all of it is commented out, and the only live call (today)
  is `ExportOutcomes(includeIgScripts: true, specificPairs: specificPairs)`
  with `specificPairs = [(R5, R4)]`. This is not part of the
  documented production pipeline; the narrative subcommand →
  step-set matrix footnotes it as `n/a (scratch)`.
  **Source:** `XVerProcessor.cs:260-308`.
  **Rationale:** cited from code shape — `wip` is the canonical place
  to wire up ad-hoc test runs without polluting the other subcommands.

## Rationale Coverage

`Decisions: 10 total — cited: 7 — AI Guess: 3 — unresolved: 0`

## Failure Modes & Edge Cases

- **Hard throw on no DB.** If `LoadDatabase(false)` does not populate
  `_db`, the method throws
  `"Cannot export outcomes without a loaded database!"`
  (`XVerProcessor.cs:568-571`).
- **Silent empty-outcomes export.** If `GenerateOutcomes` was not run
  (or produced no rows), the exporters iterate zero outcomes per pair
  and emit empty IGs without warning. Each IG still gets its
  `ig.ini` / `ig.json` / `menu.xml` skeleton via
  `IgExporter.FinalizeXVerIgs`; downstream IG publishers may flag this.
- **Missing IG-support config files.** If
  `<CrossVersionMapSourcePath>/input/ig-support/igParameters.json` or
  `xver-package-config.json` is missing, `IgExporter` silently uses
  its hard-coded defaults (including the
  `hl7.terminology@7.1.0` / `hl7.fhir.uv.extensions@5.3.0` /
  `hl7.fhir.uv.tools@1.1.2` dependency trio at
  `IgExporter.cs:515-543`). Malformed files are caught, logged, and
  ignored — they do not abort the export.
- **Filter mismatch with prior pipeline run.** If `GenerateOutcomes`
  ran with `artifactFilter: ValueSet` and `ExportOutcomes` is then
  invoked with `artifactFilter: Resource`, the export step will find
  no `DbStructureOutcome` rows for the pair and silently emit empty
  structure content. No diagnostic warns about the mismatch.
- **Re-running `export` is idempotent within a pair.** `IgExporter`
  builds tracking records fresh each run; existing files on disk are
  overwritten without consulting timestamps. There is no incremental
  mode.
- **`wip` is a scratchpad.** Because all of its body except a single
  `ExportOutcomes(...)` call is commented out, running `wip` against
  an unprepared DB will throw (see Hard-throw above). This is
  intentional — `wip` exists for inner-loop development and is
  expected to be re-edited frequently.
- **The `(R5, R4)` pin in `wip`.** The live `specificPairs` value in
  `wip` is `[(R5, R4)]`. Treat any change to that pin as
  developer-facing only; the production pipelines all leave
  `specificPairs` null.

## Coverage Checklist

- [x] `XVerProcessor.ExportOutcomes` orchestration (cs:557-612)
- [x] Implicit `LoadDatabase(false)` on `_db is null` (cs:563-566)
- [x] Hard-throw guard when `_db` is still null (cs:568-571)
- [x] `XVerExporter` construction with `_db.DbConnection` + `_config`
      (cs:573-575)
- [x] Artifact-filter → `(processVocabulary, processStructures)`
      mapping (cs:576-609)
- [x] `includeIgScripts` resolution (parameter vs.
      `_config.XverIncludeScripts`)
- [x] `_canonicalRootCrossVersion` and `_crossDefinitionVersion`
      resolution
- [x] `_allowedExportVersions` filtering via `ExportR2..R6`
- [x] Hard-coded `_versionSpecificExtBehavior` / `_versionSpecificExport`
- [x] `_exclusionSet` re-applied at export time
- [x] `wip`, `export`, and default-branch subcommand routing
- [x] Pointer to [`xver-exporter-export.md`](./xver-exporter-export.md)
      for `XVerExporter.Export` internals (not duplicated)

## References

### Source

- `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:104` (`_canonicalRootCrossVersion`)
- `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:113-128`
  (`_exclusionSet`)
- `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:256-431`
  (`ProcessCommand` — subcommand routing including `wip`, `export`,
  default)
- `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:557-612`
  (`ExportOutcomes`)
- `src/Fhir.CodeGen.Comparison/Exporter/XVerExporter.cs:32-118`
  (constructor + `Export`)
- `src/Fhir.CodeGen.Comparison/Exporter/IgExporter.cs:437-684`
  (`IgExporter` constructor, IG-support loading, `CreateInitialXVerIgs`)

### Related specs

- [`xver-exporter-export.md`](./xver-exporter-export.md) —
  deep-dive of `XVerExporter.Export` and the five component
  exporters. **Authoritative for export internals.**
- [`xver-generate-outcomes.md`](./xver-generate-outcomes.md) — produces
  the outcome rows consumed here.
- [`xver-compare-in-database.md`](./xver-compare-in-database.md) —
  upstream comparison phase.
- [`xver-load-database.md`](./xver-load-database.md) — initial DB load.

### Related `ConfigXVer` options

- `OutputDirectory` — root for all emitted IGs.
- `CrossVersionMapSourcePath` — root for IG-support config files.
- `XverArtifactVersion` — primary source for `_crossDefinitionVersion`.
- `XverIncludeScripts` — default value when `includeIgScripts == null`.
- `ExportR2`, `ExportR3`, `ExportR4`, `ExportR4B`, `ExportR5`,
  `ExportR6` — per-FHIR-version export gates.
- `ReloadDatabase` — when true, the `export` subcommand re-runs the
  four `Load*` steps before exporting.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
