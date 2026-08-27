# XVerProcessor.LoadDatabase — Step 1 of 7

## Purpose

`LoadDatabase` either reuses a named SQLite comparison database or creates a fresh comparison database and loads all configured FHIR core package definitions into it. The fresh-load path seeds content tables such as `DbFhirPackage`, `DbCodeSystem`, `DbValueSet`, `DbStructureDefinition`, `DbElement`, and `DbElementType` from the configured `DefinitionCollection` instances. This is the foundational step for the remaining cross-version pipeline: maps, substitutions, FHIR-type value sets, comparison, outcome generation, and export all require `_db`.

## Invocation & Preconditions

Direct command dispatch in `ProcessCommand` invokes this step before later pipeline work:

- `load` calls `LoadDatabase(true)`, then loads cross-version maps, extension substitutions, and FHIR-type value sets (`XVerProcessor.cs:314-319`).
- `load-base` calls `LoadDatabase(true)`, then loads extension substitutions (`XVerProcessor.cs:321-324`).
- `compare`, `compare-vs`, `compare-sd`, `outcomes`, `outcomes-vs`, `outcomes-sd`, and `export` call `LoadDatabase(_config.ReloadDatabase)` when `_config.ReloadDatabase` is true before running their requested step (`XVerProcessor.cs:336-418`).
- The default full-pipeline branch always calls `LoadDatabase(_config.ReloadDatabase)` before maps, substitutions, FHIR-type value sets, compare, outcomes, and export (`XVerProcessor.cs:421-428`).

Several public step methods also lazily call `LoadDatabase(false)` if `_db` is null:

- `ExportOutcomes` (`XVerProcessor.cs:557-566`)
- `GenerateOutcomes` (`XVerProcessor.cs:614-622`)
- `CompareInDatabase` (`XVerProcessor.cs:668-676`)
- `LoadExtensionSubstitutions` (`XVerProcessor.cs:749-757`)
- `LoadFhirTypeValueSets` (`XVerProcessor.cs:766-774`)
- `LoadFhirCrossVersionMaps` (`XVerProcessor.cs:790-798`)

Preconditions:

- A valid `ConfigXVer` instance is passed to the constructor (`XVerProcessor.cs:161-165`).
- `ComparePackages` identifies the FHIR core package directives to load (`ConfigXVer.cs:16-21`); an empty list produces an empty `_definitions` array and the fresh database constructor rejects it (`ComparisonDatabase.cs:80-89`).
- Either `CrossVersionDbPath` or `CrossVersionMapSourcePath` is configured. The constructor derives `_dbPath` from `CrossVersionDbPath`, or falls back to `<CrossVersionMapSourcePath>\db` (`XVerProcessor.cs:167-180`).
- `LogFactory` is available from `ConfigRoot` and is used by the processor and database constructors (`ConfigRoot.cs:30-32`, `XVerProcessor.cs:163-165`, `ComparisonDatabase.cs:80-94`).

## Inputs

- Configuration (`ConfigXVer` keys): `ComparePackages`, `CrossVersionDbPath`, `CrossVersionMapSourcePath`, `ReloadDatabase`, `LogFactory`.
- On-disk sources:
  - `<dbPath>\<dbName>.sqlite` or `<dbPath>\<dbName>.db` when a named path was supplied and `forceCreate=false`; this path is opened by the existing-database constructor (`XVerProcessor.cs:510-519`, `ComparisonDatabase.cs:161-190`).
  - FHIR package payloads loaded by `loadDefinitionCollections` through the standard `PackageLoader` (`XVerProcessor.cs:224-239`).
- Prior in-memory / database state:
  - `_definitions`, lazily populated by `loadDefinitionCollections` when empty (`XVerProcessor.cs:521-525`).
  - `_exclusionSet`, the static URL set skipped during content import (`XVerProcessor.cs:113-128`).
  - `_escapeValveCodes`, the static code set passed into value-set loading and post-processing (`XVerProcessor.cs:130-143`, `ComparisonDatabase.cs:652-686`).
  - `_dbPath` and `_dbName`, derived in the constructor (`XVerProcessor.cs:167-180`).
