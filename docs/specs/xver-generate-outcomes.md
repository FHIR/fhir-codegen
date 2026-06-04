# XVerProcessor.GenerateOutcomes — Step 6 of 7

## Purpose

`GenerateOutcomes` consumes rows produced by `CompareInDatabase` (`DbValueSetComparison`, `DbValueSetConceptComparison`, `DbStructureComparison`, `DbElementComparison`, and type comparisons) and emits outcome rows that `ExportOutcomes` materializes. The written rows are `DbValueSetOutcome`, `DbValueSetConceptOutcome`, `DbStructureOutcome`, `DbElementOutcome`, and `DbElementOutcomeTarget` (`DbOutcomeClasses.cs:292-395,397-775`). This is where headline decisions are made: same-name reuse, renamed reuse, generated cross-version definitions, external extension substitutions, ancestor/parent extension participation, Basic fallback, and unresolved/prohibited extension cases. Important source surprise: the `Outcome*ActionCodes` enums exist, but active generators do not assign enum values; rows encode categories through fields such as `RequiresXVerDefinition`, `IsRenamed`, `Target*Key`, `ExtensionSubstitutionKey`, `BasicElementId`, and `RequiresExtensionDefinition` (`ValueSetOutcomeGenerator.cs:335,405,507`; `DbOutcomeClasses.cs:292-775`).

## Invocation & Preconditions

- Direct callers from `ProcessCommand`: `outcomes` calls `GenerateOutcomes()` (`XVerProcessor.cs:372-382`); `outcomes-vs` calls `GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet)` (`XVerProcessor.cs:384-394`); `outcomes-sd` calls `GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource)` (`XVerProcessor.cs:396-405`); the default full pipeline calls `LoadDatabase`, map/substitution/type loaders, `CompareInDatabase`, `GenerateOutcomes`, and `ExportOutcomes` (`XVerProcessor.cs:421-428`).
- Each `outcomes*` command runs `LoadDatabase`, `LoadFhirCrossVersionMaps`, `LoadExtensionSubstitutions`, and `LoadFhirTypeValueSets` only when `_config.ReloadDatabase == true` (`XVerProcessor.cs:372-405`). The default full pipeline always runs those loaders and `CompareInDatabase` first (`XVerProcessor.cs:421-427`).
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

- Tables written: `ValueSetOutcomes`, `ValueSetConceptOutcomes`, `StructureOutcomes`, `ElementOutcomes`, and `ElementOutcomeTargets` (`DbOutcomeClasses.cs:292-395,397-775`).
- `OutcomeGenerator.GenerateOutcomes` drops and recreates requested outcome tables on every run (`OutcomeGenerator.cs:38-40`; `DbOutcomeClasses.cs:157-193`). This is a destructive reset for the selected output family.
- Structure generation writes both source-element summary rows (`DbElementOutcome`) and per-target/context rows (`DbElementOutcomeTarget`) (`ElementOutcomeGenerator.cs:898-931,969-1002,1147-1181,1820-1930`).
- The enum declarations are documentation/vocabulary for now; the row classes do not have an `OutcomeAction` column and generation does not assign the enum constants (`DbOutcomeClasses.cs:7-144,292-775`).

## Algorithm

