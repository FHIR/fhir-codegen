# XVerProcessor.LoadDatabase — Step 1 of 7

## Purpose

`LoadDatabase` either reuses a named SQLite comparison database or creates a fresh comparison database and loads all configured FHIR core package definitions into it. The fresh-load path seeds content tables such as `DbFhirPackage`, `DbCodeSystem`, `DbValueSet`, `DbStructureDefinition`, `DbElement`, and `DbElementType` from the configured `DefinitionCollection` instances. This is the foundational step for the remaining cross-version pipeline: maps, substitutions, FHIR-type value sets, comparison, outcome generation, and export all require `_db`.

**Current behavior (2026-11-17):** Fresh import persists the full retained normalized profile catalog, with a separate processing boundary described below. The default full pipeline initializes a missing named database. Reuse-only commands instead reject missing named files, and existing files without the package table are rejected without automatic rebuilding. Opening a named database does not reload it or certify its catalog. Unchanged line references below retain the historical source snapshot cited at the end of this document.

## Invocation & Preconditions

Direct command dispatch in `ProcessCommand` invokes this step before later pipeline work:

- `load` calls `LoadDatabase(true)`, then loads cross-version maps, extension substitutions, and FHIR-type value sets (`XVerProcessor.cs:314-319`).
- `load-base` calls `LoadDatabase(true)`, then loads extension substitutions (`XVerProcessor.cs:321-324`).
- `compare`, `compare-vs`, `compare-sd`, `outcomes`, `outcomes-vs`, `outcomes-sd`, and `export` call `LoadDatabase(_config.ReloadDatabase)` when `_config.ReloadDatabase` is true before running their requested step (`XVerProcessor.cs:336-418`).
- The default full-pipeline branch calls `LoadDatabase` with forced creation when `_config.ReloadDatabase` is true or the configured named database file is missing, before maps, substitutions, FHIR-type value sets, compare, outcomes, and export (historical branch: `XVerProcessor.cs:421-428`).

Several public step methods also lazily call `LoadDatabase(false)` if `_db` is null:

- `ExportOutcomes` (`XVerProcessor.cs:557-566`)
- `GenerateOutcomes` (`XVerProcessor.cs:614-622`)
- `CompareInDatabase` (`XVerProcessor.cs:668-676`)
- `LoadExtensionSubstitutions` (`XVerProcessor.cs:749-757`)
- `LoadFhirTypeValueSets` (`XVerProcessor.cs:766-774`)
- `LoadFhirCrossVersionMaps` (`XVerProcessor.cs:790-798`)

These lazy calls do not create a missing named database: they require an existing comparison database unless the command explicitly requests reload. Directory-only paths still use the existing generated-name fresh-load behavior.

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
  - `_exclusionSet`, the static processing/vocabulary exclusion set; it does not remove retained profiles or their available projections from the catalog (`XVerProcessor._exclusionSet`, `ComparisonDatabase.addStructuresToDb`).
  - `_escapeValveCodes`, the static code set passed into value-set loading and post-processing (`XVerProcessor.cs:130-143`, `ComparisonDatabase.cs:652-686`).
  - `_dbPath` and `_dbName`, derived in the constructor (`XVerProcessor.cs:167-180`).
- Method signature:

  ```csharp
  public void LoadDatabase(bool forceCreate, FhirArtifactClassEnum? artifactFilter = null)
  ```

  (`XVerProcessor.cs:505-507`)

## Outputs

