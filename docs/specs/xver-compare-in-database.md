# XVerProcessor.CompareInDatabase — Step 5 of 7

## Purpose

`CompareInDatabase` is the orchestration entry point for the cross-version comparison phase. It accepts an optional artifact filter, constructs a `FhirDbComparer`, and delegates the actual comparison work to `FhirDbComparer.Compare(processValueSets, processStructures, maxStepSize, specificPairs)`. The only comparison policy owned by this method is translating `artifactFilter` into the `processValueSets` / `processStructures` booleans used by the delegated comparer (`XVerProcessor.cs:668-716`).

## Invocation & Preconditions

Direct callers in `ProcessCommand` are:

- `compare`: optionally reloads source data, maps, substitutions, and FHIR-type ValueSets when `_config.ReloadDatabase` is `true`, then calls `CompareInDatabase()` with no artifact filter (`XVerProcessor.cs:336-345`).
- `compare-vs`: performs the same conditional reload and calls `CompareInDatabase(FhirArtifactClassEnum.ValueSet)` (`XVerProcessor.cs:348-357`).
- `compare-sd`: performs the same conditional reload and calls `CompareInDatabase(FhirArtifactClassEnum.Resource)` (`XVerProcessor.cs:360-369`).
- Default full pipeline: always runs `LoadDatabase(_config.ReloadDatabase)`, `LoadFhirCrossVersionMaps()`, `LoadExtensionSubstitutions()`, and `LoadFhirTypeValueSets()` before `CompareInDatabase()`, then continues to outcome generation and export (`XVerProcessor.cs:421-428`).

Each `compare*` subcommand therefore calls the four load steps first only when `_config.ReloadDatabase == true`; otherwise it assumes an already-loaded database is available. `CompareInDatabase` has a fallback precondition check: if `_db` is `null`, it calls `LoadDatabase(false)` (`XVerProcessor.cs:673-676`), then throws if `_db` is still `null` (`XVerProcessor.cs:678-681`).

## Inputs

Method signature (`XVerProcessor.cs:668-671`):

```csharp
public void CompareInDatabase(
    FhirArtifactClassEnum? artifactFilter = null,
    int? maxStepSize = null,
    HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
```

Parameters:

- `artifactFilter`: selects which comparison domains to run. `CodeSystem` and `ValueSet` route to vocabulary comparisons only; structure-like classes route to structure comparisons only; `null` and otherwise-unhandled values run both domains (`XVerProcessor.cs:687-715`).
- `maxStepSize`: passed unchanged into `FhirDbComparer.Compare`, then into `ValueSetComparer.CompareValueSets` and/or `StructureComparer.CompareStructures` (`XVerProcessor.cs:691-707`, `FhirDbComparer.cs:127-140`). The delegated comparers default it to `_packages.Count - 1` when it is `null` (`ValueSetComparer.cs:107-110`, `StructureComparer.cs:89-93`).
- `specificPairs`: passed unchanged into `FhirDbComparer.Compare`, then into whichever delegated comparer runs (`XVerProcessor.cs:691-713`, `FhirDbComparer.cs:127-140`). When non-null, delegated package-pair generation only accepts explicitly listed source/target FHIR sequence pairs (`ValueSetComparer.cs:120-134`, `StructureComparer.cs:103-110`).

Prior state expected by this phase includes `_db` and the database rows loaded by `LoadDatabase`, plus cross-version maps, extension substitutions, and FHIR-type ValueSets from the three preceding load steps. The default full pipeline guarantees those load steps before comparison (`XVerProcessor.cs:421-426`); direct `compare*` commands guarantee them only when `_config.ReloadDatabase` is enabled (`XVerProcessor.cs:336-369`).

## Outputs

`CompareInDatabase` returns `void` and does not itself materialize rows. Its delegated `FhirDbComparer.Compare` call drops and recreates comparison tables before running requested comparison domains (`FhirDbComparer.cs:117-119`), so this phase is destructive rather than incremental.

When value-set comparison is requested, the affected tables are:

- `DbValueSetComparison`
- `DbValueSetConceptComparison`

When structure comparison is requested, the affected tables are:

- `DbStructureComparison`
- `DbElementComparison`
- `DbElementTypeComparison`

The table set comes from `DbComparisonClasses.DropTables` / `CreateTables`, which conditionally drop or create the value-set tables when `forValueSets` is true and the structure tables when `forStructures` is true (`DbComparisonClasses.cs:32-70`).

