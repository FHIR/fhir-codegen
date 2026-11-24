// <copyright file="ProfileRepresentationIndex.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Terminology;
using Hl7.Fhir.Model;
using Microsoft.Data.Sqlite;

namespace Fhir.CodeGen.Comparison.Exporter;

internal abstract record RepresentationStep;
internal sealed record MemberStep(string Name) : RepresentationStep;
internal sealed record ChoiceStep(string TypeName) : RepresentationStep;
internal sealed record ExtensionStep(string Url, bool IsModifier = false) : RepresentationStep;
internal sealed record ResourceOwnerStep(string TypeName) : RepresentationStep;
internal sealed record RepeatStep(ImmutableArray<RepresentationStep> Steps, bool IncludeInitial = true) : RepresentationStep;
internal sealed record DatatypeStep(string Name, string MetadataUrl) : RepresentationStep;

internal enum RepresentationState
{
    Observable,
    Missing,
    OpaqueSubstitution,
    ErasedVersion,
    ErasedIdentity,
}

internal enum CodeCarrierKind
{
    None,
    Primitive,
    Coding,
    Quantity,
    CodeableConcept,
    CodeableReference,
}

internal sealed record CodeCarrierLocation(
    CodeCarrierKind Kind,
    ImmutableArray<RepresentationStep> System,
    ImmutableArray<RepresentationStep> Version,
    ImmutableArray<RepresentationStep> Code,
    bool IsDecomposed);

internal sealed record CodeTransformation(
    string? SourceSystem, string? SourceVersion, string? SourceCode,
    string? TargetSystem, string? TargetVersion, string? TargetCode)
{
    internal ImmutableArray<UnsafeCodeProvenance> Provenance { get; init; } = [];

    internal static ImmutableArray<CodeTransformation> FromEvidence(IEnumerable<UnsafeCodeMapEvidence> evidence) =>
        [.. evidence.GroupBy(row => (SourceSystem: row.SourceSystem.Url, SourceVersion: row.SourceSystem.Version, row.SourceCode,
                TargetSystem: row.TargetSystem.Url, TargetVersion: row.TargetSystem.Version, row.TargetCode))
            .OrderBy(group => group.Key.SourceSystem, StringComparer.Ordinal)
            .ThenBy(group => group.Key.SourceVersion, StringComparer.Ordinal)
            .ThenBy(group => group.Key.SourceCode, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetSystem, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetVersion, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetCode, StringComparer.Ordinal)
            .Select(group => new CodeTransformation(group.Key.SourceSystem, group.Key.SourceVersion, group.Key.SourceCode,
                group.Key.TargetSystem, group.Key.TargetVersion, group.Key.TargetCode)
            {
                Provenance = [.. group.Select(row => row.Provenance).Distinct()
                    .OrderBy(provenance => provenance.SourceRelativeFilename, StringComparer.Ordinal)
                    .ThenBy(provenance => provenance.ConceptMapId, StringComparer.Ordinal)
                    .ThenBy(provenance => provenance.ConceptMapUrl, StringComparer.Ordinal)
                    .ThenBy(provenance => provenance.GroupIndex)
                    .ThenBy(provenance => provenance.SourceIndex)
                    .ThenBy(provenance => provenance.TargetIndex)],
            })];
}

internal sealed record RepresentationLocation(
    ImmutableArray<RepresentationStep> Steps,
    string? SourceType,
    string? TargetType,
    RepresentationState State,
    ImmutableArray<CodeTransformation> Transformations)
{
    internal string? SourceElementId { get; init; }
    internal UnsafeCodeCanonical? SourceBinding { get; init; }
    internal CodeCarrierLocation? Carrier { get; init; }
}

// Paths here are ElementDefinition paths, never executable FHIRPath. The same
// attachment supplies the differential's path/profile and the index's selector.
internal sealed record ProfileExtensionAttachment(string Path, string Url, string? NarrowedType = null, DbElement? ChoiceElement = null)
{
    internal ImmutableArray<RepresentationStep> Steps
    {
        get
        {
            string[] members = Path.Split('.');
            ImmutableArray<RepresentationStep>.Builder steps = ImmutableArray.CreateBuilder<RepresentationStep>();
            if (ChoiceElement is not null)
            {
                steps.AddRange(ProfileRepresentationIndex.NativeSteps(ChoiceElement));
            }
            else
            {
                foreach (string member in members.Skip(1).SkipLast(1))
                {
                    steps.Add(new MemberStep(member.EndsWith("[x]", StringComparison.Ordinal) ? member[..^3] : member));
                }
            }

            if (NarrowedType is not null)
            {
                steps.Add(new ChoiceStep(NarrowedType));
            }

            steps.Add(new ExtensionStep(Url, members[^1] == "modifierExtension"));
            return steps.ToImmutable();
        }
    }
}

internal sealed class RelativeExtensionNode(DbElement source, string? url)
{
    internal DbElement Source { get; } = source;
    internal string? Url { get; } = url;
    internal List<RelativeExtensionNode> Children { get; } = [];
    internal List<(string SourceType, string TargetType)> ValueTypes { get; } = [];
    internal DatatypeStep? Datatype { get; set; }
    internal string? ContentReferenceUrl { get; set; }
    internal bool IsOpaque { get; set; }
    internal bool IsModifier { get; set; }
    internal ImmutableArray<string> Contexts { get; set; } = [];
}

internal sealed record RepresentationOccurrence(
    ImmutableArray<UnsafeCodePathStep> SourcePath,
    ImmutableArray<RepresentationLocation> Locations);

internal sealed record UnsafeCodeSafetyResolution(ImmutableArray<RepresentationLocation> Locations, string? Failure = null);

internal sealed class ProfileRepresentationIndex
{
    internal const string DatatypeMetadataUrl = "http://hl7.org/fhir/StructureDefinition/_datatype";

    private XVerStructureCatalog? _catalog;
    private ProfileUseFacts? _profileFacts;
    private XVerOperandEvidence? _queryReader;
    private readonly XVerOperandEvidence.ConstructionRows _rows;
    private readonly int _sourcePackageKey;
    private readonly int _targetPackageKey;
    private readonly Dictionary<int, DbElement> _elements;
    private readonly Dictionary<int, DbStructureDefinition> _structures;
    private readonly ILookup<int, DbElement> _children;
    private readonly ILookup<int, DbElementType> _types;
    private readonly Dictionary<int, DbElementType> _typesByKey;
    private readonly Dictionary<int, DbValueSet> _valueSets;
    private readonly Dictionary<int, List<DbValueSetConcept>> _concepts = [];
    private readonly ILookup<int, DbValueSetConcept>? _conceptRows;
    private readonly ILookup<int, DbElementComparison> _comparisons;
    private readonly ILookup<int?, DbElementComparison> _comparisonsByTarget;
    private readonly ILookup<int, DbElementTypeComparison> _typeComparisons;
    private readonly UnsafeCodeBindingScopeResolver _bindingResolver;
    private readonly ImmutableArray<UnsafeCodeMapEvidence> _evidence;
    private readonly Dictionary<int, (DbStructureOutcome Outcome, DbElement Target)> _profiles = [];
    private readonly Dictionary<(int Outcome, int Source), List<DbElement>> _native = [];
    private readonly Dictionary<(int Outcome, int Source), List<ProfileExtensionAttachment>> _attachments = [];
    private readonly Dictionary<string, RelativeExtensionNode> _extensions = new(StringComparer.Ordinal);
    private bool _constructionActive;
    private int _queryDepth;