- `_db` is set to a `ComparisonDatabase` instance backed by an on-disk SQLite file (`XVerProcessor.cs:514`, `XVerProcessor.cs:528`).
- On existing-DB reuse, the method opens the existing file in read/write mode, verifies the `DbFhirPackage.DefaultTableName` table in `sqlite_master`, loads table indices through `DbContentClasses.LoadIndices`, and returns without re-seeding content (historical locations: `XVerProcessor.cs:510-519`, `ComparisonDatabase.cs:161-190`). This is a package-table guard, not a complete schema/version migration check.
- On fresh DB creation, the definitions-based constructor opens/creates the SQLite file, deletes existing content tables, recreates content/support tables, and inserts package metadata (`ComparisonDatabase.cs:80-158`, `ComparisonDatabase.cs:593-647`). The content/support schema includes `DbFhirPackage`, `DbCodeSystem`, `DbCodeSystemConcept`, `DbValueSet`, `DbValueSetConcept`, `DbStructureDefinition`, `DbElement`, `DbElementAdditionalBinding`, `DbElementType`, `DbExternalInclusion`, `DbExtensionSubstitution`, and `DbFhirTypeValueSet` (`DbContentClasses.cs:31-109`).
- `_dbName` is updated from the database instance after fresh construction (`XVerProcessor.cs:528-529`).
- Fresh creation is seeded by `_db.TryLoadFromDefinitionCollections(_exclusionSet, _escapeValveCodes)` (`XVerProcessor.cs:531-535`). That method loads code systems, value sets, and structures, then runs value-set, element, and code-system post-processing (`ComparisonDatabase.cs:652-696`).
- Comparison pair / outcome / mapping tables are not created by `LoadDatabase` itself; they are created by later comparison, outcome, and map-loading steps. This is a source-level surprise because the method comment only says it "loads or creates the comparison database" (`XVerProcessor.cs:499-503`), while `initNewDb` delegates only to `DbContentClasses.CreateTables` (`ComparisonDatabase.cs:593-607`).

### Normalized inventory and processing boundary

The public `ComparisonDatabase.TryLoadFromDefinitionCollections` imports
`GetStructureInventories`: existing primitives, complex types and resources,
plus **every retained `ProfilesByUrl` definition**, including processing-excluded
profiles and their available elements/types/additional bindings. ArtifactClass
follows normalized collection membership. DSTU2 Quantity derivatives routed by
`DefinitionCollection.AddComplexType` to `AddProfile` are therefore Profiles;
they are not assumed to be native complex types.

| Definition inventory | Standalone processing |
|-|-|
| Existing primitives, complex types, resources | Existing behavior/exclusions; primitive outcomes remain suppressed. |
| Profile with ordinal declared `Type == "Quantity"` | Eligible only with a unique, acyclic, same-package base chain reaching the actual native complex-type Quantity; existing exclusions still apply. |
| Other profiles, including profiles on Age, Coding or resources | Passive definitions; constraints remain authoritative at legitimate uses, not standalone maps/comparisons/outcomes/artifacts. |
| Separately classified extensions/logical models | No additional import. |

This is not archival of original package bytes or discarded duplicate/history
entries, nor an additional import of separately classified extensions/logical models.
Each imported definition, including native
owners, has an internal `StructureDefinitionFacts` row containing Type, Kind,
Derivation, BaseDefinition, snapshot availability and Firely-serialized normalized
FHIR JSON. The JSON retains snapshot/differential restrictions omitted from the
relational traversal projection; it is not naive POCO JSON or byte-lossless storage.

`GetAvailableProjection` uses a nonempty snapshot, otherwise the available
differential. Missing/empty snapshots remain distinguished in facts. Differential
rows are not effective snapshots: required operational/use-site facts without a
usable snapshot fail contextually. No snapshot, missing declared Type, root or
alias is invented. A root type projection may use the definition's **declared**
Type only for a uniquely identified available snapshot root.

`XVerStructureCatalog` identifies a definition by package key + exact URL +
definition version, and an element by package + structure key + exact ElementId.
Canonical `|version` means definition version, never package release; unversioned
references must resolve uniquely and stored foreign keys must agree. Native-name
lookup excludes profiles. A missing declared profile cannot fall back to its base
`TypeStructureKey`. Needed roots have one owner-scoped field-order-zero element,
no parent and a valid top-level Path. Exact sliced IDs survive, notably R3
`Quantity:simplequantity` / Path `Quantity`, with children and parents in that
same owner graph.

### Inventory completion, reuse and costs

`DefinitionInventoryStore` uses **FormatVersion 1 / PolicyVersion 2**.
`BeginInventory` records independent expected counts from the normalized source
collections per package (classes, elements, types and additional bindings), not
queried row counts presented as independent evidence. Import invalidates completion
before replacement. After all packages, owner/key resolution and existing
post-processing, `CompleteInventory` validates counts, payload/projection/owner
consistency and stored links, then publishes `CompletedRevision` last through the
generated update helper. There is no unsupported outer transaction around helpers.

