// <copyright file="UnsafeCodeOutcomeGenerator.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Terminology;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;

namespace Fhir.CodeGen.Comparison.Outcomes;

internal sealed class UnsafeCodeOutcomeGenerator(IDbConnection db, ILogger? logger = null)
{
    internal void ValidateAnnotationProvenance(FhirPackageComparisonPair pair, IReadOnlyList<DbUnsafeCodeTrigger> triggers)
        => ValidateAnnotationProvenance(pair, triggers, null);

    internal void ValidateAnnotationProvenance(FhirPackageComparisonPair pair, IReadOnlyList<DbUnsafeCodeTrigger> triggers,
        XVerPreparedOperation? operation)
    {
        operation?.RequireConnection(db);
        operation?.Audit();
        using PairFacts facts = new(db, pair, operation);
        facts.ValidateAnnotationProvenance(triggers);
    }

    internal void GenerateForPair(FhirPackageComparisonPair pair, DbUnsafeCodePairState? dependency = null)
    {
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(db,
            new(XVerPreparationKind.Outcomes, XVer.XVerProcessor._exclusionSet, processVocabulary: false));
        operation.PrepareOutcomes([pair]);
        dependency ??= UnsafeCodeInputStore.BeginOutcomes(db, pair.SourcePackageKey, pair.TargetPackageKey, operation);
        GenerateForPair(pair, dependency, operation);
    }

