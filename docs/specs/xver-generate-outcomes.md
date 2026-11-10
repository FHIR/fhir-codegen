# XVerProcessor.GenerateOutcomes — Step 6 of 7

## Purpose

`GenerateOutcomes` consumes rows produced by `CompareInDatabase` (`DbValueSetComparison`, `DbValueSetConceptComparison`, `DbStructureComparison`, `DbElementComparison`, and type comparisons) and emits outcome rows that `ExportOutcomes` materializes. The written rows are `DbValueSetOutcome`, `DbValueSetConceptOutcome`, `DbStructureOutcome`, `DbElementOutcome`, and `DbElementOutcomeTarget` (`DbOutcomeClasses.cs:292-395,397-832`). This is where headline decisions are made: same-name reuse, renamed reuse, generated cross-version definitions, external extension substitutions, ancestor/parent extension participation, Basic fallback, cross-version cardinality extensions, and unresolved/prohibited extension cases. Important source surprise: the `Outcome*ActionCodes` enums exist, but active generators do not assign enum values; rows encode categories through fields such as `RequiresXVerDefinition`, `IsRenamed`, `Target*Key`, `ExtensionSubstitutionKey`, `BasicElementId`, `RequiresExtensionDefinition`, and `RequiresCardinalityDefinition` (`ValueSetOutcomeGenerator.cs:335,405,507`; `DbOutcomeClasses.cs:292-832`).

## Invocation & Preconditions

- Direct callers from `ProcessCommand`: `outcomes` calls `GenerateOutcomes()` (`XVerProcessor.cs:372-382`); `outcomes-vs` calls `GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet)` (`XVerProcessor.cs:384-394`); `outcomes-sd` calls `GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource)` (`XVerProcessor.cs:396-405`); the default full pipeline calls `LoadDatabase`, map/substitution/type loaders, `CompareInDatabase`, `GenerateOutcomes`, and `ExportOutcomes` (`XVerProcessor.cs:421-428`).
- With `ReloadDatabase == true`, each `outcomes*` command runs `LoadDatabase`, `LoadFhirCrossVersionMaps`, `LoadExtensionSubstitutions`, and `LoadFhirTypeValueSets`, then **comparison of the same requested family**, then outcomes. `outcomes-vs` remains vocabulary-only. The default pipeline runs loaders → comparison → outcomes → export (`XVerProcessor.ExecuteOutcomeCommand` / `ProcessCommand`).
- `GenerateOutcomes` calls `LoadDatabase(false)` if `_db is null` and throws if `_db` is still null (`XVerProcessor.cs:619-627`).
- Preconditions: loaded DB, populated comparison rows, cross-version maps, extension substitutions, and FHIR-type ValueSets. The outcome generators read comparison/content tables, and `ElementOutcomeGenerator` directly reads `DbExtensionSubstitution` rows (`ElementOutcomeGenerator.cs:262-301`).

## Inputs

Method signature (`XVerProcessor.cs:614-617`):

```csharp
public void GenerateOutcomes(
    FhirArtifactClassEnum? artifactFilter = null,
    int? maxStepSize = null,
    HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
```

- `artifactFilter`: `CodeSystem`/`ValueSet` dispatch vocabulary only; structure classes (`PrimitiveType`, `ComplexType`, `Resource`, `Profile`, `Extension`) dispatch structures only; default dispatches both (`XVerProcessor.cs:629-660`).
- `maxStepSize`: forwarded to both per-family generators. If null, each generator processes every package distance (`ValueSetOutcomeGenerator.cs:49-63`; `StructureOutcomeGenerator.cs:71-85`).
- `specificPairs`: optional exact `(source, target)` FHIR sequence filter, applied after the stepped package-pair list is built (`ValueSetOutcomeGenerator.cs:65-76`; `StructureOutcomeGenerator.cs:87-110`).
- Prior state: `_db`, source/target packages and artifacts, comparison tables, extension substitutions, FHIR-type ValueSet rows, and cross-version map-derived comparison rows.

## Outputs

