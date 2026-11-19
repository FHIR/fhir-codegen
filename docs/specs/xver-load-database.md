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

### Prepared-operation recovery work attempt 2 (2026-11-18; blocked at compilation)

This is **new failed-gate evidence, not a successful characterization or
performance baseline**. Production baseline is
`d749a7b5e88889e3d5ac7b49e781de478b87e499`, branch `dev`. Root `AGENTS.md`
is the build/test command and convention source; `.editorconfig` was read.
The parent established the clean index/worktree/ownership entry gates and
freshly executed the two root-only tests before this implementer dispatch.
No entry gate was repeated here.

The original three diagnostic executions above occurred within work dispatch
1, followed by its one hand-back. This recovery is Phase 1 work attempt **2**
and cumulative execution-stage attempt **2** of the unchanged maximum 3;
the numbers below identify commands, not additional work attempts. The
operative plan's recovery contract and Notes supersession ledger replace the
old inference that three diagnostic commands exhausted three dispatches.
The historical failures and hand-back remain unchanged.

Likewise, the historical "explicit root types remain untried" and
"no production fix" statements describe the earlier baseline, not the current
recipe. `d749a7b5e` already supplies the owner-declared, root-only effective
type fallback, with empty normalized root types retained. Its comparator
terminology is independently `1.0.0` throughout the five authored packages,
and its negative witness deliberately requires `NegativeQuantity` and an
`InvariantTransformation` reason. Do not apply the obsolete explicit-root-type
workaround. The later historical five passing cases are not a retained W1
capture, distinct W2, frozen expectation set or performance baseline.

#### Chronological recovery command ledger

| Run | Command / result | Wall time | Last safe gate / reached stage |
|-|-|-:|-|
| 1 (parent) | Exact `XVerProfileCatalogTests.ImportedRootTypeUsesPreserveOwnerRestrictions` selector; exit 0, 2 passed | 11,857.5666 ms | Root-only entry behavior accepted |
| 2 (parent) | Exact `XVerProfileCatalogTests.DeclaredRootTypesDoNotInventProhibitedChildTypes` selector; exit 0, 1 passed | 4,632.0447 ms | Last successful verification gate |
| 3 (this dispatch) | Exact W1 selector below; exit 1, compiler errors, no test execution | 17,180.4257 ms | Test project compilation; W1 not entered |
| 4 (evidence only) | `git --no-pager diff --stat`; `git --no-pager diff -- src/Fhir.CodeGen.Comparison`; `git --no-pager diff --cached --stat`; Python read-only test-file SHA-256/line-ending inspection; exit 0 | Not measured | Diagnosis only, not a verification retry |

Run 3's exact test invocation was:

```powershell
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics"
```

It was wrapped only with a PowerShell `Stopwatch`, exit-code capture, normal
`Write-Output` reporting and `exit $result`; no runner switches, alternate CLI,
output redirection or dependency installation were used. Restore reported all
projects up to date. Dependencies built Debug/net11.0 before test compilation
failed. The command emitted the baseline NU1510
`Microsoft.Extensions.Logging.Abstractions` warnings in Lib, LangSQLite and
Comparison, plus the documented CS3021; no package/project manifest changed.

Complete compiler failure text:

```text
E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs(159,10): error CS1513: } expected [E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj]
E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs(422,5): error CS1519: Invalid token '}' in a member declaration [E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj]
E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs(660,7): error CS1513: } expected [E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj]
E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs(692,1): error CS1022: Type or namespace definition, or end-of-file expected [E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj]
Run 3: exit=1; commandMilliseconds=17180.4257
```

Read-only inspection identifies an implementer placement error: new private
helpers/records were inserted after the `finally` block but before the existing
local `Measure` function and closing brace of `RunSyntheticPipeline`. They are
therefore inside a method rather than at class scope. This is a new harness
compile blocker, not evidence against the root-facts correction or production
pipeline. The conservative first-failed-gate rule was followed: **no repair,
rerun, W2 execution, class gate, instrumentation, warm-up, timing observation
or final suite was attempted after run 3**. The parent must reconcile the
hand-back and any permitted subsequent dispatch before a correction/execution.
Moving the helpers/records outside the enclosing method is a diagnosis, not a
fix performed here; additional compile/runtime correctness is unverified.

#### Attempted test-only changes and evidence identity

The attempted pre-W1 change adds `AssertEffectiveRootTypeUses`, deriving
package count expectations from authored collection membership, snapshot
elements, root projection and profile/target-profile cross-products, then
checking the stored declarations and rows. It intends to check all retained
owners' empty normalized roots and exact owner/package/element/native-type
linkage, null profile fields/keys, ordinal native Code/TypeName, and actual
`ForDefinition`/`AtUse` Quantity/SimpleQuantity facts with owner-local
prohibitions/invariant origins and R3 sliced IDs. Arrangement catalogs/facts
are local to that helper. **None of these new assertions executed.**

`CharacterizedPipelineProducesStableSemantics` now requests a
characterization retention path. The attempted `CaptureWorkloadSemantics`
records every SQLite table/schema/column/row, full typed negative documents,
all emitted/input relative filenames and exact file bytes/text, the complete
harness source and its SHA-256, runtime/OS, package rows and schedule settings.
Its logical metadata substitution map is intended to substitute only the
fixture GUID, inventory/input/comparison revisions and identified emitted
resource runtime date/timestamps consistently, retaining raw values and the
map as well. Capture serialization is outside the named comparison/outcome/
export clocks; any capture overhead is separately reported. Successful
characterization directories would survive database disposal and pool clearing
until reviewed/frozen; failures remain retained. **Capture/retention correctness
is unverified because compilation failed.** The duplicate W1 measurement test
has not yet been replaced with both workloads.

Exact attempted harness identity (run 3; no subsequent test-file repair):

- Path: `src/Fhir.CodeGen.Lib.Tests/XVerPreparedOperationTests.cs`.
- SHA-256:
  `4790679fabc76420e8702dd53a52515094679ab4fe46eb33284aaccb552bb758`.
- 41,112 bytes, 692 CRLF line endings, zero bare LF.
- Authored package identities remain
  `hl7.fhir.r2.core#1.0.2`, `hl7.fhir.r3.core#3.0.2`,
  `hl7.fhir.r4.core#4.0.1`, `hl7.fhir.r4b.core#4.3.0`,
  `hl7.fhir.r5.core#5.0.0`; these W1 collections are synthetic.
- Payload/map authoring functions are unchanged from `d749a7b5e`; Quantity
  profile definitions remain `quantity-definition-7`, passive Coding
  definitions remain `profile-definition-7`, comparator terminology remains
  `1.0.0`, repetitions remain 3, compare/outcomes remain combined vocabulary
  and structures over max step 4/all 20 directions, and actual export remains
  scripts-off/R3-R4-R4B targets/12 supported directions.
- No new payload/map bytes or actual input-image digest were produced: the
  compiler stopped before fixture construction. Historical map bytes/digests
  above must not be relabeled as this run's measured input image.
- No `baseline-semantics.json`, database, output tree or new fixture directory
  was created by run 3. Historical evidence directories beginning
  `15ec56e8`, `19c6e413`, `4a9f4143` and `d1945dd0` were not modified or
  cleaned. No success/failure directories were deleted.
- The production Comparison diff and staged diff were empty in run 4.
  No production hooks were added, so none require restoration. Only the owned
  test and this documentation are changed; no plan/instruction/source report/
  approach/analysis/old-slot/external database/IG edits, staging or commits.

#### Automatic implementation assumptions (not user decisions)

- Check collection-derived expected counts against both importer declarations
  and relational observations; do not promote observed historical totals to
  authored expectations. This uses the documented import projection rules and
  keeps all normalized authored roots empty.
- Capture all database tables and emitted/input files rather than guessing a
  smaller semantic field subset; keep raw bytes plus logical text/typed
  negatives so owner, alternatives, map authority and observable allocation
  choices can be reviewed. Hashes identify the harness, not replace semantics.
- Keep characterization evidence by a fixture-local retention flag until a
  successful capture has been reviewed and typed expectations frozen; do not
  retain arrangement catalog/facts for measured consumers.
- Preserve a reference-consistent, recorded substitution map with originals;
  never normalize package/definition versions, owner keys, alternatives,
  mapping keys, constraints or decisions as runtime noise.
- Treat run 3 as this dispatch's first failed gate and stop without fixing it,
  even though the static placement error is identifiable. No technical or
  performance acceptance is inferred from dependency compilation or earlier
  historical passing tests.

Phase 1 remains **partial/blocked**. Last safe gate is parent run 2. Successful
retained W1/W2 captures, frozen semantics, class correctness, expensive-operation
counts, memory/retained-size evidence and the fresh-process warm-up/three-sample
baseline are all still outstanding. There are no new operation measurements,
medians/MAD, resource results or CLI-throughput claims.

### Prepared-operation recovery work attempt 3 (2026-11-18; characterization)

This appends to, and does not erase, the failed attempts above. The user
authorized correcting the helper placement **before** compilation-building
root gates. `Measure` remains local to `RunSyntheticPipeline`; the two private
helpers and four records are now class members. Static review also checked
their model/API declarations and the importer's independent count rules.
Root `AGENTS.md` and `.editorconfig` supply commands/conventions. The unchanged
production baseline is `d749a7b5e88889e3d5ac7b49e781de478b87e499`.
No explicit normalized root types, production fixes, optimization, dependency
changes, external DB/IG access, staging or commits are part of this recovery.

Each command below uses the documented focused exact-test selector:

```powershell
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerProfileCatalogTests.ImportedRootTypeUsesPreserveOwnerRestrictions"
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerProfileCatalogTests.DeclaredRootTypesDoNotInventProhibitedChildTypes"
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics"
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedRealCoreConsumersProduceStableSemantics"
```

Commands were wrapped in a PowerShell Stopwatch with `$LASTEXITCODE` captured
and reported as `Attempt 3 run N: exit=...; commandMilliseconds=...`, then
returned unchanged. There were no logger flags, redirected output, installs,
alternate build commands or verification retries.

| Run | Result | Command ms | Runner duration |
|-|-|-:|-:|
| 1 / imported roots | Exit 0; 2 passed, 0 failed/skipped; compilation restored | 23,811.6002 | 1 s |
| 2 / prohibited child types | Exit 0; 1 passed, 0 failed/skipped | 4,273.4484 | 759 ms |
| 3 / first retained W1 | Exit 0; 1 passed, 0 failed/skipped | 35,415.5594 | 14 s |
| 4 / first retained W2 | Exit 0; 1 passed, 0 failed/skipped | 312,217.1103 | 4m46s |

These are Debug/net11.0 observations, not Release CLI throughput. All restores
were up to date. NU1510 for the existing Logging.Abstractions references
(Lib/LangSQLite/Comparison) and the existing CS3021 appeared as previously
documented. Run 4 continued beyond the tool's initial 120-second synchronous
wait; its existing session was read until completion, never restarted.

#### Successful captures retained through disposal

- W1: `temp\xver-prepared-576212f9514e4f28b76b8f0e8af98fbe\baseline-semantics.json`,
  51,943,135 bytes; exact capture harness SHA-256
  `a1fd538dac34c701adbfb30bc845dda998becbc3f3f842ac3f97df8711a4a20b`.
- W2: `temp\xver-prepared-5315fd3f90ed4b01805984a895bd0727\baseline-semantics.json`,
  857,014,911 bytes; exact capture harness SHA-256
  `865c3fed7a410cf51ea206c0ece6999f281f1668bf28cfd55161bd1876bfb44a`.
- Both have `stage-costs.json`, the isolated `workload.sqlite`, authored map
  files and complete captured harness source. W1 also retains actual emitted
  files; W2 retains `built-definitions.json`. Both survived connection disposal
  and pool clearing. Neither successful fixture nor any old failed fixture has
  been deleted. No capture is committed or used as a runtime golden.
- Identity contains production base, exact source hash/source, package rows,
  runtime/OS, flags and schedule. A harness commit cannot yet be supplied:
  the parent owns phase commit and subsequent identity verification.
- Captures include all 39 SQLite tables with schemas, every persisted column
  and row, typed negative documents, exact relative filenames, file bytes and
  text. Raw runtime values remain with their reference-consistent substitution
  map. Only fixture/inventory/input/comparison GUIDs and identified generated
  runtime dates/timestamps are substitutable. Real keys, versions, alternatives,
  constraints, map authority and facts are not noise.
- W1 initially included an additional normalized logical copy. Subsequent
  captures stream the same raw table/file/typed-document image to disk and
  retain its substitution map without this redundant copy, avoiding an extra
  very large serialization/string graph. This is test-only evidence capture,
  outside operation clocks, not a production optimization. W2's generated
  profile date is explicitly registered from its built-resource JSON.

W1 preserved three repeated groups, all five authored releases, independent
terminology `1.0.0`, profile definition versions `quantity-definition-7` /
`profile-definition-7`, empty normalized roots and exact R3 slices. Authored
count declarations and effective roots passed before maps. All 20 comparison
and outcome directions completed with current revisions; all 12 supported
export directions executed. Observed rows: 100 structures/facts, 315 elements,
370 types, 360 structure comparisons, 980 element comparisons, 1,100 type
comparisons, 260 structure outcomes, 980 element outcomes/targets, 8 typed
negative documents, 20 value-set comparisons/outcomes and 40 concept
comparisons/outcomes. There are 529 captured input/output files, including 48
StructureDefinitions: four resource profiles per export pair. Passive profiles
do not become standalone artifacts. Primary SimpleQuantity authority survives
fallback NotRelatedTo; the mapped label/rename retains explicit provenance.
NegativeQuantity is the intentional invariant obstruction, not a binding error.

W2 loads, offline with dependencies disabled and expansions enabled, the five
pinned core packages listed above (and required cached expansions except R2).
It imports the **full retained inventory**, adding one DomainResource-derived
local `QuantityConsumerWitness` carrier to R5 and R4B, without editing any actual
core payload. The extra carrier field uses actual native Range. Four physical
reopens separate import/maps/comparisons/outcomes/build. Its local resource,
type and element maps are the same bounded seam arrangement as
`PersistedRangePipelineRetainsQuantityRestrictionsAndNoPassiveArtifacts`;
structure comparison envelopes and the single carrier artifact schedule are
explicitly authored, not whole-core StructureComparer/export success.

W2 observed 1,257 structures/facts (1,255 core owners plus two carriers), 54,266
elements, 67,624 types, 838 additional bindings, 3,813 code systems, 72,170
code-system concepts, 5,059 value sets and 81,523 value-set concepts. There are
1,026 structure mappings, 15 element mappings, three map sources, two structure
comparisons/outcomes, ten element comparisons/targets, seven type comparisons,
139 element outcomes and 129 typed negative documents. Those documents contain
2,353 decisions including children; full graphs, not counts alone, are retained.
Actual Range.low/high reuse exact target SimpleQuantity|4.3.0 with comparator
max zero and sqty-1. The write-free build returns exactly the resource carrier
profile, no extensions or owner projections, and creates no output root.
Separate expected rejections execute the actual R5 comparator `ad` required
binding witness and DSTU2 Age -1/0/1 XPath versus target age-1 FHIRPath witness.
They are successful rejection assertions, not failed runner gates or successful
whole-core throughput.