    internal void GenerateForPair(FhirPackageComparisonPair pair, DbUnsafeCodePairState dependency, XVerPreparedOperation operation)
    {
        try
        {
            GenerateForPairCore(pair, dependency, operation);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private void GenerateForPairCore(FhirPackageComparisonPair pair, DbUnsafeCodePairState dependency, XVerPreparedOperation operation)
    {
        operation.RequireConnection(db);
        operation.Audit();
        IReadOnlyList<ProfileRepresentationProof> profileDecisions = operation.OutcomeDecisions(pair.SourcePackageKey, pair.TargetPackageKey);
        using PairFacts facts = new(db, pair, operation);
        if (!facts.HasCandidates)
        {
            ProfileUseFacts.PersistNegativeDecisions(db, pair, profileDecisions, operation);
            UnsafeCodeInputStore.CompleteOutcomes(db, dependency, operation);
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

            WalkResource(structure, outcome);
        }

        IEnumerable<UnsafeCodeTrigger> normalized = triggers.GroupBy(trigger => (
            trigger.StructureOutcomeKey, trigger.Context, JsonSerializer.Serialize(trigger.SourcePath),
            trigger.Identity, trigger.VersionMatch))
            .Select(group => group.First() with
            {
                EvidenceKeys = [.. group.SelectMany(trigger => trigger.EvidenceKeys).Distinct().Order()],
                RuleKeys = [.. group.SelectMany(trigger => trigger.RuleKeys).Distinct().Order()],
                AnnotationKeys = [.. group.SelectMany(trigger => trigger.AnnotationKeys).Distinct().Order()],
            });
        ProfileUseFacts.PersistNegativeDecisions(db, pair, profileDecisions, operation);
        UnsafeCodeInputStore.InsertTriggers(db, normalized, operation);
        UnsafeCodeInputStore.CompleteOutcomes(db, dependency, operation);

        void WalkResource(DbStructureDefinition structure, DbStructureOutcome outcome)
        {
            // Classification depends on the occurrence and its contributing mapping contexts, not
            // the path prefix. Discover those states once; only enumerate paths to actual results.
            Dictionary<WalkState, WalkNode> nodes = [];
            Queue<WalkNode> pending = new();
            List<(DbElement Root, WalkNode Node)> roots = [];
            Queue<WalkNode> productive = new();
            Stopwatch progress = Stopwatch.StartNew();
            long paths = 0;
            int startCount = triggers.Count;
            logger?.LogInformation("Planning unsafe-code paths for {Resource}, pair {Source}->{Target}",
                structure.Id, pair.SourcePackageKey, pair.TargetPackageKey);
            foreach (DbElement root in facts.Roots[structure.Key])
            {
                roots.Add((root, Node(root, [], false, true)));
            }
            while (pending.TryDequeue(out WalkNode? node))
            {
                ReportProgress();
                try
                {
                    Expand(node);
                }
                catch (InvalidOperationException error)
                {
                    // Discovery ignores path-local cycle closures. Surface a data error only if
                    // emission can reach this exact state by a legal path, as in the original walk.
                    node.Error = ExceptionDispatchInfo.Capture(error);
                }
                if (node.LocalTriggers.Count != 0 || node.Error is not null)
                {
                    node.Productive = true;
                    productive.Enqueue(node);
                }
            }
            while (productive.TryDequeue(out WalkNode? child))
            {
                foreach (WalkNode parent in child.Parents)
                {
                    if (!parent.Productive)
                    {
                        parent.Productive = true;
                        productive.Enqueue(parent);
                    }
                }
            }
            HashSet<(int From, int To, string Kind)> edges = [];
            List<UnsafeCodePathStep> path = [];
            Stack<WalkNode> frontier = new();
            long search = 0;
            foreach ((DbElement root, WalkNode node) in roots)
            {
                if (node.Productive) Emit(node, new(root.Id, null, false));
            }
            logger?.LogInformation("Finished unsafe-code paths for {Resource}, pair {Source}->{Target}: {States} states, {Paths} visited paths, {Triggers} triggers",
                structure.Id, pair.SourcePackageKey, pair.TargetPackageKey, nodes.Count, paths, triggers.Count - startCount);

            void ReportProgress()
            {
                if (logger is not null && progress.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    logger.LogInformation("Unsafe-code paths for {Resource}, pair {Source}->{Target}: {States} states, {Paths} visited paths, {Triggers} triggers",
                        structure.Id, pair.SourcePackageKey, pair.TargetPackageKey, nodes.Count, paths, triggers.Count - startCount);
                    progress.Restart();
                }
            }

            WalkNode Node(DbElement element, ImmutableArray<MappingUse> inherited, bool carrierCode, bool root = false)
            {
                WalkState state = new(element.Key, inherited, carrierCode, root);
                if (!nodes.TryGetValue(state, out WalkNode? node))
                {
                    node = new(state);
                    nodes.Add(state, node);
                    pending.Enqueue(node);
                }
                return node;
            }

            void Expand(WalkNode node)
            {
                DbElement element = facts.SourceElements[node.State.ElementKey];
                if (!facts.Permits(element)) return;
                ImmutableArray<MappingUse> contexts = facts.Contexts(element, outcome);
                if (!node.State.Inherited.IsEmpty)
                {
                    contexts = [.. contexts.Concat(node.State.Inherited).Distinct()];
                }

                string[] typeNames = facts.TypeNames(element);
                bool carrier = typeNames.Any(IsCarrier);
                ImmutableArray<MappingUse> childContexts = carrier || typeNames.Any(name => name is "CodeableConcept" or "CodeableReference")
                    ? contexts : [];
                bool mixedSafety = typeNames.Length == 2 && typeNames.Contains("Coding", StringComparer.Ordinal) &&
                    typeNames.Any(name => name is "boolean" or "code") && facts.HasNativeTarget(element);
                if (mixedSafety)
                {
                    UnsafeCodeEffectiveTypeFacts sourceFacts = facts.SourceUseFacts(element);
                    foreach (UnsafeCodeTypeFact type in sourceFacts.Types.Where(type => type.SourceTypeName is "Coding" or "code"))
                    {
                        UnsafeCodePathStep endpoint = new(type.Roots.Length == 1 ? type.Roots[0].ElementId : type.SourceTypeName, type.TypeUseToken, false);
                        UnsafeCodeSourceUseNormalization selection = UnsafeCodeSourceUseNormalizer.Normalize(
                            [new(element.Id, null, false), endpoint], new Dictionary<int, UnsafeCodeEffectiveTypeFacts> { [1] = sourceFacts });
                        if (selection.Failure is not null || selection.SourceUse is null)
                        {
                            throw new InvalidOperationException($"Unsafe-code type graph is unresolved for {element.Id} ({type.TypeUseToken}), pair {pair.SourcePackageKey}->{pair.TargetPackageKey}: {selection.Failure}. Reload source definitions and regenerate comparisons and outcomes.");
                        }

                        Classify(node, element, [endpoint], contexts,
                            selection.SourceUse.QualificationKind == UnsafeCodeQualificationKind.Typed);
                    }
                }
                else if (carrier)
                {
                    Classify(node, element, [], contexts, true);
                }
                else if (!node.State.CarrierCode && typeNames.Contains("code", StringComparer.Ordinal))
                {
                    Classify(node, element, [], contexts, false);
                }

                if (!node.State.Root && facts.IsResourceUse(element))
                {
                    return;
                }

                foreach (TraversalEdge edge in facts.TraversalEdges(element))
                {
                    bool reference = edge.Kind == "reference";
                    WalkNode next = Node(edge.Element, reference ? contexts : childContexts,
                        reference ? node.State.CarrierCode : carrier && edge.CodeChild);
                    node.Children.Add(new(next, (element.Key, edge.Element.Key, edge.Kind), new(edge.Element.Id, edge.TypeName, reference)));
                    next.Parents.Add(node);
                }
            }

            void Emit(WalkNode node, UnsafeCodePathStep step)
            {
                paths++;
                ReportProgress();
                node.Error?.Throw();
                path.Add(step);
                foreach (UnsafeCodeTrigger trigger in node.LocalTriggers)
                {
                    triggers.Add(trigger with { SourcePath = [.. path, .. trigger.SourcePath] });
                }
                foreach (WalkEdge edge in node.Children)
                {
                    if (edge.Next.Productive && edges.Add(edge.Key))
                    {
                        if (node.Children.Count == 1 || CanReachResult(edge.Next))
                        {
                            Emit(edge.Next, edge.Step);
                        }
                        edges.Remove(edge.Key);
                    }
                }
                path.RemoveAt(path.Count - 1);
            }

            // A globally productive cycle can become dead once the current path has used its
            // only exit edge. Check reachability without enumerating trails around that cycle.
            bool CanReachResult(WalkNode start)
            {
                search++;
                frontier.Clear();
                frontier.Push(start);
                start.LastSearch = search;
                while (frontier.TryPop(out WalkNode? node))
                {
                    if (node.LocalTriggers.Count != 0 || node.Error is not null) return true;
                    foreach (WalkEdge edge in node.Children)
                    {
                        if (edge.Next.Productive && edge.Next.LastSearch != search && !edges.Contains(edge.Key))
                        {
                            edge.Next.LastSearch = search;
                            frontier.Push(edge.Next);
                        }
                    }
                }
                return false;
            }

            void Classify(WalkNode node, DbElement element, ImmutableArray<UnsafeCodePathStep> suffix, ImmutableArray<MappingUse> contexts, bool typed)
            {
                if (contexts.IsEmpty)
                {
                    contexts = [new(new(pair.SourcePackageKey, pair.TargetPackageKey, element.Id, string.Empty), element, null)];
                }

                foreach (MappingUse use in contexts)
                {
                    UnsafeCodeElementAnnotation[] annotations = facts.AnnotationsFor(use);
                    if (annotations.Length == 0 && facts.Rules.Length == 0) continue;
                    UnsafeCodeMapEvidence[] potentialNoMaps = facts.PotentialNoMapsFor(use);
                    if (annotations.Length > 0 && potentialNoMaps.Any(item => item.SourceCode is not null && item.SourceSystem.Url is null))
                    {
                        throw new InvalidOperationException($"Unsafe-code identity is ambiguous for {element.Id}, pair {pair.SourcePackageKey}->{pair.TargetPackageKey}: the applicable authored no-map code has no qualified source system. Supply authoritative source binding context and regenerate outcomes.");
                    }

                    UnsafeCodeIdentity[] candidates = [.. facts.Rules.Where(rule => !rule.NoMapOnly || potentialNoMaps.Any(item =>
                            item.SourceSystem.Url == rule.Identity.System && item.SourceCode == rule.Identity.Code &&
                            (rule.Identity.Version is null || item.SourceSystem.Version is null || rule.Identity.Version == item.SourceSystem.Version)))
                        .Select(rule => rule.Identity)
                        .Concat(annotations.Length == 0 ? [] : potentialNoMaps.Where(item => item.SourceSystem.Url is not null && item.SourceCode is not null)
                            .Select(item => new UnsafeCodeIdentity(item.SourceSystem.Url!, item.SourceSystem.Version, item.SourceCode!)))
                        .Distinct()];
                    if (candidates.Length == 0) continue;
                    UnsafeCodeMapEvidence[] evidence = facts.EvidenceFor(use);
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

                        if (potentialNoMaps.Except(evidence).Any(item => item.SourceSystem.Url == identity.System && item.SourceCode == identity.Code &&
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
                            node.LocalTriggers.Add(new(
                                outcome.Key, use.Context, suffix, decision.Identity, decision.VersionMatch,
                                [.. facts.EvidenceRows.Where(row => decision.EvidenceProvenance.Contains(Provenance(row))).Select(row => row.Key).Order()],
                                [.. facts.RuleRows.Where(row => decision.RuleProvenance.Contains(new(row.SourceRelativeFilename, row.EntryIndex))).Select(row => row.Key).Order()])
                            {
                                AnnotationKeys = [.. facts.SelectedAnnotationKeys(decision, use)],
                            });
                        }
                    }
                }
            }
        }
    }

    private static bool IsCarrier(string? typeName) => typeName is
        "Coding" or "Quantity" or "Age" or "Count" or "Distance" or "Duration" or "MoneyQuantity" or "SimpleQuantity";

    private static string RelativeName(DbElement child, DbElement parent) =>
        child.Id.StartsWith(parent.Id + ".", StringComparison.Ordinal) ? child.Id[(parent.Id.Length + 1)..] : child.Id;

    private static bool VersionsOverlap(string? left, string? right) => left is null || right is null || left == right;

    private static UnsafeCodeProvenance Provenance(DbUnsafeCodeMapEvidence row) =>
        new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex);

    private sealed record MappingUse(UnsafeCodeMappingContext Context, DbElement Source, DbElement? Target,
        int? ElementMappingKey = null, int? ElementComparisonKey = null);

    private readonly record struct WalkState(int ElementKey, ImmutableArray<MappingUse> Inherited, bool CarrierCode, bool Root)
    {
        public bool Equals(WalkState other) => ElementKey == other.ElementKey && CarrierCode == other.CarrierCode &&
            Root == other.Root && Inherited.AsSpan().SequenceEqual(other.Inherited.AsSpan());

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(ElementKey);
            hash.Add(CarrierCode);
            hash.Add(Root);
            foreach (MappingUse use in Inherited) hash.Add(use);
            return hash.ToHashCode();
        }
    }

