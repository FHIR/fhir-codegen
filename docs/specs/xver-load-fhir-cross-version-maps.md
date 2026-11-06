# XVerProcessor.LoadFhirCrossVersionMaps — Step 2 of 7

## Purpose

`LoadFhirCrossVersionMaps` is the entry point that ingests externally-authored cross-version mapping content into the comparison database. It loads ConceptMap JSON from the configured cross-version source path and FML StructureMap text files from pair-specific input folders, and it can also seed built-in primitive type maps when `UseInternalTypeMaps` is true. The rows it creates become substitution authorities for later database comparison and outcome generation (`CompareInDatabase` and `GenerateOutcomes`).

## Invocation & Preconditions

- Direct callers from `ProcessCommand`:
  - `load` calls `LoadDatabase(true)`, then `LoadFhirCrossVersionMaps`, then the sibling load steps (`XVerProcessor.cs:314-319`).
  - `load-maps` calls `LoadFhirCrossVersionMaps`, then `LoadFhirTypeValueSets` (`XVerProcessor.cs:326-330`).
  - The default full pipeline calls `LoadDatabase(_config.ReloadDatabase)`, `LoadFhirCrossVersionMaps`, the sibling load steps, `CompareInDatabase`, `GenerateOutcomes`, and `ExportOutcomes` (`XVerProcessor.ProcessCommand`).
- Conditional callers from `ProcessCommand`: `compare`, `compare-vs`, `compare-sd`, `outcomes`, `outcomes-vs`, `outcomes-sd`, and `export` call this step only inside `_config.ReloadDatabase` branches (`XVerProcessor.cs:336-418`).
- Preconditions:
  - A comparison database must be available. If `_db` is null, this method calls `LoadDatabase(false)` and throws if the database is still null (`XVerProcessor.cs:792-799`).
  - `_config.CrossVersionMapSourcePath` must be non-empty for actual map loading. If absent, `UnsafeCodeInputStore.ReplaceWithEmptyInputs` explicitly publishes a completed, source-free safety snapshot (`SourceMode = "none"`); it clears safety facts/rules without resetting unrelated legacy maps. Missing metadata is never interpreted as an empty policy.
  - The loaded database is expected to already contain the FHIR packages and definitions that map files reference; loaders select packages, structures, elements, value sets, and concepts from `_db.DbConnection` while processing (`ConceptMapLoader.cs:435-454`, `FmlLoader.cs:238-319`).

## Inputs

- **Method signature:**
  ```csharp
  public void LoadFhirCrossVersionMaps()
  ```
  (`XVerProcessor.cs:790`)
- **Configuration (`ConfigXVer` keys):**
  - `CrossVersionMapSourcePath`: source root passed to `MappingLoader.TryLoadCrossVersionSourceMaps` (`XVerProcessor.cs:802-805`); exposed as `--map-source-path` / `Map_Source_Path` (`ConfigXVer.cs:39-57`).
  - `UseInternalTypeMaps`: controls whether built-in type maps are loaded before source type ConceptMaps (`XVerProcessor.cs:804-806`); exposed as `--use-internal-type-maps` / `Use_Internal_Type_Maps` (`ConfigXVer.cs:437-448`).
  - `LogFactory`: captured by `XVerProcessor` and passed to `MappingLoader`, then to child loaders (`XVerProcessor.cs:161-165`, `XVerProcessor.cs:801`).
