using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using static Fhir.CodeGen.Comparison.CompareTool.FhirTypeMappings;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.CompareTool;

public partial class FhirDbComparer
{
    private record class CollatedType
    {
        private static readonly string[] _quantityTypes = [
            "Quantity",
            "Age",
            "Count",
            "Distance",
            "Duration",
            "Money",
            "MoneyQuantity",
            "SimpleQuantity",
            ];

        public required string? TypeName { get; init; }
        public required int? TypeStructureKey { get; init; }
        public required string[] TypeProfiles { get; init; }
        public required string[] TargetProfiles { get; init; }
        public required List<DbElementType> UnderlyingElementTypes { get; init; }

        public CollatedType() { }

        [SetsRequiredMembers]
        public CollatedType(IEnumerable<DbElementType> types)
        {
            UnderlyingElementTypes = types is List<DbElementType> tl ? tl : types.ToList();
            TypeName = types.First().TypeName;
            TypeStructureKey = types.First().TypeStructureKey;
            TypeProfiles = types
                .Where(st => (st.TypeName == TypeName) && (!string.IsNullOrEmpty(st.TypeProfile)))
                .Select(st => st.TypeProfile!)
                .ToArray();
            TargetProfiles = types
                .Where(st => (st.TypeName == TypeName) && (!string.IsNullOrEmpty(st.TargetProfile)))
                .Select(st => st.TargetProfile!)
                .ToArray();
        }

        public string NormalizedName
        {
            get
            {
                if (string.IsNullOrEmpty(TypeName))
                {
                    return string.Empty;
                }

                if ((TypeName == "Quantity") &&
                    (TypeProfiles.Length == 1) &&
                    TypeProfiles[0].StartsWith("http://hl7.org/", StringComparison.Ordinal))
                {
                    return TypeProfiles[0].Split('/')[^1];
                }

                return TypeName;
            }
        }

        public bool HasSingleMatchingTypeProfile(string value)
        {
            if (TypeProfiles.Length != 1)
            {
                return false;
            }

            return TypeProfiles[0].EndsWith(value, StringComparison.Ordinal);
        }

        public bool IsQuantityType => _quantityTypes.Contains(NormalizedName);
    }

    private readonly ComparisonDatabase _comparisonDb;
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    //private DbComparisonCache<DbValueSetComparison> _sdOutcomeCache = new();
    //private DbComparisonCache<DbValueSetConceptComparison> _edOutcomeCache = new();

    //private DbComparisonCache<DbStructureComparison> _sdComparisonCache = new();
    //private DbComparisonCache<DbElementComparison> _edComparisonCache = new();

    public FhirDbComparer(
        ComparisonDatabase db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<FhirDbComparer>();

        _comparisonDb = db;
        _db = db.DbConnection;
    }

