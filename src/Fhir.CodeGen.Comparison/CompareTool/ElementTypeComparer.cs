using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.CompareTool.StructureComparer;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.CompareTool;

public class ElementTypeComparer
{
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private DbComparisonCache<DbElementTypeComparison> _elementTypeComparisonCache;
    private XVerStructureCatalog? _catalog;
    private ProfileUseFacts? _facts;

    public class ElementTypeComparisonTrackingRecord
    {
        public required FhirPackageComparisonPair PackagePair { get; set; }

        public required DbElement SourceElement { get; set; }
        public required DbElement? TargetElement { get; set; }

        public CMR? TypeRelationship { get; set; } = null;
        public string? TypeMessage { get; set; } = null;

        public CMR? TargetProfileRelationship { get; set; } = null;
        public string? TargetProfileMessage { get; set; } = null;

        public CMR? TypeProfileRelationship { get; set; } = null;
        public string? TypeProfileMessage { get; set; } = null;

        public List<DbElementTypeComparison> ElementTypeComparsions { get; set; } = [];
    }


    public ElementTypeComparer(
        IDbConnection db,
        ILoggerFactory loggerFactory,
        DbComparisonCache<DbElementTypeComparison> elementTypeComparisonCache)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ElementTypeComparer>();
        _db = db;