    internal IReadOnlyDictionary<string, RelativeExtensionNode> Extensions => _extensions;

    internal ProfileRepresentationIndex(IDbConnection db, FhirPackageComparisonPair pair)
        : this(db, pair, null)
    {
    }

    internal ProfileRepresentationIndex(IDbConnection db, FhirPackageComparisonPair pair, XVerPreparedOperation? operation)
    {
        using XVerPreparedOperation? owned = operation is null
            ? XVerPreparedOperation.Prepare(db, new(XVerPreparationKind.Construction)) : null;
        operation ??= owned!;
        operation.RequireConnection(db);
        _sourcePackageKey = pair.SourcePackageKey;
        _targetPackageKey = pair.TargetPackageKey;
        _rows = operation.Evidence.CopyConstructionRows();
        XVerOperandEvidence operands = operation.Evidence;
        bool InPair(int package) => package == _sourcePackageKey || package == _targetPackageKey;
        _elements = operands.Rows<DbElement>(DbElement.DefaultTableName).Where(row => InPair(row.FhirPackageKey)).ToDictionary(row => row.Key);
        _structures = operands.Rows<DbStructureDefinition>(DbStructureDefinition.DefaultTableName)
            .Where(row => InPair(row.FhirPackageKey)).ToDictionary(row => row.Key);

        _children = _elements.Values.Where(element => element.ParentElementKey is not null).ToLookup(element => element.ParentElementKey!.Value);
        _types = operands.Rows<DbElementType>(DbElementType.DefaultTableName).Where(row => InPair(row.FhirPackageKey)).ToLookup(type => type.ElementKey);
        _typesByKey = _types.SelectMany(group => group).ToDictionary(type => type.Key);
        _valueSets = operands.Rows<DbValueSet>(DbValueSet.DefaultTableName).Where(row => InPair(row.FhirPackageKey)).ToDictionary(row => row.Key);
        _conceptRows = operands.ContainsTable(DbValueSetConcept.DefaultTableName)
            ? operands.Rows<DbValueSetConcept>(DbValueSetConcept.DefaultTableName).ToLookup(row => row.ValueSetKey) : null;
        _bindingResolver = new(_valueSets, key =>
        {
            if (!_concepts.TryGetValue(key, out List<DbValueSetConcept>? concepts))
            {
                concepts = ReadConcepts(key);
                _concepts.Add(key, concepts);
            }

            return concepts;
        });
        _comparisons = operands.Rows<DbElementComparison>(DbElementComparison.DefaultTableName)
            .Where(row => row.SourceFhirPackageKey == _sourcePackageKey && row.TargetFhirPackageKey == _targetPackageKey)
            .ToLookup(comparison => comparison.SourceElementKey);
        _comparisonsByTarget = _comparisons.SelectMany(group => group).ToLookup(comparison => comparison.TargetElementKey);
        _typeComparisons = operands.Rows<DbElementTypeComparison>(DbElementTypeComparison.DefaultTableName)
            .Where(row => row.SourceFhirPackageKey == _sourcePackageKey && row.TargetFhirPackageKey == _targetPackageKey)
            .ToLookup(comparison => comparison.SourceElementKey);
        _evidence = !operands.ContainsTable(DbUnsafeCodeMapEvidence.DefaultTableName) ? [] :
            [.. operands.Rows<DbUnsafeCodeMapEvidence>(DbUnsafeCodeMapEvidence.DefaultTableName)
            .Where(row => row.SourceFhirPackageKey == _sourcePackageKey && row.TargetFhirPackageKey == _targetPackageKey)
            .Select(row => new UnsafeCodeMapEvidence(
                row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                new(row.SourceValueSetUrl, row.SourceValueSetVersion), new(row.TargetValueSetUrl, row.TargetValueSetVersion),
                new(row.SourceSystem, row.SourceSystemVersion), row.SourceCode,
                new(row.TargetSystem, row.TargetSystemVersion), row.TargetCode, row.Relationship, row.ExplicitNoMap,
                new(row.SourceRelativeFilename, row.ConceptMapId, row.ConceptMapUrl, row.GroupIndex, row.SourceIndex, row.TargetIndex)))];
    }

    internal void ReleasePayloads()
    {
        _constructionActive = false;
        if (_queryReader is not null)
        {
            _profileFacts?.Dispose();
            _catalog?.Dispose();
            _queryReader.Dispose();
        }
        _queryReader = null;
        _profileFacts = null;
        _catalog = null;
    }

    private List<DbValueSetConcept> ReadConcepts(int key) => _conceptRows is not null ? [.. _conceptRows[key]] :
        throw new InvalidOperationException("Representation binding requires the captured ValueSetConcept table.");

    internal void BeginConstruction(XVerPreparedOperation operation)
    {
        _catalog = operation.Catalog;
        _profileFacts = operation.GetComparisonPacket(_sourcePackageKey, _targetPackageKey).Facts;
        _constructionActive = true;
    }

    private void BeginQuery()
    {
        if (_queryDepth == 0 && !_constructionActive)
        {
            try
            {
                _queryReader = _rows.OpenReader();
                _catalog = new(_queryReader);
                _profileFacts = new(_catalog, _sourcePackageKey, _targetPackageKey);
            }
            catch
            {
                ReleasePayloads();
                throw;
            }
        }
        _queryDepth++;
    }

    private void EndQuery()
    {
        if (--_queryDepth == 0 && !_constructionActive)
        {
            ReleasePayloads();
        }
    }

    internal RelativeExtensionNode AddExtensionNode(
        string canonical, DbElement source, string? url, RelativeExtensionNode? parent)
    {
        RelativeExtensionNode node = new(source, url);
        if (parent is null)
        {
            _extensions[canonical] = node;
        }
        else
        {
            parent.Children.Add(node);
        }

        return node;
    }

    internal void RemoveExtension(string canonical) => _extensions.Remove(canonical);

    internal void AddProfile(DbStructureOutcome outcome, DbElement target)
    {
        _profiles[outcome.Key] = (outcome, target);
    }