- Method signature:

  ```csharp
  public void LoadDatabase(bool forceCreate, FhirArtifactClassEnum? artifactFilter = null)
  ```

  (`XVerProcessor.cs:505-507`)

## Outputs

- `_db` is set to a `ComparisonDatabase` instance backed by an on-disk SQLite file (`XVerProcessor.cs:514`, `XVerProcessor.cs:528`).
- On existing-DB reuse, the method opens the database, loads table indices through `DbContentClasses.LoadIndices`, and returns without re-seeding content (`XVerProcessor.cs:510-519`, `ComparisonDatabase.cs:161-190`).
- On fresh DB creation, the definitions-based constructor opens/creates the SQLite file, deletes existing content tables, recreates content/support tables, and inserts package metadata (`ComparisonDatabase.cs:80-158`, `ComparisonDatabase.cs:593-647`). The content/support schema includes `DbFhirPackage`, `DbCodeSystem`, `DbCodeSystemConcept`, `DbValueSet`, `DbValueSetConcept`, `DbStructureDefinition`, `DbElement`, `DbElementAdditionalBinding`, `DbElementType`, `DbExternalInclusion`, `DbExtensionSubstitution`, and `DbFhirTypeValueSet` (`DbContentClasses.cs:31-109`).
- `_dbName` is updated from the database instance after fresh construction (`XVerProcessor.cs:528-529`).
- Fresh creation is seeded by `_db.TryLoadFromDefinitionCollections(_exclusionSet, _escapeValveCodes)` (`XVerProcessor.cs:531-535`). That method loads code systems, value sets, and structures, then runs value-set, element, and code-system post-processing (`ComparisonDatabase.cs:652-696`).
- Comparison pair / outcome / mapping tables are not created by `LoadDatabase` itself; they are created by later comparison, outcome, and map-loading steps. This is a source-level surprise because the method comment only says it "loads or creates the comparison database" (`XVerProcessor.cs:499-503`), while `initNewDb` delegates only to `DbContentClasses.CreateTables` (`ComparisonDatabase.cs:593-607`).

## Algorithm

1. If `forceCreate` is false and `_dbName` is not empty, open the named database with `_db = new(_dbPath, _dbName)` and return (`XVerProcessor.cs:509-519`). The `_db != null` check immediately after construction is redundant in C# because `new` cannot return null; the only non-return path is an exception during construction.
2. If `_definitions.Length == 0`, call `loadDefinitionCollections()` (`XVerProcessor.cs:521-525`). That helper iterates `ComparePackages`, validates each directive, creates a `PackageLoader` per directive, expands R5 loads to include `hl7.terminology@5.1.0`, calls `LoadPackages(loadDirectives).Result`, and stores the resulting `DefinitionCollection` array and lookup indexes (`XVerProcessor.cs:213-250`). See the package-filter decision below for the apparent `PackageIsFhirCore` anomaly.
3. Construct the database with definitions: `_db = new(_definitions, _dbPath, _dbName, _config.LogFactory)` (`XVerProcessor.cs:527-528`). The constructor opens/creates the SQLite file in read/write/create mode and calls `initNewDb(ensureDeleted: true)` (`ComparisonDatabase.cs:147-158`).
4. Store `_dbName = _db.DbFileName` so later lazy calls can reuse the generated or supplied filename (`XVerProcessor.cs:528-529`).
5. Call `_db.TryLoadFromDefinitionCollections(_exclusionSet, _escapeValveCodes)` (`XVerProcessor.cs:531-532`). If it returns false, throw `Failed to load FHIR-based definitions into the database: ...` with the loaded package keys (`XVerProcessor.cs:532-535`).

## Decision Points

- **Existing-DB reuse.**  
  **Rule:** When `forceCreate=false` and `_dbName` is set, `LoadDatabase` opens the named SQLite database and returns without loading definitions or re-seeding content.  
  **Source:** `XVerProcessor.cs:510-519`; the existing-database constructor opens the file with `SqliteOpenMode.ReadWriteCreate` and loads indices at `ComparisonDatabase.cs:177-190`.  
  **Rationale:** AI Guess: the cross-version pipeline can be expensive; reusing a previously loaded database keeps inner-loop operations from re-downloading packages and re-importing all content.

