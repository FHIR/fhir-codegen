# XVerProcessor.LoadFhirTypeValueSets — Step 4 of 7

## Purpose

`LoadFhirTypeValueSets` ingests a hand-curated list of "FHIR-type ValueSets": ValueSets whose concepts are FHIR type names such as `Patient`, `Observation`, `Reference`, or datatype names. The list is loaded from the cross-version map source tree into the comparison database so value-set comparison can allow FHIR type/resource mapping fallbacks for those specific bindings. Downstream comparison and outcome/export steps then consume the resulting value-set comparisons when deciding whether `value[x]` content needs generated cross-version support and how Reference-like target/profile information should be carried forward.

## Invocation & Preconditions

`ProcessCommand` invokes this step directly for `load`, after database, cross-version map, and extension-substitution loading; for `load-maps`, after cross-version map loading; and in the default full pipeline before compare/outcomes/export (`XVerProcessor.cs:314-329`, `XVerProcessor.cs:421-428`). The `compare`, `compare-vs`, `compare-sd`, `outcomes`, `outcomes-vs`, `outcomes-sd`, and `export` commands call it only inside their `_config.ReloadDatabase` blocks, after `LoadDatabase(...)`, `LoadFhirCrossVersionMaps()`, and `LoadExtensionSubstitutions()` (`XVerProcessor.cs:336-418`).

The method requires a loaded `ComparisonDatabase`. If `_db` is null, it calls `LoadDatabase(false)` and throws `Failed to create or load a comparison database!` if that still leaves `_db` null (`XVerProcessor.cs:766-775`). It then forwards `_config.CrossVersionMapSourcePath` unchanged to `_db.TryLoadFhirTypeValueSets(...)`; if the database-layer loader returns `false`, the entry method throws `Failed to load extension FHIR-type value set list from source path: {CrossVersionMapSourcePath}` (`XVerProcessor.cs:777-780`).

The relevant configuration inputs are `ConfigXVer.CrossVersionMapSourcePath`, exposed as `--map-source-path` / `Map_Source_Path`, and `ConfigRoot.LogFactory`, which the processor stores for its own logger and passes to a newly-created comparison database during forced database loading (`ConfigXVer.cs:39-57`, `ConfigRoot.cs:30-32`, `XVerProcessor.cs:161-165`, `XVerProcessor.cs:520-534`).

## Inputs

- **Configuration (`ConfigXVer` keys):** `CrossVersionMapSourcePath` is the root path used to locate the source JSON file; `LogFactory` supplies loggers used by `XVerProcessor` and by newly-created `ComparisonDatabase` instances (`ConfigXVer.cs:39-57`, `ConfigRoot.cs:30-32`, `XVerProcessor.cs:161-165`, `XVerProcessor.cs:528`).
- **On-disk sources:** exactly one JSON file is read: `<CrossVersionMapSourcePath>\input\ig-support\valueSetsOfFhirTypes.json` (`ComparisonDatabase.cs:199-203`). The loader does not scan the directory or merge multiple files. The file is parsed with `System.Text.Json` as `List<string>`, where each string is treated as an unversioned ValueSet URL to store (`ComparisonDatabase.cs:208-221`, `ComparisonDatabase.cs:237-241`).
- **Prior in-memory / database state:** `_db`, populated by `LoadDatabase(false)` if needed (`XVerProcessor.cs:768-775`). The successful non-empty loader path destructively recreates only the support table for FHIR-type ValueSet URLs, so the database connection must be open and writable (`ComparisonDatabase.cs:232-241`).
- **Method signature:**
  ```csharp
  public void LoadFhirTypeValueSets()
  ```
  (`XVerProcessor.cs:766`)

## Outputs

The loader writes no files. On a successful non-empty load, it drops and recreates one support table, loads its key counter, inserts rows, and returns `true` (`ComparisonDatabase.cs:230-243`).

- **`FhirTypeValueSets`** (`DbFhirTypeValueSet`, table name at `DbContentClasses.cs:112-116`):
  - `Key` (`int`, primary key inherited from `DbRecordBase`; `DbBaseClasses.cs:11-15`).
  - `UnversionedUrl` (`string`, required; indexed by `[CgSQLiteIndex(nameof(UnversionedUrl))]`; `DbContentClasses.cs:112-116`).

The row list is built by assigning `DbFhirTypeValueSet.GetIndex()` to each JSON URL and setting `UnversionedUrl = v`; insertion uses `ignoreDuplicates: true` and `insertPrimaryKey: true` (`ComparisonDatabase.cs:233-241`). The table is also part of the support-table set created/dropped by `DbContentClasses.CreateTables` / `DropTables` and included in max-key loading (`DbContentClasses.cs:26-29`, `DbContentClasses.cs:63-68`, `DbContentClasses.cs:103-108`).