## Algorithm

1. If `_db` is `null`, call `LoadDatabase(false)` as a non-reloading attempt to attach the existing database (`XVerProcessor.cs:673-676`).
2. If `_db` is still `null`, throw `Cannot compare without a loaded database!` (`XVerProcessor.cs:678-681`).
3. Ignore the commented-out `FhirMappingComparerVs` implementation; those lines are inactive (`XVerProcessor.cs:683-684`).
4. Construct a `FhirDbComparer` using the loaded database and configured logger factory (`XVerProcessor.cs:686`).
5. Dispatch on `artifactFilter` (`XVerProcessor.cs:687-715`):
   1. `CodeSystem` or `ValueSet` calls `Compare(processValueSets: true, processStructures: false, maxStepSize, specificPairs)` (`XVerProcessor.cs:689-696`).
   2. `PrimitiveType`, `ComplexType`, `Resource`, `Profile`, or `Extension` calls `Compare(processValueSets: false, processStructures: true, maxStepSize, specificPairs)` (`XVerProcessor.cs:698-708`).
   3. `null` or any otherwise-unhandled value calls `Compare(maxStepSize, specificPairs)`, relying on `FhirDbComparer.Compare` defaults of `processValueSets: true` and `processStructures: true` (`XVerProcessor.cs:710-714`, `FhirDbComparer.cs:111-115`).

`FhirDbComparer.Compare` owns table reset and delegation to the value-set and structure comparers; see [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) for its internals. `ValueSetComparer.CompareValueSets` and `StructureComparer.CompareStructures` both build package pairs by increasing `stepSize` so closer package versions are processed first, and both include ascending and descending directions only when allowed by `specificPairs` (`ValueSetComparer.cs:112-142`, `StructureComparer.cs:95-113`). Their per-artifact comparison details are intentionally out of scope here; see [`fhirdb-comparer-do-valueset.md`](./fhirdb-comparer-do-valueset.md) and [`fhirdb-comparer-do-structure.md`](./fhirdb-comparer-do-structure.md).

## Decision Points

- **Rule:** The artifact-filter switch maps `CodeSystem` / `ValueSet` to vocabulary-only comparison, `PrimitiveType` / `ComplexType` / `Resource` / `Profile` / `Extension` to structure-only comparison, and everything else to both comparison domains. **Source:** `XVerProcessor.cs:687-715`. **Rationale:** The switch groups artifact classes by the delegated comparer that can produce rows for that class: vocabulary artifacts use `processValueSets`, structure-like artifacts use `processStructures`, and the default call relies on `FhirDbComparer.Compare` defaults for a full comparison (`FhirDbComparer.cs:111-115`).
- **Rule:** `FhirDbComparer.Compare` drops and recreates the requested comparison tables on every invocation; previous results for the selected domains are lost. **Source:** `FhirDbComparer.cs:117-119`, `DbComparisonClasses.cs:32-70`. **Rationale:** AI Guess: comparison rows are derived state, regenerable from the loaded database and maps; rebuilding from scratch is simpler and avoids stale partial-comparison rows.
- **Rule:** `ValueSetComparer.CompareValueSets` and `StructureComparer.CompareStructures` iterate package pairs in both directions: ascending lower-index package to higher-index package, then descending in reverse. Filtering by `specificPairs` is checked separately per direction. **Source:** `ValueSetComparer.cs:120-142`, `StructureComparer.cs:103-113`, `ComparisonAnnotation.cs:17-20`. **Rationale:** The live value-set comparer labels those blocks `ascending` and `descending`, and the shared `ComparisonDirection` enum names the same conceptual directions as `Up` and `Down` (`ValueSetComparer.cs:120-132`, `ComparisonAnnotation.cs:17-20`).
- **Rule:** `maxStepSize` defaults to `_packages.Count - 1` inside delegated comparers; otherwise it limits how far apart package indices may be while still being compared. **Source:** `ValueSetComparer.cs:107-114`, `StructureComparer.cs:89-97`. **Rationale:** AI Guess: because both comparers process increasing `stepSize` and explicitly comment that closer versions are processed first, a smaller `maxStepSize` is a faster inner-loop development mode while still exercising nearest-neighbor comparisons (`ValueSetComparer.cs:112-114`, `StructureComparer.cs:95-97`).
- **Rule:** When `specificPairs` is non-null, only the listed `(source, target)` sequence-code pairs are compared. **Source:** `ValueSetComparer.cs:121-122`, `ValueSetComparer.cs:133-134`, `StructureComparer.cs:103-104`, `StructureComparer.cs:109-110`. **Rationale:** Each direction is guarded by either `specificPairs is null` or an exact `Contains((sourceSequence, targetSequence))` check before work is queued or executed.
- **Rule:** `_directions` is declared as `[ComparisonDirection.Up, ComparisonDirection.Down]`, but active code in `src/Fhir.CodeGen.Comparison` has no direct usages beyond the declaration. **Source:** `XVerProcessor.cs:108-111`; repository search for `_directions` under `src\Fhir.CodeGen.Comparison` returned only `XVerProcessor.cs:111`. **Rationale:** AI Guess: `_directions` is likely vestigial from an earlier traversal design; the live implementation expresses directionality through ascending/descending package loops in the delegated comparers rather than by iterating this constant.
- **Rule:** `FhirArtifactClassEnum.Extension` routes to structure comparison, not vocabulary comparison. **Source:** `XVerProcessor.cs:698-708`. **Rationale:** The code places `Extension` in the same switch arm as `PrimitiveType`, `ComplexType`, `Resource`, and `Profile`, and that arm sets `processStructures: true` and `processValueSets: false`.

