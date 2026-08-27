# XVerProcessor.LoadExtensionSubstitutions — Step 3 of 7

## Purpose

`LoadExtensionSubstitutions` ingests hand-authored extension substitution definitions from the configured cross-version source path. These substitutions tell downstream comparison and outcome-generation steps that a given source element, expanded source context, or source type case should be represented by an explicitly named extension instead of by a generated cross-version extension definition. This is one of the most decision-rich load steps because it is where pipeline maintainers inject human judgment about extension identity, naming, modifier status, and FHIR-version applicability into the generated outcomes.

## Invocation & Preconditions

`ProcessCommand` invokes this step directly for `load`, `load-base`, `load-substitutions`, and the default full pipeline (`XVerProcessor.cs:314-424`). The `compare`, `compare-vs`, `compare-sd`, `outcomes`, `outcomes-vs`, `outcomes-sd`, and `export` commands call it only inside their `_config.ReloadDatabase` blocks, after `LoadDatabase(...)` and `LoadFhirCrossVersionMaps()` and before `LoadFhirTypeValueSets()` (`XVerProcessor.cs:336-418`).

The method requires a loaded `ComparisonDatabase`. If `_db` is null, it calls `LoadDatabase(false)` and throws `Failed to create or load a comparison database!` if that still leaves `_db` null (`XVerProcessor.cs:749-758`). It then forwards `_config.CrossVersionMapSourcePath` unchanged to `_db.TryLoadExtensionSubstitutions(...)` and throws `Failed to load extension substitutions from source path: {CrossVersionMapSourcePath}` only when that loader returns `false` (`XVerProcessor.cs:760-763`). The database loader itself does not perform an `IsNullOrEmpty` guard: its first operation is `Path.Combine(crossVersionMapSourcePath, "input", "ig-support", "extensionSubstitutions.json")` (`ComparisonDatabase.cs:246-254`). As a result, an empty path is interpreted as the relative path `input\ig-support\extensionSubstitutions.json`; a missing file or missing directory throws `Could not find extension substitution source file at {filename}!` before the method reaches its JSON parsing `try` block (`ComparisonDatabase.cs:250-254`).

The relevant configuration inputs are `ConfigXVer.CrossVersionMapSourcePath`, exposed as `--map-source-path` / `Map_Source_Path` (`ConfigXVer.cs:39-57`), and `ConfigRoot.LogFactory`, which the processor stores for logging and database construction (`ConfigRoot.cs:30-32`, `XVerProcessor.cs:161-165`).

## Inputs

- **Configuration (`ConfigXVer` keys):** `CrossVersionMapSourcePath` is the root path used to locate the substitution JSON file; `LogFactory` supplies the loggers used by `XVerProcessor`, `ComparisonDatabase`, and package loading (`ConfigXVer.cs:39-57`, `ConfigRoot.cs:30-32`, `XVerProcessor.cs:161-165`).
- **On-disk sources:** exactly one file is loaded: `<CrossVersionMapSourcePath>\input\ig-support\extensionSubstitutions.json` (`ComparisonDatabase.cs:250`). The loader does not scan a directory tree or merge multiple substitution files. The file is parsed with `System.Text.Json` as `List<DbExtensionSubstitution>` (`ComparisonDatabase.cs:256-264`). When a record supplies `ReplacementSourcePackage`, the loader also resolves that FHIR package through `PackageLoader`, appending `@latest` unless the package directive already contains `#` or `@` (`ComparisonDatabase.cs:307-332`). It then resolves `ReplacementUrl` as a `StructureDefinition` canonical in that package and reads the extension contexts from the resolved StructureDefinition (`ComparisonDatabase.cs:335-347`).
- **Prior in-memory / database state:** `_db`, populated by `LoadDatabase(false)` if needed (`XVerProcessor.cs:751-758`), and the already-loaded `FhirPackages` table. `TryLoadExtensionSubstitutions` selects all packages ordered by `PackageVersion` and uses their `DefinitionFhirSequence` values to expand version-scoped substitution records (`ComparisonDatabase.cs:287-350`).
- **Method signature:**
  ```csharp
  public void LoadExtensionSubstitutions()
  ```
  (`XVerProcessor.cs:749`)