| First capture / stage | ms | Allocated bytes | Private entry → exit | Managed entry → exit | Process peak working set | DB bytes |
|-|-:|-:|-:|-:|-:|-:|
| W1 / definitions + import + root assertions | 1,005.4674 | 132,479,128 | 57,737,216 → 88,432,640 | 18,748,536 → 37,111,016 | 187,625,472 | 581,632 |
| W1 / maps | 1,512.5408 | 89,421,288 | 88,432,640 → 96,890,880 | 37,119,240 → 30,876,248 | 196,870,144 | 921,600 |
| W1 / comparison | 4,625.8464 | 4,191,257,792 | 97,095,680 → 109,723,648 | 31,517,720 → 63,654,160 | 198,479,872 | 9,596,928 |
| W1 / outcomes | 3,854.8131 | 2,930,417,216 | 109,723,648 → 126,107,648 | 63,687,056 → 54,711,168 | 233,426,944 | 11,603,968 |
| W1 / export | 1,447.4917 | 679,496,528 | 126,136,320 → 162,291,712 | 58,025,216 → 78,215,464 | 309,932,032 | 11,603,968 |
| W1 / disposal after capture | 5.1938 | 440 | 1,240,518,656 → 1,235,103,744 | 541,689,112 → 474,901,120 | 1,319,346,176 | 11,603,968 |
| W2 / full import | 47,295.0477 | 27,687,189,024 | 2,030,247,936 → 8,814,706,688 | 1,652,039,904 → 6,263,198,504 | 8,826,396,672 | 290,783,232 |
| W2 / maps | 33,181.1092 | 37,231,623,632 | 8,950,362,112 → 10,326,638,592 | 7,143,949,472 → 6,531,011,912 | 11,165,212,672 | 291,311,616 |
| W2 / Range comparison | 59,748.7187 | 55,626,798,736 | 10,324,656,128 → 11,574,427,648 | 6,531,069,480 → 9,282,171,776 | 11,624,296,448 | 291,569,664 |
| W2 / Range outcomes | 82,714.5772 | 66,627,196,880 | 11,732,615,168 → 13,322,547,200 | 9,919,326,768 → 12,723,956,960 | 13,355,200,512 | 390,725,632 |
| W2 / readiness + build | 33,925.1603 | 31,309,026,760 | 13,305,544,704 → 12,643,737,600 | 12,723,998,080 → 9,419,737,168 | 13,917,593,600 | 390,725,632 |
| W2 / rejection witnesses | 1,795.0982 | 790,174,064 | 12,643,733,504 → 12,342,951,936 | 9,419,745,392 → 5,445,588,872 | 13,917,593,600 | 390,725,632 |
| W2 / disposal after capture | 0.4055 | 200 | 12,490,444,800 → 12,488,163,328 | 7,231,392,272 → 7,231,392,272 | 13,917,593,600 | 390,725,632 |

These first captures are **not** timing distributions. Total fixture clocks
(including setup/capture/disposal) were W1 14,914.9589 ms and W2 286,912.3131 ms.
No median/MAD, counted-hotspot baseline or speedup follows. First-capture memory
is entry/exit and process-lifetime high water, not sampled stage peak or retained
live-object size. The subsequent harness adds a test-local 25 ms process-memory
sampler, retained row/JSON/decision byte reporting, capture-only cost reporting
and a separate net consumer-through-disposal clock. It replaces the duplicate
measurement body with both unchanged workloads. Frozen typed checks include
ordinary comparison membership/relationships, mapping authority, negative
owner/use/origin/restriction graphs, canonical versions, row links, exact
generated resource contents and filename membership. Their focused class gate
and the prescribed count/timing/restored-correctness runs are still pending at
this entry; these successful captures alone do not complete Phase 1.

Automatic implementation decisions: keep raw all-table/all-file captures;
stream large captures rather than retain a redundant normalized copy; use
ordinary test-local stage evidence files because successful test output is
not printed by the unmodified focused runner; use the existing bounded carrier
seam rather than claim full-core pipeline success. Rationale: preserve reviewable
semantic authority and reproducibility without logger switches, new goldens,
public telemetry or reduced production coverage.

#### Run 5 — focused frozen-assertion class gate failed; execution stopped

The following is the **first new failed verification command in work attempt
3**, not another work attempt. No repair, rollback, retry, timing/count run,
production instrumentation, optimization or subsequent test command followed.
The runner completed its already-started three-test class invocation; its
standalone W2 test passed, but that does not make the command green.

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName~XVerPreparedOperationTests"
```

Result: **exit 1; failed 2, passed 1, skipped 0, total 3; runner duration
7m47s; command 506,816.6415 ms**. Dependencies and the test project compiled
successfully. Restore was up to date; the same NU1510/CS3021 warnings were
reported. The last successful command gate remains **run 4**.

Complete new failure/stack text:

```text
[xUnit.net 00:00:27.10] Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics [FAIL]
Failed Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics [25 s]
System.InvalidOperationException : Cannot read the Value property of a Nullable object that has no value. Check HasValue before reading Value.
   at System.Nullable`1.get_Value()
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.AssertSyntheticSemantics(IDbConnection connection, WorkloadDirectory directory) in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 677
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.RunSyntheticPipeline(Boolean captureSemantics) in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 405
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics() in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 57
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

[xUnit.net 00:00:45.89] Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts [FAIL]
Failed Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts [18 s]
System.InvalidOperationException : Cannot read the Value property of a Nullable object that has no value. Check HasValue before reading Value.
   at System.Nullable`1.get_Value()
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.AssertSyntheticSemantics(IDbConnection connection, WorkloadDirectory directory) in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 677
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.RunSyntheticPipeline(Boolean captureSemantics) in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 405
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts() in E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Lib.Tests\XVerPreparedOperationTests.cs:line 63
--- End of stack trace from previous location ---

Failed! - Failed: 2, Passed: 1, Skipped: 0, Total: 3, Duration: 7 m 47 s - Fhir.CodeGen.Lib.Tests.dll (net11.0)
Attempt 3 run 5: exit=1; commandMilliseconds=506816.6415
```

Both failed cases printed the independent authored declarations for packages
1–5, each exactly:
`PrimitiveTypeCount=5, ComplexTypeCount=7, ResourceCount=4, ProfileCount=4,
ElementCount=63, ElementTypeCount=74, AdditionalBindingCount=0`.
Both printed `W1 emitted StructureDefinitions=48` and reached semantic
characterization **after** actual comparison/outcomes/export. Their respective
fixture totals were 25,006.9042 and 18,772.4266 ms. These are failed-test
diagnostics, not successful timing observations. Their complete stage JSON is
retained in each fixture's `stage-costs.json`; the normal runner output also
printed every field below.

Read-only diagnosis identifies a new **test-oracle assumption error**, not a
production failure: line 677 dereferences
`rename.ElementMappingKey!.Value` for every directed rename comparison.
The successful run-3 capture already shows explicit mapping keys only for the
eight adjacent (one-step) directions. All twelve two-/three-/four-step rename
comparisons have null `ElementMappingKey`; they follow the existing transitive
comparison path, which carries intermediate content keys/messages rather than
assigning a direct element-map key. Actual label/rename identity and Equivalent
relationship passed immediately before the dereference. The direct provenance
assertion must distinguish direct authority from transitive path evidence; this
is a diagnosis only, **not an implemented correction or permission to resume**.
No claim is made that this is the only remaining oracle gap.

Run-5 evidence retained, without cleanup:

- Failed W1:
  `temp\xver-prepared-2ced5418b3a8440789d8d67b531b08a6\`.
- Failed combined measurement test's W1:
  `temp\xver-prepared-e4d4337aef3a45cb844e4f83e354a47e\`.
  Its W2 body was not reached. Both failures retain DB/maps/48 generated
  StructureDefinitions and stage logs; neither produced a new semantic capture.
- Passed standalone W2:
  `temp\xver-prepared-97692461570c46fe9514e26198af105f\`.
  Its `baseline-semantics.json` is 857,212,414 bytes and retains all 39 tables,
  four files and 129 typed negative documents. The resource/fact-origin/
  restriction checks and both expected-rejection witnesses passed inside this
  otherwise-red class command.
- Exact final/failed-gate harness SHA-256:
  `b54df1281dfac70c7ea0c76df96211e5bbf367c1529be68ee3205b5d9d5007a2`;
  97,686 bytes, 1,664 CRLF, zero bare LF. The passed class-W2 capture records
  this same hash and complete source. Runtime `.NET 11.0.0`;
  OS `Microsoft Windows 10.0.26300`.

| Run 5 fixture / stage | ms | Allocated bytes | Private entry → exit | Managed entry → exit | Process peak working set | DB bytes | Samples | Sampled peak private / working |
|-|-:|-:|-:|-:|-:|-:|-:|-:|
| Failed W1 / import + roots | 2,540.5468 | 132,772,952 | 57,511,936 → 89,108,480 | 19,262,392 → 38,634,432 | 189,792,256 | 581,632 | 103 | 125,976,576 / 189,702,144 |
| Failed W1 / maps | 2,771.3981 | 89,456,040 | 89,108,480 → 88,719,360 | 38,649,440 → 31,800,376 | 195,264,512 | 921,600 | 112 | 110,743,552 / 195,067,904 |
| Failed W1 / comparison | 8,987.9603 | 4,184,935,184 | 88,752,128 → 101,339,136 | 32,450,072 → 59,296,848 | 199,409,664 | 9,596,928 | 361 | 108,302,336 / 198,668,288 |
| Failed W1 / outcomes | 7,373.0040 | 2,931,567,208 | 101,339,136 → 134,602,752 | 59,337,968 → 51,997,296 | 240,336,896 | 11,603,968 | 297 | 146,841,600 / 239,968,256 |
| Failed W1 / export | 2,875.7137 | 680,136,336 | 134,606,848 → 163,999,744 | 55,321,896 → 77,899,432 | 315,076,608 | 11,603,968 | 117 | 207,085,568 / 313,225,216 |
| Failed W1 / disposal | 9.1629 | 4,440 | 154,193,920 → 149,356,544 | 83,705,096 → 83,713,320 | 315,076,608 | 11,603,968 | 2 | 154,193,920 / 264,941,568 |
| Combined test W1 / import + roots | 689.3743 | 124,322,952 | 149,495,808 → 153,255,936 | 84,294,616 → 82,434,856 | 315,076,608 | 585,728 | 29 | 153,255,936 / 265,547,776 |
| Combined test W1 / maps | 941.4961 | 86,426,520 | 153,251,840 → 144,273,408 | 82,451,176 → 73,795,712 | 315,076,608 | 925,696 | 40 | 174,985,216 / 289,284,096 |
| Combined test W1 / comparison | 8,022.5214 | 4,184,166,216 | 144,273,408 → 137,478,144 | 74,420,736 → 50,839,832 | 315,076,608 | 9,633,792 | 323 | 149,311,488 / 262,352,896 |
| Combined test W1 / outcomes | 6,765.0401 | 2,932,140,928 | 137,478,144 → 141,799,424 | 50,872,728 → 56,854,120 | 315,076,608 | 11,665,408 | 272 | 157,376,512 / 271,937,536 |
| Combined test W1 / export | 1,968.5771 | 670,347,736 | 142,716,928 → 156,819,456 | 60,170,944 → 66,960,160 | 315,076,608 | 11,665,408 | 81 | 172,367,872 / 287,965,184 |
| Combined test W1 / disposal | 1.8483 | 4,328 | 154,316,800 → 149,737,472 | 85,675,296 → 85,683,264 | 315,076,608 | 11,665,408 | 2 | 154,320,896 / 269,111,296 |
| Passed class W2 / full import | 67,569.0747 | 28,589,234,280 | 2,014,744,576 → 8,834,695,168 | 1,631,862,000 → 6,279,097,472 | 8,872,124,416 | 290,893,824 | 2,670 | 8,854,978,560 / 8,872,124,416 |
| Passed class W2 / maps | 53,954.6287 | 38,659,406,472 | 8,969,420,800 → 10,392,047,616 | 7,175,852,872 → 6,519,363,104 | 11,263,447,040 | 291,422,208 | 2,154 | 11,245,551,616 / 11,263,447,040 |
| Passed class W2 / Range comparison | 84,742.7383 | 57,704,771,904 | 10,373,640,192 → 11,693,768,704 | 6,519,420,672 → 9,234,558,008 | 11,774,504,960 | 291,676,160 | 3,382 | 11,693,768,704 / 11,774,504,960 |
| Passed class W2 / Range outcomes | 114,060.8824 | 68,971,091,576 | 11,836,731,392 → 12,598,005,760 | 9,874,332,416 → 11,283,652,968 | 12,656,676,864 | 390,893,568 | 4,533 | 12,598,005,760 / 12,656,676,864 |
| Passed class W2 / readiness + build | 59,317.3178 | 32,213,283,032 | 12,579,778,560 → 11,976,601,600 | 11,283,702,312 → 9,240,906,704 | 12,964,884,480 | 390,893,568 | 2,354 | 12,908,490,752 / 12,964,884,480 |
| Passed class W2 / witnesses | 2,639.8600 | 818,080,248 | 12,369,420,288 → 12,097,302,528 | 10,641,706,992 → 5,468,615,968 | 12,964,884,480 | 390,893,568 | 106 | 12,480,757,760 / 12,502,917,120 |
| Passed class W2 / disposal | 0.6841 | 4,712 | 11,735,777,280 → 11,736,477,696 | 7,458,838,480 → 7,458,846,704 | 12,964,884,480 | 390,893,568 | 2 | 11,736,477,696 / 11,595,186,176 |

The failed W1 logs report net consumer/disposal scopes of 19,427.0943 and
16,923.8344 ms; these cannot be successful performance samples. Passed class-W2
reports 259,509.9553 ms net consumer/disposal, 423,350.2492 ms fixture total,
and 7,166.8019 ms capture-only. Capture memory: 279 observations, peak private
12,152,164,352 bytes, peak working set 12,182,179,840 bytes. Its retained image
contains 312,573 relational rows, 123,867,864 normalized JSON UTF-8 bytes and
98,821,683 prohibition/decision JSON UTF-8 bytes. These are serialized-image
sizes, not live managed graph sizes or a claim that post-disposal memory is
all live preparation. Sampling can miss peaks between 25 ms observations;
process high water includes earlier tests/capture. Allocation deltas include
sampling overhead. The class process is not a fresh-process distribution.

Read-only final scope inspection found only the owned test/spec changes,
empty staged diff and unchanged HEAD. All **63 tracked Comparison-tree files**
byte-match `d749a7b5e` after applying Git's checkout filters to baseline bytes.
No temporary hooks were added, so none require restoration. The failed
assertion and all other attempted code remain uncommitted and unrepaired.
No automatic semantic/schedule/authority decision was made after failure.

**Hand-back state:** Phase 1 remains partial, not complete. Both initial
characterizations succeeded and survive for review, but W1 frozen assertions
are red; assertions after the failed dereference are not verified. Complete
meaningful semantic-oracle acceptance, a green full focused class,
discarded warm-up plus three fresh-process observations per workload,
all four actual hotspot counts (including roots, transient/lazy parses,
facts and directional/owner proofs), medians/MAD, comparable memory/lifetime
evidence and final restored-production correctness remain incomplete.
Instrumentation counts are **unmeasured**, not zero; static 84 arithmetic is
not substituted. There is no candidate or speedup claim. The conservative
stop applies at this final allowed cumulative work attempt; the parent owns
plan/status integration and any further disposition. No next phase/stage,
full suite, final Release gate, commit, stage, push or PR was invoked.

### Targeted mapping-provenance assertion repair (2026-11-18)

Following the explicit request to fix the assertion, the W1 expectation now
distinguishes adjacent from transitive comparisons. Adjacent directions still
require an element mapping with the exact source/target element keys and a
ConceptMap source. Transitive directions explicitly require a null direct
`ElementMappingKey`; every direction also checks its expected step count,
target element and equivalent relationship.

The documented focused command passed after the repair:

```powershell
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.CharacterizedPipelineProducesStableSemantics"
```

Result: exit 0, 1 passed, 0 failed/skipped; runner duration 25 s. This executes
the corrected assertions for all 20 W1 comparison/outcome directions and the
12 supported export directions. Harness SHA-256:
`9B59395CDE81612A93D0151E69F778133599C616882AC736ECC42F2A9788349A`.
The Comparison production tree remains identical to `d749a7b5e`.

This targeted repair does not rewrite the failed attempt-3 class result,
reset its work-attempt counters, complete Phase 1 or start optimization.
The full class, expensive-operation counts and fresh-process timing
distributions remain outstanding.

### Prepared-operation recovery work attempt 4 (2026-11-18; measured, counts blocked)

This is append-only recovery evidence. The user expressly authorized an
additional three attempts; this dispatch is cumulative work attempt **4/6**,
not a reset of the historical failures. Runs below are commands within attempt
4, not additional attempts. Root `AGENTS.md` is the command/convention authority;
the complete plan, root `.editorconfig`, harness and this specification were
read. No optimization, external DB/IG access, installation, project/dependency
change, staging or commit occurred.

**Partial result:** the parent full-class gate and the discarded warm-up/three
fresh-process observations passed. The first subsequent failed gate was run 6:
temporary count instrumentation failed compilation. No instrumented test ran,
no counts or weak-reference observations were obtained, and no repair/retry or
post-restoration test was run. Last safe gate is **run 5**. All temporary hooks
were removed, restoring the complete Comparison production tree exactly.
Phase 1 is **not complete**; count/lifetime evidence and restored correctness
remain for a separately authorized recovery/integration.

#### Measurement-only harness changes and lifecycle

The targeted adjacent/transitive mapping assertion repair is retained without
loosening any frozen assertions, schedules, map authority, roots or negatives.
The measurement changes:

- Keep all successful measurement directories, including their SQLite DB,
  maps, emitted W1 files, `stage-costs.json` and new
  `measurement-identity.json`. No historical successful/failed directory was
  deleted or modified. `WorkloadDirectory.Dispose` no longer deletes evidence.
- Extend net clocks through the frozen semantic verification in both workloads
  and through W2's actual-core expected-rejection witnesses. Report verification
  as its own stage. Comparison/outcomes/export/build scopes themselves remain
  unchanged. Clocks start before entry/readiness and encompass required proof,
  verification, reopens and materialization; add final DB disposal/pool clearing
  to the net clock. No semantic-dump serialization runs in these clocks.
- After verification, outside the net clock, record exact package rows,
  runtime/OS/process/cache identities, per-table cardinalities, raw normalized
  payload digest/bytes, prohibition JSON bytes and each input map's path/length/
  SHA-256. These are **post-verification observations**, never independent
  source-count declarations or inventory completion authority.

The exact focused command for **every run 2–6** was:

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts"
```