    public void Compare(
        bool processValueSets = true,
        bool processStructures = true,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        using XVerPreparedOperation? operation = processStructures
            ? XVerPreparedOperation.Prepare(_db, new(XVerPreparationKind.Comparison, XVerProcessor._exclusionSet,
                maxStepSize: maxStepSize, specificPairs: specificPairs, processVocabulary: processValueSets))
            : null;
        if (operation is null)
        {
            Reset();
        }
        else
        {
            UnsafeCodeInputStore.RequireInputs(_db, operation);
            operation.Evidence.ResetComparisons(processValueSets, Reset);
        }

        if (processValueSets)
        {
            // create our value set comparer
            ValueSetComparer vsComparer = new(_db, _loggerFactory);

            // run our value set comparisons
            vsComparer.CompareValueSets(
                operation,
                maxStepSize: maxStepSize,
                specificPairs: specificPairs);
        }

        if (processStructures)
        {
            // create our structure comparer
            StructureComparer sdComparer = new(_db, _loggerFactory);

            // run our structure comparisons
            sdComparer.CompareStructures(
                operation!,
                maxStepSize: maxStepSize,
                specificPairs: specificPairs);
        }

        void Reset()
        {
            DbComparisonClasses.DropTables(_db, forValueSets: processValueSets, forStructures: processStructures);
            DbComparisonClasses.CreateTables(_db, forValueSets: processValueSets, forStructures: processStructures);
        }
    }

#if false
    public void BuildComparisonPairs(
        FhirArtifactClassEnum? artifactFilter = null,
        HashSet<int>? comparisonPairFilterSet = null,
        bool allowUpdates = true)
    {
        Dictionary<int, DbFhirPackage> packages = DbFhirPackage.SelectDict(_db);

        // iterate over each FHIR Package we have
        foreach (DbFhirPackage sourcePackage in packages.Values)
        {
            _logger.LogInformation($"Processing source package {sourcePackage.Key}: {sourcePackage.PackageId}@{sourcePackage.PackageVersion}");

            // iterate over each target package
            foreach (DbFhirPackage targetPackage in packages.Values)
            {
                // skip the same package (not a pair)
                if (sourcePackage.Key == targetPackage.Key)
                {
                    continue;
                }

                // skip pairs that are not in the filters, if we have any
                if ((comparisonPairFilterSet != null) &&
                    (comparisonPairFilterSet.Count != 0) &&
                    !comparisonPairFilterSet.Contains(sourcePackage.Key) &&
                    !comparisonPairFilterSet.Contains(targetPackage.Key))
                {
                    continue;
                }

                // only process neighboring packages
                if (Common.Packaging.FhirReleases.GetReleaseDistance(sourcePackage.DefinitionFhirSequence, targetPackage.DefinitionFhirSequence) > 1)
                {
                    continue;
                }

                _logger.LogInformation($"Processing target package {targetPackage.Key}: {targetPackage.PackageId}@{targetPackage.PackageVersion}");

                // get the forward and packageReversePair package comparison pairs
                (DbFhirPackageComparisonPair packageForwardPair, DbFhirPackageComparisonPair packageReversePair) = getCreateOrUpdatePackagePair(sourcePackage, targetPackage);

                // if we have an artifact filter, check for Value Sets
                if ((artifactFilter == null) ||
                    (artifactFilter == FhirArtifactClassEnum.ValueSet))
                {
                    buildValueSetComparisonPairsForSource(
                        sourcePackage,
                        targetPackage,
                        packageForwardPair,
                        packageReversePair,
                        allowUpdates);
                }

                // if we have an artifact filter, check for Structures
                if ((artifactFilter == null) ||
                    (artifactFilter == FhirArtifactClassEnum.PrimitiveType) ||
                    (artifactFilter == FhirArtifactClassEnum.ComplexType) ||
                    (artifactFilter == FhirArtifactClassEnum.Resource) ||
                    (artifactFilter == FhirArtifactClassEnum.Profile) ||
                    (artifactFilter == FhirArtifactClassEnum.Extension))
                {
                    buildStructureComparisonPairsForSource(
                        sourcePackage,
                        targetPackage,
                        packageForwardPair,
                        packageReversePair,
                        allowUpdates);
                }
            }
        }
    }

