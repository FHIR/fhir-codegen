using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.CompareTool;

public class StructureComparer
{
    internal class StructureComparisonTrackingRecord
    {
        public required FhirPackageComparisonPair PackagePair { get; init; }

        public required DbFhirPackage SourcePackage { get; init; }
        public required DbStructureDefinition SourceStructure { get; init; }
        public required DbFhirPackage TargetPackage { get; set; }
        public required DbStructureDefinition? TargetStructure { get; set; }
        public int? TargetStructureKey { get; set; } = null;

        public DbStructureMapping? ExplicitMapping { get; set; } = null;
        public DbMappingSourceFile? ExplicitMappingSource { get; set; } = null;

        public int? ComparisonRecordKey { get; set; } = null;
        public DbStructureComparison? ComparisonRecord { get; set; } = null;

        public DbStructureDefinition?[] Contents { get; set; } = new DbStructureDefinition?[6];
        public int?[] ContentKeys { get; set; } = new int?[6];
        public List<DbStructureComparison> ComparisonSteps { get; set; } = [];

        public StructureComparisonTrackingRecord Clone() => new()
        {
            PackagePair = this.PackagePair,
            SourcePackage = this.SourcePackage,
            SourceStructure = this.SourceStructure,
            TargetPackage = this.TargetPackage,
            TargetStructure = this.TargetStructure,
            TargetStructureKey = this.TargetStructureKey,
            ExplicitMapping = this.ExplicitMapping,
            ExplicitMappingSource = this.ExplicitMappingSource,
            ComparisonRecordKey = this.ComparisonRecordKey,
            ComparisonRecord = this.ComparisonRecord,
            Contents = this.Contents.Select(v => v).ToArray(),
            ContentKeys = this.ContentKeys.Select(v => v).ToArray(),
            ComparisonSteps = this.ComparisonSteps.Select(v => v).ToList(),
        };
    }

    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private DbComparisonCache<DbStructureComparison> _sdComparisonCache;
    private DbComparisonCache<DbElementComparison> _elementComparisonCache;
    private DbComparisonCache<DbElementTypeComparison> _elementTypeComparisonCache;

    private List<DbFhirPackage> _packages = [];

    private ElementComparer? _elementComparer = null;

    public StructureComparer(
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StructureComparer>();

        _db = db;

        _sdComparisonCache = new();
        _elementComparisonCache = new();
        _elementTypeComparisonCache = new();
    }

    public void CompareStructures(
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        // get the list of packages
        _packages = DbFhirPackage.SelectList(_db, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);

        List<FhirPackageComparisonPair> packagePairs = [];
        maxStepSize ??= _packages.Count - 1;

        // we want to process closer versions first, so we do a stepped approach
        for (int stepSize = 1; stepSize <= maxStepSize; stepSize++)
        {
            for (int i = 0; i < _packages.Count - stepSize; i++)
            {
                DbFhirPackage sourcePackage = _packages[i];
                DbFhirPackage targetPackage = _packages[i + stepSize];

                if ((specificPairs is null) ||
                    specificPairs.Contains((sourcePackage.DefinitionFhirSequence, targetPackage.DefinitionFhirSequence)))
                {
                    packagePairs.Add(new(sourcePackage, targetPackage));
                }

                if ((specificPairs is null) ||
                    specificPairs.Contains((targetPackage.DefinitionFhirSequence, sourcePackage.DefinitionFhirSequence)))
                {
                    packagePairs.Add(new(targetPackage, sourcePackage));
                }
            }
        }

        _elementComparer = new(
            _db,
            _loggerFactory,
            _elementComparisonCache,
            _elementTypeComparisonCache,
            _packages);

        // iterate over our pairs in the order we built them
        foreach (FhirPackageComparisonPair packagePair in packagePairs)
        {
            _logger.LogInformation($"Processing {packagePair.SourcePackageShortName} -> {packagePair.TargetPackageShortName}");
            string? inputRevision = UnsafeCodeInputStore.BeginComparison(_db, packagePair.SourcePackageKey, packagePair.TargetPackageKey);
            doComparison(packagePair);
            applyCachedChanges(packagePair);
            UnsafeCodeInputStore.CompleteComparison(_db, packagePair.SourcePackageKey, packagePair.TargetPackageKey, inputRevision);
        }
    }

