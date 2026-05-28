using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Hl7.Fhir.Model;
using Hl7.Fhir.Model.CdsHooks;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Octokit;
using static Hl7.Fhir.Model.PaymentReconciliation;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Outcomes;

public class ElementOutcomeGenerator
{
    private class ElementOutcomeTrackingRecord
    {
        public required DbElement SourceElement { get; set; }
        public required int ElementOutcomeKey { get; init; }
        public DbElementOutcome? ElementOutcome { get; set; } = null;
        public List<DbElementComparison> ElementComparisons { get; set; } = [];
        public Dictionary<int, DbElement> TargetElements { get; set; } = [];
        public List<DbElementOutcomeTarget> OutcomeTargets { get; set; } = [];
        public List<string> Messages { get; set; } = [];
        public bool IsFullyMappedAcrossAllTargets { get; set; } = false;
        public List<DbElementComparison> MapsToIndividualTargets { get; set; } = [];
        public List<DbElementComparison> MapsToCombinationOfTargets { get; set; } = [];
        public int DiscreteTargetCount { get; set; } = 0;
        public CMR? QuantityBasedRelationship { get; set; } = null;
        public CMR? BoundValueSetRelationship { get; set; } = null;
        public List<DbElementType> MappedTypes { get; set; } = [];
        public List<DbElementType> UnmappedTypes { get; set; } = [];
        public List<ChildTypeMappingResult> ChildTypeMappingResults { get; set; } = [];
        public List<string> DistinctTargetElementTypes = [];
    }

    private class ChildTypeMappingResult
    {
        /// <summary>The parent source type being evaluated.</summary>
        public required DbElementType SourceParentType { get; set; }

        /// <summary>True if all significant children have valid type mappings.</summary>
        public required bool AllChildrenMapped { get; set; }

        /// <summary>Mapping information for each child that has mappings.</summary>
        public required Dictionary<int, ChildMappingInfo> ChildMappings { get; set; } = [];

        /// <summary>Child elements that have valid type mappings.</summary>
        public required List<DbElement> MappedTypeChildren { get; set; } = [];

        /// <summary>Child elements that do not have valid type mappings.</summary>
        public required List<DbElement> UnmappedTypeChildren { get; set; } = [];
    }

    private class ChildMappingInfo
    {
        /// <summary>The child element being mapped.</summary>
        public required DbElement ChildElement { get; set; }

        /// <summary>Valid type comparisons for this child.</summary>
        public required List<DbElementTypeComparison> TypeComparisons { get; set; } = [];

        /// <summary>Child element mappings relevant to this mapping.</summary>
        public required List<DbElementMapping> TypeChildMappings { get; set; } = [];

        /// <summary>Target element keys this child maps to.</summary>
        public required HashSet<int> TargetElementKeys { get; set; } = [];
    }

    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private readonly FhirPackageComparisonPair _packagePair;
    private readonly Dictionary<string, string?> _targetBasicElementPathLookup;
    private readonly Dictionary<string, DbElement> _targetBasicElementsById;

    private readonly Dictionary<string, string?> _targetExtensionElementPathLookup;
    private readonly Dictionary<string, DbElement> _targetExtensionElementsById;


    private readonly Dictionary<int, DbElement> _allSourceElements;
    private readonly ILookup<int, DbElement> _allSourceElementsBySdKey;

    private readonly Dictionary<int, DbElement> _allTargetElements;
    private readonly ILookup<int, DbElement> _allTargetElementsBySdKey;

    private readonly List<DbElementComparison> _edComparisons;
    private readonly ILookup<int, DbElementComparison> _edComparsionsBySourceElementKey;
    private readonly ILookup<int, DbElementComparison> _edComparsionsBySourceStructureKey;

    private readonly Dictionary<int, DbElementType> _allSourceElementTypes;
    private readonly ILookup<int, DbElementType> _sourceElementTypesByElementKey;

    private readonly Dictionary<int, DbElementType> _allTargetElementTypes;
    private readonly ILookup<int, DbElementType> _targetElementTypesByElementKey;

    private readonly List<DbElementTypeComparison> _etcComparisons;
    private readonly ILookup<int, DbElementTypeComparison> _etcComparisonsBySourceElementKey;

    private readonly Dictionary<string, DbExtensionSubstitution> _extensionSubstitutionsByElementId;
    private readonly Dictionary<string, DbExtensionSubstitution> _typeExtensionSubstitutionsByUrl;

    private readonly HashSet<string> _sourceResourceTypes;
    private readonly HashSet<string> _targetResourceTypes;

    private DbRecordCache<DbElementOutcome> _edOutcomeCache;
    private DbRecordCache<DbElementOutcomeTarget> _edOutcomeTargetCache;