`RequireReady` is read-only and does not create missing schema. Maps must bind their
completed input snapshot to this exact inventory revision. Preparatory native-only
PolicyVersion 1, missing, old or incomplete catalogs are rejected before destructive
map/comparison/outcome consumer resets, even if ordinary package tables exist.
Opening the file or reloading only maps cannot repair such a catalog. Recovery is
an explicitly authorized destructive content reload, then maps → comparisons →
outcomes → export. Content reset invalidates inventory and downstream stamps;
map reset clears map-owner facts/input bindings, not the catalog itself.

Full relational profile projections and normalized payloads for all definitions
intentionally increase storage, serialization/import work, indexes and eager
relational memory. Pair-local lazy payload/fact caches are not a bounded-eager-memory
guarantee. Release tests report observed profile/element/type/facts counts, isolated
database size, import elapsed time and per-pair time/process memory; these are
observations, not numerical acceptance thresholds or a comparable native-only
timing baseline.

One cached five-core acceptance run observed:

| Core | Profiles | Elements | Type uses | Definition facts |
|-|-:|-:|-:|-:|
| R2 | 113 | 9,901 | 11,996 | 249 |
| R3 | 33 | 7,780 | 9,422 | 205 |
| R4 | 48 | 10,658 | 13,401 | 257 |
| R4B | 43 | 10,476 | 13,302 | 249 |
| R5 | 66 | 15,431 | 19,483 | 295 |

`AllConfiguredPairsRespectQuantityProfileProcessingBoundary` recorded 33,340 ms
for public import and a 291,733,504-byte isolated SQLite file. Its per-pair
membership/canonical/FK checks took 13–47 ms, with process-private memory around
9.31–10.05 GB including prior test allocations. These are not whole-comparer
throughput or incremental per-pair memory measurements. The 952 native definitions
retain their normalized membership; 303 retained profiles are additional owners.
There is no comparable earlier database-size/import-time baseline.

R5 terminology pruning uses bindings from operational owners and their legitimate
ancestor/base/content-reference/profile dependency closure
(`GetTerminologyOwnerClosure`). Unrelated passive binding additions do not retain
previously pruned terminology. Necessary terminology for an eligible/referenced use
may intentionally survive; this does not change vocabulary selection policy.

### DSTU2 constraint XPath retention

The DSTU2 loader retains `ElementDefinition.constraint.xpath` on the normalized
`ElementDefinition.ConstraintComponent` as a simple extension with URL
`http://hl7.org/fhir/1.0/StructureDefinition/extension-ElementDefinition.constraint.xpath`
and `valueString` containing the original XPath. This applies to both snapshot
and differential constraints. The loader does not translate XPath into FHIRPath
or populate `constraint.expression` from it. This is runtime retention only;
there is no DSTU2 exporter or extension StructureDefinition generated by this change.

The internal occurrence-proof reader uses the same shared `1.0` URL, not a
basename match or a `4.0` alias. Missing, wrongly typed, duplicated, or unknown
constraint extensions cannot establish a representation proof. The persisted
FHIR JSON and effective facts retain the original invariant key, severity,
human text, source, XPath, and owner/version attribution.

Retention does not evaluate XPath. Bounded proof-layer transformations require
known corresponding datatype contexts; for example, the XML element-existence
rule is considered only at a proven Quantity-family complex-type root with the
actual closed child vocabulary and XML-attribute `id`, not arbitrary elements.
Required bindings and their qualified domains remain obligations: a native
target or an owner projection cannot widen a target required binding. Retention
and downstream bounded proof are separate responsibilities.

### Occurrence decisions

The pair-local interpreter returns typed native reuse, legal emitted-owner
projection, or explicit unrepresentable decisions. Unrepresentable means that
this implementation has no supported faithful representation, not that every
possible encoding is impossible. It retains the directed package pair, exact
use/type/profile alternative and slice context, definition canonical/version,
complete required/native effective facts and their origins, and typed reasons.
It never preserves restrictions, establishes equivalence, or supplies a usable
target canonical or projection. Binding differences do not manufacture unsafe
code rules; the existing qualified policy and executable guards remain required.

