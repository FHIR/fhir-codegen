// <copyright file="ProfileRepresentationIndex.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.Models;
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
    string? TargetSystem, string? TargetVersion, string? TargetCode);

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

internal sealed class ProfileRepresentationIndex
{
    internal const string DatatypeMetadataUrl = "http://hl7.org/fhir/StructureDefinition/_datatype";

    private readonly IDbConnection _db;
    private readonly Dictionary<int, DbElement> _elements;
    private readonly Dictionary<int, DbStructureDefinition> _structures;
    private readonly ILookup<int, DbElement> _children;
    private readonly ILookup<int, DbElementType> _types;
    private readonly Dictionary<int, DbElementType> _typesByKey;
    private readonly Dictionary<int, DbValueSet> _valueSets;
    private readonly Dictionary<int, List<DbValueSetConcept>> _concepts = [];
    private readonly ILookup<int, DbElementComparison> _comparisons;
    private readonly ILookup<int?, DbElementComparison> _comparisonsByTarget;
    private readonly ILookup<int, DbElementTypeComparison> _typeComparisons;
    private readonly List<DbUnsafeCodeMapEvidence> _evidence;
    private readonly Dictionary<int, (DbStructureOutcome Outcome, DbElement Target)> _profiles = [];
    private readonly Dictionary<(int Outcome, int Source), List<DbElement>> _native = [];
    private readonly Dictionary<(int Outcome, int Source), List<ProfileExtensionAttachment>> _attachments = [];
    private readonly Dictionary<string, RelativeExtensionNode> _extensions = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, RelativeExtensionNode> Extensions => _extensions;