- **On-disk sources under `<CrossVersionMapSourcePath>`:**
  - `<root>\input\codes\ConceptMap-*-*.json`: ConceptMap JSON for value-set and concept maps (`MappingLoader.cs:97`, `ConceptMapLoader.cs:359-393`, `ConceptMapLoader.cs:478-487`). Code-map source and target versions are parsed from the `ConceptMap-...` filename components (`MappingLoader.cs:194-217`, `ConceptMapLoader.cs:399-402`).
  - `<root>\input\types\ConceptMap-types-*.json`: ConceptMap JSON for data type structure maps (`MappingLoader.cs:113-120`, `ConceptMapLoader.cs:359-393`, `ConceptMapLoader.cs:466-475`). `ConceptMap-types-fallback.json` is a special all-package-pairs fallback file (`ConceptMapLoader.cs:403-419`, `ConceptMapLoader.cs:1369-1555`).
  - `<root>\input\resources\ConceptMap-resources-*.json`: ConceptMap JSON for resource structure maps (`MappingLoader.cs:126`, `ConceptMapLoader.cs:490-499`, `ConceptMapLoader.cs:782-920`).
  - `<root>\input\elements\ConceptMap-elements-*.json`: ConceptMap JSON for element maps linked to structure maps (`MappingLoader.cs:132`, `ConceptMapLoader.cs:502-510`, `ConceptMapLoader.cs:545-779`).
  - `<root>\input\{SourceShortName}to{TargetShortName}\*.fml`: FML StructureMap text files for pair-specific structure/element mapping details. `FmlLoader` scans both directions for each loaded package pair and only top-level `.fml` files in a matched pair directory (`FmlLoader.cs:77-119`, `FmlLoader.cs:122-187`).
  - No `StructureMap` JSON loader is present in `CrossVersionSource`; the only JSON dispatch in these loaders parses `Hl7.Fhir.Model.ConceptMap`, while FML text is parsed with `FhirMappingLanguage.TryParse` into `FhirStructureMap` (`ConceptMapLoader.cs:403-410`, `ConceptMapLoader.cs:456-461`, `FmlLoader.cs:136-149`).
- **Prior in-memory / database state:**
  - `_db.DbConnection`, the open SQLite connection from `LoadDatabase`, is passed to `MappingLoader` (`XVerProcessor.cs:801`).
  - `_loggerFactory` is passed through for loader diagnostics (`XVerProcessor.cs:801`, `MappingLoader.cs:87-92`, `FmlLoader.cs:48-60`).
  - `MappingLoader` snapshots loaded packages ordered by `PackageVersion` when it is constructed (`MappingLoader.cs:40-51`).

## Outputs

- The loader recreates the mapping tables before loading: `DbMappingClasses.DropTables(_db)` then `DbMappingClasses.CreateTables(_db)` (`MappingLoader.cs:82-84`). The table set is defined in `DbMappingClasses` (`DbMappingClasses.cs:19-35`):
  - `MappingSourceFiles` / `DbMappingSourceFile`: one record per source ConceptMap or FML file, with relative filename, file type flags, and URL (`DbMappingClasses.cs:38-48`, `MappingLoader.cs:164-191`).
  - `ValueSetMappings` / `DbValueSetMapping`: value-set-level mappings from `input\codes` ConceptMaps (`DbMappingClasses.cs:86-103`, `ConceptMapLoader.cs:1032-1066`, `ConceptMapLoader.cs:1210-1217`).
  - `ValueSetConceptMappings` / `DbValueSetConceptMapping`: source concept to target concept or explicit no-map rows from `input\codes` ConceptMaps (`DbMappingClasses.cs:105-132`, `ConceptMapLoader.cs:1068-1217`).
  - `StructureMappings` / `DbStructureMapping`: type, resource, fallback, internal primitive, and FML-created structure mappings (`DbMappingClasses.cs:135-157`, `ConceptMapLoader.cs:127-231`, `ConceptMapLoader.cs:782-920`, `ConceptMapLoader.cs:1221-1555`, `FmlLoader.cs:1227-1306`, `FmlLoader.cs:1399-1405`).
  - `ElementMappings` / `DbElementMapping`: element mappings from `input\elements` ConceptMaps and FML-derived path relationships (`DbMappingClasses.cs:160-195`, `ConceptMapLoader.cs:545-779`, `FmlLoader.cs:1308-1405`).
- Safety input rows are `DbUnsafeCodeElementAnnotation`, `DbUnsafeCodeMapEvidence`, and `DbUnsafeCodeRule`. `DbMappingClasses` also owns the input/pair state tables; it does not own the outcome trigger table.
- The entry method throws when a configured source cannot be loaded; it does not discard the loader's `false` result (`XVerProcessor.LoadFhirCrossVersionMaps`).
- No files are written by this step.

## Optional Unsafe-Code Configuration

`input/ig-support/unsafe-code-mappings.json` is an optional **map-load**
input. Missing file, missing support directory, and `[]` mean no configured
rules for the new snapshot. Configuration does not require an annotation
file. For example:

```json
[
  {
    "system": "http://hl7.org/fhir/filter-operator",
    "version": "source-system,version",
    "code": "exists",
    "noMapOnly": false,
    "excludedRelationship": ["equivalent"],
    "excludedVersion": ["4.0.1"]
  }
]
```

