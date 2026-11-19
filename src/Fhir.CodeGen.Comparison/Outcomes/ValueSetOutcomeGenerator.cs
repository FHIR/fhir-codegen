using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Microsoft.Extensions.Logging;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Outcomes;

public class ValueSetOutcomeGenerator
{
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private List<DbFhirPackage> _packages = [];

    private DbRecordCache<DbValueSetOutcome> _vsOutcomeCache;
    private DbRecordCache<DbValueSetConceptOutcome> _conceptOutcomeCache;
    private XVerPreparedOperation? _operation;

    public ValueSetOutcomeGenerator(
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ValueSetOutcomeGenerator>();

        _db = db;

        _vsOutcomeCache = new();
        _conceptOutcomeCache = new();
    }

    public void CreateOutcomesForValueSets(
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
        => CreateOutcomesForValueSets(null, maxStepSize, specificPairs);

    internal void CreateOutcomesForValueSets(XVerPreparedOperation? operation,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        _operation = operation;
        try
        {
            operation?.RequireConnection(_db);
            CreateOutcomesCore(maxStepSize, specificPairs);
        }
        catch
        {
            operation?.Dispose();
            throw;
        }
        finally
        {
            _operation = null;
            _packages.Clear();
            _vsOutcomeCache.Clear();
            _conceptOutcomeCache.Clear();
        }
    }

    private void CreateOutcomesCore(int? maxStepSize,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs)
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

            buildOutcomes(packagePair);
            applyCachedChanges(packagePair);
        }
    }

    private void applyCachedChanges(FhirPackageComparisonPair packagePair)
    {
        if (_operation is null)
        {
            writeCachedChanges(packagePair);
            return;
        }
        _operation.Evidence.ApplyOutcomeWrites(
            new(XVerOperandFamily.VocabularyOutcomes, XVerOutputTransitionKind.PairFlush,
                packagePair.SourcePackageKey, packagePair.TargetPackageKey),
            [.. _vsOutcomeCache.ToAdd, .. _conceptOutcomeCache.ToAdd],
            [.. _vsOutcomeCache.ToUpdate, .. _conceptOutcomeCache.ToUpdate],
            () => writeCachedChanges(packagePair));
    }

    private void writeCachedChanges(FhirPackageComparisonPair packagePair)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        if (_vsOutcomeCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_vsOutcomeCache.ToAddCount} value set outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _vsOutcomeCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_vsOutcomeCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_vsOutcomeCache.ToUpdateCount} value set outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _vsOutcomeCache.ToUpdate.Update(_db);
        }

        if (_conceptOutcomeCache.ToAddCount > 0)
        {
            _logger.LogInformation($"Adding {_conceptOutcomeCache.ToAddCount} value set concept outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _conceptOutcomeCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        if (_conceptOutcomeCache.ToUpdateCount > 0)
        {
            _logger.LogInformation($"Updating {_conceptOutcomeCache.ToUpdateCount} value set concept outcomes from {sourcePackage.ShortName} to {targetPackage.ShortName}");
            _conceptOutcomeCache.ToUpdate.Update(_db);
        }

        _vsOutcomeCache.Clear();
        _conceptOutcomeCache.Clear();
    }

    private void buildOutcomes(FhirPackageComparisonPair packagePair)
    {
        _logger.LogInformation($"Generating ValueSet Outcomes for {packagePair.SourcePackageShortName}->{packagePair.TargetPackageShortName}...");

        // just get all the source and target value sets now to avoid hitting the db so much
        Dictionary<int, DbValueSet> allSourceValueSets = DbValueSet.SelectDict(_db, FhirPackageKey: packagePair.SourcePackageKey);
        Dictionary<int, DbValueSetConcept> allSourceConcepts = DbValueSetConcept.SelectDict(_db, FhirPackageKey: packagePair.SourcePackageKey);
        Dictionary<int, DbValueSet> allTargetValueSets = DbValueSet.SelectDict(_db, FhirPackageKey: packagePair.TargetPackageKey);
        Dictionary<int, DbValueSetConcept> allTargetConcepts = DbValueSetConcept.SelectDict(_db, FhirPackageKey: packagePair.TargetPackageKey);

        List<DbValueSetComparison> vsComparisons = DbValueSetComparison.SelectList(
            _db,
            SourceFhirPackageKey: packagePair.SourcePackageKey,
            TargetFhirPackageKey: packagePair.TargetPackageKey);

        _logger.LogInformation(
            $"ValueSet comparisons: {vsComparisons.Count}, from" +
            $" {allSourceValueSets.Count} Value Sets" +
            $" to {allTargetValueSets.Count} Value Sets");

        List<DbValueSetConceptComparison> conceptComparisons = DbValueSetConceptComparison.SelectList(
            _db,
            SourceFhirPackageKey: packagePair.SourcePackageKey,
            TargetFhirPackageKey: packagePair.TargetPackageKey);

        _logger.LogInformation(
            $"ValueSet Concept comparisons: {conceptComparisons.Count}, from" +
            $" {allSourceConcepts.Count} Value Set Concepts" +
            $" to {allTargetConcepts.Count} Value Set Concepts");

        // create lookups of our comparisons that we need to generate outcomes
        ILookup<int, DbValueSetComparison> vsComparsionsBySourceKey = vsComparisons.ToLookup(c => c.SourceContentKey);
        ILookup<int, DbValueSetComparison> vsComparsionsByTargetKey = vsComparisons
            .Where(c => c.TargetContentKey is not null)
            .ToLookup(c => c.TargetContentKey!.Value);

        ILookup<int, DbValueSetConcept> allSourceConceptsByVsKey = allSourceConcepts.Values.ToLookup(c => c.ValueSetKey);
        ILookup<int, DbValueSetConcept> allTargetConceptsByVsKey = allTargetConcepts.Values.ToLookup(c => c.ValueSetKey);

        ILookup<int, DbValueSetConceptComparison> conceptComparsionsByVsComparisonKey = conceptComparisons.ToLookup(c => c.ValueSetComparisonKey);

        // iterate over our source value sets
        foreach (DbValueSet sourceVs in allSourceValueSets.Values)
        {
            List<DbValueSetComparison> sourceComparisons = vsComparsionsBySourceKey[sourceVs.Key].ToList();

            if ((sourceVs.CanExpand == false) ||
                (sourceVs.IsExcluded == true) ||
                XVerProcessor._exclusionSet.Contains(sourceVs.UnversionedUrl) ||
                XVerProcessor._exclusionSet.Contains(sourceVs.VersionedUrl))
            {
                continue;
            }

            Dictionary<int, DbValueSetConcept> sourceConcepts = allSourceConceptsByVsKey[sourceVs.Key]
                .ToDictionary(c => c.Key);

            if (sourceComparisons.Count == 0)
            {
                (string idLong, string idShort, string name) = XVerProcessor.GenerateArtifactId(
                    packagePair.SourcePackageShortName,
                    sourceVs.Id,
                    packagePair.TargetPackageShortName);

                string url = $"{XVerProcessor._canonicalRootCrossVersion}ValueSet/{idLong}";
                string vsFilename = "ValueSet-" + idShort;

                (string cmIdLong, string cmIdShort, string cmName) = XVerProcessor.GenerateArtifactId(
                    packagePair.SourcePackageShortName,
                    sourceVs.Id,
                    packagePair.TargetPackageShortName,
                    targetArtifactId: null,
                    prefixLong: "Map",
                    prefixShort: "Map");

                string cmUrl = $"{XVerProcessor._canonicalRootCrossVersion}ConceptMap/{idLong}";
                string cmFilename = "Concept" + cmIdShort;

                doNoMap(
                    sourceVs,
                    sourceConcepts,
                    null,
                    false,
                    0,
                    null,
                    idLong, idShort, name,
                    url, vsFilename,
                    cmIdLong, cmIdShort, cmName,
                    cmUrl, cmFilename);

                continue;
            }

            if (sourceComparisons.Count > 1)
            {
                // check to see if we have one with a target and one without
                if (sourceComparisons.Any(c => c.TargetContentKey is not null) &&
                    sourceComparisons.Any(c => c.TargetContentKey is null))
                {
                    // remove the no-map comparison for now
                    sourceComparisons = sourceComparisons
                        .Where(c => c.TargetContentKey is not null)
                        .ToList();
                }
            }

            Dictionary<int, Dictionary<int, DbValueSetConcept>> targetConceptsByVsKey = [];

            HashSet<int> fullyMappedConceptKeys = [];
            Dictionary<int, HashSet<int>> fullyMappedConceptKeysByComparisonKey = [];

            //CMR? relationship = CMR.Equivalent;
            //bool hasNoMap = false;

            Dictionary<int, DbValueSet> targetValueSets = [];
            // build objects we need for processing
            foreach (DbValueSetComparison sourceComparison in sourceComparisons)
            {
                //relationship = FhirDbComparer.ApplyRelationship(relationship, vsComparison.Relationship);

                if ((sourceComparison.TargetContentKey is null) ||
                    sourceComparison.NotMapped)
                {
                    //hasNoMap = true;
                    continue;
                }

                int targetVsKey = sourceComparison.TargetContentKey.Value;

                if (targetValueSets.ContainsKey(targetVsKey))
                {
                    continue;
                }

                targetValueSets[targetVsKey] = allTargetValueSets[targetVsKey];
                targetConceptsByVsKey[targetVsKey] = allTargetConceptsByVsKey[targetVsKey].ToDictionary(c => c.Key);

                HashSet<int> currentlyMappedComparisonKeys = [];
                fullyMappedConceptKeysByComparisonKey[sourceComparison.Key] = currentlyMappedComparisonKeys;

                // iterate over the concepts and track ones that are fully mapped
                foreach (DbValueSetConceptComparison conceptComparison in conceptComparsionsByVsComparisonKey[sourceComparison.Key])
                {
                    if ((conceptComparison.IsIdentical == true) ||
                        (conceptComparison.Relationship == CMR.Equivalent) ||
                        (conceptComparison.Relationship == CMR.SourceIsNarrowerThanTarget))
                    {
                        fullyMappedConceptKeys.Add(conceptComparison.SourceContentKey);
                        currentlyMappedComparisonKeys.Add(conceptComparison.SourceContentKey);
                    }
                }
            }

            bool fullyMapsAcrossAllTargets = fullyMappedConceptKeys.Count == sourceConcepts.Count;
            int vsTargetCount = targetValueSets.Count;

            // traverse our comparisons to build matching outcomes
            foreach (DbValueSetComparison vsComparison in sourceComparisons)
            {
                DbValueSet? targetVs = vsComparison.TargetContentKey is null
                    ? null
                    : targetValueSets[vsComparison.TargetContentKey.Value];

                (string idLong, string idShort, string name) = XVerProcessor.GenerateArtifactId(
                    packagePair.SourcePackageShortName,
                    sourceVs.Id,
                    packagePair.TargetPackageShortName,
                    targetArtifactId: targetVs?.Id);

                string url = $"{XVerProcessor._canonicalRootCrossVersion}ValueSet/{idLong}";
                string vsFilename = "ValueSet-" + idShort;

                (string cmIdLong, string cmIdShort, string cmName) = XVerProcessor.GenerateArtifactId(
                    packagePair.SourcePackageShortName,
                    sourceVs.Id,
                    packagePair.TargetPackageShortName,
                    targetArtifactId: targetVs?.Id,
                    prefixLong: "Map",
                    prefixShort: "Map");

                string cmUrl = $"{XVerProcessor._canonicalRootCrossVersion}ConceptMap/{idLong}";
                string cmFilename = $"ConceptMap-{cmIdShort}";

                if ((targetVs is null) ||
                    vsComparison.NotMapped)
                {
                    doNoMap(
                        sourceVs,
                        sourceConcepts,
                        vsComparison,
                        fullyMapsAcrossAllTargets,
                        vsTargetCount,
                        fullyMappedConceptKeys,
                        idLong, idShort, name,
                        url, vsFilename,
                        cmIdLong, cmIdShort, cmName,
                        cmUrl, cmFilename);

                    // move to next comparison
                    continue;
                }

                int targetVsKey = targetVs.Key;

                int sourceVsCount = DbValueSetComparison.SelectCount(
                    _db,
                    SourceFhirPackageKey: packagePair.SourcePackageKey,
                    TargetFhirPackageKey: packagePair.TargetPackageKey,
                    TargetValueSetKey: targetVsKey);

                Dictionary<int, DbValueSetConcept> targetConcepts = targetConceptsByVsKey[targetVsKey];

                bool isRenamed = (vsTargetCount == 1) && (sourceVs.Id != targetVs.Id);
                bool isUnmapped = false;
                bool isIdentical = vsComparison.IsIdentical == true;
                bool isEquivalent = vsComparison.Relationship == CMR.Equivalent;
                bool isBroaderThanTarget = vsComparison.Relationship == CMR.SourceIsBroaderThanTarget;
                bool isNarrowerThanTarget = vsComparison.Relationship == CMR.SourceIsNarrowerThanTarget;

                bool fullyMapsToThisTarget = fullyMappedConceptKeysByComparisonKey[vsComparison.Key].Count == sourceConcepts.Count;

                bool vsRequiresXVer;

                //OutcomeValueSetActionCodes vsAction;

                if (isIdentical)
                {
                    vsRequiresXVer = false;
                }
                else if (isEquivalent)
                {
                    vsRequiresXVer = false;
                }
                else if (fullyMapsAcrossAllTargets)
                {
                    vsRequiresXVer = false;
                }
                else
                {
                    vsRequiresXVer = true;
                }

                string artifactShort = $"{packagePair.SourceFhirSequence}: {sourceVs.Title ?? sourceVs.Name}";
                string artifactDescription = sourceVs.Description is null
                    ? $"Cross-version extension to represent the {packagePair.SourceFhirSequence} value set `{sourceVs.VersionedUrl}`"
                    : $"{packagePair.SourceFhirSequence}: {sourceVs.Description}";

                // create our value set outcome
                DbValueSetOutcome vsOutcome = new()
                {
                    Key = DbValueSetOutcome.GetIndex(),
                    ValueSetComparisonKey = vsComparison.Key,

                    SourceFhirPackageKey = packagePair.SourcePackageKey,
                    SourceFhirSequence = packagePair.SourceFhirSequence,
                    SourceValueSetKey = sourceVs.Key,
                    TotalSourceCount = sourceVsCount,

                    TargetFhirPackageKey = packagePair.TargetPackageKey,
                    TargetFhirSequence = packagePair.TargetFhirSequence,
                    TargetValueSetKey = targetVs.Key,
                    TotalTargetCount = vsTargetCount,

                    RequiresXVerDefinition = vsRequiresXVer,
                    GenLongId = idLong,
                    GenShortId = idShort,
                    GenUrl = url,
                    GenName = name,
                    GenFileName = vsFilename,

                    GenArtifactShort = artifactShort,
                    GenArtifactDescription = artifactDescription,
                    GenArtifactComment = sourceVs.Purpose,
                    GenMappingComment = sourceVs.Purpose,

                    ConceptMapLongId = cmIdLong,
                    ConceptMapShortId = cmIdShort,
                    ConceptMapUrl = cmUrl,
                    ConceptMapName = cmName,
                    ConceptMapFileName = cmFilename,

                    IsRenamed = isRenamed,
                    IsUnmapped = isUnmapped,
                    IsIdentical = isIdentical,
                    IsEquivalent = isEquivalent,
                    IsBroaderThanTarget = isBroaderThanTarget,
                    IsNarrowerThanTarget = isNarrowerThanTarget,

                    FullyMapsToThisTarget = fullyMapsToThisTarget,
                    FullyMapsAcrossAllTargets = fullyMapsAcrossAllTargets,

                    Comments = vsComparison.TechnicalMessage ?? vsComparison.UserMessage ?? "TODO",

                    //OutcomeAction = vsAction,
                    //ContentKeys = vsComparison.ContentKeys,

                    SourceCanonicalUnversioned = sourceVs.UnversionedUrl,
                    SourceCanonicalVersioned = sourceVs.VersionedUrl,
                    SourceVersion = sourceVs.Version,
                    SourceId = sourceVs.Id,
                    SourceName = sourceVs.Name,
                    TargetCanonicalUnversioned = targetVs.UnversionedUrl,
                    TargetCanonicalVersioned = targetVs.VersionedUrl,
                    TargetVersion = targetVs.Version,
                    TargetId = targetVs.Id,
                    TargetName = targetVs.Name,
                };

                _vsOutcomeCache.CacheAdd(vsOutcome);

                HashSet<int> currentlyMappedComparisonKeys = fullyMappedConceptKeysByComparisonKey[vsComparison.Key];

                // build our concept outcomes
                foreach (DbValueSetConceptComparison conceptComparison in conceptComparsionsByVsComparisonKey[vsComparison.Key])
                {
                    DbValueSetConcept sourceConcept = allSourceConcepts[conceptComparison.SourceContentKey];
                    DbValueSetConcept? targetConcept = conceptComparison.TargetContentKey is null
                        ? null
                        : allTargetConcepts[conceptComparison.TargetContentKey.Value];

                    int conceptSourceCount = targetConcept is null
                        ? -1
                        : DbValueSetConceptComparison.SelectCount(
                            _db,
                            SourceValueSetKey: sourceVs.Key,
                            TargetConceptKey: targetConcept.Key);
                    int conceptTargetCount = DbValueSetConceptComparison.SelectCount(
                        _db,
                        SourceConceptKey: sourceConcept.Key,
                        TargetValueSetKey: targetVs.Key);

                    bool conceptFullyMapsToThisTarget = currentlyMappedComparisonKeys.Contains(sourceConcept.Key);
                    bool conceptFullyMapsAcrossAllTargets = fullyMappedConceptKeys.Contains(sourceConcept.Key);

                    bool conceptRequiresXVer = !conceptFullyMapsToThisTarget && !conceptFullyMapsAcrossAllTargets;

                    string conceptComments = conceptComparison.TechnicalMessage ?? conceptComparison.UserMessage ?? "TODO";

                    CMR? relationship = conceptComparison.Relationship;

                    if (conceptComparison.SourceCodeTreatedAsEscapeValve ||
                        (conceptComparison.TargetCodeTreatedAsEscapeValve == true))
                    {
                        // update based on value set relationship
                        switch (vsComparison.Relationship)
                        {
                            case CMR.RelatedTo:
                            case CMR.SourceIsBroaderThanTarget:
                                conceptFullyMapsToThisTarget = false;
                                conceptFullyMapsAcrossAllTargets = false;
                                conceptRequiresXVer = true;
                                conceptComments += "\n" +
                                    $"Value Set Relationship of {vsComparison.Relationship} applied to 'escape-valve' mapping.";
                                break;
                            default:
                                break;
                        }

                        relationship = FhirDbComparer.ApplyRelationship(relationship, vsComparison.Relationship);
                    }

                    DbValueSetConceptOutcome conceptOutcome = new()
                    {
                        Key = DbValueSetConceptOutcome.GetIndex(),
                        ValueSetOutcomeKey = vsOutcome.Key,
                        ValueSetConceptComparisonKey = conceptComparison.Key,

                        SourceFhirPackageKey = packagePair.SourcePackageKey,
                        SourceFhirSequence = packagePair.SourceFhirSequence,
                        SourceValueSetKey = sourceVs.Key,
                        SourceValueSetConceptKey = sourceConcept.Key,
                        SourceDisplay = sourceConcept.Display,
                        TotalSourceCount = conceptSourceCount,

                        TargetFhirPackageKey = packagePair.TargetPackageKey,
                        TargetFhirSequence = packagePair.TargetFhirSequence,
                        TargetValueSetKey = targetVs.Key,
                        TargetValueSetConceptKey = targetConcept?.Key,
                        TargetDisplay = targetConcept?.Display,
                        TotalTargetCount = conceptTargetCount,

                        RequiresXVerDefinition = conceptRequiresXVer,

                        IsRenamed = targetConcept is null ? false : (sourceConcept.Code != targetConcept.Code),
                        IsUnmapped = targetConcept is null || conceptComparison.NotMapped,
                        IsIdentical = conceptComparison.IsIdentical == true,
                        IsEquivalent = relationship == CMR.Equivalent,
                        IsBroaderThanTarget = relationship == CMR.SourceIsBroaderThanTarget,
                        IsNarrowerThanTarget = relationship == CMR.SourceIsNarrowerThanTarget,

                        FullyMapsToThisTarget = conceptFullyMapsToThisTarget,
                        FullyMapsAcrossAllTargets = conceptFullyMapsAcrossAllTargets,

                        Comments = conceptComments,

                        //OutcomeAction = conceptAction,
                        //ContentKeys = conceptComparison.ContentKeys,

                        //CodeLiteralsMatch = sourceConcept.Code == targetConcept?.Code,
                        //SourceCodeTreatedAsEscapeValve = conceptComparison.SourceCodeTreatedAsEscapeValve,
                        //TargetCodeTreatedAsEscapeValve = conceptComparison.TargetCodeTreatedAsEscapeValve,

                        SourceSystem = sourceConcept.System,
                        SourceSystemVersion = sourceConcept.SystemVersion,
                        SourceCode = sourceConcept.Code,
                        TargetSystem = targetConcept?.System,
                        TargetSystemVersion = targetConcept?.SystemVersion,
                        TargetCode = targetConcept?.Code,
                    };

                    _conceptOutcomeCache.CacheAdd(conceptOutcome);
                }

            }
        }

        return;

        void doNoMap(
            DbValueSet sourceVs,
            Dictionary<int, DbValueSetConcept> sourceConcepts,
            DbValueSetComparison? vsComparison,
            bool fullyMapsAcrossAllTargets,
            int vsTargetCount,
            HashSet<int>? fullyMappedConceptKeys,
            string idLong, string idShort, string name,
            string url, string vsFilename,
            string cmIdLong, string cmIdShort, string cmName,
            string cmUrl, string cmFilename)
        {
            bool noMapVsRequiresXVer = fullyMapsAcrossAllTargets != true;

            string comments = vsComparison?.TechnicalMessage ?? vsComparison?.UserMessage ?? string.Empty;

            if (vsTargetCount == 0)
            {
                comments +=
                    $"\nFHIR ValueSet `{sourceVs.VersionedUrl}`," +
                    $" defined in FHIR {packagePair.SourceFhirSequence}" +
                    $" does not have any mapping to FHIR {packagePair.TargetFhirSequence}";
            }
            else
            {
                comments +=
                    $"\nFHIR ValueSet `{sourceVs.VersionedUrl}`," +
                    $" defined in FHIR {packagePair.SourceFhirSequence}" +
                    $" does not have a map to FHIR {packagePair.TargetFhirSequence}";
            }

            string artifactShort = $"{packagePair.SourceFhirSequence}: {sourceVs.Title ?? sourceVs.Name}";
            string artifactDescription = sourceVs.Description is null
                ? $"Cross-version extension to represent the {packagePair.SourceFhirSequence} value set `{sourceVs.VersionedUrl}`"
                : $"{packagePair.SourceFhirSequence}: {sourceVs.Description}";

            // build our no-map outcome
            DbValueSetOutcome noMapOutcome = new()
            {
                Key = DbValueSetOutcome.GetIndex(),
                ValueSetComparisonKey = vsComparison?.Key,

                SourceFhirPackageKey = packagePair.SourcePackageKey,
                SourceFhirSequence = packagePair.SourceFhirSequence,
                SourceValueSetKey = sourceVs.Key,
                TotalSourceCount = -1,

                TargetFhirPackageKey = packagePair.TargetPackageKey,
                TargetFhirSequence = packagePair.TargetFhirSequence,
                TargetValueSetKey = null,

                TotalTargetCount = vsTargetCount,

                RequiresXVerDefinition = noMapVsRequiresXVer,
                GenLongId = idLong,
                GenShortId = idShort,
                GenUrl = url,
                GenName = name,
                GenFileName = vsFilename,

                GenArtifactShort = artifactShort,
                GenArtifactDescription = artifactDescription,
                GenArtifactComment = sourceVs.Purpose is null
                    ? comments
                    : (comments + "\n" + sourceVs.Purpose),
                GenMappingComment = sourceVs.Purpose is null
                    ? comments
                    : (comments + "\n" + sourceVs.Purpose),

                ConceptMapLongId = cmIdLong,
                ConceptMapShortId = cmIdShort,
                ConceptMapUrl = cmUrl,
                ConceptMapName = cmName,
                ConceptMapFileName = cmFilename,

                IsRenamed = false,
                IsUnmapped = false,
                IsIdentical = false,
                IsEquivalent = false,
                IsBroaderThanTarget = false,
                IsNarrowerThanTarget = false,

                FullyMapsToThisTarget = false,
                FullyMapsAcrossAllTargets = fullyMapsAcrossAllTargets,

                Comments = comments,

                SourceCanonicalUnversioned = sourceVs.UnversionedUrl,
                SourceCanonicalVersioned = sourceVs.VersionedUrl,
                SourceVersion = sourceVs.Version,
                SourceId = sourceVs.Id,
                SourceName = sourceVs.Name,
                TargetCanonicalUnversioned = null,
                TargetCanonicalVersioned = null,
                TargetVersion = null,
                TargetId = null,
                TargetName = null,
            };

            _vsOutcomeCache.CacheAdd(noMapOutcome);

            if (vsComparison?.Key is not null)
            {
                // build our no-map concept outcomes
                foreach (DbValueSetConceptComparison conceptComparison in conceptComparsionsByVsComparisonKey[vsComparison.Key])
                {
                    DbValueSetConcept sourceConcept = allSourceConcepts[conceptComparison.SourceContentKey];

                    DbValueSetConceptOutcome noMapConceptOutcome = new()
                    {
                        Key = DbValueSetConceptOutcome.GetIndex(),
                        ValueSetOutcomeKey = noMapOutcome.Key,
                        ValueSetConceptComparisonKey = conceptComparison.Key,

                        SourceFhirPackageKey = packagePair.SourcePackageKey,
                        SourceFhirSequence = packagePair.SourceFhirSequence,
                        SourceValueSetKey = sourceVs.Key,
                        SourceValueSetConceptKey = sourceConcept.Key,
                        SourceDisplay = sourceConcept.Display,
                        TotalSourceCount = -1,

                        TargetFhirPackageKey = packagePair.TargetPackageKey,
                        TargetFhirSequence = packagePair.TargetFhirSequence,
                        TargetValueSetKey = null,
                        TargetValueSetConceptKey = null,
                        TargetDisplay = null,
                        TotalTargetCount = -1,

                        RequiresXVerDefinition = noMapVsRequiresXVer,

                        IsRenamed = false,
                        IsUnmapped = false,
                        IsIdentical = false,
                        IsEquivalent = false,
                        IsBroaderThanTarget = false,
                        IsNarrowerThanTarget = false,

                        FullyMapsToThisTarget = false,
                        FullyMapsAcrossAllTargets = fullyMappedConceptKeys?.Contains(sourceConcept.Key) == true,

                        Comments = vsComparison.TechnicalMessage ?? vsComparison.UserMessage ?? "The ValueSet is unmapped.",

                        SourceSystem = sourceConcept.System,
                        SourceSystemVersion = sourceConcept.SystemVersion,
                        SourceCode = sourceConcept.Code,
                        TargetSystem = null,
                        TargetSystemVersion = null,
                        TargetCode = null,
                    };

                    _conceptOutcomeCache.CacheAdd(noMapConceptOutcome);
                }
            }
            else
            {
                // build our no-map concept outcomes
                foreach (DbValueSetConcept sourceConcept in sourceConcepts.Values.OrderBy(c => c.FhirKey))
                {
                    DbValueSetConceptOutcome noMapConceptOutcome = new()
                    {
                        Key = DbValueSetConceptOutcome.GetIndex(),
                        ValueSetOutcomeKey = noMapOutcome.Key,
                        ValueSetConceptComparisonKey = null,

                        SourceFhirPackageKey = packagePair.SourcePackageKey,
                        SourceFhirSequence = packagePair.SourceFhirSequence,
                        SourceValueSetKey = sourceVs.Key,
                        SourceValueSetConceptKey = sourceConcept.Key,
                        SourceDisplay = sourceConcept.Display,
                        TotalSourceCount = -1,

                        TargetFhirPackageKey = packagePair.TargetPackageKey,
                        TargetFhirSequence = packagePair.TargetFhirSequence,
                        TargetValueSetKey = null,
                        TargetValueSetConceptKey = null,
                        TargetDisplay = null,
                        TotalTargetCount = -1,

                        RequiresXVerDefinition = noMapVsRequiresXVer,

                        IsRenamed = false,
                        IsUnmapped = false,
                        IsIdentical = false,
                        IsEquivalent = false,
                        IsBroaderThanTarget = false,
                        IsNarrowerThanTarget = false,

                        FullyMapsToThisTarget = false,
                        FullyMapsAcrossAllTargets = fullyMappedConceptKeys?.Contains(sourceConcept.Key) == true,

                        Comments = "The ValueSet is unmapped.",

                        SourceSystem = sourceConcept.System,
                        SourceSystemVersion = sourceConcept.SystemVersion,
                        SourceCode = sourceConcept.Code,
                        TargetSystem = null,
                        TargetSystemVersion = null,
                        TargetCode = null,
                    };

                    _conceptOutcomeCache.CacheAdd(noMapConceptOutcome);
                }
            }


        }
    }
}