Both effective fact graphs are classified before a negative decision is
accepted. Binding names/descriptions and common-binding annotations are metadata;
actual bindings, including min/max value-set extensions, remain restrictions.
Cardinality, type alternatives, slicing, representation, invariant parameters,
fixed/pattern values, bounds, lengths and modifier obligations cannot disappear
behind another known failure. Missing/malformed facts, unknown semantic fields
or extensions, invalid ownership and comparison-only results remain unresolved.
The offline five-core proof census classifies all 3,604 cases across all 20
directed pairs: 284 native reuses, zero owner projections and 3,320 structured
unrepresentable decisions, with zero unresolved cases. These negatives are not
preserved restrictions, Equivalent results, usable targets or universal
impossibility claims. The census includes positive R5-to-R4B native SimpleQuantity
reuse at both Range bounds
and executable non-equivalence witnesses for DSTU2 Age zero and R5 comparator
`http://hl7.org/fhir/quantity-comparator|5.0.0#ad`.

### Prepared-operation workload follow-up (2026-11-18)

The root-type consumer mismatch in the original attempt below is corrected:
effective profile facts now use the validated snapshot root's owner-declared
Type when its element-level type list is empty, matching import without changing
the normalized payload or fixture root types. A first rerun reached export,
which correctly refused the fixture's release-specific required comparator
bindings. The fixture now uses the independent terminology version `1.0.0`
for its comparator binding, ValueSet, CodeSystem, compose and expansion in all
five packages. Production proof and export safety checks remain unchanged.

Both `XVerPreparedOperationTests` now complete comparison and outcomes for all
20 directed pairs and actual vocabulary/structure export for the 12 configured
pairs. Negative-decision assertions require the deliberately incompatible
`NegativeQuantity` owner and an invariant-transformation reason, not an
accidental terminology mismatch. The focused run, including three root-type
regression cases, passes all five tests.

This confirms successful synthetic pipeline execution, not a frozen semantic
golden or a W1/W2 performance baseline. Both named workload tests still run the
same helper and do not compare independent workloads or timing distributions.

### Prepared-operation baseline attempt (2026-11-18; historical, incomplete)

This is **failed-workload diagnostic evidence, not a performance baseline**.
Production remained at `6033290a2abda35092675917e7fcdec5daba222d` on `dev`;
only the new test fixture and this document changed. Conventions/commands came
from root `AGENTS.md`. The caller supplied SDK `11.0.100`; the focused command
built Debug/net11.0. No production instrumentation, optimization, package
installation, external database, or external IG tree was used.

`XVerPreparedOperationTests` joined the existing nonparallel unsafe-code profile
collection. At that point its two named tests entered the same **unfinished W1
gate**; they were not completed semantic-equality and W1/W2 measurement tests.
Do not use the historical attempts as Phase 1 acceptance, frozen semantic
expectations, or a candidate timing comparison. No golden expectation was
derived from a failed run.

#### Authored W1 inputs and intended schedule

- Each invocation creates a distinct `temp/xver-prepared-<guid>` directory under
  the repository, containing its own `workload.sqlite`, authored map inputs and
  prospective `output` tree. Failed fixtures are retained for diagnosis; successful
  fixtures would be deleted after closing the database and clearing its pool.
- Five synthetic `DefinitionCollection` instances use core identities
  `hl7.fhir.r2.core#1.0.2`, `hl7.fhir.r3.core#3.0.2`,
  `hl7.fhir.r4.core#4.0.1`, `hl7.fhir.r4b.core#4.3.0`,
  `hl7.fhir.r5.core#5.0.0`. These are **authored inputs, not loaded real cores**.
  Public `TryLoadFromDefinitionCollections` declares counts from those collections.
- Each release retains five primitives (`code`, `string`, `uri`, `decimal`, `id`);
  seven complex types (`Element`, `BackboneElement`, `Coding`, `CodeableConcept`,
  `Quantity`, `Range`, `Extension`); four resources (`Resource`, `DomainResource`,
  `Basic`, `PreparedConsumer`); and four profiles. Base definitions are
  version-qualified and local to the package. Every owner has a snapshot root;
  root `ElementDefinition.Type` is currently omitted, with the relational root
  type projected by production import from `StructureDefinition.Type`.
- `SimpleQuantity|quantity-definition-7` prohibits comparator and carries
  `sqty-1` / `comparator.empty()`. Its R3 root ID is
  `Quantity:simplequantity` with Path `Quantity`. `NegativeQuantity` is an
  eligible, unused datatype profile with a deliberately different R5 invariant
  (`value.empty() or value >= 0` versus `value.empty() or value > 0`).
  `PassiveCodingA/B|profile-definition-7` are fully retained passive alternatives
  with fixed code `selected`, used together by the consumer.