    private sealed class WalkNode(WalkState state)
    {
        internal WalkState State { get; } = state;
        internal List<WalkNode> Parents { get; } = [];
        internal List<WalkEdge> Children { get; } = [];
        internal List<UnsafeCodeTrigger> LocalTriggers { get; } = [];
        internal ExceptionDispatchInfo? Error { get; set; }
        internal bool Productive { get; set; }
        internal long LastSearch { get; set; }
    }

    private sealed record WalkEdge(WalkNode Next, (int From, int To, string Kind) Key, UnsafeCodePathStep Step);
    private sealed record TraversalEdge(DbElement Element, string Kind, string? TypeName, bool CodeChild);

    private sealed class PairFacts : IDisposable
    {
        private readonly FhirPackageComparisonPair _pair;
        private readonly Dictionary<int, DbElement> _targetElements;
        private readonly ILookup<int, DbElement> _children;
        private readonly ILookup<(int StructureKey, string ParentId), DbElement> _looseChildren;
        private readonly ILookup<int, DbElementType> _types;
        private readonly Dictionary<int, DbElementComparison> _comparisons;
        private readonly Dictionary<int, DbElementComparison> _allComparisons;
        private readonly Dictionary<int, DbStructureComparison> _structureComparisons;
        private readonly Dictionary<int, DbElement> _allElements;
        private readonly XVerStructureCatalog _catalog;
        private readonly bool _ownsCatalog;
        private readonly ProfileUseFacts _profileFacts;
        private readonly Dictionary<int, DbElementMapping> _mappings;
        private readonly Dictionary<int, DbElementOutcome> _elementOutcomes;
        private readonly ILookup<int, DbElementOutcomeTarget> _targets;
        private readonly UnsafeCodeBindingScopeResolver _bindingResolver;
        private readonly Dictionary<int, UnsafeCodeBindingScope?> _bindings = [];
        private readonly Dictionary<int, DbElement[]> _effectiveChildren = [];
        private readonly Dictionary<int, TraversalEdge[]> _traversalEdges = [];
        private readonly Dictionary<int, bool> _permitted = [];
        private readonly Dictionary<int, DbElementType[]> _effectiveTypes = [];
        private readonly Dictionary<MappingUse, UnsafeCodeMapEvidence[]> _scopedEvidence = [];
        private readonly Dictionary<MappingUse, UnsafeCodeMapEvidence[]> _potentialNoMaps = [];
        private int? _contextOwner;
        private readonly Dictionary<int, ImmutableArray<MappingUse>> _contexts = [];
        private readonly Dictionary<int, string[]> _typeNames = [];
        private readonly Dictionary<MappingUse, HashSet<int>> _selectedAnnotationKeys = [];

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
        private readonly DbUnsafeCodeElementAnnotation[] _annotationRows;
        private readonly ILookup<int?, int> _annotationKeysByMapping;
        private readonly DbProfileMapOwnerFacts[] _annotationOwners;
        private readonly ILookup<(int?, string), DbProfileMapOwnerFacts> _unnormalizedOwners;
        private readonly UnsafeCodeMapEvidence[] _noMaps;
        private UnsafeCodeMapEvidence[] Evidence { get; }