The strict grammar is implemented by `UnsafeCodeConfigurationLoader.Parse`:

| Member | Contract |
|-|-|
| Root | JSON array of objects, not a wrapper object or `null`. |
| `system` | Required nonblank absolute identifier; no whitespace or `\|` version suffix. |
| `code` | Required nonblank string; exact code spelling. |
| `version` | Optional nonblank string; omitted means wildcard source code-system version, not an unknown-version literal. Explicit `null` is invalid. |
| `noMapOnly` | Optional Boolean, default `false`; strings/numbers/null are invalid. |
| `excludedRelationship` | Optional array of nonblank FHIR ConceptMap relationship literals: `related-to`, `equivalent`, `source-is-narrower-than-target`, `source-is-broader-than-target`, `not-related-to`. Default `[]`. |
| `excludedVersion` | Optional array of recognized full FHIR release strings (`major.minor.patch`), e.g. `4.0.1`, `4.3.0`. Short forms such as `R4` or `4.0` are invalid. Default `[]`. |

Unknown/case-mismatched members, wrong types, blank strings, duplicate
object property names (including escaped spellings), malformed JSON,
comments, and trailing commas are rejected. Duplicate object members
are errors even if their values agree. This differs from duplicate
**entries**: entries with the same exact `(system, version, code)` identity
coalesce only when their normalized policy agrees. Omitted defaults equal
explicit `false`/empty arrays; exclusion arrays are sets, ignoring order
and repetition. Contradictory entries fail rather than choosing a winner.
Wildcard and exact-version identities may coexist. No case folding,
trimming-to-match, URI rewriting, or numeric version coercion is applied.
Errors include filename, entry index (or `<root>`), JSON path, reason, and
the underlying read/parse cause.

These are separate identities: `version` matches a **source code-system**
version; `ValueSet/url|version` scopes select a **ValueSet canonical
version**; `excludedVersion` matches the **destination FHIR release**;
`XverArtifactVersion` versions the generated IG. A group system canonical
and a source/target ValueSet scope are parsed separately, not conflated.

## Authored Evidence and Snapshot Lifetime

`UnsafeCodeSourceReader` captures facts from already parsed ConceptMaps
before legacy normalization can skip unresolved concepts or elements:

- An element map must declare code `requires-omitted-mandatory-code`,
  system `http://hl7.org/fhir/uv/xver/ValueSet/xver-conceptmap-properties`,
  with Boolean type, and assign Boolean `true` on the applicable **directed
  target**. Declaration-only, source-level, false, absent, wrong-system,
  or wrong-type properties are not positive annotations. Conflicting
  relevant declarations fail contextually.
- Code evidence records source/target ValueSet scopes and versions,
  source/target systems and versions, code, authored relationship, and
  explicit `noMap: true`. Missing maps, empty targets, and inferred
  comparison failure never create explicit no-map evidence. Facts retain
  file/ConceptMap/group/source/target provenance without concept-resolution
  foreign keys; FML reconciliation does not replace them.