1. **Dispatch in `XVerProcessor.GenerateOutcomes`** (`XVerProcessor.cs:619-660`): lazy-load DB, throw if absent, construct `OutcomeGenerator`, and dispatch by `artifactFilter` into vocabulary, structures, or both.
2. **Inside `OutcomeGenerator.GenerateOutcomes`** (`OutcomeGenerator.cs:32-53`): drop/create selected tables, instantiate `ValueSetOutcomeGenerator` when `processValueSets`, and instantiate `StructureOutcomeGenerator` when `processStructures`.
3. **`ValueSetOutcomeGenerator.CreateOutcomesForValueSets`** (`ValueSetOutcomeGenerator.cs:42-77`): load packages ordered by version, build closer-first stepped pairs in both directions, apply `specificPairs`, then per pair run `buildOutcomes(packagePair)` and `applyCachedChanges(packagePair)`.
4. **ValueSet `buildOutcomes`** (`ValueSetOutcomeGenerator.cs:112-735`): load source/target ValueSets, concepts, comparisons, and concept comparisons; skip non-expandable/excluded URLs; create no-map outcomes for source ValueSets with no usable target; otherwise compute concept coverage across all targets, determine whether a generated cross-version definition is needed, and emit ValueSet plus concept outcomes.
5. **`StructureOutcomeGenerator.CreateOutcomesForStructures`** (`StructureOutcomeGenerator.cs:64-110`): use the same closer-first pair schedule, create an `ElementOutcomeGenerator` per pair, run structure `buildOutcomes`, flush caches, run `elementOutcomeGenerator.DoPostProcessing(packagePair)`, and flush again.
6. **Structure `buildOutcomes`** (`StructureOutcomeGenerator.cs:158-580`): load structures, roots, and structure comparisons; skip primitives; create tracking records for mapped/no-map structures; call `ElementOutcomeGenerator.ProcessSourceStructure`; then emit mapped or no-map `DbStructureOutcome` rows.
7. **`ElementOutcomeGenerator` constructor** (`ElementOutcomeGenerator.cs:124-301`): load source/target elements, comparisons, type comparisons, types, resource type sets, Basic/Extension path lookup dictionaries, and `_extensionSubstitutionsByElementId`. Extension substitutions include source-sequence-specific and global rows; exact IDs, cleaned `[x]` IDs, expanded context IDs, and cleaned context IDs all become keys (`ElementOutcomeGenerator.cs:262-301`).
8. **Element classifier** (`ElementOutcomeGenerator.cs:698-1947`): `ProcessSourceStructure` filters ignorable elements, computes mapping completeness, checks required ValueSet binding mappings, then `createOutcomes` walks elements in resource order and decides target rows, cross-version requirement, Basic/Extension replacements, extension substitutions, alternate-reference/canonical substitutions, legal contexts, and final `DbElementOutcome` fields.
9. **Post-processing** (`StructureOutcomeGenerator.cs:107`; `ElementOutcomeGenerator.cs:304-344`): content-reference source elements that did not initially export extensions are promoted to `RequiresExtensionDefinition = true` when referencing outcomes require cross-version definitions.

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
- **Rule:** `UseBasicResource` is the conceptual outcome for an unmapped source resource represented through target `Basic`. **Source:** enum text `DbOutcomeClasses.cs:88-91`; Basic no-target element path `ElementOutcomeGenerator.cs:821-839,859-932`. **Rationale:** Basic is the target resource available for otherwise unmapped resource data.
- **Rule:** `UseDatatypeExtension` is the conceptual outcome for an unmapped non-resource type represented as Extension. **Source:** enum text `DbOutcomeClasses.cs:92-95`; non-resource no-target path `ElementOutcomeGenerator.cs:933-1003`; complex-type-to-extension flag `ElementOutcomeGenerator.cs:1247-1255`. **Rationale:** AI Guess: datatypes have no Basic equivalent, so the portable target representation is an extension profile.
- **Rule:** Structure `UseOneOf` is commented out and not implemented. **Source:** `DbOutcomeClasses.cs:96-100`; no `OutcomeStructureActionCodes` assignments in the generator. **Rationale:** AI Guess: multi-target mappings are represented by multiple target records/counts instead.

### Element outcomes

