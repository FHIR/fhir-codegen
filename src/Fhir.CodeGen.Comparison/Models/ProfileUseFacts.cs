// <copyright file="ProfileUseFacts.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace Fhir.CodeGen.Comparison.Models;

internal sealed record ProfileUseKey(
    int PackageKey, int OwnerKey, int ElementKey, int TypeUseToken,
    string? ProfileAlternative, string? TargetProfileAlternative, string SliceContext);

internal sealed record ProfileFactOrigin(int PackageKey, int OwnerKey, string Canonical, string? Version, string ElementId);

internal enum ProfileRestrictionKind
{
    Binding,
    Fixed,
    Pattern,
    Slicing,
    Invariant,
    MinimumValue,
    MaximumValue,
    MaximumLength,
    Modifier,
    SemanticExtension,
    Representation,
    MissingMeaning,
    MustSupport,
    Unsupported,
    PrimitiveTypeObligation,
    OrderMeaning,
    DefaultValue,
}

internal sealed record ProfileRestriction(
    ProfileRestrictionKind Kind, string Name, JsonElement Value, ProfileFactOrigin Origin);

internal sealed record ProfileInvariant(
    string? Key, string? Severity, string? Expression, string? XPath, string? Human,
    string? Source, ProfileFactOrigin Origin, JsonElement Value);

internal sealed record ProfileTypeAlternative(
    string Code, ImmutableArray<string> Profiles, ImmutableArray<string> TargetProfiles, int Token, string? FhirType = null);

internal sealed record EffectiveProfileElement(
    string RelativeId, string ElementId, string Path, int? Minimum, int? Maximum,
    ImmutableArray<ProfileTypeAlternative> Types, ImmutableArray<ProfileRestriction> Restrictions,
    ImmutableArray<ProfileFactOrigin> Origins, bool MinimumKnown, bool MaximumKnown)
{
    internal bool Prohibited => Maximum == 0;
    internal ImmutableArray<string> Diagnostics { get; init; } = [];
    internal IEnumerable<ProfileInvariant> Invariants => Restrictions
        .Where(fact => fact.Kind == ProfileRestrictionKind.Invariant)
        .Select(fact => new ProfileInvariant(
            ProfileUseFacts.Text(fact.Value, "key"), ProfileUseFacts.Text(fact.Value, "severity"),
            ProfileUseFacts.Text(fact.Value, "expression"), ProfileUseFacts.InvariantXPath(fact.Value),
            ProfileUseFacts.Text(fact.Value, "human"), ProfileUseFacts.Text(fact.Value, "source"), fact.Origin, fact.Value));
}

internal sealed record EffectiveProfileBranch(
    ProfileUseKey Key, DbStructureDefinition Definition, DbElement Root,
    ImmutableDictionary<string, EffectiveProfileElement> Elements, ImmutableArray<string> Diagnostics,
    EffectiveProfileBranch? TargetProfileFacts = null);

internal sealed record EffectiveProfileUse(
    DbStructureDefinition Owner, DbElement Element, ImmutableArray<EffectiveProfileBranch> Branches,
    ImmutableArray<string> Diagnostics)
{
    // A prohibition is branch-local. The occurrence permits the union, not the intersection.
    internal bool Permits(string relativeId) => Diagnostics.Length == 0 && Branches.Any(branch => branch.Diagnostics.Length == 0 &&
        branch.Elements.TryGetValue(relativeId, out EffectiveProfileElement? element) && !element.Prohibited &&
        !branch.Elements.Any(pair => pair.Value.Prohibited && relativeId.StartsWith(pair.Key + ".", StringComparison.Ordinal)));
}

internal sealed record ProfileQualification(
    ProfileProofKind Kind, ImmutableArray<UnsafeCodeIdentity> Identities, ImmutableArray<string> Diagnostics);

internal enum ProfileProofKind
{
    NativeReuse,
    OwnerProjection,
    Unrepresentable,
    Unsafe,
    ComparisonOnly,
    Unsupported,
    MissingFacts,
    ConflictingFacts,
}

internal enum ProfileReasonCategory
{
    UnclassifiedRestriction,
    InvalidFacts,
    DatatypeContext,
    MissingTargetField,
    TypeAlternatives,
    Cardinality,
    Binding,
    InvariantTransformation,
    RestrictionTransformation,
    AdditionalTargetObligation,
    OwnerProjectionRequired,
    TargetVocabulary,
}

internal sealed record ProfileDecisionReason(
    ProfileReasonCategory Category, string RelativeId, ProfileFactOrigin Origin,
    ProfileRestriction? Restriction, string Detail);

internal sealed record ProfileElementProjection(
    string SourceElementId, string TargetElementId, string TargetPath,
    int? Minimum, int? Maximum, ImmutableArray<ProfileRestriction> Restrictions);

internal sealed record ProfileRepresentationProof(
    ProfileProofKind Kind, ProfileUseKey Input, ProfileFactOrigin SourceRoot, ProfileFactOrigin? TargetRoot,
    string? TargetCanonical, ImmutableArray<ProfileElementProjection> Projection, ImmutableArray<string> Diagnostics)
{
    internal bool PreservesRestrictions => Kind is ProfileProofKind.NativeReuse or ProfileProofKind.OwnerProjection;
    internal bool IsAcceptedDecision => InputOwnerRoot is not null && NativeInput is not null && NativeOwnerRoot is not null &&
        (PreservesRestrictions ||
        Kind == ProfileProofKind.Unrepresentable && Reasons.Length > 0 &&
        Reasons.All(reason => reason.Category is not (ProfileReasonCategory.InvalidFacts or ProfileReasonCategory.UnclassifiedRestriction)) &&
        RequiredFacts.ContainsKey("") && NativeFacts.ContainsKey("") && TargetRoot is not null &&
        TargetCanonical is null && Projection.IsEmpty && ProjectionOwner is null);
    internal int TargetPackageKey { get; init; }
    internal ProfileUseKey? NativeInput { get; init; }
    internal ProfileFactOrigin? InputOwnerRoot { get; init; }
    internal ProfileFactOrigin? NativeOwnerRoot { get; init; }
    internal bool OccurrenceCardinalityCarried { get; init; }
    internal ProfileFactOrigin? ProjectionOwner { get; init; }
    internal ImmutableArray<ProfileDecisionReason> Reasons { get; init; } = [];
    internal ImmutableArray<ProfileRepresentationProof> ChildProofs { get; init; } = [];
    internal ImmutableDictionary<string, EffectiveProfileElement> RequiredFacts { get; init; } = ImmutableDictionary<string, EffectiveProfileElement>.Empty;
    internal ImmutableDictionary<string, EffectiveProfileElement> NativeFacts { get; init; } = ImmutableDictionary<string, EffectiveProfileElement>.Empty;
}

internal sealed record PersistedProfileDecision(
    int FormatVersion, ProfileProofKind Kind, ProfileUseKey Input, int TargetPackageKey,
    ProfileFactOrigin SourceRoot, ProfileFactOrigin? InputOwnerRoot, ProfileUseKey? NativeInput,
    ProfileFactOrigin? NativeOwnerRoot, ProfileFactOrigin? TargetRoot, string? TargetCanonical, bool OccurrenceCardinalityCarried,
    ImmutableArray<ProfileDecisionReason> Reasons, ImmutableDictionary<string, EffectiveProfileElement> RequiredFacts,
    ImmutableDictionary<string, EffectiveProfileElement> NativeFacts, ImmutableArray<string> Diagnostics,
    ImmutableArray<PersistedProfileDecision> Children)
{
    internal static PersistedProfileDecision FromProof(ProfileRepresentationProof proof) => new(
        1, proof.Kind, proof.Input, proof.TargetPackageKey, proof.SourceRoot, proof.InputOwnerRoot,
        proof.NativeInput, proof.NativeOwnerRoot, proof.TargetRoot, proof.TargetCanonical, proof.OccurrenceCardinalityCarried,
        proof.Reasons, proof.RequiredFacts, proof.NativeFacts, proof.Diagnostics,
        [.. proof.ChildProofs.Select(FromProof)]);
}

internal sealed record ProfileDecisionDocument(
    int ProfileDecisionFormat, string? ExistingProhibitionReason, PersistedProfileDecision[] Decisions);