        _elementTypeComparisonCache = elementTypeComparisonCache;
    }

    public ElementTypeComparisonTrackingRecord DoTypeComparisons(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        DbElement? targetElement,
        int elementComparisonKey)
    {
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(_db, new(XVerPreparationKind.TypeComparison));
        ElementTypeComparisonTrackingRecord result = DoTypeComparisons(packagePair, sourceElement, targetElement,
            elementComparisonKey, operation, operation.GetComparisonPacket(packagePair.SourcePackageKey, packagePair.TargetPackageKey));
        operation.Audit();
        return result;
    }

    internal ElementTypeComparisonTrackingRecord DoTypeComparisons(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        DbElement? targetElement,
        int elementComparisonKey,
        XVerPreparedOperation operation,
        XVerProfileWorkPacket packet)
    {
        operation.RequireConnection(_db);
        if (!packet.Context.Equals(XVerProfileContext.Comparison) ||
            packet.Operands.SourcePackageKey != packagePair.SourcePackageKey ||
            packet.Operands.TargetPackageKey != packagePair.TargetPackageKey ||
            !ReferenceEquals(packet.Operands.Evidence, operation.Evidence))
        {
            throw new InvalidOperationException("Type work requires its exact comparison-time directed packet.");
        }
        _catalog = operation.Catalog;
        _facts = packet.Facts;
        try
        {
            return DoTypeComparisonsCore(packagePair, sourceElement, targetElement, elementComparisonKey);
        }
        finally
        {
            _catalog = null;
            _facts = null;
        }
    }

    private ElementTypeComparisonTrackingRecord DoTypeComparisonsCore(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        DbElement? targetElement,
        int elementComparisonKey)
    {
        // get the source types
        List<DbElementType> sourceTypes = EffectiveTypes(sourceElement);

        // Native backbone relationships continue to be determined by their children.
        // A constrained datatype occurrence must also account for its local restrictions.
        if ((sourceElement.ChildElementCount > 0 || targetElement?.ChildElementCount > 0) &&
            !NeedsProfileProof(sourceTypes) && (targetElement is null || !NeedsProfileProof(EffectiveTypes(targetElement))))
        {
            return targetElement is null ? createCollatedNoMap(packagePair, sourceElement, [], elementComparisonKey) : new()
            {
                PackagePair = packagePair, SourceElement = sourceElement, TargetElement = targetElement,
                ElementTypeComparsions = [],
            };
        }

        // if there is no target, these are all no-maps
        if (targetElement is null)
        {
            return createCollatedNoMap(
                packagePair,
                sourceElement,
                sourceTypes,
                elementComparisonKey);
        }


        return doTypeComparison(
            packagePair,
            sourceElement,
            sourceTypes,
            targetElement,
            elementComparisonKey);
    }

    private List<DbElementType> EffectiveTypes(DbElement element)
    {
        HashSet<int> visited = [];
        DbElement? current = element;
        while (current is not null && visited.Add(current.Key))
        {
            List<DbElementType> types = [.. _catalog!.GetTypeUses(current)];
            if (types.Count > 0)
            {
                return types;
            }
            current = _catalog.ResolveBaseElement(current, required: false);
        }
        return [];
    }

    private bool NeedsProfileProof(IEnumerable<DbElementType> types) => types.Any(type =>
        type.TypeProfile is not null || type.TargetProfile is not null || _catalog!.IsQuantityFamily(_catalog.ResolveTypeUse(type)));


    private ElementTypeComparisonTrackingRecord doTypeComparison(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        List<DbElementType> sourceTypes,
        DbElement targetElement,
        int elementComparisonKey)
    {
        // get the target types
        List<DbElementType> targetTypes = EffectiveTypes(targetElement);
        ILookup<int, DbElementType> targetTypesByStructureKey = targetTypes.ToLookup(et => et.StructureKey);
        ILookup<int?, DbElementType> targetTypesByTypeStructureKey = targetTypes.ToLookup(et => et.TypeStructureKey);

        if (targetTypes.Any(et => et.TypeStructureKey is null))
        {
            _logger.LogWarning($"Target element {targetElement.Id} ({targetElement.Key}) has no resolved type structure!");
            return new()
            {
                PackagePair = packagePair,
                SourceElement = sourceElement,
                TargetElement = targetElement,

                TypeRelationship = null,
                TypeMessage = null,
                TargetProfileRelationship = null,
                TargetProfileMessage = null,
                TypeProfileRelationship = null,
                TypeProfileMessage = null,

                ElementTypeComparsions = [],
            };
        }

        // get the list of all target structure keys for fast comparison matching
        List<int> targetTypeStructureKeys = targetTypes
            .Select(et => et.TypeStructureKey!.Value)
            .Distinct()
            .ToList();

        CMR? relationship = CMR.Equivalent;
        List<string?> technicalMessages = [];
        List<string?> userMessages = [];

        CMR? targetProfileRelationship = null;
        CMR? typeProfileRelationship = null;

        // iterate over the source types
        foreach (DbElementType sourceType in sourceTypes)
        {
            // get the known mappings for this source and any possible targets
            List<DbStructureMapping> typeStructureMappings = DbStructureMapping.SelectList(
                _db,
                IsFallback: false,
                SourceFhirPackageKey: sourceType.FhirPackageKey,
                SourceStructureKey: sourceType.TypeStructureKey,
                TargetFhirPackageKey: packagePair.TargetPackageKey,
                TargetStructureKeyValues: targetTypeStructureKeys);

            bool mapped = false;

            // iterate over the mappings
            foreach (DbStructureMapping typeStructureMapping in typeStructureMappings)
            {
                List<DbElementType> mappedTargetTypes = typeStructureMapping.TargetStructureKey is null
                    ? []
                    : targetTypesByTypeStructureKey[typeStructureMapping.TargetStructureKey.Value].ToList();

                foreach (DbElementType targetType in mappedTargetTypes)
                {
                    DbElementTypeComparison? etc = doElementTypeComparison(
                        packagePair,
                        sourceElement,
                        sourceType,
                        targetElement,
                        targetType,
                        typeStructureMapping,
                        elementComparisonKey);

                    if (etc is not null)
                    {
                        mapped = true;
                        relationship = FhirDbComparer.ApplyRelationship(relationship, etc.Relationship);
                        technicalMessages.Add(etc.TechnicalMessage);
                        userMessages.Add(etc.UserMessage);
                        targetProfileRelationship = targetProfileRelationship is null
                            ? etc.TargetProfileRelationship
                            : FhirDbComparer.ApplyRelationship(targetProfileRelationship, etc.TargetProfileRelationship);
                        typeProfileRelationship = typeProfileRelationship is null
                            ? etc.TypeProfileRelationship
                            : FhirDbComparer.ApplyRelationship(typeProfileRelationship, etc.TypeProfileRelationship);
                    }
                }
            }

            if (mapped)
            {
                continue;
            }

            // if there were none, check the fallback mappings
            typeStructureMappings = DbStructureMapping.SelectList(
                _db,
                IsFallback: true,
                SourceFhirPackageKey: sourceType.FhirPackageKey,
                SourceStructureKey: sourceType.TypeStructureKey,
                TargetFhirPackageKey: packagePair.TargetPackageKey,
                TargetStructureKeyValues: targetTypeStructureKeys);

            // iterate over the mappings
            foreach (DbStructureMapping typeStructureMapping in typeStructureMappings)
            {
                DbElementType? targetType;

                if (typeStructureMapping.TargetStructureKey is null)
                {
                    targetType = null;
                }
                else
                {
                    targetType = targetTypesByTypeStructureKey[typeStructureMapping.TargetStructureKey.Value].FirstOrDefault();
                }

                DbElementTypeComparison? etc = doElementTypeComparison(
                    packagePair,
                    sourceElement,
                    sourceType,
                    targetElement,
                    targetType,
                    typeStructureMapping,
                    elementComparisonKey);

                if (etc is not null)
                {
                    mapped = true;
                    relationship = FhirDbComparer.ApplyRelationship(relationship, etc.Relationship);
                    technicalMessages.Add(etc.TechnicalMessage);
                    userMessages.Add(etc.UserMessage);
                    targetProfileRelationship = targetProfileRelationship is null
                        ? etc.TargetProfileRelationship
                        : FhirDbComparer.ApplyRelationship(targetProfileRelationship, etc.TargetProfileRelationship);
                    typeProfileRelationship = typeProfileRelationship is null
                        ? etc.TypeProfileRelationship
                        : FhirDbComparer.ApplyRelationship(typeProfileRelationship, etc.TypeProfileRelationship);
                }
            }

            if (mapped)
            {
                continue;
            }

            // this is a no-map
            DbElementTypeComparison? noMapTypeComparison = doElementTypeComparison(
                packagePair,
                sourceElement,
                sourceType,
                targetElement,
                null,
                null,
                elementComparisonKey);

            relationship = FhirDbComparer.ApplyRelationship(relationship, CMR.SourceIsBroaderThanTarget);
            technicalMessages.Add(noMapTypeComparison?.TechnicalMessage);
            userMessages.Add(noMapTypeComparison?.UserMessage);
            targetProfileRelationship = targetProfileRelationship is null
                ? noMapTypeComparison?.TargetProfileRelationship
                : FhirDbComparer.ApplyRelationship(targetProfileRelationship, noMapTypeComparison?.TargetProfileRelationship);
            typeProfileRelationship = typeProfileRelationship is null
                ? noMapTypeComparison?.TypeProfileRelationship
                : FhirDbComparer.ApplyRelationship(typeProfileRelationship, noMapTypeComparison?.TypeProfileRelationship);
        }

        ElementTypeComparisonTrackingRecord comparison = new()
        {
            PackagePair = packagePair,
            SourceElement = sourceElement,
            TargetElement = targetElement,

            TypeRelationship = relationship,
            TypeMessage = string.Join(' ', technicalMessages.Where(v => v is not null)),
            //UserMessage = string.Join(' ', userMessages.Where(v => v is not null)),

            TargetProfileRelationship = targetProfileRelationship,
            TargetProfileMessage = null,

            TypeProfileRelationship = typeProfileRelationship,
            TypeProfileMessage = null,
        };

        return comparison;
    }

    private DbElementTypeComparison? doElementTypeComparison(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        DbElementType sourceType,
        DbElement targetElement,
        DbElementType? targetType,
        DbStructureMapping? typeStructureMapping,
        int elementComparisonKey)
    {
        // if there is no target type, this is a no-map
        if ((targetType is null) || (typeStructureMapping is null))
        {
            return null;
        }

        DbStructureDefinition sourceDefinition = _catalog!.ResolveTypeUse(sourceType);
        DbStructureDefinition targetDefinition = _catalog.ResolveTypeUse(targetType);
        bool constrained = sourceType.TypeProfile is not null || targetType.TypeProfile is not null ||
            sourceType.TargetProfile is not null && _catalog.ResolveTypeUse(sourceType, targetProfile: true).ArtifactClass == FhirArtifactClassEnum.Profile ||
            targetType.TargetProfile is not null && _catalog.ResolveTypeUse(targetType, targetProfile: true).ArtifactClass == FhirArtifactClassEnum.Profile ||
            _catalog.IsQuantityFamily(sourceDefinition) || _catalog.IsQuantityFamily(targetDefinition);
        if (constrained)
        {
            ProfileUseFacts facts = _facts!;
            EffectiveProfileUse sourceUse = facts.AtUse(sourceElement, sourceType);
            EffectiveProfileUse targetUse = facts.AtUse(targetElement, targetType);
            if (!sourceUse.Diagnostics.IsEmpty || !targetUse.Diagnostics.IsEmpty ||
                sourceUse.Branches.IsEmpty || targetUse.Branches.IsEmpty)
            {
                throw new InvalidOperationException($"Unresolved constrained comparison {sourceElement.Id}->{targetElement.Id}, pair {packagePair.SourcePackageKey}->{packagePair.TargetPackageKey}: {string.Join("; ", sourceUse.Diagnostics.Concat(targetUse.Diagnostics))}");
            }

            List<ProfileRepresentationProof> decisions = [];
            foreach (EffectiveProfileBranch branch in sourceUse.Branches.Where(branch =>
                branch.Key.ProfileAlternative == sourceType.TypeProfile && branch.Key.TargetProfileAlternative == sourceType.TargetProfile))
            {
                ProfileRepresentationProof[] alternatives = [.. targetUse.Branches.Where(target =>
                    target.Key.ProfileAlternative == targetType.TypeProfile && target.Key.TargetProfileAlternative == targetType.TargetProfile)
                    .Select(target => facts.Prove(branch, target.Definition, nativeContext: target))];
                if (alternatives.Any(proof => !proof.IsAcceptedDecision))
                {
                    throw new InvalidOperationException($"Unclassified constrained comparison {sourceElement.Id}->{targetElement.Id}: {string.Join("; ", alternatives.SelectMany(proof => proof.Diagnostics))}");
                }
                ProfileRepresentationProof? decision = alternatives.FirstOrDefault(proof => proof.PreservesRestrictions) ??
                    alternatives.FirstOrDefault(proof => proof.IsAcceptedDecision);
                if (decision is null)
                {
                    throw new InvalidOperationException($"Unresolved constrained comparison {sourceElement.Id}->{targetElement.Id}: {string.Join("; ", alternatives.SelectMany(proof => proof.Diagnostics))}");
                }
                decisions.Add(decision);
            }

            if (decisions.Count == 0)
            {
                throw new InvalidOperationException($"Missing effective alternative for {sourceElement.Id}, type {sourceType.Literal}.");
            }
            bool preserves = decisions.All(proof => proof.PreservesRestrictions);
            DbElementTypeComparison constrainedComparison = new()
            {
                Key = DbElementTypeComparison.GetIndex(),
                Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
                ElementComparisonKey = elementComparisonKey,
                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceElementKey = sourceElement.Key,
                SourceElementId = sourceElement.Id,
                SourceElementTypeKey = sourceType.Key,
                SourceTypeLiteral = sourceType.Literal,
                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetElementKey = targetElement.Key,
                TargetElementId = targetElement.Id,
                TargetElementTypeKey = targetType.Key,
                TargetTypeLiteral = targetType.Literal,
                NotMapped = !preserves,
                IsIdentical = false,
                Relationship = preserves ? typeStructureMapping.Relationship : CMR.SourceIsBroaderThanTarget,
                TechnicalMessage = ProfileUseFacts.SerializeDecisions(decisions),
                UserMessage = typeStructureMapping.Comments,
                TargetProfileRelationship = preserves ? typeStructureMapping.Relationship : null,
                TargetProfileMessage = null,
                TypeProfileRelationship = preserves ? typeStructureMapping.Relationship : null,
                TypeProfileMessage = null,
            };
            _elementTypeComparisonCache.CacheAdd(constrainedComparison);
            return constrainedComparison;
        }

        bool isSimple = sourceType.TypeProfile is null && sourceType.TargetProfile is null &&
            targetType.TypeProfile is null && targetType.TargetProfile is null;

        // if there are no profiles, comparison is simple
        if (isSimple)
        {
            DbElementTypeComparison etc = new()
            {
                Key = DbElementTypeComparison.GetIndex(),
                Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
                ElementComparisonKey = elementComparisonKey,

                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceElementKey = sourceElement.Key,
                SourceElementId = sourceElement.Id,
                SourceElementTypeKey = sourceType.Key,
                SourceTypeLiteral = sourceType.Literal,

                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetElementKey = targetElement.Key,
                TargetElementId = targetElement.Id,
                TargetElementTypeKey = targetType.Key,
                TargetTypeLiteral = targetType.Literal,

                NotMapped = targetType == null,
                IsIdentical = null,

                Relationship = typeStructureMapping.Relationship,
                TechnicalMessage = typeStructureMapping.TechnicalNotes,
                UserMessage = typeStructureMapping.Comments,

                TargetProfileRelationship = null,
                TargetProfileMessage = null,

                TypeProfileRelationship = null,
                TypeProfileMessage = null,
            };

            _elementTypeComparisonCache.CacheAdd(etc);
            return etc;
        }

        // if there are no source profiles but there are target profiles, the source is broader than the target
        if ((sourceType.TargetProfile is null) && (sourceType.TypeProfile is null) &&
            ((targetType.TargetProfile is not null) || (targetType.TypeProfile is not null)))
        {
            DbElementTypeComparison etc = new()
            {
                Key = DbElementTypeComparison.GetIndex(),
                Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
                ElementComparisonKey = elementComparisonKey,

                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceElementKey = sourceElement.Key,
                SourceElementId = sourceElement.Id,
                SourceElementTypeKey = sourceType.Key,
                SourceTypeLiteral = sourceType.Literal,

                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetElementKey = targetElement.Key,
                TargetElementId = targetElement.Id,
                TargetElementTypeKey = targetType.Key,
                TargetTypeLiteral = targetType.Literal,

                NotMapped = false,
                IsIdentical = null,

                Relationship = FhirDbComparer.ApplyRelationship(typeStructureMapping.Relationship, CMR.SourceIsBroaderThanTarget),
                TechnicalMessage = typeStructureMapping.TechnicalNotes +
                    $" Source type {sourceType.Literal} is less restricted than target type {targetType.Literal}",
                UserMessage = typeStructureMapping.Comments,

                TargetProfileRelationship = sourceType.TargetProfile is null ? null : CMR.SourceIsBroaderThanTarget,
                TargetProfileMessage = null,

                TypeProfileRelationship = sourceType.TypeProfile is null ? null : CMR.SourceIsBroaderThanTarget,
                TypeProfileMessage = null,
            };

            _elementTypeComparisonCache.CacheAdd(etc);
            return etc;
        }

        // if there are source profiles but no attached target profiles, the source is narrower than the target
        if ((targetType.TargetProfile is null) && (targetType.TypeProfile is null) &&
            ((sourceType.TargetProfile is not null) || (sourceType.TypeProfile is not null)))
        {
            DbElementTypeComparison etc = new()
            {
                Key = DbElementTypeComparison.GetIndex(),
                Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
                ElementComparisonKey = elementComparisonKey,

                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceElementKey = sourceElement.Key,
                SourceElementId = sourceElement.Id,
                SourceElementTypeKey = sourceType.Key,
                SourceTypeLiteral = sourceType.Literal,

                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetElementKey = targetElement.Key,
                TargetElementId = targetElement.Id,
                TargetElementTypeKey = targetType.Key,
                TargetTypeLiteral = targetType.Literal,

                NotMapped = false,
                IsIdentical = null,

                Relationship = FhirDbComparer.ApplyRelationship(typeStructureMapping.Relationship, CMR.SourceIsNarrowerThanTarget),
                TechnicalMessage = typeStructureMapping.TechnicalNotes +
                    $" Source type {sourceType.Literal} is more restricted than target type {targetType.Literal}",
                UserMessage = typeStructureMapping.Comments,

                TargetProfileRelationship = sourceType.TargetProfile is null ? null : CMR.SourceIsNarrowerThanTarget,
                TargetProfileMessage = null,

                TypeProfileRelationship = sourceType.TypeProfile is null ? null : CMR.SourceIsNarrowerThanTarget,
                TypeProfileMessage = null,
            };

            _elementTypeComparisonCache.CacheAdd(etc);
            return etc;
        }

        CMR? targetProfileRelationship = null;
        string? targetProfileMessage = null;

        CMR? typeProfileRelationship = null;
        string? typeProfileMessage = null;

        // if we have source and target profiles, we need to check if these are related
        if ((sourceType.TargetProfileStructureKey is not null) && (targetType.TargetProfileStructureKey is not null))
        {
            (targetProfileRelationship, targetProfileMessage) = doProfileComparison(
                packagePair.SourcePackageKey,
                sourceType.TargetProfileStructureKey.Value,
                packagePair.TargetPackageKey,
                targetType.TargetProfileStructureKey.Value);
        }

        if ((sourceType.TypeProfileStructureKey is not null) && (targetType.TypeProfileStructureKey is not null))
        {
            (typeProfileRelationship, typeProfileMessage) = doProfileComparison(
                packagePair.SourcePackageKey,
                sourceType.TypeProfileStructureKey.Value,
                packagePair.TargetPackageKey,
                targetType.TypeProfileStructureKey.Value);
        }

        CMR? profileRelationship = targetProfileRelationship ?? typeProfileRelationship;

        // check for no viable profile target
        if (profileRelationship is null)
        {
            return null;
            //DbElementTypeComparison noProfileMatchTypeComparison = new()
            //{
            //    Key = DbElementTypeComparison.GetIndex(),
            //    Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
            //    ElementComparisonKey = elementComparisonKey,

            //    SourceFhirPackageKey = packagePair.SourcePackageKey,
            //    SourceFhirSequence = packagePair.SourceFhirSequence,
            //    SourceElementKey = sourceElement.Key,
            //    SourceElementTypeKey = sourceType.Key,

            //    TargetFhirPackageKey = packagePair.TargetPackageKey,
            //    TargetFhirSequence = packagePair.TargetFhirSequence,
            //    TargetElementKey = targetElement.Key,
            //    TargetElementTypeKey = targetType.Key,

            //    NotMapped = true,
            //    IsIdentical = null,

            //    Relationship = CMR.SourceIsBroaderThanTarget,
            //    TechnicalMessage = typeStructureMapping.TechnicalNotes +
            //        $" Source type {sourceType.Literal} did not find a compatible profile target in {targetType.Literal}",
            //    UserMessage = typeStructureMapping.Comments,

            //    TargetProfileRelationship = targetProfileRelationship,
            //    TargetProfileMessage = targetProfileMessage,

            //    TypeProfileRelationship = typeProfileRelationship,
            //    TypeProfileMessage = typeProfileMessage,
            //};

            //_elementTypeComparisonCache.CacheAdd(noProfileMatchTypeComparison);
            //return noProfileMatchTypeComparison;
        }

        //ElementTypeComparisonTrackingRecord comparison = new()
        //{
        //    PackagePair = packagePair,
        //    SourceElement = sourceElement,
        //    TargetElement = targetElement,

        //    TypeRelationship = typeStructureMapping.Relationship,
        //    TypeMessage = typeStructureMapping.Comments,

        //    TargetProfileRelationship = targetProfileRelationship,
        //    TargetProfileMessage = targetProfileMessage,

        //    TypeProfileRelationship = typeProfileRelationship,
        //    TypeProfileMessage = typeProfileMessage,
        //};


        DbElementTypeComparison elementTypeComparison = new()
        {
            Key = DbElementTypeComparison.GetIndex(),
            Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
            ElementComparisonKey = elementComparisonKey,

            SourceFhirPackageKey = packagePair.SourcePackageKey,
            SourceFhirSequence = packagePair.SourceFhirSequence,
            SourceElementKey = sourceElement.Key,
            SourceElementId = sourceElement.Id,
            SourceElementTypeKey = sourceType.Key,
            SourceTypeLiteral = sourceType.Literal,

            TargetFhirPackageKey = packagePair.TargetPackageKey,
            TargetFhirSequence = packagePair.TargetFhirSequence,
            TargetElementKey = targetElement.Key,
            TargetElementId = targetElement.Id,
            TargetElementTypeKey = targetType.Key,
            TargetTypeLiteral = targetType.Literal,

            NotMapped = false,
            IsIdentical = null,

            Relationship = FhirDbComparer.ApplyRelationship(typeStructureMapping.Relationship, profileRelationship),
            TechnicalMessage = typeStructureMapping.TechnicalNotes +
                $" Source type {sourceType.Literal} is mapped to target type {targetType.Literal}",
            UserMessage = typeStructureMapping.Comments,

            TargetProfileRelationship = targetProfileRelationship,
            TargetProfileMessage = targetProfileMessage,

            TypeProfileRelationship = typeProfileRelationship,
            TypeProfileMessage = typeProfileMessage,
        };

        _elementTypeComparisonCache.CacheAdd(elementTypeComparison);
        return elementTypeComparison;
    }

    private (CMR? relationship, string? notes) doProfileComparison(
        int sourcePackageKey,
        int sourceProfileStructureKey,
        int targetPackageKey,
        int targetProfileStructureKey)
    {
        // get the known mappings for this source and any possible targets
        List<DbStructureMapping> typeStructureMappings = DbStructureMapping.SelectList(
            _db,
            IsFallback: false,
            SourceFhirPackageKey: sourcePackageKey,
            SourceStructureKey: sourceProfileStructureKey,
            TargetFhirPackageKey: targetPackageKey,
            TargetStructureKey: targetProfileStructureKey);

        // iterate over the mappings
        foreach (DbStructureMapping typeStructureMapping in typeStructureMappings)
        {
            if (typeStructureMapping.ExplicitNoMap != true)
            {
                return (typeStructureMapping.Relationship, typeStructureMapping.Comments);
            }
        }

        // get the fallback mappings for this source and any possible targets
        typeStructureMappings = DbStructureMapping.SelectList(
            _db,
            IsFallback: true,
            SourceFhirPackageKey: sourcePackageKey,
            SourceStructureKey: sourceProfileStructureKey,
            TargetFhirPackageKey: targetPackageKey,
            TargetStructureKey: targetProfileStructureKey);

        // iterate over the mappings
        foreach (DbStructureMapping typeStructureMapping in typeStructureMappings)
        {
            if (typeStructureMapping.ExplicitNoMap != true)
            {
                return (typeStructureMapping.Relationship, typeStructureMapping.Comments);
            }
        }

        // no match found
        return (null, null);
    }

    private ElementTypeComparisonTrackingRecord createCollatedNoMap(
        FhirPackageComparisonPair packagePair,
        DbElement sourceElement,
        List<DbElementType> sourceTypes,
        int elementComparisonKey)
    {
        ElementTypeComparisonTrackingRecord noMapComparison = new()
        {
            PackagePair = packagePair,
            SourceElement = sourceElement,
            TargetElement = null,

            TypeRelationship = null,
            TypeMessage = null,

            TargetProfileRelationship = null,
            TargetProfileMessage = null,

            TypeProfileRelationship = null,
            TypeProfileMessage = null,

            ElementTypeComparsions = [],
        };

        // create the relevant element type comparisons
        foreach (DbElementType sourceType in sourceTypes)
        {
            DbElementTypeComparison noMapTypeComparison = new()
            {
                Key = DbElementTypeComparison.GetIndex(),
                Steps = Math.Abs(packagePair.SourceFhirSequence - packagePair.TargetFhirSequence),
                ElementComparisonKey = elementComparisonKey,

                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceElementKey = sourceElement.Key,
                SourceElementId = sourceElement.Id,
                SourceElementTypeKey = sourceType.Key,
                SourceTypeLiteral = sourceType.Literal,

                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetElementKey = null,
                TargetElementId = null,
                TargetElementTypeKey = null,
                TargetTypeLiteral = null,

                NotMapped = true,
                IsIdentical = null,

                Relationship = null,
                TechnicalMessage = null,
                UserMessage = null,

                TargetProfileRelationship = null,
                TargetProfileMessage = null,

                TypeProfileRelationship = null,
                TypeProfileMessage = null,
            };

            _elementTypeComparisonCache.CacheAdd(noMapTypeComparison);
            noMapComparison.ElementTypeComparsions.Add(noMapTypeComparison);
        }

        return noMapComparison;
    }

}