    internal void AddNative(DbStructureOutcome outcome, DbElement target)
    {
        foreach (DbElementComparison comparison in _comparisonsByTarget[target.Key]
            .Where(comparison => comparison.SourceStructureKey == outcome.SourceStructureKey && !comparison.NotMapped))
        {
            (int, int) key = (outcome.Key, comparison.SourceElementKey);
            if (!_native.TryGetValue(key, out List<DbElement>? targets))
            {
                targets = [];
                _native[key] = targets;
            }

            if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }
    }

    internal void Attach(DbStructureOutcome outcome, DbElementOutcome source, ProfileExtensionAttachment attachment)
    {
        (int, int) key = (outcome.Key, source.SourceElementKey);
        if (!_attachments.TryGetValue(key, out List<ProfileExtensionAttachment>? attachments))
        {
            attachments = [];
            _attachments[key] = attachments;
        }

        if (!attachments.Contains(attachment))
        {
            attachments.Add(attachment);
        }
    }

    private sealed record Projection(
        ImmutableArray<RepresentationStep> Steps,
        DbElement? Native,
        RelativeExtensionNode? Extension,
        RepresentationState State = RepresentationState.Observable)
    {
        internal DbElement? SourceBindingElement { get; init; }
        internal DbElement? TargetBindingElement { get; init; }
    }

    private sealed record ResolvedOccurrence(
        DbElement? Source, List<Projection> Projections, DbElement Target, string? MissingType);

    internal ImmutableArray<RepresentationLocation> Resolve(int outcomeKey, IReadOnlyList<UnsafeCodePathStep> sourcePath)
    {
        BeginQuery();
        try
        {
            return BuildLocations(ResolveOccurrence(outcomeKey, sourcePath));
        }
        finally
        {
            EndQuery();
        }
    }

    internal UnsafeCodeSafetyResolution ResolveSafety(int outcomeKey, ImmutableArray<UnsafeCodePathStep> sourcePath)
    {
        BeginQuery();
        try
        {
            return ResolveSafetyCore(outcomeKey, sourcePath);
        }
        finally
        {
            EndQuery();
        }
    }

    private UnsafeCodeSafetyResolution ResolveSafetyCore(int outcomeKey, ImmutableArray<UnsafeCodePathStep> sourcePath)
    {
        if (!_profiles.TryGetValue(outcomeKey, out (DbStructureOutcome Outcome, DbElement Target) profile))
        {
            return new([], "Missing resource profile");
        }

        Dictionary<int, UnsafeCodeEffectiveTypeFacts> precedingFacts = [];
        DbElement? source = null;
        for (int i = 0; i < sourcePath.Length; i++)
        {
            UnsafeCodeEffectiveTypeFacts? facts = source is null ? null : SourceUseFacts(source);
            if (facts is not null)
            {
                precedingFacts[i] = facts;
            }

            if (UnsafeCodeSourceUseNormalizer.IsEndpointCandidate(sourcePath[i], facts))
            {
                break;
            }

            source = FindSource(profile.Outcome, sourcePath[i], source);
            if (source is null)
            {
                return new([], "InvalidSourceUse: missing source occurrence");
            }
        }

        UnsafeCodeSourceUseNormalization normalized = UnsafeCodeSourceUseNormalizer.Normalize(sourcePath, precedingFacts);
        if (normalized.Failure is not null)
        {
            return new([], normalized.Failure);
        }

        UnsafeCodeSourceUse? selection = normalized.SourceUse;
        UnsafeCodeTypeFact? selected = null;
        if (selection is not null)
        {
            selected = precedingFacts[sourcePath.Length - 1].Types.Single(type => type.TypeUseToken == selection.TypeUseToken);
        }
        else if (source is not null)
        {
            UnsafeCodeEffectiveTypeFacts facts = SourceUseFacts(source);
            if (facts.TypeNames.Contains("Coding", StringComparer.Ordinal) && facts.TypeNames.Contains("code", StringComparer.Ordinal))
            {
                return new([], "AmbiguousSourceUse: legacy Coding/code path cannot certify independent primitive qualification");
            }

            if (facts.TypeNames.Contains("Coding", StringComparer.Ordinal) &&
                (facts.TypeNames.Contains("boolean", StringComparer.Ordinal) ||
                    facts.TypeNames.Length == 1 && source.IsChoiceType && facts.DeclaredCount != 1))
            {
                if (!facts.Complete || facts.TypeNames.Length != 2 || !facts.TypeNames.Contains("boolean", StringComparer.Ordinal))
                {
                    return new([], "InvalidSourceUse: incomplete or conflicting legacy source declarations");
                }

                UnsafeCodeTypeFact[] coding = [.. facts.Types.Where(type => type.SourceTypeName == "Coding")];
                if (coding.Length != 1)
                {
                    return new([], "InvalidSourceUse: ambiguous legacy Coding declaration");
                }

                selected = coding[0];
            }
        }

        return new(BuildLocations(ResolveOccurrence(outcomeKey, normalized.OccurrencePath), selected));
    }