    public ElementOutcomeGenerator(
        IDbConnection db,
        ILoggerFactory loggerFactory,
        FhirPackageComparisonPair packagePair,
        DbRecordCache<DbElementOutcome> edOutcomeCache,
        DbRecordCache<DbElementOutcomeTarget> edOutcomeTargetCache)
    {
        _db = db;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ElementOutcomeGenerator>();
        _packagePair = packagePair;
        _edOutcomeCache = edOutcomeCache;
        _edOutcomeTargetCache = edOutcomeTargetCache;

        _allSourceElements = DbElement.SelectDict(_db, FhirPackageKey: _packagePair.SourcePackageKey);
        _allTargetElements = DbElement.SelectDict(_db, FhirPackageKey: _packagePair.TargetPackageKey);

        _edComparisons = DbElementComparison.SelectList(
            _db,
            SourceFhirPackageKey: packagePair.SourcePackageKey,
            TargetFhirPackageKey: packagePair.TargetPackageKey);

        _logger.LogInformation(
            $"Element comparisons: {_edComparisons.Count}, from" +
            $" {_allSourceElements.Count} Elements" +
            $" to {_allTargetElements.Count} Elements");

        _allSourceElementTypes = DbElementType.SelectDict(_db, FhirPackageKey: packagePair.SourcePackageKey);
        _allTargetElementTypes = DbElementType.SelectDict(_db, FhirPackageKey: packagePair.TargetPackageKey);

        _etcComparisons = DbElementTypeComparison.SelectList(
            _db,
            SourceFhirPackageKey: packagePair.SourcePackageKey,
            TargetFhirPackageKey: packagePair.TargetPackageKey);

        _logger.LogInformation(
            $"Element Type comparisons: {_etcComparisons.Count}, from" +
            $" {_allSourceElementTypes.Count} Element Types" +
            $" to {_allTargetElementTypes.Count} Element Types");

        _allSourceElementsBySdKey = _allSourceElements.Values.ToLookup(c => c.StructureKey);
        _allTargetElementsBySdKey = _allTargetElements.Values.ToLookup(c => c.StructureKey);

        _sourceElementTypesByElementKey = _allSourceElementTypes.Values.ToLookup(c => c.ElementKey);
        _targetElementTypesByElementKey = _allTargetElementTypes.Values.ToLookup(c => c.ElementKey);

        _edComparsionsBySourceElementKey = _edComparisons.ToLookup(c => c.SourceContentKey);
        _edComparsionsBySourceStructureKey = _edComparisons.ToLookup(c => c.SourceStructureKey);

        _etcComparisonsBySourceElementKey = _etcComparisons.ToLookup(c => c.SourceElementKey);

        _sourceResourceTypes = [];
        foreach (DbStructureDefinition sd in DbStructureDefinition.SelectList(_db, FhirPackageKey: packagePair.SourcePackageKey, ArtifactClass: FhirArtifactClassEnum.Resource))
        {
            _sourceResourceTypes.Add(sd.Name);
        }

        _targetResourceTypes = [];
        foreach (DbStructureDefinition sd in DbStructureDefinition.SelectList(_db, FhirPackageKey: packagePair.TargetPackageKey, ArtifactClass: FhirArtifactClassEnum.Resource))
        {
            _targetResourceTypes.Add(sd.Name);
        }

        _targetBasicElementPathLookup = [];
        _targetBasicElementsById = [];

        // check for a basic structure
        DbStructureDefinition? targetBasicResource = DbStructureDefinition.SelectSingle(
            _db,
            FhirPackageKey: _packagePair.TargetPackageKey,
            Name: "Basic",
            ArtifactClass: FhirArtifactClassEnum.Resource);
        if (targetBasicResource != null)
        {
            // get the elements for this structure
            List<DbElement> targetBasicElements = DbElement.SelectList(
                _db,
                StructureKey: targetBasicResource.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

            // iterate over the elements
            foreach (DbElement element in targetBasicElements)
            {
                // skip root, elements with empty paths, and `code` element
                if ((element.ResourceFieldOrder == 0) ||
                    string.IsNullOrEmpty(element.Path) ||
                    element.Path.Equals("Basic.code", StringComparison.Ordinal))
                {
                    continue;
                }

                // add the path to the dictionary, but strip "Basic" from the front
                _targetBasicElementPathLookup.Add(element.Path.Substring(5), element.BasePath);
                _targetBasicElementsById.Add(element.Id, element);
                if (element.BasePath is not null)
                {
                    _targetBasicElementsById[element.BasePath] = element;
                }
            }
        }

        _targetExtensionElementPathLookup = [];
        _targetExtensionElementsById = [];

        // check for a basic structure
        DbStructureDefinition? targetExtensionType = DbStructureDefinition.SelectSingle(
            _db,
            FhirPackageKey: _packagePair.TargetPackageKey,
            Name: "Extension",
            ArtifactClass: FhirArtifactClassEnum.ComplexType);
        if (targetExtensionType != null)
        {
            // get the elements for this structure
            List<DbElement> targetExtensionElements = DbElement.SelectList(
                _db,
                StructureKey: targetExtensionType.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

            // iterate over the elements
            foreach (DbElement element in targetExtensionElements)
            {
                // skip root and elements with empty paths
                if ((element.ResourceFieldOrder == 0) ||
                    string.IsNullOrEmpty(element.Path))
                {
                    continue;
                }

                // add the path to the dictionary, but strip "Extension" from the front
                _targetExtensionElementPathLookup.Add(element.Path.Substring(9), element.BasePath);
                _targetExtensionElementsById.Add(element.Id, element);
                if (element.BasePath is not null)
                {
                    _targetExtensionElementsById[element.BasePath] = element;
                }
            }
        }

        // build our extension substitution lookup
        List<DbExtensionSubstitution> extensionSubstitutions = DbExtensionSubstitution.SelectList(
            _db,
            SourceFhirSequence: _packagePair.SourceFhirSequence);

        extensionSubstitutions.AddRange(DbExtensionSubstitution.SelectList(_db, SourceFhirSequenceIsNull: true));

        _extensionSubstitutionsByElementId = [];
        _typeExtensionSubstitutionsByUrl = [];
        foreach (DbExtensionSubstitution extSub in extensionSubstitutions)
        {
            if (extSub.SourceTypeReplacement is not null)
            {
                _typeExtensionSubstitutionsByUrl[extSub.ReplacementUrl] = extSub;
            }

            if (extSub.SourceElementId is not null)
            {
                _extensionSubstitutionsByElementId[extSub.SourceElementId] = extSub;

                string sourceElementClean = extSub.SourceElementId.Replace("[x]", string.Empty, StringComparison.Ordinal);
                if (sourceElementClean != extSub.SourceElementId)
                {
                    _extensionSubstitutionsByElementId[sourceElementClean] = extSub;
                }
            }

            if (extSub.SourceFromContextExpandedLiteral is not null)
            {
                foreach (string ctxElementId in extSub.SourceFromContextExpanded)
                {
                    _extensionSubstitutionsByElementId[ctxElementId] = extSub;
                    string ctxElementClean = ctxElementId.Replace("[x]", string.Empty, StringComparison.Ordinal);
                    if (ctxElementClean != ctxElementId)
                    {
                        _extensionSubstitutionsByElementId[ctxElementClean] = extSub;
                    }
                }
            }
        }
    }

    internal void DoPostProcessing(FhirPackageComparisonPair packagePair)
    {
        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        // find elements that are not exporting, but need to based on child element state (do we need this?)

        // find elements that are not exporting, but are used as a content reference and any of those elements are
        doContentReferenceChecks(sourcePackage, targetPackage);
    }

    private void doContentReferenceChecks(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage)
    {
        // get the element outcomes that are used as content references and not exporting extensions
        List<DbElementOutcome> edOutcomes = DbElementOutcome.SelectList(
            _db,
            SourceFhirPackageKey: sourcePackage.Key,
            TargetFhirPackageKey: targetPackage.Key,
            SourceUsedAsContentReference: true,
            RequiresExtensionDefinition: false);

        // iterate acros the outcomes to check if any targets need extension definitions
        foreach (DbElementOutcome edOutcome in edOutcomes)
        {
            // check if any require this to have an extension
           int count = DbElementOutcome.SelectCount(
                _db,
                SourceFhirPackageKey: sourcePackage.Key,
                TargetFhirPackageKey: targetPackage.Key,
                ContentReferenceOutcomeKey: edOutcome.Key,
                RequiresXVerDefinition: true);

            if (count != 0)
            {
                edOutcome.RequiresExtensionDefinition = true;
                edOutcome.Comments += $"\nCreating extension because element `{edOutcome.SourceId}` is a content reference and requires an extesion definition.";
                _edOutcomeCache.CacheUpdate(edOutcome);
            }
        }
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

    private static readonly char[] _literalSplitChars = [',', '(', ')', '[', ']'];

    private bool canMapElementsByType(
        DbElement sourceEd,
        DbElement targetEd)
    {
        string[] sourceBaseTypes = sourceEd.FullCollatedTypeLiteral
            .Split(_literalSplitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<string> targetBaseTypes = targetEd.FullCollatedTypeLiteral
            .Split(_literalSplitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

        foreach (string sbt in sourceBaseTypes)
        {
            if (sbt.Contains('/') || sbt.Contains(':'))
            {
                continue;
            }

            if (!targetBaseTypes.Contains(sbt))
            {
                return false;
            }
        }

        return true;
    }

    //public void ProcessNoMapStructure(
    //    DbStructureDefinition sourceSd,
    //    DbStructureOutcome sdOutcome)
    //{
    //    if (sourceSd.ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType)
    //    {
    //        return;
    //    }

    //    // get the elements of this structure, but filter out elements we should ignore
    //    List<DbElement> sourceElements = _allSourceElementsBySdKey[sourceSd.Key]
    //        .Where(ed => !skipElement(ed, skipFirstElement: false))
    //        .ToList();

    //    if (sourceElements.Count == 0)
    //    {
    //        // this is a special type like 'datatype', just itnore it
    //        return;
    //    }

    //    DbElement rootEd = sourceElements[0];
    //    DbElementOutcome? rootEdOutcome = null;

    //    Dictionary<int, DbElementOutcome> edKeyOutcomeLookup = [];
    //    //HashSet<int> outcomesRequiringXver = [];

    //    // iterate over the source elements for this structure to create no-map edOutcomes
    //    foreach (DbElement sourceEd in sourceElements.OrderBy(ed => ed.ResourceFieldOrder))
    //    {
    //        DbElement? contentRefEd = null;
    //        DbElementOutcome? contentRefEdOutcome = null;
    //        if (sourceEd.ContentReferenceSourceKey is not null)
    //        {
    //            contentRefEd = _allSourceElements[sourceEd.ContentReferenceSourceKey.Value];
    //            if (!edKeyOutcomeLookup.TryGetValue(contentRefEd.Key, out contentRefEdOutcome))
    //            {
    //                throw new Exception(
    //                    $"Could not find outcome for content reference element with key {contentRefEd.Key} and id `{contentRefEd.Id}` while processing element with key {sourceEd.Key} and id `{sourceEd.Id}`.");
    //            }
    //        }

    //        DbElement? parentEd = sourceEd.ParentElementKey is null
    //            ? null
    //            : _allSourceElements[sourceEd.ParentElementKey.Value];

    //        List<DbElementType> sourceEts = _sourceElementTypesByElementKey[sourceEd.Key]
    //            .OrderBy(et => et.Literal)
    //            .ToList();

    //        (string idLong, string idShort, string name) = XVerProcessor.GenerateExtensionId(
    //            _packagePair.SourcePackageShortName,
    //            sourceEd.Id);

    //        string extUrl = $"http://hl7.org/fhir/{_packagePair.SourceFhirVersionShort}/StructureDefinition/{idLong}";
    //        string? extFilename = $"StructureDefinition-{idShort}";

    //        string comments =
    //            $"Element `{sourceEd.Id}` is not mapped to FHIR {_packagePair.TargetFhirSequence}," +
    //            $" since FHIR {_packagePair.SourceFhirSequence} `{sourceSd.Name}` is not mapped.";

    //        string artifactShort = $"{_packagePair.SourceFhirSequence}: {sourceEd.Short ?? sourceEd.NameClean()} (new)";
    //        string artifactDescription = sourceEd.Definition is null
    //            ? $"Cross-version extension to represent the {_packagePair.SourceFhirSequence} element `{sourceEd.Id}`"
    //            : $"{_packagePair.SourceFhirSequence}: {sourceEd.Definition}";

    //        // cannot generate extensions for root elements
    //        bool requiresXVerDefinition = sourceEd.ResourceFieldOrder != 0;

    //        DbElementOutcome? parentOutcome = null;

    //        if ((sourceEd.ParentElementKey is not null) &&
    //            edKeyOutcomeLookup.TryGetValue(sourceEd.ParentElementKey!.Value, out DbElementOutcome? parentOutcomeValue))
    //        {
    //            parentOutcome = parentOutcomeValue;
    //            requiresXVerDefinition = requiresXVerDefinition || parentOutcome.RequiresXVerDefinition;
    //        }

    //        List<string> contexts = [];

    //        bool defineAsModifier = sourceEd.IsModifier;
    //        if (defineAsModifier &&
    //            (sourceEd.ParentElementKey == rootEd.Key) &&
    //            (rootEdOutcome is not null))
    //        {
    //            rootEdOutcome.DefineAsModifier = true;
    //            rootEdOutcome.ModifierReason =
    //                $"Element `{sourceEd.Id}` is a modifier and is a direct child of the root element," +
    //                $" so the extension definition for `{sourceSd.Name}` must be a modifier.";
    //            rootEdOutcome.Comments += "\n" + rootEdOutcome.ModifierReason;
    //        }

    //        if (sourceEd.ResourceFieldOrder == 0)
    //        {
    //            contexts.Add("Basic");
    //        }
    //        else if (parentOutcome is not null)
    //        {
    //            contexts.Add(parentOutcome.GenLongId ?? parentOutcome.SourceName);
    //        }

    //        // check to see if we are trying to define an extension onto basic that has a matching basic path and compatible type
    //        string? basicBasePath = null;
    //        string? basicPath = null;
    //        if (requiresXVerDefinition &&
    //            _targetBasicElementPathLookup.TryGetValue(sourceEd.Path.Substring(sourceSd.Name.Length), out basicBasePath) &&
    //            _targetBasicElementsById.TryGetValue(basicBasePath!, out DbElement? basicEd))
    //        {
    //            if (canMapElementsByType(sourceEd, basicEd) &&
    //                (sourceEd.MinCardinality >= basicEd.MinCardinality) &&
    //                ((basicEd.MaxCardinality == -1) || (basicEd.MaxCardinality >= sourceEd.MaxCardinality)) &&
    //                (   ((sourceEd.ChildElementCount == 0) && (basicEd.ChildElementCount == 0)) ||
    //                    ((sourceEd.ChildElementCount > 0) && (basicEd.ChildElementCount > 0))))
    //            {
    //                basicPath = basicEd.Id;
    //                requiresXVerDefinition = false;
    //                comments +=
    //                    $"\nElement matches Basic element path `{basicBasePath}` and is compatible," +
    //                    $" use that element instead.";
    //                contexts = [];
    //                parentOutcome = null;
    //            }
    //            else
    //            {
    //                comments +=
    //                    $"\nThe source element matches Basic element path `{basicBasePath}`," +
    //                    $" but the definitions are not compatible" +
    //                    $" (source: `{sourceEd.FullCollatedTypeLiteral}`:{sourceEd.FhirCardinalityString}" +
    //                    $" -> basic: `{basicEd.FullCollatedTypeLiteral}`:{basicEd.FhirCardinalityString}).";
    //                basicBasePath = null;
    //                basicPath = null;
    //            }
    //        }

    //        if (_extensionSubstitutionsByElementId.TryGetValue(sourceEd.Id, out DbExtensionSubstitution? extSubstitute))
    //        {
    //            comments +=
    //                $"\nThere is an externally-defined extension that has been mapped as the" +
    //                $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}`:" +
    //                $" `{extSubstitute.ReplacementUrl}`.";
    //        }

    //        if (sourceEd.IsDeprecated)
    //        {
    //            comments += $"\nElement `{sourceEd.Id}` has been flagged as deprecated.";
    //        }

    //        string? ancestorCR = parentEd?.UsedAsContentReference == true
    //            ? parentEd.Id
    //            : parentOutcome?.SourceAncestorUsedAsContentReferenceId;

    //        int? ancestorCrKey = parentEd?.UsedAsContentReference == true
    //            ? parentOutcome?.Key
    //            : parentOutcome?.SourceAncestorContentReferenceOutcomeKey;

    //        bool requiresSliceDefinition = requiresXVerDefinition && (parentOutcome?.RequiresXVerDefinition ?? false);
    //        bool requiresExtensionDefinition = requiresXVerDefinition && !requiresSliceDefinition;

    //        bool extensionDefinitionIsProhibited = false;
    //        string? extensionProhibitionReason = null;

    //        List<string> sourceDistinctTypes = sourceEd.DistinctTypes;

    //        // check for invalid source types that cannot have extensions
    //        if (sourceDistinctTypes.Contains("Resource") ||
    //            sourceDistinctTypes.Any(v => _sourceResourceTypes.Contains(v)) ||
    //            sourceDistinctTypes.Any(v => _targetResourceTypes.Contains(v)))
    //        {
    //            extensionDefinitionIsProhibited = true;
    //            extensionProhibitionReason = $"Element is a resource type that cannot be represented via extensions.";
    //        }

    //        // create the non-mapped element outcome
    //        DbElementOutcome elementOutcome = new()
    //        {
    //            Key = DbElementOutcome.GetIndex(),

    //            SourceFhirPackageKey = _packagePair.SourcePackageKey,
    //            SourceFhirSequence = _packagePair.SourceFhirSequence,
    //            SourceStructureKey = sourceSd.Key,
    //            SourceElementKey = sourceEd.Key,
    //            SourceCanonicalUnversioned = sourceSd.UnversionedUrl,
    //            SourceCanonicalVersioned = sourceSd.VersionedUrl,
    //            SourceVersion = sourceSd.Version,
    //            SourceId = sourceEd.Id,
    //            SourceName = sourceEd.Name,
    //            SourceResourceOrder = sourceEd.ResourceFieldOrder,
    //            SourceComponentOrder = sourceEd.ComponentFieldOrder,
    //            SourceMinCardinality = sourceEd.MinCardinality,
    //            SourceMaxCardinalityString = sourceEd.MaxCardinalityString,
    //            SourceChildElementCount = sourceEd.ChildElementCount,
    //            SourceUsedAsContentReference = sourceEd.UsedAsContentReference == true,
    //            SourceAncestorUsedAsContentReferenceId = ancestorCR,
    //            SourceAncestorContentReferenceOutcomeKey = ancestorCrKey,
    //            SourceIsDeprecated = sourceEd.IsDeprecated,
    //            TotalSourceCount = -1,

    //            TargetFhirPackageKey = _packagePair.TargetPackageKey,
    //            TargetFhirSequence = _packagePair.TargetFhirSequence,
    //            TotalTargetCount = 0,
    //            OutcomeTargetCount = 1,

    //            ExtensionDefinitionIsProhibited = extensionDefinitionIsProhibited,
    //            ExtensionProhibitionReason = extensionProhibitionReason,

    //            RequiresXVerDefinition = requiresXVerDefinition,
    //            RequiresExtensionDefinition = requiresExtensionDefinition,
    //            RequiresSliceDefinition = requiresSliceDefinition,
    //            GenLongId = idLong,
    //            GenShortId = idShort,
    //            GenUrl = extUrl,
    //            GenName = name,
    //            GenFileName = extFilename,
    //            GenSliceName = sourceEd.NameClean(),

    //            GenArtifactShort = artifactShort,
    //            GenArtifactDescription = artifactDescription,
    //            GenArtifactComment = sourceEd.Comments is null
    //                ? comments
    //                : (comments + "\n" + sourceEd.Comments),
    //            GenMappingComment = sourceEd.Comments is null
    //                ? comments
    //                : (comments + "\n" + sourceEd.Comments),

    //            ContentReferenceOutcomeKey = contentRefEdOutcome?.Key,
    //            ContentReferenceExtensionUrl = contentRefEdOutcome?.GenUrl,
    //            ContentReferenceRequiresXVerDefinition = contentRefEdOutcome?.RequiresXVerDefinition,
    //            ContentReferenceAncestorId = contentRefEdOutcome?.SourceId ?? parentOutcome?.ContentReferenceAncestorId,
    //            RequiresDefinitionAsContentReference = (contentRefEdOutcome is not null) && requiresXVerDefinition,

    //            AncestorElementOutcomeKey = requiresXVerDefinition ? rootEdOutcome?.Key : null,
    //            ParentElementOutcomeKey = parentOutcome?.Key,
    //            SourceIsModifier = sourceEd.IsModifier,
    //            DefineAsModifier = defineAsModifier,
    //            ModifierReason = sourceEd.IsModifierReason,
    //            ExtensionContexts = contexts,
    //            BasicElementBaseId = basicBasePath,
    //            BasicElementId = basicPath,
    //            ExtensionSubstitutionKey = extSubstitute?.Key,
    //            ExtensionSubstitutionUrl = extSubstitute?.ReplacementUrl,

    //            IsRenamed = false,
    //            IsUnmapped = true,
    //            IsIdentical = false,
    //            IsEquivalent = false,
    //            IsBroaderThanTarget = false,
    //            IsNarrowerThanTarget = false,

    //            FullyMapsToThisTarget = false,
    //            FullyMapsAcrossAllTargets = false,
    //            UnmappedTypeKeys = sourceEts.Select(et => et.Key).ToList(),
    //            UnmappedTypeNames = sourceEts.Select(et => et.Literal).ToList(),

    //            Comments = comments,
    //        };

    //        if (sourceEd.ResourceFieldOrder == 0)
    //        {
    //            rootEdOutcome = elementOutcome;
    //        }

    //        edKeyOutcomeLookup[sourceEd.Key] = elementOutcome;

    //        _edOutcomeCache.CacheAdd(elementOutcome);

    //        // create the matching no-map outcome target
    //        DbElementOutcomeTarget nmEOT = new()
    //        {
    //            Key = DbElementOutcomeTarget.GetIndex(),
    //            ElementOutcomeKey = elementOutcome.Key,
    //            StructureOutcomeKey = sdOutcome.Key,
    //            ElementComparisonKey = null,
    //            SourceFhirPackageKey = _packagePair.SourcePackageKey,
    //            SourceFhirSequence = _packagePair.SourceFhirSequence,
    //            TargetFhirPackageKey = _packagePair.TargetPackageKey,
    //            TargetFhirSequence = _packagePair.TargetFhirSequence,
    //            TargetStructureKey = null,
    //            TargetElementKey = null,
    //            TargetElementId = null,
    //            TargetResourceOrder = null,
    //            TargetComponentOrder = null,
    //            TargetMinCardinality = null,
    //            TargetMaxCardinalityString = null,
    //            TargetIsModifier = null,
    //            ContextElementKey = null,
    //            ContextElementId = null,
    //            ContextRootExtensionUrl = null,
    //            ContextParentExtensionUrl = null,
    //            FullyMapsToThisTarget = false,
    //            Comments = comments,
    //        };

    //        _edOutcomeTargetCache.CacheAdd(nmEOT);
    //    }
    //}

    private bool relationshipMaps(CMR? relationship) =>
        (relationship is null) ||
        (relationship == CMR.Equivalent) ||
        (relationship == CMR.SourceIsNarrowerThanTarget);

    private bool skipType(string typeName) => typeName switch
    {
        "BackboneElement" => true,
        "Base" => true,
        "DataType" => true,
        "Element" => true,
        "Resource" => true,
        "DomainResource" => true,
        _ => false,
    };

    public void ProcessSourceStructure(
        DbStructureDefinition sourceSd,
        Dictionary<int, StructureOutcomeGenerator.StructureOutcomeTrackingRecord> structureTrackingRecords)
    {
        if (sourceSd.ArtifactClass == Common.Models.FhirArtifactClassEnum.PrimitiveType)
        {
            return;
        }

        Dictionary<int, DbElement> sourceElements = _allSourceElementsBySdKey[sourceSd.Key]
            .Where(ed => !skipElement(ed, skipFirstElement: false))
            .ToDictionary(c => c.Key);

        Dictionary<int, ElementOutcomeTrackingRecord> elementTrackingRecords = [];

        determineMappingCompleteness(
            sourceElements,
            elementTrackingRecords);

        checkValueSetMappings(elementTrackingRecords);

        Dictionary<int, (DbElementOutcome outcome, DbElementOutcome? rootOutcome)> edKeyOutcomeLookup = [];
        List<DbElementOutcome> rootElementOutcomes = [];

        createOutcomes(
            elementTrackingRecords,
            structureTrackingRecords,
            edKeyOutcomeLookup,
            rootElementOutcomes,
            sourceSd);
    }

    private void createOutcomes(
        Dictionary<int, ElementOutcomeTrackingRecord> elementTrackingRecords,
        Dictionary<int, StructureOutcomeGenerator.StructureOutcomeTrackingRecord> structureTrackingRecords,
        Dictionary<int, (DbElementOutcome outcome, DbElementOutcome? rootOutcome)> edKeyOutcomeLookup,
        List<DbElementOutcome> rootElementOutcomes,
        DbStructureDefinition sourceSd)
    {
        // iterate over our element tracking records to create edOutcomes
        foreach (ElementOutcomeTrackingRecord edTr in elementTrackingRecords.Values.OrderBy(etr => etr.SourceElement.ResourceFieldOrder))
        {
            // don't create element edOutcomes for unmapped primitive types - they are handled at the structure level
            if (sourceSd.ArtifactClass == FhirArtifactClassEnum.PrimitiveType)
            {
                continue;
            }

            DbElement sourceEd = edTr.SourceElement;

            DbElement? contentRefEd = null;
            DbElementOutcome? contentRefEdOutcome = null;
            if (sourceEd.ContentReferenceSourceKey is not null)
            {
                contentRefEd = _allSourceElements[sourceEd.ContentReferenceSourceKey.Value];
                if (edKeyOutcomeLookup.TryGetValue(contentRefEd.Key, out (DbElementOutcome outcome, DbElementOutcome? rootOutcome) contentRefOutcomeLookupRec))
                {
                    contentRefEdOutcome = contentRefOutcomeLookupRec.outcome;
                }
                else
                {
                    throw new Exception(
                        $"Could not find outcome for content reference element with key {contentRefEd.Key} and id `{contentRefEd.Id}` while processing element with key {sourceEd.Key} and id `{sourceEd.Id}`.");
                }
            }

            DbElement? parentEd = sourceEd.ParentElementKey is null
                ? null
                : _allSourceElements[sourceEd.ParentElementKey.Value];

            List<DbElementComparison> elementComparisons = edTr.ElementComparisons;

            bool sourceIsRootEd = sourceEd.ResourceFieldOrder == 0;

            (string idLong, string idShort, string name) = XVerProcessor.GenerateExtensionId(
                _packagePair.SourcePackageShortName,
                sourceEd.Id);

            string extUrl = $"http://hl7.org/fhir/{_packagePair.SourceFhirVersionShort}/StructureDefinition/{idLong}";
            string? extFilename = $"StructureDefinition-{idShort}";

            HashSet<string> targetStructures = [];
            string? basicBasePath = null;
            string? basicPath = null;
            DbElement? basicEd = null;

            string? extensionBasePath = null;
            string? extensionPath = null;
            DbElement? extensionEd = null;

            DbExtensionSubstitution? extSubstitute = null;
            bool elementRequiresXVer = (sourceEd.ResourceFieldOrder != 0) &&
                (!edTr.IsFullyMappedAcrossAllTargets) &&
                (sourceEd.ChildElementCount == 0);
            List<string> outcomeComments = [];
            List<string> outcomeNotes = [];

            Dictionary<int, DbElement> allContextTargets = [];

            DbElementOutcome? ancestorOutcome = null;
            DbElementOutcome? parentOutcome = null;

            if ((parentEd is not null) &&
                edKeyOutcomeLookup.TryGetValue(parentEd.Key, out (DbElementOutcome outcome, DbElementOutcome? ancestor) po))
            {
                parentOutcome = po.outcome;
                ancestorOutcome = po.ancestor;
            }

            List<DbElementOutcomeTarget> currentElementOutcomeTargets = [];
            DbElement? altReferenceContextTargetEd = null;
            DbElement? altCanonicalContextTargetEd = null;

            // process across each target structure this element has a link to
            foreach (StructureOutcomeGenerator.StructureOutcomeTrackingRecord sdTr in structureTrackingRecords.Values)
            {
                if (sourceIsRootEd)
                {
                    sdTr.SourceRootElement = sourceEd;

                    // we cannot generate extensions for root elements of resources
                    if (sourceSd.ArtifactClass == FhirArtifactClassEnum.Resource)
                    {
                        elementRequiresXVer = false;

                        FhirArtifactClassEnum tAC = sdTr.TargetStructure?.ArtifactClass ?? FhirArtifactClassEnum.Resource;
                        string tName = sdTr.TargetStructure?.Name ?? "Basic";

                        outcomeComments.Add(
                            $"FHIR {_packagePair.SourceFhirSequence} {sourceSd.ArtifactClass} `{sourceSd.Name}`" +
                            $" is representable via" +
                            $" FHIR {_packagePair.TargetFhirSequence} {tAC} `{tName}`.");

                        //if ((tName == "Basic") &&
                        //    (sourceEd.Id != "Basic"))
                        //{
                        //    elementRequiresXVer = true;
                        //}
                    }
                    else
                    {
                        outcomeComments.Add(
                            $"FHIR {_packagePair.SourceFhirSequence} {sourceSd.ArtifactClass} `{sourceSd.Name}`" +
                            $" is representable via extensions in" +
                            $" FHIR {_packagePair.TargetFhirSequence}.");
                    }
                }

                sdTr.IsFullyMappedAcrossAllTargets = sdTr.IsFullyMappedAcrossAllTargets &&
                    edTr.IsFullyMappedAcrossAllTargets;

                List<DbElementComparison> targetSdEdComparisons;
                DbElement? contextTargetEd = null;
                string? contextRootExtensionUrl = null;
                string? contextParentExtensionUrl = null;

                string? targetComments = null;

                // if the structure target is unmapped, add a non-mapping outcome target
                if (sdTr.TargetStructure is null)
                {
                    if (sourceSd.ArtifactClass == FhirArtifactClassEnum.Resource)
                    {
                        targetStructures.Add("Basic");

                        targetSdEdComparisons = elementComparisons
                            .Where(ec => ec.TargetStructureKey is null)
                            .ToList();

                        // determine the context, if possible
                        if (sourceIsRootEd)
                        {
                            contextTargetEd = DbElement.SelectSingle(
                                _db,
                                FhirPackageKey: _packagePair.TargetPackageKey,
                                Id: "Basic",
                                ResourceFieldOrder: 0);

                            if (contextTargetEd is not null)
                            {
                                allContextTargets[contextTargetEd.Key] = contextTargetEd;
                            }
                        }
                        else
                        {
                            if ((sourceEd.ParentElementKey is not null) &&
                                edKeyOutcomeLookup.TryGetValue(sourceEd.ParentElementKey.Value, out (DbElementOutcome outcome, DbElementOutcome? rootOutcome) nmParent))
                            {
                                contextRootExtensionUrl = nmParent.rootOutcome?.GenUrl;
                                contextParentExtensionUrl = nmParent.outcome.GenUrl;
                            }
                        }

                        targetComments =
                            $"Element `{sourceEd.Id}` is not mapped to FHIR {_packagePair.TargetFhirSequence}," +
                            $" since FHIR {_packagePair.SourceFhirSequence} `{sourceSd.Name}` is not mapped.";

                        // create our target
                        DbElementOutcomeTarget nmEOT = new()
                        {
                            Key = DbElementOutcomeTarget.GetIndex(),
                            ElementOutcomeKey = edTr.ElementOutcomeKey,
                            StructureOutcomeKey = sdTr.StructureOutcomeKey,
                            ElementComparisonKey = targetSdEdComparisons.FirstOrDefault()?.Key,

                            SourceFhirPackageKey = _packagePair.SourcePackageKey,
                            SourceFhirSequence = _packagePair.SourceFhirSequence,
                            TargetFhirPackageKey = _packagePair.TargetPackageKey,
                            TargetFhirSequence = _packagePair.TargetFhirSequence,

                            TargetStructureKey = null,
                            TargetElementKey = null,
                            TargetElementId = null,
                            TargetResourceOrder = null,
                            TargetComponentOrder = null,
                            TargetMinCardinality = null,
                            TargetMaxCardinalityString = null,
                            TargetIsModifier = null,

                            ContextElementKey = contextTargetEd?.Key,
                            ContextElementId = contextTargetEd?.Id,
                            ContextRootExtensionUrl = contextRootExtensionUrl,
                            ContextParentExtensionUrl = contextParentExtensionUrl,

                            FullyMapsToThisTarget = false,
                            Comments = targetComments,
                        };

                        edTr.OutcomeTargets.Add(nmEOT);
                        _edOutcomeTargetCache.CacheAdd(nmEOT);
                        currentElementOutcomeTargets.Add(nmEOT);
                    }
                    else
                    {
                        targetStructures.Add("Extension");

                        targetSdEdComparisons = elementComparisons
                            .Where(ec => ec.TargetStructureKey is null)
                            .ToList();

                        // determine the context, if possible
                        if (sourceIsRootEd)
                        {
                            contextTargetEd = DbElement.SelectSingle(
                                _db,
                                FhirPackageKey: _packagePair.TargetPackageKey,
                                Id: "Extension",
                                ResourceFieldOrder: 0);

                            if (contextTargetEd is not null)
                            {
                                allContextTargets[contextTargetEd.Key] = contextTargetEd;
                            }
                        }
                        else
                        {
                            if ((sourceEd.ParentElementKey is not null) &&
                                edKeyOutcomeLookup.TryGetValue(sourceEd.ParentElementKey.Value, out (DbElementOutcome outcome, DbElementOutcome? rootOutcome) nmParent))
                            {
                                contextRootExtensionUrl = nmParent.rootOutcome?.GenUrl;
                                contextParentExtensionUrl = nmParent.outcome.GenUrl;
                            }
                        }

                        targetComments =
                            $"Element `{sourceEd.Id}` is not mapped to FHIR {_packagePair.TargetFhirSequence}," +
                            $" since FHIR {_packagePair.SourceFhirSequence} `{sourceSd.Name}` is not mapped.";

                        // create our target
                        DbElementOutcomeTarget nmEOT = new()
                        {
                            Key = DbElementOutcomeTarget.GetIndex(),
                            ElementOutcomeKey = edTr.ElementOutcomeKey,
                            StructureOutcomeKey = sdTr.StructureOutcomeKey,
                            ElementComparisonKey = targetSdEdComparisons.FirstOrDefault()?.Key,

                            SourceFhirPackageKey = _packagePair.SourcePackageKey,
                            SourceFhirSequence = _packagePair.SourceFhirSequence,
                            TargetFhirPackageKey = _packagePair.TargetPackageKey,
                            TargetFhirSequence = _packagePair.TargetFhirSequence,

                            TargetStructureKey = null,
                            TargetElementKey = null,
                            TargetElementId = null,
                            TargetResourceOrder = null,
                            TargetComponentOrder = null,
                            TargetMinCardinality = null,
                            TargetMaxCardinalityString = null,
                            TargetIsModifier = null,

                            ContextElementKey = contextTargetEd?.Key,
                            ContextElementId = contextTargetEd?.Id,
                            ContextRootExtensionUrl = contextRootExtensionUrl,
                            ContextParentExtensionUrl = contextParentExtensionUrl,

                            FullyMapsToThisTarget = false,
                            Comments = targetComments,
                        };

                        edTr.OutcomeTargets.Add(nmEOT);
                        _edOutcomeTargetCache.CacheAdd(nmEOT);
                        currentElementOutcomeTargets.Add(nmEOT);
                    }

                    continue;
                }

                // skip primitive targets
                if (sdTr.TargetStructure.ArtifactClass == FhirArtifactClassEnum.PrimitiveType)
                {
                    continue;
                }

                // get the element comparisons that target this structure
                targetSdEdComparisons = elementComparisons
                    .Where(ec => ec.TargetStructureKey == sdTr.TargetStructure.Key)
                    .ToList();

                // if there are comparisons, but none for this structure, this is something that cannot appear there
                if ((targetSdEdComparisons.Count == 0) &&
                    (elementComparisons.Count != 0))
                {
                    continue;
                }

                targetStructures.Add(sdTr.TargetStructure.Name);

                // build the list of target elements for this structure
                List<DbElement> targetEds = targetSdEdComparisons
                    .Where(ec => ec.TargetElementKey is not null)
                    .Distinct()
                    .Select(ec => _allTargetElements[ec.TargetElementKey!.Value])
                    .ToList();

                // figure out our context element, if possible
                contextTargetEd = findCommonAncestor(
                    sdTr.TargetStructure.FhirPackageKey,
                    targetEds,
                    requireNonPrimitive: (sourceEd.IsChoiceType || sourceEd.ChildElementCount > 0));
                if (contextTargetEd is not null)
                {
                    allContextTargets[contextTargetEd.Key] = contextTargetEd;
                }

                // check to see if there are any mapped comparisons (need to know later)
                bool hasMappedComparisons = elementComparisons.Any(ec => ec.NotMapped == false);

                HashSet<int?> mappedTargetEdKeys = [];

                // traverse the comparisons for this element (can map to multiple elments in target structure)
                foreach (DbElementComparison elementComparison in elementComparisons.Where(ec => ec.TargetStructureKey == sdTr.TargetStructure.Key))
                {
                    if (hasMappedComparisons && elementComparison.NotMapped)
                    {
                        continue;
                    }

                    if (!mappedTargetEdKeys.Add(elementComparison.TargetElementKey))
                    {
                        continue;
                    }

                    DbElement? ecTargetEd = elementComparison.TargetElementKey is null
                        ? null
                        : _allTargetElements[elementComparison.TargetElementKey.Value];

                    if (ecTargetEd is null)
                    {
                        targetComments =
                            $"Element `{sourceEd.Id}` is mapped to FHIR {_packagePair.TargetFhirSequence}" +
                            $" structure `{sdTr.TargetStructure.Name}`," +
                            $" but has no target element specified.";

                        DbElement? testTargetEd = null;
                        DbElement currentSourceEd = sourceEd;
                        while (testTargetEd is null)
                        {
                            if (currentSourceEd.ParentElementKey is null)
                            {
                                targetComments =
                                    $"Element `{sourceEd.Id}` failed to resolve a context based on" +
                                    $" the parent source element upwards and mapping to" +
                                    $" `{sdTr.TargetStructure.Name}`.";

                                break;
                            }

                            // check to see if we can move the source element id upward and see if we can resolve something
                            currentSourceEd = _allSourceElements[currentSourceEd.ParentElementKey.Value];

                            // get the element comparisons that target this structure
                            List<DbElementComparison> parentEdComparisons = DbElementComparison.SelectList(
                                _db,
                                SourceFhirPackageKey: _packagePair.SourcePackageKey,
                                TargetFhirPackageKey: _packagePair.TargetPackageKey,
                                SourceElementKey: currentSourceEd.Key,
                                TargetStructureKey: sdTr.TargetStructure.Key);

                            // if there are no comparisons, this does not help
                            if (parentEdComparisons.Count == 0)
                            {
                                continue;
                            }

                            // build the list of target elements for this structure
                            List<DbElement> parentTargetEds = parentEdComparisons
                                .Where(ec => ec.TargetElementKey is not null)
                                .Distinct()
                                .Select(ec => _allTargetElements[ec.TargetElementKey!.Value])
                                .ToList();

                            // figure out our context element, if possible
                            contextTargetEd = findCommonAncestor(
                                sdTr.TargetStructure.FhirPackageKey,
                                parentTargetEds,
                                requireNonPrimitive: (sourceEd.IsChoiceType || sourceEd.ChildElementCount > 0));
                            if (contextTargetEd is not null)
                            {
                                testTargetEd = contextTargetEd;
                                allContextTargets[contextTargetEd.Key] = contextTargetEd;

                                targetComments =
                                    $"Element `{sourceEd.Id}` has a context of {contextTargetEd.Id}" +
                                    $" based on following the parent source element upwards and mapping to" +
                                    $" `{sdTr.TargetStructure.Name}`.";

                                break;
                            }
                        }
                    }
                    else
                    {
                        if (sourceIsRootEd)
                        {
                            targetComments =
                                $"The root element `{sourceEd.Id}` is mapped to FHIR {_packagePair.TargetFhirSequence}" +
                                $" `{ecTargetEd.Id}`.";
                        }
                        else
                        {
                            targetComments =
                                $"Element `{sourceEd.Id}` is mapped to FHIR {_packagePair.TargetFhirSequence}" +
                                $" element `{ecTargetEd.Id}` as `{elementComparison.Relationship}`.";
                        }
                    }

                    // create our target
                    DbElementOutcomeTarget eot = new()
                    {
                        Key = DbElementOutcomeTarget.GetIndex(),
                        ElementOutcomeKey = edTr.ElementOutcomeKey,
                        StructureOutcomeKey = sdTr.StructureOutcomeKey,
                        ElementComparisonKey = targetSdEdComparisons.FirstOrDefault()?.Key,

                        SourceFhirPackageKey = _packagePair.SourcePackageKey,
                        SourceFhirSequence = _packagePair.SourceFhirSequence,
                        TargetFhirPackageKey = _packagePair.TargetPackageKey,
                        TargetFhirSequence = _packagePair.TargetFhirSequence,

                        TargetStructureKey = sdTr.TargetStructure.Key,
                        TargetElementKey = elementComparison.NotMapped ? null : ecTargetEd?.Key,
                        TargetElementId = elementComparison.NotMapped ? null : ecTargetEd?.Id,
                        TargetResourceOrder = elementComparison.NotMapped ? 0 : ecTargetEd?.ResourceFieldOrder,
                        TargetComponentOrder = elementComparison.NotMapped ? 0 : ecTargetEd?.ComponentFieldOrder,
                        TargetMinCardinality = elementComparison.NotMapped ? null : ecTargetEd?.MinCardinality,
                        TargetMaxCardinalityString = elementComparison.NotMapped ? null : ecTargetEd?.MaxCardinalityString,
                        TargetIsModifier = elementComparison.NotMapped ? null : ecTargetEd?.IsModifier,

                        ContextElementKey = contextTargetEd?.Key,
                        ContextElementId = contextTargetEd?.Id,
                        ContextRootExtensionUrl = contextRootExtensionUrl,
                        ContextParentExtensionUrl = contextParentExtensionUrl,

                        FullyMapsToThisTarget = edTr.IsFullyMappedAcrossAllTargets &&
                            edTr.MapsToIndividualTargets.Any(ec => (ec.TargetElementKey is not null) && (ec.TargetElementKey == ecTargetEd?.Key)),
                        Comments = targetComments,
                    };

                    edTr.OutcomeTargets.Add(eot);
                    _edOutcomeTargetCache.CacheAdd(eot);
                    currentElementOutcomeTargets.Add(eot);
                }
            }

            // check for no outcome target elements
            if (!sourceIsRootEd &&
                edTr.OutcomeTargets.All(eot => eot.TargetElementId is null))
            {
                elementRequiresXVer = true;
            }

            // check for the current element not thinking it needs a definition, but the parent does
            if (!elementRequiresXVer && (parentOutcome?.RequiresXVerDefinition == true))
            {
                elementRequiresXVer = true;
            }

            if (parentOutcome?.RequiresXVerDefinition == true)
            {
                outcomeComments.Add(
                    $"Element `{sourceEd.Id}` is part of an existing definition because" +
                    $" parent element `{parentOutcome.SourceId}` requires a cross-version extension.");
            }

            bool defineAsModifier = sourceEd.IsModifier;
            string? modifierReason = sourceEd.IsModifierReason;

            // if this is the root element, force some values
            if (sourceIsRootEd)
            {
                //elementRequiresXVer = false;
                ancestorOutcome = null;
                parentOutcome = null;
            }

            bool allowsBasicReplacement = false;
            // check if we are only targeting basic
            if (((targetStructures.Count == 0) && (sourceSd.ArtifactClass == FhirArtifactClassEnum.Resource)) ||
                ((targetStructures.Count == 1) && targetStructures.Contains("Basic")))
            {
                allowsBasicReplacement = true;
            }

            bool typeTargetsExtension = false;
            if ((sourceSd.ArtifactClass == FhirArtifactClassEnum.ComplexType) &&
                ((targetStructures.Count == 0) || ((targetStructures.Count == 1) && targetStructures.Contains("Extension"))))
            {
                if (sourceSd.Name != "Extension")
                {
                    elementRequiresXVer = true;
                    typeTargetsExtension = true;
                }
            }

            // check to see if we are trying to define an extension onto basic that has a matching basic path
            if (elementRequiresXVer &&
                allowsBasicReplacement &&
                _targetBasicElementPathLookup.TryGetValue(sourceEd.Path.Substring(sourceSd.Name.Length), out basicBasePath) &&
                _targetBasicElementsById.TryGetValue(basicBasePath!, out basicEd))
            {
                basicPath = basicEd.Id;

                if (canMapElementsByType(sourceEd, basicEd) &&
                    (sourceEd.MinCardinality >= basicEd.MinCardinality) &&
                    ((basicEd.MaxCardinality == -1) || (basicEd.MaxCardinality >= sourceEd.MaxCardinality)) &&
                    (((sourceEd.ChildElementCount == 0) && (basicEd.ChildElementCount == 0)) ||
                        ((sourceEd.ChildElementCount > 0) && (basicEd.ChildElementCount > 0))))
                {
                    elementRequiresXVer = false;
                    outcomeComments.Add(
                        $"Element matches Basic element path `{basicPath}` (`{basicBasePath}`)," +
                        $" use that element instead.");
                    elementRequiresXVer = false;
                    allContextTargets.Clear();
                    parentOutcome = null;
                }
                else
                {
                    basicBasePath = null;
                    basicPath = null;
                    outcomeNotes.Add(
                        $"While the source element matches Basic element path `{basicPath}` (`{basicBasePath}`)," +
                        $" the definitions are not compatible" +
                        $" (source: `{sourceEd.FullCollatedTypeLiteral}`:{sourceEd.FhirCardinalityString}" +
                        $" -> basic: `{basicEd.FullCollatedTypeLiteral}`:{basicEd.FhirCardinalityString}).");
                }
            }

            // check to see if we are trying to define an extension onto extension that has a matching path
            if (elementRequiresXVer &&
                typeTargetsExtension &&
                _targetExtensionElementPathLookup.TryGetValue(sourceEd.Path.Substring(sourceSd.Name.Length), out extensionBasePath) &&
                _targetExtensionElementsById.TryGetValue(extensionBasePath!, out extensionEd))
            {
                extensionPath = extensionEd.Id;

                if (canMapElementsByType(sourceEd, extensionEd) &&
                    (sourceEd.MinCardinality >= extensionEd.MinCardinality) &&
                    ((extensionEd.MaxCardinality == -1) || (extensionEd.MaxCardinality >= sourceEd.MaxCardinality)) &&
                    (((sourceEd.ChildElementCount == 0) && (extensionEd.ChildElementCount == 0)) ||
                        ((sourceEd.ChildElementCount > 0) && (extensionEd.ChildElementCount > 0))))
                {
                    elementRequiresXVer = false;
                    outcomeComments.Add(
                        $"Element matches Extension element path `{extensionPath}` (`{extensionBasePath}`)," +
                        $" use that element instead.");
                    elementRequiresXVer = false;
                    allContextTargets.Clear();
                    parentOutcome = null;
                }
                else
                {
                    extensionBasePath = null;
                    extensionPath = null;
                    outcomeNotes.Add(
                        $"While the source element matches Extension element path `{extensionPath}` (`{extensionBasePath}`)," +
                        $" the definitions are not compatible" +
                        $" (source: `{sourceEd.FullCollatedTypeLiteral}`:{sourceEd.FhirCardinalityString}" +
                        $" -> extension: `{extensionEd.FullCollatedTypeLiteral}`:{extensionEd.FhirCardinalityString}).");
                }
            }

            // if we need a modifier extension, we need to review contexts to ensure they can accept it
            if (elementRequiresXVer && defineAsModifier)
            {
                /*
                 * Determine if this extension should really be a modifier based on context:
                 *  * Modifier element -> Modifier element : extension
                 *  * Modifier element -> Backbone element (not modifier) : modifier extension
                 *  * Modifier element -> Primitive-type element (not modifier) : modifier extension, context moves up a level
                 *  * Modifier element -> Primitive-type element (array, not modifier) : currently unresolvable, but also has not happened yet
                 */

                List<(DbElement toRemove, DbElement replacement, string comment)> ctxChanges = [];

                if (allContextTargets.Count == 0)
                {
                    if (parentOutcome?.RequiresXVerDefinition == true)
                    {
                        // need to promote the modifier to the parent context
                        parentOutcome.DefineAsModifier = true;
                        modifierReason = 
                            $"Child element `{sourceEd.Name}` is a modifier," +
                            $" so this extension must be a modifier.";
                        parentOutcome.Comments += "\n" + modifierReason;
                    }

                    if ((ancestorOutcome is not null) &&
                        (ancestorOutcome.Key != parentOutcome?.Key))
                    {
                        ancestorOutcome.DefineAsModifier = true;
                        modifierReason = 
                            $"Descendent element `{sourceEd.Id}` is a modifier and part of this extension," +
                            $" so this extension must be a modifier.";
                        ancestorOutcome.Comments += "\n" + modifierReason;
                    }
                }
                else
                {
                    // iterate over the context target elements for initial checks
                    foreach (DbElement ctxTargetEd in allContextTargets.Values)
                    {
                        // if this is a modifier element, we do not need to define as a modifier
                        if (ctxTargetEd.IsModifier)
                        {
                            defineAsModifier = false;
                            modifierReason = null;
                            outcomeNotes.Add(
                                $"The target context `{ctxTargetEd.Id}` is a modifier element," +
                                $" so this extension does not need to be defined as a modifier.");
                            break;
                        }

                        if (ctxTargetEd.ChildElementCount > 0)
                        {
                            // this is okay - elements with children can have modifier extensions
                            continue;
                        }

                        // check to see if the element is or only has primitive types
                        List<DbElementType> ctxTargetEdTypes = _targetElementTypesByElementKey.Contains(ctxTargetEd.Key)
                            ? _targetElementTypesByElementKey[ctxTargetEd.Key].ToList()
                            : [];

                        if (ctxTargetEdTypes.All(ctxEt => FhirTypeUtils.IsPrimitiveType(ctxEt.TypeName ?? ctxEt.Literal)))
                        {
                            // need to move up to a higher level
                            if (ctxTargetEd.ParentElementKey is null)
                            {
                                throw new Exception(
                                    $"Cannot determine modifier extension context for source element `{sourceEd.Id}`" +
                                    $" mapping to target primitive-type element `{ctxTargetEd.Id}`" +
                                    $" because the target element has no parent to move up to.");
                            }

                            DbElement ctxTargetParentEd = _allTargetElements[ctxTargetEd.ParentElementKey.Value];

                            string replacementComment =
                                $"The target element context `{ctxTargetEd.Id}` is a primitive-type element" +
                                $" and this extension needs to be defined as a modifier." +
                                $" The context was moved up to parent element `{ctxTargetParentEd.Id}`.";

                            ctxChanges.Add((ctxTargetEd, ctxTargetParentEd, replacementComment));
                        }
                    }

                    foreach ((DbElement toRemove, DbElement replacement, string comment) in ctxChanges)
                    {
                        // iterate over the targets to update their contexts if necessary
                        foreach (DbElementOutcomeTarget eot in edTr.OutcomeTargets)
                        {
                            if (eot.ContextElementKey == toRemove.Key)
                            {
                                eot.ContextElementKey = replacement.Key;
                                eot.ContextElementId = replacement.Id;
                                eot.Comments +=
                                    $"\n{comment}";
                            }
                        }

                        allContextTargets.Remove(toRemove.Key);
                        allContextTargets[replacement.Key] = replacement;

                        outcomeComments.Add(comment);
                    }
                }
            }

            if (_extensionSubstitutionsByElementId.TryGetValue(sourceEd.Id, out extSubstitute))
            {
                switch (extSubstitute.ReplacementUrl)
                {
                    case CommonDefinitions.ExtUrlAlternateCanonical:
                        outcomeNotes.Add(
                            $"The standard extension `alternate-canonical` has been mapped as the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with an unmapped Canonical type.");
                        break;

                    case CommonDefinitions.ExtUrlAlternateReference:
                        outcomeNotes.Add(
                            $"The standard extension `alternate-reference` has been mapped as the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with an unmapped Canonical type.");
                        break;

                    default:
                        outcomeNotes.Add(
                            $"An externally-defined extension that has been mapped as the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}`:" +
                            $" `{extSubstitute.ReplacementUrl}`.");
                        break;
                }
            }

            List<string> alternateCanonicalTargets = [];
            List<string> alternateReferenceTargets = [];

            List<string> unmappedTypeNames = edTr.UnmappedTypes.Select(ut => ut.TypeName!).Distinct().ToList();
            List<string> mappedTypeNames = edTr.MappedTypes.Select(ut => ut.TypeName!).Distinct().ToList();

            List<string> unmappedDistinctTypeNames = edTr.UnmappedTypes.Select(ut => ut.TypeName!).Distinct().ToList();

            bool hasUnmappedCodeableReference = unmappedDistinctTypeNames.Contains("CodeableReference");
            bool crMapsConcept = edTr.ChildTypeMappingResults.Any(cmr => cmr.MappedTypeChildren.Any(e => e.Name == "concept"));
            bool crMapsReference = edTr.ChildTypeMappingResults.Any(cmr => cmr.MappedTypeChildren.Any(e => e.Name == "reference"));
            bool promoteCodeableReferenceReferenceType = hasUnmappedCodeableReference && crMapsConcept;

            bool allAlternates = unmappedDistinctTypeNames.All(tn =>
                (tn == "Reference") ||
                (tn == "canonical") ||
                (promoteCodeableReferenceReferenceType && (tn == "CodeableReference")));

            bool allAlternateReferences = unmappedDistinctTypeNames.All(tn =>
                (tn == "Reference") ||
                (promoteCodeableReferenceReferenceType && (tn == "CodeableReference")));

            bool allAlternateCanonicals = unmappedDistinctTypeNames.All(tn => tn == "canonical");

            bool targetHasReference = edTr.DistinctTargetElementTypes.Any(tn => tn == "Reference");
            bool targetHasCanonical = edTr.DistinctTargetElementTypes.Any(tn => tn == "canonical");

            List<DbElementType> unmappedReferenceTypes = edTr.UnmappedTypes
                .Where(ut =>
                    ((ut.TypeName == "Reference") ||
                        (promoteCodeableReferenceReferenceType && (ut.TypeName == "CodeableReference"))) &&
                    (ut.TargetProfile is not null))
                .ToList();

            List<DbElementType> unmappedCanonicalTypes = edTr.UnmappedTypes
                .Where(ut => (ut.TypeName == "canonical") && (ut.TargetProfile is not null))
                .ToList();

            if (sourceEd.ChildElementCount == 0)
            {
                // check for allowed reference targets
                if (targetHasReference &&
                    (unmappedReferenceTypes.Count > 0))
                {
                    alternateReferenceTargets = unmappedReferenceTypes
                        .Select(ut => ut.TargetProfile!)
                        .Distinct()
                        .Order()
                        .ToList();

                    if (allAlternates && (unmappedCanonicalTypes.Count == 0))
                    {
                        if (_typeExtensionSubstitutionsByUrl.TryGetValue(CommonDefinitions.ExtUrlAlternateReference, out DbExtensionSubstitution? arExt))
                        {
                            extSubstitute = arExt;
                        }
                        outcomeNotes.Add(
                            $"The standard extension `alternate-reference` has been mapped as the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with unmapped reference targets:" +
                            $" {string.Join(", ", alternateReferenceTargets.Select(v => v.Replace("http://hl7.org/fhir/StructureDefinition/", "")))}.");
                    }
                    else
                    {
                        outcomeNotes.Add(
                            $"The standard extension `alternate-reference` has been mapped as PART of the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with unmapped reference targets:" +
                            $" {string.Join(", ", alternateReferenceTargets.Select(v => v.Replace("http://hl7.org/fhir/StructureDefinition/", "")))}.");
                    }

                    foreach (DbElementType ut in unmappedReferenceTypes)
                    {
                        edTr.UnmappedTypes.Remove(ut);
                    }

                    unmappedTypeNames.Remove("Reference");
                    if (promoteCodeableReferenceReferenceType)
                    {
                        unmappedTypeNames.Remove("CodeableReference");
                    }
                }

                // check for allowed canonical targets
                if (targetHasCanonical &&
                    (unmappedCanonicalTypes.Count > 0))
                {
                    alternateCanonicalTargets = unmappedCanonicalTypes
                        .Select(ut => ut.TargetProfile!)
                        .Distinct()
                        .Order()
                        .ToList();

                    if (allAlternates && (unmappedReferenceTypes.Count == 0))
                    {
                        if (_typeExtensionSubstitutionsByUrl.TryGetValue(CommonDefinitions.ExtUrlAlternateCanonical, out DbExtensionSubstitution? acExt))
                        {
                            extSubstitute = acExt;
                        }
                        outcomeNotes.Add(
                            $"The standard extension `alternate-canonical` has been mapped as the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with an unmapped Canonical type:" +
                            $" {string.Join(", ", alternateCanonicalTargets.Select(v => v.Replace("http://hl7.org/fhir/StructureDefinition/", "")))}.");
                    }
                    else
                    {
                        outcomeNotes.Add(
                            $"The standard extension `altnernate-canonical` has been mapped as PART of the" +
                            $" representation of FHIR {_packagePair.SourceFhirSequence} element `{sourceEd.Id}` with an unmapped canonical targets:" +
                            $" {string.Join(", ", alternateCanonicalTargets.Select(v => v.Replace("http://hl7.org/fhir/StructureDefinition/", "")))}.");
                    }

                    foreach (DbElementType ut in unmappedCanonicalTypes)
                    {
                        edTr.UnmappedTypes.Remove(ut);
                    }

                    unmappedTypeNames.Remove("canonical");
                }
            }

            // check to see if we have now mapped everything
            if ((unmappedTypeNames.Count == 0) &&
                elementRequiresXVer &&
                ((alternateCanonicalTargets.Count != 0) || (alternateReferenceTargets.Count != 0)))
            {
                elementRequiresXVer = false;
            }

            List<string> unmappedChildTypeNames = edTr.ChildTypeMappingResults
                .SelectMany(ctr => ctr.UnmappedTypeChildren)
                .Select(ued => ued.Name)
                .Distinct()
                .ToList();

            DbElement? targetEd = allContextTargets.Count == 1
                ? allContextTargets.Values.First()
                : null;

            DbElementComparison? singleEC = elementComparisons.Count == 1
                ? elementComparisons[0]
                : null;

            outcomeComments.AddRange(currentElementOutcomeTargets.Where(eot => eot.Comments is not null).Select(eot => eot.Comments!));

            string? ancestorCR = parentEd?.UsedAsContentReference == true
                ? parentEd.Id
                : parentOutcome?.SourceAncestorUsedAsContentReferenceId;

            int? ancestorCrKey = parentEd?.UsedAsContentReference == true
                ? parentOutcome?.Key
                : parentOutcome?.SourceAncestorContentReferenceOutcomeKey;

            // check to see if we need to update the content reference outcome
            if ((contentRefEdOutcome is not null) &&
                elementRequiresXVer &&
                (contentRefEdOutcome.RequiresDefinitionAsContentReference != true))
            {
                contentRefEdOutcome.RequiresDefinitionAsContentReference = true;
            }

            bool requiresSliceDefinition = elementRequiresXVer && (parentOutcome?.RequiresXVerDefinition ?? false);
            bool requiresExtensionDefinition = elementRequiresXVer &&
                !requiresSliceDefinition &&
                (basicBasePath is null) &&
                (extSubstitute is null) &&
                (contentRefEdOutcome?.RequiresDefinitionAsContentReference != true);

            string artifactShort = (edTr.DiscreteTargetCount != 0) && (sourceEd.Short == targetEd?.Short)
                ? $"{_packagePair.SourceFhirSequence}: {sourceEd.NameClean()}"
                : $"{_packagePair.SourceFhirSequence}: {sourceEd.Short}";
            string artifactDescription = $"{_packagePair.SourceFhirSequence}: `{sourceEd.Id}`";

            if (edTr.DiscreteTargetCount == 0)
            {
                artifactShort += " (new)";
                artifactDescription += $" (new:{sourceEd.FullCollatedTypeLiteral.Replace("http://hl7.org/fhir/StructureDefinition/", "")})";

                outcomeComments.Add(
                    $"Element `{sourceEd.Id}`" +
                    $" has no mapping targets in FHIR {_packagePair.TargetFhirSequence}." +
                    $" Typically, this is because the element has been added (is a new element).");
            }
            else
            {
                if (edTr.UnmappedTypes.Count > 0)
                {
                    artifactShort += " additional types";
                    artifactDescription += $" additional types ({string.Join(", ", edTr.UnmappedTypes.Select(ut => ut.Literal))})";

                    outcomeComments.Add(
                        $"The mappings for `{sourceEd.Id}`" +
                        $" do not cover the following types:" +
                        $" {string.Join(", ", unmappedTypeNames.Order())}.");
                }

                if (unmappedChildTypeNames.Count > 0)
                {
                    if (!artifactShort.EndsWith("additional types"))
                    {
                        artifactShort += " additional types";
                    }
                    artifactDescription += $" additional types from child elements ({string.Join(", ", unmappedChildTypeNames)})";

                    outcomeComments.Add(
                        $"The mappings for `{sourceEd.Id}`" +
                        $" do not cover the following types based on type expansion:" +
                        $" {string.Join(", ", unmappedChildTypeNames.Order())}.");
                }

                switch (edTr.BoundValueSetRelationship)
                {
                    case CMR.SourceIsNarrowerThanTarget:
                    case CMR.RelatedTo:
                        artifactShort += " additional codes";
                        artifactDescription += $" additional codes";

                        outcomeComments.Add(
                            $"The mappings for `{sourceEd.Id}`" +
                            $" do not allow expression of the necessary codes, per the bindings on the source and target.");

                        break;
                }
            }

            // if we have any targets, check for choice-type contexts that need to be moved up
            if (allContextTargets.Count > 0)
            {
                List<(DbElement toRemove, DbElement replacement, string comment)> ctxChanges = [];

                // iterate over the context target elements to see if we have targets that are choice types
                foreach (DbElement ctxTargetEd in allContextTargets.Values)
                {
                    if (!ctxTargetEd.IsChoiceType)
                    {
                        continue;
                    }

                    // check to see if we have sources that are going to be alternates and targets that could match
                    if ((unmappedReferenceTypes.Count > 0) &&
                        targetHasReference)
                    {
                        altReferenceContextTargetEd = ctxTargetEd;
                        outcomeNotes.Add(
                            $"Source element `{sourceEd.Id}` has unmapped reference types." +
                            $" While the target element `{ctxTargetEd.Id}` is a choice type and does not allow extensions," +
                            $" the `alternate-reference` extension can be applied to Reference values within it.");

                        if (unmappedDistinctTypeNames.Count == 1)
                        {
                            // do not change the target
                            continue;
                        }
                    }

                    if ((unmappedCanonicalTypes.Count > 0) &&
                        targetHasCanonical)
                    {
                        altCanonicalContextTargetEd = ctxTargetEd;
                        outcomeNotes.Add(
                            $"Source element `{sourceEd.Id}` has unmapped canonical types." +
                            $" While the target element `{ctxTargetEd.Id}` is a choice type and does not allow extensions," +
                            $" the `alternate-canonical` extension can be applied to canoncial values within it.");

                        if (unmappedDistinctTypeNames.Count == 1)
                        {
                            // do not change the target
                            continue;
                        }
                    }

                    // need to move up to a higher level
                    if (ctxTargetEd.ParentElementKey is null)
                    {
                        throw new Exception(
                            $"Cannot determine modifier extension context for source element `{sourceEd.Id}`" +
                            $" mapping to target choice-type element `{ctxTargetEd.Id}`" +
                            $" because the target element has no parent to move up to.");
                    }

                    DbElement ctxTargetParentEd = _allTargetElements[ctxTargetEd.ParentElementKey.Value];

                    string replacementComment =
                        $"The target context `{ctxTargetEd.Id}` is a choice-type element" +
                        $" and cannot directly hold extensions." +
                        $" The context is moved up to parent element `{ctxTargetParentEd.Id}`.";

                    ctxChanges.Add((ctxTargetEd, ctxTargetParentEd, replacementComment));
                }

                foreach ((DbElement toRemove, DbElement replacement, string comment) in ctxChanges)
                {
                    // iterate over the targets to update their contexts if necessary
                    foreach (DbElementOutcomeTarget eot in edTr.OutcomeTargets)
                    {
                        if (eot.ContextElementKey == toRemove.Key)
                        {
                            eot.ContextElementKey = replacement.Key;
                            eot.ContextElementId = replacement.Id;
                            eot.Comments +=
                                $"\n{comment}";
                        }
                    }

                    allContextTargets.Remove(toRemove.Key);
                    allContextTargets[replacement.Key] = replacement;

                    outcomeComments.Add(comment);
                }
            }

            // if we still have have no context targets but need an extension, it just goes on element
            if (elementRequiresXVer &&
                (allContextTargets.Count == 0))
            {
                if (targetStructures.Count == 1)
                {
                    DbElement sdRootContextEd = DbElement.SelectSingle(
                        _db,
                        FhirPackageKey: _packagePair.TargetPackageKey,
                        Id: targetStructures.First(),
                        ResourceFieldOrder: 0)
                        ?? throw new Exception("Cannot find root element for context.");

                    allContextTargets[sdRootContextEd.Key] = sdRootContextEd;
                }
                else
                {
                    DbElement genericContextEd = DbElement.SelectSingle(
                        _db,
                        FhirPackageKey: _packagePair.TargetPackageKey,
                        Id: "Element",
                        ResourceFieldOrder: 0)
                        ?? throw new Exception("Cannot find generic Element for context.");

                    allContextTargets[genericContextEd.Key] = genericContextEd;
                }
            }

            if (sourceEd.IsDeprecated)
            {
                outcomeNotes.Add($"Element `{sourceEd.Id}` has been flagged as deprecated.");
            }

            if (outcomeNotes.Count > 0)
            {
                outcomeComments.AddRange(outcomeNotes);
            }

            bool extensionDefinitionIsProhibited = false;
            string? extensionProhibitionReason = null;

            // check for invalid source types that cannot have extensions
            if (elementRequiresXVer &&
                unmappedDistinctTypeNames.Contains("Resource") ||
                unmappedDistinctTypeNames.Any(v => _sourceResourceTypes.Contains(v)) ||
                unmappedDistinctTypeNames.Any(v => _targetResourceTypes.Contains(v)))
            {
                extensionDefinitionIsProhibited = true;
                extensionProhibitionReason = $"Element is a resource type that cannot be represented via extensions.";
            }

            string outcomeComment = string.Join('\n', outcomeComments.Distinct());

            // create the mapped element outcome
            DbElementOutcome elementOutcome = new()
            {
                Key = edTr.ElementOutcomeKey,

                SourceFhirPackageKey = _packagePair.SourcePackageKey,
                SourceFhirSequence = _packagePair.SourceFhirSequence,
                SourceStructureKey = sourceSd.Key,
                SourceElementKey = sourceEd.Key,
                SourceCanonicalUnversioned = sourceSd.UnversionedUrl,
                SourceCanonicalVersioned = sourceSd.VersionedUrl,
                SourceVersion = sourceSd.Version,
                SourceId = sourceEd.Id,
                SourceName = sourceEd.Name,
                SourceResourceOrder = sourceEd.ResourceFieldOrder,
                SourceComponentOrder = sourceEd.ComponentFieldOrder,
                SourceMinCardinality = sourceEd.MinCardinality,
                SourceMaxCardinalityString = sourceEd.MaxCardinalityString,
                SourceChildElementCount = sourceEd.ChildElementCount,
                SourceUsedAsContentReference = sourceEd.UsedAsContentReference == true,
                SourceAncestorUsedAsContentReferenceId = ancestorCR,
                SourceAncestorContentReferenceOutcomeKey = ancestorCrKey,
                SourceIsDeprecated = sourceEd.IsDeprecated,
                TotalSourceCount = -1,

                TargetFhirPackageKey = _packagePair.TargetPackageKey,
                TargetFhirSequence = _packagePair.TargetFhirSequence,
                TotalTargetCount = edTr.DiscreteTargetCount,
                OutcomeTargetCount = edTr.OutcomeTargets.Count,

                ExtensionDefinitionIsProhibited = extensionDefinitionIsProhibited,
                ExtensionProhibitionReason = extensionProhibitionReason,

                RequiresXVerDefinition = elementRequiresXVer,
                RequiresExtensionDefinition = requiresExtensionDefinition,
                RequiresSliceDefinition = requiresSliceDefinition,
                GenLongId = idLong,
                GenShortId = idShort,
                GenUrl = extUrl,
                GenName = name,
                GenFileName = extFilename,
                GenSliceName = sourceEd.NameClean(),

                GenArtifactShort = artifactShort,
                GenArtifactDescription = artifactDescription,
                GenArtifactComment = sourceEd.Comments is null
                    ? outcomeComment
                    : (outcomeComment + "\n" + sourceEd.Comments),
                GenMappingComment = sourceEd.Comments is null
                    ? outcomeComment
                    : (outcomeComment + "\n" + sourceEd.Comments),
                ContentReferenceOutcomeKey = contentRefEdOutcome?.Key,
                ContentReferenceExtensionUrl = contentRefEdOutcome?.GenUrl,
                ContentReferenceRequiresXVerDefinition = contentRefEdOutcome?.RequiresXVerDefinition,
                ContentReferenceAncestorId = contentRefEdOutcome?.SourceId ?? parentOutcome?.ContentReferenceAncestorId,
                RequiresDefinitionAsContentReference = (contentRefEd is not null) && elementRequiresXVer,

                AlternateCanonicalTargets = alternateCanonicalTargets,
                AlternateCanonicalContextLiteral = altCanonicalContextTargetEd?.Id,
                AlternateReferenceTargets = alternateReferenceTargets,
                AlternateReferenceContextLiteral = altReferenceContextTargetEd?.Id,

                AncestorElementOutcomeKey = elementRequiresXVer ? ancestorOutcome?.Key : null,
                ParentElementOutcomeKey = parentOutcome?.Key,
                SourceIsModifier = sourceEd.IsModifier,
                DefineAsModifier = defineAsModifier,
                ModifierReason = modifierReason,
                ExtensionContexts = allContextTargets.Values.Select(ed => ed.Id).Distinct().Order().ToList(),
                BasicElementBaseId = basicBasePath,
                BasicElementId = basicPath,
                ExtensionElementBaseId = extensionBasePath,
                ExtensionElementId = extensionPath,
                ExtensionSubstitutionKey = extSubstitute?.Key,
                ExtensionSubstitutionUrl = extSubstitute?.ReplacementUrl,

                IsRenamed = targetEd is null ? false : (sourceEd.Name != targetEd.Name),
                IsUnmapped = targetEd is null || (singleEC?.NotMapped == true),
                IsIdentical = singleEC?.IsIdentical == true,
                IsEquivalent = singleEC?.Relationship == CMR.Equivalent,
                IsBroaderThanTarget = singleEC?.Relationship == CMR.SourceIsBroaderThanTarget,
                IsNarrowerThanTarget = singleEC?.Relationship == CMR.SourceIsNarrowerThanTarget,

                FullyMapsAcrossAllTargets = edTr.IsFullyMappedAcrossAllTargets,
                FullyMapsToThisTarget = targetEd is null ? false : edTr.IsFullyMappedAcrossAllTargets,

                MappedTypeKeys = edTr.MappedTypes.Select(mt => mt.Key).ToList(),
                MappedTypeNames = edTr.MappedTypes.Select(mt => mt.Literal).ToList(),
                UnmappedTypeKeys = edTr.UnmappedTypes.Select(ut => ut.Key).ToList(),
                UnmappedTypeNames = edTr.UnmappedTypes.Select(ut => ut.Literal).ToList(),

                MappedTypeChildElementKeys = edTr.ChildTypeMappingResults
                    .SelectMany(ctr => ctr.MappedTypeChildren)
                    .Select(med => med.Key)
                    .Distinct()
                    .ToList(),
                MappedChildTypeElementNames = edTr.ChildTypeMappingResults
                    .SelectMany(ctr => ctr.MappedTypeChildren)
                    .Select(med => med.Name)
                    .Distinct()
                    .ToList(),
                UnmappedTypeChildKeys = edTr.ChildTypeMappingResults
                    .SelectMany(ctr => ctr.UnmappedTypeChildren)
                    .Select(ued => ued.Key)
                    .Distinct()
                    .ToList(),
                UnmappedChildTypeNames = unmappedChildTypeNames,

                Comments = outcomeComment,
            };

            if (sourceIsRootEd)
            {
                rootElementOutcomes.Add(elementOutcome);
            }

            _edOutcomeCache.CacheAdd(elementOutcome);
            edTr.ElementOutcome = elementOutcome;

            foreach (StructureOutcomeGenerator.StructureOutcomeTrackingRecord sdTr in structureTrackingRecords.Values)
            {
                sdTr.ElementOutcomes.Add(elementOutcome);
            }

            edKeyOutcomeLookup[sourceEd.Key] = (elementOutcome, ancestorOutcome);
        }
    }

    private void checkValueSetMappings(
        Dictionary<int, ElementOutcomeTrackingRecord> elementTrackingRecords)
    {
        // iterate over our tracking records to check valueset mapping (if necessary)
        foreach (ElementOutcomeTrackingRecord elementTrackingRec in elementTrackingRecords.Values)
        {
            DbElement sourceEd = elementTrackingRec.SourceElement;

            if (sourceEd.ValueSetBindingStrength != Hl7.Fhir.Model.BindingStrength.Required)
            {
                if (sourceEd.ValueSetBindingStrength is not null)
                {
                    elementTrackingRec.Messages.Add(
                        $"Element `{sourceEd.Id}` has a" +
                        $" {sourceEd.ValueSetBindingStrength} binding to" +
                        $" `{sourceEd.BindingValueSet}`," +
                        $" which allows for any value and is not considered for full-mapping.");
                }

                continue;
            }

            bool vsMappingHasValidTarget = false;
            DbElement? mappingTargetEd = null;
            CMR? boundVsRelationship = CMR.Equivalent;

            foreach (DbElementComparison ec in elementTrackingRec.ElementComparisons)
            {
                if ((ec.TargetStructureKey is null) ||
                    (ec.TargetContentKey is null))
                {
                    continue;
                }

                boundVsRelationship = FhirDbComparer.ApplyRelationship(boundVsRelationship, ec.BoundValueSetRelationship);

                DbElement targetEd = elementTrackingRec.TargetElements[ec.TargetContentKey!.Value];

                // if the source and target are required, we need to consider the vs relationship
                if (targetEd.ValueSetBindingStrength == Hl7.Fhir.Model.BindingStrength.Required)
                {
                    // if the vs relationship is equivalent or source narrower, then we are good
                    if (relationshipMaps(ec.BoundValueSetRelationship))
                    {
                        vsMappingHasValidTarget = true;
                        mappingTargetEd = targetEd;

                        elementTrackingRec.Messages.Add(
                            $"Element `{sourceEd.Id}` has a required binding to `{sourceEd.BindingValueSet}`" +
                            $" and maps to `{targetEd.Id}` that has a required binding to `{targetEd.BindingValueSet}`" +
                            $" - the value set relationship is {ec.BoundValueSetRelationship}, which is considered fully-mapped.");

                        break;
                    }

                    elementTrackingRec.Messages.Add(
                        $"Element `{sourceEd.Id}` has a required binding to `{sourceEd.BindingValueSet}`" +
                        $" and maps to `{targetEd.Id}` that has a required binding to `{targetEd.BindingValueSet}`" +
                        $" - the value set relationship is {ec.BoundValueSetRelationship}, which is NOT considered fully-mapped.");

                    continue;
                }
            }

            elementTrackingRec.BoundValueSetRelationship = boundVsRelationship;
            if (!vsMappingHasValidTarget)
            {
                elementTrackingRec.IsFullyMappedAcrossAllTargets = false;
            }
        }
    }

    private void determineMappingCompleteness(
        Dictionary<int, DbElement> sourceEds,
        Dictionary<int, ElementOutcomeTrackingRecord> edTrs)
    {
        HashSet<int> fullyMappedElementsAllTargets = [];

        // iterate over the source elements for this structure to determine mapping completeness
        foreach (DbElement sourceEd in sourceEds.Values.OrderBy(ed => ed.ResourceFieldOrder))
        {
            // get all the source types for this element
            Dictionary<int, DbElementType> sourceEts = _sourceElementTypesByElementKey[sourceEd.Key]
                .ToDictionary(et => et.Key);

            if ((sourceEts.Count == 0) &&
                (sourceEd.BaseElementKey is not null))
            {
                sourceEts = _sourceElementTypesByElementKey[sourceEd.BaseElementKey!.Value]
                    .ToDictionary(et => et.Key);
            }

            // filter out base element types that make no sense to deal with (e.g., any target will cover)
            if (sourceEts.Count != 0)
            {
                List<int> typesToFilter = [];
                foreach ((int key, DbElementType et) in sourceEts)
                {
                    if (skipType(et.TypeName ?? et.Literal))
                    {
                        typesToFilter.Add(key);
                    }
                }

                foreach (int ttf in typesToFilter)
                {
                    sourceEts.Remove(ttf);
                }
            }

            // get the all comparisons for this element to the target FHIR package
            List<DbElementComparison> elementComparisons = _edComparsionsBySourceElementKey[sourceEd.Key]
                .ToList();

            HashSet<int> unrelatedEdComparisons = elementComparisons
                //.Where(ec => ec.NotMapped || (ec.Relationship == CMR.NotRelatedTo))
                .Where(ec => ec.TargetStructureKey is null)
                .Select(ec => ec.Key)
                .ToHashSet();

            // if we have at least one good comparsion, remove anything unmapped or not related
            if ((unrelatedEdComparisons.Count > 0) &&
                (unrelatedEdComparisons.Count < elementComparisons.Count))
            {
                elementComparisons = elementComparisons
                    .Where(ec => !unrelatedEdComparisons.Contains(ec.Key))
                    .ToList();
            }

            if (!edTrs.TryGetValue(sourceEd.Key, out ElementOutcomeTrackingRecord? edTr))
            {
                edTr = new()
                {
                    SourceElement = sourceEd,
                    ElementOutcomeKey = DbElementOutcome.GetIndex(),
                    ElementComparisons = elementComparisons,
                    DiscreteTargetCount = elementComparisons
                        .Select(ec => ec.TargetElementKey)
                        .Where(tk => tk is not null)
                        .Distinct()
                        .Count(),
                    TargetElements = elementComparisons
                        .Where(ec => ec.TargetElementKey is not null)
                        .Select(ec => ec.TargetElementKey!.Value)
                        .Distinct()
                        .Select(key => _allTargetElements[key])
                        .ToDictionary(te => te.Key),
                };
                edTrs[sourceEd.Key] = edTr;
            }

            // check for elements with children and no types (e.g., backbone elements) - need to resolve children first
            if ((sourceEts.Count == 0) &&
                (sourceEd.ChildElementCount > 0))
            {
                edTr.Messages.Add($"Element `{sourceEd.Id}` has child elements and mapping relationships will be determined by them.");
                continue;
            }

            // easy check for any single comparison that fully maps
            List<DbElementComparison> fullyMappedComparisons = elementComparisons
                .Where(ec =>
                    (ec.TargetElementId is not null) &&
                    (ec.Relationship is not null) &&
                    relationshipMaps(ec.Relationship) &&
                    relationshipMaps(ec.BoundValueSetRelationship) &&
                    relationshipMaps(ec.TypeRelationship) &&
                    relationshipMaps(ec.TypeProfileRelationship) &&
                    relationshipMaps(ec.TargetProfileRelationship))
                .ToList();

            if (fullyMappedComparisons.Count != 0)
            {
                fullyMappedElementsAllTargets.Add(sourceEd.Key);

                edTr.IsFullyMappedAcrossAllTargets = true;
                edTr.MapsToIndividualTargets = fullyMappedComparisons;
                edTr.Messages.Add(
                    $"Element `{sourceEd.Id}` has fully-mapped types to individual targets:" +
                    $" {string.Join(", ", fullyMappedComparisons.Select(fmc => $"`{fmc.TargetElementId}`"))}");

                edTr.MappedTypes.AddRange(sourceEts.Values.OrderBy(et => et.Literal));

                continue;
            }

            // resolve the target elements involved in the comparisons
            Dictionary<int, DbElement> currentTargetElements = [];
            foreach (DbElementComparison ec in elementComparisons)
            {
                if ((ec.TargetStructureKey is null) ||
                    (ec.TargetContentKey is null))
                {
                    continue;
                }

                DbElement targetEd = _allTargetElements[ec.TargetContentKey!.Value];
                currentTargetElements[targetEd.Key] = targetEd;
            }

            edTr.TargetElements = currentTargetElements;
            edTr.DistinctTargetElementTypes = currentTargetElements
                .Values
                .SelectMany(v => v.DistinctTypeLiterals.Split(','))
                .Distinct()
                .ToList();

            Dictionary<int, DbElementType> mappedTypes = [];
            Dictionary<int, DbElementType> unmappedTypes = sourceEts
                .Select(kvp => kvp)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // check for no targets
            if (currentTargetElements.Count == 0)
            {
                edTr.UnmappedTypes.AddRange(unmappedTypes.Values);

                edTr.Messages.Add(
                    $"Element `{sourceEd.Id}` does not have any mapping targets." +
                    $" All source types are unmapped: `{sourceEd.FullCollatedTypeLiteral}`");

                // with no target, this is not mapped
                continue;
            }

            List<DbElementTypeComparison> etComparisons = _etcComparisonsBySourceElementKey[sourceEd.Key]
                .ToList();

            foreach (DbElementTypeComparison etc in etComparisons)
            {
                if (etc.TargetElementId is null)
                {
                    continue;
                }

                if ((etc.IsIdentical == true) ||
                    relationshipMaps(etc.Relationship))
                {
                    mappedTypes[etc.SourceElementTypeKey] = sourceEts[etc.SourceElementTypeKey];
                    unmappedTypes.Remove(etc.SourceElementTypeKey);

                    edTr.Messages.Add(
                        $"Element `{sourceEd.Id}` type `{etc.SourceTypeLiteral}`" +
                        $" maps to target element `{etc.TargetElementId}` type `{etc.TargetTypeLiteral}`" +
                        $" with relationship {etc.Relationship}.");

                    if (unmappedTypes.Count == 0)
                    {
                        fullyMappedElementsAllTargets.Add(sourceEd.Key);

                        edTr.MappedTypes.AddRange(mappedTypes.Values.OrderBy(et => et.Literal));
                        edTr.IsFullyMappedAcrossAllTargets = true;
                        edTr.MapsToIndividualTargets = fullyMappedComparisons;
                        edTr.Messages.Add(
                            $"Element `{sourceEd.Id}` has mapped all types across all target elements:" +
                            $" {string.Join(", ", currentTargetElements.Values.Select(targetEd => $"`{targetEd.Id}`"))}");

                        break;
                    }
                }
            }

            // check if unmapped types are quantity types
            if ((unmappedTypes.Count > 0) &&
                (currentTargetElements.Count > 0))
            {
                DbStructureComparison? pairQuantityComparison = DbStructureComparison.SelectSingle(
                    _db,
                    SourceFhirPackageKey: _packagePair.SourcePackageKey,
                    SourceId: "Quantity",
                    TargetFhirPackageKey: _packagePair.TargetPackageKey,
                    TargetId: "Quantity");

                CMR qRelationship = pairQuantityComparison?.Relationship ?? CMR.Equivalent;

                // iterate over each unmapped type to see if it is a quantity type
                foreach (DbElementType unmappedType in unmappedTypes.Values)
                {
                    if (!unmappedType.IsQuantityType())
                    {
                        continue;
                    }

                    string sourceNormalizedTypeName = unmappedType.GetNormalizedName();

                    // build a list of target types and their elements (elements will be duplicated)
                    List<(DbElement, DbElementType)> targetQuantityTypes = [];
                    foreach (DbElement te in currentTargetElements.Values)
                    {
                        if (!_targetElementTypesByElementKey.Contains(te.Key))
                        {
                            continue;
                        }

                        IEnumerable<DbElementType> targetTypes = _targetElementTypesByElementKey[te.Key];

                        foreach (DbElementType tet in targetTypes)
                        {
                            if (tet.IsQuantityType())
                            {
                                targetQuantityTypes.Add((te, tet));
                            }
                        }
                    }

                    // resolve a source structure for the normalized type name
                    DbStructureDefinition? sourceQSd = DbStructureDefinition.SelectSingle(
                        _db,
                        FhirPackageKey: _packagePair.SourcePackageKey,
                        Name: sourceNormalizedTypeName);

                    if ((sourceQSd is null) &&
                        (sourceNormalizedTypeName != "Quantity"))
                    {
                        // use the base Quantity type
                        sourceQSd = DbStructureDefinition.SelectSingle(
                            _db,
                            FhirPackageKey: _packagePair.SourcePackageKey,
                            Name: "Quantity");
                    }

                    if (sourceQSd is null)
                    {
                        continue;
                    }

                    // iterate over any matches
                    foreach ((DbElement targetEd, DbElementType targetType) in targetQuantityTypes)
                    {
                        string targetNormalizedTypeName = targetType.GetNormalizedName();

                        // if the source and target normalized names are the same, we can assume equivalence anyway
                        if (sourceNormalizedTypeName == targetNormalizedTypeName)
                        {
                            edTr.Messages.Add(
                                $"Element `{sourceEd.Id}` normalized quantity type `{sourceNormalizedTypeName}`" +
                                $" maps to target element `{targetEd.Id}` normalized quantity type `{targetNormalizedTypeName}`" +
                                $" with assumed equivalence based on profile and type matching.");

                            mappedTypes[unmappedType.Key] = unmappedType;
                            unmappedTypes.Remove(unmappedType.Key);
                            if (unmappedTypes.Count == 0)
                            {
                                fullyMappedElementsAllTargets.Add(sourceEd.Key);

                                edTr.MappedTypes.Add(unmappedType);
                                edTr.IsFullyMappedAcrossAllTargets = true;
                                edTr.MapsToIndividualTargets = fullyMappedComparisons;
                                edTr.Messages.Add(
                                    $"Element `{sourceEd.Id}` has mapped all types across all target elements:" +
                                    $" {string.Join(", ", currentTargetElements.Values.Select(targetEd => $"`{targetEd.Id}`"))}");

                                edTr.QuantityBasedRelationship = qRelationship;

                                break;
                            }

                            continue;
                        }

                        // resolve a target structure for the normalized type name
                        DbStructureDefinition? targetQSd = DbStructureDefinition.SelectSingle(
                            _db,
                            FhirPackageKey: _packagePair.TargetPackageKey,
                            Name: targetNormalizedTypeName);

                        if ((targetQSd is null) &&
                            (targetNormalizedTypeName != "Quantity"))
                        {
                            // use the base Quantity type
                            targetQSd = DbStructureDefinition.SelectSingle(
                                _db,
                                FhirPackageKey: _packagePair.TargetPackageKey,
                                Name: "Quantity");
                        }

                        if (targetQSd is null)
                        {
                            continue;
                        }

                        // check for comparison between the structures
                        DbStructureComparison? qComparison = DbStructureComparison.SelectSingle(
                            _db,
                            SourceFhirPackageKey: _packagePair.SourcePackageKey,
                            SourceStructureKey: sourceQSd.Key,
                            TargetFhirPackageKey: _packagePair.TargetPackageKey,
                            TargetStructureKey: targetQSd.Key);

                        if ((qComparison is not null) &&
                            relationshipMaps(qComparison.Relationship))
                        {
                            edTr.Messages.Add(
                                $"Element `{sourceEd.Id}` normalized quantity type `{sourceNormalizedTypeName}`" +
                                $" maps to target element `{targetEd.Id}` normalized quantity type `{targetNormalizedTypeName}`" +
                                $" with relationship {qComparison.Relationship}.");

                            mappedTypes[unmappedType.Key] = unmappedType;
                            unmappedTypes.Remove(unmappedType.Key);
                            if (unmappedTypes.Count == 0)
                            {
                                fullyMappedElementsAllTargets.Add(sourceEd.Key);

                                edTr.MappedTypes.Add(unmappedType);
                                edTr.IsFullyMappedAcrossAllTargets = true;
                                edTr.MapsToIndividualTargets = fullyMappedComparisons;
                                edTr.Messages.Add(
                                    $"Element `{sourceEd.Id}` has mapped all types across all target elements:" +
                                    $" {string.Join(", ", currentTargetElements.Values.Select(targetEd => $"`{targetEd.Id}`"))}");

                                edTr.QuantityBasedRelationship = qComparison.Relationship ?? qRelationship;

                                break;
                            }

                            continue;
                        }

                        // if there is no comparison, check to see if there is a mapping (primary first, then we'll check fallback)
                        DbStructureMapping? qMapping = DbStructureMapping.SelectSingle(
                            _db,
                            IsFallback: false,
                            SourceFhirPackageKey: _packagePair.SourcePackageKey,
                            SourceStructureKey: sourceQSd.Key,
                            TargetFhirPackageKey: _packagePair.TargetPackageKey,
                            TargetStructureKey: targetQSd.Key);

                        qMapping ??= DbStructureMapping.SelectSingle(
                            _db,
                            IsFallback: true,
                            SourceFhirPackageKey: _packagePair.SourcePackageKey,
                            SourceStructureKey: sourceQSd.Key,
                            TargetFhirPackageKey: _packagePair.TargetPackageKey,
                            TargetStructureKey: targetQSd.Key);

                        if ((qMapping is not null) &&
                            relationshipMaps(qMapping.Relationship))
                        {
                            edTr.Messages.Add(
                                $"Element `{sourceEd.Id}` normalized quantity type `{sourceNormalizedTypeName}`" +
                                $" maps to target element `{targetEd.Id}` normalized quantity type `{targetNormalizedTypeName}`" +
                                $" with relationship {qMapping.Relationship}" +
                                $" (source ConceptMap: `{(qMapping.ConceptMapFilename ?? "-")}`, source FML: `{(qMapping.FmlFilename ?? "-")}`).");

                            mappedTypes[unmappedType.Key] = unmappedType;
                            unmappedTypes.Remove(unmappedType.Key);
                            if (unmappedTypes.Count == 0)
                            {
                                fullyMappedElementsAllTargets.Add(sourceEd.Key);

                                edTr.MappedTypes.Add(unmappedType);
                                edTr.IsFullyMappedAcrossAllTargets = true;
                                edTr.MapsToIndividualTargets = fullyMappedComparisons;
                                edTr.Messages.Add(
                                    $"Element `{sourceEd.Id}` has mapped all types across all target elements:" +
                                    $" {string.Join(", ", currentTargetElements.Values.Select(targetEd => $"`{targetEd.Id}`"))}");

                                edTr.QuantityBasedRelationship = qMapping.Relationship ?? qRelationship;

                                break;
                            }

                            continue;
                        }
                    }

                    if (unmappedTypes.Count == 0)
                    {
                        break;
                    }
                }
            }

            List<string> unmappedDistinctTypeNames = unmappedTypes.Select(kvp => kvp.Value.TypeName ?? kvp.Value.Literal).Distinct().ToList();

            // check if unmapped types can be handled via child element mappings (distributed mapping)
            if ((unmappedTypes.Count > 0) &&
                (sourceEts.Count <= 2) &&
                //(unmappedDistinctTypeNames.Count == 1) &&
                (sourceEd.ChildElementCount == 0) &&
                (currentTargetElements.Count > 0))
            {
                List<ChildTypeMappingResult> childMappingResults = [];

                // iterate over each unmapped type to check if its children provide mappings
                foreach (DbElementType unmappedType in unmappedTypes.Values)
                {
                    ChildTypeMappingResult cmr = checkChildElementMappings(
                        unmappedType,
                        sourceEd,
                        currentTargetElements);
                    childMappingResults.Add(cmr);
                }

                // process the results and update unmappedTypes accordingly
                HashSet<int> resolvedViaChildren = processChildMappingResults(
                    unmappedTypes,
                    childMappingResults,
                    edTr,
                    sourceEd);

                edTr.ChildTypeMappingResults.AddRange(childMappingResults);

                // if we resolved any types via children, log summary
                if (resolvedViaChildren.Count > 0)
                {
                    // update distinct type names
                    unmappedDistinctTypeNames = unmappedTypes.Select(kvp => kvp.Value.TypeName ?? kvp.Value.Literal).Distinct().ToList();

                    edTr.Messages.Add(
                        $"Element `{sourceEd.Id}` resolved {resolvedViaChildren.Count} unmapped type(s)" +
                        $" via distributed child element mappings");
                }
            }

            // if we still have unmapped types, then this element is not fully mapped across all targets
            if (unmappedTypes.Count != 0)
            {
                edTr.UnmappedTypes.AddRange(unmappedTypes.Values);

                edTr.Messages.Add(
                    $"Element `{sourceEd.Id}` does not fully map to targets:" +
                    $" {string.Join(", ", currentTargetElements.Values.Select(te => $"`{te.Id}`"))}," +
                    $" because it does not account for source types:" +
                    $" {string.Join(", ", unmappedTypes.Values.Select(et => $"`{et.Literal}`"))}");

                // with unmapped types, there is no full mapping
                continue;
            }

            edTr.Messages.Add(
                $"Element `{sourceEd.Id}` has fully-mapped types across: " +
                $" {string.Join(", ", currentTargetElements.Values.Select(te => $"`{te.Id}`"))}");

            edTr.IsFullyMappedAcrossAllTargets = true;
            if (elementComparisons.Count == 1)
            {
                edTr.MapsToIndividualTargets = elementComparisons;
            }
            else
            {
                edTr.MapsToCombinationOfTargets.AddRange(elementComparisons);
            }
        }
    }


    private DbElement? findCommonAncestor(
        int targetFhirPackageKey,
        List<DbElement> eds,
        bool requireNonPrimitive)
    {
        if (eds.Count == 0)
        {
            return null;
        }

        if (eds.Count == 1)
        {
            if (!requireNonPrimitive ||
                eds[0].IsChoiceType ||
                (eds[0].ChildElementCount > 0) ||
                (eds[0].IsPrimitiveTypeElement() != true))
            {
                return eds[0];
            }

            if ((eds[0].ParentElementKey is null) ||
                !_allTargetElements.TryGetValue(eds[0].ParentElementKey!.Value, out DbElement? parentEd))
            {
                throw new Exception("Element is primitive and has no parent, cannot find common ancestor.");
            }

            return parentEd;
        }

        List<int> structureKeys = eds.Select(ed => ed.StructureKey)
            .Distinct()
            .ToList();

        if (structureKeys.Count > 1)
        {
            throw new Exception("Cannot find common ancestor for elements from multiple structures.");
        }

        List<HashSet<string>> components = [];

        // build the list of path components that appear in each element
        foreach (DbElement ed in eds)
        {
            string[] idComponents = ed.Id.Split('.');
            for (int i = 0; i < idComponents.Length; i++)
            {
                if (components.Count <= i)
                {
                    components.Add([]);
                }
                components[i].Add(idComponents[i]);
            }
        }

        string? commonId = null;
        // iterate forward until we hit something we cannot work with
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i].Count > 1)
            {
                // multiple element paths at this component, so stop
                break;
            }
            commonId = string.Join('.', components[0..(i + 1)].Select(c => c.First()));
        }

        if (commonId is null)
        {
            return null;
        }

        DbElement? commonEd = DbElement.SelectSingle(_db, FhirPackageKey: targetFhirPackageKey, Id: commonId);
        if (commonEd is null)
        {
            throw new Exception($"Cannot resolve common ancestor element `{commonId}` in package key {targetFhirPackageKey}");
        }

        return commonEd;
    }

    /// <summary>
    /// Determines if an unmapped source type can be fully represented through mappings of its child elements to target elements.
    /// </summary>
    /// <param name="sourceEt">The source type that lacks a direct mapping</param>
    /// <param name="sourceEd">The source element containing this type</param>
    /// <returns>Result indicating if all children are mapped and how</returns>
    private ChildTypeMappingResult checkChildElementMappings(
        DbElementType sourceEt,
        DbElement sourceEd,
        Dictionary<int, DbElement> currentTargetElements)
    {
        ChildTypeMappingResult result = new()
        {
            SourceParentType = sourceEt,
            AllChildrenMapped = false,
            ChildMappings = [],
            MappedTypeChildren = [],
            UnmappedTypeChildren = []
        };

        // type must have a resolved structure to check children
        if (sourceEt.TypeStructureKey is null)
        {
            return result;
        }

        // get the child elements of this type structure, but filter out elements we should ignore (root, id, extension, modifierExtension)
        List<DbElement> typeChildren = _allSourceElementsBySdKey[sourceEt.TypeStructureKey.Value]
            .Where(ed => !skipElement(ed))
            .ToList();

        // if there are no meaningful child elements, this type can't be distributed
        if (typeChildren.Count == 0)
        {
            return result;
        }

        HashSet<int> targetStructureKeysHash = [];
        HashSet<string> targetTypeLiterals = [];

        // build a list of target types and their elements (elements will be duplicated)
        List<(DbElement, DbElementType)> targetTypes = [];
        foreach (DbElement te in currentTargetElements.Values)
        {
            targetStructureKeysHash.Add(te.StructureKey);

            if (!_targetElementTypesByElementKey.Contains(te.Key))
            {
                continue;
            }

            IEnumerable<DbElementType> currentTargetTypes = _targetElementTypesByElementKey[te.Key];

            foreach (DbElementType tet in currentTargetTypes)
            {
                targetTypes.Add((te, tet));
                targetTypeLiterals.Add(tet.Literal);
            }
        }

        List<int> targetStructureKeys = targetStructureKeysHash.ToList();

        // iterate over the child elements to try and map the types
        foreach (DbElement childEd in typeChildren)
        {
            // check to see if there is a source mapping that drilled into this type from the original source element
            string typeChildId = $"{sourceEd.Id}.{childEd.Name}";
            List<DbElementMapping> typeChildMappings = DbElementMapping.SelectList(
                _db,
                SourceFhirPackageKey: _packagePair.SourcePackageKey,
                SourceElementId: typeChildId,
                TargetFhirPackageKey: _packagePair.TargetPackageKey,
                TargetStructureKeyValues: targetStructureKeys);

            List<DbElementMapping> validTypeChildMappings = typeChildMappings
                .Where(em => (em.Relationship == CMR.Equivalent) || (em.Relationship == CMR.SourceIsNarrowerThanTarget))
                .ToList();

            // check for existing comparisons
            List<DbElementTypeComparison> childTypeComparisons = _etcComparisonsBySourceElementKey[childEd.Key].ToList();

            // find valid mappings (identical, equivalent, or source is narrower)
            List<DbElementTypeComparison> validComparisons = childTypeComparisons
                .Where(etc =>
                    (etc.IsIdentical == true) ||
                    (etc.Relationship == CMR.Equivalent) ||
                    (etc.Relationship == CMR.SourceIsNarrowerThanTarget))
                .Where(etc => (etc.TargetTypeLiteral is not null) && targetTypeLiterals.Contains(etc.TargetTypeLiteral))
                .ToList();

            // if we have valid mappings or comparisons, use them
            if ((validTypeChildMappings.Count > 0) ||
                (validComparisons.Count > 0))
            {
                HashSet<int> targetElementKeys = [];

                foreach (DbElementMapping tcm in validTypeChildMappings)
                {
                    if (tcm.TargetElementKey.HasValue)
                    {
                        targetElementKeys.Add(tcm.TargetElementKey.Value);
                    }
                }

                foreach (DbElementTypeComparison vm in validComparisons)
                {
                    if (vm.TargetElementKey.HasValue)
                    {
                        targetElementKeys.Add(vm.TargetElementKey.Value);
                    }
                }

                result.MappedTypeChildren.Add(childEd);
                result.ChildMappings[childEd.Key] = new ChildMappingInfo
                {
                    ChildElement = childEd,
                    TypeComparisons = validComparisons,
                    TypeChildMappings = validTypeChildMappings,
                    TargetElementKeys = targetElementKeys
                };

                continue;
            }

            bool childElementIsMapped = false;
            List<DbElementType> sourceChildEts = _sourceElementTypesByElementKey[childEd.Key].ToList();

            // iterate over the types in this source child element to try and find type-based mappings
            foreach (DbElementType sourceChildElementType in sourceChildEts)
            {
                if (sourceChildElementType.TypeStructureKey is null)
                {
                    continue;
                }

                // resolve a source structure for current type
                DbStructureDefinition? sourceCETSd = DbStructureDefinition.SelectSingle(
                    _db,
                    FhirPackageKey: _packagePair.SourcePackageKey,
                    Key: sourceChildElementType.TypeStructureKey);

                if (sourceCETSd is null)
                {
                    throw new Exception(
                        $"Cannot find structure definition for source child element type `{sourceChildElementType.Literal}`" +
                        $" (key {sourceChildElementType.Key}) in package key {_packagePair.SourcePackageKey}");
                }

                // iterate over any potential matches
                foreach ((DbElement targetEd, DbElementType targetType) in targetTypes)
                {
                    if (targetType.TypeStructureKey is null)
                    {
                        continue;
                    }

                    // resolve a target structure for the target type
                    DbStructureDefinition? targetCETSd = DbStructureDefinition.SelectSingle(
                        _db,
                        FhirPackageKey: _packagePair.TargetPackageKey,
                        Key: targetType.TypeStructureKey);

                    if (targetCETSd is null)
                    {
                        throw new Exception(
                            $"Cannot find structure definition for target child element type `{targetType.Literal}`" +
                            $" (key {targetType.Key}) in package key {_packagePair.TargetPackageKey}");
                    }

                    // check for comparison between the structures
                    DbStructureComparison? cetComparison = DbStructureComparison.SelectSingle(
                        _db,
                        SourceFhirPackageKey: _packagePair.SourcePackageKey,
                        SourceStructureKey: sourceCETSd.Key,
                        TargetFhirPackageKey: _packagePair.TargetPackageKey,
                        TargetStructureKey: targetCETSd.Key);

                    if ((cetComparison is not null) &&
                        ((cetComparison.Relationship == CMR.Equivalent) || (cetComparison.Relationship == CMR.SourceIsNarrowerThanTarget)))
                    {
                        childElementIsMapped = true;
                        result.MappedTypeChildren.Add(childEd);
                        result.ChildMappings[childEd.Key] = new ChildMappingInfo
                        {
                            ChildElement = childEd,
                            TypeComparisons = validComparisons,
                            TypeChildMappings = validTypeChildMappings,
                            TargetElementKeys = [targetEd.Key],
                        };

                        continue;
                    }

                    // if there is no comparison, check to see if there is a mapping (primary first, then we'll check fallback)
                    DbStructureMapping? cetMapping = DbStructureMapping.SelectSingle(
                        _db,
                        IsFallback: false,
                        SourceFhirPackageKey: _packagePair.SourcePackageKey,
                        SourceStructureKey: sourceCETSd.Key,
                        TargetFhirPackageKey: _packagePair.TargetPackageKey,
                        TargetStructureKey: targetCETSd.Key);

                    cetMapping ??= DbStructureMapping.SelectSingle(
                        _db,
                        IsFallback: true,
                        SourceFhirPackageKey: _packagePair.SourcePackageKey,
                        SourceStructureKey: sourceCETSd.Key,
                        TargetFhirPackageKey: _packagePair.TargetPackageKey,
                        TargetStructureKey: targetCETSd.Key);

                    if ((cetMapping is not null) &&
                        ((cetMapping.Relationship == CMR.Equivalent) || (cetMapping.Relationship == CMR.SourceIsNarrowerThanTarget)))
                    {
                        childElementIsMapped = true;
                        result.MappedTypeChildren.Add(childEd);
                        result.ChildMappings[childEd.Key] = new ChildMappingInfo
                        {
                            ChildElement = childEd,
                            TypeComparisons = validComparisons,
                            TypeChildMappings = validTypeChildMappings,
                            TargetElementKeys = [targetEd.Key],
                        };

                        continue;
                    }
                }
            }

            if (childElementIsMapped)
            {
                result.MappedTypeChildren.Add(childEd);
            }
            else
            {
                result.UnmappedTypeChildren.Add(childEd);
                continue;
            }
        }

        // All children must be mapped for distributed mapping to work
        result.AllChildrenMapped = (result.UnmappedTypeChildren.Count == 0);

        return result;
    }

    /// <summary>
    /// Processes the results of child mapping checks and updates tracking accordingly.
    /// </summary>
    /// <param name="unmappedTypes">Dictionary of unmapped types (will be modified)</param>
    /// <param name="childMappingResults">Results from checking child mappings</param>
    /// <param name="elementTrackingRec">The tracking record to update with messages</param>
    /// <param name="sourceElement">The source element being processed</param>
    /// <returns>Set of unmapped type keys that were successfully handled via children</returns>
    private HashSet<int> processChildMappingResults(
        Dictionary<int, DbElementType> unmappedTypes,
        List<ChildTypeMappingResult> childMappingResults,
        ElementOutcomeTrackingRecord elementTrackingRec,
        DbElement sourceElement)
    {
        HashSet<int> resolvedTypeKeys = [];

        foreach (ChildTypeMappingResult cmr in childMappingResults)
        {
            if (!cmr.AllChildrenMapped)
            {
                // still have unmapped
                if (cmr.UnmappedTypeChildren.Count > 0)
                {
                    elementTrackingRec.Messages.Add(
                        $"Element `{sourceElement.Id}` has unmapped type `{cmr.SourceParentType.Literal}` -" +
                        $" cannot distribute via children because the following children lack mappings:" +
                        $" {string.Join(", ", cmr.UnmappedTypeChildren.Select(uc => $"`{uc.Id}`"))}");
                }
                continue;
            }

            // all children are mapped - this is a successful distributed mapping for this type build a nice message
            List<string> childMappingDescriptions = cmr.ChildMappings.Values
                .Select(cmi =>
                {
                    // get target element names
                    List<string> targetNames = cmi.TargetElementKeys
                        .Select(tek => _allTargetElements.TryGetValue(tek, out DbElement? te)
                            ? te.Id
                            : $"Unknown({tek})")
                        .ToList();

                    return $"`{cmi.ChildElement.Name}` -> {string.Join(", ", targetNames.Select(tn => $"`{tn}`"))}";
                })
                .ToList();

            elementTrackingRec.Messages.Add(
                $"Element `{sourceElement.Id}` has unmapped source type `{cmr.SourceParentType.Literal}`" +
                $" that fully maps when distributed across target elements via child mappings:" +
                $" {string.Join("; ", childMappingDescriptions)}");

            // remove from unmapped types using the CORRECT KEY (parent type key, not child type key)
            if (unmappedTypes.Remove(cmr.SourceParentType.Key))
            {
                resolvedTypeKeys.Add(cmr.SourceParentType.Key);
            }
        }

        return resolvedTypeKeys;
    }

}