- **Fresh creation is destructive for content tables.**  
  **Rule:** The definitions-based constructor always calls `initNewDb(ensureDeleted: true)`, and `initNewDb` drops then recreates content/support tables before inserting package metadata.  
  **Source:** `ComparisonDatabase.cs:147-158`, `ComparisonDatabase.cs:593-647`, `DbContentClasses.cs:31-109`.  
  **Rationale:** The code path explicitly requests `ensureDeleted: true` and recreates a clean content schema before loading definitions, so a forced/fresh load is designed to replace the prior content snapshot rather than merge into it.

- **`_exclusionSet` URLs skipped at load time.**  
  **Rule:** `ucum-units`, `all-languages`, `mimetypes`, `timezones`, plus the DSTU2 BCP47 / BCP13 URLs for the latter two categories, are excluded from import.  
  **Source:** `XVerProcessor.cs:113-128` defines the set and comments it as "ValueSet and CodeSystem URLs to exclude from processing"; `XVerProcessor.cs:532` passes it into `_db.TryLoadFromDefinitionCollections`; `ComparisonDatabase.cs:670-677` passes it to code-system, value-set, and structure import helpers.  
  **Rationale:** AI Guess: these are externally maintained or broad infrastructure vocabularies whose codes change too frequently, or whose content is too large/ambient, to serve as useful cross-version anchors.

- **`_escapeValveCodes`.**  
  **Rule:** The codes `OTHER`, `Other`, `other`, `OTH`, `UNKNOWN`, `Unknown`, `unknown`, and `UNK` get downstream "escape valve" handling during value-set import and post-processing.  
  **Source:** `XVerProcessor.cs:130-143` defines the set and labels `OTH` / `UNK` as v3 Null Flavor variants; `XVerProcessor.cs:532` passes it into `_db.TryLoadFromDefinitionCollections`; `ComparisonDatabase.cs:673-686` uses it while adding value sets and during value-set post-processing.  
  **Rationale:** Source comments identify the set as codes considered "escape valve" codes and tie `OTH` / `UNK` to v3 Null Flavor "other" / "Unknown"; AI Guess: the case variants exist because source vocabularies are inconsistent about code casing.

- **`loadDefinitionCollections` package filter — anomaly.**  
  **Rule:** The check is `if (FhirPackageUtils.PackageIsFhirCore(directive)) throw new Exception($"Package {directive} is not a FHIR Core package!");`. The condition appears to be inverted with respect to the exception message because the throw fires when the package is core.  
  **Source:** `XVerProcessor.cs:217-222`.  
  **Rationale:** AI Guess: this looks like a bug — likely the missing `!` in the condition. Either the `if` is meant to be `if (!FhirPackageUtils.PackageIsFhirCore(directive))` or the exception message is reversed. TODO: reviewer please confirm and file a bug if real.

- **R5 → adds `hl7.terminology@5.1.0`.**  
  **Rule:** When loading an R5 directive, the loader also adds `hl7.terminology@5.1.0` to the package load list.  
  **Source:** `XVerProcessor.cs:230-236`.  
  **Rationale:** AI Guess: R5 splits a substantial amount of terminology content out of the core package into `hl7.terminology`; pinning to `5.1.0` matches the version contemporaneous with the R5 launch and avoids tracking moving terminology updates.

- **`_dbName` derivation in the constructor.**  
  **Rule:** If `CrossVersionDbPath` ends in `.sqlite` or `.db`, the file name becomes `_dbName` and its directory becomes `_dbPath`; otherwise the path is treated as a directory and `_dbName` is left null so `LoadDatabase` generates a name on fresh construction. If `CrossVersionDbPath` is empty, `_dbPath` falls back to `<CrossVersionMapSourcePath>\db`.  
  **Source:** `XVerProcessor.cs:167-180`.  
  **Rationale:** The constructor branches directly on the path extension and assigns `_dbPath` / `_dbName` from the resulting directory/file split or directory-only path.