The PowerShell wrapper starts `[System.Diagnostics.Stopwatch]::StartNew()`,
executes that command, captures `$LASTEXITCODE`, stops the stopwatch, prints
`Attempt 4 run N ...: exit=...; commandMilliseconds=...`, and exits with that
same result. No logger/performance CLI, redirection or extra runner switches.
All commands were synchronous, attached, with 600-second initial waits; none
timed out or was restarted/killed.

Each invocation is a new `dotnet test` process and a new testhost. The exact test
always runs W1 first, disposes it, then awaits W2 in the same process, serially
in the existing nonparallel collection. Thus each workload has a fresh process
relative to its other observations, but **W2 is not a cold process relative to
W1**: runtime/JIT, heaps and process high water carry over. Baseline/candidate
must preserve this same order and lifecycle. One paired warm-up is discarded;
exactly three paired observations are accepted without a timing-noise retry.
There is no candidate yet and no speedup or CLI-throughput claim.

| Run | Role / result | Command ms | Runner duration |
|-|-|-:|-:|
| 1 (parent, entry harness) | Full `FullyQualifiedName~XVerPreparedOperationTests` class; exit 0, 3 passed/0 failed/skipped | 520,099.19 | 8m25s |
| 2 | Discarded paired warm-up; exit 0, 1 passed/0 failed/skipped | 278,880.5701 | 4m16s |
| 3 | Accepted observation 1; exit 0, 1 passed/0 failed/skipped | 245,393.7905 | 3m41s |
| 4 | Accepted observation 2; exit 0, 1 passed/0 failed/skipped | 246,047.3037 | 3m40s |
| 5 | Accepted observation 3; exit 0, 1 passed/0 failed/skipped | 241,149.5451 | 3m36s |
| 6 | Separate count run; exit 1, compilation failure, no test execution | 20,487.6321 | Not entered |

Restore was up to date. Runs emitted the already-observed NU1510
Logging.Abstractions warnings (Lib/LangSQLite/Comparison) and documented CS3021
at `SqlOnFhir/ViewDefinition.cs(159,40)`. No manifest was changed to suppress
them. No broad solution build/full suite, root-only gate or first characterization
was repeated. Read-only inspection used git status/diff/hash/checkout-byte
comparison, SDK/host/cache inventory and selective Python JSON/SQLite-sized
evidence extraction; no 857 MB capture was loaded. One initial guessed
`Models/DbElementOutcome.cs` search path did not exist; its actual declaration
was located in `DbOutcomeClasses.cs` before running the measurements. That
read-only lookup was not a compile/test gate.

#### Persistent sample identities and unchanged workload inputs

Host: Windows 11 Pro, OS/runtime description `Microsoft Windows 10.0.26300`,
Intel Core i9-13900KF, 24 cores / 32 logical processors, OS visible memory
66,820,332 KiB. SDK **11.0.100**, runtime **.NET 11.0.0**, X64,
default focused-command **Debug/net11.0**, C# 14. Production baseline/HEAD:
`d749a7b5e88889e3d5ac7b49e781de478b87e499`. This is not Release CLI timing.
No CPU affinity, host isolation or cold filesystem-cache guarantee is claimed.
The read-only cached-package digest probe overlapped run 4's command startup;
the host was not exclusively reserved. No sample was rejected on that basis.

Entry/parent run-1 harness:
`9B59395CDE81612A93D0151E69F778133599C616882AC736ECC42F2A9788349A`.
All warm-up/accepted samples and the restored final source use harness SHA-256
`66D72D6631ECADA5C4611906ADEF5F3B16E9E1DB7E09AD1780B787AE2A699E19`
(102,570 bytes, 1,739 CRLF, zero bare LF). **No harness commit exists yet**;
parent owns integration/commit and must record its identity after verification.

All paths below are under repository-local `temp/`; retain both files in each
directory, not just this summary. All samples have `Completed=true`.

| Run | Testhost PID | W1 directory | W2 directory |
|-|-:|-|-|
| 2 warm-up | 47944 | `xver-prepared-171e7e2d77544e9584f231da109e4379` | `xver-prepared-3b7f64e0a92142d687fe885fae42ff65` |
| 3 observation 1 | 51052 | `xver-prepared-6cea3f1b236745c9943b6846feef87b1` | `xver-prepared-1dd803bde7ec4effb0143bb9ae586a14` |
| 4 observation 2 | 21004 | `xver-prepared-c9f02c6c9ba542639c19b2b24a577465` | `xver-prepared-d6b5fa77a8ef44919b41e706f528ed5f` |
| 5 observation 3 | 51100 | `xver-prepared-26111675338d4f45b4c91b1c2f430814` | `xver-prepared-a5f83d6f6b3e4ea7b73101e4234e55f4` |

SHA-256 identities for the persistent observation files:

| Run/workload | `stage-costs.json` | `measurement-identity.json` |
|-|-|-|
| 2/W1 | `83912a2b4a2d165fa8c5b174ec62cc6b83cf7663bc16bba22b5a8ba5efd8c04d` | `3ddd44ac36cf1fd51d1b640ec519584ee77f78e9b4a40974fa8fe5fcc42b5473` |
| 2/W2 | `d0c79d4ec586fda58572b5821f763ea8eda5806733a16191caa125c323c8a443` | `b186136b8767b85cd52375635609777925174cd543bbcf4b46dd85718d55ca3a` |
| 3/W1 | `16aec041a8eff27dcdf289cd9a22760e9732b178c5ff01edcb2a2c456130f6aa` | `c1f6ea091753edf875ce4a0003f7fe02f0012bf86ed6809d98033ff8d2e2af2c` |
| 3/W2 | `29ca2f7b5c552860184a8f674f9774848046a7dfaf3f5808eec07d1d43b4bb2d` | `56a9773c58e9f33729345ee43bd7dbeed1d523f69f38acf225338d3676f6709d` |
| 4/W1 | `8d0b8e0bff5a2c44169ed44321eeae84e2f6bbe7346a4ea9eddc3a7f499a2935` | `b5b52173bc87a7071a8652c7e415759e59266a221a8056508d3609dfc327fd8b` |
| 4/W2 | `6e44fb8e7a8db28fb59de8fa1bfc1d1316229de21ef7cb8ebe76966b7e13f639` | `6296e306f37b0a9c798b04a04685dd3b2761e80d2c8fd80cf11c453dc0b491b7` |
| 5/W1 | `d2929864d64c70b3d4fe28a60eb554125e2ee44d94378b72ff43aefbb7a084ce` | `343a8176b861020b6639843272c3f64ab98ac2f9c82cf0d5c40311deb6945bf2` |
| 5/W2 | `827dd3a24392528e004b433d838b3eee75db79c36737ee2c7d4229cc59a30963` | `7a43f6c8495f530f8789c131a9a0745df7f04f1b6416c38615d7acbc81c40a54` |

The W1/W2 recipes above remain operative: five release identities, three repeated
W1 groups, independent comparator terminology `1.0.0`, profile-definition
versions `quantity-definition-7` / `profile-definition-7`, authored empty root
type lists, max step 4, both vocabulary/structures enabled for actual W1
compare/outcomes/export, 20 comparison/outcome directions, 12 actual scripts-off
exports to R3/R4/R4B, 48 emitted StructureDefinitions. The fixed eight adjacent
rename directions require exact map endpoints/ConceptMap source; twelve
transitive directions require null direct keys and all directions check steps.

W2 still imports every retained owner of the actual five cached cores plus
two local R5/R4B DomainResource-derived carrier envelopes (1,257 facts).
It exercises actual R5→R4B Range/carrier element/type comparisons, actual
element outcomes, full readiness and a write-free build with its explicit
local structure envelopes/single-carrier schedule. It retains four physical
reopens and actual R5 comparator/DSTU2 Age expected-rejection witnesses.
It is **not whole-core comparison/export throughput**. Its map loader uses
the unchanged production internal primitive maps (`useInternalTypeMaps: true`),
in addition to its three local JSON maps; no external map repository is read.
W1 disables internal maps.

For **all four runs**, observed raw normalized payload identities match exactly:

| Workload | Structures/facts; elements/types/additional bindings | Total retained relational rows | Normalized JSON UTF-8 bytes | Prohibition JSON UTF-8 bytes | Raw normalized-payload SHA-256 |
|-|-|-:|-:|-:|-|
| W1 | 100; 315/370/0 | 6,185 | 103,112 | 112,500 | `7c67389b93345fdcf242d24a50edc332dba5295624e4ca1200249f16d5c300ff` |
| W2 | 1,257; 54,266/67,624/838 | 312,573 | 123,867,864 | 98,819,836 | `b8d0d6a31ac493d0fc75cd63a37282a4edaf863eb51f2fd0b1fe43b63802cad6` |

The payload digest reads facts ordered by package/structure key; for each row
append UTF-8 ASCII-decimal `packageKey/structureKey/jsonByteLength:` and exact
UTF-8 normalized JSON bytes to SHA-256. No JSON normalization/substitution is
performed. These digests identify input payloads, not prove semantic equality.
Each identity file additionally contains all 39 table counts and five exact
package rows (keys 1–5 in R2/R3/R4/R4B/R5 order). Definition import declarations
still originate independently from authored/loaded collections, never this query.
Prohibition-byte sizes include every nonnull `ExtensionProhibitionReason`,
not just typed negatives; these are serialized sizes, not managed graph sizes.

W1 maps: 61 files, 33,818 bytes, framed SHA-256
`2c9d7955739338c44343ea2746321ad02b51a0b60e565193d2de27b81eec7bff`.
W2 maps: three files, 3,138 bytes, framed SHA-256
`3a7ae05f74752835374e93c0d5b316c47668c3a2ea1f56c33f3908798ee11d16`.
Every file's relative POSIX name/length/exact SHA-256 agrees across all four
runs and is retained in each identity JSON. Framing is the previously specified
4-byte big-endian path length/path/8-byte big-endian content length/content,
sorted by path, relative to each fixture root.

Actual W2 cache is `C:\Users\ginoc\.fhir\packages`; loading is offline,
dependencies disabled, automatic expansions enabled (no R2 expansion required
by this harness). Exact cache-byte identities below were independently read
without changing cache contents. Hash every file recursively under the named
package directory (including `package/` in relative POSIX paths), sorted by
path, with the same framed SHA-256 recipe above:

| Cache package | Files | Bytes | Framed SHA-256 |
|-|-:|-:|-|
| `hl7.fhir.r2.core#1.0.2` | 2,846 | 27,433,308 | `41e9b678cf775cb089b28b329d6a1ac9128636152362407271b049fa65a654e4` |
| `hl7.fhir.r3.core#3.0.2` | 4,397 | 31,602,429 | `17907529aa4787b87e2cf23eefca3be216f4d392ac59e403232e45d4fe33a24b` |
| `hl7.fhir.r3.expansions#3.0.2` | 462 | 6,366,384 | `3cc403e42b42bea9c715f682b8c40efa7ac61dafc8645c013aaf8274f287afcf` |
| `hl7.fhir.r4.core#4.0.1` | 4,742 | 38,110,274 | `ded21a32204674e6456e55142198fd0a3b29b8b858a0932a55f7e2b5d624c225` |
| `hl7.fhir.r4.expansions#4.0.1` | 1,108 | 15,215,057 | `809098c2a9894ad635b29cb3781c125f63649d430860d8e8dc83dd649072eb53` |
| `hl7.fhir.r4b.core#4.3.0` | 3,914 | 67,587,778 | `38d370757e6b4cfcf6e015c0036923e31b4950c75c20c19abc9e82f2093fecb1` |
| `hl7.fhir.r4b.expansions#4.3.0` | 659 | 30,812,883 | `2a8e6f59431676305fd9f5c1e6338a2d447c56b25a2fae4f5c25ca6873bb678e` |
| `hl7.fhir.r5.core#5.0.0` | 3,836 | 87,257,723 | `18e92cf46d800979d9b508acd60d996c95232960fd0b0c7c84db07ceb5a95377` |
| `hl7.fhir.r5.expansions#5.0.0` | 775 | 33,826,337 | `29f151c60c9028a8787576d2fff39d9feeb4fefdddad728fb1c3551e82ccfb54` |

#### Stage timing/allocation observations

All times are milliseconds; allocations are exact
`GC.GetTotalAllocatedBytes(true)` deltas, including test-local sampling overhead.
Stage abbreviations: **I** definitions/import (W1 also authored root checks;
W2 full retained import only); **M** maps; **C** actual comparison; **O** actual
outcomes; **E/B** W1 actual export or W2 readiness/write-free build; **V** frozen
semantic verification; **N** W2 actual-core negative witnesses; **D** final DB
disposal/pool clearing. W1 export includes readiness, construction and emission;
these inner costs are not separately instrumented timing claims.

| Run/workload | I ms / allocated B | M ms / allocated B | C ms / allocated B | O ms / allocated B |
|-|-:|-:|-:|-:|
| 2/W1 (discarded) | 1105.3579 / 132731632 | 1395.2904 / 89644408 | 5350.7318 / 4197831704 | 4629.0574 / 2935665352 |
| 3/W1 | 1053.8189 / 132589232 | 1491.1001 / 89652880 | 4996.1272 / 4197688696 | 3945.2857 / 2935532800 |
| 4/W1 | 1029.2050 / 132659112 | 1819.5082 / 89703288 | 4951.0701 / 4198972952 | 4664.3431 / 2935698584 |
| 5/W1 | 1128.6510 / 132618144 | 1664.8907 / 89283896 | 5257.8143 / 4180036616 | 3762.6708 / 2930981680 |
| 2/W2 (discarded) | 34382.5315 / 28596227392 | 25133.2322 / 38658596080 | 53846.7019 / 57702181328 | 81846.0123 / 69008393104 |
| 3/W2 | 32881.9393 / 28595928096 | 32228.3052 / 38659839576 | 41711.0818 / 57700122784 | 55065.4625 / 68987169112 |
| 4/W2 | 35618.7873 / 28596332608 | 27382.7070 / 38659031912 | 40413.8460 / 57699902800 | 53125.5227 / 68974005416 |
| 5/W2 | 34162.3539 / 28583649408 | 25845.5717 / 38646161808 | 41136.7935 / 57671948624 | 53878.2341 / 68955831104 |