- Tables written: `ValueSetOutcomes`, `ValueSetConceptOutcomes`, `StructureOutcomes`, `ElementOutcomes`, and `ElementOutcomeTargets` (`DbOutcomeClasses.cs:292-395,397-832`).
- `OutcomeGenerator.GenerateOutcomes` drops and recreates requested outcome tables on every run (`OutcomeGenerator.cs:38-40`; `DbOutcomeClasses.cs:157-193`). This is a destructive reset for the selected output family.
- Structure generation writes both source-element summary rows (`DbElementOutcome`) and per-target/context rows (`DbElementOutcomeTarget`) (`ElementOutcomeGenerator.cs:613-651,693-731,893-932,1572-1687`).
- Structure generation also records cross-version **cardinality-only** extension needs alongside data-type extensions: the `DbElementOutcome.RequiresCardinalityDefinition` / `RequiresCardinalitySlice` flags (`DbOutcomeClasses.cs:438-439`), the parsed `CardinalityExtensionContexts` list (`DbOutcomeClasses.cs:582-600`), `GetCombinedContexts()` which unions extension and cardinality contexts (`DbOutcomeClasses.cs:602-630`), and the per-target `DbElementOutcomeTarget.CardinalityContext*` fields (`DbOutcomeClasses.cs:825-828`).
- The enum declarations are documentation/vocabulary for now; the row classes do not have an `OutcomeAction` column and generation does not assign the enum constants (`DbOutcomeClasses.cs:7-144,292-832`).

## Owning-Structure Root Identity

`StructureOutcomeGenerator.buildOutcomes` indexes source and target roots
by `DbElement.StructureKey` and resolves them with the owning
`DbStructureDefinition.Key`. A base datatype and its profiles may share
root element Id `Coding`; these remain separate owners and must not
collide in a package-wide Id dictionary. Multiple roots within the same
owner remain invalid and fail rather than selecting an arbitrary root.
Existing missing-root/null behavior is unchanged.

## Unsafe-Code Safety Outcomes

After final structure/element outcome persistence and content-reference
post-processing, `StructureOutcomeGenerator` invokes
`UnsafeCodeOutcomeGenerator.GenerateForPair`. It consumes persisted
annotations, authored evidence, rules, and the actual contributing
element-comparison/mapping contexts. It does not reread configuration.

For one qualified source code, contributing directed context, and destination:

```text
A = true annotation on the applicable directed element target
N = relevant authored explicit noMap: true
M = rules matching source system/code/effective source system version
eligible(r) = !r.noMapOnly || N
exempt = an eligible rule excludes the destination release
         or an applicable authored relationship
unsafe = !exempt && ((A && N) || any eligible rule in M)
```

Eligibility is checked **before** exemptions: an ineligible `noMapOnly`
entry supplies neither a trigger nor an exemption. Eligible exemptions
override both positive mechanisms for this code/context/destination.
Unrelated mappings, scopes, codes, targets, or reverse directions cannot
cancel another trigger; surviving contexts are unioned. Missing maps and
inferred failure are not explicit `N`. Configuration can independently
qualify a code without either annotation or no-map, unless gated by
`noMapOnly`.

All identities use exact ordinal matching. Source code-system version,
ValueSet canonical version, destination full FHIR release, and generated IG
version remain separate. Version-qualified canonicals are split into URL
and version. Wildcard/exact overlaps are normalized into disjoint version
matches: an exact-version exemption subtracts that version from a wildcard
trigger. Unknown instance version cannot satisfy an exact-version entry,
but remains covered by wildcard rules unless excluded. Only authoritative,
applicable source context can supply a missing version.

### Resource uses and qualification

Traversal covers actual resource uses of inherited/nested datatypes,
choices, repeated elements, and content references, including cycles.
`Coding`, `CodeableConcept.coding`, `CodeableReference.concept`, and
quantity-style system/code carriers retain qualified identities without
requiring a primitive binding. Primitive codes require authoritative,
unique source binding qualification. A proven different system/version or
non-member is a non-match; missing/ambiguous qualification for an applicable
candidate is a blocking outcome error, **not** a presumed safe value.
Errors identify element, direction, candidate identity, and the missing
binding/scope context. Resource-valued child uses have their own owners;
references are not followed to external resources.

At effective native Coding/boolean and Coding/code choices, the safety
unit is an independently classified **source type use**. Boolean supplies
no safety use. Coding retains typed qualification; the choice's primitive
code alternative calls `PrimitiveIdentity` separately for every candidate
system/code group, using the original occurrence and contributing
`MappingUse.Source` binding. Neither the code datatype root nor Coding's
qualification supplies that identity. Carrier-owned `.code` suppression
still applies to actual carrier child edges, not to the mixed choice's
primitive alternative. Quantity comparator and other non-H2 identities
retain their ordinary paths.

An internal immutable `UnsafeCodeSourceUse` retains the original occurrence
path, effective source type, exact `TypeProfile ?? TypeName` token, resolved
source root element Id and typed/primitive qualification kind. Its selected
path appends one terminal datatype-value endpoint using the unchanged
`UnsafeCodePathStep` vocabulary. For example:

```text
Parameters / Parameters.parameter / Parameters.parameter.value[x]
  / (ElementId="Coding", TypeName="Coding", IsContentReference=false)
```

Primitive code appends `("code", "code", false)` instead. A profiled Coding
uses its uniquely resolved source root's actual element Id and exact
canonical/version token, not the profile basename. An incoming typed-child
prefix is retained in full. Pure normalization consumes immutable
declaration/root facts supplied by the caller and rejects nonterminal,
duplicate, reference, undeclared, missing, ambiguous or resource endpoints.
Ordinary typed-child steps such as `("Coding.code", "Coding", false)`
remain traversal, not selected values.

Selected paths, identity/version matches and provenance normalize
independently. Equal code literals do not collapse different systems,
versions or selected paths. Collection precedes trigger insertion and
completion remains last: a failed primitive qualification leaves **no
inserted triggers** and all outcome completion stamps unset. A complete
authoritative nonmember or version nonmatch removes only that primitive
use, leaving typed Coding classification independent. Context element IDs
remain those of the original contributing map, never the endpoint root.
No public API, JSON fields, columns or format constants change.

`DbUnsafeCodeTrigger` stores owning structure outcome, directed context,
semantic source/type-use path (`SourcePathJson`), qualified identity,
source version, wildcard/exact version match (`IsWildcardVersion`,
`VersionsJson`), and evidence/rule/annotation provenance keys. Arbitrary
versions and paths use lossless JSON, not comma splitting. No FHIRPath is
stored. Rendering is deferred to the typed construction boundary described
in [export](./xver-exporter-export.md#typed-construction-and-instance-invariants).

### Completion and failure

Before selected structure outcomes begin, `BeginOutcomes` requires a
completed compatible input and a successful comparison revision against
that input. Old element-map provenance is rejected. After triggers flush,
`CompleteOutcomes` rechecks dependencies and writes
`OutcomeInputRevision`, `OutcomeComparisonRevision`, and
`OutcomeFormatVersion` **last**. A successful evaluation with **zero
triggers** writes the same completion stamp; it is different from never
evaluated, failed, stale, or incompatible state.

Structure outcome reset invalidates all pair outcome stamps and recreates
the trigger table but preserves comparisons. A pair filter does not narrow
this destructive reset: excluded pairs stay incomplete. Insert errors or
mid-pass dependency changes propagate without completion; partial rows
cannot certify readiness. Vocabulary-only outcomes neither require safety
prerequisites nor reset existing structure triggers/stamps.

`PairFacts` loads rules, evidence, annotations, elements, types, comparisons,
outcomes, and binding data once per pair into dictionaries/lookups
(`UnsafeCodeOutcomeGenerator.cs:241-283`); binding/context/evidence
resolution is memoized. The rule/element classifier loops use these
in-memory collections rather than issuing a query for every rule at every
element. This is a query-placement inspection, not a timing claim.

## Algorithm

1. **Dispatch in `XVerProcessor.GenerateOutcomes`** (`XVerProcessor.cs:619-660`): lazy-load DB, throw if absent, construct `OutcomeGenerator`, and dispatch by `artifactFilter` into vocabulary, structures, or both.
2. **Inside `OutcomeGenerator.GenerateOutcomes`** (`OutcomeGenerator.cs:32-53`): drop/create selected tables, instantiate `ValueSetOutcomeGenerator` when `processValueSets`, and instantiate `StructureOutcomeGenerator` when `processStructures`.
3. **`ValueSetOutcomeGenerator.CreateOutcomesForValueSets`** (`ValueSetOutcomeGenerator.cs:42-77`): load packages ordered by version, build closer-first stepped pairs in both directions, apply `specificPairs`, then per pair run `buildOutcomes(packagePair)` and `applyCachedChanges(packagePair)`.
4. **ValueSet `buildOutcomes`** (`ValueSetOutcomeGenerator.cs:112-735`): load source/target ValueSets, concepts, comparisons, and concept comparisons; skip non-expandable/excluded URLs; create no-map outcomes for source ValueSets with no usable target; otherwise compute concept coverage across all targets, determine whether a generated cross-version definition is needed, and emit ValueSet plus concept outcomes.
5. **`StructureOutcomeGenerator.CreateOutcomesForStructures`**: use the same closer-first pair schedule, validate/begin safety dependencies, create an `ElementOutcomeGenerator` per pair, run structure `buildOutcomes`, flush caches, run `elementOutcomeGenerator.DoPostProcessing(packagePair)`, flush again, then classify/persist safety triggers and complete the pair.
6. **Structure `buildOutcomes`** (`StructureOutcomeGenerator.cs:158-580`): load structures, roots, and structure comparisons; skip primitives; create tracking records for mapped/no-map structures; call `ElementOutcomeGenerator.ProcessSourceStructure`; then emit mapped or no-map `DbStructureOutcome` rows.
7. **`ElementOutcomeGenerator` constructor** (`ElementOutcomeGenerator.cs:124-301`): load source/target elements, comparisons, type comparisons, types, resource type sets, Basic/Extension path lookup dictionaries, and `_extensionSubstitutionsByElementId`. Extension substitutions include source-sequence-specific and global rows; exact IDs, cleaned `[x]` IDs, expanded context IDs, and cleaned context IDs all become keys (`ElementOutcomeGenerator.cs:262-301`).
8. **Element classifier** (`ElementOutcomeGenerator.cs:435-1704`): `ProcessSourceStructure` filters ignorable elements, computes mapping completeness, checks required ValueSet binding mappings, then `createOutcomes` walks elements in resource order and decides target rows, cross-version requirement, Basic/Extension replacements, extension substitutions, alternate-reference/canonical substitutions, legal contexts, and final `DbElementOutcome` fields. `createOutcomes` also decides cross-version **cardinality-only** extensions: when a source element permits more repetitions than its mapped target it sets `requiresCardDefinition` (`ElementOutcomeGenerator.cs:967-973`), propagates `RequiresCardinalitySlice` to children of a cardinality parent/ancestor (`ElementOutcomeGenerator.cs:975-988`), fills the per-target `CardinalityContext*` fields at each `DbElementOutcomeTarget` creation path (`ElementOutcomeGenerator.cs:640-643,720-723,920-923`), and stores `RequiresCardinalityDefinition`/`RequiresCardinalitySlice` plus the distinct `CardinalityExtensionContexts` on the summary row (`ElementOutcomeGenerator.cs:1609-1610,1645`).
9. **Post-processing** (`StructureOutcomeGenerator.cs:107`; `ElementOutcomeGenerator.cs:304-313`): content-reference source elements that did not initially export extensions are promoted to `RequiresExtensionDefinition = true` when referencing outcomes require cross-version definitions.

## Decision Points

- **Rule:** Standalone `outcomes*` commands reuse existing DB state unless `ReloadDatabase` is true; the default command always reloads, compares, generates, and exports. **Source:** `XVerProcessor.cs:372-405,421-428`. **Rationale:** AI Guess: this supports fast outcome iteration while keeping the default path reproducible.
- **Rule:** A null `_db` triggers `LoadDatabase(false)`, then a hard exception. **Source:** `XVerProcessor.cs:619-627`. **Rationale:** All generators are database-backed and cannot run from source packages alone.
- **Rule:** `artifactFilter` is a family switch: vocabulary, structures, or both. **Source:** `XVerProcessor.cs:629-660`. **Rationale:** It aligns generation with the two exporter domains.
- **Rule:** Selected outcome tables are dropped and recreated before generation. **Source:** `OutcomeGenerator.cs:38-40`; `DbOutcomeClasses.cs:157-193`. **Rationale:** AI Guess: destructive reset avoids stale rows after comparison or generator changes.
- **Rule:** Package pairs are generated closer-first and both directions; `specificPairs` filters directed pairs exactly. **Source:** `ValueSetOutcomeGenerator.cs:49-76`; `StructureOutcomeGenerator.cs:71-110`. **Rationale:** AI Guess: nearby FHIR versions are easiest to inspect first, and directed filtering lets callers request only `R5 -> R4`, for example.
- **Rule:** Per-pair caches are flushed and cleared after each package pair; structure generation flushes again after element post-processing. **Source:** `ValueSetOutcomeGenerator.cs:79-110`; `StructureOutcomeGenerator.cs:104-156`. **Rationale:** This bounds memory and makes inserted rows available before post-processing.

### ValueSet and concept outcomes

- **Rule:** ValueSet outcomes skip non-expandable, DB-excluded, or `_exclusionSet` URLs. **Source:** `ValueSetOutcomeGenerator.cs:153-164`; `_exclusionSet` in `XVerProcessor.cs:113-128`. **Rationale:** The generator re-applies vocabulary exclusions so pre-excluded CodeSystems/ValueSets do not get derived outcomes.
- **Rule:** A source ValueSet with no comparisons gets a no-target cross-version ValueSet/ConceptMap outcome. **Source:** `ValueSetOutcomeGenerator.cs:169-202,530-629`. **Rationale:** No target comparison means a generated definition is the only available representation.
- **Rule:** If mapped and no-map comparisons both exist for one source ValueSet, the no-map comparison is removed. **Source:** `ValueSetOutcomeGenerator.cs:205-216`. **Rationale:** AI Guess: no-map is fallback metadata and would duplicate mapped outcomes if retained.
- **Rule:** Concept coverage treats identical, equivalent, and source-narrower concept comparisons as fully mapped. **Source:** `ValueSetOutcomeGenerator.cs:252-265`. **Rationale:** Those relationships preserve source expressivity in the target.
- **Rule:** A mapped ValueSet requires a cross-version definition only if it is not identical, not equivalent, and not fully mapped across all targets. **Source:** `ValueSetOutcomeGenerator.cs:337-352`. **Rationale:** Generated ValueSets are reserved for residual semantics/concepts not represented by target artifacts.
- **Rule:** `UseValueSetSameName` means mapped target, no rename, and no generated definition. **Source:** enum text `DbOutcomeClasses.cs:7-17`; fields in `ValueSetOutcomeGenerator.cs:324-352,393-417`. **Rationale:** AI Guess: current rows encode this as `TargetValueSetKey != null`, `IsRenamed == false`, and `RequiresXVerDefinition == false`.
- **Rule:** `UseValueSetRenamed` means a single mapped target whose ID differs and no generated definition is needed. **Source:** `DbOutcomeClasses.cs:14-17`; `ValueSetOutcomeGenerator.cs:324,393-417`. **Rationale:** The target artifact is reusable but documentation/export must preserve the rename.
- **Rule:** `UseCrossVersionDefinition` means no target coverage and `RequiresXVerDefinition == true`. **Source:** `DbOutcomeClasses.cs:19-22`; no-map fields in `ValueSetOutcomeGenerator.cs:542-583,620-627`. **Rationale:** The source ValueSet must be generated in the cross-version IG.
- **Rule:** `UseSameNameAndCrossVersion` and `UseRenamedAndCrossVersion` mean a mapped target exists but residual concepts require a generated definition. **Source:** `DbOutcomeClasses.cs:24-32`; `ValueSetOutcomeGenerator.cs:337-352,393-417`. **Rationale:** AI Guess: exporters can reuse the target and add cross-version supplement material.
- **Rule:** `UseOtherValueSets` and `UseOtherAndCrossVersion` mean this comparison has no target but other target ValueSets cover some/all concepts; the distinction is `FullyMapsAcrossAllTargets` and `RequiresXVerDefinition`. **Source:** `DbOutcomeClasses.cs:34-42`; `ValueSetOutcomeGenerator.cs:530-583,612-627`. **Rationale:** AI Guess: this represents intentional no-map-to-this-target when coverage is provided elsewhere.
- **Rule:** Concept outcomes similarly derive `UseConceptSameCode`, `UseConceptChangedCode`, `UnmappedConcept`, `UseCrossVersionDefinition`, `MappedElsewhere`, `UseCodeAndCrossVersion`, and `UseOneOfMultipleCodes` from target code, rename, unmapped, coverage, and xver fields rather than enum assignment. **Source:** enum text `DbOutcomeClasses.cs:45-76`; row creation `ValueSetOutcomeGenerator.cs:473-520,638-680,688-730`. **Rationale:** AI Guess: booleans and target-code fields are richer for exporters than one flattened action value.
- **Rule:** Escape-valve concept mappings are adjusted during outcomes: if source/target code was treated as escape-valve and the parent ValueSet relationship is `RelatedTo` or `SourceIsBroaderThanTarget`, the concept is forced not fully mapped and requires xver. **Source:** `_escapeValveCodes` in `XVerProcessor.cs:130-143`; outcome logic `ValueSetOutcomeGenerator.cs:452-470`. **Rationale:** AI Guess: OTHER/OTH/UNKNOWN/UNK can be semantically broad even when the literal code appears map-like.

### Structure outcomes

- **Rule:** Primitive source structures are skipped. **Source:** `StructureOutcomeGenerator.cs:199-206`; `ElementOutcomeGenerator.cs:702-705`. **Rationale:** Primitive differences are handled through element/type comparison, not standalone profile outcomes.
- **Rule:** No comparison, explicit no-map, null target, or missing target creates a no-target structure tracking record. **Source:** `StructureOutcomeGenerator.cs:210-260`. **Rationale:** Elements still need outcomes so resource/type content can fall back to Basic or Extension.
- **Rule:** A mapped structure requires xver only if not identical, not equivalent, and not fully mapped across all targets after element processing. **Source:** `StructureOutcomeGenerator.cs:353-379`. **Rationale:** Element completeness can eliminate the need for generated structure material.
- **Rule:** `UseStructureSameName` means a mapped target with the same ID and no generated definition. **Source:** `DbOutcomeClasses.cs:78-87`; `StructureOutcomeGenerator.cs:353-379,409-474`. **Rationale:** AI Guess: represented by target fields, `IsRenamed == false`, and `RequiresXVerDefinition == false`.
- **Rule:** `UseStructureRenamed` means a single mapped target with a different ID. **Source:** `DbOutcomeClasses.cs:84-87`; `StructureOutcomeGenerator.cs:353,427-450`. **Rationale:** The concrete target is usable, but rename metadata is preserved.
- **Rule:** `UseBasicResource` is the conceptual outcome for an unmapped source resource represented through target `Basic`. **Source:** enum text `DbOutcomeClasses.cs:88-91`; Basic no-target element path `ElementOutcomeGenerator.cs:569-652`. **Rationale:** Basic is the target resource available for otherwise unmapped resource data.
- **Rule:** `UseDatatypeExtension` is the conceptual outcome for an unmapped non-resource type represented as Extension. **Source:** enum text `DbOutcomeClasses.cs:92-95`; non-resource no-target path `ElementOutcomeGenerator.cs:653-734`; complex-type-to-extension flag `ElementOutcomeGenerator.cs:998-1007`. **Rationale:** AI Guess: datatypes have no Basic equivalent, so the portable target representation is an extension profile.

### Element outcomes

- **Rule:** `ProcessSourceStructure` filters out id/extension/modifierExtension elements, determines type/relationship completeness, checks required ValueSet bindings, then calls `createOutcomes`. **Source:** `ElementOutcomeGenerator.cs:347-357,403-432`. **Rationale:** The final row needs structural, type, terminology, and context state.
- **Rule:** Mapping-compatible relationships are `null`, `Equivalent`, and `SourceIsNarrowerThanTarget`. **Source:** `ElementOutcomeGenerator.cs:387-390`. **Rationale:** AI Guess: null is treated permissively for identity/default comparison paths; equivalent and source-narrower preserve source meaning.
- **Rule:** An element initially requires xver when it is non-root, not fully mapped across all targets, and leaf; no-target rows and parent requirements can also force xver. **Source:** `ElementOutcomeGenerator.cs:493-496,936-954`. **Rationale:** Leaf source data with no complete native target needs representation, and children of generated definitions must stay inside that definition.
- **Rule:** `UseElementSameName` means a mapped target/context name matches and no extension/slice is required. **Source:** enum `DbOutcomeClasses.cs:102-111`; target selection and fields `ElementOutcomeGenerator.cs:1343-1349,1654-1662`. **Rationale:** AI Guess: inferred from target rows, `IsRenamed == false`, and no xver requirement because the enum is not assigned.
- **Rule:** `UseElementRenamed` means the selected target/context name differs. **Source:** enum `DbOutcomeClasses.cs:108-111`; `IsRenamed = sourceEd.Name != targetEd.Name` in `ElementOutcomeGenerator.cs:1654`. **Rationale:** Rename is tracked separately from mapping completeness.
- **Rule:** `UseExtension` means `RequiresXVerDefinition` is true and the element must define its own extension, not a slice, Basic element, external substitution, or content-reference definition. **Source:** enum `DbOutcomeClasses.cs:112-115`; computation `ElementOutcomeGenerator.cs:1369-1374`; generated fields `ElementOutcomeGenerator.cs:1605-1617`. **Rationale:** Export needs a standalone extension StructureDefinition.
- **Rule:** `UseExtensionFromAncestor` means the element participates in an ancestor/parent generated definition. **Source:** enum `DbOutcomeClasses.cs:116-119`; propagation and slice fields `ElementOutcomeGenerator.cs:943-954,1369,1607,1638-1639`. **Rationale:** The source child is represented under an existing generated parent context.
- **Rule:** `UseBasicElement` means an otherwise-xver resource element matches a compatible target `Basic` element path. **Source:** enum `DbOutcomeClasses.cs:120-123`; Basic path logic `ElementOutcomeGenerator.cs:990-1041,1044-1077`; stored fields `ElementOutcomeGenerator.cs:1647-1648`. **Rationale:** Reusing Basic avoids unnecessary generated extensions.
- **Rule:** `Unresolved` means xver is needed but extension definition is prohibited, especially for Resource-typed values. **Source:** enum `DbOutcomeClasses.cs:128-131`; prohibition logic `ElementOutcomeGenerator.cs:1557-1568`; stored fields `ElementOutcomeGenerator.cs:1602-1603`. **Rationale:** AI Guess: these rows remain for review/diagnostics because safe extension generation is not possible.
- **Rule:** `IsExtension` is represented by omission: source extension/modifierExtension elements are skipped and no outcome row is created. **Source:** enum `DbOutcomeClasses.cs:132-135`; skip logic `ElementOutcomeGenerator.cs:347-357,412-414`. **Rationale:** Source-side extensions already have extension semantics.
- **Rule:** `_extensionSubstitutionsByElementId` is built from source-sequence-specific and global `DbExtensionSubstitution` rows; duplicate keys silently overwrite earlier entries. **Source:** `ElementOutcomeGenerator.cs:262-301`, especially writes at `280,285,293,297`. **Rationale:** AI Guess: substitution rows are SME override data; the code does not log conflicts.
- **Rule:** A matching extension substitution prevents generating a new extension by making `extSubstitute` non-null and storing substitution fields. **Source:** lookup `ElementOutcomeGenerator.cs:1185-1208`; `requiresExtensionDefinition` condition `ElementOutcomeGenerator.cs:1369-1374`; fields `ElementOutcomeGenerator.cs:1651-1652`. **Rationale:** AI Guess: curated external extensions should be reused rather than duplicated. Caveat: later alternate-reference/canonical logic can overwrite `extSubstitute` (`ElementOutcomeGenerator.cs:1260-1265,1301-1306`).
- **Rule:** Standard `alternate-reference` and `alternate-canonical` substitutions can absorb unmapped Reference/CodeableReference/canonical target-profile gaps; if they remove all unmapped types, xver is cleared. **Source:** `ElementOutcomeGenerator.cs:1210-1335`. **Rationale:** AI Guess: these standard extensions preserve narrower target profile/canonical allowances without generating a full element extension.
- **Rule:** Required ValueSet bindings can make an element incomplete even when structural mapping exists. **Source:** `ElementOutcomeGenerator.cs:1706-1775`. **Rationale:** Required bindings constrain legal codes, so target binding relationship matters.
- **Rule:** Quantity-like unmapped types can be considered mapped by normalized quantity type equivalence, structure comparisons, or structure mappings. **Source:** `ElementOutcomeGenerator.cs:1968-2177`. **Rationale:** AI Guess: Quantity profile names vary while remaining semantically representable.
- **Rule:** A leaf type can map by distributing its child elements across target elements when all meaningful children map. **Source:** `ElementOutcomeGenerator.cs:2181-2218,2339-2637`. **Rationale:** AI Guess: some version changes flatten/expand complex source values across target fields.
- **Rule:** Choice-type and modifier-extension contexts may be promoted to parent contexts to find legal extension placement. **Source:** modifier context `ElementOutcomeGenerator.cs:1079-1181`; choice context `ElementOutcomeGenerator.cs:1433-1517`. **Rationale:** FHIR extensions must be placed on legal containing elements.
- **Rule:** If xver is still needed and no context target exists, context falls back to the sole target structure root or generic `Element`. **Source:** `ElementOutcomeGenerator.cs:1519-1545`. **Rationale:** AI Guess: this prevents dropping extension definitions solely because no precise context was found.
- **Rule:** `RequiresCardinalityDefinition` is set when a source element permits more repetitions than its mapped target (a cardinality-only extension, distinct from a data-type `RequiresExtensionDefinition` extension). **Source:** computation `ElementOutcomeGenerator.cs:967-973`, row assignment `ElementOutcomeGenerator.cs:1609`. **Rationale:** The narrower target element cannot hold the source's extra repetitions, so they are carried in a generated cardinality extension.
- **Rule:** A parent/ancestor cardinality requirement propagates a `RequiresCardinalitySlice` to children, placing them inside the parent's generated cardinality extension. **Source:** `ElementOutcomeGenerator.cs:975-988`, row assignment `ElementOutcomeGenerator.cs:1610`. **Rationale:** AI Guess: descendant data belongs to the same cardinality extension as its repeating ancestor.
- **Rule:** `DbElementOutcomeTarget.CardinalityContext*` records the element/URL context where a cardinality extension attaches, gathered into the row's `CardinalityExtensionContexts`. **Source:** model fields `DbOutcomeClasses.cs:825-828`; population `ElementOutcomeGenerator.cs:640-643,720-723,920-923`; row contexts `ElementOutcomeGenerator.cs:1645`. **Rationale:** Cardinality contexts are tracked separately from data-type extension contexts so emission can target them independently.
- **Rule:** `GetCombinedContexts()` unions data-type extension contexts (only when actually defining, substituting, or using alternate reference/canonical targets) with cardinality contexts (only when `RequiresCardinalityDefinition`). **Source:** `DbOutcomeClasses.cs:602-630`. **Rationale:** A generated extension's legal `Context` is the union of whichever extension categories the element actually requires.
- **Rule:** `DoPostProcessing` promotes content-reference outcomes to extension definitions when referencing outcomes require xver. **Source:** call `StructureOutcomeGenerator.cs:107`; implementation `ElementOutcomeGenerator.cs:304-313`. **Rationale:** Referenced generated definitions must exist for dependent extensions.

## Rationale Coverage

`Decisions: 48 total — cited: 27 — AI Guess: 21 — unresolved: 0`

Every rule above has a source citation. `cited` means the rationale is directly supported by code structure, comments, enum docs, or stored fields; `AI Guess` means the rule is source-backed but the reason is inferred.

## Failure Modes & Edge Cases

- `GenerateOutcomes` throws when `LoadDatabase(false)` fails to populate `_db` (`XVerProcessor.cs:619-627`).
- Structure safety requires current completed input/comparison revisions before evaluation; an empty table is not proof of readiness. Vocabulary-only generation retains its existing comparison behavior and has no structure-safety prerequisite.
- Re-running `outcomes*` always wipes the selected outcome tables first (`OutcomeGenerator.cs:38-40`).
- Extension-substitution conflicts are silent; the last dictionary write for a key wins (`ElementOutcomeGenerator.cs:269-301`).
- Source id/extension/modifierExtension elements are skipped, not written as explicit `IsElementId` / `IsExtension` rows (`ElementOutcomeGenerator.cs:347-357,412-414`).
- Context promotion can throw instead of producing an unresolved row if a required parent context cannot be found (`ElementOutcomeGenerator.cs:1144-1150,1479-1486`).
- Basic/Extension path replacement happens before element-ID extension substitution, and alternate-reference/canonical can later overwrite `extSubstitute` (`ElementOutcomeGenerator.cs:990-1077,1185-1208,1260-1265,1301-1306`).

## Coverage Checklist

- [x] `XVerProcessor.GenerateOutcomes` orchestration (cs:614-661)
- [x] `OutcomeGenerator.GenerateOutcomes` orchestration
- [x] `ValueSetOutcomeGenerator.CreateOutcomesForValueSets`
- [x] `StructureOutcomeGenerator.CreateOutcomesForStructures`
- [x] `ElementOutcomeGenerator.ProcessSourceStructure` / `createOutcomes`
- [x] Cardinality-extension outcome category (`RequiresCardinalityDefinition` / `RequiresCardinalitySlice`, `CardinalityContext*`, `GetCombinedContexts`)
- [x] `_extensionSubstitutionsByElementId` construction
- [x] All seven active `OutcomeElementActionCodes` values
- [x] All four active `OutcomeStructureActionCodes` values
- [x] All seven active `OutcomeValueSetActionCodes` values
- [x] `_exclusionSet` and `_escapeValveCodes` usage in outcomes

## References

- Source:
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:104-143`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:256-431`
  - `src/Fhir.CodeGen.Comparison/XVer/XVerProcessor.cs:614-661`
  - `src/Fhir.CodeGen.Comparison/Outcomes/OutcomeGenerator.cs`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ValueSetOutcomeGenerator.cs`
  - `src/Fhir.CodeGen.Comparison/Outcomes/StructureOutcomeGenerator.cs`
  - `src/Fhir.CodeGen.Comparison/Outcomes/ElementOutcomeGenerator.cs`
  - `src/Fhir.CodeGen.Comparison/Models/DbOutcomeClasses.cs` (enums + row classes)
- Related specs:
  - [`xver-load-extension-substitutions.md`](./xver-load-extension-substitutions.md) — feeds `_extensionSubstitutionsByElementId`
  - [`xver-load-fhir-cross-version-maps.md`](./xver-load-fhir-cross-version-maps.md) — feeds the algorithmic mappings
  - [`xver-compare-in-database.md`](./xver-compare-in-database.md) — produces the comparison rows consumed here
  - [`xver-export-outcomes.md`](./xver-export-outcomes.md) — consumes the outcomes produced here
  - [`xver-exporter-export.md`](./xver-exporter-export.md) — exporter deep-dive
- Related `ConfigXVer` options: `ReloadDatabase`, `XverArtifactVersion`, `CrossVersionMapSourcePath`.

---
*Legacy outcome references verified at `e36315a1c9d16450ba81457e4f888eff78d4ae42`; safety outcomes and reload routing rechecked at `88af2c4e4fb0f6f770297f0e1209efc212317af6` on `2026-11-05`.*
