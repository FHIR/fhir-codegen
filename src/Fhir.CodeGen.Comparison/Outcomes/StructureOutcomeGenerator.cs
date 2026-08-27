using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;


namespace Fhir.CodeGen.Comparison.Outcomes;

public class StructureOutcomeGenerator
{
    public class StructureOutcomeTrackingRecord
    {
        public required DbStructureDefinition SourceStructure { get; set; }
        public required DbStructureComparison? StructureComparison { get; set; }
        public required DbStructureDefinition? TargetStructure { get; set; }

        public DbElement? SourceRootElement { get; set; } = null;
        public DbElement? TargetRootElement { get; set; } = null;

        public required int StructureOutcomeKey { get; set; }

        public List<DbElementOutcome> ElementOutcomes { get; set; } = [];

        public bool IsFullyMappedAcrossAllTargets { get; set; } = false;
        public bool IsFullyMappedToThisTarget { get; set; } = false;

        public List<string> Messages { get; } = [];
    }

    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private List<DbFhirPackage> _packages = [];

    private DbRecordCache<DbStructureOutcome> _sdOutcomeCache;
    private DbRecordCache<DbElementOutcome> _edOutcomeCache;
    private DbRecordCache<DbElementOutcomeTarget> _edOutcomeTargetCache;

    public StructureOutcomeGenerator(
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StructureOutcomeGenerator>();

        _db = db;

        _sdOutcomeCache = new();
        _edOutcomeCache = new();
        _edOutcomeTargetCache = new();
    }