| Run/workload | E/B ms / allocated B | V ms / allocated B | N ms / allocated B | D ms / allocated B | Net consumer + D ms | Total including setup/import/evidence/D ms |
|-|-:|-:|-:|-:|-:|-:|
| 2/W1 (discarded) | 1242.7477 / 681660704 | 109.5002 / 63946344 | — | 5.3043 / 4440 | 11444.1016 | 14040.3256 |
| 3/W1 | 1494.8536 / 681551968 | 114.1909 / 63946304 | — | 5.1790 / 4440 | 10676.7493 | 13322.0402 |
| 4/W1 | 1822.9677 / 681664192 | 129.3132 / 63951856 | — | 5.0668 / 4440 | 11694.4277 | 14656.8106 |
| 5/W1 | 1668.9404 / 679764088 | 125.6943 / 63939272 | — | 5.3209 / 4440 | 10952.4363 | 13852.5000 |
| 2/W2 (discarded) | 28797.6703 / 32196863624 | 3572.6542 / 3539481736 | 750.7956 / 818916912 | 1.4206 / 4520 | 169677.6520 | 242018.0600 |
| 3/W2 | 28708.5678 / 32197289784 | 3756.8178 / 3500486008 | 738.8843 / 814613704 | 1.3513 / 4520 | 130808.6024 | 208583.0127 |
| 4/W2 | 29987.1842 / 32234788616 | 3635.6646 / 3539719760 | 799.2498 / 818919560 | 2.1759 / 4456 | 129025.3232 | 205595.9231 |
| 5/W2 | 28313.6701 / 32169952496 | 3898.8680 / 3570472984 | 772.3330 / 818909680 | 1.3345 / 4456 | 129123.2825 | 202540.9005 |

Total is the fixture stopwatch, not command/testhost wall time. It includes
offline cached-definition loading, import/setup/checks, input-identity evidence
generation and disposal; no semantic capture was requested. Net starts after
maps and includes intervening required checks/reopens, V/N and final D, but
excludes setup/import and the post-verification identity-file read/serialization.
W2 cached loading/carrier authoring is included in total but not a separately
clocked stage. The total-minus-sum-of-stage residuals for runs 3/4/5 are W1
221.4848 / 235.3365 / 238.5176 ms and W2 13490.6027 / 14630.7856 /
14531.7417 ms: loading, between-stage checks/reopens, observations and evidence
overhead, **not an isolated cache-loading measurement**. Preserve this
measurement lifecycle in candidate comparisons.

Median / median absolute deviation (MAD), runs **3–5 only**:

| Workload/stage | ms median / MAD | Allocated B median / MAD | Sampled peak private B median / MAD | Sampled peak working B median / MAD |
|-|-:|-:|-:|-:|
| W1/I | 1053.8189 / 24.6139 | 132618144 / 28912 | 122454016 / 2818048 | 185393152 / 2580480 |
| W1/M | 1664.8907 / 154.6175 | 89652880 / 50408 | 120180736 / 196608 | 202956800 / 176128 |
| W1/C | 4996.1272 / 45.0571 | 4197688696 / 1284256 | 111349760 / 544768 | 200527872 / 1269760 |
| W1/O | 3945.2857 / 182.6149 | 2935532800 / 165784 | 146911232 / 1220608 | 241160192 / 2146304 |
| W1/E | 1668.9404 / 154.0273 | 681551968 / 112224 | 203128832 / 1433600 | 308772864 / 983040 |
| W1/V | 125.6943 / 3.6189 | 63946304 / 5552 | 173318144 / 3481600 | 280625152 / 2273280 |
| W1/D | 5.1790 / 0.1122 | 4440 / 0 | 169943040 / 2875392 | 281112576 / 4485120 |
| W2/I | 34162.3539 / 1280.4146 | 28595928096 / 404512 | 8319799296 / 19226624 | 8360919040 / 19304448 |
| W2/M | 27382.7070 / 1537.1353 | 38659031912 / 807664 | 10804916224 / 454656 | 10862956544 / 1212416 |
| W2/C | 41136.7935 / 574.2883 | 57699902800 / 219984 | 11725479936 / 221184 | 11801841664 / 5734400 |
| W2/O | 53878.2341 / 752.7114 | 68974005416 / 13163696 | 13249765376 / 131047424 | 13299224576 / 140046336 |
| W2/B | 28708.5678 / 394.8977 | 32197289784 / 27337288 | 13921918976 / 105209856 | 13975666688 / 100175872 |
| W2/V | 3756.8178 / 121.1532 | 3539719760 / 30753224 | 12711817216 / 211099648 | 12756586496 / 186572800 |
| W2/N | 772.3330 / 26.9168 | 818909680 / 9880 | 12101885952 / 203988992 | 12150345728 / 181063680 |
| W2/D | 1.3513 / 0.0168 | 4456 / 0 | 12037750784 / 234430464 | 12087656448 / 211808256 |
| W1 net consumer + D | **10952.4363 / 275.6870** | — | — | — |
| W1 total including import | **13852.5000 / 530.4598** | — | — | — |
| W2 bounded consumer + D | **129123.2825 / 97.9593** | — | — | — |
| W2 total including import | **205595.9231 / 2987.0896** | — | — | — |

#### Memory and disposal observations (all byte values exact)

P = refreshed process-private bytes, G = `GC.GetTotalMemory(false)`,
S = observed stage peak private/working bytes, n = observations.
The test-local sampler requests observations every 25 ms and at entry/finish;
it can miss shorter peaks and observes process-wide, not attributable per-object
memory. No forced GC occurred in timing runs. Sampling callbacks and measurement
helpers allocate. `ProcessPeakWorkingSetBytes` in each retained stage JSON is
the separate process-lifetime high water, including W1 before W2; it is not S.
Clocks include the action but stage sampler setup/finish observations surround
it; net includes intervening measurement overhead.

| Run/stage | P entry → exit | G entry → exit | S private / working | n |
|-|-:|-:|-:|-:|
| 2/W1 I | 57532416 → 87920640 | 19138328 → 38481232 | 113246208 / 176713728 | 45 |
| 2/W1 M | 87920640 → 100339712 | 38497680 → 31776768 | 120205312 / 202575872 | 57 |
| 2/W1 C | 100458496 → 106741760 | 32426464 → 58286928 | 110432256 / 199204864 | 216 |
| 2/W1 O | 106741760 → 142323712 | 58328048 → 62245872 | 151121920 / 244772864 | 187 |
| 2/W1 E | 142516224 → 168144896 | 65570608 → 75735056 | 207949824 / 313790464 | 51 |
| 2/W1 V | 168144896 → 173912064 | 75743280 → 83473760 | 173916160 / 285958144 | 6 |
| 2/W1 D | 174108672 → 171925504 | 85869048 → 85877272 | 174112768 / 286470144 | 2 |
| 3/W1 I | 57286656 → 89210880 | 19158952 → 38284120 | 122454016 / 185393152 | 44 |
| 3/W1 M | 89210880 → 100376576 | 38300568 → 31634304 | 119808000 / 201957376 | 61 |
| 3/W1 C | 100433920 → 110006272 | 32284000 → 56067440 | 111349760 / 200527872 | 201 |
| 3/W1 O | 110006272 → 142188544 | 56108560 → 62659368 | 155123712 / 248647680 | 159 |
| 3/W1 E | 142315520 → 168214528 | 65985224 → 75365056 | 215764992 / 321507328 | 62 |
| 3/W1 V | 168214528 → 178888704 | 75373280 → 83000576 | 178929664 / 287690752 | 6 |
| 3/W1 D | 178995200 → 171442176 | 85395864 → 85404088 | 178995200 / 288178176 | 2 |
| 4/W1 I | 57143296 → 86777856 | 19073456 → 38314608 | 119635968 / 182812672 | 42 |
| 4/W1 M | 86777856 → 90296320 | 38331056 → 31713512 | 120377344 / 203132928 | 74 |
| 4/W1 C | 90603520 → 107290624 | 32363208 → 52935032 | 107806720 / 197025792 | 199 |
| 4/W1 O | 107290624 → 122806272 | 52967928 → 76581464 | 145690624 / 239013888 | 188 |
| 4/W1 E | 122810368 → 162738176 | 79903656 → 78497152 | 203128832 / 308772864 | 74 |
| 4/W1 V | 162738176 → 166895616 | 78505376 → 84679008 | 173318144 / 278351872 | 7 |
| 4/W1 D | 167067648 → 162103296 | 87072472 → 87080696 | 167067648 / 276627456 | 2 |
| 5/W1 I | 57257984 → 88092672 | 19002136 → 38336912 | 125874176 / 188755968 | 47 |
| 5/W1 M | 88092672 → 100012032 | 38353360 → 31143576 | 120180736 / 202956800 | 68 |
| 5/W1 C | 100069376 → 111017984 | 31793272 → 54870480 | 111894528 / 201797632 | 212 |
| 5/W1 O | 111017984 → 127303680 | 54903376 → 44909416 | 146911232 / 241160192 | 152 |
| 5/W1 E | 127303680 → 153350144 | 48236216 → 71021944 | 201695232 / 307789824 | 68 |
| 5/W1 V | 153350144 → 169836544 | 71030168 → 96110440 | 169836544 / 280625152 | 6 |
| 5/W1 D | 169943040 → 166289408 | 98505728 → 98513952 | 169943040 / 281112576 | 2 |
| 2/W2 I | 1996673024 → 8316272640 | 1618958600 → 6491543800 | 8316276736 / 8374542336 | 1374 |
| 2/W2 M | 8544948224 → 10120462336 | 7357450064 → 7337861784 | 10774470656 / 10846412800 | 1007 |
| 2/W2 C | 10102095872 → 11714928640 | 7337927576 → 10934961040 | 11714928640 / 11789070336 | 2151 |
| 2/W2 O | 12151316480 → 12755824640 | 11566305504 → 6876248720 | 13065232384 / 13140918272 | 3260 |
| 2/W2 B | 12738752512 → 12201222144 | 6876298064 → 9436888000 | 13478223872 / 13532319744 | 1152 |
| 2/W2 V | 12201218048 → 11642978304 | 9436896224 → 6158256496 | 12269760512 / 12335472640 | 144 |
| 2/W2 N | 11642974208 → 11538100224 | 6158264720 → 6833013768 | 11643179008 / 11711000576 | 32 |
| 2/W2 D | 11521155072 → 11503812608 | 7168063096 → 7168071320 | 11521163264 / 11590119424 | 2 |
| 3/W2 I | 2011369472 → 8560050176 | 1633314072 → 6275091888 | 8585621504 / 8608296960 | 1314 |
| 3/W2 M | 8799105024 → 10138808320 | 7141234800 → 7245987576 | 10848010240 / 10904666112 | 1290 |
| 3/W2 C | 10119921664 → 11778138112 | 7246053368 → 10923540696 | 11778142208 / 11841900544 | 1669 |
| 3/W2 O | 12138659840 → 12569276416 | 11555698392 → 6911064472 | 13015044096 / 13094834176 | 2203 |
| 3/W2 B | 12549664768 → 12441243648 | 6911113816 → 10455043816 | 13167976448 / 13231775744 | 1150 |
| 3/W2 V | 12441243648 → 11897888768 | 10455052040 → 6166407400 | 12500717568 / 12570013696 | 152 |
| 3/W2 N | 11897888768 → 11753902080 | 6166423848 → 6837715824 | 11897896960 / 11969282048 | 31 |
| 3/W2 D | 11803320320 → 11786563584 | 7153898008 → 7153906232 | 11803320320 / 11875848192 | 2 |
| 4/W2 I | 2010988544 → 8319799296 | 1634880312 → 6480989424 | 8319799296 / 8360919040 | 1424 |
| 4/W2 M | 8574840832 → 10145517568 | 7348074160 → 7338782096 | 10804461568 / 10862956544 | 1097 |
| 4/W2 C | 10124152832 → 11725479936 | 7338839664 → 10939327416 | 11725479936 / 11801841664 | 1617 |
| 4/W2 O | 12167598080 → 13378600960 | 11571223504 → 12762270952 | 13380812800 / 13439270912 | 2125 |
| 4/W2 B | 13361000448 → 12641828864 | 12762320296 → 9425288848 | 13921918976 / 13975666688 | 1200 |
| 4/W2 V | 12641828864 → 12101885952 | 9425305296 → 6171777768 | 12711817216 / 12756586496 | 147 |
| 4/W2 N | 12101885952 → 12062646272 | 6171785992 → 6846672984 | 12101885952 / 12150345728 | 33 |
| 4/W2 D | 12037750784 → 12018642944 | 7163067072 → 7163075296 | 12037750784 / 12087656448 | 2 |
| 5/W2 I | 2022813696 → 8300572672 | 1635255408 → 6460582176 | 8300572672 / 8341614592 | 1366 |
| 5/W2 M | 8547262464 → 10141634560 | 7352057056 → 7339578088 | 10804916224 / 10861744128 | 1035 |
| 5/W2 C | 10121728000 → 11725258752 | 7339635656 → 10938178136 | 11725258752 / 11796107264 | 1647 |
| 5/W2 O | 12163727360 → 12877348864 | 11567406816 → 6877427576 | 13249765376 / 13299224576 | 2156 |
| 5/W2 B | 12859834368 → 12676120576 | 6877476920 → 9395164408 | 14027128832 / 14075842560 | 1133 |
| 5/W2 V | 12676120576 → 12525985792 | 9395172632 → 5965456392 | 13058334720 / 13087866880 | 158 |
| 5/W2 N | 12525985792 → 12382265344 | 5965464616 → 6640436952 | 12525985792 / 12559499264 | 33 |
| 5/W2 D | 12361719808 → 12343349248 | 6975538792 → 6975547016 | 12361719808 / 12397387776 | 2 |

DB lengths agree across all four observations (bytes, stage medians identical,
MAD zero): W1 I **581632**, M **921600**, C **9596928**, O/E/V/D **11603968**;
W2 I **290873344**, M **291401728**, C **291655680**,
O/B/V/N/D **390873088**. These include index/output storage, not merely payloads.
Process-lifetime peak working bytes at final disposal for runs 2/3/4/5:
W1 **318963712 / 323493888 / 313892864 / 313978880**;
W2 **13532319744 / 13232922624 / 13975674880 / 14075842560**.
There was no OOM/resource failure in these runs; no memory improvement is claimed.

The large W2 post-disposal observations are **not a live-object census**:
loaded DefinitionCollections, returned built outputs/negative witnesses and
the GC's reserved/uncollected memory can survive while the fixture method is
still returning. Sources show import/arrangement definitions remain local
through this lifecycle; explicit database disposal and pool clearing execute,
but that alone cannot prove interpreter/cache reachability after return or
reused-object release. The attempted separate weak-reference census never
executed. No claim of zero retained operation/pair/payload references, bounded
live graph size, or explained preparation-only peak is made. That lifetime
acceptance evidence remains incomplete alongside the missing counts.

#### Run 6 first failure, counting points and exact restoration

The separate instrumented build added only temporary local observation hooks
to the three expressly owned production files, plus a temporary test-local
stage dictionary/weak-reference collector. Intended actual call sites:

| Cost | Temporary point / distinction | Actual run-6 observation |
|-|-|-|
| Catalogs / fallback roots | `XVerStructureCatalog` constructor plus immediate caller stack frame (to identify ElementComparer fallback construction); `ResolveRoot` request/hit/new root | **Unmeasured** |
| Inventory | `DefinitionInventoryStore.ValidateInventory` entry; each actual loop call to `StructureDefinitionFactsStore.ReadDefinition` separately from all `ReadDefinition` parses | **Unmeasured**; no static 84 arithmetic substituted |
| Lazy payloads | `ReadPayload` request and cache-miss parse; `ReleasePayloads` call/count before clearing | **Unmeasured**; not conflated with transient inventory parses |
| Facts | Actual facts constructor/context, `Definition` and keyed `AtUse` request/cache-hit/creation, relational `AtUse` requests, `ForDefinition` branch creation and `Dispose` | **Unmeasured** |
| Directional proofs | `ClassifyPair`, each forward/reverse `ClassifyRequiredUses`, plus native versus emitted-owner `Prove` contexts | **Unmeasured**, including reverse |
| Emitted-owner projection | `ProveInOwner` requests and actual nonnull-emitted-owner `Prove` calls | **Unmeasured**, not assumed zero from no returned projections |
| Lifetime | Weak references only to created catalogs/facts; intended post-disposal and outside-timing post-return collection checkpoints | **Not executed** |
| Raw operand audits | Baseline has no prepared exact raw-audit implementation | **Not present**; no invented count/rows/bytes/time |

The attempted test collector would persist `baseline-counts.json` in its own
unique fixture directory, partitioning actual stages from arrangement and
between-stage assertion work; no public telemetry, environment switch,
alternate engine or permanent benchmark subsystem was intended or retained.
Compilation stopped **before any fixture construction**, so no such file,
new DB, count directory, runtime count or lifetime observation exists.

Complete new failure diagnostic:

```text
E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Comparison\Models\ProfileUseFacts.cs(345,60): error CS0103: The name 'comparisons' does not exist in the current context [E:\ai\git\fhir-codegen\src\Fhir.CodeGen.Comparison\Fhir.CodeGen.Comparison.csproj]
Attempt 4 run 6 SEPARATE instrumented counts, timings excluded: exit=1; commandMilliseconds=20487.6321
```

Diagnosis, not a repair: temporary constructor-count instrumentation converted
`ProfileUseFacts(XVerStructureCatalog catalog, int sourcePackageKey,
int targetPackageKey, IReadOnlyList<DbElementComparison>? comparisons = null)`
from a primary constructor to a conventional constructor, storing catalog and
package keys and initializing `_correspondenceComparisons`. It overlooked
the original primary parameter's additional captured use in
`ClassifyRequiredUses`' local `Decide` function:
`foreach (DbElementComparison comparison in comparisons ?? [])`.
That body therefore no longer compiled. No production semantic regression is
established; no count result exists. No extra field/fix or retry was attempted.

Failed instrumented source SHA-256 identities (before restoring):

- Test: `12402C4FB49AC7BBCF7C0C7E778D8388636595F08EEE82D6C8C89E3CA5888628`.
- DbStructureDefinitionFacts:
  `500B6322D2A9805119D69653E105A160AED54A2172C8AE622011C946762E003D`.
- XVerStructureCatalog:
  `04336ACA464B8BB2AAC2E0D5B3B78A83C92D01DB8FD0181E3E20DC76B9A114D2`.
- ProfileUseFacts:
  `F7F8FA531515C5F56713AFF24E7A90A7491B758BCB5B22EB166B487D877F8A79`.

All observation hooks and the temporary test collector were then **removed**
using inverse patches, not repaired or retried. Final source matches the
successful measurement harness hash above. A read-only Python comparison
enumerated every tracked file under `src/Fhir.CodeGen.Comparison` with
`git ls-tree -r --name-only d749a7b5e... -- src/Fhir.CodeGen.Comparison`,
obtained each baseline's checkout-filtered bytes with
`git cat-file --filters d749a7b5e...:<path>`, and compared current raw bytes:
**63 files checked, zero differences**. Exact restored owned production hashes:

| File in `Models/` | Bytes / CRLF / bare LF | SHA-256 |
|-|-|-|
| `DbStructureDefinitionFacts.cs` | 24865 / 496 / 0 | `154055b37e047e22a202949616ae29024ad85d963558a399de5d1d807a93ac07` |
| `XVerStructureCatalog.cs` | 31808 / 708 / 0 | `ccacf4f5afcebe85737400db8bda9e87c5e471b071b1c72543e2821f4163f279` |
| `ProfileUseFacts.cs` | 134667 / 2165 / 0 | `5c21a62723f7721a3e12e6f25374abe1d941cd9f07a233937b64a34e0f0b7449` |

`git diff --check` passed; staged diff stayed empty, HEAD stayed
`d749a7b5e88889e3d5ac7b49e781de478b87e499`, and only the owned harness/spec
remain modified. No correctness command followed the failed gate: identical
restored source bytes do not replace the required uninstrumented rerun.
Parent owns plan/status integration, further recovery disposition, final
restored full-class verification and any phase commit. No attempts 5/6 were
automatically consumed, no later phase started, and no success/phase-completion
claim is made from the timing distribution alone.

### Prepared-operation recovery work attempt 5 (2026-11-18; counts and lifetime)

This append-only entry records the separately authorized **attempt 5/6**, not
a retry inside attempt 4 or consumption of attempt 6. The accepted attempt-4
warm-up and observations, including W1 net median/MAD **10952.4363/275.6870 ms**
and W2 bounded net **129123.2825/97.9593 ms**, remain unchanged. This dispatch
ran one separate instrumentation test successfully and removed every temporary
production/test hook. The parent still owns the required restored full-class
correctness gate, plan integration and phase commit; this is not Phase 1
completion or optimization acceptance.

#### Diagnosis, command and source identities

Before verification, the implementer read the complete native implementer
instructions, root `AGENTS.md`, Phase 1 and applicable recovery/measurement
history, the three production files and actual workload callers. The attempt-4
CS0103 was caused by rewriting a primary constructor, not by a missing original
production dependency. All four original primary parameters are captured:
`catalog`, `sourcePackageKey`, `targetPackageKey`, and `comparisons`. In
particular, `comparisons` supplies both `_correspondenceComparisons` and the
local `ClassifyRequiredUses.Decide` loop. The temporary attempt-5 implementation
preserved that constructor, all captures and initialization order. Its only
constructor-count change in `ProfileUseFacts` was an identity wrapper around
the existing `_active = []` initializer. `XVerStructureCatalog` retained its
original constructor arguments, selectors, checks and assignments.

Static inspection caught and moved a newly inserted temporary helper out of
the inventory method before the first verification; no compiler/test command
was used for that inspection. No preliminary build, root smoke gate, full-class
gate or repeat timing distribution was run. **Run 1**, including its embedded
build, was exactly:

```powershell
dotnet test src\Fhir.CodeGen.Lib.Tests\Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts"
```

Result: **exit 0; 1 passed, 0 failed, 0 skipped; runner 3m59s;
command 266496.0345 ms**. SDK 11.0.100, default Debug/net11.0, unchanged
production baseline `d749a7b5e88889e3d5ac7b49e781de478b87e499`, testhost PID
58696. Restore was up to date; only the previously observed NU1510
Logging.Abstractions warnings and documented CS3021 appeared. The synchronous
PowerShell wrapper used a Stopwatch, `2>&1 | Tee-Object` to the evidence log,
captured/returned `$LASTEXITCODE`, and appended the elapsed/result line.
No install, additional runner switch, CLI benchmark, timeout, failure or
verification retry occurred.