## Outputs

The loader rewrites one database table and writes no files.

- **`ExtensionSubstitutions`** (`DbExtensionSubstitution`, table name at `DbContentClasses.cs:119-121`):
  - `Key` (`int`, primary key inherited from `DbRecordBase`; `DbBaseClasses.cs:11-15`)
  - `ReplacementUrl` (`string`, required)
  - `ReplacementName` (`string?`)
  - `ReplacementSourcePackage` (`string?`)
  - `SourceVersion` (`string?`)
  - `SourceFhirSequence` (`FhirSequenceCodes?`)
  - `SourceElementId` (`string?`)
  - `SourceTypeReplacement` (`string?`)
  - `SourceFromContextElement` (`string?`)
  - `SourceFromContextExpandedLiteral` (`string?`)
  - `IsModifier` (`bool?`)
  - `ContextsLiteral` (`string?`)

`SourceFromContextExpanded` and `Contexts` are public list facades over the `*Literal` columns but are marked `[CgSQLiteIgnore]`, so they are not database columns (`DbContentClasses.cs:132-179`). The table has an index on `SourceElementId` (`DbContentClasses.cs:119-121`). `TryLoadExtensionSubstitutions` drops and recreates this table, loads the max key counter, stages records in a `DbRecordCache<DbExtensionSubstitution>`, and inserts staged rows with `ignoreDuplicates: true` and `insertPrimaryKey: true` (`ComparisonDatabase.cs:280-285`, `ComparisonDatabase.cs:445-449`).

## Algorithm

1. `XVerProcessor.LoadExtensionSubstitutions` ensures `_db` exists by calling `LoadDatabase(false)` on demand and throwing if database creation/load still fails (`XVerProcessor.cs:749-758`).
2. It calls `_db.TryLoadExtensionSubstitutions(_config.CrossVersionMapSourcePath)` and throws a source-path-specific exception if the loader returns `false` (`XVerProcessor.cs:760-763`).
3. `ComparisonDatabase.TryLoadExtensionSubstitutions` constructs the single source filename `<CrossVersionMapSourcePath>\input\ig-support\extensionSubstitutions.json` and throws immediately if that file does not exist (`ComparisonDatabase.cs:246-254`).
4. It logs the filename, opens the JSON file, deserializes it as `List<DbExtensionSubstitution>`, and returns `true` with a warning if the array is null or empty. JSON read or deserialization exceptions are logged and converted to `false` (`ComparisonDatabase.cs:256-276`).
5. It logs the number of substitution requests, drops and recreates `ExtensionSubstitutions`, loads the table's max key, creates an empty record cache, creates an empty package-definition cache, and selects the database packages ordered by `PackageVersion` (`ComparisonDatabase.cs:278-289`).
6. For each JSON record, it backfills `SourceFhirSequence` from `SourceVersion` when the sequence is absent and the version string is present (`ComparisonDatabase.cs:291-297`).
7. If `ReplacementSourcePackage` is absent, the record is treated as already self-contained: the loader assigns a new key, stages the JSON record as-is, and does not resolve package contexts or expand it across database packages (`ComparisonDatabase.cs:299-305`).
8. If `ReplacementSourcePackage` is present, the loader resolves or reuses a `DefinitionCollection` for that package. Directives containing `#` or `@` are used as-is; otherwise the loader appends `@latest`. It loads without auto-loading expansions and without resolving dependencies, and throws if the package cannot be loaded (`ComparisonDatabase.cs:307-332`).
9. The loader resolves `ReplacementUrl` in that package and requires the resolved resource to be a `StructureDefinition`; unresolved canonicals or non-StructureDefinition resources throw (`ComparisonDatabase.cs:335-347`).
10. It chooses applicable database packages. If the JSON record specifies a source sequence, only packages with matching `DefinitionFhirSequence` apply; otherwise every selected package applies (`ComparisonDatabase.cs:348-350`).
11. For each applicable package, it filters the replacement extension's contexts by the `version-specific-use` extension. Missing start defaults to DSTU2, missing end defaults to R6, and contexts outside the package's FHIR sequence are skipped (`ComparisonDatabase.cs:352-382`). Packages with no applicable contexts produce no rows for that JSON record/package combination (`ComparisonDatabase.cs:384-388`).
12. If the JSON record already names `SourceElementId` or `SourceTypeReplacement`, the loader creates one row for that applicable package, copying replacement metadata, source identifiers, modifier status, and the applicable context expressions (`ComparisonDatabase.cs:390-410`).
13. If the record has neither `SourceElementId` nor `SourceTypeReplacement`, `SourceFromContextElement` becomes required; absence of all three source selectors throws an invalid-request exception (`ComparisonDatabase.cs:413-417`).
14. For context-derived records, the loader expands each applicable context expression by appending `SourceFromContextElement`, stores those expanded element ids in `SourceFromContextExpanded`, stores the raw context expressions in `Contexts`, stages the row, and continues (`ComparisonDatabase.cs:419-441`).
15. After all JSON records are processed, the loader inserts staged rows into `ExtensionSubstitutions` and returns `true` (`ComparisonDatabase.cs:445-449`).
16. Downstream outcome generation reads version-matched substitution rows plus sequence-null rows, builds lookup dictionaries by source element id, `[x]`-stripped source element id, expanded context id, `[x]`-stripped expanded context id, and type replacement URL, then stores the selected substitution key and URL on each element outcome (`ElementOutcomeGenerator.cs:262-301`, `ElementOutcomeGenerator.cs:1432-1455`, `ElementOutcomeGenerator.cs:1884-1895`).