    internal ProfileRepresentationIndex(IDbConnection db, FhirPackageComparisonPair pair)
    {
        _db = db;
        _elements = DbElement.SelectDict(db, FhirPackageKey: pair.SourcePackageKey);
        foreach (DbElement element in DbElement.SelectList(db, FhirPackageKey: pair.TargetPackageKey))
        {
            _elements[element.Key] = element;
        }

        _structures = DbStructureDefinition.SelectDict(db, FhirPackageKey: pair.SourcePackageKey);
        foreach (DbStructureDefinition structure in DbStructureDefinition.SelectList(db, FhirPackageKey: pair.TargetPackageKey))
        {
            _structures[structure.Key] = structure;
        }

        _children = _elements.Values.Where(element => element.ParentElementKey is not null).ToLookup(element => element.ParentElementKey!.Value);
        _types = DbElementType.SelectList(db, FhirPackageKey: pair.SourcePackageKey)
            .Concat(DbElementType.SelectList(db, FhirPackageKey: pair.TargetPackageKey)).ToLookup(type => type.ElementKey);
        _typesByKey = _types.SelectMany(group => group).ToDictionary(type => type.Key);
        _valueSets = DbValueSet.SelectDict(db, FhirPackageKey: pair.SourcePackageKey);
        foreach (DbValueSet valueSet in DbValueSet.SelectList(db, FhirPackageKey: pair.TargetPackageKey))
        {
            _valueSets[valueSet.Key] = valueSet;
        }
        _comparisons = DbElementComparison.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .ToLookup(comparison => comparison.SourceElementKey);
        _comparisonsByTarget = _comparisons.SelectMany(group => group).ToLookup(comparison => comparison.TargetElementKey);
        _typeComparisons = DbElementTypeComparison.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            .ToLookup(comparison => comparison.SourceElementKey);
        try
        {
            _evidence = DbUnsafeCodeMapEvidence.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 1 && error.Message.Contains("no such table: UnsafeCodeMapEvidence", StringComparison.Ordinal))
        {
            // Feature readiness belongs to export preflight, not construction.
            _evidence = [];
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

    internal ImmutableArray<RepresentationLocation> Resolve(int outcomeKey, IReadOnlyList<UnsafeCodePathStep> sourcePath)
    {
        if (!_profiles.TryGetValue(outcomeKey, out (DbStructureOutcome Outcome, DbElement Target) profile) || sourcePath.Count == 0)
        {
            return [];
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
                return [new([], step.TypeName, null, RepresentationState.Missing, [])];
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

        if (source is null || projections.Count == 0)
        {
            return [new([], sourcePath[^1].TypeName, null, RepresentationState.Missing, [])];
        }

        List<RepresentationLocation> locations = [];
        foreach (Projection projection in projections)
        {
            if (projection.Extension is RelativeExtensionNode extension)
            {
                if (extension.IsOpaque)
                {
                    AddLocation(new(projection.Steps, source.DistinctTypeLiterals, null, RepresentationState.OpaqueSubstitution, []), projection);
                }
                else if (extension.ValueTypes.Count > 0)
                {
                    foreach ((string sourceType, string targetType) in extension.ValueTypes)
                    {
                        AddLocation(new(projection.Steps.Add(new MemberStep("value")).Add(new ChoiceStep(targetType)),
                            sourceType, targetType, projection.State, []), projection);
                    }
                }
                else
                {
                    if (projection.State != RepresentationState.Observable)
                    {
                        AddLocation(new(projection.Steps, source.DistinctTypeLiterals, null, projection.State, []), projection);
                        continue;
                    }

                    AddLocation(new(projection.Steps, source.DistinctTypeLiterals, extension.Datatype?.Name, projection.State, []), projection);
                }
            }
            else
            {
                ImmutableArray<CodeTransformation> transformations = [.. _evidence.Where(evidence =>
                        !evidence.ExplicitNoMap && evidence.SourceCode is not null && evidence.TargetCode is not null &&
                        ScopeMatches(projection.SourceBindingElement, evidence.SourceValueSetUrl, evidence.SourceValueSetVersion) &&
                        ScopeMatches(projection.TargetBindingElement, evidence.TargetValueSetUrl, evidence.TargetValueSetVersion))
                    .Select(evidence => new CodeTransformation(evidence.SourceSystem, evidence.SourceSystemVersion, evidence.SourceCode,
                        evidence.TargetSystem, evidence.TargetSystemVersion, evidence.TargetCode)).Distinct()];
                string[] targetTypes = projection.Native is null ? [] : [.. Types(projection.Native)
                    .Select(type => type.TypeName ?? type.Literal).Distinct()];
                if (targetTypes.Length == 0 && projection.Native is not null)
                {
                    targetTypes = projection.Native.DistinctTypeLiterals.Split(',', StringSplitOptions.RemoveEmptyEntries);
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

        return [.. locations.Select(location => location with { Steps = WithOwners(location.Steps, profile.Target) })
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
            DbElement? root = TypeRoot(target.FhirPackageKey, type.TypeName, type.TypeProfileStructureKey ?? type.TypeStructureKey);
            if (root is not null)
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
                mapped.AddRange(children.Where(child => child.Name == source.Name ||
                    (source.BasePath is not null && child.BasePath == source.BasePath)));
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
            if (source.MaxCardinalityString == "0")
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
        DbElement[] children = [.. EffectiveChildren(source)];
        foreach (DbElement child in children)
        {
            yield return (child, "child", null, false);
        }

        foreach (DbElementType type in Types(source))
        {
            if (type.TypeName == "Reference")
            {
                continue;
            }

            DbElement? root = TypeRoot(source.FhirPackageKey, type.TypeName, type.TypeProfileStructureKey ?? type.TypeStructureKey);
            if (root is not null && !IsResource(root))
            {
                foreach (DbElement child in EffectiveChildren(root).Where(child => !children.Any(local => local.Name == child.Name)))
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

    private DbElement? TypeRoot(int packageKey, string? name, int? structureKey = null) =>
        _elements.Values.FirstOrDefault(element => element.FhirPackageKey == packageKey && !element.Id.Contains('.') &&
            (structureKey is not null ? element.StructureKey == structureKey : element.Id == name));

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

    private IEnumerable<DbElement> EffectiveChildren(DbElement element) =>
        BaseChain(element).SelectMany(basis => _children[basis.Key]).DistinctBy(child => child.Name, StringComparer.Ordinal);

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

    private bool ScopeMatches(DbElement? element, string? url, string? version)
    {
        UnsafeCodeCanonical binding = element is null ? new(null, null) : Binding(element);
        return url is not null && binding.Url == url && (version is null || binding.Version == version);
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
                concepts = DbValueSetConcept.SelectList(_db, ValueSetKey: valueSet.Key);
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
        if (steps.Length <= prefix.Length || !steps.Take(prefix.Length).SequenceEqual(prefix))
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
            prefix.Length < steps.Length && steps.Take(prefix.Length).SequenceEqual(prefix)).OrderByDescending(prefix => prefix.Length)];
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
