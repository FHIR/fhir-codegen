// <copyright file="UnsafeCodeOutcomeGenerator.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Model;

namespace Fhir.CodeGen.Comparison.Outcomes;

internal sealed class UnsafeCodeOutcomeGenerator(IDbConnection db)
{
    internal void GenerateForPair(FhirPackageComparisonPair pair, DbUnsafeCodePairState? dependency = null)
    {
        dependency ??= UnsafeCodeInputStore.BeginOutcomes(db, pair.SourcePackageKey, pair.TargetPackageKey);
        PairFacts facts = new(db, pair);
        if (!facts.HasCandidates)
        {
            UnsafeCodeInputStore.CompleteOutcomes(db, dependency);
            return;
        }

        List<UnsafeCodeTrigger> triggers = [];
        foreach (DbStructureOutcome outcome in facts.Outcomes.Values)
        {
            if (!facts.Structures.TryGetValue(outcome.SourceStructureKey, out DbStructureDefinition? structure) ||
                !facts.IsResource(structure.Key))
            {
                continue;
            }

            foreach (DbElement root in facts.Roots[structure.Key])
            {
                Walk(root, [], [], false, new HashSet<(int, int, string)>());
            }

            void Walk(
                DbElement element,
                ImmutableArray<UnsafeCodePathStep> prefix,
                ImmutableArray<MappingUse> inheritedContexts,
                bool carrierCode,
                HashSet<(int, int, string)> edges,
                string? typeName = null,
                bool contentReference = false)
            {
                if (element.MaxCardinalityString == "0")
                {
                    return;
                }

                ImmutableArray<UnsafeCodePathStep> path = prefix.Add(new(element.Id, typeName, contentReference));
                ImmutableArray<MappingUse> contexts = [.. facts.Contexts(element, outcome).Concat(inheritedContexts).Distinct()];

                string[] typeNames = facts.TypeNames(element);
                bool carrier = typeNames.Any(IsCarrier);
                ImmutableArray<MappingUse> childContexts = carrier || typeNames.Any(name => name is "CodeableConcept" or "CodeableReference")
                    ? contexts : [];
                if (carrier)
                {
                    Classify(element, path, contexts, true);
                }
                else if (!carrierCode && typeNames.Contains("code", StringComparer.Ordinal))
                {
                    Classify(element, path, contexts, false);
                }

                if (prefix.Length > 0 && facts.IsResourceUse(element))
                {
                    return;
                }

                foreach (DbElement child in facts.Children(element))
                {
                    Follow(child, "child", null, false, carrier && RelativeName(child, element) == "code", childContexts);
                }

                foreach ((string? TypeName, int StructureKey) type in facts.TypeStructures(element))
                {
                    if (facts.IsResource(type.StructureKey))
                    {
                        continue;
                    }

                    foreach (DbElement root in facts.Roots[type.StructureKey])
                    {
                        // Inline snapshot children override the corresponding type fields.
                        foreach (DbElement child in facts.Children(root))
                        {
                            if (!facts.Children(element).Any(local => RelativeName(local, element) == RelativeName(child, root)))
                            {
                                Follow(child, "type", type.TypeName, false, carrier && RelativeName(child, root) == "code", childContexts);
                            }
                        }
                    }
                }

                if (element.ContentReferenceSourceKey is int referenceKey && facts.SourceElements.TryGetValue(referenceKey, out DbElement? reference))
                {
                    Follow(reference, "reference", null, true, carrierCode, contexts);
                }

                void Follow(DbElement next, string kind, string? nextType, bool isReference, bool inCarrier, ImmutableArray<MappingUse> uses)
                {
                    // Keep the first recurrence in the semantic path; repeated edges close the graph.
                    (int, int, string) edge = (element.Key, next.Key, kind);
                    if (edges.Add(edge))
                    {
                        Walk(next, path, uses, inCarrier, edges, nextType, isReference);
                        edges.Remove(edge);
                    }
                }
            }

            void Classify(DbElement element, ImmutableArray<UnsafeCodePathStep> path, ImmutableArray<MappingUse> contexts, bool typed)
            {
                if (contexts.IsEmpty)
                {
                    contexts = [new(new(pair.SourcePackageKey, pair.TargetPackageKey, element.Id, string.Empty), element, null)];
                }

                foreach (MappingUse use in contexts)
                {
                    UnsafeCodeElementAnnotation[] annotations = [.. facts.Annotations.Where(annotation => annotation.Context == use.Context)];
                    UnsafeCodeMapEvidence[] evidence = facts.EvidenceFor(use);
                    UnsafeCodeMapEvidence[] potentialEvidence = facts.PotentialEvidenceFor(use);
                    if (annotations.Length > 0 && potentialEvidence.Any(item => item.ExplicitNoMap && item.SourceCode is not null && item.SourceSystem.Url is null))
                    {
                        throw new InvalidOperationException($"Unsafe-code identity is ambiguous for {element.Id}, pair {pair.SourcePackageKey}->{pair.TargetPackageKey}: the applicable authored no-map code has no qualified source system. Supply authoritative source binding context and regenerate outcomes.");
                    }

                    UnsafeCodeIdentity[] candidates = [.. facts.Rules.Where(rule => !rule.NoMapOnly || potentialEvidence.Any(item =>
                            item.ExplicitNoMap && item.SourceSystem.Url == rule.Identity.System && item.SourceCode == rule.Identity.Code &&
                            (rule.Identity.Version is null || item.SourceSystem.Version is null || rule.Identity.Version == item.SourceSystem.Version)))
                        .Select(rule => rule.Identity)
                        .Concat(annotations.Length == 0 ? [] : potentialEvidence.Where(item => item.ExplicitNoMap && item.SourceSystem.Url is not null && item.SourceCode is not null)
                            .Select(item => new UnsafeCodeIdentity(item.SourceSystem.Url!, item.SourceSystem.Version, item.SourceCode!)))
                        .Distinct()];
                    foreach (IGrouping<(string System, string Code), UnsafeCodeIdentity> group in candidates.GroupBy(candidate => (candidate.System, candidate.Code)))
                    {
                        UnsafeCodeIdentity identity = new(group.Key.System, null, group.Key.Code);
                        if (!typed)
                        {
                            UnsafeCodeIdentity? resolved = facts.PrimitiveIdentity(element, use.Source, identity, group, outcome);
                            if (resolved is null)
                            {
                                continue;
                            }

                            identity = resolved;
                        }

                        if (potentialEvidence.Except(evidence).Any(item => item.ExplicitNoMap &&
                            item.SourceSystem.Url == identity.System && item.SourceCode == identity.Code &&
                            VersionsOverlap(item.SourceSystem.Version, identity.Version) &&
                            (annotations.Length > 0 || facts.Rules.Any(rule => rule.NoMapOnly &&
                                rule.Identity.System == identity.System && rule.Identity.Code == identity.Code &&
                                VersionsOverlap(rule.Identity.Version, identity.Version) &&
                                VersionsOverlap(rule.Identity.Version, item.SourceSystem.Version)))))
                        {
                            throw new InvalidOperationException(
                                $"Unsafe-code binding scope is ambiguous for {element.Id}, mapping {use.Context.SourceElementId}->{use.Context.TargetElementId}, pair {pair.SourcePackageKey}->{pair.TargetPackageKey}, candidate {identity.System}|{identity.Version}#{identity.Code}. Resolve the applicable authored no-map source/target binding scopes and regenerate outcomes.");
                        }

                        foreach (UnsafeCodeDecision decision in UnsafeCodePolicy.Classify(
                            identity, pair.TargetFhirSequence.ToLongVersion(), use.Context, annotations, evidence, facts.Rules))
                        {
                            triggers.Add(new(
                                outcome.Key, use.Context, path, decision.Identity, decision.VersionMatch,
                                [.. facts.EvidenceRows.Where(row => decision.EvidenceProvenance.Contains(Provenance(row))).Select(row => row.Key).Order()],
                                [.. facts.RuleRows.Where(row => decision.RuleProvenance.Contains(new(row.SourceRelativeFilename, row.EntryIndex))).Select(row => row.Key).Order()]));
                        }
                    }
                }
            }
        }

        IEnumerable<UnsafeCodeTrigger> normalized = triggers.GroupBy(trigger => (
            trigger.StructureOutcomeKey, trigger.Context, JsonSerializer.Serialize(trigger.SourcePath),
            trigger.Identity, trigger.VersionMatch))
            .Select(group => group.First() with
            {
                EvidenceKeys = [.. group.SelectMany(trigger => trigger.EvidenceKeys).Distinct().Order()],
                RuleKeys = [.. group.SelectMany(trigger => trigger.RuleKeys).Distinct().Order()],
            });
        UnsafeCodeInputStore.InsertTriggers(db, normalized, facts.AnnotationKeys);
        UnsafeCodeInputStore.CompleteOutcomes(db, dependency);
    }

