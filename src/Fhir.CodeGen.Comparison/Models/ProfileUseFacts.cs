// <copyright file="ProfileUseFacts.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
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
    internal bool IsAcceptedDecision => PreservesRestrictions ||
        Kind == ProfileProofKind.Unrepresentable && Reasons.Length > 0 &&
        Reasons.All(reason => reason.Category is not (ProfileReasonCategory.InvalidFacts or ProfileReasonCategory.UnclassifiedRestriction)) &&
        RequiredFacts.ContainsKey("") && NativeFacts.ContainsKey("") && TargetRoot is not null &&
        TargetCanonical is null && Projection.IsEmpty && ProjectionOwner is null;
    internal int TargetPackageKey { get; init; }
    internal ProfileUseKey? NativeInput { get; init; }
    internal ProfileFactOrigin? ProjectionOwner { get; init; }
    internal ImmutableArray<ProfileDecisionReason> Reasons { get; init; } = [];
    internal ImmutableArray<ProfileRepresentationProof> ChildProofs { get; init; } = [];
    internal ImmutableDictionary<string, EffectiveProfileElement> RequiredFacts { get; init; } = ImmutableDictionary<string, EffectiveProfileElement>.Empty;
    internal ImmutableDictionary<string, EffectiveProfileElement> NativeFacts { get; init; } = ImmutableDictionary<string, EffectiveProfileElement>.Empty;
}

/// <summary>Pair-local, conservative facts. No production consumer is routed through this interpreter yet.</summary>
internal sealed class ProfileUseFacts(XVerStructureCatalog catalog, int sourcePackageKey, int targetPackageKey) : IDisposable
{
    internal const string XPathExtensionUrl = CommonDefinitions.ExtUrlEdConstraintXPath;