## Decision Points

- **Rule:** The source layout is a single mandatory file at `<CrossVersionMapSourcePath>\input\ig-support\extensionSubstitutions.json`; there is no directory scan and no cross-file merge order. **Source:** `ComparisonDatabase.cs:246-254`. **Rationale:** AI Guess: keeping the substitutions in `input\ig-support` makes them part of the cross-version IG support content rather than generated database output, while using one file avoids file-order ambiguity.
- **Rule:** Missing source file is a hard error, not a `false` return. Because the file existence check is outside the parse `try` block, `XVerProcessor.LoadExtensionSubstitutions` does not wrap this case in its `Failed to load extension substitutions...` message. **Source:** `ComparisonDatabase.cs:250-254`, `XVerProcessor.cs:760-763`. **Rationale:** The code distinguishes missing required source content from parse/load failures by throwing before the `try`/`catch` that returns `false`.
- **Rule:** Empty `CrossVersionMapSourcePath` is not skipped. It is passed directly to `Path.Combine`, making the effective filename `input\ig-support\extensionSubstitutions.json` relative to the current process directory; if that file is absent, the loader throws. **Source:** `XVerProcessor.cs:760`, `ComparisonDatabase.cs:246-254`. **Rationale:** This is intentionally stricter than `LoadFhirCrossVersionMaps`, which checks `!string.IsNullOrEmpty(_config.CrossVersionMapSourcePath)` before loading source maps and discards the loader return value (`XVerProcessor.cs:801-807`).
- **Rule:** A non-empty substitution source destructively replaces the `ExtensionSubstitutions` table on each load. **Source:** `ComparisonDatabase.cs:278-285`, `ComparisonDatabase.cs:445-449`. **Rationale:** AI Guess: this table is treated as a projection of the authoritative JSON/package inputs, so retaining old rows across loads would risk stale manual decisions.
- **Rule:** Records without `ReplacementSourcePackage` are inserted directly after key assignment; records with `ReplacementSourcePackage` are package-resolved, context-filtered, and potentially expanded across one or more loaded FHIR packages. **Source:** direct insert path at `ComparisonDatabase.cs:299-305`; package resolution and expansion at `ComparisonDatabase.cs:307-441`. **Rationale:** The method comments describe this as loading definitions needed to expand content and then iterating over FHIR packages to resolve contexts and source applicability (`ComparisonDatabase.cs:291-352`).
- **Rule:** Version applicability can come from the JSON record (`SourceFhirSequence` or `SourceVersion`) and from version-specific-use extensions on the replacement StructureDefinition's contexts. **Source:** `ComparisonDatabase.cs:291-297`, `ComparisonDatabase.cs:348-382`. **Rationale:** The source code converts `SourceVersion` into a sequence and filters contexts using `version-specific-use` start/end bounds before adding package-specific rows.
- **Rule:** A source selector is required after package context resolution: `SourceElementId` or `SourceTypeReplacement` creates a direct row; otherwise `SourceFromContextElement` must be present so context expressions can be expanded. **Source:** `ComparisonDatabase.cs:390-417`, `ComparisonDatabase.cs:419-441`. **Rationale:** The thrown message names records missing both context replacement and element/type replacement as invalid substitution requests.
- **Rule:** Duplicate logical substitutions are not resolved by this loader. It generates new primary keys, stages rows keyed by `Key`, and inserts with duplicate ignores that only matter for insert-level constraints; there is no uniqueness rule over `(SourceFhirSequence, SourceElementId, SourceFromContextExpandedLiteral, SourceTypeReplacement, ReplacementUrl)`. Downstream dictionaries assign by lookup key, so later assignments can overwrite earlier dictionary values for the same source element/context/type key. **Source:** `DbRecordCache.CacheAdd` keys only by `Key` (`DbBaseClasses.cs:68-72`), loader rows receive new keys (`ComparisonDatabase.cs:302`, `ComparisonDatabase.cs:396`, `ComparisonDatabase.cs:427`), downstream assignments overwrite dictionary entries (`ElementOutcomeGenerator.cs:271-301`). **Rationale:** Unresolved: the source does not document intended duplicate handling or the stable ordering guarantees, if any, of `DbExtensionSubstitution.SelectList` without `orderByProperties`.
- **Rule:** Substitution rows suppress generated extension definitions for matched element outcomes and carry the explicit replacement URL forward. Outcome generation sets `extSubstitute` from element/context/type lookups, writes `ExtensionSubstitutionKey` and `ExtensionSubstitutionUrl`, and excludes substituted rows from the generated-extension condition because `requiresExtensionDefinition` requires `extSubstitute is null`. **Source:** lookup and notes at `ElementOutcomeGenerator.cs:262-301` and `ElementOutcomeGenerator.cs:1432-1455`; generated-extension condition at `ElementOutcomeGenerator.cs:1616-1621`; outcome fields at `ElementOutcomeGenerator.cs:1884-1895`; exporter use at `StructureFhirExporter.cs:1624-1745`. **Rationale:** AI Guess: substitutions encode SME judgment about extension naming and semantics that the algorithmic mapper cannot discover, so the explicit URL should win over generated cross-version extension artifacts.