- **Rule:** `ProcessSourceStructure` filters out id/extension/modifierExtension elements, determines type/relationship completeness, checks required ValueSet bindings, then calls `createOutcomes`. **Source:** `ElementOutcomeGenerator.cs:347-357,698-728`. **Rationale:** The final row needs structural, type, terminology, and context state.
- **Rule:** Mapping-compatible relationships are `null`, `Equivalent`, and `SourceIsNarrowerThanTarget`. **Source:** `ElementOutcomeGenerator.cs:682-685`. **Rationale:** AI Guess: null is treated permissively for identity/default comparison paths; equivalent and source-narrower preserve source meaning.
- **Rule:** An element initially requires xver when it is non-root, not fully mapped across all targets, and leaf; no-target rows and parent requirements can also force xver. **Source:** `ElementOutcomeGenerator.cs:788-791,1185-1203`. **Rationale:** Leaf source data with no complete native target needs representation, and children of generated definitions must stay inside that definition.
- **Rule:** `UseElementSameName` means a mapped target/context name matches and no extension/slice is required. **Source:** enum `DbOutcomeClasses.cs:102-111`; target selection and fields `ElementOutcomeGenerator.cs:1590-1596,1844-1854,1897-1905`. **Rationale:** AI Guess: inferred from target rows, `IsRenamed == false`, and no xver requirement because the enum is not assigned.
- **Rule:** `UseElementRenamed` means the selected target/context name differs. **Source:** enum `DbOutcomeClasses.cs:108-111`; `IsRenamed = sourceEd.Name != targetEd.Name` in `ElementOutcomeGenerator.cs:1590-1596,1897`. **Rationale:** Rename is tracked separately from mapping completeness.
- **Rule:** `UseExtension` means `RequiresXVerDefinition` is true and the element must define its own extension, not a slice, Basic element, external substitution, or content-reference definition. **Source:** enum `DbOutcomeClasses.cs:112-115`; computation `ElementOutcomeGenerator.cs:1616-1621`; generated fields `ElementOutcomeGenerator.cs:1852-1860`. **Rationale:** Export needs a standalone extension StructureDefinition.
- **Rule:** `UseExtensionFromAncestor` means the element participates in an ancestor/parent generated definition. **Source:** enum `DbOutcomeClasses.cs:116-119`; propagation and slice fields `ElementOutcomeGenerator.cs:1192-1203,1224-1237,1616-1621,1884-1885`. **Rationale:** The source child is represented under an existing generated parent context.
- **Rule:** `UseBasicElement` means an otherwise-xver resource element matches a compatible target `Basic` element path. **Source:** enum `DbOutcomeClasses.cs:120-123`; Basic path logic `ElementOutcomeGenerator.cs:1239-1245,1258-1289`; stored fields `ElementOutcomeGenerator.cs:1890-1891`. **Rationale:** Reusing Basic avoids unnecessary generated extensions.
- **Rule:** `Unresolved` means xver is needed but extension definition is prohibited, especially for Resource-typed values. **Source:** enum `DbOutcomeClasses.cs:128-131`; prohibition logic `ElementOutcomeGenerator.cs:1804-1815`; stored fields `ElementOutcomeGenerator.cs:1849-1850`. **Rationale:** AI Guess: these rows remain for review/diagnostics because safe extension generation is not possible.
- **Rule:** `IsExtension` is represented by omission: source extension/modifierExtension elements are skipped and no outcome row is created. **Source:** enum `DbOutcomeClasses.cs:132-135`; skip logic `ElementOutcomeGenerator.cs:347-357,707-709`. **Rationale:** Source-side extensions already have extension semantics.
- **Rule:** `IsElementId` and `MappedElsewhere` appear in the enum but are not among the requested seven active categories and are not assigned by current generation. **Source:** `DbOutcomeClasses.cs:136-143`; repository search found no active assignment sites. **Rationale:** Unresolved: source does not explain whether these are legacy or planned categories.
- **Rule:** Element `UseOneOf` is commented out and not implemented. **Source:** `DbOutcomeClasses.cs:124-127`; no `OutcomeElementActionCodes` assignments in `ElementOutcomeGenerator.cs`. **Rationale:** AI Guess: multi-target cases are represented with multiple `DbElementOutcomeTarget` rows.
- **Rule:** `_extensionSubstitutionsByElementId` is built from source-sequence-specific and global `DbExtensionSubstitution` rows; duplicate keys silently overwrite earlier entries. **Source:** `ElementOutcomeGenerator.cs:262-301`, especially writes at `280,285,293,297`. **Rationale:** AI Guess: substitution rows are SME override data; the code does not log conflicts.
- **Rule:** A matching extension substitution prevents generating a new extension by making `extSubstitute` non-null and storing substitution fields. **Source:** lookup `ElementOutcomeGenerator.cs:1432-1455`; `requiresExtensionDefinition` condition `ElementOutcomeGenerator.cs:1616-1621`; fields `ElementOutcomeGenerator.cs:1894-1895`. **Rationale:** AI Guess: curated external extensions should be reused rather than duplicated. Caveat: later alternate-reference/canonical logic can overwrite `extSubstitute` (`ElementOutcomeGenerator.cs:1509-1512,1550-1553`).
- **Rule:** Standard `alternate-reference` and `alternate-canonical` substitutions can absorb unmapped Reference/CodeableReference/canonical target-profile gaps; if they remove all unmapped types, xver is cleared. **Source:** `ElementOutcomeGenerator.cs:1465-1582`. **Rationale:** AI Guess: these standard extensions preserve narrower target profile/canonical allowances without generating a full element extension.
- **Rule:** Required ValueSet bindings can make an element incomplete even when structural mapping exists. **Source:** `ElementOutcomeGenerator.cs:1949-2018`. **Rationale:** Required bindings constrain legal codes, so target binding relationship matters.
- **Rule:** Quantity-like unmapped types can be considered mapped by normalized quantity type equivalence, structure comparisons, or structure mappings. **Source:** `ElementOutcomeGenerator.cs:2211-2420`. **Rationale:** AI Guess: Quantity profile names vary while remaining semantically representable.
- **Rule:** A leaf type can map by distributing its child elements across target elements when all meaningful children map. **Source:** `ElementOutcomeGenerator.cs:2422-2462,2576-2880`. **Rationale:** AI Guess: some version changes flatten/expand complex source values across target fields.
- **Rule:** Choice-type and modifier-extension contexts may be promoted to parent contexts to find legal extension placement. **Source:** modifier context `ElementOutcomeGenerator.cs:1326-1429`; choice context `ElementOutcomeGenerator.cs:1680-1764`. **Rationale:** FHIR extensions must be placed on legal containing elements.
- **Rule:** If xver is still needed and no context target exists, context falls back to the sole target structure root or generic `Element`. **Source:** `ElementOutcomeGenerator.cs:1766-1792`. **Rationale:** AI Guess: this prevents dropping extension definitions solely because no precise context was found.
- **Rule:** `DoPostProcessing` promotes content-reference outcomes to extension definitions when referencing outcomes require xver. **Source:** call `StructureOutcomeGenerator.cs:107`; implementation `ElementOutcomeGenerator.cs:304-344`. **Rationale:** Referenced generated definitions must exist for dependent extensions.