## Rationale Coverage

`Decisions: 7 total — cited: 4 — AI Guess: 3 — unresolved: 0`

## Failure Modes & Edge Cases

- If `_db` is initially `null`, `CompareInDatabase` attempts `LoadDatabase(false)`; if that still leaves `_db` null, it throws `Cannot compare without a loaded database!` (`XVerProcessor.cs:673-681`).
- The `FhirDbComparer.Compare` delegation returns no result; exceptions from table reset, `ValueSetComparer.CompareValueSets`, or `StructureComparer.CompareStructures` propagate to the caller (`FhirDbComparer.cs:117-140`).
- The commented-out `FhirMappingComparerVs` lines suggest a prior implementation or refactoring remnant, but they are inactive and do not affect behavior (`XVerProcessor.cs:683-684`).
- Re-running `compare`, `compare-vs`, `compare-sd`, or the default pipeline wipes the tables for whichever comparison domains are requested before writing fresh comparison rows (`FhirDbComparer.cs:117-119`, `DbComparisonClasses.cs:32-70`).
- Passing `artifactFilter: null` runs both domains through default parameters; passing an unhandled enum value also falls into the default arm and runs both domains (`XVerProcessor.cs:710-714`, `FhirDbComparer.cs:111-115`).

## Coverage Checklist

- [x] `XVerProcessor.CompareInDatabase` (`XVerProcessor.cs:668-716`)
- [x] Artifact-filter switch (`XVerProcessor.cs:687-715`)
- [x] Delegation to `FhirDbComparer.Compare` — linked to [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md); internals not duplicated
- [x] Briefly: `StructureComparer.CompareStructures` and `ValueSetComparer.CompareValueSets` package-pair generation
- [x] `_directions` constant + `ComparisonDirection` enum
- [x] Subcommand routing (`compare`, `compare-vs`, `compare-sd`, default)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:668-716`
  - `src/Fhir.CodeGen.Comparison/CompareTool/FhirDbComparer.cs:111-142`
  - `src/Fhir.CodeGen.Comparison/CompareTool/StructureComparer.cs:85-131`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:100-145`
  - `src/Fhir.CodeGen.Comparison/XVer/ComparisonAnnotation.cs:17` (`ComparisonDirection`)
  - `src/Fhir.CodeGen.Comparison/Models/DbComparisonClasses.cs:32-70` (`DropTables` / `CreateTables`)
- Related specs:
  - [`xver-load-database.md`](./xver-load-database.md)
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md)
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md)
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md)
  - [`xver-generate-outcomes.md`](./xver-generate-outcomes.md) — primary downstream consumer
  - [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) — internals
  - [`fhirdb-comparer-do-structure.md`](./fhirdb-comparer-do-structure.md) — disabled `#if false` internals
  - [`fhirdb-comparer-do-valueset.md`](./fhirdb-comparer-do-valueset.md) — disabled `#if false` internals
- Related `ConfigXVer` options: `ReloadDatabase`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