    private void applyCachedChanges(FhirPackageComparisonPair packagePair)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        if (_sdComparisonCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_sdComparisonCache.ToAddCount} structure comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _sdComparisonCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_sdComparisonCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_sdComparisonCache.ToUpdateCount} structure comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _sdComparisonCache.ToUpdate.Update(_db);
        }

        if (_elementComparisonCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_elementComparisonCache.ToAddCount} element comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _elementComparisonCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_elementComparisonCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_elementComparisonCache.ToUpdateCount} element comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _elementComparisonCache.ToUpdate.Update(_db);
        }

        if (_elementTypeComparisonCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_elementTypeComparisonCache.ToAddCount} element type comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _elementTypeComparisonCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_elementTypeComparisonCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_elementTypeComparisonCache.ToUpdateCount} element type comparisons from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _elementTypeComparisonCache.ToUpdate.Update(_db);
        }


        _sdComparisonCache.Clear();
        _elementComparisonCache.Clear();
        _elementTypeComparisonCache.Clear();
    }

    private void doComparison(FhirPackageComparisonPair packagePair)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        int sourceIndex = sourcePackage.PackageArrayIndex;
        int targetIndex = targetPackage.PackageArrayIndex;

        int steps = Math.Abs(targetIndex - sourceIndex);

        foreach (FhirArtifactClassEnum artifactClass in getOrderedArtifactClasses())
        {
            List<DbStructureDefinition> sourceStructures = DbStructureDefinition.SelectList(
                _db,
                FhirPackageKey: sourcePackage.Key,
                ArtifactClass: artifactClass);
            _logger.LogInformation($" <<< processing {sourcePackage.ShortName} {artifactClass}s, count: {sourceStructures.Count}");

            // iterate over each structure in the source package
            foreach (DbStructureDefinition sourceSd in sourceStructures)
            {
                // skip structures we know we will not process
                if (XVerProcessor._exclusionSet.Contains(sourceSd.UnversionedUrl))
                {
                    continue;
                }

                // when we have a single step or primitive types, do the comparisons directly
                if ((steps == 1) ||
                    (sourceSd.ArtifactClass == FhirArtifactClassEnum.PrimitiveType))
                {
                    // discover targets
                    List<StructureComparisonTrackingRecord> trackingRecords = buildNeighborComparisonPaths(
                        packagePair,
                        sourceSd);

                    // do the comparisons
                    foreach (StructureComparisonTrackingRecord trackingRecord in trackingRecords)
                    {
                        doComparison(trackingRecord);
                    }
                }
                else
                {
                    // discover targets
                    List<StructureComparisonTrackingRecord> trackingRecords = discoverTransitivePaths(
                        packagePair,
                        sourceSd);

                    // do the comparisons transitively
                    foreach (StructureComparisonTrackingRecord trackingRecord in trackingRecords)
                    {
                        doTransitiveComparison(trackingRecord);
                    }
                }
            }
        }

        return;

        FhirArtifactClassEnum[] getOrderedArtifactClasses() => [
            FhirArtifactClassEnum.PrimitiveType,
            FhirArtifactClassEnum.ComplexType,
            FhirArtifactClassEnum.Resource,
            FhirArtifactClassEnum.Profile,
            ];
    }

    private void doTransitiveComparison(StructureComparisonTrackingRecord trackingRecord)
    {
        // get our key (if necessary)
        trackingRecord.ComparisonRecordKey ??= DbStructureComparison.GetIndex();

        // build element comparisons for everything but primitives
        List<DbElementComparison> elementComparisons =
            (trackingRecord.SourceStructure.ArtifactClass == FhirArtifactClassEnum.PrimitiveType) ||
            (trackingRecord.TargetStructure?.ArtifactClass == FhirArtifactClassEnum.PrimitiveType)
            ? []
            : _elementComparer!.DoTransitiveElementComparisons(trackingRecord);

        // determine the relationship based on the comparison steps
        CMR? sdRelationship = CMR.Equivalent;
        CMR? sdConceptRelationship = CMR.Equivalent;
        CMR? sdValueRelationship = CMR.Equivalent;
        bool isIdentical = true;
        bool relativePathsAreIdentical = true;
        string? technicalMessage = null;

        foreach (DbStructureComparison comparisonStep in trackingRecord.ComparisonSteps)
        {
            sdRelationship = FhirDbComparer.ApplyRelationship(sdRelationship, comparisonStep.Relationship);
            sdConceptRelationship = FhirDbComparer.ApplyRelationship(sdConceptRelationship, comparisonStep.ConceptDomainRelationship);
            sdValueRelationship = FhirDbComparer.ApplyRelationship(sdValueRelationship, comparisonStep.ValueDomainRelationship);
            isIdentical = isIdentical && (comparisonStep.IsIdentical == true);
            relativePathsAreIdentical = relativePathsAreIdentical && (comparisonStep.ElementRelativePathsAreIdentical == true);
        }

        // check for an explicit mapping of this pair
        DbStructureMapping? sdMapping = DbStructureMapping.SelectSingle(
            _db,
            IsFallback: false,
            SourceFhirPackageKey: trackingRecord.SourcePackage.Key,
            SourceStructureKey: trackingRecord.SourceStructure.Key,
            TargetFhirPackageKey: trackingRecord.TargetPackage.Key,
            TargetStructureKey: trackingRecord.TargetStructureKey);

        if (sdMapping is not null)
        {
            int? mappingSourceKey = sdMapping.ConceptMapSourceKey ?? sdMapping.FmlSourceKey ?? null;

            trackingRecord.ExplicitMapping = sdMapping;
            trackingRecord.ExplicitMappingSource = mappingSourceKey is null
                ? null
                : DbMappingSourceFile.SelectSingle(_db, Key: mappingSourceKey);
            sdRelationship = sdMapping.Relationship ?? sdRelationship;

            technicalMessage = $"Using explicit mapping" +
                $" from `{trackingRecord.SourceStructure.VersionedUrl}`" +
                $" to `{trackingRecord.TargetStructure?.VersionedUrl}`" +
                $" in `{trackingRecord.ExplicitMappingSource?.Url}` (`{trackingRecord.ExplicitMappingSource?.Filename}`)";
        }

        // create our structure comparison
        DbStructureComparison sdComparison = createStructureComparison(
            trackingRecord,
            isIdentical,
            relativePathsAreIdentical,
            relationship: sdRelationship,
            cdRelationship: sdConceptRelationship,
            vdRelationship: sdValueRelationship,
            technicalMessage: technicalMessage,
            userMessage: null,
            contentStepKeys: trackingRecord.ContentKeys);

        _sdComparisonCache.CacheAdd(sdComparison);
    }


    private void doComparison(StructureComparisonTrackingRecord trackingRecord)
    {
        DbStructureDefinition sourceSd = trackingRecord.SourceStructure;
        DbStructureDefinition? targetSd = trackingRecord.TargetStructure;

        // do element comparisons on everything but primitives
        List<DbElementComparison> elementComparisons =
            (sourceSd.ArtifactClass == FhirArtifactClassEnum.PrimitiveType) ||
            (targetSd?.ArtifactClass == FhirArtifactClassEnum.PrimitiveType)
            ? []
            : _elementComparer!.DoElementComparisons(trackingRecord);

        // if there is no target structure, every element is a no map
        if (targetSd is null)
        {
            bool sdAreIdentical = elementComparisons.All(ec => ec.IsIdentical == true);
            bool elementRelativePathsAreIdentical = elementComparisons.All(ec => ec.RelativePathsAreIdentical == true);

            // create our structure comparison
            DbStructureComparison noMapComparison = createStructureComparison(
                trackingRecord,
                sdAreIdentical,
                elementRelativePathsAreIdentical,
                relationship: null,
                cdRelationship: null,
                vdRelationship: null,
                technicalMessage: null,
                userMessage: null,
                contentStepKeys: getKeyArray(
                    trackingRecord.SourcePackage,
                    sourceSd.Key,
                    trackingRecord.TargetPackage,
                    null));

            _sdComparisonCache.CacheAdd(noMapComparison);

            return;
        }

        List<string> technicalMessages = [];
        if (trackingRecord.ExplicitMapping?.Comments is not null)
        {
            technicalMessages.Add(trackingRecord.ExplicitMapping.Comments);
        }

        // determine the relationship based on the element comparisons
        CMR? sdRelationship = trackingRecord.ExplicitMapping?.Relationship ?? CMR.Equivalent;
        if (elementComparisons.Any(ec => ec.NotMapped) ||
            elementComparisons.Any(ec => ec.Relationship == CMR.SourceIsBroaderThanTarget))
        {
            sdRelationship = FhirDbComparer.ApplyRelationship(sdRelationship, CMR.SourceIsBroaderThanTarget);
            technicalMessages.Add("One or more elements are not mapped or broader than their target element.");
        }

        if (elementComparisons.Any(ec => ec.Relationship == CMR.SourceIsNarrowerThanTarget))
        {
            sdRelationship = FhirDbComparer.ApplyRelationship(sdRelationship, CMR.SourceIsNarrowerThanTarget);
            technicalMessages.Add("One or more elements are narrower than their target element.");
        }

        CMR? sdConceptRelationship = CMR.Equivalent;
        if (elementComparisons.Any(ec => ec.NotMapped) ||
            elementComparisons.Any(ec => ec.ConceptDomainRelationship == CMR.SourceIsBroaderThanTarget))
        {
            sdConceptRelationship = FhirDbComparer.ApplyRelationship(sdConceptRelationship, CMR.SourceIsBroaderThanTarget);
        }

        if (elementComparisons.Any(ec => ec.ConceptDomainRelationship == CMR.SourceIsNarrowerThanTarget))
        {
            sdConceptRelationship = FhirDbComparer.ApplyRelationship(sdConceptRelationship, CMR.SourceIsNarrowerThanTarget);
        }

        CMR? sdValueRelationship = CMR.Equivalent;
        if (elementComparisons.Any(ec => ec.NotMapped) ||
            elementComparisons.Any(ec => ec.ValueDomainRelationship == CMR.SourceIsBroaderThanTarget))
        {
            sdValueRelationship = FhirDbComparer.ApplyRelationship(sdValueRelationship, CMR.SourceIsBroaderThanTarget);
        }

        if (elementComparisons.Any(ec => ec.ValueDomainRelationship == CMR.SourceIsNarrowerThanTarget))
        {
            sdValueRelationship = FhirDbComparer.ApplyRelationship(sdValueRelationship, CMR.SourceIsNarrowerThanTarget);
        }

        bool isIdentical = elementComparisons.All(ec => ec.IsIdentical == true);
        if (isIdentical)
        {
            technicalMessages.Add("All elements are identical.");
        }

        bool relativePathsAreIdentical = elementComparisons.All(ec => ec.RelativePathsAreIdentical == true);
        if (relativePathsAreIdentical)
        {
            technicalMessages.Add("All element relative paths are identical.");
        }

        // include the element counts in the relationship (structures with more elements are broader)
        if (sourceSd.SnapshotCount > targetSd.SnapshotCount)
        {
            sdRelationship = FhirDbComparer.ApplyRelationship(sdRelationship, CMR.SourceIsBroaderThanTarget);
            technicalMessages.Add($"Source structure has more elements ({sourceSd.SnapshotCount}) than target structure ({targetSd.SnapshotCount}).");
        }
        else if (sourceSd.SnapshotCount < targetSd.SnapshotCount)
        {
            sdRelationship = FhirDbComparer.ApplyRelationship(sdRelationship, CMR.SourceIsNarrowerThanTarget);
            technicalMessages.Add($"Source structure has fewer elements ({sourceSd.SnapshotCount}) than target structure ({targetSd.SnapshotCount}).");
        }

        // create our structure comparison
        DbStructureComparison sdComparison = createStructureComparison(
            trackingRecord,
            isIdentical,
            relativePathsAreIdentical,
            relationship: sdRelationship,
            cdRelationship: sdConceptRelationship,
            vdRelationship: sdValueRelationship,
            technicalMessage: technicalMessages.Count == 0 ? null : string.Join('\n', technicalMessages),
            userMessage: trackingRecord.ExplicitMapping?.Comments,
            contentStepKeys: getKeyArray(
                trackingRecord.SourcePackage,
                sourceSd.Key,
                trackingRecord.TargetPackage,
                targetSd.Key));

        _sdComparisonCache.CacheAdd(sdComparison);
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

    private List<StructureComparisonTrackingRecord> discoverTransitivePaths(
        FhirPackageComparisonPair packagePair,
        DbStructureDefinition sourceSd)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        int sourceIndex = sourcePackage.PackageArrayIndex;
        int targetIndex = targetPackage.PackageArrayIndex;

        int steps = Math.Abs(targetIndex - sourceIndex);

        int increment = sourceIndex < targetIndex ? 1 : -1;

        int currentSourceIndex = sourceIndex;
        int currentTargetIndex = sourceIndex + increment;

        List<int> currentStructureKeys = [sourceSd.Key];

        Dictionary<int, List<DbStructureComparison>> stepComparisons = [];

        // build the comparisons possible at each step
        for (int step = 0; step < steps; step++)
        {
            DbFhirPackage fromPackage = _packages[currentSourceIndex];
            DbFhirPackage toPackage = _packages[currentTargetIndex];

            // get the existing comparisons for this step
            List<DbStructureComparison> currentComparisons = DbStructureComparison.SelectList(
                _db,
                SourceFhirPackageKey: fromPackage.Key,
                TargetFhirPackageKey: toPackage.Key,
                SourceStructureKeyValues: currentStructureKeys);

            // add our current records to our dictionary to reconcile later
            stepComparisons[step] = currentComparisons;

            // update our source structures for the next iteration
            currentStructureKeys = currentComparisons
                .Where(r => r.TargetStructureKey is not null)
                .Select(r => r.TargetStructureKey!.Value)
                .Distinct()
                .ToList();

            // increment our indices
            currentSourceIndex += increment;
            currentTargetIndex += increment;
        }

        ILookup<int, DbStructureComparison> comparisonsBySourceKey = stepComparisons.Values
            .SelectMany(l => l)
            .ToLookup(c => c.SourceContentKey);

        Dictionary<int, DbFhirPackage> packages = _packages.ToDictionary(p => p.Key);

        List<StructureComparisonTrackingRecord> trackingRecords = [];

        // iterate over the initial comparisons and build the path chains transitively
        foreach (DbStructureComparison initialComparison in stepComparisons[0])
        {
            currentSourceIndex = sourceIndex;
            currentTargetIndex = sourceIndex + increment;

            StructureComparisonTrackingRecord tr = new()
            {
                PackagePair = packagePair,
                SourcePackage = sourcePackage,
                SourceStructure = sourceSd,
                TargetPackage = targetPackage,
                TargetStructure = null,
                TargetStructureKey = initialComparison.TargetStructureKey,
                ComparisonRecordKey = initialComparison.Key,
            };

            tr.Contents[sourceIndex] = sourceSd;
            tr.ContentKeys[sourceIndex] = sourceSd.Key;

            tr.ContentKeys[currentTargetIndex] = initialComparison.TargetStructureKey;

            tr.ComparisonSteps.Add(initialComparison);

            List<StructureComparisonTrackingRecord> currentTrackingRecords = [tr];

            // follow the steps and continue building our tracking records
            for (int step = 1; step < steps; step++)
            {
                if (!stepComparisons.ContainsKey(step))
                {
                    break;
                }

                currentSourceIndex += increment;
                currentTargetIndex += increment;

                List<StructureComparisonTrackingRecord> nextTrackingRecords = [];

                // iterate over the current tracking records to expand them
                foreach (StructureComparisonTrackingRecord currentTr in currentTrackingRecords)
                {
                    int? currentTargetSdKey = currentTr.ContentKeys[currentSourceIndex];
                    if (currentTargetSdKey is null)
                    {
                        continue;
                    }

                    // get the possible next comparisons
                    IEnumerable<DbStructureComparison> possibleNextComparisons = comparisonsBySourceKey[currentTargetSdKey.Value];

                    // iterate over the possible next comparisons to build new tracking records
                    foreach (DbStructureComparison nextComparison in possibleNextComparisons)
                    {
                        StructureComparisonTrackingRecord nextTr = currentTr.Clone();

                        nextTr.TargetStructureKey = nextComparison.TargetStructureKey;
                        nextTr.ComparisonRecordKey = nextComparison.Key;
                        nextTr.ContentKeys[currentTargetIndex] = nextComparison.TargetStructureKey;
                        nextTr.ComparisonSteps.Add(nextComparison);
                        nextTrackingRecords.Add(nextTr);
                    }
                }

                currentTrackingRecords = nextTrackingRecords;
            }

            // process the completed tracking record
            foreach (StructureComparisonTrackingRecord currentTr in currentTrackingRecords)
            {
                if ((currentTr.TargetStructureKey is not null) &&
                    (currentTr.TargetStructure is null))
                {
                    currentTr.TargetStructure = DbStructureDefinition.SelectSingle(
                        _db,
                        Key: currentTr.TargetStructureKey.Value);
                }

                // need to create a new comparison record for this full path
                currentTr.ComparisonRecordKey = null;
                currentTr.ComparisonRecord = null;
            }

            // add the completed tracking records
            trackingRecords.AddRange(currentTrackingRecords);
        }

        return trackingRecords;
    }

    private List<StructureComparisonTrackingRecord> buildNeighborComparisonPaths(
        FhirPackageComparisonPair packagePair,
        DbStructureDefinition sourceSd)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        List<StructureComparisonTrackingRecord> trackingRecords = [];
        int sourceIndex = sourcePackage.PackageArrayIndex;
        int targetIndex = targetPackage.PackageArrayIndex;

        // check for explicit mappings for this structure to the target package
        List<DbStructureMapping> mappings = DbStructureMapping.SelectList(
            _db,
            IsFallback: false,
            SourceFhirPackageKey: sourcePackage.Key,
            SourceStructureKey: sourceSd.Key,
            TargetFhirPackageKey: targetPackage.Key);

        HashSet<int> targetKeys = [];
        HashSet<string> targetIds = [];
        HashSet<string> targetUrls = [];

        // iterate over the mappings to add to our tracking records
        foreach (DbStructureMapping mapping in mappings)
        {
            // resolve the target structure (if necessary)
            DbStructureDefinition? targetSd = mapping.TargetStructureKey is null
                ? null
                : DbStructureDefinition.SelectSingle(_db, Key: mapping.TargetStructureKey);

            if ((targetSd is null) &&
                (mapping.TargetStructureKey is not null))
            {
                throw new Exception($"Unable to resolve target structure with key {mapping.TargetStructureKey} for mapping {mapping.Key}");
            }

            if (targetSd is not null)
            {
                targetIds.Add(targetSd.Id);
                targetUrls.Add(targetSd.UnversionedUrl);
                if (!targetKeys.Add(targetSd.Key))
                {
                    //_logger.LogError($"Duplicate target structure key {targetSd.Key} for fallbackMapping {fallbackMapping.Key}");
                    //continue;
                    throw new Exception($"Duplicate target structure key {targetSd.Key} for mapping {mapping.Key}");
                }
            }

            int? mappingSourceKey = mapping.ConceptMapSourceKey ?? mapping.FmlSourceKey ?? null;

            StructureComparisonTrackingRecord tr = new()
            {
                PackagePair = packagePair,
                SourcePackage = sourcePackage,
                SourceStructure = sourceSd,
                TargetPackage = targetPackage,
                TargetStructure = targetSd,
                ExplicitMapping = mapping,
                ExplicitMappingSource = mappingSourceKey is null ? null : DbMappingSourceFile.SelectSingle(_db, Key: mappingSourceKey),
            };

            tr.Contents[sourceIndex] = sourceSd;
            tr.Contents[targetIndex] = targetSd;

            trackingRecords.Add(tr);
        }

        // check if there is a structure in the target package with the same ID
        DbStructureDefinition? possibleTargetSd = DbStructureDefinition.SelectSingle(
            _db,
            FhirPackageKey: targetPackage.Key,
            Id: sourceSd.Id);
        if ((possibleTargetSd is not null) &&
            targetKeys.Add(possibleTargetSd.Key))
        {
            targetIds.Add(possibleTargetSd.Id);
            targetUrls.Add(possibleTargetSd.UnversionedUrl);

            StructureComparisonTrackingRecord tr = new()
            {
                PackagePair = packagePair,
                SourcePackage = sourcePackage,
                SourceStructure = sourceSd,
                TargetPackage = targetPackage,
                TargetStructure = possibleTargetSd,
                ExplicitMapping = null,
                ExplicitMappingSource = null,
            };

            tr.Contents[sourceIndex] = sourceSd;
            tr.Contents[targetIndex] = possibleTargetSd;

            trackingRecords.Add(tr);
        }

        // check if there is a structure in the target package with the same URL
        possibleTargetSd = DbStructureDefinition.SelectSingle(
            _db,
            FhirPackageKey: targetPackage.Key,
            UnversionedUrl: sourceSd.UnversionedUrl);
        if ((possibleTargetSd is not null) &&
            targetKeys.Add(possibleTargetSd.Key))
        {
            targetIds.Add(possibleTargetSd.Id);
            targetUrls.Add(possibleTargetSd.UnversionedUrl);

            StructureComparisonTrackingRecord tr = new()
            {
                PackagePair = packagePair,
                SourcePackage = sourcePackage,
                SourceStructure = sourceSd,
                TargetPackage = targetPackage,
                TargetStructure = possibleTargetSd,
                ExplicitMapping = null,
                ExplicitMappingSource = null,
            };

            tr.Contents[sourceIndex] = sourceSd;
            tr.Contents[targetIndex] = possibleTargetSd;

            trackingRecords.Add(tr);
        }

        // if we have no targets, check for a matching name too
        if (trackingRecords.Count == 0)
        {
            possibleTargetSd = DbStructureDefinition.SelectSingle(
                _db,
                FhirPackageKey: targetPackage.Key,
                Name: sourceSd.Name);
            if ((possibleTargetSd is not null) &&
                targetKeys.Add(possibleTargetSd.Key))
            {
                targetIds.Add(possibleTargetSd.Id);
                targetUrls.Add(possibleTargetSd.UnversionedUrl);

                StructureComparisonTrackingRecord tr = new()
                {
                    PackagePair = packagePair,
                    SourcePackage = sourcePackage,
                    SourceStructure = sourceSd,
                    TargetPackage = targetPackage,
                    TargetStructure = possibleTargetSd,
                };

                tr.Contents[sourceIndex] = sourceSd;
                tr.Contents[targetIndex] = possibleTargetSd;

                trackingRecords.Add(tr);
            }
        }

        // check to see if we have any inverse mappings from the target to the source
        if (trackingRecords.Count == 0)
        {
            List<DbStructureMapping> inverseMappings = DbStructureMapping.SelectList(
                _db,
                IsFallback: false,
                SourceFhirPackageKey: targetPackage.Key,
                TargetFhirPackageKey: sourcePackage.Key,
                TargetStructureKey: sourceSd.Key);

            // iterate over the mappings to add to our tracking records
            foreach (DbStructureMapping inverseMapping in inverseMappings)
            {
                // resolve the source structure
                DbStructureDefinition? inverseSourceSd = DbStructureDefinition.SelectSingle(_db, Key: inverseMapping.SourceStructureKey);

                if (inverseSourceSd is null)
                {
                    throw new Exception($"Unable to resolve structure with key {inverseMapping.SourceStructureKey} for mapping {inverseMapping.Key}");
                }

                targetIds.Add(inverseSourceSd.Id);
                targetUrls.Add(inverseSourceSd.UnversionedUrl);
                if (!targetKeys.Add(inverseSourceSd.Key))
                {
                    throw new Exception($"Duplicate target structure key {inverseSourceSd.Key} for mapping {inverseMapping.Key}");
                }

                DbMappingSourceFile? mappingSource = inverseMapping.ConceptMapSourceKey is not null
                    ? DbMappingSourceFile.SelectSingle(_db, Key: inverseMapping.ConceptMapSourceKey)
                    : inverseMapping.FmlSourceKey is not null
                    ? DbMappingSourceFile.SelectSingle(_db, Key: inverseMapping.FmlSourceKey)
                    : null;

                StructureComparisonTrackingRecord tr = new()
                {
                    PackagePair = packagePair,
                    SourcePackage = sourcePackage,
                    SourceStructure = sourceSd,
                    TargetPackage = targetPackage,
                    TargetStructure = inverseSourceSd,
                    ExplicitMapping = inverseMapping,
                    ExplicitMappingSource = mappingSource,
                };

                tr.Contents[sourceIndex] = sourceSd;
                tr.Contents[targetIndex] = inverseSourceSd;

                trackingRecords.Add(tr);
            }
        }

        // TODO: I do not think we want to build the comparisons for fallback mappings...

        //// check for fallback mappings for this structure to the target package if necessary
        //if (trackingRecords.Count == 0)
        //{
        //    List<DbStructureMapping> fallbackMappings = DbStructureMapping.SelectList(
        //        _db,
        //        IsFallback: true,
        //        SourceFhirPackageKey: sourcePackage.Key,
        //        SourceStructureKey: sourceSd.Key,
        //        TargetFhirPackageKey: targetPackage.Key);

        //    // iterate over the mappings to add to our tracking records
        //    foreach (DbStructureMapping fallbackMapping in fallbackMappings)
        //    {
        //        // resolve the target structure (if necessary)
        //        DbStructureDefinition? targetSd = fallbackMapping.TargetStructureKey is null
        //            ? null
        //            : DbStructureDefinition.SelectSingle(_db, Key: fallbackMapping.TargetStructureKey);

        //        if ((targetSd is null) &&
        //            (fallbackMapping.TargetStructureKey is not null))
        //        {
        //            throw new Exception($"Unable to resolve target structure with key {fallbackMapping.TargetStructureKey} for mapping {fallbackMapping.Key}");
        //        }

        //        if (targetSd is not null)
        //        {
        //            targetIds.Add(targetSd.Id);
        //            targetUrls.Add(targetSd.UnversionedUrl);
        //            if (!targetKeys.Add(targetSd.Key))
        //            {
        //                //_logger.LogError($"Duplicate target structure key {targetSd.Key} for fallbackMapping {fallbackMapping.Key}");
        //                //continue;
        //                throw new Exception($"Duplicate target structure key {targetSd.Key} for mapping {fallbackMapping.Key}");
        //            }
        //        }

        //        int? mappingSourceKey = fallbackMapping.ConceptMapSourceKey ?? fallbackMapping.FmlSourceKey ?? null;

        //        StructureComparisonTrackingRecord tr = new()
        //        {
        //            PackagePair = packagePair,
        //            SourcePackage = sourcePackage,
        //            SourceStructure = sourceSd,
        //            TargetPackage = targetPackage,
        //            TargetStructure = targetSd,
        //            ExplicitMapping = fallbackMapping,
        //            ExplicitMappingSource = mappingSourceKey is null ? null : DbMappingSourceFile.SelectSingle(_db, Key: mappingSourceKey),
        //        };

        //        tr.Contents[sourceIndex] = sourceSd;
        //        tr.Contents[targetIndex] = targetSd;

        //        trackingRecords.Add(tr);
        //    }
        //}

        // if we still have no targets, treat as a no map
        if (trackingRecords.Count == 0)
        {
            StructureComparisonTrackingRecord tr = new()
            {
                PackagePair = packagePair,
                SourcePackage = sourcePackage,
                SourceStructure = sourceSd,
                TargetPackage = targetPackage,
                TargetStructure = null,
            };

            tr.Contents[sourceIndex] = sourceSd;

            trackingRecords.Add(tr);
        }

        return trackingRecords;
    }

    private DbStructureComparison createStructureComparison(
        StructureComparisonTrackingRecord sdTrackingRecord,
        bool isIdentical,
        bool? relativePathsAreIdentical,
        CMR? relationship,
        CMR? cdRelationship,
        CMR? vdRelationship,
        string? technicalMessage,
        string? userMessage,
        int?[] contentStepKeys)
    {
        sdTrackingRecord.ComparisonRecordKey ??= DbStructureComparison.GetIndex();

        return new()
        {
            Key = sdTrackingRecord.ComparisonRecordKey.Value,
            StructureMappingKey = sdTrackingRecord.ExplicitMapping?.Key,

            Steps = Math.Abs(sdTrackingRecord.SourcePackage.DefinitionFhirSequence - sdTrackingRecord.TargetPackage.DefinitionFhirSequence),

            SourceFhirPackageKey = sdTrackingRecord.SourcePackage.Key,
            SourceFhirSequence = sdTrackingRecord.SourcePackage.DefinitionFhirSequence,
            SourceStructureKey = sdTrackingRecord.SourceStructure.Key,
            SourceCanonicalUnversioned = sdTrackingRecord.SourceStructure.UnversionedUrl,
            SourceCanonicalVersioned = sdTrackingRecord.SourceStructure.VersionedUrl,
            SourceId = sdTrackingRecord.SourceStructure.Id,
            SourceName = sdTrackingRecord.SourceStructure.Name,
            SourceVersion = sdTrackingRecord.SourceStructure.Version,

            TargetFhirPackageKey = sdTrackingRecord.TargetPackage.Key,
            TargetFhirSequence = sdTrackingRecord.TargetPackage.DefinitionFhirSequence,
            TargetStructureKey = sdTrackingRecord.TargetStructure?.Key,
            TargetCanonicalUnversioned = sdTrackingRecord.TargetStructure?.UnversionedUrl,
            TargetCanonicalVersioned = sdTrackingRecord.TargetStructure?.VersionedUrl,
            TargetId = sdTrackingRecord.TargetStructure?.Id,
            TargetName = sdTrackingRecord.TargetStructure?.Name,
            TargetVersion = sdTrackingRecord.TargetStructure?.Version,

            ContentKeys = contentStepKeys,

            Relationship = relationship,
            ConceptDomainRelationship = cdRelationship,
            ValueDomainRelationship = vdRelationship,

            NotMapped = sdTrackingRecord.TargetStructure is null,
            IsBasedOnFallbackMapping = sdTrackingRecord.ExplicitMapping?.IsFallback == true,

            IsIdentical = isIdentical,
            ElementRelativePathsAreIdentical = relativePathsAreIdentical,

            TechnicalMessage = technicalMessage,
            UserMessage = userMessage,
        };
    }
}
