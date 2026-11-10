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

public class ElementComparer
{
    private class ElementTrackingRecord
    {
        public required DbElement SourceElement { get; init; }
        public int? CurrentElementKey { get; set; }
        public required int?[] ContentKeys { get; set; }
        public CMR? Relationship { get; set; }
        public CMR? ConceptDomainRelationship { get; set; }
        public CMR? ValueDomainRelationship { get; set; }
        public bool IsIdentical { get; set; }
        public bool RelativePathsAreIdentical { get; set; }
        public List<string> TechnicalMessages { get; set; } = [];
    }

    private class ElementTypeTrackingRecord
    {
        public required DbElementType? SourceType { get; init; }
        public required DbElementType? TargetType { get; set; }

        public required CMR? Relationship { get; set; }

        public required DbStructureMapping? TypeMapping { get; set; }

        public required CMR? TargetProfileRelationship { get; set; }
        public required string? TargetProfileMessage { get; set; }

        public required CMR? TypeProfileRelationship { get; set; }
        public required string? TypeProfileMessage { get; set; }

        public required string? UserMessage { get; set; }
        public required string? TechnicalMessage { get; set; }

        public int? SourceTypeKey => SourceType?.Key;
        public string? SourceTypeSymbol => SourceType?.Literal;

        public bool IsIdentical =>
            (SourceType is not null) &&
            (TargetType is not null) &&
            (Relationship == CMR.Equivalent) &&
            (SourceType.TypeName == TargetType.TypeName) &&
            (SourceType.TargetProfile == TargetType.TargetProfile) &&
            (SourceType.TypeProfile == TargetType.TypeProfile);

        public bool IsEquivalent =>
            (SourceType is not null) &&
            (TargetType is not null) &&
            (Relationship == CMR.Equivalent);

        public bool IsBroaderThanTarget =>
            (SourceType is not null) &&
            (TargetType is not null) &&
            (Relationship == CMR.SourceIsBroaderThanTarget);

        public bool IsNarrowerThanTarget =>
            (SourceType is not null) &&
            (TargetType is not null) &&
            (Relationship == CMR.SourceIsNarrowerThanTarget);

        public bool IsAdded =>
            (SourceType is null) &&
            (TargetType is not null);

        public bool IsRemoved =>
            (SourceType is not null) &&
            (TargetType is null);
    }

    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private DbComparisonCache<DbElementComparison> _elementComparisonCache;
    private DbComparisonCache<DbElementTypeComparison> _elementTypeComparisonCache;

    private List<DbFhirPackage> _packages = [];
    private ElementTypeComparer _elementTypeComparer;