    private (DbFhirPackageComparisonPair forward, DbFhirPackageComparisonPair reverse) getCreateOrUpdatePackagePair(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage)
    {
        DbFhirPackageComparisonPair? packageForwardPair = DbFhirPackageComparisonPair.SelectSingle(
            _db,
            SourcePackageKey: sourcePackage.Key,
            TargetPackageKey: targetPackage.Key);

        if (packageForwardPair == null)
        {
            // create a new pair
            packageForwardPair = new()
            {
                SourcePackageKey = sourcePackage.Key,
                SourcePackageShortName = sourcePackage.ShortName,
                TargetPackageKey = targetPackage.Key,
                TargetPackageShortName = targetPackage.ShortName,
            };
            packageForwardPair.Insert(_db);
        }

        DbFhirPackageComparisonPair? packageReversePair = DbFhirPackageComparisonPair.SelectSingle(
            _db,
            SourcePackageKey: packageForwardPair.TargetPackageKey,
            TargetPackageKey: packageForwardPair.SourcePackageKey);

        if (packageReversePair == null)
        {
            packageReversePair = invert(packageForwardPair);
            packageReversePair.Insert(_db);
        }

        if (packageReversePair.InverseComparisonKey != packageForwardPair.Key)
        {
            packageReversePair.InverseComparisonKey = packageForwardPair.Key;
            packageReversePair.Update(_db);
        }

        if (packageForwardPair.InverseComparisonKey != packageReversePair.Key)
        {
            packageForwardPair.InverseComparisonKey = packageReversePair.Key;
            packageForwardPair.Update(_db);
        }

        return (packageForwardPair, packageReversePair);
    }
#endif

#if false
    [Obsolete]
    public void Compare(
        FhirArtifactClassEnum? artifactFilter,
        HashSet<int>? comparisonPairFilterSet = null,
        bool allowUpdates = true)
    {
        Dictionary<int, DbFhirPackage> packages = DbFhirPackage.SelectDict(_db);

        // iterate over each FHIR Package we have
        foreach (DbFhirPackage sourcePackage in packages.Values)
        {
            _logger.LogInformation($"Processing source package {sourcePackage.Key}: {sourcePackage.PackageId}@{sourcePackage.PackageVersion}");
            
            List<(DbFhirPackageComparisonPair forward, DbFhirPackageComparisonPair reverse)> bidirectionalPairs = [];

            foreach (DbFhirPackageComparisonPair packageForwardPair in DbFhirPackageComparisonPair.SelectList(_db, SourcePackageKey: sourcePackage.Key))
            {
                // skip pairs that are not in the filter
                if ((comparisonPairFilterSet != null) &&
                    (comparisonPairFilterSet.Count != 0) &&
                    !comparisonPairFilterSet.Contains(packageForwardPair.Key))
                {
                    continue;
                }

                DbFhirPackageComparisonPair? packageReversePair = DbFhirPackageComparisonPair.SelectSingle(
                    _db,
                    SourcePackageKey: packageForwardPair.TargetPackageKey,
                    TargetPackageKey: packageForwardPair.SourcePackageKey);

                if (packageReversePair == null)
                {
                    throw new Exception($"Failed to resolve reverse comparison pair for {packageForwardPair.Key}");
                }

                bidirectionalPairs.Add((packageForwardPair, packageReversePair));
            }

            // consistency check
            if (bidirectionalPairs.Any(biPair => !packages.ContainsKey(biPair.forward.TargetPackageKey)))
            {
                throw new Exception("Failed to resolve packages in all pairwise comparisons!");
            }

            if ((artifactFilter == null) ||
                (artifactFilter == FhirArtifactClassEnum.ValueSet))
            {
                _vsComparisonCache.Clear();
                _conceptComparisonCache.Clear();

                List<DbValueSet> valueSets = DbValueSet.SelectList(_db, FhirPackageKey: sourcePackage.Key);
                _logger.LogInformation($" <<< processing {sourcePackage.FhirVersionShort} ValueSets, count: {valueSets.Count}");

                // iterate over value sets in the package
                foreach (DbValueSet sourceVs in valueSets)
                {
                    // if we are not allowing updates, we need to see if this value set has been compared before
                    if (allowUpdates == false)
                    {
                        int matchCount = DbValueSetComparison.SelectCount(
                            _db,
                            SourceFhirPackageKey: sourcePackage.Key,
                            SourceValueSetKey: sourceVs.Key);

                        if (matchCount != 0)
                        {
                            continue;
                        }
                    }

                    _logger.LogInformation($" <<< processing ValueSet {sourceVs.VersionedUrl}");

                    // iterate over the comparison pairs
                    foreach ((DbFhirPackageComparisonPair forward, DbFhirPackageComparisonPair reverse) in bidirectionalPairs)
                    {
                        // grab our target package
                        DbFhirPackage targetPackage = packages[forward.TargetPackageKey];

                        doValueSetComparisons(
                            sourcePackage,
                            sourceVs,
                            targetPackage,
                            forward,
                            reverse);
                    }
                }

                // update the database
                _vsComparisonCache.ToAdd.Insert(_db, insertPrimaryKey: true);
                _vsComparisonCache.ToUpdate.Update(_db);
                _conceptComparisonCache.ToAdd.Insert(_db, insertPrimaryKey: true);
                _conceptComparisonCache.ToUpdate.Update(_db);
            }

            // any structure triggers all of them
            if ((artifactFilter == null) ||
                (artifactFilter == FhirArtifactClassEnum.PrimitiveType) ||
                (artifactFilter == FhirArtifactClassEnum.ComplexType) ||
                (artifactFilter == FhirArtifactClassEnum.Resource) ||
                (artifactFilter == FhirArtifactClassEnum.Profile) ||
                (artifactFilter == FhirArtifactClassEnum.LogicalModel))
            {
                _sdComparisonCache.Clear();
                _edComparisonCache.Clear();
                _collatedTypeComparisonCache.Clear();
                _typeComparisonCache.Clear();

                // iterate over our artifact types
                foreach (FhirArtifactClassEnum artifactClass in getArtifactClassSequence())
                {
                    List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(_db, FhirPackageKey: sourcePackage.Key, ArtifactClass: artifactClass);
                    _logger.LogInformation($" <<< processing Structures:{artifactClass}, count: {structures.Count}");

                    // iterate over the structures in the package
                    foreach (DbStructureDefinition sourceSd in structures)
                    {
                        // if we are not allowing updates, we need to see if this structure has been compared before
                        if (allowUpdates == false)
                        {
                            int matchCount = DbStructureComparison.SelectCount(
                                _db,
                                SourceFhirPackageKey: sourcePackage.Key,
                                SourceStructureKey: sourceSd.Key);

                            if (matchCount != 0)
                            {
                                continue;
                            }
                        }

                        _logger.LogInformation($" <<< processing Structure:{artifactClass} {sourceSd.VersionedUrl}");

                        // iterate over the comparison pairs
                        foreach ((DbFhirPackageComparisonPair forward, DbFhirPackageComparisonPair reverse) in bidirectionalPairs)
                        {
                            // grab our target package
                            DbFhirPackage targetPackage = packages[forward.TargetPackageKey];

                            doStructureComparisons(
                                sourcePackage,
                                sourceSd,
                                targetPackage,
                                forward,
                                reverse);
                        }
                    }
                }

                // update the database
                _sdComparisonCache.ToAdd.Insert(_db, insertPrimaryKey: true);
                _sdComparisonCache.ToUpdate.Update(_db);
                _edComparisonCache.ToAdd.Insert(_db, insertPrimaryKey: true);
                _edComparisonCache.ToUpdate.Update(_db);
                _collatedTypeComparisonCache.ToAdd.Insert(_db, insertPrimaryKey: true);
                _collatedTypeComparisonCache.ToUpdate.Update(_db);
                _typeComparisonCache.ComparisonsToAdd.Insert(_db, insertPrimaryKey: true);
                _typeComparisonCache.ComparisonsToUpdate.Update(_db);
            }
        }

        doElementComparisonPostProcessing();

        return;

        FhirArtifactClassEnum[] getArtifactClassSequence() => [
            FhirArtifactClassEnum.PrimitiveType,
            FhirArtifactClassEnum.ComplexType,
            FhirArtifactClassEnum.Resource,
            FhirArtifactClassEnum.Profile,
            FhirArtifactClassEnum.LogicalModel,
            ];
    }
#endif

#if false
    private void doElementComparisonPostProcessing()
    {
        {
            DbFhirPackage r3 = DbFhirPackage.SelectSingle(_db, DefinitionFhirSequence: Common.Packaging.FhirReleases.FhirSequenceCodes.STU3)
                ?? throw new Exception("Failed to resolve FHIR Package STU3!");

            DbFhirPackage r4 = DbFhirPackage.SelectSingle(_db, DefinitionFhirSequence: Common.Packaging.FhirReleases.FhirSequenceCodes.R4)
                ?? throw new Exception("Failed to resolve FHIR Package R4!");

            DbElementComparison? r3r4BindingVs = DbElementComparison.SelectSingle(
                _db,
                SourceFhirPackageKey: r3.Key,
                TargetFhirPackageKey: r4.Key,
                SourceElementToken: "ElementDefinition.binding.valueSet[x]",
                TargetElementToken: "ElementDefinition.binding.valueSet");

            if (r3r4BindingVs != null)
            {
                r3r4BindingVs.Relationship = CMR.Equivalent;
                r3r4BindingVs.ValueDomainRelationship = CMR.Equivalent;
                r3r4BindingVs.TechnicalMessage = "While the type of the element changed, all known contents are compatible.";
                r3r4BindingVs.UserMessage = "The element type changed between STU3 and R4, but the contents are known compatible.";

                r3r4BindingVs.Update(_db);
            }

            DbElementComparison? r4r3BindingVs = DbElementComparison.SelectSingle(
                _db,
                SourceFhirPackageKey: r4.Key,
                TargetFhirPackageKey: r3.Key,
                SourceElementToken: "ElementDefinition.binding.valueSet",
                TargetElementToken: "ElementDefinition.binding.valueSet[x]");

            if (r4r3BindingVs != null)
            {
                r4r3BindingVs.Relationship = CMR.Equivalent;
                r4r3BindingVs.ValueDomainRelationship = CMR.Equivalent;
                r4r3BindingVs.TechnicalMessage = "While the type of the element changed, all known contents are compatible.";
                r4r3BindingVs.UserMessage = "The element type changed between STU3 and R4, but the contents are known compatible.";

                r4r3BindingVs.Update(_db);
            }
        }
    }
#endif