        internal PairFacts(IDbConnection db, FhirPackageComparisonPair pair)
            : this(db, pair, null)
        {
        }

        internal PairFacts(IDbConnection db, FhirPackageComparisonPair pair, XVerPreparedOperation? operation)
        {
            _pair = pair;
            _ownsCatalog = operation is null;
            _catalog = operation?.Catalog ?? new(db, XVer.XVerProcessor._exclusionSet);
            _profileFacts = operation?.GetComparisonPacket(pair.SourcePackageKey, pair.TargetPackageKey).Facts
                ?? new(_catalog, pair.SourcePackageKey, pair.TargetPackageKey);
            try
            {
                _allElements = (operation?.Evidence.Rows<DbElement>(DbElement.DefaultTableName) ?? DbElement.SelectList(db)).ToDictionary(row => row.Key);
                Structures = (operation?.Evidence.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName) ?? DbStructureDefinition.SelectList(db))
                    .Where(row => row.FhirPackageKey == pair.SourcePackageKey).ToDictionary(row => row.Key);
                SourceElements = _allElements.Values.Where(row => row.FhirPackageKey == pair.SourcePackageKey).ToDictionary(row => row.Key);
                _targetElements = _allElements.Values.Where(row => row.FhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
                _children = SourceElements.Values.Where(element => element.ParentElementKey is not null).ToLookup(element => element.ParentElementKey!.Value);
                _looseChildren = SourceElements.Values.Where(element => element.ParentElementKey is null && element.Id.Contains('.', StringComparison.Ordinal))
                    .ToLookup(element => (element.StructureKey, element.Id[..element.Id.LastIndexOf('.')]));
                Roots = SourceElements.Values.Where(element => element.ParentElementKey is null && !element.Path.Contains('.', StringComparison.Ordinal)).ToLookup(element => element.StructureKey);
                _types = (operation?.Evidence.Rows<DbElementType>(DbElementType.DefaultTableName) ?? DbElementType.SelectList(db))
                    .Where(row => row.FhirPackageKey == pair.SourcePackageKey).ToLookup(type => type.ElementKey);
                Outcomes = (operation?.Evidence.Rows<DbStructureOutcome>(DbStructureOutcome.DefaultTableName) ?? DbStructureOutcome.SelectList(db))
                    .Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey && row.TargetFhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
                foreach (DbStructureOutcome outcome in Outcomes.Values)
                {
                    if (!_catalog.IsProcessed(outcome.SourceStructureKey) ||
                        outcome.TargetStructureKey is int targetKey && !_catalog.IsProcessed(targetKey))
                    {
                        throw new InvalidOperationException($"Passive standalone outcome {outcome.Key} is not operational for pair {pair.SourcePackageKey}->{pair.TargetPackageKey}.");
                    }
                }
                _allComparisons = (operation?.Evidence.Rows<DbElementComparison>(DbElementComparison.DefaultTableName) ?? DbElementComparison.SelectList(db)).ToDictionary(row => row.Key);
                _structureComparisons = (operation?.Evidence.Rows<DbStructureComparison>(DbStructureComparison.DefaultTableName) ?? DbStructureComparison.SelectList(db)).ToDictionary(row => row.Key);
                _comparisons = _allComparisons.Values.Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey &&
                    row.TargetFhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
                _mappings = (operation?.Evidence.Rows<DbElementMapping>(DbElementMapping.DefaultTableName) ?? DbElementMapping.SelectList(db)).ToDictionary(row => row.Key);
                _elementOutcomes = (operation?.Evidence.Rows<DbElementOutcome>(DbElementOutcome.DefaultTableName) ?? DbElementOutcome.SelectList(db))
                    .Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey && row.TargetFhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
                _targets = (operation?.Evidence.Rows<DbElementOutcomeTarget>(DbElementOutcomeTarget.DefaultTableName) ?? DbElementOutcomeTarget.SelectList(db))
                    .Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey && row.TargetFhirPackageKey == pair.TargetPackageKey)
                    .Where(target => _elementOutcomes.ContainsKey(target.ElementOutcomeKey))
                    .ToLookup(target => _elementOutcomes[target.ElementOutcomeKey].SourceElementKey);
                Dictionary<int, DbValueSet> valueSets = (operation?.Evidence.Rows<DbValueSet>(DbValueSet.DefaultTableName) ?? DbValueSet.SelectList(db))
                    .Where(row => row.FhirPackageKey == pair.SourcePackageKey || row.FhirPackageKey == pair.TargetPackageKey).ToDictionary(row => row.Key);
                ILookup<int, DbValueSetConcept> concepts = (operation?.Evidence.Rows<DbValueSetConcept>(DbValueSetConcept.DefaultTableName) ?? DbValueSetConcept.SelectList(db))
                    .Where(row => valueSets.ContainsKey(row.ValueSetKey)).ToLookup(row => row.ValueSetKey);
                _bindingResolver = new(valueSets, key => concepts[key]);
                DbUnsafeCodeElementAnnotation[] annotationRows = [.. (operation?.Evidence.Rows<DbUnsafeCodeElementAnnotation>(DbUnsafeCodeElementAnnotation.DefaultTableName) ?? DbUnsafeCodeElementAnnotation.SelectList(db))
                    .Where(row => row.RequiresOmittedMandatoryCode)];
                _annotationRows = annotationRows;
                _annotationOwners = [.. operation?.Evidence.Rows<DbProfileMapOwnerFacts>(DbProfileMapOwnerFacts.DefaultTableName) ?? DbProfileMapOwnerFacts.SelectList(db)];
                _unnormalizedOwners = _annotationOwners.Where(row => row.ResolutionState == ProfileMapOwnerResolution.Unnormalized)
                    .ToLookup(row => (row.SourceStructureKey, row.SourceElementId));
                _annotationKeysByMapping = _annotationOwners.Where(owner => owner.ResolutionState == ProfileMapOwnerResolution.Resolved &&
                    owner.ElementMappingKey is not null).ToLookup(owner => owner.ElementMappingKey, owner => owner.AnnotationKey);
                Annotations = [.. annotationRows
                    .Select(row => new UnsafeCodeElementAnnotation(
                        new(row.SourceFhirPackageKey, row.TargetFhirPackageKey, row.SourceElementId, row.TargetElementId),
                        new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex)))];
                EvidenceRows = [.. (operation?.Evidence.Rows<DbUnsafeCodeMapEvidence>(DbUnsafeCodeMapEvidence.DefaultTableName) ?? DbUnsafeCodeMapEvidence.SelectList(db))
                    .Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey && row.TargetFhirPackageKey == pair.TargetPackageKey)];
                Evidence = [.. EvidenceRows.Select(row => new UnsafeCodeMapEvidence(
                    row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                    new(row.SourceValueSetUrl, row.SourceValueSetVersion), new(row.TargetValueSetUrl, row.TargetValueSetVersion),
                    new(row.SourceSystem, row.SourceSystemVersion), row.SourceCode,
                    new(row.TargetSystem, row.TargetSystemVersion), row.TargetCode, row.Relationship, row.ExplicitNoMap, Provenance(row)))];
                RuleRows = [.. operation?.Evidence.Rows<DbUnsafeCodeRule>(DbUnsafeCodeRule.DefaultTableName) ?? DbUnsafeCodeRule.SelectList(db)];
                _noMaps = [.. Evidence.Where(item => item.ExplicitNoMap)];
                Rules = [.. RuleRows.Select(UnsafeCodeInputStore.ReadRule)];
            }
            finally
            {
                if (_ownsCatalog)
                {
                    _catalog.ReleasePayloads();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_ownsCatalog)
                {
                    _profileFacts.Dispose();
                }
            }
            finally
            {
                if (_ownsCatalog)
                {
                    _catalog.Dispose();
                }
            }
        }

        internal bool Permits(DbElement element)
        {
            if (!_permitted.TryGetValue(element.Key, out bool permitted))
                _permitted[element.Key] = permitted = _profileFacts.Permits(element);
            return permitted;
        }

        internal TraversalEdge[] TraversalEdges(DbElement element)
        {
            if (_traversalEdges.TryGetValue(element.Key, out TraversalEdge[]? cached)) return cached;
            List<TraversalEdge> edges = [];
            HashSet<string> localNames = new(StringComparer.Ordinal);
            foreach (DbElement child in Children(element))
            {
                string name = RelativeName(child, element);
                localNames.Add(name);
                edges.Add(new(child, "child", null, name == "code"));
            }
            foreach ((string? typeName, int key) in TypeStructures(element))
            {
                if (IsResource(key)) continue;
                foreach (DbElement root in Roots[key])
                {
                    foreach (DbElement child in Children(root))
                    {
                        string name = RelativeName(child, root);
                        if (!localNames.Contains(name) && _profileFacts.PermitsChild(element, child, key))
                            edges.Add(new(child, "type", typeName, name == "code"));
                    }
                }
            }
            if (element.ContentReferenceSourceKey is int referenceKey && SourceElements.TryGetValue(referenceKey, out DbElement? reference))
                edges.Add(new(reference, "reference", null, false));
            return _traversalEdges[element.Key] = [.. edges];
        }

        internal UnsafeCodeElementAnnotation[] AnnotationsFor(MappingUse use)
        {
            if (_selectedAnnotationKeys.TryGetValue(use, out HashSet<int>? cached))
            {
                return [.. _annotationRows.Where(row => cached.Contains(row.Key)).Select(row =>
                    new UnsafeCodeElementAnnotation(use.Context, AnnotationProvenance(row)))];
            }

            List<UnsafeCodeElementAnnotation> selected = [];
            HashSet<int> selectedKeys = [];
            foreach (MappingUse authority in ContributingUses(use))
            {
                List<DbUnsafeCodeElementAnnotation> unnormalized = [];
                IEnumerable<int> linkedAnnotations = _annotationKeysByMapping[authority.ElementMappingKey];
                foreach (DbUnsafeCodeElementAnnotation row in _annotationRows.Where(row =>
                    row.SourceFhirPackageKey == authority.Source.FhirPackageKey &&
                    row.TargetFhirPackageKey == authority.Target?.FhirPackageKey &&
                    ((row.SourceElementId == authority.Source.Id && row.TargetElementId == authority.Target?.Id) ||
                        linkedAnnotations.Contains(row.Key))))
                {
                    DbProfileMapOwnerFacts[] owners = [.. _annotationOwners.Where(owner => owner.AnnotationKey == row.Key &&
                        (owner.SourceStructureKey is null || owner.SourceStructureKey == authority.Source.StructureKey) &&
                        (owner.TargetStructureKey is null || owner.TargetStructureKey == authority.Target?.StructureKey))];
                    if (!_annotationOwners.Any(owner => owner.AnnotationKey == row.Key) ||
                        owners.Any(owner => owner.ResolutionState is ProfileMapOwnerResolution.Ambiguous or ProfileMapOwnerResolution.Unresolved))
                    {
                        throw new InvalidOperationException($"Annotation {row.Key} has unresolved owner/authority at {use.Source.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Reload maps, compare, and regenerate outcomes.");
                    }

                    if (owners.Any(owner => owner.ResolutionState == ProfileMapOwnerResolution.Resolved &&
                        owner.ElementMappingKey is not null && owner.ElementMappingKey == authority.ElementMappingKey))
                    {
                        Add(row);
                    }
                    else if (authority.ElementMappingKey is null && owners.Any(owner =>
                        owner.ResolutionState == ProfileMapOwnerResolution.Unnormalized &&
                        owner.SourceStructureKey == authority.Source.StructureKey &&
                        owner.TargetStructureKey == authority.Target?.StructureKey))
                    {
                        unnormalized.Add(row);
                    }
                }

                if (unnormalized.Select(AnnotationProvenance).Distinct().Count() > 1)
                {
                    throw new InvalidOperationException($"Annotation authority is ambiguous at {authority.Source.Id}->{authority.Target?.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Reload maps with a unique authored authority.");
                }
                foreach (DbUnsafeCodeElementAnnotation row in unnormalized)
                {
                    Add(row);
                }
            }
            _selectedAnnotationKeys.Add(use, selectedKeys);
            return [.. selected];

            void Add(DbUnsafeCodeElementAnnotation row)
            {
                if (selectedKeys.Add(row.Key))
                {
                    selected.Add(new(use.Context, AnnotationProvenance(row)));
                }
            }
        }

        internal void ValidateAnnotationProvenance(IReadOnlyList<DbUnsafeCodeTrigger> triggers)
        {
            foreach (DbUnsafeCodeTrigger trigger in triggers)
            {
                int[] keys = JsonSerializer.Deserialize<int[]>(trigger.AnnotationKeysJson)!;
                if (keys.Length == 0)
                {
                    continue;
                }
                if (!Outcomes.TryGetValue(trigger.StructureOutcomeKey, out DbStructureOutcome? outcome))
                {
                    throw Incomplete();
                }

                ImmutableArray<UnsafeCodePathStep> path = JsonSerializer.Deserialize<ImmutableArray<UnsafeCodePathStep>>(trigger.SourcePathJson);
                Dictionary<int, UnsafeCodeEffectiveTypeFacts> precedingFacts = [];
                DbElement? source = null;
                ImmutableArray<MappingUse> contexts = [];
                ImmutableArray<MappingUse> childContexts = [];
                for (int index = 0; index < path.Length; index++)
                {
                    UnsafeCodePathStep step = path[index];
                    if (source is not null)
                    {
                        precedingFacts[index] = SourceUseFacts(source);
                        if (UnsafeCodeSourceUseNormalizer.IsEndpointCandidate(step, precedingFacts[index]))
                        {
                            if (UnsafeCodeSourceUseNormalizer.Normalize(path, precedingFacts).Failure is not null)
                            {
                                throw Incomplete();
                            }
                            break;
                        }
                        if (index > 1 && IsResourceUse(source))
                        {
                            throw Incomplete();
                        }
                    }

                    IEnumerable<DbElement> candidates = source is null ? Roots[outcome.SourceStructureKey] :
                        step.IsContentReference
                            ? source.ContentReferenceSourceKey is int referenceKey && SourceElements.TryGetValue(referenceKey, out DbElement? reference)
                                ? [reference] : []
                            : step.TypeName is not null
                                ? TypeStructures(source).Where(type => type.TypeName == step.TypeName && !IsResource(type.StructureKey))
                                    .SelectMany(type => Roots[type.StructureKey]).SelectMany(Children)
                                    .Where(child => !Children(source).Any(local =>
                                        RelativeName(local, source) == RelativeName(child, Roots[child.StructureKey].Single())))
                                : Children(source);
                    DbElement[] matches = [.. candidates.Where(element => element.Id == step.ElementId).DistinctBy(element => element.Key)];
                    if (matches.Length != 1)
                    {
                        throw Incomplete();
                    }
                    source = matches[0];
                    contexts = [.. Contexts(source, outcome).Concat(step.IsContentReference ? contexts : childContexts).Distinct()];
                    string[] names = TypeNames(source);
                    childContexts = names.Any(IsCarrier) || names.Any(name => name is "CodeableConcept" or "CodeableReference") ? contexts : [];
                }

                HashSet<int> selected = [];
                foreach (MappingUse use in contexts.Where(use => use.Context.SourceElementId == trigger.SourceElementId &&
                    use.Context.TargetElementId == trigger.TargetElementId))
                {
                    AnnotationsFor(use);
                    selected.UnionWith(_selectedAnnotationKeys[use]);
                }
                if (path.IsDefaultOrEmpty || keys.Any(key => !selected.Contains(key)))
                {
                    throw Incomplete();
                }

                InvalidOperationException Incomplete() => new(
                    $"Unsafe-code trigger provenance is incomplete or does not belong to its contributing owner/edge at {trigger.SourceElementId}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Regenerate load, compare, outcomes, and export.");
            }
        }

        private static UnsafeCodeProvenance AnnotationProvenance(DbUnsafeCodeElementAnnotation row) =>
            new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex);

        internal IEnumerable<int> SelectedAnnotationKeys(UnsafeCodeDecision decision, MappingUse use) =>
            _annotationRows.Where(row => _selectedAnnotationKeys[use].Contains(row.Key) &&
                decision.AnnotationProvenance.Contains(AnnotationProvenance(row)))
                .Select(row => row.Key).Distinct().Order();

        private IEnumerable<MappingUse> ContributingUses(MappingUse use)
        {
            if (use.ElementComparisonKey is not int comparisonKey || !_allComparisons.TryGetValue(comparisonKey, out DbElementComparison? comparison) ||
                comparison.Steps <= 1 || comparison.ElementMappingKey is not null)
            {
                yield return use;
                yield break;
            }

            if (!_structureComparisons.TryGetValue(comparison.StructureComparisonKey, out DbStructureComparison? structure))
            {
                throw new InvalidOperationException($"Missing transitive comparison authority at {use.Source.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}.");
            }

            int sourceIndex = _pair.SourcePackage.PackageArrayIndex;
            int targetIndex = _pair.TargetPackage.PackageArrayIndex;
            int increment = sourceIndex < targetIndex ? 1 : -1;
            for (int index = sourceIndex; index != targetIndex; index += increment)
            {
                int nextIndex = index + increment;
                if (comparison.ContentKeys[index] is not int sourceKey || comparison.ContentKeys[nextIndex] is not int targetKey ||
                    !_allElements.TryGetValue(sourceKey, out DbElement? source) || !_allElements.TryGetValue(targetKey, out DbElement? target) ||
                    structure.ContentKeys[index] != source.StructureKey || structure.ContentKeys[nextIndex] != target.StructureKey ||
                    !_catalog.IsProcessed(source.StructureKey) || !_catalog.IsProcessed(target.StructureKey))
                {
                    throw new InvalidOperationException($"Invalid transitive owner/element path at {use.Source.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}. Rerun comparisons and outcomes.");
                }

                DbElementComparison[] steps = [.. _allComparisons.Values.Where(step =>
                    step.SourceFhirPackageKey == source.FhirPackageKey && step.TargetFhirPackageKey == target.FhirPackageKey &&
                    step.SourceStructureKey == source.StructureKey && step.TargetStructureKey == target.StructureKey &&
                    step.SourceElementKey == source.Key && step.TargetElementKey == target.Key && !step.NotMapped &&
                    _structureComparisons.TryGetValue(step.StructureComparisonKey, out DbStructureComparison? owner) &&
                    owner.SourceStructureKey == source.StructureKey && owner.TargetStructureKey == target.StructureKey)];
                if (steps.Length == 0 || steps.Select(step => step.ElementMappingKey).Distinct().Count() != 1)
                {
                    throw new InvalidOperationException($"Transitive mapping authority is missing or ambiguous at {source.Id}->{target.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}.");
                }

                DbElementComparison step = steps[0];
                if (step.ElementMappingKey is int mappingKey &&
                    (!_mappings.TryGetValue(mappingKey, out DbElementMapping? mapping) ||
                        mapping.SourceFhirPackageKey != source.FhirPackageKey || mapping.TargetFhirPackageKey != target.FhirPackageKey ||
                        mapping.SourceStructureKey != source.StructureKey || mapping.TargetStructureKey != target.StructureKey ||
                        mapping.SourceElementId != source.Id || mapping.TargetElementId != target.Id ||
                        mapping.SourceElementKey is int mappedSource && mappedSource != source.Key ||
                        mapping.TargetElementKey is int mappedTarget && mappedTarget != target.Key))
                {
                    throw new InvalidOperationException($"Transitive mapping provenance is inconsistent at {source.Id}->{target.Id}, pair {_pair.SourcePackageKey}->{_pair.TargetPackageKey}.");
                }
                yield return new(use.Context, source, target, step.ElementMappingKey, step.Key);
            }
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

        internal UnsafeCodeEffectiveTypeFacts SourceUseFacts(DbElement element)
        {
            DbElement aggregate = BaseChain(element).FirstOrDefault(basis => basis.DistinctTypes.Any(name => name.Length > 0)) ?? element;
            DbElementType[] rows = Types(element);
            ImmutableArray<UnsafeCodeTypeFact> types = rows.Length == 0
                ? [.. aggregate.DistinctTypes.Where(name => name.Length > 0).Select(name =>
                    new UnsafeCodeTypeFact(null, name, name, name, ResolveRoots(name, null, null)))]
                : [.. rows.Select(row => new UnsafeCodeTypeFact(row.Key, row.TypeName ?? string.Empty,
                    row.TypeProfile ?? row.TypeName ?? row.Literal, row.Literal,
                    ResolveRoots(row.TypeName, row.TypeProfile, row.TypeProfileStructureKey ??
                        (row.TypeProfile is null ? row.TypeStructureKey : null))))];
            return new(types, [.. aggregate.DistinctTypes.Where(name => name.Length > 0)], aggregate.DistinctTypeCount);

            ImmutableArray<UnsafeCodeRootFact> ResolveRoots(string? name, string? profile, int? key)
            {
                UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(profile);
                DbStructureDefinition[] matches = [.. Structures.Values.Where(structure =>
                    structure.FhirPackageKey == element.FhirPackageKey && (profile is not null
                        ? structure.UnversionedUrl == canonical.Url && (canonical.Version is null || structure.Version == canonical.Version)
                        : structure.Name == name && structure.ArtifactClass != FhirArtifactClassEnum.Profile))];
                if (matches.Length != 1 || key is not null && matches[0].Key != key ||
                    matches[0].ArtifactClass is not (FhirArtifactClassEnum.PrimitiveType or FhirArtifactClassEnum.ComplexType or FhirArtifactClassEnum.Profile))
                {
                    return [];
                }

                DbElement? exactRoot = profile is null ? _catalog.ResolveRoot(matches[0]) : _profileFacts.ForDefinition(matches[0])?.Root;
                if (exactRoot is null) return [];
                return [new UnsafeCodeRootFact(matches[0].Key, exactRoot.Id, IsResource(matches[0].Key))];
            }
        }

        internal bool HasNativeTarget(DbElement element) => BaseChain(element).Any(basis =>
            _targets[basis.Key].Any(target => target.ElementComparisonKey is int key &&
                _comparisons.TryGetValue(key, out DbElementComparison? comparison) && !comparison.NotMapped &&
                comparison.TargetElementKey is not null));

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
                if (type.TypeProfile is not null)
                {
                    DbStructureDefinition definition = _catalog.ResolveTypeUse(type);
                    EffectiveProfileUse use = _profileFacts.AtUse(element, type);
                    if (!use.Diagnostics.IsEmpty || use.Branches.Any(branch => !branch.Diagnostics.IsEmpty))
                    {
                        ProfileUseFacts.Warn($"Unsafe-code effective profile facts are unresolved for {element.Id}: " +
                            string.Join("; ", use.Diagnostics.Concat(use.Branches.SelectMany(branch => branch.Diagnostics))));
                        continue;
                    }
                    RequireTypeRoot(definition.Key, type.TypeName);
                    yield return (type.TypeProfile, definition.Key);
                    continue;
                }
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
            if (_contextOwner != owner.Key)
            {
                _contextOwner = owner.Key;
                _contexts.Clear();
            }
            if (_contexts.TryGetValue(element.Key, out ImmutableArray<MappingUse> cached))
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
                        uses.Add(new(new(_pair.SourcePackageKey, _pair.TargetPackageKey, basis.Id, targetElement.Id), basis, targetElement,
                            comparison.ElementMappingKey, comparison.Key));
                    }
                }

                // Authored annotations survive maps that could not be normalized. Require the exact
                // directed target in a contributing structure, never a similarly named leaf.
                if (_unnormalizedOwners.Contains((basis.StructureKey, basis.Id)) &&
                    !_targets[basis.Key].Any(target => Outcomes.TryGetValue(target.StructureOutcomeKey, out DbStructureOutcome? contributing) &&
                        contributing.SourceStructureKey == basis.StructureKey && (basis.StructureKey != owner.SourceStructureKey || contributing.Key == owner.Key)))
                {
                    HashSet<int> targetStructures = [.. Outcomes.Values.Where(row => row.SourceStructureKey == basis.StructureKey &&
                        (basis.StructureKey != owner.SourceStructureKey || row.Key == owner.Key))
                        .Select(row => row.TargetStructureKey).OfType<int>()];
                    foreach (DbProfileMapOwnerFacts annotation in _unnormalizedOwners[(basis.StructureKey, basis.Id)])
                    {
                        foreach (DbElement target in _targetElements.Values.Where(target => target.Id == annotation.TargetElementId &&
                            target.StructureKey == annotation.TargetStructureKey && targetStructures.Contains(target.StructureKey)))
                        {
                            uses.Add(new(new(_pair.SourcePackageKey, _pair.TargetPackageKey, basis.Id, target.Id), basis, target));
                        }
                    }
                }

                if (uses.Count > 0)
                {
                    break;
                }
            }

            ImmutableArray<MappingUse> result = [.. uses.Distinct()];
            _contexts.Add(element.Key, result);
            return result;
        }

        internal UnsafeCodeMapEvidence[] EvidenceFor(MappingUse use)
        {
            if (_scopedEvidence.TryGetValue(use, out UnsafeCodeMapEvidence[]? cached))
            {
                return cached;
            }

            UnsafeCodeBindingScope? source = Binding(use.Source);
            UnsafeCodeBindingScope? target = use.Target is null ? null : Binding(use.Target);
            UnsafeCodeMapEvidence[] evidence = [.. Evidence.Select(item => UnsafeCodeEvidenceApplicability.QualifyEvidence(item, source))
                .Where(item => UnsafeCodeEvidenceApplicability.IsPrecise(item, source, target))];
            _scopedEvidence.Add(use, evidence);
            return evidence;
        }

        internal UnsafeCodeMapEvidence[] PotentialNoMapsFor(MappingUse use)
        {
            if (_potentialNoMaps.TryGetValue(use, out UnsafeCodeMapEvidence[]? cached))
            {
                return cached;
            }

            UnsafeCodeBindingScope? source = Binding(use.Source);
            UnsafeCodeBindingScope? target = use.Target is null ? null : Binding(use.Target);
            UnsafeCodeMapEvidence[] evidence = [.. _noMaps.Select(item => UnsafeCodeEvidenceApplicability.QualifyEvidence(item, source))
                .Where(item => UnsafeCodeEvidenceApplicability.IsPotential(item, source, target))];
            _potentialNoMaps.Add(use, evidence);
            return evidence;
        }

        internal UnsafeCodeIdentity? PrimitiveIdentity(
            DbElement element,
            DbElement bindingElement,
            UnsafeCodeIdentity candidate,
            IEnumerable<UnsafeCodeIdentity> candidates,
            DbStructureOutcome owner)
        {
            UnsafeCodeBindingScope? binding = Binding(element) ?? Binding(bindingElement);
            if (binding is not null && binding.Required)
            {
                ImmutableArray<UnsafeCodeCanonical> identities = UnsafeCodeEvidenceApplicability.Identities(binding, candidate.Code);
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

        private UnsafeCodeBindingScope? Binding(DbElement element)
        {
            if (_bindings.TryGetValue(element.Key, out UnsafeCodeBindingScope? cached))
            {
                return cached;
            }

            DbElement? bound = BaseChain(element).FirstOrDefault(basis => basis.BindingValueSetKey is not null || basis.BindingValueSet is not null);
            UnsafeCodeBindingScope? scope = _bindingResolver.Resolve(bound);
            _bindings.Add(element.Key, scope);
            return scope;
        }
    }
}