- Annotation plus relevant explicit no-map and independently applicable
  configuration are separate positive mechanisms. Eligibility and
  code/context-local exemptions are applied during
  [outcomes](./xver-generate-outcomes.md#unsafe-code-safety-outcomes), not
  by changing comparisons.

Configuration preflight precedes any destructive map reset. A bad or
unreadable configuration retains the preceding completed snapshot,
including old mappings and completion revisions. Once an accepted load
starts, completion is invalidated before replacement. The new completed
input revision is published **last**, after the relevant map capture and
existing loading stages finish. A mid-load source/database failure may
leave partial rows but never a completed snapshot. This is explicit marker
invalidation and last-write completion, **not** a transaction around the
entire loader: generated insert/update helpers already open transactions.
Do not treat incomplete rows as an evaluated-empty policy.

Export/build reads persisted decisions, never rereads this JSON. Editing
or removing the file alone deliberately leaves existing exported policy
unchanged. To apply either change: reload maps → compare structures →
generate outcomes → export. A reload without new comparisons cannot
certify outcomes over old element-mapping provenance.

## Algorithm

1. `LoadFhirCrossVersionMaps` ensures `_db` exists. If not, it calls `LoadDatabase(false)` and fails fast only if the database remains null (`XVerProcessor.cs:792-799`).
2. With no source path, publish the explicit empty safety snapshot described above and return. Otherwise construct `MappingLoader`.
3. `MappingLoader.TryLoadCrossVersionSourceMaps` validates that the source path exists. Invalid or missing directories log an error and return `false` before any table reset (`MappingLoader.cs:53-64`).
4. `MappingLoader` verifies database access, creates a `PackageLoader` configured not to auto-load expansions or resolve dependencies, and preflights optional unsafe-code configuration. Only then does it invalidate/drop/recreate mapping state, begin the new input snapshot, and insert normalized rules (`MappingLoader.TryLoadCrossVersionSourceMaps`).
5. `ConceptMapLoader` loads source ConceptMaps in this order: `input\codes`, type maps / internal type maps, `input\resources`, and `input\elements` (`MappingLoader.cs:86-132`). For each relative path, it requires `<root>\input` and the specific subdirectory, otherwise it logs a warning and returns for that category (`ConceptMapLoader.cs:359-373`).
6. `ConceptMapLoader.LoadSourceMaps` enumerates matching top-level files only, parses source/target versions from filenames, skips maps whose source or target package is not in the loaded database, parses the JSON as `ConceptMap`, dispatches by category, and logs category totals (`ConceptMapLoader.cs:392-541`).
7. Code ConceptMaps create or reuse `ValueSetMappings`, then add `ValueSetConceptMappings` for mapped concepts or explicit no-map concepts. Invalid source/target concept literals are logged and skipped, while unresolved source or target value sets throw unless they are in the processor exclusion set or are non-expandable (`ConceptMapLoader.cs:925-1217`).
8. Type ConceptMaps create `StructureMappings` for non-primitive data types and explicit no-map rows. Primitive source types in these external type ConceptMaps are skipped even when `UseInternalTypeMaps` is false (`ConceptMapLoader.cs:1221-1367`). The fallback type ConceptMap is applied across every ordered pair of loaded packages, marks rows `IsFallback = true`, and skips pairs/types not present in the package pair (`ConceptMapLoader.cs:1369-1555`).
9. Resource ConceptMaps create `StructureMappings` for resource mappings or explicit no-map rows (`ConceptMapLoader.cs:782-920`). Element ConceptMaps locate a relevant structure map, then create `ElementMappings` for target elements or explicit no-map rows; unresolved element paths often log and continue rather than throwing (`ConceptMapLoader.cs:545-779`).
10. If `UseInternalTypeMaps` is true, `ConceptMapLoader.LoadInternalTypeMaps(loadPrimitives: true, loadComplex: false)` adds built-in primitive type `StructureMappings` for both directions of each loaded package pair before external type ConceptMaps are loaded (`MappingLoader.cs:109-124`, `ConceptMapLoader.cs:54-124`, `ConceptMapLoader.cs:127-231`). The mapping data comes from `FhirTypeMappings.PrimitiveMappings` (`FhirTypeMappings.cs:112-120`).
11. `FmlLoader.LoadSourceFml` scans `<root>\input` for package-pair directories named `{source.ShortName}to{target.ShortName}` and the reverse direction for every loaded package pair. Each top-level `.fml` file is parsed; names ending in the no-`R` source-to-target suffix are normalized, and maps named `primitives` are skipped (`FmlLoader.cs:77-119`, `FmlLoader.cs:122-187`).
12. For each parsed FML map, `FmlLoader` records a `MappingSourceFiles` row, resolves source/target structures and root group parameters against the database, walks simple-copy and complex mapping expressions to collect source-to-target element path relationships, and then reconciles those paths into `StructureMappings`/`ElementMappings` by adding missing rows or updating FML source metadata on existing rows (`FmlLoader.cs:189-403`, `FmlLoader.cs:575-895`, `FmlLoader.cs:1128-1408`).
13. After all categories and FML, publish completed inputs and return `true`; `LoadFhirCrossVersionMaps` propagates failure instead of ignoring `false`.

## Decision Points

- **Rule:** A database is loaded implicitly if this step is invoked before `_db` is initialized. **Source:** `XVerProcessor.cs:792-799`. **Rationale:** This lets `load-maps` work without the caller separately opening the comparison database, while still throwing if the database cannot be created or opened.
- **Rule:** No configured map source explicitly completes empty safety inputs while preserving legacy maps. **Source:** `XVerProcessor.LoadFhirCrossVersionMaps`, `UnsafeCodeInputStore.ReplaceWithEmptyInputs`. **Rationale:** A legitimate source-free run must remain distinguishable from a missing/failed load.
- **Rule:** When `CrossVersionMapSourcePath` is non-empty but points to a missing directory, the lower-level loader logs an error and returns `false`. **Source:** `MappingLoader.cs:53-64`. **Rationale:** The loader distinguishes "not configured" at the entry point from "configured but invalid" inside the source loader.
- **Rule:** Map loading is destructive for mapping tables: it drops and recreates `MappingSourceFiles`, `ValueSetMappings`, `ValueSetConceptMappings`, `StructureMappings`, and `ElementMappings` before loading. **Source:** `MappingLoader.cs:82-84`, `DbMappingClasses.cs:19-35`. **Rationale:** This avoids mixing stale source-derived maps with the current source tree and loaded package set.
- **Rule:** `UseInternalTypeMaps == true` loads built-in primitive maps (`loadPrimitives: true`, `loadComplex: false`) before loading external type ConceptMaps; `false` skips the internal load and loads only external type ConceptMaps. **Source:** `XVerProcessor.cs:804-806`, `MappingLoader.cs:109-124`, `ConceptMapLoader.cs:54-124`, `ConceptMapLoader.cs:127-231`. **Rationale:** The built-in primitive list is curated in code (`FhirTypeMappings.PrimitiveMappings`) and is used as the primitive authority, while complex type handling remains sourced from ConceptMaps/FML in this path.
- **Rule:** External type ConceptMaps do not add primitive source type mappings; `loadSourceTypeMap` skips rows whose resolved source structure is `PrimitiveType`. **Source:** `ConceptMapLoader.cs:1252-1266`. **Rationale:** The source comment says primitive source maps need source-map fixes and are intentionally replaced by internal maps for now.
- **Rule:** File-format dispatch is by folder/pattern: ConceptMap JSON is loaded from `codes`, `types`, `resources`, and `elements`; FML text is loaded from package-pair directories; no StructureMap JSON reader exists in these loader classes. **Source:** `MappingLoader.cs:97-139`, `ConceptMapLoader.cs:359-461`, `FmlLoader.cs:77-187`. **Rationale:** Keeping ConceptMap and FML responsibilities separate lets ConceptMaps establish artifact/value-set mappings and lets FML refine path-level structure/element mappings.
- **Rule:** Duplicate rows are generally inserted with `ignoreDuplicates: true`, and FML updates existing structure/element map rows with its source-file metadata rather than always creating new rows. **Source:** `ConceptMapLoader.cs:120-124`, `ConceptMapLoader.cs:774-778`, `ConceptMapLoader.cs:916-920`, `ConceptMapLoader.cs:1210-1217`, `ConceptMapLoader.cs:1362-1367`, `ConceptMapLoader.cs:1551-1555`, `FmlLoader.cs:1297-1306`, `FmlLoader.cs:1383-1405`. **Rationale:** AI Guess: this makes map loading tolerant of overlapping authorities, with exact duplicate persistence left to table constraints and generated SQLite helpers; reviewer please confirm the exact uniqueness constraints generated for these partial classes.
- **Rule:** The entry method throws on a configured source loader's `false` result. **Source:** `XVerProcessor.LoadFhirCrossVersionMaps`. **Rationale:** An invalid configured source must not look like successfully loaded empty policy.
- **Rule:** FML loading only considers loaded package pairs and scans both directions under exact `{ShortName}to{ShortName}` directory names. **Source:** `FmlLoader.cs:77-119`. **Rationale:** Pair-scoped directories prevent FML intended for one FHIR-version pair from being applied to unrelated package combinations.

## Rationale Coverage

`Decisions: 10 total — cited: 9 — AI Guess: 1 — unresolved: 0`

The remaining `AI Guess:` concerns legacy duplicate-row tolerance, not the strict configuration contract above.

## Failure Modes & Edge Cases

- Invalid configured source directories return `false` in `MappingLoader` and throw at the processor boundary; an absent source instead completes explicit empty safety inputs.
- Missing `<root>\input` or category subdirectories log warnings and skip that category; they do not abort the full map load once the root path has been accepted (`ConceptMapLoader.cs:359-373`, `FmlLoader.cs:77-84`).
- Safety-relevant code/element ConceptMap parse/read failures abort with filename and direction context, leaving input completion unset. Invalid filename version tokens, group domains, and unresolved required structures/resources/types/value sets also propagate loader failures.
- Malformed or unprocessable FML is mostly contained per file: parse failures log an error and continue; exceptions while processing a file are caught and logged by `loadSourceFml`; unresolved expressions inside groups often log or skip, but missing required root structures or no processable groups throw and are caught at the per-file boundary (`FmlLoader.cs:142-185`, `FmlLoader.cs:189-403`, `FmlLoader.cs:637-681`, `FmlLoader.cs:863-895`).
- Fewer than two loaded packages means pair-based loaders have little or nothing to do: internal type maps iterate `targetIndex < sourceIndex`, fallback maps skip same-package pairs, and FML iterates `sourceIndex < _packages.Count - 1`; ConceptMap files can still be enumerated but are skipped if their source or target package is not loaded (`ConceptMapLoader.cs:54-92`, `ConceptMapLoader.cs:435-454`, `ConceptMapLoader.cs:1398-1414`, `FmlLoader.cs:86-119`).
- FML may decline to create a structure mapping if other maps already exist for the source structure and target package but not the FML target structure; it logs a warning and continues (`FmlLoader.cs:1239-1264`).
- Primitive FML files named `primitives` are skipped because primitive mappings are expected to come from internal type maps (`FmlLoader.cs:164-173`). External type ConceptMaps also skip primitive source structures (`ConceptMapLoader.cs:1262-1266`).

## Coverage Checklist

- [x] `XVerProcessor.LoadFhirCrossVersionMaps` (`XVerProcessor.cs:790-809`)
- [x] `XVerProcessor.ProcessCommand` invocation paths (`XVerProcessor.cs:256-431`)
- [x] `MappingLoader.TryLoadCrossVersionSourceMaps` (`MappingLoader.cs:53-154`)
- [x] `MappingLoader.getOrCreateMappingSourceFileKey` and filename parsers (`MappingLoader.cs:164-235`)
- [x] `ConceptMapLoader.LoadInternalTypeMaps` and internal primitive map builders (`ConceptMapLoader.cs:54-231`)
- [x] `ConceptMapLoader.LoadSourceMaps` dispatch (`ConceptMapLoader.cs:350-542`)
- [x] `ConceptMapLoader` element/resource/code/type/fallback loaders (`ConceptMapLoader.cs:545-1558`)
- [x] `FmlLoader.LoadSourceFml` and FML processing/reconciliation (`FmlLoader.cs:77-1408`)
- [x] Mapping table definitions (`DbMappingClasses.cs:19-195`)
- [x] `ConfigXVer` options referenced by this step (`ConfigXVer.cs:39-57`, `ConfigXVer.cs:437-448`)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:146-166`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:256-431`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:790-809`
  - `src/Fhir.CodeGen.Comparison/CrossVersionSource/MappingLoader.cs:40-154`
  - `src/Fhir.CodeGen.Comparison/CrossVersionSource/MappingLoader.cs:164-235`
  - `src/Fhir.CodeGen.Comparison/CrossVersionSource/ConceptMapLoader.cs:54-231`
  - `src/Fhir.CodeGen.Comparison/CrossVersionSource/ConceptMapLoader.cs:350-1558`
  - `src/Fhir.CodeGen.Comparison/CrossVersionSource/FmlLoader.cs:77-1408`
  - `src/Fhir.CodeGen.Comparison/Models/DbMappingClasses.cs:19-195`
  - `src/Fhir.CodeGen.Comparison/CompareTool/FhirTypeMappings.cs:112-120`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs:39-57`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs:437-448`
- Related specs:
  - [`xver-load-database.md`](./xver-load-database.md) — prerequisite
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md) — sibling load step
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md) — sibling load step
  - [`xver-compare-in-database.md`](./xver-compare-in-database.md) — downstream consumer
  - [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md)
- Related `ConfigXVer` options: `CrossVersionMapSourcePath`, `UseInternalTypeMaps`, `ReloadDatabase`.

---
*Legacy loader references verified at `d02100974b2dc1b05ecf1af69c29095e6973f4c8`; safety snapshot/configuration contracts rechecked at `88af2c4e4fb0f6f770297f0e1209efc212317af6` on `2026-11-05`.*