The original and executed instrumented byte images are retained under
`temp\xver-attempt5-count-evidence-7f939c2180b945a693b85739a63a7497\`.
Each of the four source filenames has `.original` and `.instrumented` appended.
Exact executed SHA-256 values:

| Instrumented filename | SHA-256 |
|-|-|
| `XVerPreparedOperationTests.cs.instrumented` | `005bf073bd13c3ddbbe2f0b51c18faa85fd9a78f3a6b0b857306ce1f56137347` |
| `DbStructureDefinitionFacts.cs.instrumented` | `a2f3d918cb5e57a3e817a4c08ea4ca7a8863c920d58b7893d41614d8d39e3281` |
| `XVerStructureCatalog.cs.instrumented` | `905ab23b5a02b7f2f5ebea989a7a1588c7992416e1a92e1ade6312c1165b80c4` |
| `ProfileUseFacts.cs.instrumented` | `9ddb838c2082c3376ca426a70f5b9340409caf93506dc770789a76d9f047eb4e` |
| `run1-output.log` (4097 bytes) | `c24954911b5899aaae43873d61196d174d4d1bc7124d6f94cfd5d732c0a82175` |

Temporary sites refer to those retained instrumented images, not restored
production line numbers:

- Facts store lines 131/134: every `ReadDefinition` entry and successfully
  parsed payload; inventory lines 335/390/392: every full semantic validation
  and every transient inventory parse/result. The temporary weak/count helper
  is at lines 166-266.
- Catalog lines 33-43: constructor entry and actual stack frames; 78-86:
  completed catalog owner, resident row objects and raw normalized strings/
  UTF-8 bytes; 255/258: root   requests/hits; 599/609: lazy payload cache misses/
  objects; 621-627: payload-release calls, pre-clear counts and post-clear size.
  Fallback constructions are counted only when an actual captured frame is
  `ElementComparer.<DoElementComparisons>g__ResolveRoot|12_7`; the retained
  caller histogram corroborates the 208 observed calls.
- Profile facts line 174: constructor identity wrapper; 266-268: actual
  directional classification plus source/target and supplied/null comparison
  context; 438: `ProveInOwner` entry; 621-625/689-702: use requests/hits,
  created branches/uses and fact graphs; 708-716: disposal and cache sizes;
  722-724: definition branches; 733-760: actual proof calls, emitted-owner
  calls and constructed decision objects/edges; 1233-1242/1334: definition
  requests/hits/misses and successfully cached effective graphs.
- Test lines 63-72 reset between W1 and W2 and take caller-after-return
  censuses; existing `Measure` helpers label stages independently, including
  setup and between-stage checks. Lines 140/448 take post-operation forced-GC
  censuses with ordinary input/output locals deliberately still live.
  Lines 517-601 implement scalar-only reports, weak censuses and owner/
  connection inspection. Entry observations are at 389/399/435 and
  1582/1655/1690; returned build objects are observed at 1708-1713.

These are actual callpoint counts, not schedule-derived arithmetic. Counts
are separate dictionaries per stage; setup is never subtracted from an
ambiguous total. Every normal workload assertion, map, output, direction and
typed negative remained enabled. W1 still completes 20 comparison/outcome and
12 supported export directions; W2 still performs all retained-core import,
four reopens, actual bounded Range consumers and both expected-rejection
witnesses. Instrumentation timings/allocations in the new stage files include
probe overhead and are **not additional accepted timing observations**.
Forced collections occur only after `operation.Stop()` or after the workload
returns in this separate run, never in the accepted attempt-4 timing runs.
No semantic-dump serialization was requested.

#### Retained successful evidence and input agreement

All paths below are under repository-local `temp\`; no success/failure
directory, old capture or accepted sample was cleaned up.

| Workload | Directory | Count/lifetime report SHA-256 (bytes) |
|-|-|-|
| W1 | `xver-prepared-512d7f4c5374494e8b96e1117e351dbc` | `274a7cf4ee8ec67054b1a5ecbdad4029a5ee52e0785e44e86521916f5f62cf56` (150536) |
| W2 | `xver-prepared-c7d29b640b8d43b28d1ca873706f9143` | `beb56e2cc626cfa2f562464ccca1ce6f904eb07847dc373733524e1ecabc1310` (336399) |

The report filename is `attempt5-counts-lifetime.json`; it contains all
per-stage counters, constructor caller/direction histograms, weak checkpoints
and scalar field/handler observations, not just the tables below.

| File | W1 SHA-256 (bytes) | W2 SHA-256 (bytes) |
|-|-|-|
| `measurement-identity.json` | `747743ae65beca1bf08e3b235ccc6df8cfee8889be3833a876b803377fe16ed9` (15694) | `439200b8a5621fbdb51346209b77db6507c237394ef123eb9b636f6c08504105` (5091) |
| `stage-costs.json` | `c7601c18812d5c6d1c1aa9bddd502ab853b4a917d27edc5df42a627d70ba9a47` (3555) | `2df4d055616463967270e7d2a726de1d0f9e4eb3f0876ac29b8fe4bff2a4710a` (4233) |

Both `stage-costs.json` files have `Completed=true`. A field-by-field comparison
against accepted attempt-4 run 5 verified identical exact package rows,
repetition count, comparator terminology version, max step, script flag, all
39 table cardinalities, raw payload identity/bytes, prohibition JSON bytes and
every local map path/length/hash. W1 retains 6185 relational rows, 103112 raw
normalized UTF-8 bytes and 112500 prohibition bytes; W2 retains 312573 rows,
123867864 normalized bytes and 98819836 prohibition bytes. Payload SHA-256
remains respectively `7c67389b93345fdcf242d24a50edc332dba5295624e4ca1200249f16d5c300ff`
and `b8d0d6a31ac493d0fc75cd63a37282a4edaf863eb51f2fd0b1fe43b63802cad6`.
DB file sizes after disposal are 11603968 and 390873088 bytes. Input/cache/map
recipes and framed hashes from attempt 4 remain authoritative.

#### Observed catalog, root, inventory and parse counts

I = full semantic inventory validations; IP = every transient inventory parse;
LP = lazy catalog payload parses; RP = all `ReadDefinition` calls. Successful
catalog counts equal constructor-entry counts. Root hits are included in root
requests, not additional requests.

| Workload/stage | Catalogs | Fallback-root catalogs | Root requests / hits | I | IP | LP | RP |
|-|-:|-:|-:|-:|-:|-:|-:|
| W1 definitions/import and root assertions | 3 | 0 | 940 / 750 | 1 | 100 | 100 | 200 |
| W1 fixture maps | 5 | 0 | 0 / 0 | 3 | 300 | 0 | 300 |
| W1 actual compare | 314 | 208 | 62928 / 61860 | **84** | 8400 | 4640 | 13040 |
| W1 actual outcomes | 202 | 0 | 49992 / 47584 | 82 | 8200 | 1568 | 9768 |
| W1 actual export | 51 | 0 | 29280 / 28300 | 1 | 100 | 912 | 1012 |
| W1 frozen verification | 2 | 0 | 65 / 50 | 0 | 0 | 15 | 15 |
| **W1 all stages** | **577** | **208** | **143205 / 138544** | **171** | **17100** | **7235** | **24335** |
| W2 full retained import | 2 | 0 | 37935 / 36621 | 1 | 1257 | 582 | 1839 |
| W2 retained-inventory checks/reopen after import | 1 | 0 | 1 / 0 | 0 | 0 | 0 | 0 |
| W2 local maps | 6 | 0 | 50505 / 49722 | 3 | 3771 | 873 | 4644 |
| W2 actual Range comparison | 7 | 0 | 152936 / 151441 | 4 | 5028 | 1625 | 6653 |
| W2 comparison assertions/reopen | 1 | 0 | 0 / 0 | 0 | 0 | 1 | 1 |
| W2 actual Range outcomes | 10 | 0 | 238058 / 236126 | 4 | 5028 | 2046 | 7074 |
| W2 readiness/write-free build | 7 | 0 | 187548 / 186404 | 1 | 1257 | 1173 | 2430 |
| W2 frozen verification | 1 | 0 | 724 / 551 | 0 | 0 | 173 | 173 |
| W2 expected-rejection witnesses | 1 | 0 | 254 / 239 | 0 | 0 | 15 | 15 |
| **W2 all stages** | **36** | **0** | **667961 / 661104** | **13** | **16341** | **6488** | **22829** |

Other between-stage/disposal events have zero values in these columns.
`RP = IP + LP` in both workloads; no unclassified parser calls occurred.
The bold 84 is now an observed W1 compare count, not a universal static formula.
Inventory still parses every retained definition on every full validation.
W2's zero fallback-root construction count characterizes its bounded path; it
does not contradict W1's actual fallback work.

The observed entry hooks each fired once for W1 compare/outcomes/export and
once for W2 bounded comparison/outcomes/readiness-build. **OBSERVED prepared
raw exact audit replay count: 0 on each of those baseline paths.** Basis:
these actual entries executed the original unprepared code, and `d749a7b5e`
has no prepared-operation/evidence replay producer at all. This is a path
observation, not a counter attached to an invented baseline audit function.
Audit rows, JSON bytes and replay time are **not applicable**, not fabricated
measurements or a claim that ordinary semantic validation performs no reads.

#### Observed effective facts, directional work and graph sizes

Instances count initializer executions; definition misses are counted after
the cache lookup misses, and uses at actual construction. Uses and branch
graphs differ because profile alternatives can create multiple branches.

| Workload/stage | Facts instances / disposals | Definition requests / hits / misses | Use requests / hits / creations | Directional computations | Proof calls |
|-|-:|-:|-:|-:|-:|
| W1 setup/root assertions | 5 / 5 | 205 / 105 / 100 | 10 / 0 / 10 | 0 | 0 |
| W1 compare | 400 / 400 | 12680 / 7520 / 5160 | 2120 / 120 / 2000 | 40 | 1640 |
| W1 outcomes | 108 / 108 | 15280 / 12672 / 2608 | 2816 / 240 / 2576 | 80 | 2568 |
| W1 export | 60 / 48 | 9120 / 7584 / 1536 | 1680 / 144 / 1536 | 48 | 1536 |
| **W1 total** | **573 / 561** | **37285 / 27881 / 9404** | **6626 / 504 / 6122** | **168** | **5744** |
| W2 comparison | 4 / 4 | 39627 / 38942 / 685 | 5244 / 3368 / 1876 | 2 | 4952 |
| W2 outcomes | 5 / 5 | 79198 / 77868 / 1330 | 10480 / 6736 / 3744 | 4 | 9900 |
| W2 readiness/build | 5 / 4 | 79198 / 77868 / 1330 | 10480 / 6736 / 3744 | 4 | 9900 |
| W2 negative witnesses | 2 / 2 | 27 / 9 / 18 | 0 / 0 / 0 | 0 | 3 |
| **W2 total** | **16 / 15** | **198050 / 194687 / 3363** | **26204 / 16840 / 9364** | **10** | **24755** |

All omitted stages have zero effective-fact/proof events. W1 comparison
computes each of 20 directions twice; outcomes four times. Export computes
both directions for each scheduled pair at readiness and again at
construction (48 computations, with the exact direction histogram retained).
W2 computes 5->4 and 4->5 once each in comparison, then twice each in outcomes
and build. Every counted directional computation received supplied comparison
rows; comparison-free `Prove` work is counted separately as proof calls.
`ProveInOwner` entries and `Prove` calls with a nonnull emitted owner were
**observed zero in every stage**. Both actual hooks existed; neither workload
required owner-specific projection proofs on this baseline. This is not a
claim that those obligations can be skipped or that a nonzero projection
workload was measured.

Actual constructed graph topology below counts dictionary/array **edges**,
not deduplicated objects or retained managed bytes. A definition graph and
several decisions can reference the same immutable element. DG/DE =
successfully cached effective-definition dictionaries/element edges; UG/UE =
created effective-use branch dictionaries/element edges; R/N/reasons =
required/native fact edges and reason edges on actual proof objects.

| Workload/stage | DG / DE | UG / UE | Proof objects; R / N / reasons | Restriction JSON UTF-8 bytes, definition / use graphs |
|-|-:|-:|-:|-:|
| W1 setup | 100 / 315 | 10 / 60 | 0; 0 / 0 / 0 | 4306 / 3140 |
| W1 compare | 5160 / 21600 | 2480 / 11600 | 1640; 8640 / 7280 / 368 | 260984 / 405888 |
| W1 outcomes | 2608 / 9568 | 3056 / 13696 | 2568; 13488 / 10768 / 720 | 145576 / 486344 |
| W1 export | 1536 / 5616 | 1824 / 8160 | 1536; 8064 / 6432 / 408 | 82668 / 287136 |
| W2 comparison | 685 / 26106 | 1888 / 13736 | 4952; 39627 / 36694 / 7735 | 12028352 / 9167097 |
| W2 outcomes | 1330 / 52004 | 3768 / 27408 | 9900; 79222 / 73356 / 15470 | 23935520 / 18279870 |
| W2 readiness/build | 1330 / 52004 | 3768 / 27408 | 9900; 79222 / 73356 / 15470 | 23935520 / 18279870 |
| W2 negative witnesses | 18 / 112 | 0 / 0 | 3; 24 / 24 / 43 | 59846 / 0 |

All constructed proof child/projection edge counts were zero. Full type,
restriction and origin edge counts are retained per stage in the raw reports.
Restriction sizes sum exact `JsonElement.GetRawText()` UTF-8 bytes for each
graph reference; shared/repeated restrictions can therefore be counted more
than once. These numbers must not be presented as unique allocation sizes.

For comparison with the actual retained serialized result image, a separate
read-only Python `sqlite3` pass opened only these two disposed fixture DBs
with `mode=ro`. It streamed rows whose field contains
`"ProfileDecisionFormat":`, parsed each document and recursively counted
`Decisions`/`Children`, `RequiredFacts`, `NativeFacts` and `Reasons`:

| Workload / persisted field | Documents | UTF-8 bytes | Decision nodes | Required / native / reason / child edges |
|-|-:|-:|-:|-:|-:|
| W1 `ElementOutcomes.ExtensionProhibitionReason` | 8 | 112500 | 8 | 48 / 48 / 16 / 0 |
| W1 `ElementTypeComparisons.TechnicalMessage` | 360 | 3554728 | 360 | 1920 / 1920 / 16 / 0 |
| W2 `ElementOutcomes.ExtensionProhibitionReason` | 129 | 98819836 | 2353 | 18835 / 16834 / 3845 / 0 |
| W2 `ElementTypeComparisons.TechnicalMessage` | 2 | 81368 | 2 | 16 / 16 / 0 / 0 |

This measures serialized retained graph shape, not live deserialized-object
ownership. No 857 MB characterization capture was loaded or rewritten.

#### Weak lifetimes, release sites and observable retention

The temporary collector stores only counters, strings, numeric field
descriptors, weak references and a `ConditionalWeakTable` with unrelated
marker values. It never keeps production objects in a strong static list,
event handler or callback. Each stage/category retains the **first 128 weak
targets**, including actual parsed Firely payloads, catalog owners/rows/raw
strings, effective dictionaries/elements/uses, proof objects and working
facts. Constructor counts are exhaustive; lifetime probes are sampled and
not a unique-object census. Facts first become object probes on actual
method use/disposal; unused representation-index facts are visible through
test-local owner inspection instead.

After consumers but before final disposal, forced collection runs twice
around pending finalizers. The W1 synchronous workload has a no-inline
boundary; the final W1 check is after its entire stack frame returns.
The final W2 check is after awaiting its completed workload, with a separate
no-inline census/collection helper. The W2 earlier checkpoint explicitly
keeps `built` alive across collection, distinguishing returned output from
preparation accidentally rooted by a probe.

| Observation | Actual sampled references alive / sampled |
|-|-|
| W1 after consumers, DB and verification locals still live | Catalog owners 0/317; parsed payloads 0/655; catalog payloads 0/499; facts owners 0/289; effective-definition graphs 0/484; use graphs 0/394; proofs 0/384; connection 1/1 |
| W1 caller after return and forced GC | **0/10595 weak slots alive** across all categories, including both observations of the connection |
| W2 after consumers, returned build deliberately live | Catalog owners 1/36; returned build/resource/representation index each 1/1; actual representation-index facts 1/1; effective-definition graphs 4/402 and elements 32/496; proofs 0/387; parsed/catalog payloads each 0/784; observed connections 2/5 |
| W2 caller after await and forced GC | **0/11479 weak slots alive** across all categories, including returned outputs and six connection observations |

Weak slots can refer to the same target more than once. The four surviving
W2 effective dictionaries before return are consistent with the still-live
local persisted negative witnesses, which retain required/native fact
dictionaries by reference. No per-target identity map was retained to prove
the unique owner of each surviving sampled dictionary. They all disappeared
after return; they are not evidence of reuse retention. Nor does zero sampled
row survival imply no row retention: the first-128 row samples can come from
earlier catalogs rather than the last still-live catalog.

The latter limitation was checked directly at the actual returned/reusable
objects, without retaining those objects in the report:

- After W2 comparison, `ElementComparer._elementTypeComparer._catalog` still
  owns 1257 structures, 54266 elements and 1257 normalized facts; its
  `_payloads`/`_payloadElements` are both empty and its root cache has 10 rows.
  After W2 outcomes, `ElementOutcomeGenerator._catalog` retains the same
  relational/JSON inventory with empty payload caches and two roots.
  These baseline reusable instances **do retain relational rows/raw JSON**
  while the caller keeps the object. No claim of fresh-call authority or
  universal release-on-return is made.
- After W2 build, the builder has its own full catalog (one cached root),
  one `_profileDecisions` dictionary entry and a representation index. The
  index owns another complete catalog plus 25927 pair elements and 546 pair
  structures; its `_profileFacts` has zero definition/use cache entries.
  All inspected catalog parsed-payload caches are empty. The returned
  `BuiltDefinitions.Representations` deliberately keeps that index, its
  full normalized rows/JSON and its DB reference alive even after the
  builder itself is collected. The single returned profile and empty
  projection/extension lists retain their normal output contract.
- Each complete catalog contains 100/5/315/100/370 W1 or
  1257/5/54266/1257/67624 W2 structure/package/element/facts/type row
  references. Its raw normalized JSON totals 103112 or 123867864 UTF-8 bytes.
  The raw reports count those actual resident references/bytes at every
  construction, not peak or deduplicated residency. A live W2 returned
  representation catalog thus explains real retained raw inventory even
  when its sampled row subset reports zero alive.

At actual release sites W1 made 564 catalog-payload releases, clearing 7235
payloads/7235 payload-element maps; W2 made 19, clearing 2414 payloads/2413
payload-element maps. Every observed post-clear payload count was zero.
W1 disposed 561 facts instances with 6122 uses and 9404 definitions; W2
disposed 15 with 9364 uses and 3363 definitions. Every post-disposal use/
definition cache count was zero. The 12 W1 and one W2 extra constructed
facts instances belong to the unchanged representation-index construction
pattern, not a newly added disposal fix. The W2 inspected instance was empty.
Not every W2 lazy payload is explicitly cleared: other catalogs become
unreachable. Explicit release counts are not inflated into all-object
disposal guarantees.

Active and disposed `SqliteConnection` objects were inspected for delegate
fields across their inheritance chain: W1 Open/Closed and W2 Open plus all
five Closed observations had **no delegate-valued fields**. No observer was
attached. Prepared-operation owners, pair packets or lifecycle subscriptions
do not exist on this baseline, so their counts/detachment checks are **not
applicable**. This reflection observation is not proof about hidden native
provider state or future connection lifetime. After whole-workload return
all probed connection objects were collectible.

Process-memory observations reinforce the distinction, not a leak inference:
W1 managed/private/working bytes were 36075984/160059392/274063360 before
disposal after forced collection, and 23482568/122527744/237277184 after
return. W2 was 4932452088/12303163392/12344348672 with ordinary core-input
locals and returned output deliberately live, then
27378976/12065034240/12130701312 after return and forced collection.
At DB-disposal checkpoints without another forced GC the W1/W2 managed
readings were 37607576/5269492728. JIT/async locals and returned values are
therefore not mistaken for reusable-object leaks. Large remaining private/
working bytes are not assigned to a live graph merely because the process
retains reserved/native memory. Exact unique managed graph bytes and a full
native/CLR heap partition were not measured. The count-run sampled private
peak reached 14577987584 bytes, with no OOM/resource failure; it is not an
uninstrumented baseline memory sample or a memory improvement.

#### Restoration and handback boundary

Inverse patches removed all temporary test/production edits. The complete
Comparison tree was compared as raw bytes against
`git cat-file --filters d749a7b5e...:<path>` for every path from
`git ls-tree -r --name-only`: **63 files, zero differences**. No untracked
Comparison source exists. Each restored owned source also exactly matches
its saved dispatch `.original` byte image:

| Restored file | Bytes / CRLF / bare LF | SHA-256 |
|-|-|-|
| Test harness | 102570 / 1739 / 0 | `66d72d6631ecada5c4611906adef5f3b16e9e1db7e09ad1780b787ae2a699e19` |
| `DbStructureDefinitionFacts.cs` | 24865 / 496 / 0 | `154055b37e047e22a202949616ae29024ad85d963558a399de5d1d807a93ac07` |
| `XVerStructureCatalog.cs` | 31808 / 708 / 0 | `ccacf4f5afcebe85737400db8bda9e87c5e471b071b1c72543e2821f4163f279` |
| `ProfileUseFacts.cs` | 134667 / 2165 / 0 | `5c21a62723f7721a3e12e6f25374abe1d941cd9f07a233937b64a34e0f0b7449` |

The successful measured harness/net contract has **no persistent change**;
candidate timing must retain attempt 4's exact W1-then-W2 order, clocks,
verification/negative witnesses, disposal and semantic-dump exclusion.
The temporary count run is not comparable timing evidence. `git diff --check`
and empty-index/scope checks passed after restoration; only the pre-existing
owned harness/spec edits remain, plus this specification appendix. HEAD stays
`d749a7b5e88889e3d5ac7b49e781de478b87e499`. No instructions, policy, source
report, approach/analysis, old slot, dependency, schema, public API or other
tracked path was edited. No stage, commit, push, PR, GitHub write, nested
agent, additional test command or cleanup occurred. The last successful
verification in this dispatch is **attempt 5 run 1, instrumented**; restored
source identity is not the parent's still-required uninstrumented full-class
correctness gate.

### Phase 7 candidate measurements (2026-11-19; net acceptance fails)

This is **partial Phase 7 evidence in cumulative work dispatch 9/10**, not a
new attempt, baseline reset, phase completion, or permission to waive a gate.
Candidate production is the supplied clean Phase 6 commit
`aa290477b09ac4bc48fc347993f295cea678012b`. Root `AGENTS.md` supplied commands,
conventions and the uniform policy; no agents were invoked. The source report,
selected approach, plan, exporter specification and preceding Phase 1 protocol
were read without modification. The frozen Phase 1 baseline remains authoritative.

#### Workload identity and command ledger

The actual workload/clock/assertion bodies are unchanged from the saved Phase 1
`XVerPreparedOperationTests.cs.original` under
`temp/xver-attempt5-count-evidence-7f939c2180b945a693b85739a63a7497/`.
Read-only member-text comparisons (line-ending normalized for this comparison
only) found identical `IdenticalWorkloadsReportStageCosts`, `RunSyntheticPipeline`,
`RunRealCorePipeline`, `SyntheticDefinitions`, `WriteSyntheticMaps`,
`AssertEffectiveRootTypeUses`, `AssertGeneratedResource`, `AssertSyntheticSemantics`,
`AssertNegativeDocuments`, `AssertCoreRows`, `AssertCoreNegativeWitnesses`,
`RecordMeasurementIdentity`, `CompareRangeElements`, `GenerateRangeOutcomes`
and `BuildRangeDefinitions`. The surrounding test file has accumulated Phase 2–6
tests; its candidate SHA-256 is
`6a067b36f19de26d148e4e8cda3f8fc1e4f8e9254a42e3ee92a3315ec3cc077c`.
The original Phase 1 harness SHA-256 remains the one recorded above, not this hash.

W1 still runs all 20 comparison/outcome directions and 12 scripts-off actual
vocabulary/structure exports with three repeated groups, max step 4, five
authored releases, empty normalized root types and independent comparator
terminology `1.0.0`. The eight adjacent direct-map keys and twelve transitive
null-key provenance assertions remain unchanged. W2 still imports the complete
five pinned real cores plus two carriers (1,257 definitions/facts), performs
bounded Range consumers with four reopens and both actual-core rejection
witnesses. It is not whole-core actual CLI throughput.

Every run 1–5 used exactly:

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.IdenticalWorkloadsReportStageCosts"
```