## Rationale Coverage

`Decisions: 9 total — cited: 5 — AI Guess: 3 — unresolved: 1`

Cited decisions: missing-file hard error; empty path strictness versus `LoadFhirCrossVersionMaps`; package/context expansion behavior; version applicability; source-selector validation. AI Guess decisions: single-file `input\ig-support` source layout rationale; destructive table replacement rationale; substitution precedence rationale. Unresolved decision: duplicate logical substitution conflict handling/order.

## Failure Modes & Edge Cases

- `_db` remains null after `LoadDatabase(false)`: `LoadExtensionSubstitutions` throws `Failed to create or load a comparison database!` (`XVerProcessor.cs:751-758`).
- `TryLoadExtensionSubstitutions` returns `false`: `LoadExtensionSubstitutions` throws `Failed to load extension substitutions from source path: {_config.CrossVersionMapSourcePath}` (`XVerProcessor.cs:760-763`). In the current loader, this return path is used for JSON open/deserialize exceptions caught inside the parse block (`ComparisonDatabase.cs:258-276`).
- Empty or missing `CrossVersionMapSourcePath`: no short-circuit. Empty becomes the relative path `input\ig-support\extensionSubstitutions.json`; missing directories/files throw `Could not find extension substitution source file at {filename}!` before the catch block (`ComparisonDatabase.cs:250-254`).
- Malformed substitution JSON: exceptions thrown while opening or deserializing the JSON file are logged as errors and returned as `false`, which the processor converts into its source-path failure exception (`ComparisonDatabase.cs:258-276`, `XVerProcessor.cs:760-763`).
- Empty JSON source: if deserialization yields null or an empty list, the loader logs a warning and returns `true` without dropping/recreating `ExtensionSubstitutions`, because table recreation happens after that early return (`ComparisonDatabase.cs:263-281`).
- Path exists but contains no substitution file: same as missing file, a direct throw from the file existence check (`ComparisonDatabase.cs:250-254`).
- `ReplacementSourcePackage` cannot be loaded: package loading throws `Could not load package: {directive}` (`ComparisonDatabase.cs:321-332`).
- `ReplacementUrl` cannot be resolved in the replacement package or resolves to a non-StructureDefinition resource: the loader throws before writing staged rows (`ComparisonDatabase.cs:335-344`).
- A package-resolved record has no `SourceElementId`, no `SourceTypeReplacement`, and no `SourceFromContextElement`: the loader throws `Substitution record with key {jsonRec.Key} is missing both a context replacement and an element/type replacement!` (`ComparisonDatabase.cs:390-417`).
- A replacement StructureDefinition has contexts, but none apply to a given database package after version filtering: that package contributes no substitution row for the JSON record (`ComparisonDatabase.cs:352-388`).