## Algorithm

1. `XVerProcessor.LoadFhirTypeValueSets` checks whether `_db` is null (`XVerProcessor.cs:766-769`).
2. If needed, it calls `LoadDatabase(false)` and throws if `_db` is still null, making the loader dependent on a usable comparison database before any source JSON is read (`XVerProcessor.cs:770-775`).
3. It calls `_db.TryLoadFhirTypeValueSets(_config.CrossVersionMapSourcePath)` and throws if that call returns `false` (`XVerProcessor.cs:777-780`).
4. `ComparisonDatabase.TryLoadFhirTypeValueSets` constructs the single source filename `<CrossVersionMapSourcePath>\input\ig-support\valueSetsOfFhirTypes.json` (`ComparisonDatabase.cs:199-203`).
5. If that file does not exist, the loader throws `Could not find FHIR-type value set list source file at {filename}!` before entering its JSON parse `try` block (`ComparisonDatabase.cs:202-206`).
6. Inside the `try` block, it logs the filename, opens the JSON file read-only, and deserializes it as `List<string>` (`ComparisonDatabase.cs:210-216`).
7. If deserialization yields `null` or an empty list, it logs a warning and returns `true` immediately (`ComparisonDatabase.cs:216-221`). Because table recreation occurs later, this early-return path does not clear or create `FhirTypeValueSets` (`ComparisonDatabase.cs:216-234`).
8. Any exception thrown while opening or deserializing the JSON file is logged as an error and converted to `false`, which the entry method then converts to its source-path exception (`ComparisonDatabase.cs:224-228`, `XVerProcessor.cs:777-780`).
9. For a non-empty list, the loader logs the count, drops and recreates `FhirTypeValueSets`, and loads the table's max key (`ComparisonDatabase.cs:230-235`).
10. It projects every URL string into a `DbFhirTypeValueSet` row with a generated primary key and `UnversionedUrl` equal to the original string (`ComparisonDatabase.cs:237-239`).
11. It inserts the projected rows with primary keys and duplicate-ignore semantics, then returns `true` (`ComparisonDatabase.cs:241-243`).
12. During downstream comparison, `FhirDbComparer.Compare` runs `ValueSetComparer.CompareValueSets` before `StructureComparer.CompareStructures` when both value-set and structure processing are enabled (`FhirDbComparer.cs:111-140`). `ValueSetComparer` reads `FhirTypeValueSets` once into `_fhirTypeValueSetUrls` at the start of value-set comparison (`ValueSetComparer.cs:100-105`).
13. `ValueSetComparer` treats a source ValueSet as allowing type fallbacks when its `UnversionedUrl` is present in that hash set. In transitive comparisons, type fallbacks are considered only when no target concept was reached; in direct comparisons, they are considered before ordinary same-code matching (`ValueSetComparer.cs:442-470`, `ValueSetComparer.cs:622-679`, `ValueSetComparer.cs:714-717`, `ValueSetComparer.cs:888-917`).
14. Structure comparison and outcomes consume the resulting value-set comparisons indirectly: element comparison looks up the bound `DbValueSetComparison` when both source and target elements have binding ValueSet keys, outcome generation turns those comparisons into `DbValueSetOutcome` / concept outcomes, and the FHIR exporter later uses those outcomes when binding generated `Extension.value[x]` elements (`ElementComparer.cs:323-350`, `ElementComparer.cs:531-558`, `ValueSetOutcomeGenerator.cs:333-418`, `ValueSetOutcomeGenerator.cs:424-493`, `StructureFhirExporter.cs:3233-3265`, `StructureFhirExporter.cs:3304-3335`).
15. The same exporter decides whether source types can be represented directly as `Extension.value[x]` types or must be emitted as child extension slices, and it preserves type profiles / target profiles for Reference-like types when adding value types (`StructureFhirExporter.cs:2825-2857`, `StructureFhirExporter.cs:2998-3164`, `StructureFhirExporter.cs:3184-3230`, `StructureFhirExporter.cs:3339-3368`, `FhirTypeMappings.cs:216-231`).

## Decision Points