    public void CreateOutcomesForStructures(
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

                packagePairs.Add(new(sourcePackage, targetPackage));
                packagePairs.Add(new(targetPackage, sourcePackage));
            }
        }

        // iterate over our pairs in the order we built them
        foreach (FhirPackageComparisonPair packagePair in packagePairs)
        {
            if ((specificPairs is not null) &&
                !specificPairs.Contains((packagePair.SourceFhirSequence, packagePair.TargetFhirSequence)))
            {
                continue;
            }

            // create our element outcome generator for this package pair
            ElementOutcomeGenerator elementOutcomeGenerator = new(
                _db,
                _loggerFactory,
                packagePair,
                _edOutcomeCache,
                _edOutcomeTargetCache);

            buildOutcomes(packagePair, elementOutcomeGenerator);
            applyCachedChanges(packagePair);

            elementOutcomeGenerator.DoPostProcessing(packagePair);
            //updateOutcomeContextsForContentReferences(packagePair);
            applyCachedChanges(packagePair);
        }
    }
    private void applyCachedChanges(FhirPackageComparisonPair packagePair)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        if (_sdOutcomeCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_sdOutcomeCache.ToAddCount} structure outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _sdOutcomeCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_sdOutcomeCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_sdOutcomeCache.ToUpdateCount} structure outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _sdOutcomeCache.ToUpdate.Update(_db);
        }

        if (_edOutcomeCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_edOutcomeCache.ToAddCount} element outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _edOutcomeCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_edOutcomeCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_edOutcomeCache.ToUpdateCount} element outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _edOutcomeCache.ToUpdate.Update(_db);
        }

        if (_edOutcomeTargetCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_edOutcomeTargetCache.ToAddCount} element outcome targets from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _edOutcomeTargetCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_edOutcomeTargetCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_edOutcomeTargetCache.ToUpdateCount} element outcome targets from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _edOutcomeTargetCache.ToUpdate.Update(_db);
        }

        _sdOutcomeCache.Clear();
        _edOutcomeCache.Clear();
        _edOutcomeTargetCache.Clear();
    }

    private void buildOutcomes(
        FhirPackageComparisonPair packagePair,
        ElementOutcomeGenerator elementOutcomeGenerator)
    {
        _logger.LogInformation($"Generating Structure Outcomes for {packagePair.SourcePackageShortName}->{packagePair.TargetPackageShortName}...");

        // just get all the source and target structures and elements now to avoid hitting the db so much
        Dictionary<int, DbStructureDefinition> allSourceStructures = DbStructureDefinition.SelectDict(_db, FhirPackageKey: packagePair.SourcePackageKey);
        Dictionary<int, DbStructureDefinition> allTargetStructures = DbStructureDefinition.SelectDict(_db, FhirPackageKey: packagePair.TargetPackageKey);

        Dictionary<string, DbElement> sourceStructureRootElements = DbElement.SelectList(
            _db,
            FhirPackageKey: packagePair.SourcePackageKey,
            ResourceFieldOrder: 0)
            .ToDictionary(ed => ed.Id);
        Dictionary<string, DbElement> targetStructureRootElements = DbElement.SelectList(
            _db,
            FhirPackageKey: packagePair.TargetPackageKey,
            ResourceFieldOrder: 0)
            .ToDictionary(ed => ed.Id);

        List <DbStructureComparison> sdComparisons = DbStructureComparison.SelectList(
            _db,
            SourceFhirPackageKey: packagePair.SourcePackageKey,
            TargetFhirPackageKey: packagePair.TargetPackageKey);

        _logger.LogInformation(
            $"Structure comparisons: {sdComparisons.Count}, from" +
            $" {allSourceStructures.Count} Structures" +
            $" to {allTargetStructures.Count} Structures");

        // create lookups of our comparisons that we need to generate outcomes
        ILookup<int, DbStructureComparison> sdComparsionsBySourceKey = sdComparisons.ToLookup(c => c.SourceContentKey);
        ILookup<int, DbStructureComparison> sdComparsionsByTargetKey = sdComparisons
            .Where(c => c.TargetContentKey is not null)
            .ToLookup(c => c.TargetContentKey!.Value);

        //ILookup<int, DbStructureComparison> sdNoMapComparisons = sdComparisons
        //    .Where(c => (c.NotMapped == true) || (c.TargetContentKey is null))
        //    .ToLookup(c => c.SourceContentKey);

        // iterate over our source structures
        foreach (DbStructureDefinition sourceSd in allSourceStructures.Values)
        {
            // skip primitive structures
            if (sourceSd.ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType)
            {
                continue;
            }

            Dictionary<int, StructureOutcomeTrackingRecord> trackingRecords = [];

            List<DbStructureComparison> structureComparisons = sdComparsionsBySourceKey[sourceSd.Key]
                .Where(sc => sc.TargetFhirPackageKey == packagePair.TargetPackageKey)
                .ToList();
            if (structureComparisons.Count == 0)
            {
                trackingRecords.Add(
                    0,
                    new()
                    {
                        SourceStructure = sourceSd,
                        SourceRootElement = sourceStructureRootElements.GetValueOrDefault(sourceSd.Id),
                        StructureComparison = null,
                        TargetStructure = null,
                        StructureOutcomeKey = DbStructureOutcome.GetIndex(),
                        IsFullyMappedAcrossAllTargets = true,
                        IsFullyMappedToThisTarget = false,
                    });

                //// create our structure no-map outcome
                //DbStructureOutcome noMapSdOutcome = createNoMapStructureOutcome(
                //    packagePair,
                //    sourceSd,
                //    structureOutcomeKey: null,
                //    structureComparisonKey: null,
                //    comments: null);

                //// create our element no-map outcomes
                //elementOutcomeGenerator.ProcessNoMapStructure(sourceSd, noMapSdOutcome);

                //continue;
            }

            // build our tracking records for valid comparisons, be optimistic that all elements will be fully mapped
            foreach (DbStructureComparison structureComparison in structureComparisons)
            {
                if (structureComparison.NotMapped ||
                    (structureComparison.TargetContentKey is null) ||
                    !allTargetStructures.TryGetValue(structureComparison.TargetContentKey.Value, out DbStructureDefinition? targetSd))
                {
                    trackingRecords.Add(
                        0,
                        new()
                        {
                            SourceStructure = sourceSd,
                            SourceRootElement = sourceStructureRootElements.GetValueOrDefault(sourceSd.Id),
                            StructureComparison = structureComparison,
                            TargetStructure = null,
                            StructureOutcomeKey = DbStructureOutcome.GetIndex(),
                            IsFullyMappedAcrossAllTargets = true,
                            IsFullyMappedToThisTarget = false,
                        });
                    continue;
                }

                trackingRecords.Add(
                    structureComparison.TargetContentKey!.Value,
                    new()
                    {
                        SourceStructure = sourceSd,
                        SourceRootElement = sourceStructureRootElements.GetValueOrDefault(sourceSd.Id),
                        StructureComparison = structureComparison,
                        TargetStructure = targetSd,
                        TargetRootElement = targetStructureRootElements.GetValueOrDefault(targetSd.Id),
                        StructureOutcomeKey = DbStructureOutcome.GetIndex(),
                        IsFullyMappedAcrossAllTargets = true,
                        IsFullyMappedToThisTarget = true,
                    });
            }

            // process elements to determine how elements map across our target structures, if neither side is a primitive type
            elementOutcomeGenerator.ProcessSourceStructure(sourceSd, trackingRecords);

            int discreteTargetCount = trackingRecords.Values
                .Where(tr => tr.TargetStructure is not null)
                .Select(tr => tr.TargetStructure!.Key)
                .Distinct()
                .Count();

            // iterate over our tracking records to build structure outcomes
            foreach (StructureOutcomeTrackingRecord sdTr in trackingRecords.Values)
            {
                DbStructureDefinition? targetSd = sdTr.TargetStructure;

                (string idLong, string idShort, string name) = XVerProcessor.GenerateProfileId(
                    packagePair.SourceFhirSequence,
                    packagePair.SourcePackageShortName,
                    sourceSd.Id,
                    packagePair.TargetFhirSequence,
                    packagePair.TargetPackageShortName,
                    targetSd?.Id);

                string url = $"http://hl7.org/fhir/{packagePair.SourceFhirVersionShort}/StructureDefinition/{idLong}";

                // the element concept maps are combined now, so ignore the target ID for conventtions
                (string emIdLong, string emIdShort, string emName) = XVerProcessor.GenerateSdElementMapId(
                    packagePair.SourcePackageShortName,
                    sourceSd.Id,
                    packagePair.TargetPackageShortName);

                //string emUrl = $"http://hl7.org/fhir/{packagePair.SourceFhirVersionShort}/ConceptMap/{emIdLong}";
                string emUrl = $"{XVerProcessor._canonicalRootCrossVersion}ConceptMap/{emIdLong}";

                if (sdTr.StructureComparison is null ||
                    sdTr.StructureComparison.NotMapped ||
                    (targetSd is null))
                {
                    bool noMapSdRequiresXVer = !sdTr.IsFullyMappedAcrossAllTargets;

                    string noMapComments;
                    if (sdTr.Messages.Count > 0)
                    {
                        noMapComments =
                            $"FHIR {packagePair.SourceFhirSequence} `{sourceSd.Name}` does not map to" +
                            $" FHIR {packagePair.TargetFhirSequence}." +
                            $"\n{string.Join('\n', sdTr.Messages)}";
                    }
                    else
                    {
                        noMapComments =
                            $"FHIR {packagePair.SourceFhirSequence} `{sourceSd.Name}` does not map to" +
                            $" FHIR {packagePair.TargetFhirSequence}.";
                        if (sdTr.StructureComparison?.TechnicalMessage is not null)
                        {
                            noMapComments += "\n" + sdTr.StructureComparison.TechnicalMessage;
                        }
                        if (sdTr.StructureComparison?.UserMessage is not null)
                        {
                            noMapComments += "\n" + sdTr.StructureComparison.UserMessage;
                        }
                    }

                    // create our no-map structure outcome
                    DbStructureOutcome noMapSdOutcome = createNoMapStructureOutcome(
                        packagePair,
                        sourceSd,
                        structureOutcomeKey: sdTr.StructureOutcomeKey,
                        structureComparisonKey: null,
                        comments: noMapComments);

                    // element outcomes have been created in the element outcome generator
                    continue;
                }

                bool isRenamed = (discreteTargetCount == 1) && (sourceSd.Id != targetSd.Id);
                bool isUnmapped = false;
                bool isIdentical = sdTr.StructureComparison.IsIdentical == true;
                bool isEquivalent = sdTr.StructureComparison.Relationship == CMR.Equivalent;
                bool isBroaderThanTarget = sdTr.StructureComparison.Relationship == CMR.SourceIsBroaderThanTarget;
                bool isNarrowerThanTarget = sdTr.StructureComparison.Relationship == CMR.SourceIsNarrowerThanTarget;

                bool fullyMapsToThisTarget = sdTr.IsFullyMappedToThisTarget;

                bool sdRequiresXVer;

                if (isIdentical)
                {
                    sdRequiresXVer = false;
                }
                else if (isEquivalent)
                {
                    sdRequiresXVer = false;
                }
                else if (sdTr.IsFullyMappedAcrossAllTargets)
                {
                    sdRequiresXVer = false;
                }
                else
                {
                    sdRequiresXVer = true;
                }

                string comments;
                if (sdTr.Messages.Count > 0)
                {
                    comments =
                        $"FHIR {packagePair.SourceFhirSequence} `{sourceSd.Name}` is mapped to " +
                        $" FHIR {packagePair.TargetFhirSequence} `{targetSd.Name}`." +
                        $"\n{string.Join('\n', sdTr.Messages)}";
                }
                else
                {
                    comments = 
                        $"FHIR {packagePair.SourceFhirSequence} `{sourceSd.Name}` is mapped to " +
                        $" FHIR {packagePair.TargetFhirSequence} `{targetSd.Name}`.";
                    if (sdTr.StructureComparison.TechnicalMessage is not null)
                    {
                        comments += "\n" + sdTr.StructureComparison.TechnicalMessage;
                    }
                    if (sdTr.StructureComparison.UserMessage is not null)
                    {
                        comments += "\n" + sdTr.StructureComparison.UserMessage;
                    }
                }

                string artifactShort = $"{packagePair.SourceFhirSequence}: {sourceSd.Title ?? sourceSd.Name}";
                string artifactDescription = sourceSd.Description is null
                    ? $"Cross-version extension to represent a {packagePair.SourceFhirSequence} `{sourceSd.Name}`"
                    : $"{packagePair.SourceFhirSequence}: {sourceSd.Description}";

                // create our structure outcome
                DbStructureOutcome sdOutcome = new()
                {
                    Key = sdTr.StructureOutcomeKey,
                    StructureComparisonKey = sdTr.StructureComparison.Key,

                    SourceFhirPackageKey = packagePair.SourcePackageKey,
                    SourceFhirSequence = packagePair.SourceFhirSequence,
                    SourceStructureKey = sourceSd.Key,
                    SourceArtifactClass = sourceSd.ArtifactClass,
                    TotalSourceCount = -1,

                    TargetFhirPackageKey = packagePair.TargetPackageKey,
                    TargetFhirSequence = packagePair.TargetFhirSequence,
                    TargetStructureKey = targetSd.Key,
                    TargetArtifactClass = targetSd.ArtifactClass,
                    TotalTargetCount = discreteTargetCount,

                    RequiresXVerDefinition = sdRequiresXVer,

                    IsRenamed = isRenamed,
                    IsUnmapped = isUnmapped,
                    IsIdentical = isIdentical,
                    IsEquivalent = isEquivalent,
                    IsBroaderThanTarget = isBroaderThanTarget,
                    IsNarrowerThanTarget = isNarrowerThanTarget,

                    FullyMapsToThisTarget = fullyMapsToThisTarget,
                    FullyMapsAcrossAllTargets = sdTr.IsFullyMappedAcrossAllTargets,

                    Comments = comments,

                    SourceCanonicalUnversioned = sourceSd.UnversionedUrl,
                    SourceCanonicalVersioned = sourceSd.VersionedUrl,
                    SourceVersion = sourceSd.Version,
                    SourceId = sourceSd.Id,
                    SourceName = sourceSd.Name,
                    TargetCanonicalUnversioned = targetSd.UnversionedUrl,
                    TargetCanonicalVersioned = targetSd.VersionedUrl,
                    TargetVersion = targetSd.Version,
                    TargetId = targetSd.Id,
                    TargetName = targetSd.Name,

                    GenLongId = idLong,
                    GenShortId = idShort,
                    GenUrl = url,
                    GenName = name,
                    GenFileName = $"StructureDefinition-{idShort}",

                    GenArtifactShort = artifactShort,
                    GenArtifactDescription = artifactDescription,
                    GenArtifactComment = sourceSd.Comment is null
                        ? comments
                        : (comments + "\n" + sourceSd.Comment),
                    GenMappingComment = sourceSd.Comment is null
                        ? comments
                        : (comments + "\n" + sourceSd.Comment),

                    ElementConceptMapLongId = emIdLong,
                    ElementConceptMapShortId = emIdShort,
                    ElementConceptMapUrl = emUrl,
                    ElementConceptMapName = emName,
                    ElementConceptMapFileName = $"ConceptMap-{emIdShort}",
                };

                _sdOutcomeCache.CacheAdd(sdOutcome);
            }
        }
    }

    private DbStructureOutcome createNoMapStructureOutcome(
        FhirPackageComparisonPair packagePair,
        DbStructureDefinition sourceSd,
        int? structureOutcomeKey,
        int? structureComparisonKey,
        string? comments)
    {
        (string idLong, string idShort, string name) = XVerProcessor.GenerateProfileId(
            packagePair.SourceFhirSequence,
            packagePair.SourcePackageShortName,
            sourceSd.Id,
            packagePair.TargetFhirSequence,
            packagePair.TargetPackageShortName);

        string url = $"http://hl7.org/fhir/{packagePair.SourceFhirVersionShort}/StructureDefinition/{idLong}";

        (string emIdLong, string emIdShort, string emName) = XVerProcessor.GenerateSdElementMapId(
            packagePair.SourcePackageShortName,
            sourceSd.Id,
            packagePair.TargetPackageShortName);

        //string emUrl = $"http://hl7.org/fhir/{packagePair.SourceFhirVersionShort}/ConceptMap/{emIdLong}";
        string emUrl = $"{XVerProcessor._canonicalRootCrossVersion}ConceptMap/{emIdLong}";

        structureOutcomeKey ??= DbStructureOutcome.GetIndex();
        comments ??=
            $"FHIR {packagePair.SourceFhirSequence} `{sourceSd.Name}` does not map to" +
            $" FHIR {packagePair.TargetFhirSequence}.";

        string artifactShort = $"{packagePair.SourceFhirSequence}: {sourceSd.Title ?? sourceSd.Name}";
        string artifactDescription = sourceSd.Description is null
            ? $"Cross-version extension to represent a {packagePair.SourceFhirSequence} `{sourceSd.Name}`"
            : $"{packagePair.SourceFhirSequence}: {sourceSd.Description}";

        // build our no-map outcome
        DbStructureOutcome noMapOutcome = new()
        {
            Key = structureOutcomeKey!.Value,
            StructureComparisonKey = structureComparisonKey,

            SourceFhirPackageKey = packagePair.SourcePackageKey,
            SourceFhirSequence = packagePair.SourceFhirSequence,
            SourceStructureKey = sourceSd.Key,
            SourceArtifactClass = sourceSd.ArtifactClass,
            TotalSourceCount = -1,

            TargetFhirPackageKey = packagePair.TargetPackageKey,
            TargetFhirSequence = packagePair.TargetFhirSequence,
            TargetStructureKey = null,
            TargetArtifactClass = null,

            TotalTargetCount = 0,

            RequiresXVerDefinition = true,
            GenLongId = idLong,
            GenShortId = idShort,
            GenUrl = url,
            GenName = name,
            GenFileName = $"StructureDefinition-{idShort}",

            GenArtifactShort = artifactShort,
            GenArtifactDescription = artifactDescription,
            GenArtifactComment = sourceSd.Comment is null
                    ? comments
                    : (comments + "\n" + sourceSd.Comment),
            GenMappingComment = sourceSd.Comment is null
                    ? comments
                    : (comments + "\n" + sourceSd.Comment),

            ElementConceptMapLongId = emIdLong,
            ElementConceptMapShortId = emIdShort,
            ElementConceptMapUrl = emUrl,
            ElementConceptMapName = emName,
            ElementConceptMapFileName = $"ConceptMap-{emIdLong}",

            IsRenamed = false,
            IsUnmapped = false,
            IsIdentical = false,
            IsEquivalent = false,
            IsBroaderThanTarget = false,
            IsNarrowerThanTarget = false,

            FullyMapsToThisTarget = false,
            FullyMapsAcrossAllTargets = false,

            Comments = comments,

            SourceCanonicalUnversioned = sourceSd.UnversionedUrl,
            SourceCanonicalVersioned = sourceSd.VersionedUrl,
            SourceVersion = sourceSd.Version,
            SourceId = sourceSd.Id,
            SourceName = sourceSd.Name,
            TargetCanonicalUnversioned = null,
            TargetCanonicalVersioned = null,
            TargetVersion = null,
            TargetId = null,
            TargetName = null,
        };

        _sdOutcomeCache.CacheAdd(noMapOutcome);
        return noMapOutcome;
    }
}
