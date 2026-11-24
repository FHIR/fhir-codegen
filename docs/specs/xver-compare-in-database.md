# XVerProcessor.CompareInDatabase — Step 5 of 7

## Purpose

`CompareInDatabase` is the orchestration entry point for the cross-version comparison phase. It accepts an optional artifact filter, constructs a `FhirDbComparer`, and delegates the actual comparison work to `FhirDbComparer.Compare(processValueSets, processStructures, maxStepSize, specificPairs)`. The only comparison policy owned by this method is translating `artifactFilter` into the `processValueSets` / `processStructures` booleans used by the delegated comparer (`XVerProcessor.cs:668-716`).

## Invocation & Preconditions

Direct callers in `ProcessCommand` are:

- `compare`: optionally reloads source data, maps, substitutions, and FHIR-type ValueSets when `_config.ReloadDatabase` is `true`, then calls `CompareInDatabase()` with no artifact filter (`XVerProcessor.cs:336-345`).
- `compare-vs`: performs the same conditional reload and calls `CompareInDatabase(FhirArtifactClassEnum.ValueSet)` (`XVerProcessor.cs:348-357`).
- `compare-sd`: performs the same conditional reload and calls `CompareInDatabase(FhirArtifactClassEnum.Resource)` (`XVerProcessor.cs:360-369`).
- Default full pipeline: always runs `LoadDatabase(_config.ReloadDatabase)`, `LoadFhirCrossVersionMaps()`, `LoadExtensionSubstitutions()`, and `LoadFhirTypeValueSets()` before `CompareInDatabase()`, then continues to outcome generation and export (`XVerProcessor.cs:421-428`).
- Reload-enabled `outcomes`, `outcomes-sd`, and `outcomes-vs` also run
  comparison after those four load steps, using respectively both families,
  structures only, and vocabulary only. Reload-enabled `export` runs both
  comparison and outcomes before export. `ProcessCommand` uses the shared
  `ExecuteOutcomeCommand` dispatch seam for this ordering.

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

`CompareInDatabase` returns `void` and does not itself materialize rows. When structures are requested, its delegated `FhirDbComparer.Compare` first calls the read-only `UnsafeCodeInputStore.RequireInputs`, then drops and recreates requested comparison tables. The accepted path is destructive rather than incremental; a readiness failure occurs before either requested family's reset.

When value-set comparison is requested, the affected tables are:

- `DbValueSetComparison`
- `DbValueSetConceptComparison`

When structure comparison is requested, the affected tables are:

- `DbStructureComparison`
- `DbElementComparison`
- `DbElementTypeComparison`

The table set comes from `DbComparisonClasses.DropTables` / `CreateTables`, which conditionally drop or create the value-set tables when `forValueSets` is true and the structure tables when `forStructures` is true (`DbComparisonClasses.cs:32-70`).

## Safety Comparison Revisions and Provenance

Safety code classification remains a separate outcome pass; annotations and
configured rules do not change comparison decisions. Structure/type comparison
now uses the catalog's operational boundary and effective constrained-use facts
below rather than profile-blind Quantity equivalence.

`RequireInputs` validates completed inventory **format 1 / policy 2**, completed
unsafe inputs **format 2**, and the input snapshot's exact inventory-revision
binding. Missing/old/incomplete snapshots (including preparatory native-only
policy 1) fail before destructive reset; readiness creates no missing schema.
Opening the named database, maps-only loading or comparison cannot repair an
incomplete catalog. Vocabulary-only comparison remains independent.

`StructureComparer.CompareStructures` calls
`UnsafeCodeInputStore.BeginComparison` before each directed pair, clearing
its old comparison/outcome certification. Only after the pair's final
structure/element/type cache flush does `CompleteComparison` assign a new
comparison revision against the **completed input revision** captured at
the start. Missing/incomplete/incompatible inputs, changed dependencies,
and failed flushes cannot certify the pair. Structure comparison cannot run
without compatible completed inputs, including for direct `StructureComparer`
entry; an explicitly loaded source-free snapshot is valid, absent metadata is not.
Vocabulary-only comparison neither requires nor certifies structure safety.