- Repetition count is **3**: the consumer has three `quantityN` and three
  `simpleN` Quantity uses constrained to SimpleQuantity, three native Range uses,
  and three Coding uses with both passive alternatives. Each native Range has
  low/high constrained to the exact SimpleQuantity definition version.
  Quantity.comparator has a required binding to the fixture's qualified
  comparator value set; each release has the corresponding complete two-code
  code system, compose and expansion.
- Maps comprise one type fallback plus type/resource/element maps for each of
  the 20 directed pairs. Primary SimpleQuantity equivalence overrides fallback
  `NotRelatedTo`. Other types use fallback equivalence. Resource maps relate
  PreparedConsumer conservatively (`RelatedTo`) to request a generated resource
  profile. The single explicit element map per pair maps `.renamed` in R5 to
  `.label` elsewhere (identity for other pairs); other elements exercise root
  fallback. There is no unsafe-rule configuration or fabricated completion stamp.
- Intended calls are actual `FhirDbComparer.Compare` and
  `OutcomeGenerator.GenerateOutcomes`, both with vocabulary/structures enabled,
  `maxStepSize: 4`, no pair filter (normal 20 directed stepped pairs).
  Actual `XVerExporter.Export` is configured with scripts disabled,
  vocabulary/structures enabled, max step 4, and target flags R3/R4/R4B only
  (12 directed pairs). Outcomes/export were **not reached**, so successful safe
  resource construction, typed-negative persistence and the export schedule
  assertion are not yet observed.

The final failed fixture contains 100 structures/facts, 315 elements, 370 type
rows, zero additional bindings, five code systems, five value sets and ten
value-set concepts. It has 61 map source files, 380 structure maps and 20 element
maps. The 61 actual JSON map files total **33,818 bytes**. Their framed SHA-256 is
`2c9d7955739338c44343ea2746321ad02b51a0b60e565193d2de27b81eec7bff`:
sort file paths, then feed each repository-fixture-relative POSIX path as UTF-8
prefixed by its 4-byte big-endian length, followed by exact file bytes prefixed
by their 8-byte big-endian length, to SHA-256. No map/payload normalization
was applied.

#### Exact attempts and failure