Each command was synchronous and bounded, with a 600-second initial wait and
the **same** shell subsequently read through completion; no command was
restarted on reaching that wait. A PowerShell Stopwatch printed command wall
time and returned the actual exit code. No output redirection, logger switches,
installs, external DB/IG access, cleanup or parallel memory-heavy tests occurred.
All runs used default Debug/net11.0, runtime `.NET 11.0.0`, X64, OS
`Microsoft Windows 10.0.26300`. The shared host was not exclusively reserved;
no affinity/cold-cache guarantee is made.

| Run | Role | Exit / passed / failed / skipped | Command ms | Runner duration |
|-|-|-|-:|-|
| 1 | Discarded paired warm-up | 0 / 1 / 0 / 0 | 614019.5150 | 9m52s |
| 2 | Accepted observation 1 | 0 / 1 / 0 / 0 | 660760.3633 | 10m40s |
| 3 | Accepted observation 2 | 0 / 1 / 0 / 0 | 628490.8866 | 10m09s |
| 4 | Accepted observation 3 | 0 / 1 / 0 / 0 | 612100.0827 | 9m53s |
| 5 | Separate temporary actual-site counts/lifetime | 0 / 1 / 0 / 0 | 605495.0971 | 9m47s |

Restore was up to date; these commands emitted the previously observed NU1510
Logging.Abstractions warnings and documented CS3021, with no compiler or test
failure. After exact hook restoration, run 6 uses the requested combined selector:

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName~XVerPreparedOperationTests|FullyQualifiedName~XVerProfileCatalogTests|FullyQualifiedName~XVerQuantityProfileTests|FullyQualifiedName~UnsafeCodeProfileTests"
```

Run 6 completed **exit 1, 548 passed, 1 failed, 0 skipped, 549 total**;
command wall **3569018.4668 ms**, runner **59m08s**. This is a red restored
correctness gate, not inferred success from run 5.
At runner 00:38:20.55, the sole reported failure was
`InvocationAndBuildConfigurationChangesCannotCertifyAStaleSchedule(mutation:
"output", duringBuild: False)` (3 s), after the test reached `Completed=true`:

```text
System.IO.IOException : The process cannot access the file 'snapshot.sqlite' because it is being used by another process.
   at System.IO.FileSystem.RemoveDirectoryRecursive(String fullPath, WIN32_FIND_DATA& findData, Boolean topLevel)
   at System.IO.Directory.Delete(String path, Boolean recursive)
   at Fhir.CodeGen.Lib.Tests.UnsafeCodeTestFixture.Dispose() in UnsafeCodeTestFixture.cs:line 2472
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.RetainedExportFixture.Dispose() in XVerPreparedOperationTests.cs:line 694
   at Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.InvocationAndBuildConfigurationChangesCannotCertifyAStaleSchedule(String mutation, Boolean duringBuild) in XVerPreparedOperationTests.cs:line 628