`DbUnsafeCodePairState.InputRevision` and `ComparisonRevision` bind later
outcomes to the exact map snapshot used by comparison. This is necessary
because `DbElementOutcomeTarget.ElementComparisonKey` points to
`DbElementComparison`, whose `ElementMappingKey` must identify the actual
authored `DbElementMapping.Key`. `ElementComparer` now persists that
element-map key, not a numerically coincident structure-map key; inferred
or transitive rows with no contributing explicit element map leave it
absent. Regenerating outcomes after a map reload **without rerunning
comparison** is rejected rather than trusting obsolete mapping provenance.

The reset is family-wide even with `specificPairs` or `maxStepSize`:
all structure comparison/outcome stamps are invalidated before destructive
deletion; only actually completed pairs receive new revisions. An excluded
pair is not current just because it has no rows. Outcome resets retain
comparison revisions; vocabulary-only resets retain structure revisions.
See [reset bookkeeping](./fhirdb-comparer-compare.md#safety-state-at-reset-and-pair-boundaries).

Recovery from incompatible catalog state is explicit destructive content reload →
maps → compare → outcomes → export. If only maps changed on a compatible catalog,
rerun maps → compare → outcomes → export. There is no inferred migration or
consumer/export-time repair.

## Processing and Constrained Uses

`StructureComparer` selects `XVerStructureCatalog.GetProcessedStructures` on both
sides. Existing native primitives/complex types/resources retain their behavior
and exclusions. Profiles require ordinal declared `Type == "Quantity"` and unique,
acyclic ancestry to the actual same-package native Quantity. Profiles on Age,
Coding and resources stay passive; missing comparisons do not promote them.
Target selection and each transitive intermediate edge must pass the same
both-endpoint gate, including old persisted comparison rows.

Passive definitions can still constrain legitimate type/profile/targetProfile
occurrences inside processed owners. `ElementTypeComparer.EffectiveTypes` prefers
local declarations, consulting exact base correspondence only when absent.
`ProfileUseFacts.AtUse` combines inherited and local restrictions per type/profile
alternative and slice context, including cardinality, fixed/pattern values,
bindings, bounds, slicing, opaque invariants and known primitive bridge obligations.
Local restrictions cannot loosen the base; alternatives form a permitted union,
not a conjunction. One prohibited branch does not prohibit the whole occurrence.
Canonical versions are definition versions, and native-name lookup excludes profiles.

Constrained and Quantity-family uses require complete typed proofs. A supported
native reuse preserves the mapping authority's relationship; a structured
`Unrepresentable` comparison is not mapped/Equivalent and retains exact facts
and reasons in `TechnicalMessage`. Unknown/malformed/unclassified required semantics
are reported as warnings and skipped rather than becoming an accepted negative or default Equivalent. Missing
profiles never fall back to unprofiled Quantity. The actual-core proof census
(3,604 uses: 284 native reuses, zero projections, 3,320 negatives, zero unresolved)
is classification evidence, not proof that all artifacts can be scheduled/exported.

## Owning-Root Correspondence

`ElementComparer.DoElementComparisons` applies explicit element mappings
first. Its fallback resolves one root within each exact package/owning
structure, then substitutes the target root's path for the source root's
Id prefix. Structure names need not equal root element IDs: R3 SimpleQuantity
has root Id `Quantity:simplequantity` and Path `Quantity`.
Required roots must be unique and well formed, and source IDs must have
the exact root, member or slice boundary; invalid facts raise a contextual
error. An ordinary absent target member remains a no-map comparison.

## Algorithm

1. If `_db` is `null`, call `LoadDatabase(false)` as a non-reloading attempt to attach the existing database (`XVerProcessor.cs:673-676`).
2. If `_db` is still `null`, throw `Cannot compare without a loaded database!` (`XVerProcessor.cs:678-681`).
3. Construct a `FhirDbComparer` using the loaded database and configured logger factory (`XVerProcessor.cs:686`).
4. Dispatch on `artifactFilter` (`XVerProcessor.cs:687-715`):
   1. `CodeSystem` or `ValueSet` calls `Compare(processValueSets: true, processStructures: false, maxStepSize, specificPairs)` (`XVerProcessor.cs:689-696`).
   2. `PrimitiveType`, `ComplexType`, `Resource`, `Profile`, or `Extension` calls `Compare(processValueSets: false, processStructures: true, maxStepSize, specificPairs)` (`XVerProcessor.cs:698-708`).
   3. `null` or any otherwise-unhandled value calls `Compare(maxStepSize, specificPairs)`, relying on `FhirDbComparer.Compare` defaults of `processValueSets: true` and `processStructures: true` (`XVerProcessor.cs:710-714`, `FhirDbComparer.cs:111-115`).

`FhirDbComparer.Compare` owns table reset and delegation to the value-set and structure comparers; see [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) for its internals. `ValueSetComparer.CompareValueSets` and `StructureComparer.CompareStructures` both build package pairs by increasing `stepSize` so closer package versions are processed first, and both include ascending and descending directions only when allowed by `specificPairs` (`ValueSetComparer.cs:112-142`, `StructureComparer.cs:95-113`).

## Decision Points

- **Rule:** The artifact-filter switch maps `CodeSystem` / `ValueSet` to vocabulary-only comparison, `PrimitiveType` / `ComplexType` / `Resource` / `Profile` / `Extension` to structure-only comparison, and everything else to both comparison domains. **Source:** `XVerProcessor.cs:687-715`. **Rationale:** The switch groups artifact classes by the delegated comparer that can produce rows for that class: vocabulary artifacts use `processValueSets`, structure-like artifacts use `processStructures`, and the default call relies on `FhirDbComparer.Compare` defaults for a full comparison (`FhirDbComparer.cs:111-115`).
- **Rule:** `FhirDbComparer.Compare` drops and recreates the requested comparison tables on every invocation; previous results for the selected domains are lost. **Source:** `FhirDbComparer.cs:117-119`, `DbComparisonClasses.cs:32-70`. **Rationale:** AI Guess: comparison rows are derived state, regenerable from the loaded database and maps; rebuilding from scratch is simpler and avoids stale partial-comparison rows.
- **Rule:** `ValueSetComparer.CompareValueSets` and `StructureComparer.CompareStructures` iterate package pairs in both directions: ascending lower-index package to higher-index package, then descending in reverse. Filtering by `specificPairs` is checked separately per direction. **Source:** `ValueSetComparer.cs:120-142`, `StructureComparer.cs:103-113`, `ComparisonAnnotation.cs:17-20`. **Rationale:** The live value-set comparer labels those blocks `ascending` and `descending`, and the shared `ComparisonDirection` enum names the same conceptual directions as `Up` and `Down` (`ValueSetComparer.cs:120-132`, `ComparisonAnnotation.cs:17-20`).
- **Rule:** `maxStepSize` defaults to `_packages.Count - 1` inside delegated comparers; otherwise it limits how far apart package indices may be while still being compared. **Source:** `ValueSetComparer.cs:107-114`, `StructureComparer.cs:89-97`. **Rationale:** AI Guess: because both comparers process increasing `stepSize` and explicitly comment that closer versions are processed first, a smaller `maxStepSize` is a faster inner-loop development mode while still exercising nearest-neighbor comparisons (`ValueSetComparer.cs:112-114`, `StructureComparer.cs:95-97`).
- **Rule:** When `specificPairs` is non-null, only the listed `(source, target)` sequence-code pairs are compared. **Source:** `ValueSetComparer.cs:121-122`, `ValueSetComparer.cs:133-134`, `StructureComparer.cs:103-104`, `StructureComparer.cs:109-110`. **Rationale:** Each direction is guarded by either `specificPairs is null` or an exact `Contains((sourceSequence, targetSequence))` check before work is queued or executed.
- **Rule:** `FhirArtifactClassEnum.Extension` routes to structure comparison, not vocabulary comparison. **Source:** `XVerProcessor.cs:698-708`. **Rationale:** The code places `Extension` in the same switch arm as `PrimitiveType`, `ComplexType`, `Resource`, and `Profile`, and that arm sets `processStructures: true` and `processValueSets: false`.

## Rationale Coverage

`Decisions: 6 total — cited: 4 — AI Guess: 2 — unresolved: 0`

## Failure Modes & Edge Cases

- If `_db` is initially `null`, `CompareInDatabase` attempts `LoadDatabase(false)`; if that still leaves `_db` null, it throws `Cannot compare without a loaded database!` (`XVerProcessor.cs:673-681`).
- The `FhirDbComparer.Compare` delegation returns no result; exceptions from table reset, `ValueSetComparer.CompareValueSets`, or `StructureComparer.CompareStructures` propagate to the caller (`FhirDbComparer.cs:117-140`).
- After readiness succeeds, re-running comparison wipes the tables for whichever domains are requested before writing fresh rows. A structure readiness failure leaves previous rows/stamps intact; pair-time failures after reset do not complete certification (`FhirDbComparer.Compare`, `DbComparisonClasses.DropTables`).
- Passing `artifactFilter: null` runs both domains through default parameters; passing an unhandled enum value also falls into the default arm and runs both domains (`XVerProcessor.cs:710-714`, `FhirDbComparer.cs:111-115`).

## Coverage Checklist

- [x] `XVerProcessor.CompareInDatabase` (`XVerProcessor.cs:668-716`)
- [x] Artifact-filter switch (`XVerProcessor.cs:687-715`)
- [x] Delegation to `FhirDbComparer.Compare` — linked to [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md); internals not duplicated
- [x] Briefly: `StructureComparer.CompareStructures` and `ValueSetComparer.CompareValueSets` package-pair generation
- [x] `ComparisonDirection` enum (`Up`/`Down`)
- [x] Subcommand routing (`compare`, `compare-vs`, `compare-sd`, default)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:668-716`
  - `src/Fhir.CodeGen.Comparison/CompareTool/FhirDbComparer.cs:111-142`
  - `src/Fhir.CodeGen.Comparison/CompareTool/StructureComparer.cs:85-131`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:100-145`
  - `src/Fhir.CodeGen.Comparison/XVer/ComparisonAnnotation.cs:17` (`ComparisonDirection`)
  - `src/Fhir.CodeGen.Comparison/Models/DbComparisonClasses.cs:32-70` (`DropTables` / `CreateTables`)
  - `src/Fhir.CodeGen.Comparison/Models/XVerStructureCatalog.cs` / `ProfileUseFacts.cs` — processing boundary, exact roots and effective-use proofs
  - `src/Fhir.CodeGen.Comparison/CompareTool/ElementTypeComparer.cs` — local type precedence and constrained comparisons
  - `src/Fhir.CodeGen.Comparison/Models/DbUnsafeCodeClasses.cs` — pre-reset input readiness and pair revisions
- Related specs:
  - [`xver-load-database.md`](./xver-load-database.md)
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md)
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md)
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md)
  - [`xver-generate-outcomes.md`](./xver-generate-outcomes.md) — primary downstream consumer
  - [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) — internals
- Related `ConfigXVer` options: `ReloadDatabase`.

---
*Legacy comparison references verified at `d02100974b2dc1b05ecf1af69c29095e6973f4c8`; safety revisions and command routing rechecked at `88af2c4e4fb0f6f770297f0e1209efc212317af6` on `2026-11-05`.*

*Active catalog/constrained-comparison contract checked by method against `64d48bb993c6eef207e9b8ca03d593a277068746` on `2026-11-17`; unchanged historical line references retained.*