Only this sanctioned per-test diagnostic form was executed, three times:

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics"
```

| Attempt | Result | Command wall time | Test time / last stage |
|-|-|-:|-|
| 1 | Exit 1; fixture compile error CS0117: enum incorrectly qualified as `CodeSystem.CodeSystemContentMode` | Not captured | No test executed |
| 2 | Exit 1; shared connection closed by fixture's `using MappingLoader` | 25.943 s | Runner reported 3 s; maps completed, subsequent mapping SELECT failed |
| 3 | Exit 1; native Quantity root effective-type resolution | 23.105 s | Runner reported 3 s; `FhirDbComparer.Compare` failed |

Attempt 1 was corrected to `CodeSystemContentMode`. Attempt 2 was diagnosed
against `MappingLoader.Dispose`, which closes/disposes the supplied connection;
the fixture now follows existing fixtures by leaving that connection owned by
`ComparisonDatabase`. No production behavior was changed.

Attempt 3 fails in the first R2-to-R3 structure direction:

```text
Profile facts pair 1->2, package 1, owner 10
'http://hl7.org/fhir/StructureDefinition/Quantity|1.0.2',
element 'Quantity': missing/ambiguous effective type-use for row 14.
```

The call chain is `FhirDbComparer.Compare` → `StructureComparer.CompareStructures`
→ `ElementComparer.DoElementComparisons` (fallback path) →
`ElementTypeComparer.doElementTypeComparison` → `ProfileUseFacts.AtUse`.
Import, inventory validation, map loading and vocabulary comparison had succeeded.
Read-only inspection of the retained database confirms root type row 14 has
package 1, structure/element 10/14, `TypeName = Quantity`,
`TypeStructureKey = 10`, and null profile/target-profile fields. The normalized
payload root is `{"id":"Quantity","path":"Quantity","min":0,"max":"*"}`;
the definition declares Type Quantity and BaseDefinition
`Element|1.0.2`. The importer synthesizes that relational root type, whereas
`ProfileUseFacts.Definition` reads the empty normalized root type list and
`AtUse` cannot match it.

This diagnoses an input-shape/consumer mismatch, **not proof that W1 cannot be
made successful solely in the fixture**. Explicitly authoring normalized root
types, as the shared authored-definition fixture does, remains an untried
fixture correction. The three-attempt debugging budget was exhausted, so no
fourth run, production fix, narrowed workload or expectation-changing workaround
was attempted. All 20 pair-state rows exist from input setup, but all comparison
completion stamps are null; structure/element/type comparison tables contain
zero rows. Vocabulary comparison has 20 comparison and 40 concept-comparison
rows. Pair-state row count is therefore not evidence of executing 20 pairs.

The attempt-3 database/maps remain at repository-local
`temp/xver-prepared-4a9f4143ebe84bc8966e92b8c7e16db0/`.
Attempt 2 remains at
`temp/xver-prepared-19c6e41356bd496fb0af173fdc66f588/`.
No output tree was materialized.

#### Actual uninstrumented diagnostic measurements

Stopwatch scopes encompass the named action, including constructor/readiness
work. The import scope currently also includes synthetic definition construction.
Allocation is the `GC.GetTotalAllocatedBytes(true)` delta. Private bytes use
`Process.PrivateMemorySize64` after refresh; managed bytes use
`GC.GetTotalMemory(false)`. These are entry/exit observations, not live retained
graph measurements. Working-set peak is the process-lifetime
`PeakWorkingSet64`, not a sampled stage-private peak. DB size is the SQLite file
length after the action, including partially failed work.

| Attempt/stage | ms | Allocated bytes | Private entry → exit | Managed entry → exit | Process peak working set | DB bytes |
|-|-:|-:|-:|-:|-:|-:|
| 2 / definitions + import | 1,295.2146 | 67,996,320 | 57,663,488 → 88,358,912 | 18,506,864 → 46,352,960 | 188,825,600 | 581,632 |
| 2 / maps (including incorrect disposal) | 2,161.2927 | 89,479,888 | 88,387,584 → 81,829,888 | 47,927,160 → 47,024,232 | 188,825,600 | 921,600 |
| 2 / database disposal | 3.9001 | 384 | 82,661,376 → 81,178,624 | 47,032,456 → 47,040,680 | 188,825,600 | 921,600 |
| 3 / definitions + import | 955.5843 | 67,919,632 | 57,458,688 → 88,469,504 | 18,257,120 → 46,121,536 | 187,170,816 | 581,632 |
| 3 / maps | 1,516.4160 | 89,435,528 | 88,477,696 → 92,016,640 | 47,695,736 → 46,583,496 | 187,170,816 | 921,600 |
| 3 / failed comparison | 599.2830 | 150,936,072 | 92,180,480 → 89,141,248 | 47,233,192 → 45,271,896 | 187,170,816 | 1,028,096 |
| 3 / database disposal | 3.4840 | 440 | 89,141,248 → 85,598,208 | 45,271,896 → 45,280,120 | 187,170,816 | 1,028,096 |

Fixture-clock totals including setup/checks/disposal were 3,667.143 ms
(attempt 2) and 3,150.134 ms (attempt 3). Neither is successful pipeline time.
The inputs/code differed between attempts; they are not repeated identical
observations and no median or MAD is meaningful.

#### Counting definitions, seams and remaining acceptance gaps

No expensive-operation instrumentation was added. Catalog/validator/parse,
effective-definition/use, directional-classification and owner-projection
execution counts are **unmeasured**, not zero. Row counts above are final
database cardinalities only. The intended temporary counting points remain
`DefinitionInventoryStore.ValidateInventory` (whole semantic passes),
`StructureDefinitionFactsStore.ReadDefinition` (transient inventory parses,
separated from lazy parse callers), `XVerStructureCatalog` construction and
`ReadPayload` cache misses, `ProfileUseFacts.Definition` / `AtUse` cache misses,
`ClassifyRequiredUses` (one direction) and `ProveInOwner` (owner-specific proofs).
Future raw-audit count/rows/exact JSON bytes/time have no baseline implementation;
do not fabricate observed values for them.

Relevant existing W1 boundaries are import completion; mapping readiness/input
completion; comparison readiness before reset, pair begin and post-flush pair
completion; outcome readiness before reset, pair begin/generation/completion;
export readiness, write-free construction and materialization. Catalog consumers
read packages/structures/facts/elements/types; inventory also reads count/state
and additional bindings; map/proof consumers read normalized map/provenance and
comparison rows; outcome/export safety independently reads persisted decisions,
targets, triggers and live pair/input stamps. This is a preliminary source seam
inventory, **not** the complete operand manifest or a measured dependency audit.

Still outstanding: a successful complete W1; the W2 five-pinned-cached-core
import and actual Range comparison/outcome/build seams with identified authored
envelopes and R5-comparator/DSTU2-Age correctness witnesses; frozen logical
rows/resources, exact negative documents/constraints/provenance/owner/order/
filename semantics; consistent substitution of only identified runtime
IDs/timestamps; one discarded warmup plus three identical fresh-process
observations; separate temporary instrumented counts; uninstrumented full
operation timing including disposal/emission; sampled memory and retained
row/JSON/decision sizes; and final correctness verification. Neither the full
class selector nor `IdenticalWorkloadsReportStageCosts` was run. The real-core
cache was not probed or skipped, and no W2 data is claimed. Production source
is untouched, so there are no observation hooks to restore.

## Algorithm

1. If `forceCreate` is false and `_dbName` is not empty, open the named database with `_db = new(_dbPath, _dbName, _config.LogFactory)` and return (historical location: `XVerProcessor.cs:509-519`). The existing-database constructor rejects a missing file with `FileNotFoundException` and its path, without creating a parent directory. It opens with `SqliteOpenMode.ReadWrite`, checks `sqlite_master` for `DbFhirPackage.DefaultTableName` before loading indices, and throws `InvalidDataException` with the database path and rebuild guidance if the table is absent. Constructor initialization failures dispose the connection and rethrow the original exception; there is no fallback to fresh creation.
2. If `_definitions.Length == 0`, call `loadDefinitionCollections()` (`XVerProcessor.cs:521-525`). That helper iterates `ComparePackages`, validates each directive, creates a `PackageLoader` per directive, expands R5 loads to include `hl7.terminology@5.1.0`, calls `LoadPackages(loadDirectives).Result`, and stores the resulting `DefinitionCollection` array and lookup indexes (`XVerProcessor.cs:213-250`). See the package-filter decision below for the apparent `PackageIsFhirCore` anomaly.
3. Construct the database with definitions: `_db = new(_definitions, _dbPath, _dbName, _config.LogFactory)` (`XVerProcessor.cs:527-528`). The constructor opens/creates the SQLite file in read/write/create mode and calls `initNewDb(ensureDeleted: true)` (`ComparisonDatabase.cs:147-158`).
4. Store `_dbName = _db.DbFileName` so later lazy calls can reuse the generated or supplied filename (`XVerProcessor.cs:528-529`).
5. Call `_db.TryLoadFromDefinitionCollections(_exclusionSet, _escapeValveCodes)` (`XVerProcessor.cs:531-532`). If it returns false, throw `Failed to load FHIR-based definitions into the database: ...` with the loaded package keys (`XVerProcessor.cs:532-535`).

## Decision Points

- **Existing-DB reuse.**  
  **Rule:** When `forceCreate=false` and `_dbName` is set, `LoadDatabase` opens a validated existing named SQLite database and returns without loading definitions or re-seeding content. Only the default full-pipeline branch automatically requests fresh loading for a missing named file; reuse-only subcommands must explicitly request initialization. Invalid existing databases are never automatically rebuilt.\
  **Source:** `XVerProcessor.ProcessCommand` default branch and `LoadDatabase`; the existing-database constructor now uses `SqliteOpenMode.ReadWrite` and checks the package table before loading indices (historical locations: `XVerProcessor.cs:510-519`, `ComparisonDatabase.cs:177-190`).\
  **Rationale:** AI Guess: the cross-version pipeline can be expensive; reusing a previously loaded database keeps inner-loop operations from re-downloading packages and re-importing all content.

- **Fresh creation is destructive for content tables.**  
  **Rule:** The definitions-based constructor always calls `initNewDb(ensureDeleted: true)`, and `initNewDb` drops then recreates content/support tables before inserting package metadata. Recovery with `xver load` or `--reload-db true` explicitly selects this destructive path and replaces existing content; preserve valuable database contents first.\
  **Source:** `ComparisonDatabase.cs:147-158`, `ComparisonDatabase.cs:593-647`, `DbContentClasses.cs:31-109`.  
  **Rationale:** The code path explicitly requests `ensureDeleted: true` and recreates a clean content schema before loading definitions, so a forced/fresh load is designed to replace the prior content snapshot rather than merge into it.

- **`_exclusionSet` URLs skipped at load time.**  
  **Rule:** `ucum-units`, `all-languages`, `mimetypes`, `timezones`, plus the DSTU2 BCP47 / BCP13 URLs for the latter two categories, retain their vocabulary exclusions. Structure exclusions still prevent processing; all retained profiles and their available projections are stored even when excluded.\
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
  **Rule:** `LoadDatabase` exposes `FhirArtifactClassEnum? artifactFilter = null`, but the method body never reads it; fresh loads import the inventories described above, not separately classified extensions/logical models.\
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
- When `forceCreate=false` and `_dbName` is set but the file does not exist, reuse throws `FileNotFoundException` including the path, without creating the file or its parent directory. The default full pipeline avoids this reuse path by requesting fresh loading for a missing named file.
- An empty or unrelated existing SQLite database lacking `DbFhirPackage.DefaultTableName` throws `InvalidDataException` including its path and explicit `xver load` / `--reload-db true` recovery guidance. Existing data is not overwritten automatically; explicit rebuilding is destructive for existing content.
- Other SQLite errors, such as a corrupt/non-SQLite file, propagate unchanged rather than being converted to schema guidance or triggering a rebuild. Connections are disposed when reuse constructor initialization fails.
- `Program.DoXVer` writes caught errors (including inner-exception messages when present) to stderr and returns exit code 1. Successful processing still returns 0. Historically these errors were written to stdout and the command returned 0 even after failure.
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
- [x] Full normalized profile inventory, exact identity/roots, independent completion and passive binding isolation

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs` (lines 104-143, 161-250, 256-431, 505-538, 557-566, 614-622, 668-676, 749-798)
  - `src/Fhir.CodeGen.Comparison/Models/ComparisonDatabase.cs` (lines 80-190, 593-696; `TryLoadFromDefinitionCollections` signature at lines 652-654)
  - `src/Fhir.CodeGen.Comparison/Models/DbContentClasses.cs` (lines 31-109)
  - `src/Fhir.CodeGen.Comparison/Models/DbStructureDefinitionFacts.cs` — normalized facts, inventory format/policy/counts and read-only readiness
  - `src/Fhir.CodeGen.Comparison/Models/XVerStructureCatalog.cs` — definition/processing views and exact owner resolution
  - `src/Fhir.CodeGen.Comparison/Models/ProfileUseFacts.cs` — effective-use classification and bounded proof
  - `src/Fhir.CodeGen.Common/Extensions/CommonDefinitions.cs` / `src/Fhir.CodeGen.CrossVersionLoader/Convert_20_50/ElementDefinition_20_50.cs` — approved runtime XPath retention
  - `src/Fhir.CodeGen.Lib.Tests/XVerQuantityProfileTests.cs` / `XVerProfileCatalogTests.cs` — actual normalized cores and catalog regressions
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigXVer.cs` (lines 16-92, 459-472)
  - `src/Fhir.CodeGen.Lib/Configuration/ConfigRoot.cs` (lines 30-32)
  - `src/fhir-codegen/Program.cs` (`DoXVer`, revised failure stream and exit code)
- Related specs:
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md)
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md)
  - [`xver-load-fhir-type-valuesets.md`](./xver-load-fhir-type-valuesets.md)
  - [`fhirdb-comparer-compare.md`](./fhirdb-comparer-compare.md) — downstream consumer of the loaded DB
- Related `ConfigXVer` options: `ComparePackages`, `CrossVersionDbPath`, `CrossVersionMapSourcePath`, `ReloadDatabase`, `LogFactory`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*

*Database lifecycle and xver failure behavior revised on `2026-11-13`; historical references retained.*

*Active catalog/processing contract checked by method against `64d48bb993c6eef207e9b8ca03d593a277068746` on `2026-11-17`; unchanged historical line references retained.*