- **Rule:** A "FHIR-type ValueSet" is not inferred from package content. It is any URL string listed in `<CrossVersionMapSourcePath>\input\ig-support\valueSetsOfFhirTypes.json`; every string in the non-empty JSON list is inserted as `DbFhirTypeValueSet.UnversionedUrl`. **Source:** `ComparisonDatabase.cs:202-216`, `ComparisonDatabase.cs:237-241`, `DbContentClasses.cs:112-116`. **Rationale:** The downstream comparer gates type/resource/fallback concept matching on membership in this URL set, so only hand-curated bindings get FHIR-type fallback behavior (`ValueSetComparer.cs:100-105`, `ValueSetComparer.cs:442-470`, `ValueSetComparer.cs:714-917`).
- **Rule:** Missing source file is a hard exception rather than a `false` return. **Source:** `ComparisonDatabase.cs:202-206`. **Rationale:** The file existence check is outside the parse `try`/`catch`; missing required support content stops the load before JSON parsing begins (`ComparisonDatabase.cs:202-228`).
- **Rule:** JSON open/deserialization failures return `false`, and `LoadFhirTypeValueSets` converts that `false` into an exception. **Source:** `ComparisonDatabase.cs:210-228`, `XVerProcessor.cs:777-780`. **Rationale:** The database layer records the low-level parse/opening error in logs, while the processor raises a pipeline-level failure message tied to the configured source path.
- **Rule:** Empty `CrossVersionMapSourcePath` is not skipped or specially handled. The loader passes it to `Path.Combine`, producing a relative `input\ig-support\valueSetsOfFhirTypes.json` path for the default empty string; if that file is absent, the missing-file exception is thrown. **Source:** `ConfigXVer.cs:39-57`, `XVerProcessor.cs:777`, `ComparisonDatabase.cs:199-206`. **Rationale:** The code contains no `IsNullOrEmpty` guard in this step, unlike `LoadFhirCrossVersionMaps`, which explicitly checks the source path before loading map artifacts (`XVerProcessor.cs:801-807`).
- **Rule:** A successful non-empty load replaces the `FhirTypeValueSets` table; an empty/null deserialized list returns success before replacement. **Source:** empty early return at `ComparisonDatabase.cs:216-221`; replacement at `ComparisonDatabase.cs:230-241`. **Rationale:** Non-empty source content is treated as the authoritative projection for the support table. AI Guess: the empty-list no-op is intended to avoid failing when the support list is intentionally absent, but it can leave existing table contents untouched because the drop/create happens after the early return.
- **Rule:** Downstream consumer behavior is isolated to value-set comparison. `ValueSetComparer.CompareValueSets` reads the table into a `HashSet<string>` once, then uses it to decide whether FHIR type/resource/fallback maps may supply target concepts when a FHIR-type code is otherwise unmatched. **Source:** `ValueSetComparer.cs:100-105`, `ValueSetComparer.cs:442-470`, `ValueSetComparer.cs:622-679`, `ValueSetComparer.cs:714-917`. **Rationale:** This prevents ordinary ValueSets from receiving type-name fallback behavior while allowing FHIR-type bindings to map concepts whose names changed or moved across FHIR releases.
- **Rule:** Structure comparison, outcome generation, and export do not read `DbFhirTypeValueSet` directly; they receive its effect through `DbValueSetComparison` and `DbValueSetOutcome` records. **Source:** `FhirDbComparer.cs:121-140`, `ElementComparer.cs:323-350`, `ElementComparer.cs:531-558`, `ValueSetOutcomeGenerator.cs:333-418`, `StructureFhirExporter.cs:3233-3265`, `StructureFhirExporter.cs:3304-3335`. **Rationale:** AI Guess: the intended pipeline boundary is that FHIR-type URL knowledge belongs to value-set comparison, while structure/outcome/export logic consumes normalized comparison/outcome facts when deciding generated `value[x]` bindings, extension slices, and Reference-like target-profile carry-forward.

## Rationale Coverage

`Decisions: 7 total — cited: 5 — AI Guess: 2 — unresolved: 0`

Cited decisions: definition of the loaded URL set; missing-file behavior; parse-failure behavior; empty path behavior; value-set comparer fallback behavior. AI Guess decisions: the intent behind the empty-list no-op and the pipeline-boundary rationale for indirect structure/outcome/export effects. Unresolved decisions: none.

## Failure Modes & Edge Cases