    public ElementComparer(
        IDbConnection db,
        ILoggerFactory loggerFactory,
        DbComparisonCache<DbElementComparison> elementComparisonCache,
        DbComparisonCache<DbElementTypeComparison> elementTypeComparisonCache,
        List<DbFhirPackage> packages)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ElementComparer>();
        _db = db;
        _elementComparisonCache = elementComparisonCache;
        _elementTypeComparisonCache = elementTypeComparisonCache;
        _packages = packages;
        _elementTypeComparer = new(
            db,
            loggerFactory,
            elementTypeComparisonCache);
    }

    private bool skipElement(
        DbElement ed,
        bool skipFirstElement = true,
        bool skipIds = true,
        bool skipExtensions = true,
        bool skipModifierExtenions = true) =>
        (skipFirstElement && (ed.ResourceFieldOrder == 0)) ||
        (skipIds && ((ed.BasePath == "id") || (ed.BasePath == "Element.id") || (ed.BasePath == "Resource.id"))) ||
        (skipExtensions && ((ed.Name == "extension") || (ed.FullCollatedTypeLiteral == "Extension"))) ||
        (skipModifierExtenions &&
            ((ed.Name == "modifierExtension") || (ed.BasePath == "DomainResource.modifierExtension") || (ed.BasePath == "BackboneElement.modifierExtension")));


    internal List<DbElementComparison> DoTransitiveElementComparisons(StructureComparisonTrackingRecord sdTr)
    {
        if (sdTr.ComparisonSteps.Count == 0)
        {
            throw new Exception("Cannot build transitive comparisons without comparison steps!");
        }

        List<DbElementComparison> elementComparisons = [];

        int sourceIndex = sdTr.SourcePackage.PackageArrayIndex;
        int targetIndex = sdTr.TargetPackage.PackageArrayIndex;
        int increment = sourceIndex < targetIndex ? 1 : -1;

        // get element comparisons for each step, keyed by StructureComparisonKey
        Dictionary<int, List<DbElementComparison>> stepElementComparisons = [];
        foreach (DbStructureComparison sdCompStep in sdTr.ComparisonSteps)
        {
            List<DbElementComparison> currentStepComparisons = DbElementComparison.SelectList(
                _db,
                StructureComparisonKey: sdCompStep.Key);
            stepElementComparisons[sdCompStep.Key] = currentStepComparisons;
        }

        // get the initial source elements that are not extension or id elements
        List<DbElement> sourceElements = DbElement.SelectList(
            _db,
            StructureKey: sdTr.SourceStructure.Key)
            .Where(ed => !skipElement(ed, skipFirstElement: false))
            //.Where(e => e.FullCollatedTypeLiteral?.Equals("Extension", StringComparison.Ordinal) == false)
            //.Where(e => (e.Name.Equals("id", StringComparison.Ordinal) == false) &&
            //    (e.FullCollatedTypeLiteral?.Equals("id", StringComparison.Ordinal) == false))
            .ToList();

        // get the target elements that are not extension or id elements (if we have a target)
        Dictionary<int, DbElement> targetElements = sdTr.TargetStructure is null
            ? []
            : DbElement.SelectList(_db, StructureKey: sdTr.TargetStructure.Key)
                .Where(ed => !skipElement(ed, skipFirstElement: false))
                //.Where(e => e.FullCollatedTypeLiteral?.Equals("Extension", StringComparison.Ordinal) == false)
                //.Where(e => (e.Name.Equals("id", StringComparison.Ordinal) == false) &&
                //    (e.FullCollatedTypeLiteral?.Equals("id", StringComparison.Ordinal) == false))
                .ToDictionary(e => e.Key);

        // build a lookup from source element key to the first step's element comparisons
        ILookup<int, DbElementComparison> firstStepBySourceElement = stepElementComparisons[sdTr.ComparisonSteps[0].Key]
            .ToLookup(ec => ec.SourceElementKey);

        // iterate over each source element and follow it through the transitive steps
        foreach (DbElement sourceElement in sourceElements.OrderBy(ed => ed.ResourceFieldOrder))
        {
            // check for no target structure
            if (sdTr.TargetStructure is null)
            {
                int?[] contentKeys = new int?[6];
                contentKeys[sourceIndex] = sourceElement.Key;

                DbElementComparison noMapComparison = createElementComparison(
                    sdTr,
                    sourceElement,
                    targetElement: null,
                    elementComparisonKey: DbElementComparison.GetIndex(),
                    relationship: null,
                    cdRelationship: null,
                    vdRelationship: null,
                    technicalMessage: null,
                    userMessage: null,
                    contentStepKeys: contentKeys,
                    boundVsComparison: null,
                    typeComparison: null);

                _elementComparisonCache.CacheAdd(noMapComparison);
                elementComparisons.Add(noMapComparison);
                continue;
            }

            // get the initial element comparisons for this source element
            List<DbElementComparison> initialComparisons = firstStepBySourceElement[sourceElement.Key].ToList();

            // if there are no initial comparisons, this is a no-map
            if (initialComparisons.Count == 0)
            {
                int?[] contentKeys = new int?[6];
                contentKeys[sourceIndex] = sourceElement.Key;

                DbElementComparison noMapComparison = createElementComparison(
                    sdTr,
                    sourceElement,
                    targetElement: null,
                    elementComparisonKey: DbElementComparison.GetIndex(),
                    relationship: null,
                    cdRelationship: null,
                    vdRelationship: null,
                    technicalMessage: null,
                    userMessage: null,
                    contentStepKeys: contentKeys,
                    boundVsComparison: null,
                    typeComparison: null);

                _elementComparisonCache.CacheAdd(noMapComparison);
                elementComparisons.Add(noMapComparison);
                continue;
            }

            // follow the element through each step
            List<ElementTrackingRecord> currentPaths = initialComparisons
                .Select(ec => new ElementTrackingRecord
                {
                    SourceElement = sourceElement,
                    CurrentElementKey = ec.TargetElementKey,
                    ContentKeys = getKeyArray(sourceIndex, sourceElement.Key, sourceIndex + increment, ec.TargetElementKey),
                    Relationship = ec.Relationship,
                    IsIdentical = ec.IsIdentical == true,
                    RelativePathsAreIdentical = ec.RelativePathsAreIdentical == true,
                    TechnicalMessages = ec.TechnicalMessage is null ? [] : [ec.TechnicalMessage],
                })
                .ToList();

            // process subsequent steps
            for (int step = 1; step < sdTr.ComparisonSteps.Count; step++)
            {
                DbStructureComparison stepComparison = sdTr.ComparisonSteps[step];
                List<DbElementComparison> stepElements = stepElementComparisons[stepComparison.Key];
                ILookup<int, DbElementComparison> stepBySourceElement = stepElements.ToLookup(ec => ec.SourceElementKey);

                int stepTargetIndex = sourceIndex + (increment * (step + 1));

                List<ElementTrackingRecord> nextPaths = [];

                foreach (ElementTrackingRecord path in currentPaths)
                {
                    // if current element is null, continue with null
                    if (path.CurrentElementKey is null)
                    {
                        path.ContentKeys[stepTargetIndex] = null;
                        nextPaths.Add(path);
                        continue;
                    }

                    // get the next comparisons for this element
                    List<DbElementComparison> nextComparisons = stepBySourceElement[path.CurrentElementKey.Value].ToList();

                    if (nextComparisons.Count == 0)
                    {
                        // no explicitMapping found - treat as no-map from this point
                        path.CurrentElementKey = null;
                        path.Relationship = FhirDbComparer.ApplyRelationship(path.Relationship, null);
                        path.IsIdentical = false;
                        path.RelativePathsAreIdentical = false;
                        nextPaths.Add(path);
                        continue;
                    }

                    // expand paths for each next comparison
                    foreach (DbElementComparison nextComp in nextComparisons)
                    {
                        int?[] newContentKeys = path.ContentKeys.ToArray();
                        newContentKeys[stepTargetIndex] = nextComp.TargetElementKey;

                        nextPaths.Add(new ElementTrackingRecord
                        {
                            SourceElement = sourceElement,
                            CurrentElementKey = nextComp.TargetElementKey,
                            ContentKeys = newContentKeys,
                            Relationship = FhirDbComparer.ApplyRelationship(path.Relationship, nextComp.Relationship),
                            ConceptDomainRelationship = FhirDbComparer.ApplyRelationship(path.ConceptDomainRelationship, nextComp.ConceptDomainRelationship),
                            ValueDomainRelationship = FhirDbComparer.ApplyRelationship(path.ValueDomainRelationship, nextComp.ValueDomainRelationship),
                            IsIdentical = path.IsIdentical && (nextComp.IsIdentical == true),
                            RelativePathsAreIdentical = path.RelativePathsAreIdentical && (nextComp.RelativePathsAreIdentical == true),
                            TechnicalMessages = nextComp.TechnicalMessage is null
                                ? [.. path.TechnicalMessages]
                                : [.. path.TechnicalMessages, nextComp.TechnicalMessage],
                        });
                    }
                }

                currentPaths = nextPaths;
            }

            // create the final element comparison records for each completed path
            foreach (ElementTrackingRecord path in currentPaths)
            {
                DbElement? targetElement = null;
                CMR? relationship = path.Relationship;
                string? technicalMessage = path.TechnicalMessages.Count == 0 ? null : string.Join('\n', path.TechnicalMessages);

                if (path.CurrentElementKey is not null)
                {
                    targetElements.TryGetValue(path.CurrentElementKey.Value, out targetElement);
                }

                DbValueSetComparison? boundVsComparison = null;
                if ((sourceElement.BindingValueSetKey is not null) &&
                    (targetElement?.BindingValueSetKey is not null))
                {
                    boundVsComparison = DbValueSetComparison.SelectSingle(
                           _db,
                           SourceValueSetKey: sourceElement.BindingValueSetKey,
                           TargetValueSetKey: targetElement.BindingValueSetKey);
                }

                int elementComparisonKey = DbElementComparison.GetIndex();

                ElementTypeComparer.ElementTypeComparisonTrackingRecord etr =  _elementTypeComparer.DoTypeComparisons(
                    sdTr.PackagePair,
                    sourceElement,
                    targetElement,
                    elementComparisonKey);

                // build the element comparison db record
                DbElementComparison elementComparison = createElementComparison(
                    sdTr,
                    sourceElement,
                    targetElement,
                    elementComparisonKey,
                    path.Relationship,
                    path.ConceptDomainRelationship,
                    path.ValueDomainRelationship,
                    boundVsComparison: boundVsComparison,
                    typeComparison: etr,
                    technicalMessage: technicalMessage,
                    userMessage: null,
                    contentStepKeys: path.ContentKeys);

                _elementComparisonCache.CacheAdd(elementComparison);
                elementComparisons.Add(elementComparison);
            }
        }

        return elementComparisons;
    }

    internal List<DbElementComparison> DoElementComparisons(
        StructureComparisonTrackingRecord trackingRecord)
    {
        List<DbElementComparison> elementComparisons = [];

        DbStructureDefinition sourceSd = trackingRecord.SourceStructure;
        DbStructureDefinition? targetSd = trackingRecord.TargetStructure;

        // get the source elements that are not id and extension elements
        List<DbElement> sourceElements = DbElement.SelectList(
            _db,
            StructureKey: trackingRecord.SourceStructure.Key)
            .Where(ed => !skipElement(ed, skipFirstElement: false))
            //.Where(e => e.FullCollatedTypeLiteral?.Equals("Extension", StringComparison.Ordinal) == false)
            //.Where(e => (e.Name.Equals("id", StringComparison.Ordinal) == false) &&
            //    (e.FullCollatedTypeLiteral?.Equals("id", StringComparison.Ordinal) == false))
            .ToList();

        // if there is no target structure, every element is a no map
        if (targetSd is null)
        {
            // create our element comparisons
            foreach (DbElement sourceElement in sourceElements)
            {
                // build the element comparison db record
                DbElementComparison elementComparison = createElementComparison(
                    trackingRecord,
                    sourceElement,
                    targetElement: null,
                    elementComparisonKey: DbElementComparison.GetIndex(),
                    relationship: null,
                    cdRelationship: null,
                    vdRelationship: null,
                    technicalMessage: null,
                    userMessage: null,
                    contentStepKeys: getKeyArray(trackingRecord.SourcePackage, sourceElement.Key),
                    boundVsComparison: null,
                    typeComparison: null);

                _elementComparisonCache.CacheAdd(elementComparison);
                elementComparisons.Add(elementComparison);
            }

            return elementComparisons;
        }

        // get the target elements that are not extensions or id elements
        Dictionary<int, DbElement> targetElements = DbElement.SelectList(
            _db,
            StructureKey: targetSd.Key)
            .Where(ed => !skipElement(ed, skipFirstElement: false))
            //.Where(e => e.FullCollatedTypeLiteral?.Equals("Extension", StringComparison.Ordinal) == false)
            //.Where(e => (e.Name.Equals("id", StringComparison.Ordinal) == false) &&
            //    (e.FullCollatedTypeLiteral?.Equals("id", StringComparison.Ordinal) == false))
            .ToDictionary(e => e.Key);

        // get any explicit element mappings
        List<DbElementMapping> elementMappings = trackingRecord.ExplicitMapping is null
            ? []
            : DbElementMapping.SelectList(_db, StructureMappingKey: trackingRecord.ExplicitMapping.Key);

        // create lookups
        ILookup<string, DbElement> targetElementsByPath = targetElements.Values.ToLookup(e => e.Path);
        ILookup<int, DbElementMapping> elementMappingsBySourceKey = elementMappings
            .Where(m => m.SourceElementKey is not null)
            .ToLookup(m => m.SourceElementKey!.Value);
        ILookup<string, DbElementMapping> elementMappingsBySourceId = elementMappings.ToLookup(m => m.SourceElementId);

        Dictionary<int, List<DbElementComparison>> comparisonsByParentKey = [];
        Dictionary<int, List<DbElementComparison>> comparisonsBySourceKey = [];
        Dictionary<int, List<int>> sourceKeysByDepth = [];
        DbElement? sourceRoot = null;
        DbElement? targetRoot = null;

        // iterate over each source element
        foreach (DbElement sourceElement in sourceElements)
        {
            List<DbElementComparison>? parentElementComparisons;

            if (sourceElement.ParentElementKey is null)
            {
                parentElementComparisons = [];
                comparisonsByParentKey[-1] = parentElementComparisons;
            }
            else if (!comparisonsByParentKey.TryGetValue(sourceElement.ParentElementKey.Value, out parentElementComparisons))
            {
                parentElementComparisons = [];
                comparisonsByParentKey[sourceElement.ParentElementKey.Value] = parentElementComparisons;
            }

            if (!comparisonsBySourceKey.TryGetValue(sourceElement.Key, out List<DbElementComparison>? currentElementComparisons))
            {
                currentElementComparisons = [];
                comparisonsBySourceKey[sourceElement.Key] = currentElementComparisons;
            }

            int sourceElementDepth = sourceElement.Depth;

            if (!sourceKeysByDepth.TryGetValue(sourceElementDepth, out List<int>? elementsAtDepth))
            {
                elementsAtDepth = [];
                sourceKeysByDepth[sourceElementDepth] = elementsAtDepth;
            }

            elementsAtDepth.Add(sourceElement.Key);

            CMR? elementRelationship = null;
            CMR? elementConceptRelationship = null;
            CMR? elementValueRelationship = null;
            string? technicalMessage = null;
            string? userMessage = null;

            bool addedMappings = false;

            // check for explicit mappings first
            List<DbElementMapping> explicitMappings = elementMappingsBySourceKey[sourceElement.Key].ToList();
            foreach (DbElementMapping explicitMapping in explicitMappings ?? [])
            {
                if (explicitMapping is null)
                {
                    continue;
                }

                addedMappings = true;

                bool targetIsHigherLevelThanExplicit = false;
                int mappedElementComparisonKey = DbElementComparison.GetIndex();

                DbElement? targetElement = null;

                if (explicitMapping.TargetElementKey is not null)
                {
                    targetElements.TryGetValue(explicitMapping.TargetElementKey.Value, out targetElement);
                }

                // if we have not resolved an element from the mapping yet, move upwards until we find one
                if ((explicitMapping.TargetElementId is not null) &&
                    (explicitMapping.TargetElementKey is null))
                {
                    int dotCount = explicitMapping.TargetElementId.Count(c => c == '.');
                    string[] components = explicitMapping.TargetElementId.Split('.');
                    while (dotCount >= 1)
                    {
                        string testId = string.Join('.', components.Take(dotCount + 1));
                        targetElement = targetElements.Values.FirstOrDefault(e => e.Id == testId);
                        if (targetElement is not null)
                        {
                            targetIsHigherLevelThanExplicit = true;
                            break;
                        }
                        dotCount--;
                    }
                }

                // use the explicit relationship
                elementRelationship = explicitMapping.Relationship ?? CMR.Equivalent;
                elementConceptRelationship = explicitMapping.ConceptDomainRelationship ?? CMR.Equivalent;
                elementValueRelationship = explicitMapping.ValueDomainRelationship ?? CMR.Equivalent;

                technicalMessage = $"Using explicit mapping" +
                    $" from `{sourceSd.VersionedUrl}`" +
                    $" to `{targetSd.VersionedUrl}`" +
                    $" in `{trackingRecord.ExplicitMappingSource?.Url}` (`{trackingRecord.ExplicitMappingSource?.Filename}`)";

                if (targetIsHigherLevelThanExplicit)
                {
                    technicalMessage += $"; mapped to higher-level target element `{targetElement?.Id}`";
                }

                DbValueSetComparison? boundValueSetComparsion = null;
                if ((sourceElement.BindingValueSetKey is not null) &&
                    (targetElement?.BindingValueSetKey is not null))
                {
                    boundValueSetComparsion = DbValueSetComparison.SelectSingle(
                            _db,
                            SourceValueSetKey: sourceElement.BindingValueSetKey,
                            TargetValueSetKey: targetElement.BindingValueSetKey);
                }

                // still need to build the type comparisons, even if we are mapped
                ElementTypeComparer.ElementTypeComparisonTrackingRecord mappedTypeComparison = _elementTypeComparer.DoTypeComparisons(
                    trackingRecord.PackagePair,
                    sourceElement,
                    targetElement,
                    mappedElementComparisonKey);

                // build the element comparison db record
                DbElementComparison elementComparison = createElementComparison(
                    trackingRecord,
                    sourceElement,
                    targetElement,
                    mappedElementComparisonKey,
                    elementRelationship,
                    elementConceptRelationship,
                    elementValueRelationship,
                    boundVsComparison: boundValueSetComparsion,
                    typeComparison: mappedTypeComparison,
                    technicalMessage,
                    explicitMapping.Comments,
                    contentStepKeys: getKeyArray(
                        trackingRecord.SourcePackage,
                        sourceElement.Key,
                        trackingRecord.TargetPackage,
                        targetElement?.Key),
                    elementMappingKey: explicitMapping.Key);

                _elementComparisonCache.CacheAdd(elementComparison);
                elementComparisons.Add(elementComparison);
                parentElementComparisons.Add(elementComparison);
                currentElementComparisons.Add(elementComparison);
            }

            if (addedMappings)
            {
                continue;
            }

            sourceRoot ??= ResolveRoot(sourceSd, trackingRecord.SourcePackage.Key, sourceElement);
            targetRoot ??= ResolveRoot(targetSd, trackingRecord.TargetPackage.Key, sourceElement);
            if (sourceElement.Id != sourceRoot.Id &&
                !sourceElement.Id.StartsWith(sourceRoot.Id + ".", StringComparison.Ordinal) &&
                !sourceElement.Id.StartsWith(sourceRoot.Id + ":", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot compare element `{sourceElement.Id}` ({sourceElement.Key}) in owner `{sourceSd.VersionedUrl}` ({sourceSd.Key})" +
                    $" to `{targetSd.VersionedUrl}` ({targetSd.Key}), pair {trackingRecord.SourcePackage.Key}->{trackingRecord.TargetPackage.Key}:" +
                    $" source element does not have owning root `{sourceRoot.Id}` ({sourceRoot.Key}) as an exact prefix.");
            }
            string targetPath = targetRoot.Path + sourceElement.Id[sourceRoot.Id.Length..];

            // no explicit explicitMapping, try to find by path
            List<DbElement> possibleTargets = targetElementsByPath[targetPath].ToList();

            if (possibleTargets.Count > 1)
            {
                technicalMessage = $"Multiple target elements with path {targetPath} found in target structure {trackingRecord.TargetStructure!.Name}";
                userMessage = $"Multiple elements with path {targetPath} found in target structure.";
            }

            // if no targets found by path, this is a no-map
            if (possibleTargets.Count == 0)
            {
                DbElementComparison noMapComparison = createElementComparison(
                    trackingRecord,
                    sourceElement,
                    targetElement: null,
                    elementComparisonKey: DbElementComparison.GetIndex(),
                    relationship: null,
                    cdRelationship: null,
                    vdRelationship: null,
                    technicalMessage: technicalMessage,
                    userMessage: null,
                    contentStepKeys: getKeyArray(
                        trackingRecord.SourcePackage,
                        sourceElement.Key,
                        trackingRecord.TargetPackage,
                        null),
                    boundVsComparison: null,
                    typeComparison: null);
                _elementComparisonCache.CacheAdd(noMapComparison);
                elementComparisons.Add(noMapComparison);
                parentElementComparisons.Add(noMapComparison);
                currentElementComparisons.Add(noMapComparison);
                continue;
            }

            // iterate over the possible targets
            foreach (DbElement targetElement in possibleTargets)
            {
                CMR? targetRelationship = elementRelationship ??
                    (possibleTargets.Count == 1 ? CMR.Equivalent : CMR.SourceIsBroaderThanTarget);

                int elementComparisonKey = DbElementComparison.GetIndex();

                DbValueSetComparison? boundVsComparison = null;
                if ((sourceElement.BindingValueSetKey is not null) &&
                    (targetElement.BindingValueSetKey is not null))
                {
                    boundVsComparison = DbValueSetComparison.SelectSingle(
                           _db,
                           SourceValueSetKey: sourceElement.BindingValueSetKey,
                           TargetValueSetKey: targetElement.BindingValueSetKey);
                }

                bool vsIsEquivalent = (boundVsComparison is null) || (boundVsComparison.Relationship == CMR.Equivalent);

                // still need to build the type comparisons, even if we are mapped
                ElementTypeComparer.ElementTypeComparisonTrackingRecord typeComparison = _elementTypeComparer.DoTypeComparisons(
                    trackingRecord.PackagePair,
                    sourceElement,
                    targetElement,
                    elementComparisonKey);

                bool elementIsIdentical = (possibleTargets.Count == 1) &&
                    (sourceElement.Id == targetElement.Id) &&
                    (typeComparison.TypeRelationship == CMR.Equivalent) &&
                    vsIsEquivalent;
                bool elementIsEquivalent = elementIsIdentical ||
                    ((possibleTargets.Count == 1) && (typeComparison.TypeRelationship == CMR.Equivalent) && vsIsEquivalent);
                bool elementIsBroaderThanTarget = (possibleTargets.Count > 1) || (typeComparison.TypeRelationship == CMR.SourceIsBroaderThanTarget);

                if (elementIsIdentical)
                {
                    targetRelationship = CMR.Equivalent;
                }
                else if (elementIsEquivalent)
                {
                    targetRelationship = CMR.Equivalent;
                }
                else if (elementIsBroaderThanTarget)
                {
                    targetRelationship = CMR.SourceIsBroaderThanTarget;
                }

                // build the element comparison db record
                DbElementComparison elementComparison = createElementComparison(
                    trackingRecord,
                    sourceElement,
                    targetElement,
                    elementComparisonKey,
                    targetRelationship,
                    elementConceptRelationship,
                    elementValueRelationship,
                    boundVsComparison: boundVsComparison,
                    typeComparison: typeComparison,
                    technicalMessage,
                    userMessage,
                    contentStepKeys: getKeyArray(
                        trackingRecord.SourcePackage,
                        sourceElement.Key,
                        trackingRecord.TargetPackage,
                        targetElement.Key));
                _elementComparisonCache.CacheAdd(elementComparison);
                elementComparisons.Add(elementComparison);
                parentElementComparisons.Add(elementComparison);
                currentElementComparisons.Add(elementComparison);
            }
        }

        // elements with child elements need to be updated based on their child relationships
        foreach ((int depth, List<int> elementKeysAtDepth) in sourceKeysByDepth.OrderByDescending(kvp => kvp.Key))
        {
            // iterate over the keys
            foreach (int currentSourceKey in elementKeysAtDepth)
            {
                // check for comparisons where this element is the parent
                if (!comparisonsByParentKey.TryGetValue(currentSourceKey, out List<DbElementComparison>? childComparisons))
                {
                    // this element has no children
                    continue;
                }

                // if we have child comparisons, we first need to resolve the current comparison so we can update it
                if (!comparisonsBySourceKey.TryGetValue(currentSourceKey, out List<DbElementComparison>? currentComparisons))
                {
                    // this element has no comparisons
                    throw new Exception("Failed to resolve comparisons during post-processing!");
                }

                // iterate over the current comparisons
                foreach (DbElementComparison currentComparison in currentComparisons)
                {
                    if (currentComparison.NotMapped)
                    {
                        continue;
                    }

                    CMR? relationship = currentComparison.Relationship ?? CMR.Equivalent;

                    // apply child comparisons
                    foreach (DbElementComparison childComparison in childComparisons)
                    {
                        // only process relationship changes based on matching structure
                        if (childComparison.TargetStructureKey != currentComparison.TargetStructureKey)
                        {
                            continue;
                        }

                        relationship = FhirDbComparer.ApplyRelationship(relationship, childComparison.Relationship);

                        currentComparison.TechnicalMessage ??= string.Empty;
                        currentComparison.TechnicalMessage +=
                            $"\nComparison from child element: `{childComparison.SourceElementId}`->`{childComparison.TargetElementId}`" +
                            $" of {childComparison.Relationship} applied.";
                    }
                }
            }
        }

        // return our list of element comparisons
        return elementComparisons;

        DbElement ResolveRoot(DbStructureDefinition owner, int packageKey, DbElement sourceElement)
        {
            List<DbElement> roots = DbElement.SelectList(_db, FhirPackageKey: packageKey, StructureKey: owner.Key, ResourceFieldOrder: 0);
            if (roots.Count != 1 || string.IsNullOrEmpty(roots[0].Id) || string.IsNullOrEmpty(roots[0].Path) ||
                roots[0].ParentElementKey is not null || roots[0].Path.Contains('.', StringComparison.Ordinal) ||
                roots[0].Id != roots[0].Path)
            {
                throw new InvalidOperationException(
                    $"Cannot compare element `{sourceElement.Id}` ({sourceElement.Key}), pair {trackingRecord.SourcePackage.Key}->{trackingRecord.TargetPackage.Key}:" +
                    $" owner `{owner.VersionedUrl}` ({owner.Key}) requires one well-formed root;" +
                    $" found [{string.Join(", ", roots.Select(root => $"{root.Key}:{root.Id}/{root.Path}"))}].");
            }
            return roots[0];
        }
    }

    //private DbCollatedTypeComparison doCollatedTypeComparison(
    //    DbFhirPackage sourcePackage,
    //    DbFhirPackage targetPackage,
    //    DbElement sourceElement,
    //    DbElement targetElement,
    //    int elementComparisonKey)
    //{
    //    DbCollatedTypeComparison? existing = DbCollatedTypeComparison.SelectSingle(
    //                    _db,
    //                    SourceElementKey: sourceElement.Key,
    //                    TargetElementKey: targetElement.Key);
    //    if (existing is not null)
    //    {
    //        return existing;
    //    }

    //    DbCollatedType? sourceCollated = DbCollatedType.SelectSingle(
    //        _db,
    //        ElementKey: sourceElement.Key);
    //    if (sourceCollated is null)
    //    {
    //        // check to see if this element has a base element key set
    //        if ((sourceElement.BaseElementKey is not null) &&
    //            (DbElement.SelectSingle(_db, Key: sourceElement.BaseElementKey) is DbElement be) &&
    //            (DbCollatedType.SelectSingle(_db, ElementKey: be.Key) is DbCollatedType bect))
    //        {
    //            sourceCollated = bect;
    //        }
    //        else
    //        {
    //            throw new Exception($"Source element {sourceElement.Id} ({sourceElement.Key}) has no collated type!");
    //        }
    //    }

    //    DbCollatedType? targetCollated = DbCollatedType.SelectSingle(
    //        _db,
    //        ElementKey: targetElement.Key);
    //    if (targetCollated is null)
    //    {
    //        // check to see if this element has a base element key set
    //        if ((targetElement.BaseElementKey is not null) &&
    //            (DbElement.SelectSingle(_db, Key: targetElement.BaseElementKey) is DbElement be) &&
    //            (DbCollatedType.SelectSingle(_db, ElementKey: be.Key) is DbCollatedType bect))
    //        {
    //            targetCollated = bect;
    //        }
    //        else
    //        {
    //            throw new Exception($"Target element {targetElement.Id} ({targetElement.Key}) has no collated type!");
    //        }
    //    }

    //    // get the source types
    //    List<DbElementType> sourceTypeList = DbElementType.SelectList(_db, ElementKey: sourceElement.Key);
    //    ILookup<string?, DbElementType> sourceTypesByName = sourceTypeList.ToLookup(et => et.TypeName);

    //    // get the target types
    //    Dictionary<int, DbElementType> targetTypeDict = DbElementType.SelectDict(_db, ElementKey: targetElement.Key);
    //    List<int> targetTypeStructureKeys = targetTypeDict
    //        .Values
    //        .Select(et => et.TypeStructureKey!.Value)
    //        .Distinct()
    //        .ToList();
    //    ILookup<int, DbElementType> targetTypesByTypeStructureKey = targetTypeDict
    //        .Values
    //        .ToLookup(et => et.TypeStructureKey!.Value);

    //    //ILookup<string?, DbElementType> targetTypesByName = targetTypeDict.Values.ToLookup(et => et.TypeName);
    //    //ILookup<string, DbElementType> targetTypesByLiteral = targetTypeDict.Values.ToLookup(et => et.Literal);
    //    //List<DbElementType> targetQuantityTypes = targetTypeDict.Values.Where(et => et.IsQuantityType()).ToList();

    //    // create our shell comparison so we have somewhere to track everything
    //    DbCollatedTypeComparison ctc = new()
    //    {
    //        Key = DbCollatedTypeComparison.GetIndex(),

    //        SourceFhirPackageKey = sourcePackage.Key,
    //        SourceFhirSequence = sourcePackage.DefinitionFhirSequence,
    //        SourceElementKey = sourceElement.Key,
    //        SourceCollatedTypeKey = sourceCollated.Key,

    //        TargetFhirPackageKey = targetPackage.Key,
    //        TargetFhirSequence = targetPackage.DefinitionFhirSequence,
    //        TargetElementKey = targetElement.Key,
    //        TargetCollatedTypeKey = targetCollated.Key,

    //        Steps = Math.Abs(sourcePackage.DefinitionFhirSequence - targetPackage.DefinitionFhirSequence),
    //        ElementComparisonKey = elementComparisonKey,

    //        NotMapped = false,
    //        IsIdentical = null,

    //        Relationship = null,
    //        TechnicalMessage = null,
    //        UserMessage = null,

    //        TargetProfileRelationship = null,
    //        TargetProfileMessage = null,

    //        TypeProfileRelationship = null,
    //        TypeProfileMessage = null,
    //    };

    //    List<(DbElementType sourceType, DbElementType targetType)> typesIdentical = [];
    //    List<(DbElementType sourceType, DbElementType targetType)> typesEquivalent = [];
    //    List<(DbElementType sourceType, DbElementType targetType)> typesSourceIsBroader = [];
    //    List<(DbElementType sourceType, DbElementType targetType)> typesSourceIsNarrower = [];
    //    List<DbElementType> typesAdded = [];
    //    List<DbElementType> typesRemoved = [];

    //    HashSet<int> usedSourceTypeKeys = [];
    //    HashSet<int> usedTargetTypeKeys = [];

    //    // iterate over the source types
    //    foreach (DbElementType sourceType in sourceTypeList)
    //    {
    //        // get any comparisons from the source type structure to a target type structure
    //        List<DbStructureComparison> structureComparisons = DbStructureComparison.SelectList(
    //            _db,
    //            SourceFhirPackageKey: sourceType.FhirPackageKey,
    //            SourceStructureKey: sourceType.TypeStructureKey,
    //            TargetFhirPackageKey: targetPackage.Key,
    //            TargetStructureKeyValues: targetTypeStructureKeys);

    //        if (structureComparisons.Count > 0)
    //        {
    //            List<DbStructureComparison> currentMatches;

    //            // simpler processing if there are no additional profiles
    //            if ((sourceType.TypeProfile is null) && (sourceType.TargetProfile is null))
    //            {
    //                // check for identical (there can be only one!)
    //                currentMatches = structureComparisons.Where(sc => sc.IsIdentical == true).ToList();
    //                if (currentMatches.Count == 1)
    //                {
    //                    DbElementType resolvedTarget = targetTypesByTypeStructureKey[currentMatches[0].TargetStructureKey!.Value]
    //                        .First();

    //                    typesIdentical.Add((sourceType, resolvedTarget));
    //                    usedSourceTypeKeys.Add(sourceType.Key);
    //                    usedTargetTypeKeys.Add(resolvedTarget.Key);
    //                    continue;
    //                }
    //            }
    //        }


    //        //// check for an exact literal match because it is faster
    //        //List<DbElementType> fullLiteralMatches = targetTypesByLiteral[sourceType.Literal].ToList();
    //        //if (fullLiteralMatches.Count == 1)
    //        //{
    //        //    typesIdentical.Add((sourceType, fullLiteralMatches[0]));
    //        //    usedSourceTypeKeys.Add(sourceType.Key);
    //        //    usedTargetTypeKeys.Add(fullLiteralMatches[0].Key);
    //        //    continue;
    //        //}

    //        //// check for a literal type match
    //        //List<DbElementType> namedMatches = targetTypesByName[sourceType.TypeName].ToList();
    //        //if (namedMatches.Count > 0)
    //        //{
    //        //    // if 
    //        //}


    //        // NOTE: I don't think I need to do this anymore - quantity genericizing should be in fallback maps
    //        //// if this is a quantity type, check for quantity/profiling variations
    //        //if (sourceType.IsQuantityType())
    //        //{
    //        //    bool foundQuantityMatch = false;
    //        //    string sourceNormalizedTypeName = sourceType.GetNormalizedName();

    //        //    List<DbElementType> possibleMatches = [];

    //        //    // iterate over any matches
    //        //    foreach (DbElementType targetType in targetQuantityTypes)
    //        //    {
    //        //        string targetNormalizedTypeName = targetType.GetNormalizedName();

    //        //        // if these are the same type, it is the best match
    //        //        if (sourceNormalizedTypeName == targetNormalizedTypeName)
    //        //        {
    //        //            typesIdentical.Add((sourceType, fullLiteralMatches[0]));
    //        //            usedSourceTypeKeys.Add(sourceType.Key);
    //        //            usedTargetTypeKeys.Add(fullLiteralMatches[0].Key);
    //        //            foundQuantityMatch = true;
    //        //            break;
    //        //        }

    //        //        // if either side is "Quantity", we can use it, but do not override an exact match and keep looking
    //        //        if ((sourceNormalizedTypeName == "Quantity") || (targetNormalizedTypeName == "Quantity"))
    //        //        {
    //        //            possibleMatches.Add(targetType);
    //        //            continue;
    //        //        }
    //        //    }

    //        //    if ((!foundQuantityMatch) &&
    //        //        (possibleMatches.Count > 0))
    //        //    {
    //        //        foundQuantityMatch = true;
    //        //        typesEquivalent.Add((sourceType, possibleMatches[0]));
    //        //        usedSourceTypeKeys.Add(sourceType.Key);
    //        //        usedTargetTypeKeys.Add(possibleMatches[0].Key);
    //        //    }

    //        //    // if we found a quantity match, stop looking for type matches
    //        //    if (foundQuantityMatch)
    //        //    {
    //        //        continue;
    //        //    }
    //        //}
    //    }


    //    // start by assuming everything is a no-map
    //    Dictionary<DbElementType, DbElementType?> sourceTargetTypeMappings = sourceTypeList.ToDictionary(et => et, et => (DbElementType?)null);

    //    // iterate over the source types, looking for matching target types
    //    foreach (DbElementType sourceType in sourceTypeList)
    //    {
    //        // check to see if there is a literal match for this type
    //        if (targetTypesByName.Contains(sourceType.TypeName))
    //        {
    //            // check to see if any of our matched types have the same type and target profiles
    //            List<DbElementType> literalMatchType = targetTypesByName[sourceType.TypeName].ToList();

    //            //DbElementType? literalMatch = literalMatchType.FirstOrDefault(sourceType.IsEquivalent);
    //            DbElementType? literalMatch = literalMatchType.FirstOrDefault();
    //            if (literalMatchType != null)
    //            {
    //                // override any existing mappings, since this is an exact match
    //                sourceTargetTypeMappings[sourceType] = literalMatch;
    //                continue;
    //            }
    //        }

    //        // check to see if this is a quantity type and look for quantity matches
    //        if (sourceType.IsQuantityType())
    //        {
    //            bool foundQuantityMatch = false;
    //            string sourceNormalizedTypeName = sourceType.GetNormalizedName();

    //            // iterate over any matches
    //            foreach (DbElementType targetType in targetQuantityTypes)
    //            {
    //                string targetNormalizedTypeName = targetType.GetNormalizedName();

    //                // if these are the same type, it is the best match
    //                if (sourceNormalizedTypeName == targetNormalizedTypeName)
    //                {
    //                    sourceTargetTypeMappings[sourceType] = targetType;
    //                    foundQuantityMatch = true;
    //                    break;
    //                }

    //                // if either side is "Quantity", we can use it, but do not override an exact match and keep looking
    //                if ((sourceTargetTypeMappings[sourceType] == null) &&
    //                    ((sourceNormalizedTypeName == "Quantity") || (targetNormalizedTypeName == "Quantity")))
    //                {
    //                    sourceTargetTypeMappings[sourceType] = targetType;
    //                    foundQuantityMatch = true;
    //                    continue;
    //                }
    //            }

    //            // if we found a quantity match, stop looking for type matches
    //            if (foundQuantityMatch)
    //            {
    //                continue;
    //            }
    //        }

    //        bool haveMapping = false;

    //        // look for a compatible explicitMapping among types, based on structure comparisons in the database
    //        foreach (DbElementType targetType in targetTypeDict)
    //        {
    //            // we can only test types when both sides have resolved structures
    //            if ((sourceType.TypeStructureKey == null) ||
    //                (targetType.TypeStructureKey == null))
    //            {
    //                continue;
    //            }
                
    //            int matchCount = DbStructureComparison.SelectCount(
    //                _db,
    //                SourceFhirPackageKey: sourceElement.FhirPackageKey,
    //                SourceStructureKey: sourceType.TypeStructureKey,
    //                TargetFhirPackageKey: targetElement.FhirPackageKey,
    //                TargetStructureKey: targetType.TypeStructureKey);

    //            if (matchCount == 0)
    //            {
    //                matchCount = DbStructureMapping.SelectCount(
    //                    _db,
    //                    IsFallback: false,
    //                    SourceFhirPackageKey: sourceElement.FhirPackageKey,
    //                    SourceStructureKey: sourceType.TypeStructureKey,
    //                    TargetFhirPackageKey: targetElement.FhirPackageKey,
    //                    TargetStructureKey: targetType.TypeStructureKey);
    //            }

    //            if (matchCount == 0)
    //            {
    //                matchCount = DbStructureMapping.SelectCount(
    //                    _db,
    //                    IsFallback: true,
    //                    SourceFhirPackageKey: sourceElement.FhirPackageKey,
    //                    SourceStructureKey: sourceType.TypeStructureKey,
    //                    TargetFhirPackageKey: targetElement.FhirPackageKey,
    //                    TargetStructureKey: targetType.TypeStructureKey);
    //            }

    //            // if we did not find a target, keep looking
    //            if (matchCount == 0)
    //            {
    //                //_logger.LogWarning(
    //                //    $"{sourcePackage.ShortName}->{targetPackage.ShortName}" +
    //                //    $" type {sourcePackage.Literal}->{targetType.Literal}" +
    //                //    $" has no mapping!" +
    //                //    $" {sourceElement.Id} -> {targetElement.Id}");
    //                continue;
    //            }

    //            // if we have matching profiles (e.g., moving from reference to canonical of same type), always use it
    //            if (sourceType.HaveEquivalentProfiles(targetType))
    //            {
    //                sourceTargetTypeMappings[sourceType] = targetType;
    //                haveMapping = true;
    //                break;
    //            }
    //            else if (sourceTargetTypeMappings[sourceType] == null)
    //            {
    //                sourceTargetTypeMappings[sourceType] = targetType;
    //                haveMapping = true;
    //                continue;
    //            }
    //        }

    //        if ((!haveMapping) && (sourceTypeList.Count <= 2))
    //        {
    //            _logger.LogWarning(
    //                $"{sourcePackage.ShortName}->{targetPackage.ShortName}" +
    //                $" {sourceElement.Id} type {sourceType.Literal}" +
    //                $" has no mapping" +
    //                $" into {targetElement.Id} ({targetElement.FullCollatedTypeLiteral})");
    //        }
    //    }

    //    HashSet<int> processedTargetTypeKeys = [];

    //    // process our granular type pairs
    //    List<ElementTypeTrackingRecord> individualTypeComparisons = [];

    //    foreach ((DbElementType sourceType, DbElementType? targetType) in sourceTargetTypeMappings)
    //    {
    //        // calculate relationships for this type pair
    //        ElementTypeTrackingRecord etr = calculateTypeRelationships(
    //            sourcePackage,
    //            targetPackage,
    //            sourceType,
    //            targetType);

    //        individualTypeComparisons.Add(etr);

    //        if (targetType is not null)
    //        {
    //            processedTargetTypeKeys.Add(targetType.Key);
    //        }
    //    }

    //    // add records for any remaining target types (these are additions)
    //    foreach (DbElementType targetType in targetTypeDict)
    //    {
    //        if (processedTargetTypeKeys.Contains(targetType.Key))
    //        {
    //            continue;
    //        }

    //        individualTypeComparisons.Add(new()
    //        {
    //            SourceType = null,
    //            TargetType = targetType,
    //            Relationship = null,
    //            TargetProfileRelationship = null,
    //            TargetProfileMessage = null,
    //            TypeProfileRelationship = null,
    //            TypeProfileMessage = null,
    //            UserMessage = $"FHIR {targetPackage.ShortName} added type `{targetType.Literal}`, which is not present in FHIR {sourcePackage.ShortName}",
    //            TechnicalMessage = "Added type - no source type mapping",
    //        });
    //    }


    //    // create collated type comparison from individual comparisons
    //    DbCollatedTypeComparison collatedComparison = createCollatedTypeComparison(
    //        sourcePackage,
    //        sourceElement,
    //        sourceCollated,
    //        targetPackage,
    //        targetElement,
    //        targetCollated,
    //        elementComparisonKey,
    //        individualTypeComparisons,
    //        getKeyArray(sourcePackage, sourceCollated.Key, targetPackage, targetCollated?.Key));


    //    _collatedTypeComparisonCache.CacheAdd(collatedComparison);
    //    return collatedComparison;
    //}


    //private ElementTypeTrackingRecord calculateTypeRelationships(
    //    DbFhirPackage sourcePackage,
    //    DbFhirPackage targetPackage,
    //    DbElementType sourceType,
    //    DbElementType? targetType)
    //{
    //    if (targetType == null)
    //    {
    //        return new()
    //        {
    //            SourceType = sourceType,
    //            TargetType = null,
    //            Relationship = null,
    //            TargetProfileRelationship = null,
    //            TargetProfileMessage = null,
    //            TypeProfileRelationship = null,
    //            TypeProfileMessage = null,

    //            UserMessage = $"FHIR {sourcePackage.ShortName} Type `{sourceType.Literal}` has no mapping in FHIR {targetPackage.ShortName}",
    //            TechnicalMessage = "No target type mapping",
    //        };
    //    }

    //    CMR? relationship = CMR.Equivalent;
    //    List<string> userMessages = [];
    //    List<string> technicalMessages = [];

    //    // check for structure comparison if both types have resolved structures
    //    if ((sourceType.TypeStructureKey != null) && (targetType.TypeStructureKey != null))
    //    {
    //        DbStructureComparison? sdComparison = DbStructureComparison.SelectSingle(
    //                _db,
    //                SourceFhirPackageKey: sourcePackage.Key,
    //                SourceStructureKey: sourceType.TypeStructureKey,
    //                TargetFhirPackageKey: targetPackage.Key,
    //                TargetStructureKey: targetType.TypeStructureKey);
    //        if (sdComparison is not null)
    //        {
    //            relationship = FhirDbComparer.ApplyRelationship(relationship,  sdComparison.Relationship);

    //            if (!string.IsNullOrEmpty(sdComparison.TechnicalMessage))
    //            {
    //                technicalMessages.Add(sdComparison.TechnicalMessage);
    //            }
    //        }
    //    }

    //    // compare target profiles
    //    (CMR targetProfileRelationship, string targetProfileMessage) = compareTargetProfiles(
    //        sourcePackage,
    //        sourceType,
    //        targetPackage,
    //        targetType);
    //    if ((sourceType.TargetProfile is not null) || (targetType.TargetProfile is not null))
    //    {
    //        //relationship = FhirDbComparer.ApplyRelationship(relationship, targetProfileRelationship);
    //        if (!string.IsNullOrEmpty(targetProfileMessage))
    //        {
    //            userMessages.Add(targetProfileMessage);
    //        }
    //    }

    //    // compare type profiles
    //    (CMR typeProfileRelationship, string typeProfileMessage) = compareTypeProfiles(
    //        sourcePackage,
    //        sourceType,
    //        targetPackage,
    //        targetType);
    //    if ((sourceType.TypeProfile is not null) || (targetType.TypeProfile is not null))
    //    {
    //        //relationship = FhirDbComparer.ApplyRelationship(relationship, typeProfileRelationship);
    //        if (!string.IsNullOrEmpty(typeProfileMessage))
    //        {
    //            userMessages.Add(typeProfileMessage);
    //        }
    //    }

    //    return new()
    //    {
    //        SourceType = sourceType,
    //        TargetType = targetType,
    //        Relationship = relationship,
    //        UserMessage = string.Join(" ", userMessages),
    //        TechnicalMessage = string.Join(" ", technicalMessages),
    //        TargetProfileRelationship = targetProfileRelationship,
    //        TargetProfileMessage = targetProfileMessage,
    //        TypeProfileRelationship = typeProfileRelationship,
    //        TypeProfileMessage = typeProfileMessage,
    //    };
    //}


    //private (CMR relationship, string message) compareTypeProfiles(
    //    DbFhirPackage sourcePackage,
    //    DbElementType sourceType,
    //    DbFhirPackage targetPackage,
    //    DbElementType targetType)
    //{
    //    string[] sourceProfiles = string.IsNullOrEmpty(sourceType.TypeProfile) ? [] : [sourceType.TypeProfile];
    //    string[] targetProfiles = string.IsNullOrEmpty(targetType.TypeProfile) ? [] : [targetType.TypeProfile];

    //    return compareProfiles(
    //        sourcePackage,
    //        sourceProfiles,
    //        targetPackage,
    //        targetProfiles,
    //        $"{sourceType.Literal}:{targetType.Literal}",
    //        "type");
    //}

    //private (CMR relationship, string message) compareTargetProfiles(
    //    DbFhirPackage sourcePackage,
    //    DbElementType sourceType,
    //    DbFhirPackage targetPackage,
    //    DbElementType targetType)
    //{
    //    string[] sourceProfiles = string.IsNullOrEmpty(sourceType.TargetProfile) ? [] : [sourceType.TargetProfile];
    //    string[] targetProfiles = string.IsNullOrEmpty(targetType.TargetProfile) ? [] : [targetType.TargetProfile];

    //    return compareProfiles(
    //        sourcePackage,
    //        sourceProfiles,
    //        targetPackage,
    //        targetProfiles,
    //        $"{sourceType.Literal}:{targetType.Literal}",
    //        "target");
    //}

    //private (CMR relationship, string message) compareProfiles(
    //    DbFhirPackage sourcePackage,
    //    string[] sourceProfileList,
    //    DbFhirPackage targetPackage,
    //    string[] targetProfileList,
    //    string typeName,
    //    string profileType)
    //{
    //    if ((sourceProfileList.Length == 0) && (targetProfileList.Length == 0))
    //    {
    //        return (CMR.Equivalent, string.Empty);
    //    }

    //    if (sourceProfileList.Length == 0)
    //    {
    //        return (CMR.SourceIsBroaderThanTarget, $"Target added {typeName} {profileType} profiles: {string.Join(", ", targetProfileList)}");
    //    }

    //    if (targetProfileList.Length == 0)
    //    {
    //        return (CMR.SourceIsNarrowerThanTarget, $"Target removed {typeName} {profileType} profiles: {string.Join(", ", sourceProfileList)}");
    //    }

    //    HashSet<string> sourceTypeProfiles = new(sourceProfileList);
    //    HashSet<string> targetTypeProfiles = new(targetProfileList);

    //    List<string> missingProfiles = sourceTypeProfiles.Except(targetTypeProfiles).ToList();
    //    List<string> addedProfiles = targetTypeProfiles.Except(sourceTypeProfiles).ToList();

    //    // if we have missing profiles, check to see if they map to what we have
    //    if (missingProfiles.Count > 0)
    //    {
    //        List<DbStructureDefinition> sourceProfileStructures = [];
    //        foreach (string sp in sourceProfileList)
    //        {
    //            if (DbStructureDefinition.SelectSingle(_db, FhirPackageKey: sourcePackage.Key, UnversionedUrl: sp)
    //                is DbStructureDefinition spSd)
    //            {
    //                sourceProfileStructures.Add(spSd);
    //            }
    //        }

    //        List<DbStructureDefinition> targetProfileStructures = [];
    //        foreach (string tp in targetProfileList)
    //        {
    //            if (DbStructureDefinition.SelectSingle(_db, FhirPackageKey: targetPackage.Key, UnversionedUrl: tp)
    //                is DbStructureDefinition tpSd)
    //            {
    //                targetProfileStructures.Add(tpSd);
    //            }
    //        }

    //        foreach (string missingSourceProfile in missingProfiles)
    //        {
    //            List<DbStructureMapping> explicitMapped = DbStructureMapping.SelectList(
    //                _db,
    //                SourceFhirPackageKey: sourcePackage.Key,
    //                SourceStructureUrl: missingSourceProfile,
    //                TargetFhirPackageKey: targetPackage.Key,
    //                TargetStructureKeyValues: targetProfileStructures.Select(sd => sd.Key).ToList());

    //            Debug.Fail("working...");
    //        }
    //    }

    //    // 

    //    if ((missingProfiles.Count == 0) && (addedProfiles.Count == 0))
    //    {
    //        return (CMR.Equivalent, string.Empty);
    //    }

    //    if (missingProfiles.Count == 0)
    //    {
    //        return (CMR.SourceIsBroaderThanTarget, $"Target added {typeName} {profileType} profiles: {string.Join(", ", addedProfiles)}");
    //    }

    //    if (addedProfiles.Count == 0)
    //    {
    //        return (CMR.SourceIsNarrowerThanTarget, $"Target removed {typeName} {profileType} profiles: {string.Join(", ", missingProfiles)}");
    //    }

    //    return (CMR.RelatedTo, $"Target added {typeName} {profileType} profiles: {string.Join(", ", addedProfiles)}, removed {profileType} profiles: {string.Join(", ", missingProfiles)}");
    //}


    //private CMR? calculateOverallRelationship(CMR? conceptDomainRelationship, CMR? valueDomainRelationship)
    //{
    //    if (conceptDomainRelationship == null && valueDomainRelationship == null)
    //    {
    //        return CMR.Equivalent;
    //    }

    //    if (conceptDomainRelationship == null)
    //    {
    //        return valueDomainRelationship;
    //    }

    //    if (valueDomainRelationship == null)
    //    {
    //        return conceptDomainRelationship;
    //    }

    //    return FhirDbComparer.ApplyRelationship(conceptDomainRelationship, valueDomainRelationship);
    //}

    private int?[] getKeyArray(
        int sourceIndex,
        int sourceKey,
        int targetIndex,
        int? targetKey)
    {
        int?[] result = [null, null, null, null, null, null];
        result[sourceIndex] = sourceKey;
        result[targetIndex] = targetKey;
        return result;
    }

    private int?[] getKeyArray(
        DbFhirPackage sourcePackage,
        int sourceKey)
    {
        int?[] result = [null, null, null, null, null, null];
        result[sourcePackage.PackageArrayIndex] = sourceKey;
        return result;
    }

    private int?[] getKeyArray(
        DbFhirPackage sourcePackage,
        int sourceKey,
        DbFhirPackage targetPackage,
        int? targetKey)
    {
        int?[] result = [null, null, null, null, null, null];
        result[sourcePackage.PackageArrayIndex] = sourceKey;
        result[targetPackage.PackageArrayIndex] = targetKey;
        return result;
    }

    private DbElementComparison createElementComparison(
        StructureComparisonTrackingRecord sdTrackingRecord,
        DbElement sourceElement,
        DbElement? targetElement,
        int elementComparisonKey,
        CMR? relationship,
        CMR? cdRelationship,
        CMR? vdRelationship,
        DbValueSetComparison? boundVsComparison,
        ElementTypeComparer.ElementTypeComparisonTrackingRecord? typeComparison,
        string? technicalMessage,
        string? userMessage,
        int?[] contentStepKeys,
        bool applyRelatedRelationships = true,
        int? elementMappingKey = null)
    {
        sdTrackingRecord.ComparisonRecordKey ??= DbStructureComparison.GetIndex();

        bool? isIdentical;
        bool? relativePathsAreIdentical;
        bool? sourceRequiresMoreValues;
        bool? targetRequiresMoreValues;
        bool? sourceAllowsMoreValues;
        bool? targetAllowsMoreValues;

        if (targetElement is null)
        {
            isIdentical = null;
            relativePathsAreIdentical = null;
            sourceRequiresMoreValues = null;
            targetRequiresMoreValues = null;
            sourceAllowsMoreValues = null;
            targetAllowsMoreValues = null;
        }
        else
        {
            isIdentical = sourceElement.Id == targetElement.Id;
            relativePathsAreIdentical = sourceElement.Id[sourceElement.StructureName.Length..] == targetElement.Id[targetElement.StructureName.Length..];
            sourceRequiresMoreValues = sourceElement.MinCardinality > targetElement.MinCardinality;
            targetRequiresMoreValues = targetElement.MinCardinality > sourceElement.MinCardinality;

            if ((sourceElement.MaxCardinality == -1) && (targetElement.MaxCardinality != -1))
            {
                sourceAllowsMoreValues = true;
            }
            else if ((targetElement.MaxCardinality == -1) && (sourceElement.MaxCardinality != -1))
            {
                sourceAllowsMoreValues = false;
            }
            else
            {
                sourceAllowsMoreValues = sourceElement.MaxCardinality > targetElement.MaxCardinality;
            }

            if ((targetElement.MaxCardinality == -1) && (sourceElement.MaxCardinality != -1))
            {
                targetAllowsMoreValues = true;
            }
            else if ((sourceElement.MaxCardinality == -1) && (targetElement.MaxCardinality != -1))
            {
                targetAllowsMoreValues = false;
            }
            else
            {
                targetAllowsMoreValues = targetElement.MaxCardinality > sourceElement.MaxCardinality;
            }

            if (applyRelatedRelationships && (sourceAllowsMoreValues == true))
            {
                if (sourceElement.ResourceFieldOrder == 0)
                {
                    technicalMessage += $"\n" +
                        $"Source element ({sourceElement.MaxCardinalityString}) allows more values than the target ({targetElement.MaxCardinalityString}), the is based on a type so remains {relationship}.";
                }
                else
                {
                    relationship = FhirDbComparer.ApplyRelationship(relationship, CMR.SourceIsBroaderThanTarget);
                    technicalMessage += $"\n" +
                        $"Source element ({sourceElement.MaxCardinalityString}) allows more values than the target ({targetElement.MaxCardinalityString}).";
                }
            }

            if (applyRelatedRelationships && (targetAllowsMoreValues == true))
            {
                if (targetElement.ResourceFieldOrder == 0)
                {
                    technicalMessage += $"\n" +
                        $"Source element ({sourceElement.MaxCardinalityString}) allows fewer values than the target ({targetElement.MaxCardinalityString}), but is targeting a type so remains {relationship}.";
                }
                else
                {
                    relationship = FhirDbComparer.ApplyRelationship(relationship, CMR.SourceIsNarrowerThanTarget);
                    technicalMessage += $"\n" +
                        $"Source element ({sourceElement.MaxCardinalityString}) allows fewer values than the target ({targetElement.MaxCardinalityString}).";
                }
            }

            // apply bound vs relationship to overall relationship - only if the binding is required on either side
            if (applyRelatedRelationships &&
                (boundVsComparison?.Relationship is not null) &&
                ((sourceElement.ValueSetBindingStrength == BindingStrength.Required) || (targetElement.ValueSetBindingStrength == BindingStrength.Required)))
            {
                relationship = FhirDbComparer.ApplyRelationship(relationship, boundVsComparison.Relationship);
                technicalMessage += $"\n" +
                    $"Value set binding relationship of {boundVsComparison.Relationship} applied to comparison," +
                    $" source is a {sourceElement.ValueSetBindingStrength} to `{sourceElement.BindingValueSet}`," +
                    $" target is a {targetElement.ValueSetBindingStrength} to `{targetElement.BindingValueSet}`.";
            }

            // apply element type relationship to overall relationship
            if (applyRelatedRelationships && (typeComparison?.TypeRelationship is not null))
            {
                relationship = FhirDbComparer.ApplyRelationship(relationship, typeComparison.TypeRelationship);
                technicalMessage += $"\n" +
                    $"Type relationship of {typeComparison.TypeRelationship} applied to comparison.";
            }
        }

        return new()
        {
            Key = elementComparisonKey,
            StructureComparisonKey = sdTrackingRecord.ComparisonRecordKey.Value,
            ElementMappingKey = elementMappingKey,

            Steps = Math.Abs(sdTrackingRecord.SourcePackage.DefinitionFhirSequence - sdTrackingRecord.TargetPackage.DefinitionFhirSequence),

            SourceFhirPackageKey = sdTrackingRecord.SourcePackage.Key,
            SourceFhirSequence = sdTrackingRecord.SourcePackage.DefinitionFhirSequence,
            SourceStructureKey = sdTrackingRecord.SourceStructure.Key,
            SourceElementKey = sourceElement.Key,
            SourceElementId = sourceElement.Id,

            TargetFhirPackageKey = sdTrackingRecord.TargetPackage.Key,
            TargetFhirSequence = sdTrackingRecord.TargetPackage.DefinitionFhirSequence,
            TargetStructureKey = sdTrackingRecord.TargetStructure?.Key,
            TargetElementKey = targetElement?.Key,
            TargetElementId = targetElement?.Id,

            ContentKeys = contentStepKeys,

            Relationship = relationship,
            ConceptDomainRelationship = cdRelationship,
            ValueDomainRelationship = vdRelationship,

            BoundValueSetComparisonKey = boundVsComparison?.Key,
            BoundValueSetRelationship = boundVsComparison?.Relationship,

            TypeRelationship = typeComparison?.TypeRelationship,
            TypeMessage = typeComparison?.TypeMessage,
            TypeProfileRelationship = typeComparison?.TypeProfileRelationship,
            TypeProfileMessage = typeComparison?.TypeProfileMessage,
            TargetProfileRelationship = typeComparison?.TargetProfileRelationship,
            TargetProfileMessage = typeComparison?.TargetProfileMessage,

            NotMapped = targetElement is null,

            IsIdentical = isIdentical,
            RelativePathsAreIdentical = relativePathsAreIdentical,
            SourceRequiresMoreValues = sourceRequiresMoreValues,
            TargetRequiresMoreValues = targetRequiresMoreValues,
            SourceAllowsMoreValues = sourceAllowsMoreValues,
            TargetAllowsMoreValues = targetAllowsMoreValues,

            TechnicalMessage = technicalMessage,
            UserMessage = userMessage,
        };
    }

    //private DbCollatedTypeComparison createCollatedTypeComparison(
    //    DbFhirPackage sourcePackage,
    //    DbElement sourceElement,
    //    DbCollatedType sourceCollatedType,
    //    DbFhirPackage targetPackage,
    //    DbElement? targetElement,
    //    DbCollatedType? targetCollatedType,
    //    int elementComparisonKey,
    //    List<ElementTypeTrackingRecord> typeComparisons,
    //    int?[] contentStepKeys)
    //{
    //    CMR? relationship = CMR.Equivalent;
    //    CMR? targetProfileRelationship = CMR.Equivalent;
    //    CMR? typeProfileRelationship = CMR.Equivalent;

    //    List<string> targetProfileMessages = [];
    //    List<string> typeProfileMessages = [];
    //    List<string> technicalMessages = [];
    //    List<string> userMessages = [];

    //    List<int> identicalTypesKeys = [];
    //    List<string> identicalTypesSymbols = [];
    //    List<int> equivalentTypesKeys = [];
    //    List<string> equivalentTypesSymbols = [];
    //    List<int> broaderTypesKeys = [];
    //    List<string> broaderTypesSymbols = [];
    //    List<int> narrowerTypesKeys = [];
    //    List<string> narrowerTypesSymbols = [];
    //    List<int> addedTypesKeys = [];
    //    List<string> addedTypesSymbols = [];
    //    List<int> removedTypesKeys = [];
    //    List<string> removedTypesSymbols = [];

    //    foreach (ElementTypeTrackingRecord etr in typeComparisons)
    //    {
    //        relationship = FhirDbComparer.ApplyRelationship(relationship, etr.Relationship);

    //        if (etr.TechnicalMessage is not null)
    //        {
    //            technicalMessages.Add(etr.TechnicalMessage);
    //        }

    //        if (etr.UserMessage is not null)
    //        {
    //            userMessages.Add(etr.UserMessage);
    //        }

    //        if (etr.IsIdentical)
    //        {
    //            identicalTypesKeys.Add(etr.SourceType!.Key);
    //            identicalTypesSymbols.Add(etr.SourceType!.Literal);

    //        }

    //        if (etr.IsEquivalent)
    //        {
    //            equivalentTypesKeys.Add(etr.SourceType!.Key);
    //            equivalentTypesSymbols.Add(etr.SourceType!.Literal);
    //        }
    //        else if (etr.IsBroaderThanTarget)
    //        {
    //            broaderTypesKeys.Add(etr.SourceType!.Key);
    //            broaderTypesSymbols.Add(etr.SourceType!.Literal);
    //        }
    //        else if (etr.IsNarrowerThanTarget)
    //        {
    //            narrowerTypesKeys.Add(etr.SourceType!.Key);
    //            narrowerTypesSymbols.Add(etr.SourceType!.Literal);
    //        }
    //        else if (etr.IsAdded)
    //        {
    //            addedTypesKeys.Add(etr.TargetType!.Key);
    //            addedTypesSymbols.Add(etr.TargetType!.Literal);
    //        }
    //        else if (etr.IsRemoved)
    //        {
    //            removedTypesKeys.Add(etr.SourceType!.Key);
    //            removedTypesSymbols.Add(etr.SourceType!.Literal);
    //        }

    //        if ((etr.SourceType?.TargetProfile is not null) || (etr.TargetType?.TargetProfile is not null))
    //        {
    //            targetProfileRelationship = FhirDbComparer.ApplyRelationship(targetProfileRelationship, etr.TargetProfileRelationship);
    //            if (etr.TargetProfileMessage is not null)
    //            {
    //                targetProfileMessages.Add(etr.TargetProfileMessage);
    //            }
    //        }

    //        if ((etr.SourceType?.TypeProfile is not null) || (etr.TargetType?.TypeProfile is not null))
    //        {
    //            typeProfileRelationship = FhirDbComparer.ApplyRelationship(typeProfileRelationship, etr.TypeProfileRelationship);
    //            if (etr.TypeProfileMessage is not null)
    //            {
    //                typeProfileMessages.Add(etr.TypeProfileMessage);
    //            }
    //        }
    //    }

    //    bool isIdentical = (identicalTypesKeys.Count == typeComparisons.Count) &&
    //        (sourceElement.Id == targetElement?.Id);

    //    if ((identicalTypesKeys.Count == typeComparisons.Count) ||
    //        (equivalentTypesKeys.Count == typeComparisons.Count))
    //    {
    //        relationship = CMR.Equivalent;
    //    }
    //    else if ((broaderTypesKeys.Count > 0) &&
    //        (narrowerTypesKeys.Count == 0) &&
    //        (removedTypesKeys.Count == 0))
    //    {
    //        relationship = CMR.SourceIsBroaderThanTarget;
    //    }
    //    else if ((narrowerTypesKeys.Count > 0) &&
    //        (broaderTypesKeys.Count == 0) &&
    //        (addedTypesKeys.Count == 0))
    //    {
    //        relationship = CMR.SourceIsNarrowerThanTarget;
    //    }
    //    else
    //    {
    //        relationship = CMR.RelatedTo;
    //    }

    //    string? targetProfileMessage = targetProfileMessages.Count == 0
    //        ? null
    //        : string.Join(' ', targetProfileMessages);

    //    string? typeProfileMessage = typeProfileMessages.Count == 0
    //        ? null
    //        : string.Join(' ', typeProfileMessages);

    //    string? technicalMessage = technicalMessages.Count == 0
    //        ? null
    //        : string.Join(' ', technicalMessages);

    //    string? userMessage = userMessages.Count == 0
    //        ? null
    //        : string.Join(' ', userMessages);

    //    return new()
    //    {
    //        Key = DbCollatedTypeComparison.GetIndex(),
    //        ElementComparisonKey = elementComparisonKey,

    //        Steps = Math.Abs(sourcePackage.DefinitionFhirSequence - targetPackage.DefinitionFhirSequence),

    //        SourceFhirPackageKey = sourcePackage.Key,
    //        SourceFhirSequence = sourcePackage.DefinitionFhirSequence,
    //        SourceElementKey = sourceElement.Key,
    //        SourceCollatedTypeKey = sourceCollatedType.Key,


    //        TargetFhirPackageKey = targetPackage.Key,
    //        TargetFhirSequence = targetPackage.DefinitionFhirSequence,
    //        TargetElementKey = targetElement?.Key,
    //        TargetCollatedTypeKey = targetCollatedType?.Key,

    //        ContentKeys = contentStepKeys,

    //        Relationship = relationship,
    //        TargetProfileRelationship = targetProfileRelationship,
    //        TargetProfileMessage = targetProfileMessage,
    //        TypeProfileRelationship = typeProfileRelationship,
    //        TypeProfileMessage = typeProfileMessage,

    //        NotMapped = targetElement is null,

    //        IsIdentical = isIdentical,

    //        IdenticalTypesKeys = identicalTypesKeys,
    //        IdenticalTypesSymbols = identicalTypesSymbols,
    //        EquivalentTypesKeys = equivalentTypesKeys,
    //        EquivalentTypesSymbols = equivalentTypesSymbols,
    //        BroaderTypesKeys = broaderTypesKeys,
    //        BroaderTypesSymbols = broaderTypesSymbols,
    //        NarrowerTypesKeys = narrowerTypesKeys,
    //        NarrowerTypesSymbols = narrowerTypesSymbols,
    //        AddedTypesKeys = addedTypesKeys,
    //        AddedTypesSymbols = addedTypesSymbols,
    //        RemovedTypesKeys = removedTypesKeys,
    //        RemovedTypesSymbols = removedTypesSymbols,

    //        TechnicalMessage = technicalMessage,
    //        UserMessage = userMessage,
    //    };

    //}
}