    internal static CMR? RelationshipForCounts(int? sourceCount, int? targetCount)
    {
        // zero counts mean the target cannot be evaluated
        if ((sourceCount == null) || (targetCount == null) ||
            (sourceCount == 0) || (targetCount == 0))
        {
            return null;
        }

        return ((int)sourceCount).CompareTo((int)targetCount) switch
        {
            < 0 => CMR.SourceIsNarrowerThanTarget,
            > 0 => CMR.SourceIsBroaderThanTarget,
            _ => CMR.Equivalent,
        };
    }

    internal static CMR ApplyRelationship(CMR? existing, CMR? change) => existing switch
    {
        CMR.Equivalent => change ?? CMR.Equivalent,
        CMR.RelatedTo => (change == CMR.NotRelatedTo) ? CMR.NotRelatedTo : CMR.RelatedTo,
        CMR.SourceIsNarrowerThanTarget => (change == CMR.SourceIsNarrowerThanTarget || change == CMR.Equivalent)
            ? CMR.SourceIsNarrowerThanTarget : CMR.RelatedTo,
        CMR.SourceIsBroaderThanTarget => (change == CMR.SourceIsBroaderThanTarget || change == CMR.Equivalent)
            ? CMR.SourceIsBroaderThanTarget : CMR.RelatedTo,
        CMR.NotRelatedTo => change ?? CMR.NotRelatedTo,
        _ => change ?? existing ?? CMR.NotRelatedTo,
    };


#if false
    private DbFhirPackageComparisonPair invert(DbFhirPackageComparisonPair other)
    {
        return new()
        {
            InverseComparisonKey = other.Key,
            SourcePackageKey = other.TargetPackageKey,
            SourcePackageShortName = other.TargetPackageShortName,
            TargetPackageKey = other.SourcePackageKey,
            TargetPackageShortName = other.SourcePackageShortName,
        };
    }
#endif


    private CMR? invert(CMR? existing) => existing switch
    {
        CMR.RelatedTo => CMR.RelatedTo,
        CMR.Equivalent => CMR.Equivalent,
        CMR.SourceIsNarrowerThanTarget => CMR.SourceIsBroaderThanTarget,
        CMR.SourceIsBroaderThanTarget => CMR.SourceIsNarrowerThanTarget,
        CMR.NotRelatedTo => CMR.NotRelatedTo,
        _ => null,
    };

}