    private readonly Dictionary<int, (DbElement Root, ImmutableDictionary<string, EffectiveProfileElement> Elements,
        ImmutableArray<string> Diagnostics)> _definitions = [];
    private readonly Dictionary<(int, int, int), EffectiveProfileUse> _uses = [];
    private readonly HashSet<int> _active = [];

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
                            ? catalog.ResolveNativeType(owner.FhirPackageKey, type.Code, context: element.Id)!
                            : catalog.ResolveDefinition(owner.FhirPackageKey, profile, context: element.Id)!;
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
        catalog.ReleasePayloads();
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
        IReadOnlySet<int>? emittedOwnerKeys = null, EffectiveProfileBranch? nativeContext = null)
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
                ProjectionOwner = kind == ProfileProofKind.OwnerProjection ? Origin(emittedOwner!, targetOccurrence!.Id) : null,
                RequiredFacts = source.Elements,
                NativeFacts = target?.Elements ?? ImmutableDictionary<string, EffectiveProfileElement>.Empty,
                ChildProofs = [.. childProofs],
            };
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
            if (emittedOwner is not null && targetOccurrence is not null)
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
            foreach ((string id, EffectiveProfileElement element) in source.Elements)
            {
                if (!target.Elements.TryGetValue(id, out EffectiveProfileElement? counterpart))
                {
                    Difference(ProfileReasonCategory.MissingTargetField, id, element, "complete target snapshot lacks exact child/slice " + id);
                    cannotProject = true;
                    continue;
                }
                if (element.Minimum != counterpart.Minimum || element.Maximum != counterpart.Maximum)
                {
                    Difference(ProfileReasonCategory.Cardinality, id, element,
                        $"cardinality {element.Minimum}..{element.Maximum} differs from target {counterpart.Minimum}..{counterpart.Maximum}");
                }
                if (!SameTypes(source, target, id, childProofs))
                {
                    Difference(ProfileReasonCategory.TypeAlternatives, id, element, "no supported target type/profile alternative transformation");
                    cannotProject = true;
                }
                foreach (ProfileRestriction fact in element.Restrictions)
                {
                    if (!counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id)) &&
                        !(element.Prohibited && counterpart.Prohibited))
                    {
                        Difference(ReasonFor(fact), id, element,
                            "unsupported projection of " + fact.Name + " " + fact.Value.GetRawText(), fact);
                        cannotProject |= !CanProject(fact);
                    }
                }
                foreach (ProfileRestriction fact in counterpart.Restrictions)
                {
                    if (!element.Restrictions.Any(other => Equivalent(other, fact, source, target, id)) &&
                        !(element.Prohibited && counterpart.Prohibited))
                    {
                        Difference(ReasonFor(fact), id, counterpart,
                            "target restriction not entailed by the source: " + fact.Name + " " + fact.Value.GetRawText(), fact);
                        cannotProject = true;
                    }
                }
            }
            foreach ((string id, EffectiveProfileElement element) in target.Elements.Where(pair => !source.Elements.ContainsKey(pair.Key)))
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
            foreach ((string id, EffectiveProfileElement element) in source.Elements)
            {
                EffectiveProfileElement counterpart = target.Elements[id];
                if ((element.Minimum ?? 0) < (counterpart.Minimum ?? 0) ||
                    (element.Maximum ?? int.MaxValue) > (counterpart.Maximum ?? int.MaxValue))
                {
                    Difference(ProfileReasonCategory.Cardinality, id, element, "projection would loosen the target cardinality");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                if (id.Contains(':') || element.Restrictions.Any(fact => fact.Kind == ProfileRestrictionKind.Slicing &&
                    !counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id))))
                {
                    Difference(ProfileReasonCategory.RestrictionTransformation, id, element, "projection needs an unproven slice/discriminator rewrite");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                ImmutableArray<ProfileRestriction> projected = [.. element.Restrictions
                    .Where(fact => !counterpart.Restrictions.Any(other => Equivalent(fact, other, source, target, id)))
                    .Select(ProjectRestriction)];
                if (projected.Any(fact => !LegalProjection(fact, counterpart)))
                {
                    Difference(ProfileReasonCategory.RestrictionTransformation, id, element, "restriction is not legal for the target FHIR element datatype");
                    return Result(ProfileProofKind.Unrepresentable);
                }
                projection.Add(new(element.ElementId, targetOccurrence.Id + id, targetOccurrence.Path + id,
                    element.Minimum, element.Maximum, projected));
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
                            Validate(ForDefinition(definition), active);
                        }
                    }
                }
                if (branch.TargetProfileFacts is not null)
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
                "mustSupport" => ProfileRestrictionKind.MustSupport,
                _ when name.StartsWith("fixed", StringComparison.Ordinal) => ProfileRestrictionKind.Fixed,
                _ when name.StartsWith("pattern", StringComparison.Ordinal) => ProfileRestrictionKind.Pattern,
                _ when name.StartsWith("minValue", StringComparison.Ordinal) => ProfileRestrictionKind.MinimumValue,
                _ when name.StartsWith("maxValue", StringComparison.Ordinal) => ProfileRestrictionKind.MaximumValue,
                _ => ProfileRestrictionKind.Unsupported,
            };
            restrictions.Add(new(kind, name, property.Value.Clone(), origin));
        }
        if (element.Max is not null && element.Max != "*" && !int.TryParse(element.Max, CultureInfo.InvariantCulture, out _))
        {
            diagnostics.Add(Context(owner, element.ElementId, "malformed maximum cardinality " + element.Max));
        }
        ImmutableArray<ProfileTypeAlternative> types = [.. element.Type.Select((type, index) =>
            new ProfileTypeAlternative(type.Code, [.. type.Profile], [.. type.TargetProfile], index, FhirType(type)))];
        foreach (JsonElement type in json.RootElement.TryGetProperty("type", out JsonElement typeList) ? typeList.EnumerateArray() : [])
        {
            if (type.EnumerateObject().Any(property => property.Name is not ("code" or "profile" or "targetProfile") &&
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
        if (!rootType && inherited.Types.Length > 0 && local.Types.Length > 0 &&
            !local.Types.All(type => inherited.Types.Any(basis => TypeNarrows(type, basis) &&
                Narrower(type.Profiles, basis.Profiles, local.Origins[^1].PackageKey) &&
                Narrower(type.TargetProfiles, basis.TargetProfiles, local.Origins[^1].PackageKey))))
        {
            diagnostics.Add(At(local, "local type/profile alternatives illegally loosen inherited restrictions"));
            types = [.. types.Where(type => inherited.Types.Any(basis => TypeNarrows(type, basis)))];
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

    private bool SameTypes(EffectiveProfileBranch source, EffectiveProfileBranch target, string id,
        List<ProfileRepresentationProof> proofs)
    {
        if (id == "" || IsProhibited(source, id) && IsProhibited(target, id))
        {
            return true;
        }
        ImmutableArray<ProfileTypeAlternative> sourceTypes = source.Elements[id].Types;
        ImmutableArray<ProfileTypeAlternative> targetTypes = target.Elements[id].Types;
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
                    List<EffectiveProfileBranch> sources = Branches(source, from, fromProfiles);
                    List<EffectiveProfileBranch> targets = Branches(target, to, toProfiles);
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

                    List<EffectiveProfileBranch> Branches(EffectiveProfileBranch context, ProfileTypeAlternative type, ImmutableArray<string> profiles) =>
                        [.. Alternatives(profiles).Select(profile =>
                        {
                            DbStructureDefinition definition = profile is null
                                ? catalog.ResolveNativeType(context.Key.PackageKey, type.Code, context: context.Root.Id + id)!
                                : catalog.ResolveDefinition(context.Key.PackageKey, profile, context: context.Root.Id + id)!;
                            return targetProfile ? ForDefinition(definition) : AtChild(context, id, type, definition, profile);
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
        EffectiveProfileBranch sourceContext, EffectiveProfileBranch targetContext, string relativeId)
    {
        if (source.Kind != target.Kind || source.Name != target.Name)
        {
            return false;
        }
        if (source.Kind == ProfileRestrictionKind.Invariant)
        {
            string? sourceExpression = Text(source.Value, "expression");
            string? targetExpression = Text(target.Value, "expression");
            string? sourceXPath = InvariantXPath(source.Value);
            string? targetXPath = InvariantXPath(target.Value);
            string? sourceMeaning = InvariantMeaning(source.Value, sourceContext, relativeId);
            string? targetMeaning = InvariantMeaning(target.Value, targetContext, relativeId);
            return Text(source.Value, "severity") == Text(target.Value, "severity") &&
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

    private static bool CanProject(ProfileRestriction fact) => fact.Kind switch
    {
        ProfileRestrictionKind.Fixed or ProfileRestrictionKind.Pattern => fact.Value.ValueKind is
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
        ProfileRestrictionKind.MinimumValue or ProfileRestrictionKind.MaximumValue => fact.Name is "minValueDecimal" or "maxValueDecimal" &&
            fact.Value.ValueKind == JsonValueKind.Number,
        ProfileRestrictionKind.MaximumLength => fact.Value.ValueKind == JsonValueKind.Number,
        ProfileRestrictionKind.Invariant => BoundedExpression(fact.Value) is not null,
        _ => false,
    };

    private static bool LegalProjection(ProfileRestriction fact, EffectiveProfileElement target) => fact.Kind switch
    {
        ProfileRestrictionKind.MinimumValue or ProfileRestrictionKind.MaximumValue =>
            target.Types.Length == 1 && target.Types[0].Code == "decimal",
        ProfileRestrictionKind.MaximumLength => target.Types.Length == 1 &&
            target.Types[0].Code is "string" or "code" or "uri" or "url" or "id" or "canonical" or "markdown",
        ProfileRestrictionKind.Fixed or ProfileRestrictionKind.Pattern => target.Types.Length == 1 &&
            fact.Name.EndsWith(char.ToUpperInvariant(target.Types[0].Code[0]) + target.Types[0].Code[1..], StringComparison.Ordinal),
        ProfileRestrictionKind.Invariant => BoundedExpression(fact.Value) is not null,
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

    private static bool TypeNarrows(ProfileTypeAlternative local, ProfileTypeAlternative inherited) =>
        TypeIdentity(local) == TypeIdentity(inherited) ||
        TypeIdentity(local) == "id" && TypeIdentity(inherited) == "string";

    private static string? FhirType(ElementDefinition.TypeRefComponent type) =>
        type.Code == "http://hl7.org/fhirpath/System.String" && type.Extension.Count == 1 &&
        type.Extension[0].Url == CommonDefinitions.ExtUrlFhirType &&
        type.Extension[0].Value is FhirUrl value && value.Value is "id" or "string" or "uri" ? value.Value : null;

    private static bool ValidTypeExtension(JsonElement type) =>
        Text(type, "code") == "http://hl7.org/fhirpath/System.String" &&
        type.TryGetProperty("extension", out JsonElement extensions) && extensions.ValueKind == JsonValueKind.Array &&
        extensions.GetArrayLength() == 1 && Text(extensions[0], "url") == CommonDefinitions.ExtUrlFhirType &&
        Text(extensions[0], "valueUrl") is "id" or "string" or "uri" &&
        extensions[0].EnumerateObject().All(property => property.Name is "url" or "valueUrl");

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
        (!(invariant.TryGetProperty("xpath", out _) || invariant.TryGetProperty("extension", out _)) ||
            InvariantXPath(invariant) is not null) &&
        invariant.EnumerateObject().All(property => property.Name is "key" or "severity" or "human" or "expression" or "xpath" or "source" ||
            property.Name == "extension" && property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.GetArrayLength() <= 1 && property.Value.EnumerateArray().All(ValidXPathExtension));

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