- `_db` remains null after `LoadDatabase(false)`: `LoadFhirTypeValueSets` throws `Failed to create or load a comparison database!` (`XVerProcessor.cs:768-775`).
- `TryLoadFhirTypeValueSets` returns `false`: `LoadFhirTypeValueSets` throws `Failed to load extension FHIR-type value set list from source path: {_config.CrossVersionMapSourcePath}` (`XVerProcessor.cs:777-780`). In the current loader, `false` is returned only for exceptions caught while opening/deserializing the JSON file (`ComparisonDatabase.cs:210-228`).
- Missing source file or directory: `TryLoadFhirTypeValueSets` throws `Could not find FHIR-type value set list source file at {filename}!` before the parse `try` block, so the processor's `false`-return wrapper is not used for this case (`ComparisonDatabase.cs:202-206`, `XVerProcessor.cs:777-780`).
- Empty `CrossVersionMapSourcePath`: because no guard exists, the effective source is the relative file `input\ig-support\valueSetsOfFhirTypes.json`; if it is absent, the missing-file exception is thrown (`ComparisonDatabase.cs:199-206`).
- Null `crossVersionMapSourcePath`: although the config property is non-null and defaults to `string.Empty`, the method signature does not validate null before `Path.Combine`; a null caller value would fail before the loader reaches the file-existence check (`ConfigXVer.cs:39-57`, `ComparisonDatabase.cs:199-203`).
- Malformed JSON or wrong JSON shape: exceptions from file open/deserialization are logged and returned as `false`, which the entry method converts to its source-path failure exception (`ComparisonDatabase.cs:210-228`, `XVerProcessor.cs:777-780`).
- Deserialized null or empty list: the loader logs a warning and returns `true` without dropping or recreating `FhirTypeValueSets`, because table replacement starts after that early return (`ComparisonDatabase.cs:216-234`). This can preserve stale rows if the table already existed.
- Duplicate URLs in the JSON: the loader creates one row candidate per string and inserts with `ignoreDuplicates: true`, but the visible row class only defines an index, not a unique URL constraint; duplicate behavior therefore depends on generated SQLite constraints outside the handwritten row class (`ComparisonDatabase.cs:237-241`, `DbContentClasses.cs:112-116`).
- Non-URL strings or URLs not present in `DbValueSet`: no validation is performed by this loader. Such strings are stored but will only matter if a source `DbValueSet.UnversionedUrl` exactly matches them during value-set comparison (`ComparisonDatabase.cs:237-241`, `ValueSetComparer.cs:442`, `ValueSetComparer.cs:714`).

## Coverage Checklist

- [x] `XVerProcessor.LoadFhirTypeValueSets` (`XVerProcessor.cs:766-781`)
- [x] `XVerProcessor.ProcessCommand` invocation paths (`XVerProcessor.cs:314-329`, `XVerProcessor.cs:336-418`, `XVerProcessor.cs:421-428`)
- [x] `ComparisonDatabase.TryLoadFhirTypeValueSets` (`ComparisonDatabase.cs:199-244`)
- [x] `DbFhirTypeValueSet` row class (`DbContentClasses.cs:112-116`) and inherited key (`DbBaseClasses.cs:11-15`)
- [x] Downstream consumer pointers in `ValueSetComparer.cs` (`ValueSetComparer.cs:100-105`, `ValueSetComparer.cs:442-470`, `ValueSetComparer.cs:622-679`, `ValueSetComparer.cs:714-917`)
- [x] Indirect structure/outcome/export path (`FhirDbComparer.cs:121-140`, `ElementComparer.cs:323-350`, `ElementComparer.cs:531-558`, `ValueSetOutcomeGenerator.cs:333-493`, `StructureFhirExporter.cs:2825-2857`, `StructureFhirExporter.cs:3233-3368`)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:314-329`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:336-418`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:421-428`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:766-781`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:801-807`
  - `src/Fhir.CodeGen.Comparison/Models/ComparisonDatabase.cs:199-244` (TryLoadFhirTypeValueSets)
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs:26-29`
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs:63-68`
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs:103-116`
  - `src/Fhir.CodeGen.Comparison/Models/DbBaseClasses.cs:11-15`
  - `src/Fhir.CodeGen.Comparison/CompareTool/FhirDbComparer.cs:111-140`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:100-105` (consumer)
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:442-470`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:622-679`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ValueSetComparer.cs:714-917`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ElementComparer.cs:323-350`
  - `src/Fhir.CodeGen.Comparison/CompareTool/ElementComparer.cs:531-558`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ValueSetOutcomeGenerator.cs:333-493`
  - `src/Fhir.CodeGen.Comparison/Exporter/StructureFhirExporter.cs:2825-2857`
  - `src/Fhir.CodeGen.Comparison/Exporter/StructureFhirExporter.cs:2998-3164`
  - `src/Fhir.CodeGen.Comparison/Exporter/StructureFhirExporter.cs:3184-3368`
  - `src/Fhir.CodeGen.Comparison/CompareTool/FhirTypeMappings.cs:216-231`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs:39-57`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs:30-32`
- Related specs:
  - [`xver-load-database.md`](./xver-load-database.md) — prerequisite
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md)
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md)
  - [`xver-compare-in-database.md`](./xver-compare-in-database.md) — primary downstream consumer
  - [`xver-generate-outcomes.md`](./xver-generate-outcomes.md)
- Related `ConfigXVer` options: `CrossVersionMapSourcePath`, `ReloadDatabase`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