- **`artifactFilter` is accepted but unused.**  
  **Rule:** `LoadDatabase` exposes `FhirArtifactClassEnum? artifactFilter = null`, but the method body never reads it; fresh loads import all package content classes.  
  **Source:** Signature at `XVerProcessor.cs:505-507`; body at `XVerProcessor.cs:509-538`; full-content import at `ComparisonDatabase.cs:670-677`.  
  **Rationale:** AI Guess: this is a leftover or future extension point from an earlier design where database loading might have supported partial artifact loading.

## Rationale Coverage

Decisions: 8 total — cited: 2 — AI Guess: 5 — unresolved: 1

## Failure Modes & Edge Cases

- Fresh database construction throws `ArgumentOutOfRangeException(nameof(definitions))` when `_definitions` is empty (`ComparisonDatabase.cs:80-89`). This can happen if `ComparePackages` is empty and `loadDefinitionCollections` leaves `_definitions` empty (`XVerProcessor.cs:215-245`).
- DB creation throws when `_db.TryLoadFromDefinitionCollections` returns false (`XVerProcessor.cs:532-535`). The method itself returns false when no `DefinitionCollection` is attached to the database instance (`ComparisonDatabase.cs:652-660`).
- `loadDefinitionCollections` currently throws when `PackageIsFhirCore(directive)` returns true (`XVerProcessor.cs:217-222`). Per the suspected bug above, this practically means fresh loading fails for FHIR core package directives unless the predicate semantics differ from the method name.
- `PackageLoader.LoadPackages(loadDirectives).Result` can throw if a package cannot be downloaded or resolved. A null result is converted to `Could not load package: {directive}` (`XVerProcessor.cs:224-239`).
- When `forceCreate=false` and `_dbName` is empty, the method silently falls through to the fresh-DB branch (`XVerProcessor.cs:510-528`).
- When `forceCreate=false` and `_dbName` is set but the file does not exist, the existing-database constructor uses `SqliteOpenMode.ReadWriteCreate`, so it can create/open an empty SQLite file and return without content seeding (`ComparisonDatabase.cs:177-190`). Downstream steps may then fail because expected content tables or rows are missing.
- `artifactFilter` does not limit what is loaded; callers expecting a value-set-only or structure-only load still get the full fresh-load path (`XVerProcessor.cs:505-538`, `ComparisonDatabase.cs:670-677`).

## Coverage Checklist

- [x] `XVerProcessor.LoadDatabase` (`XVerProcessor.cs:505-538`)
- [x] `XVerProcessor.loadDefinitionCollections` (`XVerProcessor.cs:213-250`)
- [x] `XVerProcessor` constructor `_dbPath`/`_dbName` derivation (`XVerProcessor.cs:161-208`)
- [x] `_exclusionSet` constant (`XVerProcessor.cs:113-128`)
- [x] `_escapeValveCodes` constant (`XVerProcessor.cs:130-143`)
- [x] R5 terminology pin (`XVerProcessor.cs:230-236`)
- [x] Implicit `LoadDatabase(false)` callers in other public step methods
- [x] `ComparisonDatabase` constructors and `TryLoadFromDefinitionCollections` signature (`ComparisonDatabase.cs:80-190`, `ComparisonDatabase.cs:652-654`)
- [x] Content table creation/drop helper (`DbContentClasses.cs:31-109`)

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs` (lines 104-143, 161-250, 256-431, 505-538, 557-566, 614-622, 668-676, 749-798)
  - `src/Fhir.CodeGen.Comparison/Models/ComparisonDatabase.cs` (lines 80-190, 593-696; `TryLoadFromDefinitionCollections` signature at lines 652-654)
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs` (lines 31-109)
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs` (lines 16-92, 459-472)
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs` (lines 30-32)
- Related specs:
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md)
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md)
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md)
  - [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) — downstream consumer of the loaded DB
- Related `ConfigXVer` options: `ComparePackages`, `CrossVersionDbPath`, `CrossVersionMapSourcePath`, `ReloadDatabase`, `LogFactory`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