```

Diagnosis read the actual shared fixture, without editing it: connections use
`Pooling=false`; `Dispose` disposes the connection, then recursively deletes
the fixture directory, logs an IOException and rethrows. The completed wrapper
uses this shared cleanup. The failed command output did not include that
fixture's absolute GUID directory or locking process identity; neither is
invented here. The cause remains **undetermined**, not established as the
same cause as Phase 6 run 10's earlier cleanup IOException.

Run 7, the smallest unchanged diagnostic selector, was exactly:

```powershell
dotnet test src/Fhir.CodeGen.Lib.Tests/Fhir.CodeGen.Lib.Tests.csproj --filter "FullyQualifiedName=Fhir.CodeGen.Lib.Tests.XVerPreparedOperationTests.InvocationAndBuildConfigurationChangesCannotCertifyAStaleSchedule"
```

It completed **exit 0, 9 passed, 0 failed/skipped**, command
**52489.1239 ms**, runner **38 s**. No code change, cleanup retry, sleep,
pooling change or process termination preceded it. That passing theory does
**not** overwrite the full run's failure or establish a green four-class gate.
The full gate was not rerun repeatedly given the independent material
performance blocker and temp-only production ownership.

Run 6's completed isolated workload evidence remains in
`temp/xver-prepared-b3259a8029654361ac1a97e0ddaed68f/` (W1 characterization),
`xver-prepared-ac28d761aa934954873867afa40fa9c1/` (W1 measurement),
`xver-prepared-08f08ceb471f4b0c93d57255664d5e6b/` (W2 measurement), and
`xver-prepared-2246540d5c904e5b9a7b91776202c37c/` (W2 characterization).
Those observations are correctness evidence, **not additional accepted fresh
timing samples**. The characterization captures, DBs, maps and outputs are
retained, not dumped wholesale. No separate console-output log file was
created for runs 1–7; their actual command output is retained in the session
and this chronological ledger, with raw workload files as identified above.

No mandatory final solution Release build or full external-filtered suite is
claimed here; those belong to the parent after Phase 7 acceptance/integration.

Read/edit diagnostics, in chronological order: the first unbounded root-AGENTS
view refused its size, so explicit ranges were read; two guessed view ranges
were beyond EOF (`ProfileUseFacts.cs` 2250 and `XVerPreparedOperation.cs` 835);
one search included nonexistent `Models/DbProfileMapOwnerFacts.cs` and reported
that skipped path. The first instrumentation patch had an empty trailing hunk
and was rejected without changing a file; the corrected patch applied before
the first instrumented build. An initial count-summary script split slash-bearing
stage names at their first slash; the corrected rightmost-slash grouping below
uses the original unmodified reports. An initial frozen-body lookup assumed
four-space member indentation, reported three missing nested methods, and was
corrected to use the matched indentation; all three bodies were then identical.
An initial sample comparison used Python newline-converting `read_text` on only
the candidate, giving unequal text but equal parsed JSON; a subsequent
raw-byte decode on both files confirmed exact equality after the sole date
substitution. These were read/edit diagnostics, not failed tests or extra samples.

#### Retained raw samples

All directories below are under repository-local `temp/`. Each contains its
unchanged DB, maps, W1 output where applicable, `stage-costs.json` and
`measurement-identity.json`, with `Completed=true`. Do not delete the earlier
52 MB W1 / 857 MB W2 semantic captures or any prior evidence.

| Run | PID | W1 directory suffix after `xver-prepared-` | W2 suffix |
|-|-:|-|-|
| 1 | 21316 | `c4fb6045ff374108bf7915ad9e85ab12` | `4529f729e721433fb96b34d701bc7d4e` |
| 2 | 42568 | `c54d874ac66b4045afdbb35e8a9d1432` | `82a31583bda54271ba0d474e87aa40b6` |
| 3 | 54004 | `c038f77aa47c44e1bcc493700bdf4a07` | `e7e99e3fa2de46caa660334a8c12219f` |
| 4 | 50604 | `572ff8d8e9ba466a9946abf801a7af22` | `6e260a31499f4efb9b1e115303bfacf2` |
| 5 | Count run | `4a9b25edd9cd4faca3aecec3971560d4` | `6167fa880f454ebfb49d1e5bafb46639` |

SHA-256 identities, `stage-costs.json` / `measurement-identity.json`:

| Run/workload | Stage costs | Measurement identity |
|-|-|-|
| 1/W1 | `9f70b0801d2ef83696daac182f0553cb5951e9f6c9ce8e71f8334ffb722b1eec` | `17b7af038e6e75e6620d2c821154356917677ddf85bfab738f8c56fe0dedc924` |
| 1/W2 | `7dd5649c2efd385652669f7590232df9b767f505f654a56e37768583acda05eb` | `9f9909e61b9b02e889a0ac9170c299631a2a26982168c3b61ef288329438b922` |
| 2/W1 | `51d27cdf1f774b558b5125dd120dce092eaf2c332831839e5a1c53c6839f9427` | `4597464b7bcb24d46096e7650829e8f91c9b78750da26756b6abbce02d278599` |
| 2/W2 | `de071c1cd519683d861d162a8a2f275cee4cc20f199e6547c158006399a12ce6` | `6faf8ceb6b9bdb33ed56774d50a6b4acf4c1e9d5f11f2a36b19e10467e26bc17` |
| 3/W1 | `014af78691b9d8bdd81a2aba307fb0ea1f54cad2260375e9faab203f6a6087d9` | `3c53699c30e05ab266f0d14ef48e8013ebf064a9c4c002a722e5cd87d4601968` |
| 3/W2 | `69950798e04a9318f638662e87627f2ef7a4a53e5bf17a7d4b27199581e61010` | `5f352acee591745666c6631eba8417daf5584a1ed13986a69ea0fbf1f837001b` |
| 4/W1 | `449ac1d3a703f1fdee4b63073e9de12f7ab7253bb776f612cb653898a4a7c4e5` | `c8ffcef87286e29ef7e25e38c5cf8150c4a528669ffc58d25b3bffc00b769ecb` |
| 4/W2 | `915accfae64de8ec77a5e8d33233f767240e82faf437209ecdc3d45bd5630a0a` | `03adb96db1dc3d99d02196a948452f9dd53070801f87fc08784d0485709b2f11` |
| 5/W1 | `8fbe0e749ce15ec2cecf734d97721cb52ceb428fb90a3071d501ccb26bea4f97` | `99ffb780ff08c5e2ee72065c817f69c2315bbf06dc3291f8d1ab4a7919fa2c79` |
| 5/W2 | `bc349f60ac913e15a91401ae6de0a7dc24eeb3407c9be6b932b016d6b2b0a67d` | `138f83197a69a29cb153c9290f3b93bccb59434007264b3877078790475d8f54` |

For **all eight uninstrumented workload observations**, comparison with the
corresponding first accepted Phase 1 identity found exact equality of package
rows, repetition count, terminology version, max step, scripts flag, every
table cardinality, retained row count, normalized JSON byte count/digest,
persisted negative JSON size, and every map's path/byte count/SHA-256.
W1 has 6,185 retained rows, 103,112 normalized JSON bytes, payload digest
`7c67389b93345fdcf242d24a50edc332dba5295624e4ca1200249f16d5c300ff`,
112,500 persisted negative bytes and 61 maps totaling 33,818 bytes. W2 has
312,573 retained rows, 123,867,864 normalized JSON bytes, digest
`b8d0d6a31ac493d0fc75cd63a37282a4edaf863eb51f2fd0b1fe43b63802cad6`,
98,819,836 persisted negative bytes and three local maps totaling 3,138 bytes.
W2's unchanged internal primitive maps are additional production inputs.
The identity property named `ProductionBaseline` still intentionally names
`d749a7b5e...`; the **candidate revision is aa290477...**, not that property.

#### Net timing and resource result

The required W1-then-W2 paired lifecycle is unchanged: W2 inherits W1's JIT/heap
history, but each accepted pair uses a new process. Preparation/readiness,
semantic checks, raw audits, independent persisted verification, negative
witnesses, emission and final DB disposal/pool clearing remain inside net
clocks. Identity/dump serialization remains outside. Runs 1 and 5 are excluded.
No new baseline, clocks, assertions, maps or noise waiver were introduced.

| Run | W1 net ms | W2 bounded net ms | W1 total incl. setup ms | W2 total incl. setup ms |
|-|-:|-:|-:|-:|
| 1 discarded | 33200.1889 | 486918.0114 | 35668.2047 | 556900.2300 |
| 2 | 31766.0902 | 531874.4025 | 34282.0187 | 606410.6024 |
| 3 | 31652.6649 | 503730.6185 | 34416.8500 | 574746.8391 |
| 4 | 37733.4752 | 483605.7040 | 40340.0046 | 553310.0546 |

| Workload | Frozen baseline net median / MAD ms | Candidate median / MAD ms | Verdict |
|-|-:|-:|-|
| W1 | 10952.4363 / 275.6870 | **31766.0902 / 113.4253** | 20813.6539 ms slower; 2.9004× baseline |
| W2 bounded | 129123.2825 / 97.9593 | **503730.6185 / 20124.9145** | 374607.3360 ms slower; 3.9012× baseline |

Both regressions exceed the larger MAD by a wide margin. They are **failed
net acceptance**, not inconclusive savings or a reason to take four speculative
extra samples. No candidate accepted observation is faster than its baseline.
Candidate total-including-setup medians/MAD are W1 34416.8500/134.8313 ms and
W2 574746.8391/21436.7845 ms. Frozen totals were 13852.5000/530.4598 and
205595.9231/2987.0896 ms.

Per-stage candidate median/MAD from the same three observations:

| Workload/stage | ms median / MAD | Allocated B median / MAD | Sampled peak private B median / MAD |
|-|-:|-:|-:|
| W1 import/root assertions | 1048.8872 / 13.6171 | 132594984 / 3904 | 119476224 / 106496 |
| W1 maps | 1428.2155 / 24.0545 | 89655296 / 7144 | 130654208 / 110592 |
| W1 compare | 8424.7936 / 66.2048 | 3942790640 / 528400 | 176107520 / 155648 |
| W1 outcomes | 18047.1788 / 31.5771 | 9419950128 / 31648 | 350515200 / 9527296 |
| W1 export incl. readiness/build/audits/emission | 5103.7840 / 111.8526 | 2346056648 / 17456 | 359505920 / 14176256 |
| W1 frozen verification | 115.7702 / 4.3214 | 63942776 / 4144 | 272809984 / 6680576 |
| W1 disposal/pool clear | 5.9387 / 0.4228 | 4440 / 0 | 267382784 / 7512064 |
| W2 full retained import | 32541.5588 / 605.1375 | 28583299496 / 117776 | 8604925952 / 24350720 |
| W2 maps | 25466.2851 / 847.7518 | 38646119624 / 153576 | 10883915776 / 1847296 |
| W2 Range comparison | 47628.0404 / 486.3305 | 63977397000 / 29256 | 11708993536 / 11296768 |
| W2 Range outcomes | 242688.5595 / 5709.1715 | 328402696960 / 1226296 | 15604301824 / 36438016 |
| W2 readiness/write-free build | 203738.4408 / 5782.7464 | 294283087600 / 1124400 | 15314194432 / 609906688 |
| W2 frozen verification | 3270.9903 / 831.3658 | 3533344168 / 1101840 | 12859191296 / 320610304 |
| W2 actual negative witnesses | 781.7383 / 29.5899 | 814611896 / 2896 | 12260532224 / 472223744 |
| W2 disposal/pool clear | 1.3772 / 0.0510 | 4584 / 64 | 12091199488 / 351510528 |

All three W1 operation stages regress against the baseline table above despite
reduced comparison allocation. W2 comparison also regresses (baseline median
41136.7935 ms); its outcomes/build regress drastically (baseline
53878.2341/28708.5678 ms and 68974005416/32197289784 allocated bytes).
The unchanged frozen harness measures W1 export as one stage; no invented
uninstrumented inner readiness/emission timing is claimed.

Peak sampled private/working bytes, taking each observation's maximum stage,
have median/MAD W1 **359505920/14176256** and **456019968/15192064**;
W2 **15604301824/319799296** and **15683383296/303501312**. Compare the
baseline W1 export private 203128832 and W2 build private 13921918976:
the candidate's larger peaks are an explicit adverse tradeoff, not a memory win.
No OOM/resource failure occurred in these completed runs. Entry/exit managed
and private readings, working-set samples, allocation deltas and sample counts
remain in every stage file. These are sampled process observations, not unique
managed graph sizes or a universal ceiling. Final DB sizes remain identical:
W1 11603968 bytes, W2 390873088 bytes; all per-stage DB sizes also agree.

#### Actual call-site counts and raw audits (separate run 5)

Temporary instrumentation exists only in retained `.instrumented` images under
`temp/xver-phase7-count-evidence-20261119/`; it has been removed from source.
The common catalog constructor counts completed owners and constructor stacks
for fallback attribution; `ResolveRoot` counts requests/hits; `ReadPayload`
counts actual misses. `ValidateInventoryRows` counts full semantic passes and
each transient inventory parse; `ReadDefinition` counts actual parses.
`ProfileUseFacts` keeps its primary constructor and all captures, including
`comparisons`; an identity wrapper at `_active` counts instances. Definition/
use misses, `ClassifyRequiredUses`, `Prove`, `ProveInOwner`, graph construction
and release sites count their actual executions. Operation/packet/product
constructors and disposal sites count lifetime edges. No schedule arithmetic
is presented as an execution counter.

Raw-audit scope is the actual `XVerOperandEvidence.Audit` entry through its
exit, with counters at `TableImage` materialization **only while that scope is
active**. It records full row counts and UTF-8 bytes of stored text cells whose
first non-whitespace character is `{` or `[`, without normalizing or parsing
those cells. This JSON-text metric covers object/array JSON in these operands;
it is not total SQL wire traffic, scalar JSON or unique retained bytes.
Supplemental selector passes, capture, `AcceptExpected` post-write scans,
directional capture and independent persisted-result reads are not extra
`Audit` invocations/rows in this table. Their costs remain in net clocks.
Audit elapsed times include probe overhead and are not accepted timing samples.

| Stage | Catalogs | Semantic passes / inventory parses | Lazy parses | Facts instances | Effective definitions / uses created | Directional computations |
|-|-:|-:|-:|-:|-:|-:|
| W1 import/root checks | 3 | 1 / 100 | 100 | 5 | 100 / 10 | 0 |
| W1 maps | 5 | 3 / 300 | 0 | 0 | 0 / 0 | 0 |
| W1 compare | 1 | 1 / 100 | 700 | 50 | 1440 / 1560 | 30 |
| W1 outcomes | 1 | 1 / 100 | 688 | 40 | 688 / 656 | 20 |
| W1 export | 1 | 1 / 100 | 576 | 30 | 576 / 576 | 18 |
| W1 verification | 2 | 0 / 0 | 15 | 0 | 0 / 0 | 0 |
| **W1 all stages** | **13** | **7 / 700** | **2079** | **125** | **2804 / 2802** | **68** |
| W2 import | 2 | 1 / 1257 | 582 | 0 | 0 / 0 | 0 |
| W2 between-stage checks/reopens | 2 | 0 / 0 | 1 | 0 | 0 / 0 | 0 |
| W2 maps | 6 | 3 / 3771 | 873 | 0 | 0 / 0 | 0 |
| W2 comparison | 8 | 4 / 5028 | 1615 | 4 | 675 / 1876 | 2 |
| W2 outcomes | 7 | 3 / 3771 | 1556 | 3 | 665 / 1872 | 2 |
| W2 readiness/build | 4 | 1 / 1257 | 1415 | 5 | 1330 / 3744 | 4 |
| W2 verification | 1 | 0 / 0 | 173 | 0 | 0 / 0 | 0 |
| W2 negative witnesses | 1 | 0 / 0 | 15 | 2 | 18 / 0 | 0 |
| **W2 all stages** | **31** | **12 / 15084** | **6230** | **14** | **2688 / 7492** | **8** |

Against the actual Phase 1 counters: W1 catalogs 577→13, fallback-root catalogs
208→**0**, semantic validations 171→7, lazy parses 7235→2079, effective
definitions 9404→2804, facts instances 573→125, directional work 168→68.
W2 catalogs 36→31, semantic validations 13→12, lazy parses 6488→6230,
effective definitions 3363→2688, directional work 10→8. These are actual
reductions, **not sufficient acceptance**. W2 comparison catalogs increase
7→8, build lazy parses increase 1173→1415, and build directional computations
stay 4: independent readiness/build seams remain independent by contract.
Do not silently fuse those calls or change W2 to improve the measurement.
W1's 30 comparison classifications include legitimate later-reverse operand
epochs; 20 outcome and 18 export directions reflect their exact scheduled
dependency contexts, not a promise that every command always computes 20.

| Stage | Raw audits | Rows | Object/array JSON text bytes | Count-run audit ms |
|-|-:|-:|-:|-:|
| W1 compare | 284 | 636230 | 685602118 | 4985.5362 |
| W1 outcomes | 243 | 1187925 | 1765347293 | 11473.1103 |
| W1 export | 70 | 414410 | 492480217 | 4136.6359 |
| **W1 total** | **597** | **2238565** | **2943429628** | **20595.2824** |
| W2 comparison | 4 | 522036 | 717559588 | 4954.8297 |
| W2 outcomes | 13 | 3334495 | 2716632545 | 20181.5792 |
| W2 readiness/build | 5 | 1055980 | 1391870345 | 8308.1652 |
| **W2 total** | **22** | **4912511** | **4826062478** | **33444.5741** |

Baseline prepared raw audits were absent, as documented above. In W1 the large
replayed decision fields dominate: outcome-stage `ElementComparisons` and
`ElementTypeComparisons` each contribute approximately 864 MB. W2 outcome
audits include 1610282232 facts-JSON bytes; build audits include 494099180
negative-outcome JSON bytes. Exact per-table numbers and directional/context
histograms are in `phase7-counts-lifetime.json`.

The reports are 50089 bytes / SHA-256
`be290b0aab4046274f1c521de3985e7d18549179db726c689e23a2802151eddb`
(W1) and 41636 bytes /
`701058ce8d3a40d680d41ce51c79beff791f1a1d05278fe24b14dd684d6b2d22`
(W2). Timer frequency is 10000000 ticks/sec.

#### Lifetime, graph sharing and retained-owner limitations

The collector retains counters, scalar descriptions and first-128-distinct weak
targets per stage/category, with a ConditionalWeakTable for de-duplication;
it does not strongly root observed owners. Forced collections occur outside
operation clocks in this separate run. W1 has a no-inline frame boundary;
W2's final observation is after its completed await. Neither is a universal
heap census. W2's first checkpoint deliberately keeps the returned build alive.

| Checkpoint | Managed / private / working bytes | Important live weak targets |
|-|-:|-|
| W1 consumers done; ordinary locals live | 39335816 / 242208768 / 357175296 | Connection 3/3 slots; operation/catalog/facts/payload/proof/product slots zero |
| W1 after returned frame | 25658360 / 213114880 / 330362880 | **0/2646** slots, including connections |
| W2 consumers done; returned build live | 5201245400 / 12540301312 / 12607336448 | Build 1/1; connections 1/3; effective-definition graphs 4/402; operation/evidence/packet/facts/proof/product/payload slots zero |
| W2 after completed await | 30468200 / 12389109760 / 12455575552 | **0/2414** slots, including returned build and connections |

Slots can reference the same connection at multiple operation entries.
The four W2 graph dictionaries surviving with ordinary locals alive are
compatible with the still-live persisted negative witnesses; their unique
retaining path was not heap-inspected. No unique graph-byte claim follows.
W2's post-return private reservation is not assigned to a live managed graph.

At actual release sites W1 disposes 3 operations, 125 facts and 3 prepared
catalogs; W2 disposes 6 operations, 14 facts and 8 catalogs (including other
explicitly owning paths). All counted post-disposal operation dictionary edges
and catalog structure/element/facts row edges are zero. Payload release sites
clear 2079 W1 and 2723 W2 entries; not all legacy W2 catalogs are explicitly
disposed, so do not equate that with every parse having an explicit release.
No owner-projection or proof-with-emitted-owner hook fired in either workload,
as on the baseline. This is measured zero, not coverage of a nonzero projection
workload or permission to remove projection obligations.

Constructed graph **edges**, not unique objects/heap bytes: W1 has 10599
effective-definition element edges, 15556 use-branch element edges and 2544
proof objects with 13392/11080/628 required/native/reason edges. W2 has
104172/54848 definition/use element edges and 19805 proof objects with
158484/146752/30983 required/native/reason edges. Child edges are zero.
Release observes 360 W1 outcome-product and 216 export-product decision edges;
W2 observes 2413 each. These can share immutable elements/proofs.

Scalar inspections of actual reused/returned objects qualify the weak results:

- The bounded `ElementComparer` has `_operation == null` after return.
  The top-level comparer/generator/exporter expose none of the inspected
  catalog/operation/cache fields; that empty scalar result is not proof about
  every hidden field.
- The legacy bounded `ElementOutcomeGenerator` **still retains** its readonly
  catalog with 1257 structures, 54266 elements and 1257 raw facts while the
  generator is kept alive; payload/payload-element caches are empty, roots=2,
  `_operation == null`. Its source/target element/type arrays also remain.
  This is observable relational/raw-JSON retention, not universal release.
  Fresh independent-call authority cannot be inferred merely from zero
  payloads or from collection after the whole workload returns.
- The bounded builder has null operation/catalog/representations/tracking and
  zero decision/target-type/canonical/reference caches after return.
  Returned `BuiltDefinitions.Representations` has null catalog/profile-facts,
  but intentionally retains read-only construction rows and pair lookups:
  25927 elements, 546 structures, 32805 types, 1572 value sets and its built
  profile/native representation information. Those returned rows are not a
  live readiness capability and can share captured immutable strings.
- Only the managed objects/fields above were sampled. No full native provider,
  event-list or CLR heap partition was taken; connection weak collection and
  observed disposal do not prove a universal provider-lifetime ceiling.

#### Sample semantic diff, diagnosis and ownership gate

The retained baseline and run-2 candidate file
`output/hl7.fhir.uv.xver-r2.r3/input/resources/StructureDefinition-profile-PreparedConsumer.json`
were read as raw bytes. Their raw SHA-256 values are
`f54df4fb5967cf7c7d44fe18aaf1b351456c69d6c40cc24220ca06a197e8596a`
and `49561a37eae12cf0c31e56b23e5455aff3f8055bfc3812eccd62b535a392f4ef`.
The **sole substitution** is the already-recorded resource `#date`:
`2026-11-18T13:37:38.5676473-06:00` and
`2026-11-19T10:23:44.8857978-06:00` each map consistently to the existing
`<runtime-timestamp-16>` token. After that, decoded text is exactly identical
(1929 characters) and the semantic diff is **empty**. No keys, versions,
owners, constraints, alternatives, canonicals or map provenance were removed.
This is one sample, not a full-output equivalence census; the unchanged frozen
assertions additionally passed in all measured runs.

Concrete diagnosis within the approved architecture:

1. W1's newly required repeated all-field raw audits are a large observed
   expense (20.60 seconds in the counted run). Raw `Audit` currently calls
   `Read`, which uses generated typed selectors plus supplemental stored-column
   SELECTs before exact comparison. Preserving every boundary and exact raw
   field/member comparison is mandatory; reducing audits is not a proposed fix.
2. W2's 33.44 seconds of raw-audit scopes **does not explain** its roughly
   375-second net regression. Source inspection identifies a specific additional
   repeated-work candidate: `XVerStructureCatalog.HasValueSet` calls
   `_evidence.Rows<DbValueSet>` for every qualified existence query, copying
   **the entire captured collection** before filtering. The baseline uses a
   qualified `SelectList`. `DbValueSetOperand.ToRow` reparses Compose and other
   FHIR components for every copied row. `ProfileUseFacts` invokes this in
   required-binding proofs. This is a source-supported costly path, **not a
   measured attribution of all remaining seconds**; no per-lookup timer was
   installed. `GetElementMapping` likewise copies its entire captured table.
3. The narrow suggested ownership adjustment is permanent internal work in
   `XVerStructureCatalog.cs` and `XVerOperandEvidence.cs`: exact immutable
   captured-row existence/key lookup without decoding unrelated rows, and
   investigation of exact raw replay without unnecessary semantic decoding.
   Keep independent expected-write transitions, all persisted fields/nulls,
   membership, live stamps/coherence, all audit boundaries, optional contracts,
   independent persisted verification and no cross-command reuse. This is a
   request for parent disposition, not an implemented optimization or guaranteed
   speedup. If additional owners are needed, stop for ownership again.

No material production edit is permitted by Phase 7's current **temporary
instrumentation only** ownership. Therefore net acceptance is genuinely blocked
in this slice. No assertion, inventory, proof, audit or workload was weakened to
turn the measurements green. The two requested named matrix expansions
`EveryOperandCategoryIsAuditedAtOriginalBoundaries` and
`WarmProductsCannotHidePersistedDecisionTampering` were **not implemented** in
this dispatch. Existing extensive Phase 2–6 matrix/lifecycle tests remain, but
are not relabeled as completion of the missing Phase 7 matrix. Additional
emitted-owner/branch matrix closure and exhaustive reused-seam authority
acceptance likewise remain unclaimed. The parent must retain these outstanding
gates along with the measured performance blocker; a successful correctness
rerun alone cannot close them.

#### Exact temporary-source restoration

Each of the six pre-hook and executed images is retained as `<filename>.original`
and `<filename>.instrumented` under
`temp/xver-phase7-count-evidence-20261119/`. After run 5, all six source files
were restored and compared byte-for-byte both with their saved originals and
`git cat-file --filters HEAD:<path>`, **before** the correctness rerun:

| Restored file | Bytes | Restored SHA-256 |
|-|-:|-|
| `DbStructureDefinitionFacts.cs` | 29169 | `b2f62026e03db65544b20dadfbfdebfaf4903f3f6fdd9462647ee5add0b29a82` |
| `XVerStructureCatalog.cs` | 36324 | `88f1ba394f048e7305db6b69eff2ba73944c87e7f579b7e231b937bb39d053dc` |
| `ProfileUseFacts.cs` | 137910 | `a4f64cc78d4c34b8128ca0400c1a24dcff440fed3108d6eb8d33614a40cb1c7d` |
| `XVerOperandEvidence.cs` | 253764 | `ccb553e5a262e728675644e9fa1dc076083866a438e534c297c17696b43126bb` |
| `XVerPreparedOperation.cs` | 31061 | `4061d9ce377563d14e68a02627b01e7c23eaac9d448f497861ccacb3fd14215c` |
| `XVerPreparedOperationTests.cs` | 248930 | `6a067b36f19de26d148e4e8cda3f8fc1e4f8e9254a42e3ee92a3315ec3cc077c` |

Instrumented SHA-256 values in the same file order:
`e0e96d35088f4e13b49ab2361b9ddb47ca072776cabfddc242121e491615aa1a`,
`b3b1feba8ddd73bd188c89e63701a70da1663417f96ec4d40e4d72e6fae054cf`,
`a18b3b6c8b6d04762fae420c93c193018b387d326dec5f410bfb125e00b0c75d`,
`0759df2a5c4e2491d44fe8c86eaed9526e7ad9815f706021a0b83e955b5dea1e`,
`8a8b5ab5dd6ffbea9ef43fe3613c3176529cb861e1ced4005274248f65a5fecc`,
`531eb8e50ff4a20de411f0d1dbdce5526d87ec60f3daf783b455b7ab0cd9b21e`.

No production telemetry, test observation hook, runtime switch, alternate
engine or permanent benchmark was retained. No stage/commit/push/PR occurred.
The only intended persistent edit from this slice is this evidence appendix.
No new automatic design/contract assumption is proposed for the parent's
ASSUMPTION ledger; the scope adjustment above remains unapproved.
Post-run-7 source comparisons again found all six original/restored byte
images identical to checkout-filtered HEAD. The spec retains CRLF and no bare
LF; `git diff --check` passes, the index is empty, HEAD is unchanged, and the
only tracked modification is this owned specification. All invoked commands
completed; none was abandoned or left running.

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