## Coverage Checklist

- [x] `XVerProcessor.LoadExtensionSubstitutions` (`XVerProcessor.cs:749-764`)
- [x] `XVerProcessor.ProcessCommand` invocation paths (`XVerProcessor.cs:314-424`)
- [x] `ComparisonDatabase.TryLoadExtensionSubstitutions` (`ComparisonDatabase.cs:246-449`)
- [x] `DbExtensionSubstitution` row type (`DbContentClasses.cs:119-180`) and inherited key/cache behavior (`DbBaseClasses.cs:11-15`, `DbBaseClasses.cs:68-72`)
- [x] Downstream `DbExtensionSubstitution` consumers in outcome/export generation (`ElementOutcomeGenerator.cs:262-301`, `ElementOutcomeGenerator.cs:1432-1455`, `ElementOutcomeGenerator.cs:1616-1621`, `ElementOutcomeGenerator.cs:1884-1895`, `StructureFhirExporter.cs:1624-1745`)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:314-424`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:749-764`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:790-809`
  - `src/Fhir.CodeGen.Comparison/Models/ComparisonDatabase.cs:246-449`
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs:119-180`
  - `src/Fhir.CodeGen.Comparison/Models/DbBaseClasses.cs:11-15`
  - `src/Fhir.CodeGen.Comparison/Models/DbBaseClasses.cs:68-72`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ElementOutcomeGenerator.cs:262-301`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ElementOutcomeGenerator.cs:1432-1455`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ElementOutcomeGenerator.cs:1616-1621`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ElementOutcomeGenerator.cs:1884-1895`
  - `src/Fhir.CodeGen.Comparison/Exporter/StructureFhirExporter.cs:1624-1745`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs:39-57`
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs:30-32`
- Related specs:
  - [`xver-load-database.md`](./xver-load-database.md) — prerequisite
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md) — sibling load step
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md) — sibling load step
  - [`xver-generate-outcomes.md`](./xver-generate-outcomes.md) — primary downstream consumer
- Related `ConfigXVer` options: `CrossVersionMapSourcePath`, `ReloadDatabase`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