/// <summary>Pair-local effective occurrence facts and bounded representation decisions.</summary>
internal sealed class ProfileUseFacts(XVerStructureCatalog catalog, int sourcePackageKey, int targetPackageKey,
    IReadOnlyList<DbElementComparison>? comparisons = null) : IDisposable
{
    internal const string XPathExtensionUrl = CommonDefinitions.ExtUrlEdConstraintXPath;

    private readonly Dictionary<int, (DbElement Root, ImmutableDictionary<string, EffectiveProfileElement> Elements,
        ImmutableArray<string> Diagnostics)> _definitions = [];
    private readonly Dictionary<(int, int, int), EffectiveProfileUse> _uses = [];
    private readonly HashSet<int> _active = [];
    private readonly ILookup<int, DbElementComparison> _correspondenceComparisons = (comparisons ?? [])
        .Where(comparison => comparison.SourceFhirPackageKey == sourcePackageKey && comparison.TargetFhirPackageKey == targetPackageKey)
        .ToLookup(comparison => comparison.SourceElementKey);

    internal static string SerializeDecisions(IEnumerable<ProfileRepresentationProof> decisions, string? existingReason = null) =>
        JsonSerializer.Serialize(new ProfileDecisionDocument(1, existingReason,
            [.. decisions.Select(PersistedProfileDecision.FromProof)]));

    internal static IReadOnlyList<PersistedProfileDecision> ReadDecisions(string json)
    {
        try
        {
            ProfileDecisionDocument document = JsonSerializer.Deserialize<ProfileDecisionDocument>(json)
                ?? throw new InvalidOperationException("Missing persisted profile decisions.");
            if (document.ProfileDecisionFormat != 1 || document.Decisions is not { Length: > 0 } ||
                document.Decisions.Any(decision => decision is null || decision.Kind != ProfileProofKind.Unrepresentable || !Valid(decision)))
            {
                throw new InvalidOperationException("Incomplete or incompatible persisted negative profile decisions.");
            }
            return document.Decisions;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Malformed persisted negative profile decisions.", exception);
        }

        static bool Valid(PersistedProfileDecision decision) =>
            decision.FormatVersion == 1 && decision.Input is not null && decision.SourceRoot is not null &&
            decision.Input.PackageKey == decision.SourceRoot.PackageKey && decision.TargetPackageKey > 0 &&
            decision.InputOwnerRoot is not null && decision.InputOwnerRoot.PackageKey == decision.Input.PackageKey &&
            decision.InputOwnerRoot.OwnerKey == decision.Input.OwnerKey &&
            decision.NativeInput is not null && decision.NativeInput.PackageKey == decision.TargetPackageKey &&
            decision.NativeOwnerRoot is not null && decision.NativeOwnerRoot.PackageKey == decision.TargetPackageKey &&
            decision.NativeOwnerRoot.OwnerKey == decision.NativeInput.OwnerKey &&
            decision.TargetRoot is not null && decision.TargetRoot.PackageKey == decision.TargetPackageKey &&
            decision.RequiredFacts is not null && decision.RequiredFacts.ContainsKey("") &&
            decision.NativeFacts is not null && decision.NativeFacts.ContainsKey("") &&
            !decision.Children.IsDefault && decision.Children.All(child => child is not null && Valid(child)) &&
            !decision.Diagnostics.IsDefault && !decision.Reasons.IsDefault &&
            decision.Kind switch
            {
                ProfileProofKind.NativeReuse => !string.IsNullOrEmpty(decision.TargetCanonical) && decision.Reasons.IsEmpty,
                ProfileProofKind.Unrepresentable => decision.TargetCanonical is null && !decision.Reasons.IsEmpty &&
                    decision.Reasons.All(reason => reason.Origin is not null &&
                        reason.Category is not (ProfileReasonCategory.InvalidFacts or ProfileReasonCategory.UnclassifiedRestriction)),
                _ => false,
            };
    }

    internal EffectiveProfileUse AtUse(DbElement element, DbElementType row)
    {
        DbStructureDefinition owner = catalog.GetOwner(element);
        EffectiveProfileElement local = Definition(owner).Elements[element.Id[catalog.ResolveRoot(owner).Id.Length..]];
        ProfileTypeAlternative[] alternatives = [.. local.Types.Where(type => type.Code == row.TypeName &&
            (row.TypeProfile is null ? type.Profiles.IsEmpty : type.Profiles.Contains(row.TypeProfile)) &&
            (row.TargetProfile is null ? type.TargetProfiles.IsEmpty : type.TargetProfiles.Contains(row.TargetProfile)))];
        if (alternatives.Length != 1)
        {
            throw new InvalidOperationException(Context(owner, element.Id, $"missing/ambiguous effective type-use for row {row.Key}"));
        }
        return AtUse(owner, element, alternatives[0].Token);
    }

    internal bool Permits(DbElement element)
    {
        DbStructureDefinition owner = catalog.GetOwner(element);
        string id = element.Id[catalog.ResolveRoot(owner).Id.Length..];
        return !Definition(owner).Elements.Any(pair => pair.Value.Prohibited &&
            (id == pair.Key || id.StartsWith(pair.Key + ".", StringComparison.Ordinal)));
    }

    internal bool PermitsChild(DbElement occurrence, DbElement child, int datatypeKey)
    {
        DbStructureDefinition owner = catalog.GetOwner(occurrence);
        string id = occurrence.Id[catalog.ResolveRoot(owner).Id.Length..];
        string childId = child.Id[catalog.ResolveRoot(catalog.GetOwner(child)).Id.Length..];
        EffectiveProfileElement local = Definition(owner).Elements[id];
        EffectiveProfileUse[] uses = [.. local.Types.Select(type => AtUse(owner, occurrence, type.Token))];
        if (uses.Any(use => !use.Diagnostics.IsEmpty))
        {
            throw new InvalidOperationException(Context(owner, occurrence.Id, string.Join("; ", uses.SelectMany(use => use.Diagnostics))));
        }
        return uses.SelectMany(use => use.Branches).Where(branch => branch.Definition.Key == datatypeKey).Any(branch =>
            branch.Diagnostics.IsEmpty && !branch.Elements.Any(pair => pair.Value.Prohibited &&
                (childId == pair.Key || childId.StartsWith(pair.Key + ".", StringComparison.Ordinal))));
    }

    // Classification deliberately follows declared profile obligations, not the unrelated native FHIR graph.
    // The target occurrence, when present, is part of the proof (not merely its base datatype).
    internal IReadOnlyList<ProfileRepresentationProof> ClassifyRequiredUses()
    {
        List<ProfileRepresentationProof> decisions = [];
        IReadOnlyList<DbStructureDefinition> owners = catalog.GetProcessedStructures(sourcePackageKey);
        HashSet<int> usedDefinitions = [.. owners.SelectMany(catalog.GetOwnedElements)
            .Where(element => element.ResourceFieldOrder != 0)
            .SelectMany(catalog.GetTypeUses)
            .Select(type => type.TypeProfileStructureKey ?? type.TypeStructureKey).OfType<int>()];
        foreach (DbStructureDefinition owner in owners)
        {
            DbElement root = catalog.ResolveRoot(owner);
            if (catalog.IsQuantityFamily(owner) &&
                (owner.ArtifactClass == Common.Models.FhirArtifactClassEnum.Profile || usedDefinitions.Contains(owner.Key)))
            {
                Decide(ForDefinition(owner), null);
            }
            foreach (DbElement element in catalog.GetOwnedElements(owner).Where(element => element.Key != root.Key))
            {
                EffectiveProfileElement local = Definition(owner).Elements[element.Id[root.Id.Length..]];
                if (Definition(owner).Elements.Any(pair => pair.Value.Prohibited &&
                    (local.RelativeId == pair.Key || local.RelativeId.StartsWith(pair.Key + ".", StringComparison.Ordinal))))
                {
                    continue;
                }
                foreach (ProfileTypeAlternative type in local.Types.Where(Required))
                {
                    EffectiveProfileUse use = AtUse(owner, element, type.Token);
                    if (!use.Diagnostics.IsEmpty || use.Branches.IsEmpty)
                    {
                        throw new InvalidOperationException(Context(owner, element.Id, string.Join("; ", use.Diagnostics)));
                    }
                    foreach (EffectiveProfileBranch branch in use.Branches)
                    {
                        Decide(branch, element);
                    }
                }
            }
        }
        return decisions;

        bool Required(ProfileTypeAlternative type)
        {
            bool constrainedTarget = false;
            foreach (string canonical in type.TargetProfiles)
            {
                DbStructureDefinition referenced = catalog.ResolveDefinition(sourcePackageKey, canonical)!;
                catalog.ResolveRoot(referenced);
                catalog.ReadPayload(referenced);
                constrainedTarget |= referenced.ArtifactClass == Common.Models.FhirArtifactClassEnum.Profile;
            }
            if (!type.Profiles.IsEmpty || constrainedTarget)
            {
                return true;
            }
            DbStructureDefinition? native = catalog.ResolveNativeType(sourcePackageKey, type.Code, required: type.Code == "Quantity");
            return native is not null && catalog.IsQuantityFamily(native);
        }

        void Decide(EffectiveProfileBranch branch, DbElement? occurrence)
        {
            List<EffectiveProfileBranch> targets = [];
            bool cardinalityCarried = false;
            if (occurrence is not null)
            {
                DbStructureDefinition useOwner = catalog.GetOwner(occurrence);
                List<DbElement> targetElements = [];
                foreach (DbElementComparison comparison in comparisons ?? [])
                {
                    if (comparison.SourceElementKey == occurrence.Key && !comparison.NotMapped &&
                        comparison.TargetStructureKey is int ownerKey && comparison.TargetElementKey is int elementKey)
                    {
                        if (comparison.SourceFhirPackageKey != sourcePackageKey || comparison.TargetFhirPackageKey != targetPackageKey ||
                            comparison.SourceStructureKey != useOwner.Key || comparison.SourceElementId != occurrence.Id)
                        {
                            throw new InvalidOperationException(Context(useOwner, occurrence.Id, "mapped occurrence contradicts its exact source owner/element"));
                        }
                        if (comparison.ElementMappingKey is not null)
                        {
                            RequireMappingAuthority(comparison);
                        }
                        DbStructureDefinition targetOwner = catalog.GetStructure(ownerKey);
                        if (targetOwner.FhirPackageKey != targetPackageKey || !catalog.IsProcessed(targetOwner))
                        {
                            throw new InvalidOperationException(Context(useOwner, occurrence.Id, "mapped target owner is passive"));
                        }
                        targetElements.Add(catalog.ResolveOwnedElement(targetOwner, comparison.TargetElementId ??
                            throw new InvalidOperationException(Context(useOwner, occurrence.Id, "mapped target element Id is missing")), elementKey)!);
                    }
                }
                if (targetElements.Count == 0)
                {
                    DbStructureDefinition? targetOwner = catalog.ResolveDefinition(targetPackageKey, useOwner.UnversionedUrl, required: false);
                    if (targetOwner is not null && catalog.IsProcessed(targetOwner))
                    {
                        string relative = occurrence.Id[catalog.ResolveRoot(useOwner).Id.Length..];
                        DbElement? targetElement = catalog.ResolveOwnedElement(targetOwner, catalog.ResolveRoot(targetOwner).Id + relative, required: false);
                        if (targetElement is not null)
                        {
                            targetElements.Add(targetElement);
                        }
                    }
                }
                foreach (DbElement targetElement in targetElements.DistinctBy(element => element.Key))
                {
                    DbStructureDefinition targetOwner = catalog.GetOwner(targetElement);
                    EffectiveProfileElement targetLocal = Definition(targetOwner).Elements[targetElement.Id[catalog.ResolveRoot(targetOwner).Id.Length..]];
                    foreach (ProfileTypeAlternative type in targetLocal.Types)
                    {
                        EffectiveProfileUse targetUse = AtUse(targetOwner, targetElement, type.Token);
                        if (!targetUse.Diagnostics.IsEmpty || targetUse.Branches.IsEmpty)
                        {
                            throw new InvalidOperationException(Context(targetOwner, targetElement.Id, string.Join("; ", targetUse.Diagnostics)));
                        }
                        targets.AddRange(targetUse.Branches);
                    }
                }
            }
            if (targets.Count == 0)
            {
                DbStructureDefinition? target = catalog.ResolveDefinition(targetPackageKey, branch.Definition.UnversionedUrl, required: false)
                    ?? catalog.ResolveNativeType(targetPackageKey, catalog.GetDeclaredType(branch.Definition), required: false);
                if (target is null && catalog.IsQuantityFamily(branch.Definition))
                {
                    // Definition movement/absence is not an alias. An actual ancestor is only a
                    // representation candidate; Prove still has to retain every required restriction.
                    target = catalog.GetAncestors(branch.Definition)
                        .Select(ancestor => catalog.ResolveDefinition(targetPackageKey, ancestor.UnversionedUrl, required: false))
                        .FirstOrDefault(candidate => candidate is not null);
                }
                target ??= catalog.ResolveNativeType(targetPackageKey, catalog.GetDeclaredType(branch.Definition),
                    context: branch.Root.Id + " required representation")!;
                targets.Add(ForDefinition(target));
                cardinalityCarried = occurrence is not null;
            }
            ProfileRepresentationProof[] proofs = [.. targets.Select(target => Prove(branch, target.Definition, nativeContext: target,
                occurrenceCardinalityCarried: cardinalityCarried))];
            // Invalid alternatives are not evidence for a negative classification.
            if (proofs.Any(proof => !proof.IsAcceptedDecision))
            {
                throw new InvalidOperationException(Context(branch.Definition, branch.Root.Id,
                    string.Join("; ", proofs.Where(proof => !proof.IsAcceptedDecision).SelectMany(proof => proof.Diagnostics))));
            }
            ProfileRepresentationProof? positive = proofs.FirstOrDefault(proof => proof.PreservesRestrictions);
            if (positive is not null)
            {
                decisions.Add(positive);
            }
            else
            {
                decisions.AddRange(proofs);
            }
        }
    }

    internal static IReadOnlyList<ProfileRepresentationProof> ClassifyPair(IDbConnection db, FhirPackageComparisonPair pair)
    {
        XVerStructureCatalog catalog = new(db, XVer.XVerProcessor._exclusionSet);
        try
        {
            return ClassifyPair(db, pair, catalog);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    private static IReadOnlyList<ProfileRepresentationProof> ClassifyPair(
        IDbConnection db, FhirPackageComparisonPair pair, XVerStructureCatalog catalog)
    {
        ImmutableArray<ProfileRepresentationProof> decisions = ClassifyDirection(catalog,
            pair.SourcePackageKey, pair.TargetPackageKey,
            DbElementComparison.SelectList(db, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey));
        ClassifyDirection(catalog, pair.TargetPackageKey, pair.SourcePackageKey,
            DbElementComparison.SelectList(db, SourceFhirPackageKey: pair.TargetPackageKey, TargetFhirPackageKey: pair.SourcePackageKey));
        return decisions;
    }

    internal static ImmutableArray<ProfileRepresentationProof> ClassifyDirection(XVerStructureCatalog catalog,
        int sourcePackageKey, int targetPackageKey, IReadOnlyList<DbElementComparison> comparisons)
    {
        using ProfileUseFacts facts = new(catalog, sourcePackageKey, targetPackageKey, comparisons);
        return [.. facts.ClassifyRequiredUses()];
    }

    internal ProfileRepresentationProof ProveInOwner(ProfileRepresentationProof decision,
        DbStructureDefinition emittedOwner, IReadOnlySet<int> emittedOwnerKeys)
    {
        DbStructureDefinition sourceOwner = catalog.GetStructure(decision.Input.OwnerKey);
        DbElement sourceElement = catalog.GetOwnedElements(sourceOwner).Single(element => element.Key == decision.Input.ElementKey);
        EffectiveProfileBranch source = AtUse(sourceOwner, sourceElement, decision.Input.TypeUseToken).Branches
            .Single(branch => branch.Key == decision.Input);
        if (decision.NativeInput is not ProfileUseKey native || native.OwnerKey != emittedOwner.Key ||
            native.TypeUseToken < 0 || decision.OccurrenceCardinalityCarried)
        {
            return decision;
        }
        DbElement targetElement = catalog.GetOwnedElements(emittedOwner).Single(element => element.Key == native.ElementKey);
        EffectiveProfileBranch target = AtUse(emittedOwner, targetElement, native.TypeUseToken).Branches
            .Single(branch => branch.Key == native);
        return Prove(source, target.Definition, emittedOwner, targetElement, emittedOwnerKeys, target);
    }

    internal static void PersistNegativeDecisions(IDbConnection db, FhirPackageComparisonPair pair,
        IReadOnlyList<ProfileRepresentationProof> decisions, XVerPreparedOperation? operation = null)
    {
        operation?.Audit();
        List<DbElementOutcome> outcomes = operation is null ? DbElementOutcome.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            : [.. operation.Evidence.Rows<DbElementOutcome>(DbElementOutcome.DefaultTableName)
                .Where(row => row.SourceFhirPackageKey == pair.SourcePackageKey && row.TargetFhirPackageKey == pair.TargetPackageKey)];
        List<DbElementOutcome> updates = [];
        List<DbElementOutcome> inserts = [];
        foreach (IGrouping<int, ProfileRepresentationProof> group in decisions.Where(proof => !proof.PreservesRestrictions)
            .GroupBy(proof => proof.Input.ElementKey))
        {
            DbElementOutcome[] matches = [.. outcomes.Where(outcome => outcome.SourceElementKey == group.Key)];
            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"Ambiguous negative profile outcome for element {group.Key}, pair {pair.SourcePackageKey}->{pair.TargetPackageKey}.");
            }
            DbElementOutcome outcome;
            if (matches.Length == 0)
            {
                DbStructureDefinition owner;
                DbElement element;
                if (operation is null)
                {
                    element = DbElement.SelectSingle(db, Key: group.Key)!;
                    owner = DbStructureDefinition.SelectSingle(db, Key: element.StructureKey)!;
                }
                else
                {
                    owner = operation.Catalog.GetStructure(group.First().Input.OwnerKey);
                    element = operation.Catalog.GetOwnedElements(owner).Single(row => row.Key == group.Key);
                }
                outcome = NegativeOutcome(pair, owner, element);
                inserts.Add(outcome);
            }
            else
            {
                outcome = matches[0];
                updates.Add(outcome);
            }
            string? existingReason = outcome.ExtensionProhibitionReason;
            if (IsDecisionDocument(existingReason))
            {
                ReadDecisions(existingReason!);
                existingReason = JsonSerializer.Deserialize<ProfileDecisionDocument>(existingReason!)!.ExistingProhibitionReason;
            }
            outcome.ExtensionDefinitionIsProhibited = true;
            outcome.ExtensionProhibitionReason = SerializeDecisions(group, existingReason);
            outcome.IsEquivalent = false;
            outcome.IsIdentical = false;
            outcome.FullyMapsAcrossAllTargets = false;
            outcome.FullyMapsToThisTarget = false;
            outcome.GenUrl = null;
            outcome.ExtensionSubstitutionUrl = null;
            outcome.ContentReferenceExtensionUrl = null;
        }
        HashSet<int> negativeOutcomes = [.. inserts.Concat(updates).Select(outcome => outcome.Key)];
        List<DbElementOutcomeTarget> targets = [.. (operation is null ? DbElementOutcomeTarget.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            : operation.Evidence.Rows<DbElementOutcomeTarget>(DbElementOutcomeTarget.DefaultTableName))
            .Where(target => target.SourceFhirPackageKey == pair.SourcePackageKey && target.TargetFhirPackageKey == pair.TargetPackageKey &&
                negativeOutcomes.Contains(target.ElementOutcomeKey))];
        foreach (DbElementOutcomeTarget target in targets)
        {
            target.FullyMapsToThisTarget = false;
            target.ContextRootExtensionUrl = null;
            target.ContextParentExtensionUrl = null;
            target.CardinalityContextRootExtensionUrl = null;
            target.CardinalityContextParentExtensionUrl = null;
        }
        HashSet<int> negativeOwners = [.. decisions.Where(proof => !proof.PreservesRestrictions).Select(proof => proof.Input.OwnerKey)];
        List<DbStructureOutcome> structures = [.. (operation is null ? DbStructureOutcome.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)
            : operation.Evidence.Rows<DbStructureOutcome>(DbStructureOutcome.DefaultTableName))
            .Where(outcome => outcome.SourceFhirPackageKey == pair.SourcePackageKey && outcome.TargetFhirPackageKey == pair.TargetPackageKey &&
                negativeOwners.Contains(outcome.SourceStructureKey))];
        foreach (DbStructureOutcome outcome in structures)
        {
            outcome.IsEquivalent = false;
            outcome.IsIdentical = false;
            outcome.FullyMapsAcrossAllTargets = false;
            outcome.FullyMapsToThisTarget = false;
            outcome.TargetCanonicalVersioned = null;
            outcome.TargetCanonicalUnversioned = null;
        }
        void Write()
        {
            inserts.Insert(db, insertPrimaryKey: true);
            updates.Update(db);
            targets.Update(db);
            structures.Update(db);
        }
        if (operation is null)
        {
            Write();
        }
        else
        {
            operation.Evidence.ApplyOutcomeWrites(
                new(XVerOperandFamily.StructureOutcomes, XVerOutputTransitionKind.PostProcess, pair.SourcePackageKey, pair.TargetPackageKey),
                inserts, [.. updates, .. targets, .. structures], Write);
        }
    }

    internal static void RequirePersistedDecisions(IDbConnection db, FhirPackageComparisonPair pair)
    {
        XVerStructureCatalog catalog = new(db, XVer.XVerProcessor._exclusionSet);
        try
        {
            IReadOnlyList<ProfileRepresentationProof> required = ClassifyPair(db, pair, catalog);
            RequirePersistedDecisions(db, pair, required, catalog);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    internal static void RequirePersistedDecisions(IDbConnection db, FhirPackageComparisonPair pair,
        IReadOnlyList<ProfileRepresentationProof> required, XVerStructureCatalog catalog)
    {
        List<DbStructureOutcome> structureOutcomes = DbStructureOutcome.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
        List<DbElementOutcome> elementOutcomes = DbElementOutcome.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
        List<DbElementOutcomeTarget> elementTargets = DbElementOutcomeTarget.SelectList(db,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey);
        if (required.Any(proof => !proof.IsAcceptedDecision || proof.Input.PackageKey != pair.SourcePackageKey ||
            proof.TargetPackageKey != pair.TargetPackageKey ||
            catalog.GetStructure(proof.Input.OwnerKey).FhirPackageKey != pair.SourcePackageKey))
        {
            throw new InvalidOperationException("Expected profile decisions contradict the catalog or exact directed pair.");
        }
        foreach (DbStructureOutcome outcome in structureOutcomes)
        {
            DbStructureDefinition source = catalog.GetStructure(outcome.SourceStructureKey);
            DbStructureDefinition? target = outcome.TargetStructureKey is int targetKey ? catalog.GetStructure(targetKey) : null;
            if (source.FhirPackageKey != pair.SourcePackageKey || !catalog.IsProcessed(source) ||
                target is not null && (target.FhirPackageKey != pair.TargetPackageKey || !catalog.IsProcessed(target)))
            {
                throw new InvalidOperationException($"Passive or misowned standalone outcome {outcome.Key} cannot certify/export structure representations.");
            }
        }
        Dictionary<(ProfileUseKey Input, ProfileUseKey? NativeInput), PersistedProfileDecision> expected =
            required.Where(proof => !proof.PreservesRestrictions)
                .ToDictionary(proof => (proof.Input, proof.NativeInput), PersistedProfileDecision.FromProof);
        Dictionary<(ProfileUseKey Input, ProfileUseKey? NativeInput), PersistedProfileDecision> actual = [];
        HashSet<int> negativeOutcomes = [];
        foreach (DbElementOutcome outcome in elementOutcomes
            .Where(outcome => IsDecisionDocument(outcome.ExtensionProhibitionReason)))
        {
            if (!outcome.ExtensionDefinitionIsProhibited || outcome.IsEquivalent || outcome.IsIdentical ||
                outcome.FullyMapsToThisTarget || outcome.FullyMapsAcrossAllTargets || outcome.GenUrl is not null ||
                outcome.ExtensionSubstitutionUrl is not null || outcome.ContentReferenceExtensionUrl is not null)
            {
                throw new InvalidOperationException("Negative profile outcome advertises a usable/equivalent representation.");
            }
            negativeOutcomes.Add(outcome.Key);
            foreach (PersistedProfileDecision decision in ReadDecisions(outcome.ExtensionProhibitionReason!))
            {
                if (decision.Input.OwnerKey != outcome.SourceStructureKey || decision.Input.ElementKey != outcome.SourceElementKey ||
                    decision.Input.PackageKey != pair.SourcePackageKey || decision.TargetPackageKey != pair.TargetPackageKey ||
                    !actual.TryAdd((decision.Input, decision.NativeInput), decision))
                {
                    throw new InvalidOperationException("Persisted negative profile decision contradicts its exact pair/owner/occurrence.");
                }
            }
        }
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out PersistedProfileDecision? value) ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pair.Value), JsonSerializer.SerializeToElement(value))))
        {
            throw new InvalidOperationException("Persisted negative profile decisions are missing, stale, or incomplete. Reload maps, compare, generate outcomes, then export.");
        }
        HashSet<int> negativeOwners = [.. expected.Keys.Select(key => key.Input.OwnerKey)];
        if (structureOutcomes.Any(outcome => negativeOwners.Contains(outcome.SourceStructureKey) &&
                (outcome.IsEquivalent || outcome.IsIdentical || outcome.FullyMapsAcrossAllTargets || outcome.FullyMapsToThisTarget ||
                    outcome.TargetCanonicalVersioned is not null || outcome.TargetCanonicalUnversioned is not null)) ||
            elementTargets
                .Any(target => negativeOutcomes.Contains(target.ElementOutcomeKey) &&
                    (target.FullyMapsToThisTarget || target.ContextRootExtensionUrl is not null || target.ContextParentExtensionUrl is not null ||
                        target.CardinalityContextRootExtensionUrl is not null || target.CardinalityContextParentExtensionUrl is not null)))
        {
            throw new InvalidOperationException("Persisted negative profile decisions advertise a usable target.");
        }
    }

    private static bool IsDecisionDocument(string? reason) =>
        reason?.TrimStart().StartsWith("{\"ProfileDecisionFormat\":", StringComparison.Ordinal) == true;

    private static DbElementOutcome NegativeOutcome(FhirPackageComparisonPair pair, DbStructureDefinition owner, DbElement element) => new()
    {
        Key = DbElementOutcome.GetIndex(),
        SourceFhirPackageKey = pair.SourcePackageKey, TargetFhirPackageKey = pair.TargetPackageKey,
        SourceFhirSequence = pair.SourceFhirSequence, TargetFhirSequence = pair.TargetFhirSequence,
        SourceStructureKey = owner.Key, SourceElementKey = element.Key, SourceId = element.Id, SourceName = element.Name,
        SourceCanonicalVersioned = owner.VersionedUrl, SourceCanonicalUnversioned = owner.UnversionedUrl, SourceVersion = owner.Version,
        RequiresXVerDefinition = false, RequiresExtensionDefinition = false, RequiresSliceDefinition = false,
        RequiresCardinalityDefinition = false, RequiresCardinalitySlice = false,
        GenLongId = null, GenShortId = null, GenName = null, GenUrl = null, GenFileName = null, GenSliceName = null,
        GenArtifactShort = null, GenArtifactDescription = null, GenArtifactComment = null, GenMappingComment = null,
        SourceResourceOrder = element.ResourceFieldOrder, SourceComponentOrder = element.ComponentFieldOrder,
        SourceMinCardinality = element.MinCardinality, SourceMaxCardinalityString = element.MaxCardinalityString,
        SourceChildElementCount = element.ChildElementCount, SourceUsedAsContentReference = false,
        SourceAncestorUsedAsContentReferenceId = null, SourceAncestorContentReferenceOutcomeKey = null,
        AncestorElementOutcomeKey = null, ParentElementOutcomeKey = null, ExtensionSubstitutionKey = null,
        ExtensionSubstitutionUrl = null, BasicElementBaseId = null, BasicElementId = null,
        ExtensionElementBaseId = null, ExtensionElementId = null, ContentReferenceOutcomeKey = null,
        ContentReferenceExtensionUrl = null, ContentReferenceRequiresXVerDefinition = null,
        ContentReferenceAncestorId = null, RequiresDefinitionAsContentReference = null,
        ExtensionDefinitionIsProhibited = true, ExtensionProhibitionReason = null, OutcomeTargetCount = 0,
        SourceIsModifier = element.IsModifier, DefineAsModifier = false, ModifierReason = null, SourceIsDeprecated = false,
        TotalTargetCount = 0, TotalSourceCount = 0, IsRenamed = false, IsUnmapped = true,
        IsIdentical = false, IsEquivalent = false, IsBroaderThanTarget = false, IsNarrowerThanTarget = false,
        FullyMapsToThisTarget = false, FullyMapsAcrossAllTargets = false, Comments = "Explicit unrepresentable profile use.",
    };

    internal EffectiveProfileUse AtUse(DbStructureDefinition owner, DbElement element, int typeUseToken)
    {
        if (_uses.TryGetValue((owner.Key, element.Key, typeUseToken), out EffectiveProfileUse? cached))
        {
            return cached;
        }
        catalog.ResolveOwnedElement(owner, element.Id, element.Key);
        (DbElement root, ImmutableDictionary<string, EffectiveProfileElement> elements, ImmutableArray<string> diagnostics) = Definition(owner);
        string relative = element.Id[root.Id.Length..];
        EffectiveProfileElement local = elements[relative];
        ProfileTypeAlternative? type = local.Types.SingleOrDefault(row => row.Token == typeUseToken);
        List<EffectiveProfileBranch> branches = [];
        List<string> errors = [];
        if (type is null)
        {
            errors.Add(Context(owner, element.Id, $"missing normalized type-use token {typeUseToken}"));
        }
        else
        {
            foreach (string? profile in Alternatives(type.Profiles))
            {
                foreach (string? targetProfile in Alternatives(type.TargetProfiles))
                {
                    ProfileUseKey key = new(owner.FhirPackageKey, owner.Key, element.Key, type.Token, profile, targetProfile, relative);
                    try
                    {
                        DbStructureDefinition datatype = profile is null
                            ? (targetProfile is null ? catalog.ResolveQuantityProfileRootType(element, type.Code) : null)
                                ?? catalog.ResolveNativeType(owner.FhirPackageKey, type.Code, context: element.Id)!
                            : catalog.ResolveDefinition(owner.FhirPackageKey, profile, context: element.Id)!;
                        if (profile is not null)
                        {
                            DbStructureDefinition native = catalog.ResolveNativeType(owner.FhirPackageKey, type.Code, context: element.Id)!;
                            if (datatype.Key != native.Key && !catalog.GetAncestors(datatype).Any(ancestor => ancestor.Key == native.Key))
                            {
                                throw new InvalidOperationException(Context(owner, element.Id,
                                    $"declared profile {profile} does not constrain the native datatype {type.Code}"));
                            }
                        }
                        // Validate the importer cross-product, but never use its rows as conjunctive declarations.
                        foreach (DbElementType row in catalog.GetTypeUses(element).Where(row =>
                            row.TypeName == type.Code && row.TypeProfile == profile && row.TargetProfile == targetProfile))
                        {
                            catalog.ResolveTypeUse(row);
                            if (targetProfile is not null)
                            {
                                catalog.ResolveTypeUse(row, targetProfile: true);
                            }
                        }
                        EffectiveProfileBranch? targetFacts = null;
                        if (targetProfile is not null)
                        {
                            DbStructureDefinition target = catalog.ResolveDefinition(owner.FhirPackageKey, targetProfile, context: element.Id)!;
                            targetFacts = ForDefinition(target);
                        }
                        (DbElement datatypeRoot, ImmutableDictionary<string, EffectiveProfileElement> datatypeElements,
                            ImmutableArray<string> datatypeErrors) = Definition(datatype);
                        Dictionary<string, EffectiveProfileElement> merged = datatypeElements.ToDictionary();
                        List<string> branchErrors = [.. datatypeErrors, .. local.Diagnostics];
                        merged[""] = Merge(merged[""], local with { RelativeId = "" }, branchErrors, rootType: true, occurrence: true);
                        foreach ((string id, EffectiveProfileElement child) in elements.Where(pair => pair.Key.StartsWith(relative + ".", StringComparison.Ordinal)))
                        {
                            string childId = id[relative.Length..];
                            EffectiveProfileElement atChild = child with { RelativeId = childId };
                            branchErrors.AddRange(child.Diagnostics);
                            merged[childId] = merged.TryGetValue(childId, out EffectiveProfileElement? inherited)
                                ? Merge(inherited, atChild, branchErrors) : atChild;
                        }
                        branches.Add(new(key, datatype, datatypeRoot, merged.ToImmutableDictionary(), [.. branchErrors], targetFacts));
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(Context(owner, element.Id, exception.Message));
                    }
                }
            }
        }
        EffectiveProfileUse use = new(owner, element, [.. branches], [.. errors]);
        _uses.Add((owner.Key, element.Key, typeUseToken), use);
        return use;
    }

    public void Dispose()
    {
        _uses.Clear();
        _definitions.Clear();
        _active.Clear();
    }

    internal EffectiveProfileBranch ForDefinition(DbStructureDefinition owner)
    {
        (DbElement root, ImmutableDictionary<string, EffectiveProfileElement> elements, ImmutableArray<string> diagnostics) = Definition(owner);
        return new(new(owner.FhirPackageKey, owner.Key, root.Key, -1, owner.UnversionedUrl + "|" + owner.Version, null, root.Id),
            owner, root, elements, diagnostics);
    }

    internal ProfileRepresentationProof Prove(
        EffectiveProfileBranch source, DbStructureDefinition targetDefinition,
        DbStructureDefinition? emittedOwner = null, DbElement? targetOccurrence = null,
        IReadOnlySet<int>? emittedOwnerKeys = null, EffectiveProfileBranch? nativeContext = null,
        bool occurrenceCardinalityCarried = false)
    {
        ProfileFactOrigin sourceRoot = Origin(source.Definition, source.Root.Id);
        EffectiveProfileBranch? target = null;
        List<ProfileDecisionReason> reasons = [];
        List<ProfileRepresentationProof> childProofs = [];
        ProfileRepresentationProof Result(ProfileProofKind kind, string? canonical = null,
            ImmutableArray<ProfileElementProjection> projection = default) =>
            new(kind, source.Key, sourceRoot, target is null ? null : Origin(targetDefinition, target.Root.Id),
                canonical, projection.IsDefault ? [] : projection,
                [.. reasons.Select(reason => Context(source.Definition, source.Root.Id, reason.Detail))])
            {
                TargetPackageKey = targetPackageKey, Reasons = [.. reasons],
                NativeInput = target?.Key,
                InputOwnerRoot = OwnerRoot(source.Key),
                NativeOwnerRoot = target is null ? null : OwnerRoot(target.Key),
                OccurrenceCardinalityCarried = occurrenceCardinalityCarried,
                ProjectionOwner = kind == ProfileProofKind.OwnerProjection ? Origin(emittedOwner!, targetOccurrence!.Id) : null,
                RequiredFacts = source.Elements,
                NativeFacts = target?.Elements ?? ImmutableDictionary<string, EffectiveProfileElement>.Empty,
                ChildProofs = [.. childProofs],
            };
        ProfileFactOrigin? OwnerRoot(ProfileUseKey key)
        {
            DbStructureDefinition? owner = catalog.GetDefinitions(key.PackageKey).SingleOrDefault(owner => owner.Key == key.OwnerKey);
            return owner is null ? null : Origin(owner, catalog.ResolveRoot(owner).Id);
        }
        ProfileRepresentationProof Failure(ProfileProofKind kind, string fact) =>
            Fail(kind, new(ProfileReasonCategory.InvalidFacts, "", sourceRoot, null, fact));
        ProfileRepresentationProof Fail(ProfileProofKind kind, ProfileDecisionReason reason)
        {
            reasons.Add(reason);
            return Result(kind);
        }
        void Difference(ProfileReasonCategory category, string id, EffectiveProfileElement element, string detail,
            ProfileRestriction? restriction = null) =>
            reasons.Add(new(category, id, restriction?.Origin ?? element.Origins[^1], restriction, At(element, detail)));
        if (source.Key.PackageKey != sourcePackageKey || targetDefinition.FhirPackageKey != targetPackageKey)
        {
            return Failure(ProfileProofKind.ConflictingFacts, "proof endpoints contradict the directed package pair");
        }
        try
        {
            target = nativeContext ?? ForDefinition(targetDefinition);
            if (nativeContext is null && emittedOwner is not null && targetOccurrence is not null)
            {
                EffectiveProfileUse targetUse = AtUse(emittedOwner, targetOccurrence, 0);
                EffectiveProfileBranch[] contexts = [.. targetUse.Branches.Where(branch => branch.Definition.Key == targetDefinition.Key)];
                if (targetUse.Diagnostics.Length != 0 || contexts.Length != 1)
                {
                    return Failure(ProfileProofKind.MissingFacts, "target occurrence has missing/ambiguous effective datatype facts");
                }
                target = contexts[0];
            }
            catalog.ResolveOwnedElement(source.Definition, source.Root.Id, source.Root.Key);
            catalog.ResolveOwnedElement(targetDefinition, target.Root.Id, target.Root.Key);
            foreach (EffectiveProfileBranch branch in new[] { source, target })
            {
                DbStructureDefinition? inputOwner = catalog.GetDefinitions(branch.Key.PackageKey)
                    .SingleOrDefault(owner => owner.Key == branch.Key.OwnerKey);
                if (branch.Definition.FhirPackageKey != branch.Key.PackageKey || inputOwner is null ||
                    !catalog.GetOwnedElements(inputOwner).Any(element => element.Key == branch.Key.ElementKey) ||
                    catalog.ResolveRoot(branch.Definition).Key != branch.Root.Key)
                {
                    return Failure(ProfileProofKind.ConflictingFacts, "branch input/root contradicts its exact owner graph");
                }
                catalog.ReadPayload(inputOwner);
            }
            if (target.Definition.Key != targetDefinition.Key || target.Key.PackageKey != targetPackageKey)
            {
                return Failure(ProfileProofKind.ConflictingFacts, "native context contradicts the exact target owner");
            }
            if ((emittedOwner is null) != (targetOccurrence is null))
            {
                return Failure(ProfileProofKind.MissingFacts, "projection requires both an emitted owner and its exact occurrence");
            }
            if (emittedOwner is not null && targetOccurrence is not null)
            {
                catalog.ResolveOwnedElement(emittedOwner, targetOccurrence.Id, targetOccurrence.Key);
                if (target.Key.OwnerKey != emittedOwner.Key || target.Key.ElementKey != targetOccurrence.Key)
                {
                    return Failure(ProfileProofKind.ConflictingFacts, "projection native input is not the exact emitted owner occurrence");
                }
                if (emittedOwner.FhirPackageKey != targetPackageKey || !catalog.IsProcessed(emittedOwner) ||
                    emittedOwner.ArtifactClass != Common.Models.FhirArtifactClassEnum.Resource)
                {
                    return Failure(ProfileProofKind.ConflictingFacts, "projection requires an already emitted target resource/extension owner, never a standalone datatype profile");
                }
                if (emittedOwnerKeys is null || !emittedOwnerKeys.Contains(emittedOwner.Key))
                {
                    return Failure(ProfileProofKind.Unsupported, "projection requires membership in the actual emitted owner schedule");
                }
            }
            if (source.Diagnostics.Length != 0 || target.Diagnostics.Length != 0)
            {
                return Failure(ProfileProofKind.ConflictingFacts, string.Join("; ", source.Diagnostics.Concat(target.Diagnostics)));
            }
            if (string.IsNullOrEmpty(targetDefinition.Version) || string.IsNullOrEmpty(targetDefinition.UnversionedUrl) ||
                string.IsNullOrEmpty(source.Definition.Version) || string.IsNullOrEmpty(source.Definition.UnversionedUrl))
            {
                return Failure(ProfileProofKind.MissingFacts, "source/target canonical or definition version missing");
            }
            StructureDefinition sourcePayload = catalog.ReadPayload(source.Definition);
            StructureDefinition targetPayload = catalog.ReadPayload(targetDefinition);
            if (string.IsNullOrEmpty(sourcePayload.Type) || string.IsNullOrEmpty(targetPayload.Type) ||
                sourcePayload.Kind is null || targetPayload.Kind is null)
            {
                return Failure(ProfileProofKind.MissingFacts, "source/target declared datatype or kind is missing");
            }
            if (!source.Elements.ContainsKey("") || !target.Elements.ContainsKey(""))
            {
                return Failure(ProfileProofKind.MissingFacts, "effective source/target root restriction facts are missing");
            }
            // Validate both complete graphs before considering any negative representation decision.
            Validate(source, []);
            Validate(target, []);
            if (reasons.Count != 0)
            {
                return Result(ProfileProofKind.Unsupported);
            }
            if (source.TargetProfileFacts is not null || target.TargetProfileFacts is not null)
            {
                if (source.TargetProfileFacts is null || target.TargetProfileFacts is null)
                {
                    Difference(ProfileReasonCategory.TypeAlternatives, "", source.Elements[""],
                        "no supported transformation between constrained and unconstrained targetProfile alternatives");
                }
                else if (source.TargetProfileFacts.Definition.ArtifactClass != Common.Models.FhirArtifactClassEnum.Profile &&
                    target.TargetProfileFacts.Definition.ArtifactClass != Common.Models.FhirArtifactClassEnum.Profile)
                {
                    if (source.TargetProfileFacts.Definition.UnversionedUrl != target.TargetProfileFacts.Definition.UnversionedUrl)
                    {
                        Difference(ProfileReasonCategory.TypeAlternatives, "", source.Elements[""],
                            "no supported correspondence between the declared native targetProfile domains");
                    }
                }
                else
                {
                    ProfileRepresentationProof reference = Prove(source.TargetProfileFacts, target.TargetProfileFacts.Definition,
                        nativeContext: target.TargetProfileFacts);
                    childProofs.Add(reference);
                    if (!reference.IsAcceptedDecision)
                    {
                        reasons.AddRange(reference.Reasons);
                        return Result(reference.Kind);
                    }
                    if (!reference.PreservesRestrictions)
                    {
                        reasons.AddRange(reference.Reasons);
                    }
                }
            }
            if (!SameDatatypeContext(source.Definition, targetDefinition))
            {
                return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.DatatypeContext, "", sourceRoot,
                    null, $"no supported datatype context transformation {source.Root.Path} -> {target.Root.Path}"));
            }
            bool cannotProject = false;
            Dictionary<string, string> corresponding = CorrespondingElements(source, target);
            foreach ((string id, EffectiveProfileElement element) in source.Elements)
            {
                if (!corresponding.TryGetValue(id, out string? targetId))
                {
                    Difference(ProfileReasonCategory.MissingTargetField, id, element, "complete target snapshot lacks exact child/slice " + id);
                    cannotProject = true;
                    continue;
                }
                EffectiveProfileElement counterpart = target.Elements[targetId];
                bool renamedDescendants = corresponding.Any(pair => pair.Key != pair.Value &&
                    (id == "" || pair.Key.StartsWith(id + ".", StringComparison.Ordinal)));
                if (!(occurrenceCardinalityCarried && id == "") &&
                    (element.Minimum != counterpart.Minimum || element.Maximum != counterpart.Maximum))
                {
                    Difference(ProfileReasonCategory.Cardinality, id, element,
                        $"cardinality {element.Minimum}..{element.Maximum} differs from target {counterpart.Minimum}..{counterpart.Maximum}");
                }
                if (!SameTypes(source, target, id, targetId, childProofs))
                {
                    Difference(ProfileReasonCategory.TypeAlternatives, id, element, "no supported target type/profile alternative transformation");
                    cannotProject = true;
                }
                foreach (ProfileRestriction fact in element.Restrictions)
                {
                    if (fact.Kind == ProfileRestrictionKind.Invariant && renamedDescendants)
                    {
                        Difference(ProfileReasonCategory.InvariantTransformation, id, element,
                            "invariant requires an unsupported expression/XPath rewrite across mapped children", fact);
                        cannotProject = true;
                        continue;
                    }
                    if (!counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id, targetId)) &&
                        !(element.Prohibited && counterpart.Prohibited))
                    {
                        Difference(ReasonFor(fact), id, element,
                            "unsupported projection of " + fact.Name + " " + fact.Value.GetRawText(), fact);
                        cannotProject |= !CanProject(fact);
                    }
                }
                foreach (ProfileRestriction fact in counterpart.Restrictions)
                {
                    if (!element.Restrictions.Any(other => Equivalent(other, fact, source, target, id, targetId)) &&
                        !(element.Prohibited && counterpart.Prohibited))
                    {
                        Difference(ReasonFor(fact), id, counterpart,
                            "target restriction not entailed by the source: " + fact.Name + " " + fact.Value.GetRawText(), fact);
                        cannotProject = true;
                    }
                }
            }
            foreach ((string id, EffectiveProfileElement element) in target.Elements.Where(pair => !corresponding.ContainsValue(pair.Key)))
            {
                if (element.Minimum > 0 || element.Restrictions.Length != 0)
                {
                    Difference(ProfileReasonCategory.AdditionalTargetObligation, id, element,
                        "additional target child obligations have no proven source correspondence");
                    cannotProject = true;
                }
            }
            foreach (ProfileRepresentationProof child in childProofs.Where(proof => !proof.PreservesRestrictions))
            {
                reasons.AddRange(child.Reasons);
                if (!child.IsAcceptedDecision)
                {
                    return Result(child.Kind);
                }
                cannotProject = true;
            }
            cannotProject |= reasons.Any(reason => reason.Category == ProfileReasonCategory.TypeAlternatives);
            if (cannotProject)
            {
                return Result(ProfileProofKind.Unrepresentable);
            }
            string canonical = targetDefinition.UnversionedUrl + "|" + targetDefinition.Version;
            if (reasons.Count == 0)
            {
                return Result(ProfileProofKind.NativeReuse, canonical);
            }
            if (emittedOwner is null || targetOccurrence is null)
            {
                return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.OwnerProjectionRequired, "", sourceRoot,
                    null, "native reuse differs and no already emitted owner projection was requested"));
            }
            if (catalog.GetPackage(targetPackageKey).DefinitionFhirSequence is not
                (FhirReleases.FhirSequenceCodes.STU3 or FhirReleases.FhirSequenceCodes.R4 or
                FhirReleases.FhirSequenceCodes.R4B or FhirReleases.FhirSequenceCodes.R5))
            {
                return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.TargetVocabulary, "", sourceRoot,
                    null, "constraint projection requires a proven target FHIR expression/element vocabulary; DSTU2 projection is not implemented"));
            }
            ElementDefinition occurrence = catalog.ReadElement(targetOccurrence);
            if (!source.Elements[""].MinimumKnown || !source.Elements[""].MaximumKnown ||
                (source.Elements[""].Minimum ?? 0) < (occurrence.Min ?? 0) ||
                (source.Elements[""].Maximum ?? int.MaxValue) > ParseMaximum(occurrence.Max))
            {
                return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.Cardinality, "", sourceRoot,
                    null, "projection would loosen the actual target occurrence cardinality"));
            }
            if (occurrence.Type.Count != 1 || occurrence.Type[0].Code != catalog.ReadPayload(targetDefinition).Type ||
                occurrence.Type[0].Profile.Any(profile => catalog.ResolveDefinition(targetPackageKey, profile)!.Key != targetDefinition.Key))
            {
                return Failure(ProfileProofKind.ConflictingFacts, $"unproven target occurrence datatype context at {emittedOwner.VersionedUrl}#{targetOccurrence.Id}");
            }
            List<ProfileElementProjection> projection = [];
            List<string> targetOrder = [];
            foreach (DbStructureDefinition owner in catalog.GetAncestors(targetDefinition).Reverse().Append(targetDefinition))
            {
                DbElement root = catalog.ResolveRoot(owner);
                foreach (DbElement child in catalog.GetOwnedElements(owner).OrderBy(child => child.ResourceFieldOrder))
                {
                    string relative = child.Id[root.Id.Length..];
                    if (!targetOrder.Contains(relative, StringComparer.Ordinal))
                    {
                        targetOrder.Add(relative);
                    }
                }
            }
            foreach ((string id, EffectiveProfileElement element) in source.Elements.OrderBy(pair => targetOrder.IndexOf(corresponding[pair.Key])))
            {
                string targetId = corresponding[id];
                EffectiveProfileElement counterpart = target.Elements[targetId];
                if ((element.Minimum ?? 0) < (counterpart.Minimum ?? 0) ||
                    (element.Maximum ?? int.MaxValue) > (counterpart.Maximum ?? int.MaxValue))
                {
                    Difference(ProfileReasonCategory.Cardinality, id, element, "projection would loosen the target cardinality");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                if (id.Contains(':') || element.Restrictions.Any(fact => fact.Kind == ProfileRestrictionKind.Slicing &&
                    !counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id, targetId))))
                {
                    Difference(ProfileReasonCategory.RestrictionTransformation, id, element, "projection needs an unproven slice/discriminator rewrite");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                ImmutableArray<ProfileRestriction> projected = [.. element.Restrictions
                    .Where(fact => !counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id, targetId)))
                    .Select(ProjectRestriction)];
                if (projected.Any(fact => !LegalProjection(fact, counterpart)))
                {
                    Difference(ProfileReasonCategory.RestrictionTransformation, id, element, "restriction is not legal for the target FHIR element datatype");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                projection.Add(new(element.ElementId, targetOccurrence.Id + targetId, targetOccurrence.Path + targetId,
                    element.Minimum, element.Maximum, projected));
            }
            // These differential facts apply unconditionally, not just to the selected alternative.
            // Bounded equivalence of every branch to the projected context is sufficient to preserve the union.
            ImmutableArray<EffectiveProfileBranch> sourceUnion = source.Key.TypeUseToken < 0
                ? [source] : OccurrenceBranches(source.Key);
            ImmutableArray<EffectiveProfileBranch> targetUnion = OccurrenceBranches(target.Key);
            ProfileRepresentationProof[] originalProofs = [.. sourceUnion.SelectMany(branch =>
                targetUnion.Select(native => Prove(branch, native.Definition, nativeContext: native)))];
            ProfileRepresentationProof? invalid = originalProofs.FirstOrDefault(proof => !proof.IsAcceptedDecision);
            if (invalid is not null)
            {
                reasons.AddRange(invalid.Reasons);
                return Result(invalid.Kind);
            }
            List<ProfileRepresentationProof> unionProofs = [];
            foreach (EffectiveProfileBranch native in targetUnion)
            {
                Dictionary<string, EffectiveProfileElement> projectedElements = native.Elements.ToDictionary();
                foreach (ProfileElementProjection projected in projection)
                {
                    string relative = projected.TargetElementId[targetOccurrence.Id.Length..];
                    if (!projectedElements.TryGetValue(relative, out EffectiveProfileElement? element))
                    {
                        return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.TypeAlternatives, relative,
                            Origin(native.Definition, native.Root.Id), null,
                            $"unconditional owner projection has no exact field in target alternative {native.Key}"));
                    }
                    projectedElements[relative] = element with
                    {
                        Minimum = projected.Minimum, Maximum = projected.Maximum,
                        MinimumKnown = true, MaximumKnown = true,
                        Restrictions = [.. element.Restrictions, .. projected.Restrictions],
                    };
                }
                EffectiveProfileBranch projectedContext = native with { Elements = projectedElements.ToImmutableDictionary() };
                unionProofs.AddRange(sourceUnion.Select(branch => Prove(branch, native.Definition, nativeContext: projectedContext)));
            }
            invalid = unionProofs.FirstOrDefault(proof => !proof.IsAcceptedDecision);
            if (invalid is not null)
            {
                reasons.AddRange(invalid.Reasons);
                return Result(invalid.Kind);
            }
            ProfileRepresentationProof? different = unionProofs.FirstOrDefault(proof => proof.Kind != ProfileProofKind.NativeReuse);
            if (different is not null)
            {
                return Fail(ProfileProofKind.Unrepresentable, new(ProfileReasonCategory.TypeAlternatives, "",
                    different.SourceRoot, null,
                    $"unconditional owner projection does not preserve the complete permitted alternative union: " +
                    $"source {different.Input}, target {different.NativeInput}; {string.Join("; ", different.Diagnostics)}"));
            }
            reasons.Clear();
            return Result(ProfileProofKind.OwnerProjection, canonical, [.. projection]);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(ProfileProofKind.MissingFacts, exception.Message);
        }

        void Validate(EffectiveProfileBranch branch, HashSet<int> active)
        {
            if (!active.Add(branch.Definition.Key))
            {
                Difference(ProfileReasonCategory.InvalidFacts, "", branch.Elements[""],
                    "recursive constrained child profile obligations require an unsupported finite proof");
                return;
            }
            try
            {
                if (branch.Diagnostics.Length != 0)
                {
                    Difference(ProfileReasonCategory.InvalidFacts, "", branch.Elements[""], string.Join("; ", branch.Diagnostics));
                }
                StructureDefinition payload = catalog.ReadPayload(branch.Definition);
                if (string.IsNullOrEmpty(payload.Type) || payload.Kind is null ||
                    string.IsNullOrEmpty(branch.Definition.Version) || !branch.Elements.ContainsKey(""))
                {
                    throw new InvalidOperationException(Context(branch.Definition, branch.Root.Id, "missing child profile declaration/root facts"));
                }
                foreach ((string id, EffectiveProfileElement element) in branch.Elements)
                {
                    bool prohibited = IsProhibited(branch, id);
                    if (!prohibited && (element.Types.Any(type => string.IsNullOrEmpty(type.Code)) ||
                        id != "" && element.Types.IsEmpty))
                    {
                        throw new InvalidOperationException(At(element, "missing effective datatype facts (type)"));
                    }
                    if (!element.MinimumKnown || !element.MaximumKnown || element.Minimum < 0 ||
                        element.Maximum < 0 || element.Minimum > (element.Maximum ?? int.MaxValue))
                    {
                        Difference(ProfileReasonCategory.InvalidFacts, id, element, "invalid/missing effective cardinality");
                    }
                    foreach (ProfileRestriction fact in element.Restrictions.Where(fact => !Classified(fact)))
                    {
                        Difference(ProfileReasonCategory.UnclassifiedRestriction, id, element,
                            "unclassified or malformed restriction " + fact.Name + " " + fact.Value.GetRawText(), fact);
                    }
                    if (prohibited || id == "")
                    {
                        continue;
                    }
                    foreach (ProfileTypeAlternative type in element.Types)
                    {
                        // Only declared constraints introduce obligations, not the entire native datatype graph.
                        foreach (string profile in type.Profiles)
                        {
                            DbStructureDefinition definition = catalog.ResolveDefinition(branch.Key.PackageKey, profile,
                                context: At(element, "child profile"))!;
                            Validate(AtChild(branch, id, type, definition, profile), active);
                        }
                        foreach (string profile in type.TargetProfiles)
                        {
                            DbStructureDefinition definition = catalog.ResolveDefinition(branch.Key.PackageKey, profile,
                                context: At(element, "child targetProfile"))!;
                            catalog.ResolveRoot(definition);
                            catalog.ReadPayload(definition);
                            if (definition.ArtifactClass == Common.Models.FhirArtifactClassEnum.Profile)
                            {
                                Validate(ForDefinition(definition), active);
                            }
                        }
                    }
                }
                if (branch.TargetProfileFacts?.Definition.ArtifactClass == Common.Models.FhirArtifactClassEnum.Profile)
                {
                    Validate(branch.TargetProfileFacts, active);
                }
            }
            finally
            {
                active.Remove(branch.Definition.Key);
            }
        }
    }

    private ImmutableArray<EffectiveProfileBranch> OccurrenceBranches(ProfileUseKey key)
    {
        DbStructureDefinition owner = catalog.GetStructure(key.OwnerKey);
        DbElement occurrence = catalog.GetOwnedElements(owner).Single(element => element.Key == key.ElementKey);
        string relative = occurrence.Id[catalog.ResolveRoot(owner).Id.Length..];
        if (owner.FhirPackageKey != key.PackageKey || relative != key.SliceContext)
        {
            throw new InvalidOperationException(Context(owner, occurrence.Id, "alternative union contradicts its exact owner/slice context"));
        }
        EffectiveProfileElement local = Definition(owner).Elements[relative];
        List<EffectiveProfileBranch> branches = [];
        foreach (ProfileTypeAlternative type in local.Types)
        {
            EffectiveProfileUse use = AtUse(owner, occurrence, type.Token);
            if (!use.Diagnostics.IsEmpty || use.Branches.IsEmpty)
            {
                throw new InvalidOperationException(Context(owner, occurrence.Id,
                    "incomplete alternative union: " + string.Join("; ", use.Diagnostics)));
            }
            branches.AddRange(use.Branches);
        }
        if (branches.Count == 0)
        {
            throw new InvalidOperationException(Context(owner, occurrence.Id, "missing alternative union datatype facts"));
        }
        return [.. branches];
    }

    internal ProfileQualification QualifiedIdentities(
        EffectiveProfileBranch branch, string relativeId,
        IReadOnlyDictionary<string, ImmutableArray<UnsafeCodeIdentity>> requiredBindingDomains)
    {
        if (!branch.Elements.TryGetValue(relativeId, out EffectiveProfileElement? element) || element.Prohibited)
        {
            return new(ProfileProofKind.Unsupported, [], [Context(branch.Definition, relativeId, "missing or prohibited qualification context")]);
        }
        if (branch.Diagnostics.Length != 0)
        {
            return new(ProfileProofKind.ConflictingFacts, [], branch.Diagnostics);
        }
        ImmutableArray<UnsafeCodeIdentity>? permitted = null;
        foreach (ProfileRestriction binding in element.Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Binding &&
            Text(fact.Value, "strength") == "required"))
        {
            string? canonical = Text(binding.Value, "valueSet");
            if (canonical is null || !requiredBindingDomains.TryGetValue(canonical, out ImmutableArray<UnsafeCodeIdentity> members))
            {
                return new(ProfileProofKind.Unsupported, [], [Context(branch.Definition, element.ElementId, "required binding domain membership/system/version is unknown")]);
            }
            permitted = permitted is null ? members : [.. permitted.Value.Intersect(members)];
        }
        return permitted is null
            ? new(ProfileProofKind.Unsupported, [], [Context(branch.Definition, element.ElementId, "no authoritative qualification facts; invariants are not binding domains")])
            : new(ProfileProofKind.ComparisonOnly, permitted.Value, []);
    }

    private (DbElement Root, ImmutableDictionary<string, EffectiveProfileElement> Elements, ImmutableArray<string> Diagnostics)
        Definition(DbStructureDefinition owner)
    {
        if (_definitions.TryGetValue(owner.Key, out (DbElement Root, ImmutableDictionary<string, EffectiveProfileElement> Elements,
            ImmutableArray<string> Diagnostics) cached))
        {
            return cached;
        }
        if (!_active.Add(owner.Key))
        {
            throw new InvalidOperationException(Context(owner, "", "cyclic effective definition"));
        }
        try
        {
            DbElement root = catalog.ResolveRoot(owner);
            StructureDefinition payload = catalog.ReadPayload(owner);
            Dictionary<string, EffectiveProfileElement> elements = [];
            List<string> diagnostics = [];
            if (!string.IsNullOrEmpty(payload.BaseDefinition))
            {
                DbStructureDefinition ancestor = catalog.ResolveDefinition(owner.FhirPackageKey, payload.BaseDefinition, context: owner.VersionedUrl)!;
                (DbElement ancestorRoot, ImmutableDictionary<string, EffectiveProfileElement> inherited, ImmutableArray<string> errors) = Definition(ancestor);
                diagnostics.AddRange(errors);
                foreach ((string id, EffectiveProfileElement element) in inherited)
                {
                    elements[id] = element with { ElementId = root.Id + id, Path = root.Path + id };
                }
            }
            foreach (DbElement row in catalog.GetOwnedElements(owner))
            {
                ElementDefinition element = catalog.ReadElement(row);
                string id = row.Id[root.Id.Length..];
                EffectiveProfileElement local = Read(owner, element, id, diagnostics);
                if (row.Key == root.Key && local.Types.IsEmpty && payload.Type is { Length: > 0 } declaredType)
                {
                    local = local with { Types = [new(declaredType, [], [], 0)] };
                }
                if (!elements.TryGetValue(id, out EffectiveProfileElement? basis) && id[(id.LastIndexOf('.') + 1)..].Contains(':'))
                {
                    // Only this exact unsliced context may constrain the new slice.
                    elements.TryGetValue(id[..id.LastIndexOf(':')], out basis);
                    if (basis is not null)
                    {
                        // The group's minimum counts all members, not each newly introduced slice.
                        basis = basis with { Minimum = 0 };
                    }
                }
                elements[id] = basis is null ? local : Merge(basis, local, diagnostics, rootType: id == "");
            }
            foreach (DbElement row in catalog.GetOwnedElements(owner).Where(row => catalog.ReadElement(row).ContentReference is not null))
            {
                DbElement referenced = catalog.ResolveContentReference(row);
                DbStructureDefinition referencedOwner = catalog.GetOwner(referenced);
                DbElement referencedRoot = catalog.ResolveRoot(referencedOwner);
                string referenceId = referenced.Id[referencedRoot.Id.Length..];
                string id = row.Id[root.Id.Length..];
                if (referencedOwner.Key == owner.Key && (id == referenceId || id.StartsWith(referenceId + ".", StringComparison.Ordinal)))
                {
                    // Retain the unresolved reference locally; an unrelated occurrence does not need its recursive subtree.
                    continue;
                }
                IReadOnlyDictionary<string, EffectiveProfileElement> referencedElements = referencedOwner.Key == owner.Key
                    ? elements : Definition(referencedOwner).Elements;
                EffectiveProfileElement basis = referencedElements[referenceId];
                if (referenceId == "" && basis.Types.IsEmpty && catalog.GetDeclaredType(referencedOwner) is { Length: > 0 } declaredType)
                {
                    basis = basis with { Types = [new(declaredType, [], [], 0)] };
                }
                EffectiveProfileElement localReference = elements[id] with
                {
                    Restrictions = [.. elements[id].Restrictions.Where(fact => fact.Name != "contentReference")],
                };
                elements[id] = Merge(basis with { Minimum = null, Maximum = null, MinimumKnown = false, MaximumKnown = false },
                    localReference, diagnostics, occurrence: true);
                foreach ((string childId, EffectiveProfileElement child) in referencedElements
                    .Where(pair => pair.Key.StartsWith(referenceId + ".", StringComparison.Ordinal)).ToArray())
                {
                    string relative = childId[referenceId.Length..];
                    string localId = id + relative;
                    EffectiveProfileElement inherited = child with
                    {
                        RelativeId = localId, ElementId = row.Id + relative,
                        Path = row.Path + child.Path[basis.Path.Length..],
                    };
                    elements[localId] = elements.TryGetValue(localId, out EffectiveProfileElement? local)
                        ? Merge(inherited, local, diagnostics) : inherited;
                }
            }
            foreach (EffectiveProfileElement element in elements.Values.ToArray())
            {
                if (!element.MinimumKnown || !element.MaximumKnown)
                {
                    string error = At(element, "missing effective snapshot cardinality");
                    diagnostics.Add(error);
                    elements[element.RelativeId] = element with { Diagnostics = [.. element.Diagnostics, error] };
                }
            }
            (DbElement, ImmutableDictionary<string, EffectiveProfileElement>, ImmutableArray<string>) result =
                (root, elements.ToImmutableDictionary(), diagnostics.ToImmutableArray());
            _definitions.Add(owner.Key, result);
            return result;
        }
        finally
        {
            _active.Remove(owner.Key);
        }
    }

    private EffectiveProfileElement Read(
        DbStructureDefinition owner, ElementDefinition element, string relativeId, List<string> diagnostics)
    {
        int firstDiagnostic = diagnostics.Count;
        ProfileFactOrigin origin = Origin(owner, element.ElementId);
        List<ProfileRestriction> restrictions = [];
        using JsonDocument json = JsonDocument.Parse(element.ToJson());
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            string name = property.Name;
            if (Descriptive(name) || name is "min" or "max" or "type")
            {
                continue;
            }
            if (name is "isModifier" or "mustSupport" && property.Value.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            if (name == "constraint")
            {
                foreach (JsonElement constraint in property.Value.EnumerateArray())
                {
                    restrictions.Add(new(ProfileRestrictionKind.Invariant, "constraint", constraint.Clone(), origin));
                }
                continue;
            }
            if (name == "extension")
            {
                foreach (JsonElement extension in property.Value.EnumerateArray())
                {
                    if (!DescriptiveExtension(extension) && !NoBindingAnnotation(extension))
                    {
                        restrictions.Add(new(ProfileRestrictionKind.Unsupported, "extension", extension.Clone(), origin));
                    }
                }
                continue;
            }
            if (name == "binding" && property.Value.EnumerateObject().All(field =>
                field.Name == "extension" && field.Value.ValueKind == JsonValueKind.Array &&
                field.Value.EnumerateArray().All(DescriptiveBindingExtension)))
            {
                continue;
            }
            ProfileRestrictionKind kind = name switch
            {
                "binding" => ProfileRestrictionKind.Binding,
                "slicing" => ProfileRestrictionKind.Slicing,
                "isModifier" or "isModifierReason" => ProfileRestrictionKind.Modifier,
                "maxLength" => ProfileRestrictionKind.MaximumLength,
                "representation" => ProfileRestrictionKind.Representation,
                "meaningWhenMissing" => ProfileRestrictionKind.MissingMeaning,
                "orderMeaning" => ProfileRestrictionKind.OrderMeaning,
                "mustSupport" => ProfileRestrictionKind.MustSupport,
                _ when name.StartsWith("fixed", StringComparison.Ordinal) => ProfileRestrictionKind.Fixed,
                _ when name.StartsWith("pattern", StringComparison.Ordinal) => ProfileRestrictionKind.Pattern,
                _ when name.StartsWith("minValue", StringComparison.Ordinal) => ProfileRestrictionKind.MinimumValue,
                _ when name.StartsWith("maxValue", StringComparison.Ordinal) => ProfileRestrictionKind.MaximumValue,
                _ when name.StartsWith("defaultValue", StringComparison.Ordinal) => ProfileRestrictionKind.DefaultValue,
                _ => ProfileRestrictionKind.Unsupported,
            };
            restrictions.Add(new(kind, name, property.Value.Clone(), origin));
        }
        if (element.Max is not null && element.Max != "*" && !int.TryParse(element.Max, CultureInfo.InvariantCulture, out _))
        {
            diagnostics.Add(Context(owner, element.ElementId, "malformed maximum cardinality " + element.Max));
        }
        JsonElement[] typeFacts = json.RootElement.TryGetProperty("type", out JsonElement typeList) ? [.. typeList.EnumerateArray()] : [];
        ImmutableArray<ProfileTypeAlternative> types = [.. element.Type.Select((type, index) =>
            new ProfileTypeAlternative(type.Code, [.. type.Profile], [.. type.TargetProfile], index,
                index < typeFacts.Length ? FhirType(typeFacts[index]) : null))];
        foreach (JsonElement type in typeFacts)
        {
            if (ValidPrimitiveTypeObligation(type))
            {
                restrictions.Add(new(ProfileRestrictionKind.PrimitiveTypeObligation, "primitive type obligations", type.Clone(), origin));
            }
            else if ((Text(type, "code")?.StartsWith("http://hl7.org/fhirpath/System.", StringComparison.Ordinal) == true &&
                !ValidTypeExtension(type)) ||
                type.EnumerateObject().Any(property => property.Name is not ("code" or "profile" or "targetProfile") &&
                !(property.Name == "extension" && ValidTypeExtension(type))))
            {
                restrictions.Add(new(ProfileRestrictionKind.Unsupported, "type obligations", type.Clone(), origin));
            }
        }
        return new(relativeId, element.ElementId, element.Path, element.Min,
            int.TryParse(element.Max, CultureInfo.InvariantCulture, out int maximum) ? maximum : null,
            types, [.. restrictions], [origin], element.Min is not null, element.Max is not null)
        {
            Diagnostics = [.. diagnostics.Skip(firstDiagnostic)],
        };
    }

    private EffectiveProfileElement Merge(EffectiveProfileElement inherited, EffectiveProfileElement local,
        List<string> diagnostics, bool rootType = false, bool occurrence = false)
    {
        int firstDiagnostic = diagnostics.Count;
        if (!occurrence && ((local.Minimum is int min && inherited.Minimum is int baseMin && min < baseMin) ||
            (local.MaximumKnown && inherited.Maximum is int baseMax && (local.Maximum ?? int.MaxValue) > baseMax)))
        {
            diagnostics.Add(At(local, "local cardinality illegally loosens inherited restrictions"));
        }
        ImmutableArray<ProfileTypeAlternative> types = local.Types.Length == 0 ? inherited.Types : local.Types;
        bool primitiveIdStorage = !occurrence && local.RelativeId == ".id" &&
            local.Types is [{ Code: "http://hl7.org/fhirpath/System.String", FhirType: "string" }] &&
            inherited.Types is [{ Code: "http://hl7.org/fhirpath/System.String", FhirType: "id" }] &&
            local.Types[0].Profiles.IsEmpty && local.Types[0].TargetProfiles.IsEmpty &&
            inherited.Types[0].Profiles.IsEmpty && inherited.Types[0].TargetProfiles.IsEmpty &&
            catalog.GetStructure(local.Origins[^1].OwnerKey).ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType &&
            catalog.GetAncestors(catalog.GetStructure(local.Origins[^1].OwnerKey))
                .Any(ancestor => ancestor.Key == inherited.Origins[^1].OwnerKey);
        if (primitiveIdStorage)
        {
            // Native primitive snapshots can describe id's string storage. The inherited id
            // domain still constrains it; retain that narrower type, not an unrestricted string.
            types = [inherited.Types[0] with { Token = local.Types[0].Token }];
        }
        if (!rootType && !primitiveIdStorage && inherited.Types.Length > 0 && local.Types.Length > 0 &&
            !local.Types.All(type => inherited.Types.Any(basis => TypeNarrows(type, basis, local.Origins[^1].PackageKey) &&
                Narrower(type.Profiles, basis.Profiles, local.Origins[^1].PackageKey) &&
                Narrower(type.TargetProfiles, basis.TargetProfiles, local.Origins[^1].PackageKey))))
        {
            diagnostics.Add(At(local, "local type/profile alternatives illegally loosen inherited restrictions: " +
                JsonSerializer.Serialize(local.Types) + " does not narrow " + JsonSerializer.Serialize(inherited.Types)));
            types = [.. types.Where(type => inherited.Types.Any(basis => TypeNarrows(type, basis, local.Origins[^1].PackageKey)))];
        }
        List<ProfileRestriction> restrictions = [.. inherited.Restrictions];
        foreach (ProfileRestriction fact in local.Restrictions)
        {
            // An occurrence binding supplements, rather than replaces, the datatype's own domain.
            if (!occurrence && fact.Kind == ProfileRestrictionKind.Binding &&
                inherited.Restrictions.Any(basis => basis.Kind == ProfileRestrictionKind.Binding &&
                    Text(basis.Value, "strength") == "required") && Text(fact.Value, "strength") != "required")
            {
                diagnostics.Add(At(local, "local binding illegally loosens inherited required binding"));
            }
            // Retaining both domains/fixed patterns is an intersection, never last-write-wins.
            restrictions.Add(fact);
        }
        int minimum = Math.Max(local.Minimum ?? inherited.Minimum ?? 0, inherited.Minimum ?? 0);
        int maximum = Math.Min(local.Maximum ?? int.MaxValue, inherited.Maximum ?? int.MaxValue);
        if (minimum > maximum)
        {
            diagnostics.Add(At(local, "conflicting effective minimum/maximum cardinalities"));
        }
        foreach (ProfileRestriction fact in local.Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Fixed))
        {
            if (inherited.Restrictions.Any(basis => basis.Kind == ProfileRestrictionKind.Fixed &&
                !JsonElement.DeepEquals(basis.Value, fact.Value)))
            {
                diagnostics.Add(At(local, "conflicting inherited/local fixed values"));
            }
        }
        return local with
        {
            Minimum = minimum, Maximum = maximum == int.MaxValue ? null : maximum,
            Types = types, Restrictions = [.. restrictions], Origins = [.. inherited.Origins, .. local.Origins],
            MinimumKnown = local.MinimumKnown || inherited.MinimumKnown,
            MaximumKnown = local.MaximumKnown || inherited.MaximumKnown,
            Diagnostics = [.. inherited.Diagnostics, .. local.Diagnostics, .. diagnostics.Skip(firstDiagnostic)],
        };
    }

    private bool Narrower(ImmutableArray<string> local, ImmutableArray<string> inherited, int packageKey) => inherited.Length == 0 ||
        (local.Length > 0 && local.All(canonical => inherited.Any(basis => canonical == basis ||
            catalog.GetAncestors(catalog.ResolveDefinition(packageKey, canonical)!).Any(owner =>
                owner.Key == catalog.ResolveDefinition(packageKey, basis)!.Key))));

    private bool SameDatatypeContext(DbStructureDefinition source, DbStructureDefinition target)
    {
        string? sourceType = catalog.ReadPayload(source).Type;
        string? targetType = catalog.ReadPayload(target).Type;
        if (sourceType is null || targetType is null)
        {
            return false;
        }
        DbStructureDefinition sourceNative = catalog.ResolveNativeType(source.FhirPackageKey, sourceType)!;
        DbStructureDefinition targetNative = catalog.ResolveNativeType(target.FhirPackageKey, targetType)!;
        bool IsContext(DbStructureDefinition owner, DbStructureDefinition native) =>
            owner.Key == native.Key || catalog.GetAncestors(owner).Any(ancestor => ancestor.Key == native.Key);
        if (!IsContext(source, sourceNative) || !IsContext(target, targetNative))
        {
            return false;
        }
        if (sourceNative.UnversionedUrl == targetNative.UnversionedUrl)
        {
            return true;
        }
        DbStructureDefinition? sourceQuantity = catalog.ResolveNativeType(source.FhirPackageKey, "Quantity", required: false);
        DbStructureDefinition? targetQuantity = catalog.ResolveNativeType(target.FhirPackageKey, "Quantity", required: false);
        return sourceQuantity is not null && targetQuantity is not null &&
            sourceQuantity.UnversionedUrl == targetQuantity.UnversionedUrl &&
            IsContext(source, sourceQuantity) && IsContext(target, targetQuantity);
    }

    private EffectiveProfileBranch AtChild(EffectiveProfileBranch context, string id, ProfileTypeAlternative type,
        DbStructureDefinition definition, string? profile)
    {
        if (profile is not null)
        {
            DbStructureDefinition native = catalog.ResolveNativeType(context.Key.PackageKey, type.Code, context: context.Root.Id + id)!;
            if (definition.Key != native.Key && !catalog.GetAncestors(definition).Any(ancestor => ancestor.Key == native.Key))
            {
                throw new InvalidOperationException(Context(definition, context.Root.Id + id,
                    "child profile ancestry does not constrain the declared native datatype " + type.Code));
            }
        }
        EffectiveProfileBranch datatype = ForDefinition(definition);
        EffectiveProfileElement element = context.Elements[id];
        ProfileFactOrigin origin = element.Origins[^1];
        DbStructureDefinition inputOwner = catalog.ResolveDefinition(origin.PackageKey, origin.Canonical + "|" + origin.Version, origin.OwnerKey)!;
        DbElement inputElement = catalog.ResolveOwnedElement(inputOwner, origin.ElementId)!;
        List<string> diagnostics = [.. datatype.Diagnostics, .. element.Diagnostics];
        Dictionary<string, EffectiveProfileElement> elements = datatype.Elements.ToDictionary();
        elements[""] = Merge(elements[""], element with { RelativeId = "" }, diagnostics, rootType: true, occurrence: true);
        foreach ((string childId, EffectiveProfileElement child) in context.Elements.Where(pair => pair.Key.StartsWith(id + ".", StringComparison.Ordinal)))
        {
            string relative = childId[id.Length..];
            EffectiveProfileElement local = child with { RelativeId = relative };
            elements[relative] = elements.TryGetValue(relative, out EffectiveProfileElement? inherited)
                ? Merge(inherited, local, diagnostics) : local;
        }
        return datatype with
        {
            Key = new(origin.PackageKey, inputOwner.Key, inputElement.Key, type.Token, profile,
                null, context.Key.SliceContext + id),
            Elements = elements.ToImmutableDictionary(), Diagnostics = [.. diagnostics],
        };
    }

    private Dictionary<string, string> CorrespondingElements(EffectiveProfileBranch source, EffectiveProfileBranch target)
    {
        if (_correspondenceComparisons.Count == 0)
        {
            return source.Elements.Keys.Where(target.Elements.ContainsKey).ToDictionary(id => id, id => id, StringComparer.Ordinal);
        }
        Dictionary<string, string> result = new(StringComparer.Ordinal) { [""] = "" };
        foreach ((string id, EffectiveProfileElement element) in source.Elements.Where(pair => pair.Key != ""))
        {
            HashSet<int> sourceKeys = OriginElements(source, id);
            DbElementComparison[] authored = [.. sourceKeys.SelectMany(key => _correspondenceComparisons[key]).Where(comparison =>
                !comparison.NotMapped &&
                comparison.ElementMappingKey is not null)];
            HashSet<string> candidates = [];
            foreach (DbElementComparison comparison in authored)
            {
                DbStructureDefinition owner = catalog.GetStructure(comparison.SourceStructureKey);
                catalog.ResolveOwnedElement(owner, comparison.SourceElementId, comparison.SourceElementKey);
                RequireMappingAuthority(comparison);
                foreach (string targetId in target.Elements.Keys.Where(targetId => targetId != "" &&
                    (!(id.Contains(':') || targetId.Contains(':')) || id == targetId) &&
                    comparison.TargetElementKey is int key && OriginElements(target, targetId).Contains(key)))
                {
                    DbStructureDefinition targetOwner = catalog.GetStructure(comparison.TargetStructureKey!.Value);
                    catalog.ResolveOwnedElement(targetOwner, comparison.TargetElementId!, comparison.TargetElementKey);
                    candidates.Add(targetId);
                }
            }
            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(At(element, "ambiguous mapped child correspondence in the exact target context"));
            }
            string? corresponding = candidates.SingleOrDefault() ?? (target.Elements.ContainsKey(id) ? id : null);
            if (corresponding is not null)
            {
                if (result.ContainsValue(corresponding))
                {
                    throw new InvalidOperationException(At(element, "many-to-one child correspondence requires an unsupported transformation"));
                }
                result.Add(id, corresponding);
            }
        }
        return result;

        HashSet<int> OriginElements(EffectiveProfileBranch branch, string id)
        {
            HashSet<int> definitions = [branch.Definition.Key, .. catalog.GetAncestors(branch.Definition).Select(owner => owner.Key)];
            HashSet<int> keys = [];
            foreach (ProfileFactOrigin origin in branch.Elements[id].Origins)
            {
                if (!definitions.Contains(origin.OwnerKey) && origin.OwnerKey != branch.Key.OwnerKey)
                {
                    continue;
                }
                DbStructureDefinition owner = catalog.ResolveDefinition(branch.Key.PackageKey,
                    origin.Canonical + "|" + origin.Version, origin.OwnerKey)!;
                DbElement root = catalog.ResolveRoot(owner);
                // Inheritance is exact and declared; no basename, global slice stripping, or unrelated owner lookup.
                string expected = definitions.Contains(owner.Key) ? root.Id + id :
                    catalog.GetOwnedElements(owner).Single(element => element.Key == branch.Key.ElementKey).Id + id;
                if (origin.ElementId == expected)
                {
                    keys.Add(catalog.ResolveOwnedElement(owner, origin.ElementId)!.Key);
                }
            }
            return keys;
        }
    }

    private void RequireMappingAuthority(DbElementComparison comparison)
    {
        DbElementMapping mapping = catalog.GetElementMapping(comparison.ElementMappingKey!.Value)
            ?? throw new InvalidOperationException("Missing normalized element mapping authority.");
        if (mapping.SourceFhirPackageKey != sourcePackageKey || mapping.TargetFhirPackageKey != targetPackageKey ||
            mapping.SourceStructureKey != comparison.SourceStructureKey || mapping.SourceElementKey != comparison.SourceElementKey ||
            mapping.SourceElementId != comparison.SourceElementId || mapping.TargetStructureKey != comparison.TargetStructureKey ||
            mapping.TargetElementKey != comparison.TargetElementKey || mapping.TargetElementId != comparison.TargetElementId ||
            mapping.ExplicitNoMap)
        {
            throw new InvalidOperationException($"Comparison {comparison.Key} contradicts its exact normalized mapping authority {mapping.Key}.");
        }
    }

    private bool SameTypes(EffectiveProfileBranch source, EffectiveProfileBranch target, string id, string targetId,
        List<ProfileRepresentationProof> proofs)
    {
        if (id == "" || IsProhibited(source, id) && IsProhibited(target, targetId))
        {
            return true;
        }
        ImmutableArray<ProfileTypeAlternative> sourceTypes = source.Elements[id].Types;
        ImmutableArray<ProfileTypeAlternative> targetTypes = target.Elements[targetId].Types;
        bool[,] matches = new bool[sourceTypes.Length, targetTypes.Length];
        List<ProfileRepresentationProof> candidates = [];
        for (int s = 0; s < sourceTypes.Length; s++)
        {
            for (int t = 0; t < targetTypes.Length; t++)
            {
                ProfileTypeAlternative from = sourceTypes[s];
                ProfileTypeAlternative to = targetTypes[t];
                if (TypeIdentity(from) != TypeIdentity(to))
                {
                    continue;
                }
                matches[s, t] = ProfilesMatch(from.Profiles, to.Profiles, targetProfile: false) &
                    ProfilesMatch(from.TargetProfiles, to.TargetProfiles, targetProfile: true);

                bool ProfilesMatch(ImmutableArray<string> fromProfiles, ImmutableArray<string> toProfiles, bool targetProfile)
                {
                    if (fromProfiles.IsEmpty && toProfiles.IsEmpty)
                    {
                        return true;
                    }
                    if (targetProfile && (fromProfiles.IsEmpty || toProfiles.IsEmpty))
                    {
                        return false;
                    }
                    if (targetProfile)
                    {
                        DbStructureDefinition[] sourceDomains = [.. fromProfiles.Select(profile => catalog.ResolveDefinition(source.Key.PackageKey, profile)!)];
                        DbStructureDefinition[] targetDomains = [.. toProfiles.Select(profile => catalog.ResolveDefinition(target.Key.PackageKey, profile)!)];
                        if (sourceDomains.Concat(targetDomains).All(owner => owner.ArtifactClass != Common.Models.FhirArtifactClassEnum.Profile))
                        {
                            return sourceDomains.Select(owner => owner.UnversionedUrl).ToHashSet(StringComparer.Ordinal)
                                .SetEquals(targetDomains.Select(owner => owner.UnversionedUrl));
                        }
                    }
                    List<EffectiveProfileBranch> sources = Branches(source, from, fromProfiles, id);
                    List<EffectiveProfileBranch> targets = Branches(target, to, toProfiles, targetId);
                    List<ProfileRepresentationProof> alternatives = [];
                    bool[,] equivalent = new bool[sources.Count, targets.Count];
                    for (int a = 0; a < sources.Count; a++)
                    {
                        for (int b = 0; b < targets.Count; b++)
                        {
                            ProfileRepresentationProof proof = Prove(sources[a], targets[b].Definition, nativeContext: targets[b]);
                            alternatives.Add(proof);
                            equivalent[a, b] = proof.Kind == ProfileProofKind.NativeReuse;
                        }
                    }
                    bool covered = Covered(equivalent);
                    candidates.AddRange(alternatives.Where(proof => !proof.IsAcceptedDecision ||
                        (covered ? proof.PreservesRestrictions : !proof.PreservesRestrictions)));
                    return covered;

                    List<EffectiveProfileBranch> Branches(EffectiveProfileBranch context, ProfileTypeAlternative type,
                        ImmutableArray<string> profiles, string contextId) =>
                        [.. Alternatives(profiles).Select(profile =>
                        {
                            DbStructureDefinition definition = profile is null
                                ? catalog.ResolveNativeType(context.Key.PackageKey, type.Code, context: context.Root.Id + contextId)!
                                : catalog.ResolveDefinition(context.Key.PackageKey, profile, context: context.Root.Id + contextId)!;
                            return targetProfile ? ForDefinition(definition) : AtChild(context, contextId, type, definition, profile);
                        })];
                }
            }
        }
        bool same = Covered(matches);
        proofs.AddRange(candidates.Where(proof => !proof.IsAcceptedDecision ||
            (same ? proof.PreservesRestrictions : !proof.PreservesRestrictions)));
        return same;

        static bool Covered(bool[,] matrix) =>
            Enumerable.Range(0, matrix.GetLength(0)).All(a => Enumerable.Range(0, matrix.GetLength(1)).Any(b => matrix[a, b])) &&
            Enumerable.Range(0, matrix.GetLength(1)).All(b => Enumerable.Range(0, matrix.GetLength(0)).Any(a => matrix[a, b]));
    }

    private static bool IsProhibited(EffectiveProfileBranch branch, string id) =>
        branch.Elements.Any(pair => pair.Value.Prohibited &&
            (id == pair.Key || id.StartsWith(pair.Key + ".", StringComparison.Ordinal)));

    private bool Equivalent(ProfileRestriction source, ProfileRestriction target,
        EffectiveProfileBranch sourceContext, EffectiveProfileBranch targetContext, string relativeId, string targetRelativeId)
    {
        if (source.Kind != target.Kind || source.Name != target.Name)
        {
            return false;
        }
        if (source.Kind is ProfileRestrictionKind.PrimitiveTypeObligation or ProfileRestrictionKind.OrderMeaning or ProfileRestrictionKind.DefaultValue)
        {
            return SameDatatypeContext(sourceContext.Definition, targetContext.Definition) &&
                JsonElement.DeepEquals(source.Value, target.Value);
        }
        if (source.Kind == ProfileRestrictionKind.Invariant)
        {
            string? sourceExpression = Text(source.Value, "expression");
            string? targetExpression = Text(target.Value, "expression");
            string? sourceXPath = InvariantXPath(source.Value);
            string? targetXPath = InvariantXPath(target.Value);
            string? sourceMeaning = InvariantMeaning(source.Value, sourceContext, relativeId);
            string? targetMeaning = InvariantMeaning(target.Value, targetContext, targetRelativeId);
            return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(InvariantAnnotations(source.Value)),
                JsonSerializer.SerializeToElement(InvariantAnnotations(target.Value))) &&
                Text(source.Value, "severity") == Text(target.Value, "severity") &&
                ((sourceExpression is not null && sourceExpression == targetExpression &&
                    sourceXPath == targetXPath) ||
                (sourceExpression is null && targetExpression is null && sourceXPath is not null && sourceXPath == targetXPath) ||
                (sourceMeaning is not null && sourceMeaning == targetMeaning));
        }
        if (source.Kind == ProfileRestrictionKind.Binding)
        {
            if (Text(source.Value, "strength") == "required")
            {
                UnsafeCodeCanonical canonical = UnsafeCodeCanonical.Parse(Text(source.Value, "valueSet"));
                if (canonical.Url is null || canonical.Version is null && sourceContext.Key.PackageKey != targetContext.Key.PackageKey)
                {
                    return false;
                }
            }
            return JsonElement.DeepEquals(BindingSemantics(source.Value), BindingSemantics(target.Value));
        }
        return JsonElement.DeepEquals(source.Value, target.Value);
    }

    private bool CanProject(ProfileRestriction fact) => fact.Kind switch
    {
        ProfileRestrictionKind.Binding => Text(fact.Value, "strength") == "required" &&
            Text(fact.Value, "valueSet") is string canonical && catalog.HasValueSet(targetPackageKey, canonical) &&
            BindingSemantics(fact.Value).EnumerateObject().All(property => property.Name is "strength" or "valueSet"),
        ProfileRestrictionKind.Fixed or ProfileRestrictionKind.Pattern => fact.Value.ValueKind is
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
        ProfileRestrictionKind.MinimumValue or ProfileRestrictionKind.MaximumValue => fact.Name is "minValueDecimal" or "maxValueDecimal" &&
            fact.Value.ValueKind == JsonValueKind.Number,
        ProfileRestrictionKind.MaximumLength => fact.Value.ValueKind == JsonValueKind.Number,
        ProfileRestrictionKind.Invariant => BoundedExpression(fact.Value) is not null && InvariantAnnotations(fact.Value).Length == 0,
        _ => false,
    };

    private bool LegalProjection(ProfileRestriction fact, EffectiveProfileElement target) => fact.Kind switch
    {
        ProfileRestrictionKind.Binding => CanProject(fact) && target.Types.Length == 1 &&
            target.Types[0].Code is "code" or "Coding" or "CodeableConcept" or "Quantity" or "string" or "uri",
        ProfileRestrictionKind.MinimumValue or ProfileRestrictionKind.MaximumValue =>
            target.Types.Length == 1 && target.Types[0].Code == "decimal",
        ProfileRestrictionKind.MaximumLength => target.Types.Length == 1 &&
            target.Types[0].Code is "string" or "code" or "uri" or "url" or "id" or "canonical" or "markdown",
        ProfileRestrictionKind.Fixed or ProfileRestrictionKind.Pattern => target.Types.Length == 1 &&
            fact.Name.EndsWith(char.ToUpperInvariant(target.Types[0].Code[0]) + target.Types[0].Code[1..], StringComparison.Ordinal),
        ProfileRestrictionKind.Invariant => CanProject(fact),
        _ => false,
    };

    private static ProfileReasonCategory ReasonFor(ProfileRestriction fact) => fact.Kind switch
    {
        ProfileRestrictionKind.Invariant => ProfileReasonCategory.InvariantTransformation,
        ProfileRestrictionKind.Binding => ProfileReasonCategory.Binding,
        _ => ProfileReasonCategory.RestrictionTransformation,
    };

    private static bool Classified(ProfileRestriction fact) => fact.Kind switch
    {
        ProfileRestrictionKind.Unsupported or ProfileRestrictionKind.SemanticExtension => false,
        ProfileRestrictionKind.PrimitiveTypeObligation => ValidPrimitiveTypeObligation(fact.Value),
        ProfileRestrictionKind.OrderMeaning => fact.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fact.Value.GetString()),
        ProfileRestrictionKind.DefaultValue => fact.Value.ValueKind == JsonValueKind.Number &&
            (fact.Name == "defaultValueDecimal" && fact.Value.TryGetDecimal(out _) ||
                fact.Name == "defaultValueInteger" && fact.Value.TryGetInt32(out _)),
        ProfileRestrictionKind.Invariant => ValidInvariant(fact.Value),
        ProfileRestrictionKind.Binding => Text(fact.Value, "strength") is "required" or "extensible" or "preferred" or "example" &&
            (ValidCanonical(Text(fact.Value, "valueSet")) ||
                !fact.Value.TryGetProperty("valueSet", out _) && Text(fact.Value, "strength") is "example" or "preferred") &&
            fact.Value.EnumerateObject().All(property => property.Name is "strength" or "valueSet" or "description" ||
                property.Name == "additional" && property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().All(ValidAdditionalBinding) ||
                property.Name == "extension" && property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().All(extension => DescriptiveBindingExtension(extension) || BindingDomainExtension(extension))),
        ProfileRestrictionKind.Representation => fact.Value.ValueKind == JsonValueKind.Array &&
            fact.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String &&
                value.GetString() is "xmlAttr" or "xmlText" or "typeAttr" or "cdaText" or "xhtml"),
        ProfileRestrictionKind.MissingMeaning => fact.Value.ValueKind == JsonValueKind.String,
        ProfileRestrictionKind.Slicing => Text(fact.Value, "rules") is "open" or "closed" or "openAtEnd" &&
            fact.Value.EnumerateObject().All(property => property.Name is "discriminator" or "rules" or "ordered" or "description") &&
            (!fact.Value.TryGetProperty("ordered", out JsonElement ordered) || ordered.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
            (!fact.Value.TryGetProperty("discriminator", out JsonElement discriminators) ||
                discriminators.EnumerateArray().All(discriminator =>
                    Text(discriminator, "type") is "value" or "pattern" or "exists" or "type" or "profile" &&
                    !string.IsNullOrWhiteSpace(Text(discriminator, "path")) &&
                    discriminator.EnumerateObject().All(property => property.Name is "type" or "path"))),
        ProfileRestrictionKind.MaximumLength => fact.Value.ValueKind == JsonValueKind.Number &&
            fact.Value.TryGetInt32(out int length) && length >= 0,
        ProfileRestrictionKind.MinimumValue or ProfileRestrictionKind.MaximumValue =>
            fact.Name is "minValueInteger64" or "maxValueInteger64" ? ValidInteger64(fact.Value) :
            fact.Name is "minValueDecimal" or "maxValueDecimal" or "minValueInteger" or "maxValueInteger"
                ? fact.Value.ValueKind == JsonValueKind.Number
                : fact.Name is "minValueDate" or "maxValueDate" or "minValueDateTime" or "maxValueDateTime" or "minValueTime" or "maxValueTime" &&
                    fact.Value.ValueKind == JsonValueKind.String,
        ProfileRestrictionKind.Fixed or ProfileRestrictionKind.Pattern =>
            fact.Name is "fixedBoolean" or "patternBoolean" ? fact.Value.ValueKind is JsonValueKind.True or JsonValueKind.False :
            fact.Name is "fixedDecimal" or "patternDecimal" or "fixedInteger" or "patternInteger" or "fixedPositiveInt" or
                "patternPositiveInt" or "fixedUnsignedInt" or "patternUnsignedInt" ? fact.Value.ValueKind == JsonValueKind.Number :
            fact.Name is "fixedString" or "patternString" or "fixedCode" or "patternCode" or "fixedUri" or "patternUri" or
                "fixedUrl" or "patternUrl" or "fixedCanonical" or "patternCanonical" or "fixedId" or "patternId" or
                "fixedDate" or "patternDate" or "fixedDateTime" or "patternDateTime" or "fixedTime" or "patternTime" &&
                fact.Value.ValueKind == JsonValueKind.String,
        ProfileRestrictionKind.Modifier or ProfileRestrictionKind.MustSupport => fact.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    private static ProfileRestriction ProjectRestriction(ProfileRestriction fact)
    {
        if (fact.Kind != ProfileRestrictionKind.Invariant || Text(fact.Value, "expression") is not null)
        {
            return fact;
        }
        Dictionary<string, JsonElement> values = fact.Value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        values["expression"] = JsonSerializer.SerializeToElement(BoundedExpression(fact.Value));
        return fact with { Value = JsonSerializer.SerializeToElement(values) };
    }

    private static string? BoundedExpression(JsonElement invariant)
    {
        string? expression = Text(invariant, "expression");
        if (expression is "comparator.empty()" or "code.empty() or system.exists()" or
            "(code.exists() or value.empty()) and (system.empty() or system = 'urn:iso:std:iso:4217')" or
            "(code.exists() or value.empty()) and (system.empty() or system = %ucum)" or
            "hasValue() or (children().count() > id.count())")
        {
            string? xpath = InvariantXPath(invariant);
            return xpath is null || XPathExpression(xpath) == expression ? expression : null;
        }
        if (expression is not null)
        {
            return null;
        }
        return XPathExpression(InvariantXPath(invariant));
    }

    private static string? XPathExpression(string? xpath) => xpath switch
        {
            "not(exists(f:comparator))" => "comparator.empty()",
            "not(exists(f:code)) or exists(f:system)" => "code.empty() or system.exists()",
            "(f:code or not(f:value)) and (not(exists(f:system)) or f:system/@value='urn:iso:std:iso:4217')" =>
                "(code.exists() or value.empty()) and (system.empty() or system = 'urn:iso:std:iso:4217')",
            "(f:code or not(f:value)) and (not(exists(f:system)) or f:system/@value='http://unitsofmeasure.org')" =>
                "(code.exists() or value.empty()) and (system.empty() or system = %ucum)",
            _ => null,
        };

    private static bool Descriptive(string name) => name is "id" or "path" or "sliceName" or "base" or
        "short" or "definition" or "comment" or "requirements" or "label" or "code" or "alias" or "mapping" or
        // condition is an index of constraint keys, not another assertion. The actual constraints remain required.
        "isSummary" or "example" or "condition" or "isModifierReason";

    private static bool DescriptiveExtension(JsonElement extension) =>
        (Text(extension, "url") is "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status" or
            "http://hl7.org/fhir/StructureDefinition/structuredefinition-normative-version" &&
            Text(extension, "valueCode") is not null &&
            extension.EnumerateObject().All(field => field.Name is "url" or "valueCode")) ||
        (Text(extension, "url") == "http://hl7.org/fhir/StructureDefinition/elementdefinition-translatable" &&
            extension.TryGetProperty("valueBoolean", out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            extension.EnumerateObject().All(field => field.Name is "url" or "valueBoolean"));

    private static string TypeIdentity(ProfileTypeAlternative type) => type.FhirType ?? type.Code;

    private bool TypeNarrows(ProfileTypeAlternative local, ProfileTypeAlternative inherited, int packageKey)
    {
        if (TypeIdentity(local) == TypeIdentity(inherited) ||
            TypeIdentity(local) == "id" && TypeIdentity(inherited) == "string")
        {
            return true;
        }
        if (local.FhirType is null && inherited.FhirType is null)
        {
            return false;
        }
        DbStructureDefinition? subtype = catalog.ResolveNativeType(packageKey, TypeIdentity(local), required: false);
        DbStructureDefinition? basis = catalog.ResolveNativeType(packageKey, TypeIdentity(inherited), required: false);
        return subtype?.ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType &&
            basis?.ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType &&
            catalog.GetAncestors(subtype).Any(ancestor => ancestor.Key == basis.Key);
    }

    private static string? FhirType(JsonElement type) =>
        ValidTypeExtension(type) || ValidPrimitiveTypeObligation(type)
            ? type.GetProperty("extension").EnumerateArray().Where(extension => Text(extension, "url") == CommonDefinitions.ExtUrlFhirType)
                .Select(extension => Text(extension, "valueUrl")).SingleOrDefault()
            : null;

    private static bool ValidTypeExtension(JsonElement type) =>
        Text(type, "code") == "http://hl7.org/fhirpath/System.String" &&
        type.TryGetProperty("extension", out JsonElement extensions) && extensions.ValueKind == JsonValueKind.Array &&
        extensions.GetArrayLength() == 1 && Text(extensions[0], "url") == CommonDefinitions.ExtUrlFhirType &&
        Text(extensions[0], "valueUrl") is "id" or "string" or "uri" &&
        extensions[0].EnumerateObject().All(property => property.Name is "url" or "valueUrl");

    private static bool ValidPrimitiveTypeObligation(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Object ||
            !type.EnumerateObject().All(property => property.Name is "code" or "extension" ||
                property.Name is "profile" or "targetProfile" && property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && ValidCanonical(value.GetString()))) ||
            !type.TryGetProperty("extension", out JsonElement extensions) || extensions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        Dictionary<string, string> parameters = [];
        foreach (JsonElement extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            string? url = Text(extension, "url");
            bool regex = url is CommonDefinitions.ExtUrlSdRegex or CommonDefinitions.ExtUrlSdRegex2;
            string valueName = regex ? "valueString" : "valueUrl";
            if (url is not (CommonDefinitions.ExtUrlSdRegex or CommonDefinitions.ExtUrlSdRegex2 or
                CommonDefinitions.ExtUrlFhirType or CommonDefinitions.ExtUrlJsonType or CommonDefinitions.ExtUrlXmlType) ||
                string.IsNullOrWhiteSpace(Text(extension, valueName)) ||
                !extension.EnumerateObject().All(property => property.Name == "url" || property.Name == valueName) ||
                !parameters.TryAdd(regex ? CommonDefinitions.ExtUrlSdRegex : url, Text(extension, valueName)!))
            {
                return false;
            }
        }
        if (parameters.Count == 1 && parameters.ContainsKey(CommonDefinitions.ExtUrlSdRegex) &&
            Text(type, "code") is string code && FhirTypeUtils.IsPrimitiveType(code))
        {
            return true;
        }
        // The explicit FHIR bridge grounds the System code. R5 uses it with regex alone;
        // normalized earlier cores also supply the complete JSON/XML representation tuple.
        // Core positiveInt/unsignedInt also explicitly use System.String. A partial tuple
        // is not a known shape. Regex is opaque: neither evaluated nor translated to .NET syntax.
        return parameters.TryGetValue(CommonDefinitions.ExtUrlFhirType, out string? fhirType) &&
            FhirTypeUtils.TryGetPrimitiveInfo(fhirType, out FhirTypeUtils.FhirPrimitiveInfoRec primitive) &&
            (Text(type, "code") == primitive.FhirPathType ||
                primitive.FhirType is "positiveInt" or "unsignedInt" && Text(type, "code") == "http://hl7.org/fhirpath/System.String") &&
            (parameters.Count == 2 && parameters.ContainsKey(CommonDefinitions.ExtUrlSdRegex) ||
                parameters.TryGetValue(CommonDefinitions.ExtUrlJsonType, out string? jsonType) && jsonType == primitive.JsonType &&
                parameters.TryGetValue(CommonDefinitions.ExtUrlXmlType, out string? xmlType) && xmlType == primitive.XmlType);
    }

    private static bool ValidInteger64(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) &&
        value.GetString() == number.ToString(CultureInfo.InvariantCulture);

    private static bool DescriptiveBindingExtension(JsonElement extension) =>
        (Text(extension, "url") is "http://hl7.org/fhir/StructureDefinition/elementdefinition-bindingName" or
            "http://hl7.org/fhir/build/StructureDefinition/binding-definition" or
            "http://hl7.org/fhir/tools/StructureDefinition/binding-definition" &&
        Text(extension, "valueString") is not null &&
        extension.EnumerateObject().All(property => property.Name is "url" or "valueString")) ||
        (Text(extension, "url") == "http://hl7.org/fhir/StructureDefinition/elementdefinition-isCommonBinding" &&
        extension.TryGetProperty("valueBoolean", out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        extension.EnumerateObject().All(property => property.Name is "url" or "valueBoolean"));

    private static bool NoBindingAnnotation(JsonElement extension) =>
        Text(extension, "url") is "http://hl7.org/fhir/build/StructureDefinition/no-binding" or
            "http://hl7.org/fhir/tools/StructureDefinition/no-binding" &&
        extension.TryGetProperty("valueBoolean", out JsonElement value) && value.ValueKind == JsonValueKind.True &&
        extension.EnumerateObject().All(property => property.Name is "url" or "valueBoolean");

    private static bool BindingDomainExtension(JsonElement extension) =>
        Text(extension, "url") is "http://hl7.org/fhir/StructureDefinition/elementdefinition-maxValueSet" or
            "http://hl7.org/fhir/StructureDefinition/elementdefinition-minValueSet" &&
        ValidCanonical(Text(extension, "valueCanonical")) &&
        extension.EnumerateObject().All(field => field.Name is "url" or "valueCanonical");

    private static bool ValidAdditionalBinding(JsonElement binding) =>
        Text(binding, "purpose") is "maximum" or "minimum" or "required" or "extensible" or "candidate" or
            "current" or "preferred" or "ui" or "starter" or "component" &&
        ValidCanonical(Text(binding, "valueSet")) &&
        binding.EnumerateObject().All(field => field.Name is "purpose" or "valueSet" ||
            field.Name is "documentation" or "shortDoco" && field.Value.ValueKind == JsonValueKind.String ||
            field.Name == "any" && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static JsonElement BindingSemantics(JsonElement binding)
    {
        Dictionary<string, JsonElement> semantics = binding.EnumerateObject()
            .Where(property => property.Name is not ("description" or "extension"))
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        if (binding.TryGetProperty("extension", out JsonElement extensions))
        {
            JsonElement[] domains = [.. extensions.EnumerateArray().Where(BindingDomainExtension)];
            if (domains.Length != 0)
            {
                semantics["extension"] = JsonSerializer.SerializeToElement(domains);
            }
        }
        return JsonSerializer.SerializeToElement(semantics);
    }

    private static bool ValidInvariant(JsonElement invariant) =>
        Text(invariant, "severity") is "error" or "warning" &&
        !string.IsNullOrWhiteSpace(Text(invariant, "key")) && Text(invariant, "human") is not null &&
        (!string.IsNullOrWhiteSpace(Text(invariant, "expression")) || InvariantXPath(invariant) is not null) &&
        (!invariant.TryGetProperty("expression", out _) || !string.IsNullOrWhiteSpace(Text(invariant, "expression"))) &&
        (!invariant.TryGetProperty("source", out _) || Text(invariant, "source") is not null) &&
        (!invariant.TryGetProperty("xpath", out _) || InvariantXPath(invariant) is not null) &&
        invariant.EnumerateObject().All(property => property.Name is "key" or "severity" or "human" or "expression" or "xpath" or "source" ||
            property.Name == "extension" && ValidInvariantExtensions(property.Value));

    private static bool ValidInvariantExtensions(JsonElement extensions)
    {
        if (extensions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        HashSet<string> urls = [];
        foreach (JsonElement extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.Object || Text(extension, "url") is not string url || !urls.Add(url))
            {
                return false;
            }
            if (url == XPathExtensionUrl && ValidXPathExtension(extension))
            {
                continue;
            }
            string valueName = url == "http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice"
                ? "valueBoolean" : "valueMarkdown";
            if (!(url == "http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice" &&
                    extension.TryGetProperty(valueName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
                url == "http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice-explanation" &&
                    !string.IsNullOrWhiteSpace(Text(extension, valueName))) ||
                !extension.EnumerateObject().All(property => property.Name == "url" || property.Name == valueName))
            {
                return false;
            }
        }
        return !urls.Contains("http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice-explanation") ||
            urls.Contains("http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice");
    }

    private static JsonElement[] InvariantAnnotations(JsonElement invariant) =>
        invariant.TryGetProperty("extension", out JsonElement extensions)
            ? [.. extensions.EnumerateArray().Where(extension => Text(extension, "url") != XPathExtensionUrl)] : [];

    private static bool ValidCanonical(string? value) =>
        !string.IsNullOrEmpty(value) && UnsafeCodeCanonical.Parse(value) is { Url.Length: > 0, Version: not "" } &&
        value.Count(character => character == '|') <= 1;

    private static bool ValidXPathExtension(JsonElement extension) =>
        Text(extension, "url") == XPathExtensionUrl && !string.IsNullOrWhiteSpace(Text(extension, "valueString")) &&
        extension.EnumerateObject().All(field => field.Name is "url" or "valueString");

    private bool IsQuantityContext(DbStructureDefinition owner)
    {
        DbStructureDefinition? quantity = catalog.ResolveNativeType(owner.FhirPackageKey, "Quantity", required: false);
        return quantity is not null &&
            (owner.Key == quantity.Key || catalog.GetAncestors(owner).Any(ancestor => ancestor.Key == quantity.Key));
    }

    private string? InvariantMeaning(JsonElement invariant, EffectiveProfileBranch context, string relativeId)
    {
        if (!IsQuantityContext(context.Definition))
        {
            return null;
        }
        string? expression = BoundedExpression(invariant);
        if (expression is "comparator.empty()" or "code.empty() or system.exists()" or
            "(code.exists() or value.empty()) and (system.empty() or system = 'urn:iso:std:iso:4217')" or
            "(code.exists() or value.empty()) and (system.empty() or system = %ucum)")
        {
            return relativeId == "" && context.Elements.ContainsKey(".code") && context.Elements.ContainsKey(".system")
                ? expression : null;
        }
        // In this closed complex-type shape @value and h:div are illegal and id is an XML attribute.
        // Thus the XML node union is nonempty exactly when a non-id FHIR child exists. This does not
        // establish the same transformation for primitives, resources, or arbitrary profiled contexts.
        if (relativeId == "" && Text(invariant, "expression") is null &&
            InvariantXPath(invariant) == "@value|f:*|h:div" && QuantityElementShape(context))
        {
            return "hasValue() or (children().count() > id.count())";
        }
        // Parameters cannot occur at a corresponding, explicitly typed datatype child either.
        string? original = Text(invariant, "expression");
        bool excludesParameters = relativeId == "" ||
            context.Elements.TryGetValue(relativeId, out EffectiveProfileElement? element) &&
            element.Types.Length > 0 && element.Types.All(type => TypeIdentity(type) is
                "Extension" or "id" or "string" or "uri" or "code" or "decimal");
        return excludesParameters && InvariantXPath(invariant) is null &&
            original is "hasValue() or (children().count() > id.count())" or
                "hasValue() or (children().count() > id.count()) or $this is Parameters"
            ? "hasValue() or (children().count() > id.count())" : null;
    }

    private bool QuantityElementShape(EffectiveProfileBranch context) =>
        catalog.ReadPayload(context.Definition).Kind == StructureDefinition.StructureDefinitionKind.ComplexType &&
        context.Elements.ContainsKey(".value") && context.Elements.ContainsKey(".comparator") &&
        context.Elements.TryGetValue(".id", out EffectiveProfileElement? id) && id.Maximum == 1 &&
        id.Restrictions.Any(fact => fact.Kind == ProfileRestrictionKind.Representation &&
            fact.Value.ValueKind == JsonValueKind.Array && fact.Value.GetArrayLength() == 1 &&
            fact.Value[0].ValueKind == JsonValueKind.String && fact.Value[0].GetString() == "xmlAttr") &&
        context.Elements.Keys.Where(key => key != "" && !key[1..].Contains('.')).All(key =>
            key is ".id" or ".extension" or ".value" or ".comparator" or ".unit" or ".system" or ".code");

    private static IEnumerable<string?> Alternatives(ImmutableArray<string> values) => values.Length == 0 ? [null] : values.Select(value => (string?)value);

    private static int ParseMaximum(string? maximum) => maximum is null or "*" ? int.MaxValue : int.Parse(maximum, CultureInfo.InvariantCulture);

    internal static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static string? InvariantXPath(JsonElement invariant)
    {
        string? direct = Text(invariant, "xpath");
        if (!invariant.TryGetProperty("extension", out JsonElement extensions))
        {
            return direct;
        }
        if (extensions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        JsonElement[] matches = [.. extensions.EnumerateArray().Where(extension => Text(extension, "url") == XPathExtensionUrl)];
        if (matches.Length != 1 || !ValidXPathExtension(matches[0]))
        {
            return null;
        }
        string? retained = Text(matches[0], "valueString");
        return direct is null || direct == retained ? retained : null;
    }

    private static ProfileFactOrigin Origin(DbStructureDefinition owner, string elementId) =>
        new(owner.FhirPackageKey, owner.Key, owner.UnversionedUrl, owner.Version, elementId);

    private string Context(DbStructureDefinition owner, string elementId, string fact) =>
        $"Profile facts pair {sourcePackageKey}->{targetPackageKey}, package {owner.FhirPackageKey}, owner {owner.Key} '{owner.UnversionedUrl}|{owner.Version}', element '{elementId}': {fact}.";

    private static string At(EffectiveProfileElement element, string fact) =>
        $"{element.Origins[^1].Canonical}|{element.Origins[^1].Version}#{element.Origins[^1].ElementId}: {fact}";
}