## Rationale Coverage

`Decisions: 52 total — cited: 35 — AI Guess: 16 — unresolved: 1`

Every rule above has a source citation. `cited` means the rationale is directly supported by code structure, comments, enum docs, or stored fields; `AI Guess` means the rule is source-backed but the reason is inferred; `unresolved` means the behavior is visible but the source gives no reason. The unresolved item is the unused `IsElementId` / `MappedElsewhere` element enum vocabulary.

## Failure Modes & Edge Cases

- `GenerateOutcomes` throws when `LoadDatabase(false)` fails to populate `_db` (`XVerProcessor.cs:619-627`).
- Outcome generators do not validate that comparison rows exist; a DB with empty comparison tables will still drop/recreate outcome tables and then produce empty or mostly no-map outputs depending on source content (`OutcomeGenerator.cs:38-53`; `ValueSetOutcomeGenerator.cs:122-140`; `StructureOutcomeGenerator.cs:179-187`).
- Re-running `outcomes*` always wipes the selected outcome tables first (`OutcomeGenerator.cs:38-40`).
- Extension-substitution conflicts are silent; the last dictionary write for a key wins (`ElementOutcomeGenerator.cs:269-301`).
- Source id/extension/modifierExtension elements are skipped, not written as explicit `IsElementId` / `IsExtension` rows (`ElementOutcomeGenerator.cs:347-357,707-709`).
- Context promotion can throw instead of producing an unresolved row if a required parent context cannot be found (`ElementOutcomeGenerator.cs:1391-1397,1726-1732`).
- Basic/Extension path replacement happens before element-ID extension substitution, and alternate-reference/canonical can later overwrite `extSubstitute` (`ElementOutcomeGenerator.cs:1258-1297,1432-1455,1509-1512,1550-1553`).

## Coverage Checklist

- [x] `XVerProcessor.GenerateOutcomes` orchestration (cs:614-661)
- [x] `OutcomeGenerator.GenerateOutcomes` orchestration
- [x] `ValueSetOutcomeGenerator.CreateOutcomesForValueSets`
- [x] `StructureOutcomeGenerator.CreateOutcomesForStructures`
- [x] `ElementOutcomeGenerator.ProcessSourceStructure` / `createOutcomes`
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
  - [`xver-processor-write-fhir.md`](./xver-processor-write-fhir.md) — exporter deep-dive
- Related `ConfigXVer` options: `ReloadDatabase`, `XverArtifactVersion`, `CrossVersionMapSourcePath`.

---
*Verified against commit `d02100974b2dc1b05ecf1af69c29095e6973f4c8` on `2026-06-04`.*