    private UnsafeCodeEffectiveTypeFacts SourceUseFacts(DbElement element)
    {
        DbElement aggregate = BaseChain(element).FirstOrDefault(basis => basis.DistinctTypes.Any(name => name.Length > 0)) ?? element;
        DbElementType[] rows = [.. Types(element)];
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
            DbStructureDefinition[] matches = [.. _structures.Values.Where(structure =>
                structure.FhirPackageKey == element.FhirPackageKey && (profile is not null
                    ? structure.UnversionedUrl == canonical.Url && (canonical.Version is null || structure.Version == canonical.Version)
                    : structure.Name == name && structure.ArtifactClass != FhirArtifactClassEnum.Profile))];
            if (matches.Length != 1 || key is not null && matches[0].Key != key ||
                matches[0].ArtifactClass is not (FhirArtifactClassEnum.PrimitiveType or FhirArtifactClassEnum.ComplexType or FhirArtifactClassEnum.Profile))
            {
                return [];
            }

            DbElement? root = profile is null ? _catalog!.ResolveRoot(matches[0]) : _profileFacts!.ForDefinition(matches[0])?.Root;
            if (root is null) return [];
            return [new UnsafeCodeRootFact(matches[0].Key, root.Id,
                BaseChain(root).Any(basis => _structures.GetValueOrDefault(basis.StructureKey)?.ArtifactClass == FhirArtifactClassEnum.Resource ||
                    IsResource(basis)))];
        }
    }

    private ResolvedOccurrence? ResolveOccurrence(int outcomeKey, IReadOnlyList<UnsafeCodePathStep> sourcePath)
    {
        if (!_profiles.TryGetValue(outcomeKey, out (DbStructureOutcome Outcome, DbElement Target) profile) || sourcePath.Count == 0)
        {
            return null;
        }

        List<Projection> projections = [new([], profile.Target, null)];
        List<(string Element, ImmutableArray<ImmutableArray<RepresentationStep>> Steps, string[] Types)> ancestors = [];
        DbElement? source = null;
        DbElement? sourceBindingContext = null;
        for (int i = 0; i < sourcePath.Count; i++)
        {
            UnsafeCodePathStep step = sourcePath[i];
            source = FindSource(profile.Outcome, step, source);
            if (source is null)
            {
                return new(null, [], profile.Target, step.TypeName);
            }

            if (i > 0)
            {
                if (step.TypeName is not null && ancestors.Count > 1)
                {
                    int typeAncestor = ancestors.FindLastIndex(ancestors.Count - 2,
                        item => item.Types.Contains(step.TypeName, StringComparer.Ordinal));
                    if (typeAncestor >= 0 && typeAncestor < ancestors.Count - 1)
                    {
                        ImmutableArray<ImmutableArray<RepresentationStep>> prefixes = ancestors[typeAncestor].Steps;
                        projections = projections.Select(projection => projection with { Steps = CloseCycle(prefixes, projection.Steps) }).ToList();
                    }
                }

                projections = projections.SelectMany(projection => Project(projection, source, step)).ToList();
            }

            bool directUse = !sourcePath.Take(i + 1).Any(part => part.IsContentReference || part.TypeName is not null);
            if (directUse && _native.TryGetValue((outcomeKey, source.Key), out List<DbElement>? native))
            {
                projections.AddRange(native.Select(target => new Projection(NativeSteps(target), target, null)
                {
                    SourceBindingElement = sourceBindingContext,
                    TargetBindingElement = EnclosingBinding(target),
                }));
            }

            if (directUse && _attachments.TryGetValue((outcomeKey, source.Key), out List<ProfileExtensionAttachment>? attachments))
            {
                foreach (ProfileExtensionAttachment attachment in attachments)
                {
                    if (_extensions.TryGetValue(attachment.Url, out RelativeExtensionNode? extension))
                    {
                        ImmutableArray<RepresentationStep> steps = attachment.Steps.SetItem(attachment.Steps.Length - 1,
                            new ExtensionStep(extension.Url!, attachment.Steps.OfType<ExtensionStep>().Last().IsModifier));
                        if (extension.Datatype is not null)
                        {
                            steps = steps.Add(extension.Datatype);
                        }

                        projections.Add(new(steps, null, extension));
                    }
                    else
                    {
                        projections.Add(new(attachment.Steps, null, null, RepresentationState.OpaqueSubstitution));
                    }
                }
            }

            if (i > 0)
            {
                int ancestor = ancestors.FindLastIndex(item => item.Element == step.ElementId);
                if (ancestor >= 0)
                {
                    ImmutableArray<ImmutableArray<RepresentationStep>> prefixes = ancestors[ancestor].Steps;
                    projections = projections.Select(projection => projection with
                    {
                        Steps = CloseCycle(prefixes, projection.Steps),
                    }).ToList();
                }
            }

            DbElement? sourceBinding = BindingElement(source);
            sourceBindingContext = sourceBinding ?? sourceBindingContext;
            projections = projections.Select(projection => projection with
            {
                SourceBindingElement = sourceBindingContext,
                TargetBindingElement = projection.Native is null ? projection.TargetBindingElement
                    : BindingElement(projection.Native) ?? projection.TargetBindingElement,
            }).ToList();

            if (projections.Count > 0)
            {
                ancestors.Add((step.ElementId, [.. projections.Select(projection => projection.Steps).DistinctBy(StepsKey)],
                    [.. Types(source).Select(type => type.TypeProfile ?? type.TypeName ?? type.Literal)]));
            }
        }

        return new(source, projections, profile.Target, sourcePath[^1].TypeName);
    }

    private ImmutableArray<RepresentationLocation> BuildLocations(ResolvedOccurrence? occurrence, UnsafeCodeTypeFact? selected = null)
    {
        if (occurrence is null)
        {
            return [];
        }

        DbElement? source = occurrence.Source;
        if (source is null || occurrence.Projections.Count == 0)
        {
            return [new([], selected?.SourceTypeName ?? occurrence.MissingType, null, RepresentationState.Missing, [])];
        }

        List<RepresentationLocation> locations = [];
        foreach (Projection projection in occurrence.Projections)
        {
            if (projection.Extension is RelativeExtensionNode extension)
            {
                if (extension.IsOpaque)
                {
                    AddLocation(new(projection.Steps, selected?.SourceTypeName ?? source.DistinctTypeLiterals, null, RepresentationState.OpaqueSubstitution, []), projection);
                }
                else if (extension.ValueTypes.Count > 0)
                {
                    foreach ((string sourceType, string targetType) in extension.ValueTypes)
                    {
                        if (selected is not null && !SelectedExtensionType(sourceType))
                        {
                            continue;
                        }

                        AddLocation(new(projection.Steps.Add(new MemberStep("value")).Add(new ChoiceStep(targetType)),
                            selected?.SourceTypeName ?? sourceType, targetType, projection.State, []), projection);
                    }

                    if (selected is not null && !extension.ValueTypes.Any(type => SelectedExtensionType(type.SourceType)))
                    {
                        AddLocation(new(projection.Steps, selected.SourceTypeName, null, RepresentationState.Missing, []), projection);
                    }
                }
                else
                {
                    if (projection.State != RepresentationState.Observable)
                    {
                        AddLocation(new(projection.Steps, selected?.SourceTypeName ?? source.DistinctTypeLiterals, null, projection.State, []), projection);
                        continue;
                    }

                    AddLocation(new(projection.Steps, selected?.SourceTypeName ?? source.DistinctTypeLiterals, extension.Datatype?.Name, projection.State, []), projection);
                }
            }
            else
            {
                UnsafeCodeCanonical sourceDirect = projection.SourceBindingElement is null ? new(null, null) : Binding(projection.SourceBindingElement);
                UnsafeCodeCanonical targetDirect = projection.TargetBindingElement is null ? new(null, null) : Binding(projection.TargetBindingElement);
                UnsafeCodeBindingScope? sourceScope = _bindingResolver.Resolve(projection.SourceBindingElement);
                UnsafeCodeBindingScope? targetScope = _bindingResolver.Resolve(projection.TargetBindingElement);
                ImmutableArray<CodeTransformation> transformations = CodeTransformation.FromEvidence(_evidence
                    .Select(evidence => UnsafeCodeEvidenceApplicability.MatchProjection(evidence, sourceDirect, targetDirect, sourceScope, targetScope))
                    .Where(match => match.IsMatch).Select(match => match.Evidence));
                string[] targetTypes = projection.Native is null ? [] : [.. Types(projection.Native)
                    .Select(type => type.TypeName ?? type.Literal).Distinct()];
                if (targetTypes.Length == 0 && projection.Native is not null)
                {
                    targetTypes = projection.Native.DistinctTypeLiterals.Split(',', StringSplitOptions.RemoveEmptyEntries);
                }
                if (selected is not null)
                {
                    if (projection.State != RepresentationState.Observable)
                    {
                        AddLocation(new(projection.Steps, selected.SourceTypeName, null, projection.State, transformations), projection);
                        continue;
                    }

                    DbElementTypeComparison[] comparisons = [.. BaseChain(source).Select(basis => _typeComparisons[basis.Key])
                        .FirstOrDefault(rows => rows.Any())?.Where(comparison =>
                            projection.Native is not null && BaseChain(projection.Native).Any(target => target.Key == comparison.TargetElementKey) &&
                            comparison.SourceTypeLiteral == selected.Literal &&
                            (selected.RowKey is null || comparison.SourceElementTypeKey == selected.RowKey)) ?? []];
                    if (comparisons.Length == 0)
                    {
                        AddLocation(new(projection.Steps, selected.SourceTypeName, null, RepresentationState.Missing, transformations), projection);
                    }

                    foreach (DbElementTypeComparison comparison in comparisons)
                    {
                        DbElementType? targetRow = comparison.TargetElementTypeKey is int key ? _typesByKey.GetValueOrDefault(key) : null;
                        UnsafeCodeEffectiveTypeFacts? targetFacts = projection.Native is null ? null : SourceUseFacts(projection.Native);
                        UnsafeCodeTypeFact[] targetUses = [.. targetFacts?.Types.Where(type =>
                            targetRow is not null ? type.RowKey == targetRow.Key
                                : type.RowKey is null && type.Literal == comparison.TargetTypeLiteral) ?? []];
                        bool declaredTarget = targetFacts?.Complete == true && targetUses.Length == 1 &&
                            comparison.TargetTypeLiteral == targetUses[0].Literal;
                        string? actualType = declaredTarget ? targetUses[0].SourceTypeName : null;
                        RepresentationState state = comparison.NotMapped || actualType is null
                            ? RepresentationState.Missing : projection.State;
                        ImmutableArray<RepresentationStep> steps = projection.Native?.IsChoiceType == true && actualType is not null
                            ? projection.Steps.Add(new ChoiceStep(actualType)) : projection.Steps;
                        AddLocation(new(steps, selected.SourceTypeName, actualType, state, transformations), projection);
                    }

                    continue;
                }
                IEnumerable<(string Source, string? Target)> types = Types(source)
                    .Select(type => (type.TypeName ?? type.Literal, targetTypes.Contains(type.TypeName ?? type.Literal, StringComparer.Ordinal)
                        ? type.TypeName ?? type.Literal : targetTypes.Length == 1 ? targetTypes[0] : (string?)null)).Distinct();
                if (!types.Any())
                {
                    types = [(source.DistinctTypeLiterals, projection.Native?.DistinctTypeLiterals)];
                }

                foreach ((string sourceType, string? targetType) in types)
                {
                    DbElementTypeComparison? comparison = _typeComparisons[source.Key].FirstOrDefault(comparison =>
                        !comparison.NotMapped && comparison.TargetElementKey == projection.Native?.Key &&
                        (comparison.SourceTypeLiteral == sourceType ||
                            _typesByKey.GetValueOrDefault(comparison.SourceElementTypeKey)?.TypeName == sourceType));
                    string? actualType = comparison?.TargetElementTypeKey is int targetTypeKey
                        ? _typesByKey.GetValueOrDefault(targetTypeKey)?.TypeName ?? targetType
                        : targetType;
                    ImmutableArray<RepresentationStep> steps = projection.Native?.IsChoiceType == true && actualType is not null
                        ? projection.Steps.Add(new ChoiceStep(actualType)) : projection.Steps;
                    AddLocation(new(steps, sourceType, actualType, projection.State, transformations), projection);
                }
            }
        }

        return [.. locations.Select(location => location with { Steps = WithOwners(location.Steps, occurrence.Target) })
            .DistinctBy(location => (StepsKey(location.Steps), location.SourceType, location.TargetType, location.State, location.SourceBinding))];

        void AddLocation(RepresentationLocation location, Projection projection)
        {
            CodeCarrierLocation? carrier = Carrier(location, projection.Extension);
            RepresentationState state = location.State;
            CodeCarrierKind sourceKind = CarrierKind(location.SourceType);
            bool qualifiedSource = sourceKind is CodeCarrierKind.Coding or CodeCarrierKind.Quantity;
            if (state == RepresentationState.Observable && (qualifiedSource || carrier?.Kind is CodeCarrierKind.Coding or CodeCarrierKind.Quantity))
            {
                if (carrier is null || carrier.Code.IsDefault ||
                    (carrier.System.IsDefault && !PreservesQualification(projection, includeVersion: false)))
                {
                    state = RepresentationState.ErasedIdentity;
                }
                else if (sourceKind == CodeCarrierKind.Coding && carrier.Version.IsDefault &&
                    !PreservesQualification(projection, includeVersion: true))
                {
                    state = RepresentationState.ErasedVersion;
                }
            }

            locations.Add(location with
            {
                Carrier = carrier,
                State = state,
                SourceElementId = source.Id,
                SourceBinding = Binding(projection.SourceBindingElement ?? source),
            });
        }

        bool SelectedExtensionType(string sourceType) => sourceType == selected!.Literal ||
            sourceType == selected.TypeUseToken ||
            sourceType == selected.SourceTypeName && Types(source).Count(type => type.TypeName == sourceType) <= 1;
    }

    private static CodeCarrierKind CarrierKind(string? type) => type switch
    {
        "code" => CodeCarrierKind.Primitive,
        "Coding" => CodeCarrierKind.Coding,
        "Quantity" or "Age" or "Count" or "Distance" or "Duration" or "MoneyQuantity" or "SimpleQuantity" => CodeCarrierKind.Quantity,
        "CodeableConcept" => CodeCarrierKind.CodeableConcept,
        "CodeableReference" => CodeCarrierKind.CodeableReference,
        _ => CodeCarrierKind.None,
    };

    private CodeCarrierLocation? Carrier(RepresentationLocation location, RelativeExtensionNode? extension)
    {
        CodeCarrierKind kind = location.TargetType == "string" && CarrierKind(location.SourceType) is
            CodeCarrierKind.Primitive or CodeCarrierKind.Coding or CodeCarrierKind.Quantity
            ? CodeCarrierKind.Primitive : CarrierKind(location.TargetType);
        if (kind == CodeCarrierKind.None)
        {
            return null;
        }

        bool decomposed = extension is not null && extension.ValueTypes.Count == 0;
        return kind switch
        {
            CodeCarrierKind.Primitive => new(kind, default, default, [], decomposed),
            CodeCarrierKind.Coding or CodeCarrierKind.Quantity => new(kind, Field("system"), Field("version"), Field("code"), decomposed),
            _ => new(kind, default, default, default, decomposed),
        };

        ImmutableArray<RepresentationStep> Field(string name)
        {
            if (decomposed)
            {
                RelativeExtensionNode? child = extension!.Children.SingleOrDefault(child => child.Source.Name == name);
                return child is not null && child.ValueTypes.Count == 1
                    ? [new ExtensionStep(child.Url!), new MemberStep("value"), new ChoiceStep(child.ValueTypes[0].TargetType)]
                    : default;
            }

            DbElement? root = TypeRoot(TargetPackageKey, location.TargetType);
            DbElement? field = root is null ? null : EffectiveChildren(root).SingleOrDefault(child => child.Name == name);
            return field is not null ? [new MemberStep(MemberName(field))] : default;
        }
    }

    private IEnumerable<Projection> Project(Projection parent, DbElement source, UnsafeCodePathStep step)
    {
        if (parent.State != RepresentationState.Observable)
        {
            yield return parent;
            yield break;
        }

        if (parent.Extension is RelativeExtensionNode extension)
        {
            if (extension.IsOpaque)
            {
                yield return parent with { State = RepresentationState.OpaqueSubstitution };
                yield break;
            }

            if (extension.ContentReferenceUrl is string reference && _extensions.TryGetValue(reference, out RelativeExtensionNode? referenced))
            {
                extension = referenced;
            }

            if (step.IsContentReference && extension.Source.Key == source.Key)
            {
                yield return parent with { Extension = extension };
                yield break;
            }

            foreach (RelativeExtensionNode child in extension.Children)
            {
                ImmutableArray<RepresentationStep> prefix = parent.Steps.Add(new ExtensionStep(child.Url!));
                if (child.Datatype is not null)
                {
                    prefix = prefix.Add(child.Datatype);
                }

                if (child.Source.Key == source.Key)
                {
                    yield return parent with { Steps = prefix, Native = null, Extension = child, TargetBindingElement = null };
                }
                else if (!child.Source.Id.Contains('.'))
                {
                    foreach (Projection projection in Project(parent with
                    {
                        Steps = prefix, Native = null, Extension = child, TargetBindingElement = null,
                    }, source, step))
                    {
                        yield return projection;
                    }
                }
            }

            foreach ((string sourceType, string targetType) in extension.ValueTypes)
            {
                if (step.TypeName is not null && step.TypeName != sourceType &&
                    !Types(extension.Source).Any(type => type.TypeName == sourceType && type.TypeProfile == step.TypeName))
                {
                    continue;
                }

                DbElement? root = TypeRoot(source.FhirPackageKey == extension.Source.FhirPackageKey
                    ? TargetPackageKey : source.FhirPackageKey, targetType);
                if (root is not null)
                {
                    foreach (Projection projection in Project(parent with
                    {
                        Steps = parent.Steps.Add(new MemberStep("value")).Add(new ChoiceStep(targetType)), Native = root, Extension = null,
                    }, source, step))
                    {
                        yield return projection;
                    }
                }
            }

            yield break;
        }

        if (parent.Native is not DbElement target)
        {
            yield return parent with { State = RepresentationState.Missing };
            yield break;
        }

        if (step.IsContentReference)
        {
            DbElement? reference = target.ContentReferenceSourceKey is int key ? _elements.GetValueOrDefault(key) : target;
            yield return parent with { Native = reference };
            yield break;
        }

        List<DbElement> contexts = [target];
        DbElementType[] targetTypes = [.. Types(target)];
        foreach (DbElementType type in targetTypes)
        {
            DbElement? root = _catalog!.ResolveRoot(_catalog.ResolveTypeUse(type));
            if (root is not null && !contexts.Contains(root))
            {
                contexts.Add(root);
            }
        }

        foreach (string type in target.DistinctTypeLiterals.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (targetTypes.Any(targetType => targetType.TypeName == type))
            {
                continue;
            }

            DbElement? root = TypeRoot(target.FhirPackageKey, type);
            if (root is not null && !contexts.Contains(root))
            {
                contexts.Add(root);
            }
        }

        foreach (DbElement context in contexts)
        {
            DbElement[] children = [.. EffectiveChildren(context)];
            DbElementComparison[] comparisons = [.. BaseChain(source).Select(basis => _comparisons[basis.Key])
                .FirstOrDefault(rows => rows.Any()) ?? []];
            List<DbElement> mapped = [.. children.Where(child => BaseChain(child).Any(basis => comparisons.Any(comparison =>
                !comparison.NotMapped && comparison.TargetElementKey == basis.Key)))];
            if (mapped.Count == 0 && comparisons.Length == 0)
            {
                string sourceToken = source.Id[(source.Id.LastIndexOf('.') + 1)..];
                mapped.AddRange(children.Where(child => child.Id[(child.Id.LastIndexOf('.') + 1)..] == sourceToken ||
                    (!sourceToken.Contains(':') && !child.Id.Contains(':') &&
                        source.BasePath is not null && child.BasePath == source.BasePath)));
            }

            foreach (DbElement child in mapped)
            {
                ImmutableArray<RepresentationStep> steps = parent.Steps.Add(new MemberStep(MemberName(child)));
                if (target.IsChoiceType && context.Key != target.Key)
                {
                    steps = parent.Steps.Add(new ChoiceStep(context.Id)).Add(new MemberStep(MemberName(child)));
                }

                if (IsResource(child))
                {
                    steps = steps.Add(new ResourceOwnerStep(child.DistinctTypeLiterals));
                }

                yield return parent with { Steps = steps, Native = child, Extension = null };
            }

            foreach (RelativeExtensionNode extensionUse in _extensions.Values.Where(extensionUse =>
                extensionUse.Source.Key == source.Key && extensionUse.Contexts.Contains(context.Id, StringComparer.Ordinal)))
            {
                ImmutableArray<RepresentationStep> steps = parent.Steps.Add(new ExtensionStep(extensionUse.Url!, extensionUse.IsModifier));
                if (extensionUse.Datatype is not null)
                {
                    steps = steps.Add(extensionUse.Datatype);
                }

                yield return parent with { Steps = steps, Native = null, Extension = extensionUse, TargetBindingElement = null };
            }
        }
    }

    private int TargetPackageKey => _profiles.Values.First().Outcome.TargetFhirPackageKey;

    internal IEnumerable<RepresentationOccurrence> Occurrences(int outcomeKey)
    {
        BeginQuery();
        try
        {
            foreach (RepresentationOccurrence occurrence in EnumerateOccurrences(outcomeKey))
            {
                yield return occurrence;
            }
        }
        finally
        {
            EndQuery();
        }
    }

    private IEnumerable<RepresentationOccurrence> EnumerateOccurrences(int outcomeKey)
    {
        if (!_profiles.TryGetValue(outcomeKey, out (DbStructureOutcome Outcome, DbElement Target) profile))
        {
            yield break;
        }

        foreach (DbElement root in _elements.Values.Where(element => element.StructureKey == profile.Outcome.SourceStructureKey && !element.Id.Contains('.')))
        {
            foreach (RepresentationOccurrence occurrence in Walk(root, [], [], null, false))
            {
                yield return occurrence;
            }
        }

        IEnumerable<RepresentationOccurrence> Walk(DbElement source, ImmutableArray<UnsafeCodePathStep> prefix,
            HashSet<(int, int, string)> edges, string? typeName, bool contentReference)
        {
            if (!_profileFacts!.Permits(source))
            {
                yield break;
            }

            ImmutableArray<UnsafeCodePathStep> path = prefix.Add(new(source.Id, typeName, contentReference));
            yield return new(path, Resolve(outcomeKey, path));
            if (prefix.Length > 0 && IsResource(source))
            {
                yield break;
            }

            foreach ((DbElement child, string kind, string? type, bool reference) in Children(source))
            {
                (int, int, string) edge = (source.Key, child.Key, kind);
                if (edges.Add(edge))
                {
                    foreach (RepresentationOccurrence occurrence in Walk(child, path, edges, type, reference))
                    {
                        yield return occurrence;
                    }

                    edges.Remove(edge);
                }
            }
        }
    }

    private IEnumerable<(DbElement Child, string Kind, string? Type, bool Reference)> Children(DbElement source)
    {
        (string RelativeId, DbElement Child)[] children = [.. EffectiveChildEntries(source)];
        foreach ((string _, DbElement child) in children)
        {
            yield return (child, "child", null, false);
        }

        foreach (DbElementType type in Types(source))
        {
            if (type.TypeName == "Reference")
            {
                continue;
            }

            DbElement? root = _catalog!.ResolveRoot(_catalog.ResolveTypeUse(type));
            if (root is not null && !IsResource(root))
            {
                foreach ((string _, DbElement child) in EffectiveChildEntries(root).Where(child =>
                    !children.Any(local => local.RelativeId == child.RelativeId) &&
                        _profileFacts!.PermitsChild(source, child.Child, root.StructureKey)))
                {
                    yield return (child, "type", type.TypeProfile ?? type.TypeName, false);
                }
            }
        }

        if (source.ContentReferenceSourceKey is int key && _elements.TryGetValue(key, out DbElement? reference))
        {
            yield return (reference, "reference", null, true);
        }
    }

    private DbElement? TypeRoot(int packageKey, string? name, int? structureKey = null)
    {
        DbStructureDefinition? owner = structureKey is int key ? _catalog!.GetStructure(key) :
            name?.Contains("://", StringComparison.Ordinal) == true
                ? _catalog!.ResolveDefinition(packageKey, name)
                : _catalog!.ResolveNativeType(packageKey, name, required: false);
        if (owner is not null && owner.FhirPackageKey != packageKey)
        {
            throw new InvalidOperationException($"Type root owner {owner.Key} is not in package {packageKey}.");
        }
        return owner is null ? null : _catalog!.ResolveRoot(owner);
    }

    private IEnumerable<DbElement> BaseChain(DbElement element)
    {
        HashSet<int> visited = [];
        while (visited.Add(element.Key))
        {
            yield return element;
            DbElement? basis = element.BaseElementKey is int key ? _elements.GetValueOrDefault(key) : null;
            basis ??= !element.Id.Contains('.') && element.BaseStructureKey is int structureKey
                ? TypeRoot(element.FhirPackageKey, null, structureKey) : null;
            if (basis is null)
            {
                yield break;
            }

            element = basis;
        }
    }

    private IEnumerable<(string RelativeId, DbElement Child)> EffectiveChildEntries(DbElement element) =>
        BaseChain(element).SelectMany(basis => _children[basis.Key].Select(child => (
            RelativeId: child.Id.StartsWith(basis.Id + ".", StringComparison.Ordinal) ? child.Id[basis.Id.Length..] : child.Id,
            Child: child))).DistinctBy(item => item.RelativeId, StringComparer.Ordinal);

    private IEnumerable<DbElement> EffectiveChildren(DbElement element) =>
        EffectiveChildEntries(element).Select(item => item.Child);

    private IEnumerable<DbElementType> Types(DbElement element) =>
        BaseChain(element).Select(basis => _types[basis.Key]).FirstOrDefault(types => types.Any()) ?? [];

    private ImmutableArray<RepresentationStep> WithOwners(ImmutableArray<RepresentationStep> steps, DbElement root)
    {
        ImmutableArray<RepresentationStep>.Builder result = ImmutableArray.CreateBuilder<RepresentationStep>();
        string path = root.Path;
        bool native = true;
        for (int i = 0; i < steps.Length; i++)
        {
            RepresentationStep step = steps[i];
            result.Add(step);
            if (step is ExtensionStep)
            {
                native = false;
            }
            else if (native && step is MemberStep member)
            {
                path += "." + member.Name;
                DbElement? field = _elements.Values.FirstOrDefault(element => element.StructureKey == root.StructureKey &&
                    (element.Path == path || element.Path == path + "[x]"));
                if (field is not null)
                {
                    path = field.Path;
                    if (IsResource(field) && (i + 1 == steps.Length || steps[i + 1] is not ResourceOwnerStep))
                    {
                        result.Add(new ResourceOwnerStep(field.DistinctTypeLiterals));
                    }
                }
            }
        }

        return result.ToImmutable();
    }

    private bool IsResource(DbElement element) =>
        element.DistinctTypeLiterals.Split(',').Any(type => type is "Resource" or "DomainResource" ||
            _structures.Values.Any(structure => structure.FhirPackageKey == element.FhirPackageKey &&
                structure.Name == type && structure.ArtifactClass == FhirArtifactClassEnum.Resource));

    private DbElement? FindSource(DbStructureOutcome outcome, UnsafeCodePathStep step, DbElement? parent)
    {
        List<DbElement> candidates = [.. _elements.Values.Where(element =>
            element.FhirPackageKey == outcome.SourceFhirPackageKey && element.Id == step.ElementId)];
        if (parent is null)
        {
            return candidates.SingleOrDefault(element => element.StructureKey == outcome.SourceStructureKey);
        }

        if (step.IsContentReference && parent.ContentReferenceSourceKey is int referenceKey)
        {
            return candidates.SingleOrDefault(element => element.Key == referenceKey);
        }

        if (step.TypeName is not null)
        {
            DbElementType? type = Types(parent).FirstOrDefault(type => (type.TypeProfile ?? type.TypeName) == step.TypeName);
            int? structureKey = type?.TypeProfileStructureKey ?? (type?.TypeProfile is null ? type?.TypeStructureKey : null);
            if (structureKey is not null)
            {
                DbElement? root = TypeRoot(outcome.SourceFhirPackageKey, null, structureKey);
                return root is null ? null : EffectiveChildren(root).SingleOrDefault(element => element.Id == step.ElementId);
            }

            UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(step.TypeName);
            DbStructureDefinition? structure = _structures.Values.FirstOrDefault(structure =>
                structure.FhirPackageKey == outcome.SourceFhirPackageKey &&
                ((structure.UnversionedUrl == canonical.Url && (canonical.Version is null || structure.Version == canonical.Version)) ||
                    structure.Name == step.TypeName));
            if (structure is not null)
            {
                DbElement? root = TypeRoot(outcome.SourceFhirPackageKey, null, structure.Key);
                return root is null ? null : EffectiveChildren(root).SingleOrDefault(element => element.Id == step.ElementId);
            }
        }

        DbElement? child = EffectiveChildren(parent).SingleOrDefault(element => element.Id == step.ElementId);
        return child ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private DbElement? BindingElement(DbElement element) =>
        BaseChain(element).FirstOrDefault(basis => basis.BindingValueSet is not null || basis.BindingValueSetKey is not null);

    private DbElement? EnclosingBinding(DbElement element)
    {
        HashSet<int> visited = [];
        while (visited.Add(element.Key))
        {
            if (BindingElement(element) is DbElement binding)
            {
                return binding;
            }

            if (element.ParentElementKey is not int key || !_elements.TryGetValue(key, out DbElement? parent))
            {
                break;
            }

            element = parent;
        }

        return null;
    }

    private UnsafeCodeCanonical Binding(DbElement element)
    {
        UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(element.BindingValueSet);
        return canonical.Version is null && element.BindingValueSetKey is int key && _valueSets.TryGetValue(key, out DbValueSet? valueSet)
            ? new(valueSet.UnversionedUrl, valueSet.Version) : canonical;
    }

    private bool PreservesQualification(Projection projection, bool includeVersion)
    {
        (UnsafeCodeCanonical Identity, string? Code)[] source = BindingMembers(projection.SourceBindingElement);
        (UnsafeCodeCanonical Identity, string? Code)[] target = BindingMembers(projection.TargetBindingElement);
        return source.Length > 0 && target.Length > 0 && source.All(member =>
        {
            (UnsafeCodeCanonical Identity, string? Code)[] matchingSource = [.. source.Where(other =>
                member.Code is null || other.Code is null || member.Code == other.Code)];
            (UnsafeCodeCanonical Identity, string? Code)[] matchingTarget = [.. target.Where(other =>
                member.Code is null || other.Code is null || member.Code == other.Code)];
            return matchingSource.All(other => SameIdentity(other.Identity, member.Identity)) &&
                matchingTarget.Length > 0 && matchingTarget.All(other => SameIdentity(other.Identity, member.Identity));
        });

        bool SameIdentity(UnsafeCodeCanonical left, UnsafeCodeCanonical right) =>
            left.Url is not null && left.Url == right.Url && (!includeVersion ||
                (left.Version is not null && left.Version == right.Version));
    }

    private (UnsafeCodeCanonical Identity, string? Code)[] BindingMembers(DbElement? element)
    {
        if (element?.ValueSetBindingStrength != BindingStrength.Required)
        {
            return [];
        }

        UnsafeCodeCanonical binding = Binding(element);
        DbValueSet[] valueSets = [.. _valueSets.Values.Where(valueSet => valueSet.FhirPackageKey == element.FhirPackageKey &&
            valueSet.UnversionedUrl == binding.Url && (binding.Version is null || valueSet.Version == binding.Version))];
        if (valueSets.Length != 1)
        {
            return [];
        }

        DbValueSet valueSet = valueSets[0];
        if (valueSet.CanExpand)
        {
            if (!_concepts.TryGetValue(valueSet.Key, out List<DbValueSetConcept>? concepts))
            {
                concepts = ReadConcepts(valueSet.Key);
                _concepts.Add(valueSet.Key, concepts);
            }

            return [.. concepts.Select(concept =>
                (new UnsafeCodeCanonical(concept.System, string.IsNullOrEmpty(concept.SystemVersion) ? null : concept.SystemVersion), (string?)concept.Code))];
        }

        if (valueSet.Compose is not { } compose || compose.Exclude.Count > 0 ||
            compose.Include.Any(include => include.System is null || include.ValueSet.Any() || include.Filter.Count > 0))
        {
            return [];
        }

        return [.. compose.Include.SelectMany(include => include.Concept.Count == 0
            ? new (UnsafeCodeCanonical, string?)[] { (new(include.System, include.Version), null) }
            : include.Concept.Select(concept => (new UnsafeCodeCanonical(include.System, include.Version), (string?)concept.Code)))];
    }

    internal static ImmutableArray<RepresentationStep> NativeSteps(DbElement element) =>
        [.. element.Path.Split('.').Skip(1).Select(name => (RepresentationStep)new MemberStep(name.EndsWith("[x]", StringComparison.Ordinal) ? name[..^3] : name))];

    private static string MemberName(DbElement element) => element.Name.EndsWith("[x]", StringComparison.Ordinal) ? element.Name[..^3] : element.Name;

    private static ImmutableArray<RepresentationStep> CloseCycle(ImmutableArray<RepresentationStep> prefix, ImmutableArray<RepresentationStep> steps)
    {
        if (steps.Length <= prefix.Length || StepsKey(steps[..prefix.Length]) != StepsKey(prefix))
        {
            return steps;
        }

        ImmutableArray<RepresentationStep> body = steps[prefix.Length..];
        return prefix.LastOrDefault() is RepeatStep repeat && StepsKey(repeat.Steps) == StepsKey(body)
            ? prefix : prefix.Add(new RepeatStep(body));
    }

    private static ImmutableArray<RepresentationStep> CloseCycle(
        ImmutableArray<ImmutableArray<RepresentationStep>> prefixes, ImmutableArray<RepresentationStep> steps)
    {
        ImmutableArray<RepresentationStep>[] matches = [.. prefixes.Where(prefix =>
            prefix.Length < steps.Length && StepsKey(steps[..prefix.Length]) == StepsKey(prefix)).OrderByDescending(prefix => prefix.Length)];
        return matches.Length > 0 ? CloseCycle(matches[0], steps) : steps;
    }

    private static string StepsKey(ImmutableArray<RepresentationStep> steps) => string.Concat(steps.Select(step => step switch
    {
        MemberStep member => $"m{member.Name.Length}:{member.Name}",
        ChoiceStep choice => $"c{choice.TypeName.Length}:{choice.TypeName}",
        ExtensionStep extension => $"e{extension.IsModifier}:{extension.Url.Length}:{extension.Url}",
        ResourceOwnerStep owner => $"o{owner.TypeName.Length}:{owner.TypeName}",
        DatatypeStep datatype => $"d{datatype.Name.Length}:{datatype.Name}:{datatype.MetadataUrl.Length}:{datatype.MetadataUrl}",
        RepeatStep repeat => $"r{repeat.IncludeInitial}:({StepsKey(repeat.Steps)})",
        _ => throw new InvalidOperationException($"Unknown representation step {step.GetType().Name}."),
    }));
}