    private static bool IsCarrier(string? typeName) => typeName is
        "Coding" or "Quantity" or "Age" or "Count" or "Distance" or "Duration" or "MoneyQuantity" or "SimpleQuantity";

    private static string RelativeName(DbElement child, DbElement parent) =>
        child.Id.StartsWith(parent.Id + ".", StringComparison.Ordinal) ? child.Id[(parent.Id.Length + 1)..] : child.Id;

    private static bool VersionsOverlap(string? left, string? right) => left is null || right is null || left == right;

    private static UnsafeCodeProvenance Provenance(DbUnsafeCodeMapEvidence row) =>
        new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex);

    private sealed record MappingUse(UnsafeCodeMappingContext Context, DbElement Source, DbElement? Target);

    private sealed class PairFacts
    {
        private readonly FhirPackageComparisonPair _pair;
        private readonly Dictionary<int, DbElement> _targetElements;
        private readonly ILookup<int, DbElement> _children;
        private readonly ILookup<(int StructureKey, string ParentId), DbElement> _looseChildren;
        private readonly ILookup<int, DbElementType> _types;
        private readonly Dictionary<int, DbElementComparison> _comparisons;
        private readonly Dictionary<int, DbElementMapping> _mappings;
        private readonly Dictionary<int, DbElementOutcome> _elementOutcomes;
        private readonly ILookup<int, DbElementOutcomeTarget> _targets;
        private readonly Dictionary<int, DbValueSet> _valueSets;
        private readonly ILookup<int, DbValueSetConcept> _concepts;
        private readonly Dictionary<int, BindingScope?> _bindings = [];
        private readonly Dictionary<int, DbElement[]> _effectiveChildren = [];
        private readonly Dictionary<int, DbElementType[]> _effectiveTypes = [];
        private readonly Dictionary<MappingUse, UnsafeCodeMapEvidence[]> _scopedEvidence = [];
        private readonly Dictionary<MappingUse, UnsafeCodeMapEvidence[]> _potentialEvidence = [];
        private readonly Dictionary<(int ElementKey, int OwnerKey), ImmutableArray<MappingUse>> _contexts = [];
        private readonly Dictionary<int, string[]> _typeNames = [];

        internal Dictionary<int, DbStructureDefinition> Structures { get; }
        internal Dictionary<int, DbElement> SourceElements { get; }
        internal ILookup<int, DbElement> Roots { get; }
        internal Dictionary<int, DbStructureOutcome> Outcomes { get; }
        internal UnsafeCodeElementAnnotation[] Annotations { get; }
        internal DbUnsafeCodeMapEvidence[] EvidenceRows { get; }
        internal DbUnsafeCodeRule[] RuleRows { get; }
        internal UnsafeCodeRule[] Rules { get; }
        internal bool HasCandidates => Rules.Any(rule => !rule.NoMapOnly) ||
            (Evidence.Any(item => item.ExplicitNoMap) && (Annotations.Length > 0 || Rules.Length > 0));
        internal Dictionary<UnsafeCodeMappingContext, int[]> AnnotationKeys { get; }
        private UnsafeCodeMapEvidence[] Evidence { get; }

        internal PairFacts(IDbConnection db, FhirPackageComparisonPair pair)
        {
            _pair = pair;
            Structures = DbStructureDefinition.SelectDict(db, FhirPackageKey: pair.SourcePackageKey);
            SourceElements = DbElement.SelectDict(db, FhirPackageKey: pair.SourcePackageKey);
            _targetElements = DbElement.SelectDict(db, FhirPackageKey: pair.TargetPackageKey);
            _children = SourceElements.Values.Where(element => element.ParentElementKey is not null).ToLookup(element => element.ParentElementKey!.Value);
            _looseChildren = SourceElements.Values.Where(element => element.ParentElementKey is null && element.Id.Contains('.', StringComparison.Ordinal))
                .ToLookup(element => (element.StructureKey, element.Id[..element.Id.LastIndexOf('.')]));
            Roots = SourceElements.Values.Where(element => element.ParentElementKey is null && !element.Path.Contains('.', StringComparison.Ordinal)).ToLookup(element => element.StructureKey);
            _types = DbElementType.SelectList(db, FhirPackageKey: pair.SourcePackageKey).ToLookup(type => type.ElementKey);
            Outcomes = DbStructureOutcome.SelectDict(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
            _comparisons = DbElementComparison.SelectDict(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
            _mappings = DbElementMapping.SelectDict(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
            _elementOutcomes = DbElementOutcome.SelectDict(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
            _targets = DbElementOutcomeTarget.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
                .Where(target => _elementOutcomes.ContainsKey(target.ElementOutcomeKey))
                .ToLookup(target => _elementOutcomes[target.ElementOutcomeKey].SourceElementKey);
            _valueSets = DbValueSet.SelectList(db).Where(row => row.FhirPackageKey == pair.SourcePackageKey || row.FhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
            _concepts = DbValueSetConcept.SelectList(db).Where(row => _valueSets.ContainsKey(row.ValueSetKey)).ToLookup(row => row.ValueSetKey);
            DbUnsafeCodeElementAnnotation[] annotationRows = [.. DbUnsafeCodeElementAnnotation.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
                .Where(row => row.RequiresOmittedMandatoryCode)];
            AnnotationKeys = annotationRows.GroupBy(row => new UnsafeCodeMappingContext(row.SourceFhirPackageKey, row.TargetFhirPackageKey, row.SourceElementId, row.TargetElementId))
                .ToDictionary(group => group.Key, group => group.Select(row => row.Key).Order().ToArray());
            Annotations = [.. annotationRows
                .Select(row => new UnsafeCodeElementAnnotation(
                    new(row.SourceFhirPackageKey, row.TargetFhirPackageKey, row.SourceElementId, row.TargetElementId),
                    new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex)))];
            EvidenceRows = [.. DbUnsafeCodeMapEvidence.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)];
            Evidence = [.. EvidenceRows.Select(row => new UnsafeCodeMapEvidence(
                row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                new(row.SourceValueSetUrl, row.SourceValueSetVersion), new(row.TargetValueSetUrl, row.TargetValueSetVersion),
                new(row.SourceSystem, row.SourceSystemVersion), row.SourceCode,
                new(row.TargetSystem, row.TargetSystemVersion), row.TargetCode, row.Relationship, row.ExplicitNoMap, Provenance(row)))];
            RuleRows = [.. DbUnsafeCodeRule.SelectList(db)];
            Rules = [.. RuleRows.Select(UnsafeCodeInputStore.ReadRule)];
        }

        internal bool IsResource(int key)
        {
            if (!Structures.TryGetValue(key, out DbStructureDefinition? structure))
            {
                return false;
            }

            if (structure.ArtifactClass == FhirArtifactClassEnum.Resource)
            {
                return true;
            }

            return structure.ArtifactClass == FhirArtifactClassEnum.Profile && Roots[key].Any(root =>
                BaseChain(root).Any(element => Structures.GetValueOrDefault(element.StructureKey)?.ArtifactClass == FhirArtifactClassEnum.Resource) ||
                Types(root).Any(type => type.TypeStructureKey is int typeKey &&
                    Structures.GetValueOrDefault(typeKey)?.ArtifactClass == FhirArtifactClassEnum.Resource));
        }

        private IEnumerable<DbElement> BaseChain(DbElement element)
        {
            HashSet<int> visited = [];
            while (visited.Add(element.Key))
            {
                yield return element;
                Dictionary<int, DbElement> elements = element.FhirPackageKey == _pair.SourcePackageKey ? SourceElements : _targetElements;
                DbElement? next = element.BaseElementKey is int key ? elements.GetValueOrDefault(key) : null;
                if (next is null && element.BaseStructureKey is int structureKey && !element.Path.Contains('.', StringComparison.Ordinal))
                {
                    next = Roots[structureKey].SingleOrDefault();
                }

                if (next is null)
                {
                    yield break;
                }

                element = next;
            }
        }

        internal DbElement[] Children(DbElement element)
        {
            if (!_effectiveChildren.TryGetValue(element.Key, out DbElement[]? children))
            {
                Dictionary<string, DbElement> merged = new(StringComparer.Ordinal);
                foreach (DbElement basis in BaseChain(element))
                {
                    IEnumerable<DbElement> direct = _children[basis.Key].Concat(_looseChildren[(basis.StructureKey, basis.Id)]);
                    foreach (DbElement child in direct)
                    {
                        merged.TryAdd(RelativeName(child, basis), child);
                    }
                }

                children = [.. merged.Values.OrderBy(child => child.ResourceFieldOrder)];
                _effectiveChildren.Add(element.Key, children);
            }

            return children;
        }

        internal DbElementType[] Types(DbElement element)
        {
            if (!_effectiveTypes.TryGetValue(element.Key, out DbElementType[]? types))
            {
                types = BaseChain(element).Select(basis => _types[basis.Key].ToArray()).FirstOrDefault(rows => rows.Length > 0) ?? [];
                _effectiveTypes.Add(element.Key, types);
            }

            return types;
        }

        internal string[] TypeNames(DbElement element)
        {
            if (!_typeNames.TryGetValue(element.Key, out string[]? names))
            {
                names = [.. Types(element).Select(type => type.TypeName).OfType<string>()
                    .Concat(BaseChain(element).Select(basis => basis.DistinctTypes.Where(name => name.Length > 0).ToArray())
                        .FirstOrDefault(names => names.Length > 0) ?? []).Distinct(StringComparer.Ordinal)];
                _typeNames.Add(element.Key, names);
            }

            return names;
        }

        internal IEnumerable<(string? TypeName, int StructureKey)> TypeStructures(DbElement element)
        {
            DbElementType[] types = Types(element);
            if (types.Length == 0)
            {
                foreach (string name in TypeNames(element))
                {
                    DbStructureDefinition[] structures = [.. Structures.Values.Where(structure => structure.Name == name && structure.ArtifactClass != FhirArtifactClassEnum.Profile)];
                    if (structures.Length == 1)
                    {
                        RequireTypeRoot(structures[0].Key, name);
                        yield return (name, structures[0].Key);
                    }
                    else if (name is "CodeableConcept" or "CodeableReference" && Children(element).Length == 0)
                    {
                        throw new InvalidOperationException($"Unsafe-code type graph is unresolved for {element.Id} ({name}), pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Reload source definitions and regenerate comparisons and outcomes.");
                    }
                }

                yield break;
            }

            foreach (DbElementType type in types)
            {
                int? key = type.TypeProfileStructureKey ?? (type.TypeProfile is null ? type.TypeStructureKey : null);
                if (key is null)
                {
                    UnsafeCodeCanonical profile = UnsafeCodeCanonical.Parse(type.TypeProfile);
                    DbStructureDefinition[] structures = [.. Structures.Values.Where(structure => profile.Url is not null
                        ? structure.UnversionedUrl == profile.Url && (profile.Version is null || structure.Version == profile.Version)
                        : structure.Name == type.TypeName && structure.ArtifactClass != FhirArtifactClassEnum.Profile)];
                    key = structures.Length == 1 ? structures[0].Key : null;
                }

                if (key is int resolved)
                {
                    RequireTypeRoot(resolved, type.TypeName);
                    yield return (type.TypeProfile ?? type.TypeName, resolved);
                }
                else if (type.TypeProfile is not null || type.TypeName is "CodeableConcept" or "CodeableReference")
                {
                    throw new InvalidOperationException($"Unsafe-code type graph is unresolved for {element.Id} ({type.Literal}), pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Reload source definitions and regenerate comparisons and outcomes.");
                }
            }

            void RequireTypeRoot(int key, string? name)
            {
                if (name is "CodeableConcept" or "CodeableReference" && !Roots[key].Any() && Children(element).Length == 0)
                {
                    throw new InvalidOperationException($"Unsafe-code type graph is incomplete for {element.Id} ({name}), pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Reload source definitions and regenerate comparisons and outcomes.");
                }
            }
        }

        internal bool IsResourceUse(DbElement element) => TypeNames(element).Any(name => name is "Resource" or "DomainResource") ||
            Types(element).Any(type => (type.TypeProfileStructureKey ?? type.TypeStructureKey) is int key && IsResource(key));

        internal ImmutableArray<MappingUse> Contexts(DbElement element, DbStructureOutcome owner)
        {
            if (_contexts.TryGetValue((element.Key, owner.Key), out ImmutableArray<MappingUse> cached))
            {
                return cached;
            }

            List<MappingUse> uses = [];
            foreach (DbElement basis in BaseChain(element))
            {
                foreach (DbElementOutcomeTarget target in _targets[basis.Key])
                {
                    if (!Outcomes.TryGetValue(target.StructureOutcomeKey, out DbStructureOutcome? contributing) ||
                        (basis.StructureKey == owner.SourceStructureKey && contributing.Key != owner.Key) ||
                        contributing.SourceStructureKey != basis.StructureKey ||
                        target.ElementComparisonKey is not int comparisonKey ||
                        !_comparisons.TryGetValue(comparisonKey, out DbElementComparison? comparison) ||
                        comparison.SourceElementKey != basis.Key || comparison.SourceElementId != basis.Id ||
                        comparison.SourceStructureKey != basis.StructureKey ||
                        comparison.TargetElementKey != target.TargetElementKey || comparison.TargetElementId != target.TargetElementId)
                    {
                        continue;
                    }

                    if (comparison.ElementMappingKey is int mappingKey &&
                        (!_mappings.TryGetValue(mappingKey, out DbElementMapping? mapping) ||
                         mapping.SourceElementId != comparison.SourceElementId || mapping.TargetElementId != comparison.TargetElementId ||
                         mapping.SourceStructureKey != basis.StructureKey || mapping.TargetStructureKey != comparison.TargetStructureKey ||
                         (mapping.SourceElementKey is int sourceKey && sourceKey != basis.Key) ||
                         (mapping.TargetElementKey is int targetKey && targetKey != comparison.TargetElementKey)))
                    {
                        throw new InvalidOperationException($"Unsafe-code comparison provenance is inconsistent for {basis.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Rerun comparisons and outcomes.");
                    }

                    DbElement? targetElement = comparison.TargetElementKey is int key ? _targetElements.GetValueOrDefault(key) : null;
                    if (targetElement is not null && targetElement.Id == comparison.TargetElementId &&
                        targetElement.StructureKey == comparison.TargetStructureKey)
                    {
                        uses.Add(new(new(_pair.SourcePackageKey, _pair.TargetPackageKey, basis.Id, targetElement.Id), basis, targetElement));
                    }
                }

                // Authored annotations survive maps that could not be normalized. Require the exact
                // directed target in a contributing structure, never a similarly named leaf.
                HashSet<int> targetStructures = [.. Outcomes.Values.Where(row => row.SourceStructureKey == basis.StructureKey &&
                    (basis.StructureKey != owner.SourceStructureKey || row.Key == owner.Key))
                    .Select(row => row.TargetStructureKey).OfType<int>()];
                bool hasContributingTarget = _targets[basis.Key].Any(target => Outcomes.TryGetValue(target.StructureOutcomeKey, out DbStructureOutcome? contributing) &&
                    contributing.SourceStructureKey == basis.StructureKey && (basis.StructureKey != owner.SourceStructureKey || contributing.Key == owner.Key));
                foreach (UnsafeCodeElementAnnotation annotation in Annotations.Where(annotation =>
                    !hasContributingTarget && annotation.Context.SourceElementId == basis.Id))
                {
                    foreach (DbElement target in _targetElements.Values.Where(target => target.Id == annotation.Context.TargetElementId && targetStructures.Contains(target.StructureKey)))
                    {
                        uses.Add(new(annotation.Context, basis, target));
                    }
                }

                if (uses.Count > 0)
                {
                    break;
                }
            }

            ImmutableArray<MappingUse> result = [.. uses.Distinct()];
            _contexts.Add((element.Key, owner.Key), result);
            return result;
        }

        internal UnsafeCodeMapEvidence[] EvidenceFor(MappingUse use)
        {
            if (_scopedEvidence.TryGetValue(use, out UnsafeCodeMapEvidence[]? cached))
            {
                return cached;
            }

            BindingScope? source = Binding(use.Source);
            BindingScope? target = use.Target is null ? null : Binding(use.Target);
            UnsafeCodeMapEvidence[] evidence = [.. Evidence.Select(item => QualifyEvidence(item, source)).Where(item =>
                ScopeMatches(item.SourceValueSet, source) && ScopeMatches(item.TargetValueSet, target) &&
                (source is null || item.SourceSystem.Url is null || source.Allows(item.SourceSystem, item.SourceCode, item.SourceValueSet)) &&
                (item.TargetSystem.Url is null || (target is not null && target.Allows(item.TargetSystem, item.TargetCode, item.TargetValueSet))))];
            _scopedEvidence.Add(use, evidence);
            return evidence;
        }

        internal UnsafeCodeMapEvidence[] PotentialEvidenceFor(MappingUse use)
        {
            if (_potentialEvidence.TryGetValue(use, out UnsafeCodeMapEvidence[]? cached))
            {
                return cached;
            }

            BindingScope? source = Binding(use.Source);
            BindingScope? target = use.Target is null ? null : Binding(use.Target);
            UnsafeCodeMapEvidence[] evidence = [.. Evidence.Select(item => QualifyEvidence(item, source)).Where(item =>
                MayMatch(item.SourceValueSet, source) && MayMatch(item.TargetValueSet, target) &&
                (source is null || !source.Complete || item.SourceSystem.Url is null || source.Allows(item.SourceSystem, item.SourceCode, item.SourceValueSet)) &&
                (target is null || !target.Complete || item.TargetSystem.Url is null || target.Allows(item.TargetSystem, item.TargetCode, item.TargetValueSet)))];
            _potentialEvidence.Add(use, evidence);
            return evidence;
        }

        private static UnsafeCodeMapEvidence QualifyEvidence(UnsafeCodeMapEvidence evidence, BindingScope? source)
        {
            if (evidence.SourceSystem.Url is null && evidence.SourceCode is string code && source?.Complete == true)
            {
                UnsafeCodeCanonical[] identities = source.Identities(code, evidence.SourceValueSet);
                if (identities.Length == 1)
                {
                    return evidence with { SourceSystem = identities[0] };
                }
            }

            return evidence;
        }

        private static bool MayMatch(UnsafeCodeCanonical canonical, BindingScope? binding)
        {
            if (canonical.Url is null || binding is null)
            {
                return true;
            }

            UnsafeCodeCanonical[] scopes = [.. binding.Canonicals.Where(scope => scope.Url == canonical.Url)];
            return scopes.Length > 0 && (canonical.Version is null ||
                scopes.All(scope => scope.Version is null) || scopes.Any(scope => scope.Version == canonical.Version));
        }

        private static bool ScopeMatches(UnsafeCodeCanonical canonical, BindingScope? binding) =>
            canonical.Url is null || (binding is not null && binding.Canonicals.Any(scope =>
                scope.Url == canonical.Url && (canonical.Version is null || scope.Version == canonical.Version)));

        internal UnsafeCodeIdentity? PrimitiveIdentity(
            DbElement element,
            DbElement bindingElement,
            UnsafeCodeIdentity candidate,
            IEnumerable<UnsafeCodeIdentity> candidates,
            DbStructureOutcome owner)
        {
            BindingScope? binding = Binding(element) ?? Binding(bindingElement);
            if (binding is not null && binding.Required)
            {
                UnsafeCodeCanonical[] identities = binding.Identities(candidate.Code);
                if (binding.Complete && identities.Length > 0 && identities.All(identity => identity.Url != candidate.System))
                {
                    return null;
                }

                if (binding.Complete && identities.Length == 0)
                {
                    return null;
                }

                UnsafeCodeCanonical[] matches = [.. identities.Where(identity => identity.Url == candidate.System).Distinct()];
                if (binding.Complete && identities.Select(identity => identity.Url).Distinct().Count() == 1 && matches.Length == 1)
                {
                    string? version = matches[0].Version;
                    if (version is not null || candidates.All(identity => identity.Version is null))
                    {
                        return candidate with { Version = version };
                    }
                }
            }

            throw new InvalidOperationException(
                $"Unsafe-code identity is ambiguous for primitive {element.Id} in resource outcome {owner.Key}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}, candidate {candidate.System}|{candidate.Version}#{candidate.Code}. Supply an authoritative unique source binding/system version and regenerate outcomes.");
        }

        private BindingScope? Binding(DbElement element)
        {
            if (_bindings.TryGetValue(element.Key, out BindingScope? cached))
            {
                return cached;
            }

            DbElement? bound = BaseChain(element).FirstOrDefault(basis => basis.BindingValueSetKey is not null || basis.BindingValueSet is not null);
            if (bound is null)
            {
                _bindings.Add(element.Key, null);
                return null;
            }

            UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(bound.BindingValueSet);
            DbValueSet[] valueSets = bound.BindingValueSetKey is int key && _valueSets.TryGetValue(key, out DbValueSet? keyed) &&
                keyed.FhirPackageKey == bound.FhirPackageKey && (canonical.Url is null || keyed.UnversionedUrl == canonical.Url) &&
                (canonical.Version is null || keyed.Version == canonical.Version)
                ? [keyed]
                : [.. _valueSets.Values.Where(row => row.FhirPackageKey == bound.FhirPackageKey && row.UnversionedUrl == canonical.Url &&
                    (canonical.Version is null || row.Version == canonical.Version))];
            BindingScope scope = new() { Required = bound.ValueSetBindingStrength == BindingStrength.Required, Complete = valueSets.Length == 1 };
            if (canonical.Url is not null)
            {
                scope.Canonicals.Add(canonical);
            }

            foreach (DbValueSet valueSet in valueSets)
            {
                AddValueSet(valueSet, []);
            }

            _bindings.Add(element.Key, scope);
            return scope;

            void AddValueSet(DbValueSet valueSet, HashSet<int> active)
            {
                if (!active.Add(valueSet.Key))
                {
                    scope.Complete = false;
                    return;
                }

                scope.Canonicals.Add(new(valueSet.UnversionedUrl, valueSet.Version));
                int firstMember = scope.Members.Count;
                if (valueSet.CanExpand)
                {
                    foreach (DbValueSetConcept concept in _concepts[valueSet.Key])
                    {
                        scope.Members.Add((new(concept.System, string.IsNullOrEmpty(concept.SystemVersion) ? null : concept.SystemVersion), concept.Code));
                    }
                }
                else if (valueSet.Compose is { } compose)
                {
                    if (compose.Exclude.Count > 0)
                    {
                        scope.Complete = false;
                    }

                    foreach (ValueSet.ConceptSetComponent include in compose.Include)
                    {
                        if (include.System is not null)
                        {
                            if (include.Concept.Count == 0)
                            {
                                scope.Members.Add((new(include.System, include.Version), null));
                            }
                            else
                            {
                                foreach (ValueSet.ConceptReferenceComponent concept in include.Concept)
                                {
                                    scope.Members.Add((new(include.System, include.Version), concept.Code));
                                }
                            }
                        }

                        foreach (string imported in include.ValueSet)
                        {
                            UnsafeCodeCanonical importedCanonical = UnsafeCodeCanonical.Parse(imported);
                            DbValueSet[] imports = [.. _valueSets.Values.Where(row => row.FhirPackageKey == valueSet.FhirPackageKey &&
                                row.UnversionedUrl == importedCanonical.Url && (importedCanonical.Version is null || row.Version == importedCanonical.Version))];
                            if (imports.Length != 1)
                            {
                                scope.Complete = false;
                            }

                            foreach (DbValueSet import in imports)
                            {
                                AddValueSet(import, active);
                            }
                        }
                    }
                }
                else
                {
                    scope.Complete = false;
                }

                scope.ScopedMembers[new(valueSet.UnversionedUrl, valueSet.Version)] = [.. scope.Members.Skip(firstMember)];
                active.Remove(valueSet.Key);
            }
        }
    }

    private sealed class BindingScope
    {
        internal bool Required { get; init; }
        internal bool Complete { get; set; }
        internal HashSet<UnsafeCodeCanonical> Canonicals { get; } = [];
        internal List<(UnsafeCodeCanonical Identity, string? Code)> Members { get; } = [];
        internal Dictionary<UnsafeCodeCanonical, (UnsafeCodeCanonical Identity, string? Code)[]> ScopedMembers { get; } = [];
        private IEnumerable<(UnsafeCodeCanonical Identity, string? Code)> MembersFor(UnsafeCodeCanonical? scope) =>
            scope?.Url is null ? Members : ScopedMembers.Where(entry => entry.Key.Url == scope.Url &&
                (scope.Version is null || entry.Key.Version == scope.Version)).SelectMany(entry => entry.Value);
        internal UnsafeCodeCanonical[] Identities(string code, UnsafeCodeCanonical? scope = null) =>
            [.. MembersFor(scope).Where(member => member.Code is null || member.Code == code).Select(member => member.Identity).Distinct()];
        internal bool Allows(UnsafeCodeCanonical identity, string? code, UnsafeCodeCanonical scope) =>
            MembersFor(scope).Any(member => member.Identity.Url == identity.Url && (code is null || member.Code is null || member.Code == code) &&
                (identity.Version is null || member.Identity.Version is null || member.Identity.Version == identity.Version));
    }
}
