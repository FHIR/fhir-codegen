using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Hl7.Fhir.Language.Debugging;
using Hl7.Fhir.Model;
using Hl7.Fhir.Model.CdsHooks;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.Exporter.IgExporter;
using static Hl7.Fhir.Model.PaymentReconciliation;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Exporter;

public class StructureFhirExporter
{
    private readonly XVerExporter _exporter;
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private Dictionary<FhirReleases.FhirSequenceCodes, List<DbElementType>> _extensionValueTypes = [];
    private Dictionary<FhirReleases.FhirSequenceCodes, HashSet<string>> _extensionValueTypeNames = [];

    private Dictionary<FhirReleases.FhirSequenceCodes, List<DbElement>> _canonicalTargetElements = [];
    private Dictionary<FhirReleases.FhirSequenceCodes, HashSet<string>> _canonicalTargetResourceNames = [];

    private class PackagePairStructureMappingTracker
    {
        public Dictionary<string, List<string>> TargetStructuresByName { get; set; } = [];
        public Dictionary<string, List<string>> TargetStructuresByUrl { get; set; } = [];
        public Dictionary<string, List<string>> TargetProfilesByName { get; set; } = [];
        public Dictionary<string, List<string>> TargetProfilesByUrl { get; set; } = [];

        public List<string> GetTargets(string source)
        {
            HashSet<string> valid = [];

            if (TargetStructuresByName.TryGetValue(source, out List<string>? values))
            {
                foreach (string v in values)
                {
                    valid.Add(v);
                }
            }

            if (TargetStructuresByUrl.TryGetValue(source, out values))
            {
                foreach (string v in values)
                {
                    valid.Add(v);
                }
            }

            if (TargetProfilesByName.TryGetValue(source, out values))
            {
                foreach (string v in values)
                {
                    valid.Add(v);
                }
            }

            if (TargetProfilesByUrl.TryGetValue(source, out values))
            {
                foreach (string v in values)
                {
                    valid.Add(v);
                }
            }

            return valid.Order().ToList();
        }
    }

    private Dictionary<
        (FhirReleases.FhirSequenceCodes source, FhirReleases.FhirSequenceCodes target),
        PackagePairStructureMappingTracker> _resourceReferenceLookup = [];

    private static readonly FhirArtifactClassEnum[] _xverSourceArtifactClasses =
        [FhirArtifactClassEnum.Resource, FhirArtifactClassEnum.ComplexType];

    public StructureFhirExporter(
        XVerExporter exporter,
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _exporter = exporter;
        _db = db;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StructureFhirExporter>();
    }

    public void Export(XVerExportTrackingRecord tr)
    {
        // iterate over the XVer IGs
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            if (!_extensionValueTypes.ContainsKey(igTr.PackagePair.TargetFhirSequence))
            {
                DbStructureDefinition? extensionStructure = DbStructureDefinition.SelectSingle(
                    _db,
                    FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                    Name: "Extension",
                    ArtifactClass: FhirArtifactClassEnum.ComplexType);

                if (extensionStructure is null)
                {
                    throw new Exception($"Extension structure definition not found in target package `{igTr.PackagePair.TargetPackage.PackageId}`");
                }

                DbElement? extValueElement = DbElement.SelectSingle(
                    _db,
                    FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                    StructureKey: extensionStructure.Key,
                    Id: "Extension.value[x]");

                if (extValueElement is null)
                {
                    throw new Exception($"Extension.value[x] element not found in target package `{igTr.PackagePair.TargetPackage.PackageId}`");
                }

                _extensionValueTypes[igTr.PackagePair.TargetFhirSequence] = DbElementType.SelectList(
                    _db,
                    ElementKey: extValueElement.Key);

                _extensionValueTypeNames[igTr.PackagePair.TargetFhirSequence] = _extensionValueTypes[igTr.PackagePair.TargetFhirSequence]
                    .Select(et => et.TypeName ?? et.Literal)
                    .ToHashSet();
            }

            if (!_canonicalTargetElements.ContainsKey(igTr.PackagePair.TargetFhirSequence))
            {
                List<int> resourceKeys = DbStructureDefinition.SelectList(
                    _db,
                    FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                    ArtifactClass: FhirArtifactClassEnum.Resource)
                    .Select(sd => sd.Key)
                    .ToList();

                List<DbElement> urlElements = DbElement.SelectList(
                    _db,
                    FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                    Name: "url",
                    StructureKeyValues: resourceKeys);

                // remove non-root URL elements
                urlElements.RemoveAll(ed => ed.Id.Count('.') > 1);

                _canonicalTargetElements[igTr.PackagePair.TargetFhirSequence] = urlElements;
                _canonicalTargetResourceNames[igTr.PackagePair.TargetFhirSequence] = urlElements.Select(ed => ed.Id.Split('.')[0]).ToHashSet();
            }

            if (!_resourceReferenceLookup.ContainsKey(igTr.PackagePair.SequencePair))
            {
                PackagePairStructureMappingTracker structureMappingTracker = new();
                _resourceReferenceLookup[igTr.PackagePair.SequencePair] = structureMappingTracker;

                List<DbStructureOutcome> structureOutcomes = DbStructureOutcome.SelectList(
                    _db,
                    SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                    TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey);

                foreach (DbStructureOutcome sdOutcome in structureOutcomes)
                {
                    string sdTargetName = sdOutcome.TargetName ?? "Basic";
                    string sdTargetUrl = sdOutcome.TargetCanonicalUnversioned ?? "http://hl7.org/fhir/StructureDefinition/Basic";

                    if (!structureMappingTracker.TargetStructuresByName.TryGetValue(sdOutcome.SourceName, out List<string>? sdNameTargets))
                    {
                        sdNameTargets = [];
                        structureMappingTracker.TargetStructuresByName[sdOutcome.SourceName] = sdNameTargets;
                    }

                    if (!sdNameTargets.Contains(sdTargetName))
                    {
                        sdNameTargets.Add(sdTargetName);
                    }

                    if (!structureMappingTracker.TargetStructuresByUrl.TryGetValue(sdOutcome.SourceCanonicalUnversioned, out List<string>? sdUrlTargets))
                    {
                        sdUrlTargets = [];
                        structureMappingTracker.TargetStructuresByName[sdOutcome.SourceCanonicalUnversioned] = sdUrlTargets;
                    }

                    if (!sdUrlTargets.Contains(sdTargetUrl))
                    {
                        sdUrlTargets.Add(sdTargetUrl);
                    }

                    string profileTarget = sdOutcome.GenUrl!;
                    if (!structureMappingTracker.TargetProfilesByName.TryGetValue(sdOutcome.SourceName, out List<string>? profileNameTargets))
                    {
                        profileNameTargets = [];
                        structureMappingTracker.TargetProfilesByName[sdOutcome.SourceName] = profileNameTargets;
                    }

                    if (!profileNameTargets.Contains(profileTarget))
                    {
                        profileNameTargets.Add(profileTarget);
                    }

                    if (!structureMappingTracker.TargetProfilesByUrl.TryGetValue(sdOutcome.SourceCanonicalUnversioned, out List<string>? profileUrlTargets))
                    {
                        profileUrlTargets = [];
                        structureMappingTracker.TargetProfilesByName[sdOutcome.SourceCanonicalUnversioned] = profileUrlTargets;
                    }

                    if (!profileUrlTargets.Contains(profileTarget))
                    {
                        profileUrlTargets.Add(profileTarget);
                    }
                }
            }

            // export extensions
            exportExtensions(igTr);

            // export profiles
            exportProfiles(igTr);

            // TODO: decide if we are exporting type maps 

            // export resource maps
            exportResourceMaps(igTr);

            // export element maps
            exportElementMaps(igTr);
        }
    }

    private void exportElementMaps(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.ConceptMapToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        CrossVersionExporter.ConceptMapToR4? exporterR4 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R5)
            ? new()
            : null;

        if (igTr.ElementMapDir is null)
        {
            throw new Exception("ElementMapDir is null");
        }

        string dir = igTr.ElementMapDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing element maps for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the source structures (Resources + ComplexTypes)
        List<DbStructureDefinition> sourceSds = [];
        foreach (FhirArtifactClassEnum artifactClass in _xverSourceArtifactClasses)
        {
            sourceSds.AddRange(DbStructureDefinition.SelectList(
                _db,
                FhirPackageKey: igTr.PackagePair.SourcePackageKey,
                ArtifactClass: artifactClass));
        }

        // iterate over our source structures
        foreach (DbStructureDefinition sourceSd in sourceSds)
        {
            // skip abstract bases that don't need element-level maps
            if (StructurePageExporter.StructureExportExclusions.Contains(sourceSd.Id) ||
                StructurePageExporter.StructureExportExclusions.Contains(sourceSd.Name))
            {
                continue;
            }

            // get the structure outcomes for this structure
            List<DbStructureOutcome> sdOutcomes = DbStructureOutcome.SelectList(
                _db,
                SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                SourceStructureKey: sourceSd.Key);

            if (sdOutcomes.Count == 0)
            {
                continue;
            }

            // create a concept map for this resource
            ConceptMap edCm = createElementConceptMap(igTr, sdOutcomes.First());

            // add the outcomes
            addMappedElementsToElementCm(
                igTr,
                edCm,
                sourceSd,
                sdOutcomes);

            // write the profile to a file
            string filename = sdOutcomes.First().ElementConceptMapFileName
                ?? throw new Exception($"Failed to write concept map for {sourceSd.VersionedUrl} to {igTr.PackagePair.TargetPackageShortName}");
            string path = Path.Combine(dir, filename + ".json");
            if (exporterR3 is not null)
            {
                File.WriteAllText(path, exporterR3.ToJson(edCm, new SerializerSettings() { Pretty = true }));
            }
            else if (exporterR4 is not null)
            {
                File.WriteAllText(path, exporterR4.ToJson(edCm, new SerializerSettings() { Pretty = true }));
            }
            else
            {
                File.WriteAllText(path, edCm.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
            }
            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = edCm.Name,
                Id = edCm.Id,
                Url = edCm.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ConceptMap.GetLiteral(),
                Version = edCm.Version,
                Description = edCm.Description ?? edCm.Title ?? $"ConceptMap: {edCm.Url}",
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} element maps for `{igTr.PackageId}`");
        igTr.ElementMapFiles = exported;
    }

    private void addMappedElementsToElementCm(
        XVerIgExportTrackingRecord igTr,
        ConceptMap edCm,
        DbStructureDefinition sourceSd,
        List<DbStructureOutcome> sdOutcomes)
    {
        List<int> targetSdKeys = sdOutcomes
            .Where(sdo => sdo.TargetStructureKey is not null)
            .Select(sdo => sdo.TargetStructureKey!.Value)
            .Distinct()
            .ToList();
        
        Dictionary<int, DbStructureDefinition> targetSds = DbStructureDefinition.SelectDict(
            _db,
            KeyValues: targetSdKeys);

        // get the element outcomes for this structure to the target version
        List<DbElementOutcome> edOutcomes = DbElementOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            SourceStructureKey: sourceSd.Key,
            orderByProperties: [nameof(DbElementOutcome.SourceResourceOrder)]);
        ILookup<int, DbElementOutcome> edOutcomesByKey = edOutcomes.ToLookup(eo => eo.Key);

        Dictionary<string, ConceptMap.GroupComponent> groupsByTarget = [];
        Dictionary<string, ConceptMap.SourceElementComponent> cmSources = [];
        Dictionary<int, string> genUrlLookup = [];

        foreach (DbElementOutcome edOutcome in edOutcomes)
        {
            // skip the root element
            if (edOutcome.SourceResourceOrder == 0)
            {
                continue;
            }

            // get the list of targets for this element outcome
            List<DbElementOutcomeTarget> edTargets = DbElementOutcomeTarget.SelectList(
                    _db,
                    ElementOutcomeKey: edOutcome.Key,
                    orderByProperties: [nameof(DbElementOutcomeTarget.TargetElementId), nameof(DbElementOutcomeTarget.ContextElementId)]);

            if (edTargets.Count == 0)
            {
                // this element is not available in this target (e.g., mapped to another resource)
                continue;
            }

            HashSet<string> usedTargetLiterals = [];

            CMR relationship;
            if (edOutcome.IsIdentical || edOutcome.IsEquivalent)
            {
                relationship = CMR.Equivalent;
            }
            else if (edOutcome.IsBroaderThanTarget)
            {
                relationship = CMR.SourceIsBroaderThanTarget;
            }
            else if (edOutcome.IsNarrowerThanTarget)
            {
                relationship = CMR.SourceIsNarrowerThanTarget;
            }
            else
            {
                relationship = CMR.RelatedTo;
            }

            // iterate over the targets
            foreach (DbElementOutcomeTarget edTarget in edTargets)
            {
                if ((edTarget.TargetStructureKey is null) ||
                    (edTarget.TargetElementId is null))
                {
                    continue;
                }

                DbStructureDefinition? targetSd = null;
                ConceptMap.GroupComponent? currentGroup;

                targetSd = targetSds[edTarget.TargetStructureKey.Value];
                if (!groupsByTarget.TryGetValue(targetSd.VersionedUrl, out currentGroup))
                {
                    currentGroup = new()
                    {
                        Source = sourceSd.VersionedUrl,
                        Target = targetSd.VersionedUrl,
                        Element = [],
                    };
                    edCm.Group.Add(currentGroup);
                    groupsByTarget[targetSd.VersionedUrl] = currentGroup;
                }

                string cmSourceKey = targetSd.VersionedUrl + "|" + edOutcome.SourceId;
                if (!cmSources.TryGetValue(cmSourceKey, out ConceptMap.SourceElementComponent? cmSourceElement))
                {
                    cmSourceElement = new()
                    {
                        Code = edOutcome.SourceId,
                        Display = edOutcome.SourceName,
                        Target = [],
                    };
                    currentGroup.Element.Add(cmSourceElement);
                    cmSources[cmSourceKey] = cmSourceElement;
                }

                string targetCode = edTarget.TargetElementId;
                if (usedTargetLiterals.Add(cmSourceKey + targetCode))
                {
                    CMR targetRelationship = relationship;

                    if (edTarget.FullyMapsToThisTarget)
                    {
                        targetRelationship = relationship switch
                        {
                            CMR.Equivalent => relationship,
                            CMR.SourceIsNarrowerThanTarget => relationship,
                            _ => CMR.Equivalent,
                        };
                    }
                    else
                    {
                        targetRelationship = relationship switch
                        {
                            CMR.Equivalent => CMR.SourceIsBroaderThanTarget,
                            CMR.SourceIsNarrowerThanTarget => CMR.RelatedTo,
                            _ => relationship,
                        };
                    }

                    // add the target for this basic element equivalent
                    ConceptMap.TargetElementComponent cmTargetElement = new()
                    {
                        Code = targetCode,
                        Display = targetCode,
                        Relationship = targetRelationship,
                        Comment = edOutcome.GenMappingComment ?? edOutcome.Comments,
                    };
                    cmSourceElement.Target.Add(cmTargetElement);
                }
            }

            bool isAlternateCanonical = edOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateCanonical;
            bool isAlternateReference = edOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateReference;

            bool additionalAlternateCanonical = !isAlternateCanonical &&
                (edOutcome.AlternateCanonicalTargetsLiteral is not null);

            bool additionalAlternateReference = !isAlternateReference &&
                (edOutcome.AlternateReferenceTargetsLiteral is not null);

            // check for mapping to a Basic element
            if (edOutcome.BasicElementId is not null)
            {
                string basicCanonical = $"http://hl7.org/fhir/StructureDefinition/Basic|{igTr.PackagePair.TargetPackage.PackageVersion}";
                if (!groupsByTarget.TryGetValue(basicCanonical, out ConceptMap.GroupComponent? currentGroup))
                {
                    currentGroup = new()
                    {
                        Source = sourceSd.VersionedUrl,
                        Target = basicCanonical,
                        Element = [],
                    };
                    edCm.Group.Add(currentGroup);
                    groupsByTarget[basicCanonical] = currentGroup;
                }

                string cmSourceKey = basicCanonical + "|" + edOutcome.SourceId;
                if (!cmSources.TryGetValue(cmSourceKey, out ConceptMap.SourceElementComponent? cmSourceElement))
                {
                    cmSourceElement = new()
                    {
                        Code = edOutcome.SourceId,
                        Display = edOutcome.SourceName,
                        Target = [],
                    };
                    currentGroup.Element.Add(cmSourceElement);
                    cmSources[cmSourceKey] = cmSourceElement;
                }

                string targetCode = edOutcome.BasicElementId;
                if (usedTargetLiterals.Add(cmSourceKey + targetCode))
                {
                    // add the target for this basic element equivalent
                    ConceptMap.TargetElementComponent cmTargetElement = new()
                    {
                        Code = targetCode,
                        Display = targetCode,
                        Relationship = CMR.Equivalent,
                        Comment = edOutcome.GenMappingComment ?? edOutcome.Comments,
                    };
                    cmSourceElement.Target.Add(cmTargetElement);
                }
            }

            if (igTr.EdOutcomeMapTargets.TryGetValue(edOutcome.Key, out List<EdOutcomeMapTargetRecord>? mapTargetRecs) &&
                (mapTargetRecs.Count > 0))
            {
                foreach (EdOutcomeMapTargetRecord mapTargetRec in mapTargetRecs.Distinct())
                {
                    ConceptMap.GroupComponent? currentGroup;
                    string groupTarget = mapTargetRec.sdCanonical;

                    if (!groupsByTarget.TryGetValue(groupTarget, out currentGroup))
                    {
                        currentGroup = new()
                        {
                            Source = sourceSd.VersionedUrl,
                            Target = groupTarget,
                            Element = [],
                        };
                        edCm.Group.Add(currentGroup);
                        groupsByTarget[groupTarget] = currentGroup;
                    }

                    string cmSourceKey = groupTarget + "|" + edOutcome.SourceId;
                    if (!cmSources.TryGetValue(cmSourceKey, out ConceptMap.SourceElementComponent? cmSourceElement))
                    {
                        cmSourceElement = new()
                        {
                            Code = edOutcome.SourceId,
                            Display = edOutcome.SourceName,
                            Target = [],
                        };
                        currentGroup.Element.Add(cmSourceElement);
                        cmSources[cmSourceKey] = cmSourceElement;
                    }

                    string targetCode = mapTargetRec.elementPath;
                    if (usedTargetLiterals.Add(cmSourceKey + targetCode))
                    {
                        // add the target
                        ConceptMap.TargetElementComponent cmTargetElement = new()
                        {
                            Code = targetCode,
                            Relationship = relationship == CMR.Equivalent ? CMR.Equivalent : CMR.SourceIsBroaderThanTarget,
                            Comment = edOutcome.GenMappingComment ?? edOutcome.Comments,
                        };
                        cmSourceElement.Target.Add(cmTargetElement);
                    }
                }
            }
        }
    }

    private ConceptMap createElementConceptMap(
        XVerIgExportTrackingRecord igTr,
        DbStructureOutcome sdOutcome)
    {
        string targetId = sdOutcome.TargetId ?? "Basic";
        string targetVersion = sdOutcome.TargetVersion ?? igTr.PackagePair.TargetPackage.PackageVersion;

        (_, string name) = igTr.GetName(sdOutcome.ElementConceptMapName!, sdOutcome.ElementConceptMapLongId!);

        ConceptMap vsCm = new()
        {
            Id = sdOutcome.ElementConceptMapLongId,
            Url = sdOutcome.ElementConceptMapUrl,
            Name = name,
            Version = _exporter._crossDefinitionVersion,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version mapping for FHIR {igTr.PackagePair.SourceFhirSequence} {sdOutcome.SourceId} to FHIR {igTr.PackagePair.TargetFhirSequence} {targetId}",
            Description = $"This ConceptMap represents cross-version mappings for elements" +
                $" from a FHIR {igTr.PackagePair.SourceFhirSequence} {sdOutcome.SourceId}" +
                $" to FHIR {igTr.PackagePair.TargetFhirSequence}.",
            Status = PublicationStatus.Active,
            Experimental = false,
            SourceScope = new Canonical($"http://hl7.org/fhir/{igTr.PackagePair.SourceFhirVersionShort}"),
            TargetScope = new FhirUri($"http://hl7.org/fhir/{igTr.PackagePair.TargetFhirVersionShort}"),
        };

        return vsCm;
    }

    private void exportResourceMaps(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.ConceptMapToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        CrossVersionExporter.ConceptMapToR4? exporterR4 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R5)
            ? new()
            : null;

        if (igTr.ResourceMapDir is null)
        {
            throw new Exception("ResourceMapDir is null");
        }

        string dir = igTr.ResourceMapDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing resource maps for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        ConceptMap cm = createResourceConceptMap(igTr);

        // get the structure outcomes for this pair
        List<DbStructureOutcome> sdOutcomes = DbStructureOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            orderByProperties: [nameof(DbStructureOutcome.SourceName), nameof(DbStructureOutcome.TargetName)]);

        string? lastSourceId = null;

        ConceptMap.SourceElementComponent? currentSourceElement = null;

        // iterate over the outcomes
        foreach (DbStructureOutcome sdOutcome in sdOutcomes)
        {
            // check if we need a new source element
            if ((currentSourceElement is null) ||
                (lastSourceId != sdOutcome.SourceId))
            {
                // create a new source element
                currentSourceElement = new()
                {
                    Code = sdOutcome.SourceId,
                    Display = sdOutcome.SourceName,
                    Target = [],
                };
                cm.Group[0].Element.Add(currentSourceElement);
                lastSourceId = sdOutcome.SourceId;
            }

            CMR relationship;
            if (sdOutcome.IsIdentical || sdOutcome.IsEquivalent)
            {
                relationship = CMR.Equivalent;
            }
            else if (sdOutcome.IsBroaderThanTarget)
            {
                relationship = CMR.SourceIsBroaderThanTarget;
            }
            else if (sdOutcome.IsNarrowerThanTarget)
            {
                relationship = CMR.SourceIsNarrowerThanTarget;
            }
            else
            {
                relationship = CMR.RelatedTo;
            }

            // create our target element
            ConceptMap.TargetElementComponent targetElement = new()
            {
                Code = sdOutcome.TargetId ?? "Basic",
                Display = sdOutcome.TargetName ?? "Basic",
                Relationship = relationship,
                Comment = sdOutcome.Comments,
            };

            currentSourceElement.Target.Add(targetElement);
        }

        // write the resource map to a file
        string filename;
        if (cm.Id.StartsWith("ConceptMap", StringComparison.OrdinalIgnoreCase))
        {
            filename = cm.Id;
        }
        else if (cm.Id.StartsWith("Map", StringComparison.OrdinalIgnoreCase))
        {
            filename = "Concept" + cm.Id;
        }
        else
        {
            filename = cm.Id;
        }
        string path = Path.Combine(dir, filename + ".json");
        if (exporterR3 is not null)
        {
            File.WriteAllText(path, exporterR3.ToJson(cm, new SerializerSettings() { Pretty = true }));
        }
        else if (exporterR4 is not null)
        {
            File.WriteAllText(path, exporterR4.ToJson(cm, new SerializerSettings() { Pretty = true }));
        }
        else
        {
            File.WriteAllText(path, cm.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
        }
        exported.Add(new()
        {
            FileName = filename + ".json",
            FileNameWithoutExtension = filename,
            IsPageContentFile = false,
            Name = cm.Name,
            Id = cm.Id,
            Url = cm.Url,
            ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ConceptMap.GetLiteral(),
            Version = cm.Version,
            Description = cm.Description ?? cm.Title ?? $"ConceptMap: {cm.Url}",
        });

        _logger.LogInformation($"Wrote {exported.Count} resource maps for `{igTr.PackageId}`");
        igTr.ResourceMapFiles = exported;
    }

    private ConceptMap createResourceConceptMap(
        XVerIgExportTrackingRecord igTr)
    {
        string id = $"{igTr.PackagePair.SourcePackageShortName}-resource-map-to-{igTr.PackagePair.TargetPackageShortName}";
        string name =
            $"{igTr.PackagePair.SourcePackageShortName.ToPascalCase()}" +
            $"ResourceMapTo" +
            $"{igTr.PackagePair.TargetPackageShortName.ToPascalCase()}";

        (_, name) = igTr.GetName(name, id);

        ConceptMap vsCm = new()
        {
            Id = id,
            Url = $"{XVerProcessor._canonicalRootCrossVersion}ConceptMap/{id}",
            Name = name,
            Version = _exporter._crossDefinitionVersion,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version ConceptMap for FHIR {igTr.PackagePair.SourceFhirSequence} resources in FHIR {igTr.PackagePair.TargetFhirSequence}",
            Description = $"This ConceptMap represents the cross-version mapping of resource FHIR {igTr.PackagePair.SourceFhirSequence} for use in FHIR {igTr.PackagePair.TargetFhirSequence}.",
            Status = PublicationStatus.Active,
            Experimental = false,
            SourceScope = new FhirUri($"http://hl7.org/fhir/{igTr.PackagePair.SourceFhirVersionShort}/ValueSet/resource-types"),
            TargetScope = new FhirUri($"http://hl7.org/fhir/{igTr.PackagePair.TargetFhirVersionShort}/ValueSet/resource-types"),
            Group = [
                new()
                {
                    Source = $"http://hl7.org/fhir/{igTr.PackagePair.SourceFhirVersionShort}/resource-types",
                    Target = $"http://hl7.org/fhir/{igTr.PackagePair.TargetFhirVersionShort}/resource-types",
                    Element = [],
                }
            ],
        };

        return vsCm;
    }


    private void exportProfiles(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.StructureDefinitionToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        if (igTr.ProfileDir is null)
        {
            throw new Exception("ProfileDir is null");
        }

        string dir = igTr.ProfileDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing Profiles for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the structures defined in the source package (Resources + ComplexTypes)
        List<DbStructureDefinition> sourceStructures = [];
        foreach (FhirArtifactClassEnum artifactClass in _xverSourceArtifactClasses)
        {
            sourceStructures.AddRange(DbStructureDefinition.SelectList(
                _db,
                FhirPackageKey: igTr.PackagePair.SourcePackageKey,
                ArtifactClass: artifactClass));
        }

        // iterate over all structures - each will get a profile
        foreach (DbStructureDefinition sourceSd in sourceStructures)
        {
            // skip abstract bases that don't get profiled
            if (StructurePageExporter.StructureExportExclusions.Contains(sourceSd.Id) ||
                StructurePageExporter.StructureExportExclusions.Contains(sourceSd.Name))
            {
                continue;
            }

            // get the structure outcomes for this source structure
            List<DbStructureOutcome> sdOutcomes = DbStructureOutcome.SelectList(
                _db,
                SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                SourceStructureKey: sourceSd.Key);

            // iterate over the outcomes and create profiles for each target
            foreach (DbStructureOutcome sdOutcome in sdOutcomes)
            {
                // resolve the target structure (if any)
                DbStructureDefinition? targetSd = sdOutcome.TargetStructureKey is null
                    ? null
                    : DbStructureDefinition.SelectSingle(
                        _db,
                        FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                        Key: sdOutcome.TargetStructureKey.Value);

                // build the initial structure definition for the extension
                StructureDefinition profileSd = createProfileSd(
                    igTr,
                    sourceSd,
                    targetSd,
                    sdOutcome);

                //if ((targetSd is null) ||
                //    ((targetSd.Name == "Basic") && (sourceSd.Name != "Basic")))
                //{
                //    DbElementOutcome? rootElementOutcome = DbElementOutcome.SelectSingle(
                //            _db,
                //            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                //            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                //            SourceStructureKey: sdOutcome.SourceStructureKey,
                //            SourceResourceOrder: 0);

                //    if (rootElementOutcome is null)
                //    {
                //        throw new Exception($"First element outcome for source structure `{sourceSd.Name}` in package pair `{igTr.PackageId}` is not the root element");
                //    }

                //    // if this is a basic resource profile, it only needs the root extension
                //    addContentForBasicProfile(
                //        igTr,
                //        sourceSd,
                //        sdOutcome,
                //        rootElementOutcome,
                //        profileSd);
                //}
                //else
                //{
                    // add the content for a mapped resource
                    addContentForMappedProfile(
                        igTr,
                        sourceSd,
                        targetSd,
                        sdOutcome,
                        profileSd);
                //}

                // write the profile to a file
                string filename = sdOutcome.GenFileName ?? throw new ArgumentNullException(nameof(sdOutcome.GenFileName));
                string path = Path.Combine(dir, filename + ".json");

                if (exporterR3 is not null)
                {
                    File.WriteAllText(path, exporterR3.ToJson(profileSd, new SerializerSettings() { Pretty = true }));
                }
                else
                {
                    File.WriteAllText(path, profileSd.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
                }

                exported.Add(new()
                {
                    FileName = filename + ".json",
                    FileNameWithoutExtension = filename,
                    IsPageContentFile = false,
                    Name = profileSd.Name,
                    Id = profileSd.Id,
                    Url = profileSd.Url,
                    ResourceType = Hl7.Fhir.Model.FHIRAllTypes.StructureDefinition.GetLiteral(),
                    Version = profileSd.Version,
                    Description = profileSd.Description ?? profileSd.Title ?? $"Profile: {profileSd.Url}",
                });
            }
        }

        _logger.LogInformation($"Wrote {exported.Count} Profiles for `{igTr.PackageId}`");
        igTr.ProfileFiles = exported;
    }

    private void addContentForMappedProfile(
        XVerIgExportTrackingRecord igTr,
        DbStructureDefinition sourceSd,
        DbStructureDefinition? targetSd,
        DbStructureOutcome sdOutcome,
        StructureDefinition profileSd)
    {
        // get the element outcomes that require a definition and are not part of one already
        Dictionary<int, DbElementOutcome> edOutcomes = DbElementOutcome.SelectDict(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            SourceStructureKey: sdOutcome.SourceStructureKey);

        // build a lookup based on context paths
        ILookup<string, DbElementOutcome> edOutcomeContextLookup = edOutcomes.Values
            .SelectMany(edo => edo.ExtensionContexts, (edo, context) => new { Context = context, Outcome = edo })
            .ToLookup(x => x.Context, x => x.Outcome);

        // we need to traverse the elements in the order of the target structure
        List<DbElement> targetElements = targetSd is null
            ? DbElement.SelectList(
                _db,
                FhirPackageKey: igTr.PackagePair.TargetPackageKey,
                Id: "Basic",
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)])
            : DbElement.SelectList(
                _db,
                StructureKey: targetSd.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

        if (targetElements.Count == 0)
        {
            throw new Exception($"Resource with no elements!");
        }

        DbElement targetRootEd = targetElements[0];
        ILookup<string, DbElement> targetIdLookup = targetElements.ToLookup(ed => ed.Id);

        // iterate over the elements and add to the differential as necessary
        foreach (DbElement targetEd in targetElements)
        {
            // check to see if we are in the 'Basic.code' target element, which we need to profile
            if ((targetEd.Id == "Basic.code") && (sourceSd.Name != "Basic"))
            {
                ElementDefinition basicCodeEd = new ElementDefinition()
                {
                    ElementId = "Basic.code",
                    Path = "Basic.code",
                    Pattern = new CodeableConcept("http://hl7.org/fhir/fhir-types", sourceSd.Id),
                    Base = new ElementDefinition.BaseComponent()
                    {
                        Path = "Basic.code",
                        Min = 1,
                        Max = "*",
                    }
                };

                // nothing else to do on code in this case
                continue;
            }

            // skip any elements that do not have something to add
            if (!edOutcomeContextLookup.Contains(targetEd.Id))
            {
                continue;
            }

            List<DbElementOutcome> targetEdOutcomes = edOutcomeContextLookup[targetEd.Id]
                .Where(eo =>
                    (eo.ExtensionDefinitionIsProhibited != true) &&
                    (eo.RequiresXVerDefinition ||
                    (eo.ExtensionSubstitutionUrl is not null) ||
                    (eo.AlternateCanonicalTargetsLiteral is not null) ||
                    (eo.AlternateReferenceTargetsLiteral is not null)))
                .OrderBy(eo => eo.SourceResourceOrder)
                .ToList();

            // if there are none, go to the next element
            if (targetEdOutcomes.Count == 0)
            {
                continue;
            }

            List<DbElementOutcome> extSubReview = targetEdOutcomes.Where(eo =>
                (eo.ExtensionSubstitutionUrl is not null) &&
                !eo.RequiresExtensionDefinition &&
                (eo.ParentElementOutcomeKey is not null))
                .ToList();

            // check to see if there is a parent outcome that is generating an extension
            foreach (DbElementOutcome reviewOutcome in extSubReview)
            {
                if ((reviewOutcome.ParentElementOutcomeKey is null) ||
                    !edOutcomes.TryGetValue(reviewOutcome.ParentElementOutcomeKey.Value, out DbElementOutcome? rpOutcome))
                {
                    continue;
                }

                if (rpOutcome.RequiresExtensionDefinition ||
                    rpOutcome.RequiresExtensionDefinition ||
                    rpOutcome.RequiresSliceDefinition ||
                    (rpOutcome.ContentReferenceRequiresXVerDefinition == true))
                {
                    targetEdOutcomes.Remove(reviewOutcome);
                }
            }

            // if there are no more targets, go to the next element
            if (targetEdOutcomes.Count == 0)
            {
                continue;
            }

            // build our target information
            string targetId = targetEd.Id + ".extension";
            string targetPath = targetEd.Path + ".extension";
            DbElement actualTargetEd = targetEd;

            // check to see if we need to swap targets
            if (targetIdLookup.Contains(targetId))
            {
                actualTargetEd = targetIdLookup[targetId].First();
            }

            // add the base for this element we need for slicing
            ElementDefinition targetSlicingEd = new()
            {
                ElementId = targetId,
                Path = targetPath,
                Slicing = new()
                {
                    Discriminator = [
                        new ElementDefinition.DiscriminatorComponent()
                        {
                            Type = ElementDefinition.DiscriminatorType.Value,
                            Path = "url",
                        }
                    ],
                    Ordered = false,
                    Rules = ElementDefinition.SlicingRules.Open,
                },
                Min = 0,
                Max = "*",
            };
            bool addedTargetSlicingEd = false;

            ElementDefinition? targetModifierSlicingEd = null;
            string targetModifierId = targetEd.Id + ".modifierExtension";
            string targetModifierPath = targetEd.Path + ".modifierExtension";
            if (targetEdOutcomes.Any(edo => edo.DefineAsModifier == true))
            {
                targetModifierSlicingEd = new()
                {
                    ElementId = targetModifierId,
                    Path = targetModifierPath,
                    Slicing = new()
                    {
                        Discriminator = [
                            new ElementDefinition.DiscriminatorComponent()
                            {
                                Type = ElementDefinition.DiscriminatorType.Value,
                                Path = "url",
                            }
                        ],
                        Ordered = false,
                        Rules = ElementDefinition.SlicingRules.Open,
                    },
                    Min = 0,
                    Max = "*",
                };
            }
            bool addedTargetModifierSlicingEd = false;

            // add each outcome that we should have here
            foreach (DbElementOutcome targetEdOutcome in targetEdOutcomes.OrderBy(edo => edo.DefineAsModifier))
            {
                bool isAlternateCanonical = targetEdOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateCanonical;
                bool isAlternateReference = targetEdOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateReference;

                bool additionalAlternateCanonical = !isAlternateCanonical &&
                    (targetEdOutcome.AlternateCanonicalTargetsLiteral is not null);

                bool additionalAlternateReference = !isAlternateReference &&
                    (targetEdOutcome.AlternateReferenceTargetsLiteral is not null);

                int adjustedMin = (targetEdOutcome.SourceMinCardinality == 0) || additionalAlternateCanonical || additionalAlternateReference
                    ? 0
                    : targetEdOutcome.SourceMinCardinality;

                // if we think it is required, check for targeting required elements already
                if ((adjustedMin > 0) &&
                    (targetEdOutcome.TotalTargetCount > 0))
                {
                    // resolve the outcome targets
                    List<DbElementOutcomeTarget> outcomeTargets = DbElementOutcomeTarget.SelectList(
                        _db,
                        ElementOutcomeKey: targetEdOutcome.Key);

                    if (outcomeTargets.Any(eot => eot.TargetMinCardinality > 0))
                    {
                        adjustedMin = 0;
                    }
                }

                //string path = targetPath;
                string? edShort = targetEdOutcome.GenArtifactShort ?? $"Cross-version extension for {targetEdOutcome.SourceId} from {igTr.PackagePair.SourceFhirSequence} for use in FHIR {igTr.PackagePair.TargetFhirSequence}";
                string? edDefinition = targetEdOutcome.GenArtifactDescription;
                string? edComment = targetEdOutcome.GenArtifactComment ?? targetEdOutcome.Comments;

                string idPrefix = $"{targetId}:{targetEdOutcome.SourceNameClean()}";
                string modifierIdPrefix = $"{targetModifierId}:{targetEdOutcome.SourceNameClean()}";
                string sliceName = targetEdOutcome.SourceNameClean();

                bool addedSlice = false;
                bool addedModifierSlice = false;

                // check for extension
                if (targetEdOutcome.RequiresExtensionDefinition)
                {
                    if (targetEdOutcome.DefineAsModifier)
                    {
                        string sliceSuffix = addedModifierSlice
                            ? "Ext"
                            : string.Empty;
                        addedModifierSlice = true;

                        if (!addedTargetModifierSlicingEd)
                        {
                            profileSd.Differential.Element.Add(targetModifierSlicingEd);
                            addedTargetModifierSlicingEd = true;
                        }

                        profileSd.Differential.Element.Add(getEd(
                            targetEdOutcome.GenUrl!,
                            modifierIdPrefix + sliceSuffix,
                            sliceName + sliceSuffix,
                            targetModifierPath,
                            edShort,
                            edDefinition,
                            edComment,
                            adjustedMin,
                            targetEdOutcome.SourceMaxCardinalityString));
                    }
                    else
                    {
                        string sliceSuffix = addedSlice
                            ? "Ext"
                            : string.Empty;
                        addedSlice = true;

                        if (!addedTargetSlicingEd)
                        {
                            profileSd.Differential.Element.Add(targetSlicingEd);
                            addedTargetSlicingEd = true;
                        }

                        profileSd.Differential.Element.Add(getEd(
                            targetEdOutcome.GenUrl!,
                            idPrefix + sliceSuffix,
                            sliceName + sliceSuffix,
                            targetPath,
                            edShort,
                            edDefinition,
                            edComment,
                            adjustedMin,
                            targetEdOutcome.SourceMaxCardinalityString));
                    }
                }

                // check for content reference link
                if ((targetEdOutcome.RequiresDefinitionAsContentReference == true) &&
                    (targetEdOutcome.ContentReferenceExtensionUrl is not null))
                {
                    string sliceSuffix = addedSlice
                        ? FhirSanitizationUtils.SanitizeForProperty(targetEdOutcome.ContentReferenceExtensionUrl.Split('/')[^1])
                        : string.Empty;
                    addedSlice = true;

                    if (!addedTargetSlicingEd)
                    {
                        profileSd.Differential.Element.Add(targetSlicingEd);
                        addedTargetSlicingEd = true;
                    }

                    profileSd.Differential.Element.Add(getEd(
                        targetEdOutcome.ContentReferenceExtensionUrl,
                        idPrefix + sliceSuffix,
                        sliceName + sliceSuffix,
                        targetPath,
                        edShort,
                        edDefinition,
                        edComment,
                        adjustedMin,
                        targetEdOutcome.SourceMaxCardinalityString));
                }

                // check for substitution
                if (targetEdOutcome.ExtensionSubstitutionUrl is not null)
                {
                    bool isAltCanonical = targetEdOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateCanonical;
                    bool isAltReference = targetEdOutcome.ExtensionSubstitutionUrl == CommonDefinitions.ExtUrlAlternateReference;

                    if (targetEd.IsChoiceType && isAltCanonical)
                    {
                        string acTargetId = targetEd.Id + ":valueCanonical.extension";
                        string acTargetPath = targetEd.Path[..^3] + "Canonical.extension";

                        // add the base for this element we need for slicing
                        ElementDefinition acContextSlicingEd = new()
                        {
                            ElementId = acTargetId,
                            Path = acTargetPath,
                            Slicing = new()
                            {
                                Discriminator = [
                                    new ElementDefinition.DiscriminatorComponent()
                                {
                                    Type = ElementDefinition.DiscriminatorType.Value,
                                    Path = "url",
                                }
                                ],
                                Ordered = false,
                                Rules = ElementDefinition.SlicingRules.Open,
                            },
                            Min = 0,
                            Max = "*",
                        };

                        profileSd.Differential.Element.Add(acContextSlicingEd);

                        profileSd.Differential.Element.Add(getEd(
                            CommonDefinitions.ExtUrlAlternateCanonical,
                            acTargetId + ":alternateCanonical",
                            "alternateCanonical",
                            acTargetPath,
                            edShort,
                            edDefinition,
                            edComment));
                    }
                    else if (targetEd.IsChoiceType && isAltReference)
                    {
                        string arTargetId = targetEd.Id + ":valueReference.extension";
                        string arTargetPath = targetEd.Path[..^3] + "Reference.extension";

                        // add the base for this element we need for slicing
                        ElementDefinition acContextSlicingEd = new()
                        {
                            ElementId = arTargetId,
                            Path = arTargetPath,
                            Slicing = new()
                            {
                                Discriminator = [
                                    new ElementDefinition.DiscriminatorComponent()
                                {
                                    Type = ElementDefinition.DiscriminatorType.Value,
                                    Path = "url",
                                }
                                ],
                                Ordered = false,
                                Rules = ElementDefinition.SlicingRules.Open,
                            },
                            Min = 0,
                            Max = "*",
                        };

                        profileSd.Differential.Element.Add(acContextSlicingEd);

                        profileSd.Differential.Element.Add(getEd(
                            CommonDefinitions.ExtUrlAlternateReference,
                            arTargetId + ":alternateReference",
                            "alternateReference",
                            arTargetPath,
                            edShort,
                            edDefinition,
                            edComment));
                    }
                    else if (targetEdOutcome.DefineAsModifier)
                    {
                        string sliceSuffix = addedModifierSlice
                            ? FhirSanitizationUtils.SanitizeForProperty(targetEdOutcome.ExtensionSubstitutionUrl.Split('/')[^1])
                            : string.Empty;
                        addedModifierSlice = true;

                        if (!addedTargetModifierSlicingEd)
                        {
                            profileSd.Differential.Element.Add(targetModifierSlicingEd);
                            addedTargetModifierSlicingEd = true;
                        }

                        profileSd.Differential.Element.Add(getEd(
                            targetEdOutcome.ExtensionSubstitutionUrl,
                            modifierIdPrefix + sliceSuffix,
                            sliceName + sliceSuffix,
                            targetModifierPath,
                            edShort,
                            edDefinition,
                            edComment,
                            adjustedMin,
                            targetEdOutcome.SourceMaxCardinalityString));
                    }
                    else
                    {
                        string sliceSuffix = addedSlice
                            ? FhirSanitizationUtils.SanitizeForProperty(targetEdOutcome.ExtensionSubstitutionUrl.Split('/')[^1])
                            : string.Empty;
                        addedSlice = true;

                        if (!addedTargetSlicingEd)
                        {
                            profileSd.Differential.Element.Add(targetSlicingEd);
                            addedTargetSlicingEd = true;
                        }

                        profileSd.Differential.Element.Add(getEd(
                            targetEdOutcome.ExtensionSubstitutionUrl,
                            idPrefix + sliceSuffix,
                            sliceName + sliceSuffix,
                            targetPath,
                            edShort,
                            edDefinition,
                            edComment,
                            adjustedMin,
                            targetEdOutcome.SourceMaxCardinalityString));
                    }
                }

                // check for additional alternate canonical on root
                if (additionalAlternateCanonical && (targetEdOutcome.AlternateCanonicalContextLiteral is null))
                {
                    string sliceSuffix = (addedSlice || additionalAlternateReference)
                        ? "AlternateCanonical"
                        : string.Empty;
                    addedSlice = true;

                    if (!addedTargetSlicingEd)
                    {
                        profileSd.Differential.Element.Add(targetSlicingEd);
                        addedTargetSlicingEd = true;
                    }

                    profileSd.Differential.Element.Add(getEd(
                        CommonDefinitions.ExtUrlAlternateCanonical,
                        idPrefix + sliceSuffix,
                        sliceName + sliceSuffix,
                        targetPath,
                        edShort,
                        edDefinition,
                        edComment,
                        adjustedMin,
                        targetEdOutcome.SourceMaxCardinalityString));
                }

                // check for additional alternate reference on root
                if (additionalAlternateReference && (targetEdOutcome.AlternateReferenceContextLiteral is null))
                {
                    string sliceSuffix = (addedSlice || additionalAlternateCanonical)
                        ? "AlternateReference"
                        : string.Empty;
                    addedSlice = true;

                    if (!addedTargetSlicingEd)
                    {
                        profileSd.Differential.Element.Add(targetSlicingEd);
                        addedTargetSlicingEd = true;
                    }

                    profileSd.Differential.Element.Add(getEd(
                        CommonDefinitions.ExtUrlAlternateReference,
                        idPrefix + sliceSuffix,
                        sliceName + sliceSuffix,
                        targetPath,
                        edShort,
                        edDefinition,
                        edComment,
                        adjustedMin,
                        targetEdOutcome.SourceMaxCardinalityString));
                }

                if (addedSlice && (adjustedMin > 0))
                {
                    targetSlicingEd.Min += adjustedMin;
                }

                // check for additional alternate canonical NOT on root
                if (additionalAlternateCanonical &&
                    (targetEdOutcome.AlternateCanonicalContextLiteral is not null) &&
                    (targetIdLookup[targetEdOutcome.AlternateCanonicalContextLiteral].FirstOrDefault() is DbElement acTargetEd))
                {
                    string acTargetId = acTargetEd.Id;
                    string acTargetPath = acTargetEd.Path;

                    // check to see if we need to introduce type slicing first
                    if (acTargetEd.IsChoiceType)
                    {
                        acTargetId += ":valueCanonical";
                        acTargetPath = acTargetPath[..^3] + "Canonical";
                    }

                    acTargetId += ".extension";
                    acTargetPath += ".extension";

                    // add the base for this element we need for slicing
                    ElementDefinition acContextSlicingEd = new()
                    {
                        ElementId = acTargetId,
                        Path = acTargetPath,
                        Slicing = new()
                        {
                            Discriminator = [
                                new ElementDefinition.DiscriminatorComponent()
                                {
                                    Type = ElementDefinition.DiscriminatorType.Value,
                                    Path = "url",
                                }
                            ],
                            Ordered = false,
                            Rules = ElementDefinition.SlicingRules.Open,
                        },
                        Min = 0,
                        Max = "*",
                    };

                    profileSd.Differential.Element.Add(acContextSlicingEd);

                    profileSd.Differential.Element.Add(getEd(
                        CommonDefinitions.ExtUrlAlternateCanonical,
                        acTargetId + ":alternateCanonical",
                        "alternateCanonical",
                        acTargetPath,
                        edShort,
                        edDefinition,
                        edComment));
                }

                // check for additional alternate reference NOT on root
                if (additionalAlternateReference &&
                    (targetEdOutcome.AlternateReferenceContextLiteral is not null) &&
                    (targetIdLookup[targetEdOutcome.AlternateReferenceContextLiteral].FirstOrDefault() is DbElement arTargetEd))
                {
                    string arTargetId = arTargetEd.Id;
                    string arTargetPath = arTargetEd.Path;

                    // check to see if we need to introduce type slicing first
                    if (arTargetEd.IsChoiceType)
                    {
                        arTargetId += ":valueReference";
                        arTargetPath = arTargetPath[..^3] + "Reference";
                    }

                    arTargetId += ".extension";
                    arTargetPath += ".extension";

                    // add the base for this element we need for slicing
                    ElementDefinition acContextSlicingEd = new()
                    {
                        ElementId = arTargetId,
                        Path = arTargetPath,
                        Slicing = new()
                        {
                            Discriminator = [
                                new ElementDefinition.DiscriminatorComponent()
                                {
                                    Type = ElementDefinition.DiscriminatorType.Value,
                                    Path = "url",
                                }
                            ],
                            Ordered = false,
                            Rules = ElementDefinition.SlicingRules.Open,
                        },
                        Min = 0,
                        Max = "*",
                    };

                    profileSd.Differential.Element.Add(getEd(
                        CommonDefinitions.ExtUrlAlternateReference,
                        arTargetId + ":alternateReference",
                        "alternateReference",
                        arTargetPath,
                        edShort,
                        edDefinition,
                        edComment));
                }
            }
        }
    }

    private ElementDefinition getEd(
        string url,
        string id,
        string sliceName,
        string path,
        string? edShort,
        string? edDefinition,
        string? edComment,
        int? cardMin = null,
        string? cardMax = null)
    {
        return new()
        {
            ElementId = id,
            SliceName = sliceName,
            Path = path,
            Short = edShort,
            Definition = edDefinition,
            Comment = edComment,
            Min = cardMin,
            Max = cardMax,
            Base = new ElementDefinition.BaseComponent()
            {
                Path = "DomainResource.extension",
                Min = 0,
                Max = "*",
            },
            Type = [
                new ElementDefinition.TypeRefComponent()
                {
                    Code = "Extension",
                    Profile = [ url ],
                },
            ],
        };
    }

    private void addContentForBasicProfile(
        XVerIgExportTrackingRecord igTr,
        DbStructureDefinition sourceSd,
        DbStructureOutcome sdOutcome,
        DbElementOutcome rootEdOutcome,
        StructureDefinition profileSd)
    {
        string primaryExtLiteral = rootEdOutcome.DefineAsModifier
            ? "modifierExtension"
            : "extension";

        profileSd.Differential.Element = [
            new ElementDefinition()
            {
                ElementId = $"Basic.{primaryExtLiteral}",
                Path = $"Basic.{primaryExtLiteral}",
                Slicing = new()
                {
                    Discriminator = [
                        new ElementDefinition.DiscriminatorComponent()
                        {
                            Type = ElementDefinition.DiscriminatorType.Value,
                            Path = "url",
                        }
                    ],
                    Ordered = false,
                    Rules = ElementDefinition.SlicingRules.Open,
                },
                //Base = new ElementDefinition.BaseComponent()
                //{
                //    Path = "DomainResource.extension",
                //    Min = 0,
                //    Max = "*",
                //},
                Min = 1,
                Max = "*",
            },
            new ElementDefinition()
            {
                ElementId = $"Basic.{primaryExtLiteral}:{sourceSd.Id}",
                SliceName = sourceSd.Id,
                Path = $"Basic.{primaryExtLiteral}",
                Short = rootEdOutcome.GenArtifactShort ?? $"Cross-version extension for {sourceSd.Name} from {igTr.PackagePair.SourceFhirSequence} for use in FHIR {igTr.PackagePair.TargetFhirSequence}",
                Definition = rootEdOutcome.GenArtifactDescription,
                Comment = rootEdOutcome.GenArtifactComment,
                Min = 1,
                Max = "1",
                Base = new ElementDefinition.BaseComponent()
                {
                    Path = "DomainResource.extension",
                    Min = 0,
                    Max = "*",
                },
                Type = [
                    new ElementDefinition.TypeRefComponent()
                    {
                        Code = "Extension",
                        Profile = [ rootEdOutcome.GenUrl ],
                    },
                ],
            },
            new ElementDefinition()
            {
                ElementId = "Basic.code",
                Path = "Basic.code",
                Pattern = new CodeableConcept("http://hl7.org/fhir/fhir-types", sourceSd.Id),
                Base = new ElementDefinition.BaseComponent()
                {
                    Path = "Basic.code",
                    Min = 1,
                    Max = "*",
                }
            },
        ];
    }

    private StructureDefinition createProfileSd(
        XVerIgExportTrackingRecord igTr,
        DbStructureDefinition sourceSd,
        DbStructureDefinition? targetSd,
        DbStructureOutcome sdOutcome)
    {
        string targetStructureName = targetSd?.Name ?? "Basic";
        string profileId = sdOutcome.GenShortId!;

        (_, string name) = igTr.GetName(sdOutcome.GenName!, profileId);

        StructureDefinition profileSd = new()
        {
            Id = profileId,
            Url = sdOutcome.GenUrl,
            Name = name,
            Version = _exporter._crossDefinitionVersion,
            FhirVersion = EnumUtility.ParseLiteral<FHIRVersion>(igTr.PackagePair.TargetPackage.PackageVersion) ?? FHIRVersion.N5_0_0,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version Profile for {igTr.PackagePair.SourceFhirSequence}.{sourceSd.Name} for use in FHIR {igTr.PackagePair.TargetFhirSequence}",
            Description = $"This cross-version profile allows" +
                $" {igTr.PackagePair.SourceFhirSequence} {sourceSd.Name} content to be represented" +
                $" via FHIR {igTr.PackagePair.TargetFhirSequence} {targetStructureName} resources.",
            Status = PublicationStatus.Active,
            Experimental = false,
            Kind = StructureDefinition.StructureDefinitionKind.Resource,
            Abstract = false,
            Type = targetStructureName,
            BaseDefinition = $"http://hl7.org/fhir/StructureDefinition/{targetStructureName}",
            Derivation = StructureDefinition.TypeDerivationRule.Constraint,
            Differential = [],
        };

        string wg = CommonDefinitions.ResolveWorkgroup("fhir");

        // add the work group extension
        profileSd.AddExtension(CommonDefinitions.ExtUrlWorkGroup, new Hl7.Fhir.Model.Code(wg));

        // ensure there is a publisher, use the WG if there is none
        profileSd.Publisher = CommonDefinitions.WorkgroupNames[wg];

        // ensure there is a contact point - use the default WG unless there are multiple entries
        if ((profileSd.Contact == null) || (profileSd.Contact.Count < 2))
        {
            profileSd.Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames[wg],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls[wg],
                        },
                    ],
                }
            ];
        }

        profileSd.cgAddPackageSource(igTr.PackageId, _exporter._crossDefinitionVersion, null);

        // NOTE: version specific extensions are not needed for profiles since they are based on specific values of resources anyway

        return profileSd;
    }


    private void exportExtensions(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.StructureDefinitionToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        if (igTr.ExtensionDir is null)
        {
            throw new Exception("ExtensionDir is null");
        }

        string dir = igTr.ExtensionDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing extensions for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the structures defined in the source package
        Dictionary<int, DbStructureDefinition> sourceSds = DbStructureDefinition.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // get the elements defined in the source package
        Dictionary<int, DbElement> sourceEds = DbElement.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // get the structures defined in the target package
        Dictionary<int, DbStructureDefinition> targetSds = DbStructureDefinition.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.TargetPackageKey);

        // get the elements defined in the target package
        Dictionary<int, DbElement> targetEds = DbElement.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.TargetPackageKey);

        // get the element comparisons for the package pair
        Dictionary<int, DbElementComparison> edComparisons = DbElementComparison.SelectDict(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey);

        Dictionary<int, string> contentReferenceExtUrlsByEdKey = findContentReferenceUrls(
            igTr.PackagePair.SourcePackageKey,
            igTr.PackagePair.TargetPackageKey);

        HashSet<string> generatedExtensionIds = [];

        // get the element outcomes for this pair
        List<DbElementOutcome> edOutcomes = DbElementOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            RequiresExtensionDefinition: true,
            orderByProperties: [nameof(DbElementOutcome.SourceStructureKey), nameof(DbElementOutcome.SourceResourceOrder)]);

        // iterate over the outcomes that need exporting
        foreach (DbElementOutcome edOutcome in edOutcomes)
        {
            // get the source structure
            if (!sourceSds.TryGetValue(edOutcome.SourceStructureKey, out DbStructureDefinition? sourceSd))
            {
                _logger.LogError($"Source structure with key `{edOutcome.SourceStructureKey}` not found for element outcome with key `{edOutcome.Key}`");
                continue;
            }

            if (StructurePageExporter.StructureExportExclusions.Contains(edOutcome.SourceId) ||
                StructurePageExporter.StructureExportExclusions.Contains(sourceSd.Name))
            {
                continue;
            }

            // get the source element
            if (!sourceEds.TryGetValue(edOutcome.SourceElementKey, out DbElement? sourceEd))
            {
                _logger.LogError($"Source element with key `{edOutcome.SourceElementKey}` not found for element outcome with key `{edOutcome.Key}`");
                continue;

            }

            if (skipElement(sourceEd, skipFirstElement: false))
            {
                continue;
            }

            if (edOutcome.ExtensionDefinitionIsProhibited)
            {
                continue;
            }

            List<StructureDefinition.ContextComponent> contexts = edOutcome.ExtensionContexts
                .Select(c => new StructureDefinition.ContextComponent()
                {
                    Type = StructureDefinition.ExtensionContextType.Element,
                    Expression = c,
                })
                .ToList();

            string purpose = buildPurpose(
                igTr,
                edComparisons,
                sourceEd,
                edOutcome);

            StructureDefinition? extSd = buildExtSd(
                generatedExtensionIds,
                igTr,
                sourceEds,
                targetEds,
                edComparisons,
                contentReferenceExtUrlsByEdKey,
                edOutcome,
                sourceSd,
                sourceEd,
                purpose,
                contexts,
                useComponentDefinition: false);

            if (extSd is null)
            {
                continue;
            }

            // write the extension to a file
            string filename = edOutcome.GenFileName ?? throw new ArgumentNullException(nameof(edOutcome.GenFileName));
            string path = Path.Combine(dir, filename + ".json");

            if (exporterR3 is not null)
            {
                File.WriteAllText(path, exporterR3.ToJson(extSd, new SerializerSettings() { Pretty = true }));
            }
            else
            {
                File.WriteAllText(path, extSd.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
            }

            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = extSd.Name,
                Id = extSd.Id,
                Url = extSd.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.StructureDefinition.GetLiteral(),
                Version = extSd.Version,
                Description = extSd.Description ?? extSd.Title ?? $"Extension: {extSd.Url}",
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} Extensions for `{igTr.PackageId}`");
        igTr.ExtensionFiles = exported;
    }

    private Dictionary<int, string> findContentReferenceUrls(
        int sourceFhirPackageKey,
        int targetFhirPackageKey)
    {
        Dictionary<int, string> dict = [];

        List<DbElementOutcome> xverCrEdOutcomes = DbElementOutcome.SelectList(
            _db,
            SourceFhirPackageKey: sourceFhirPackageKey,
            TargetFhirPackageKey: targetFhirPackageKey,
            RequiresXVerDefinition: true,
            SourceUsedAsContentReference: true);
        foreach (DbElementOutcome eo in xverCrEdOutcomes)
        {
            if (eo.GenUrl is not null)
            {
                dict[eo.SourceElementKey] = eo.GenUrl;
            }
        }

        //List<DbElementOutcome> componentCrEdOutcomes = DbElementOutcome.SelectList(
        //    _db,
        //    SourceFhirPackageKey: sourceFhirPackageKey,
        //    TargetFhirPackageKey: targetFhirPackageKey,
        //    RequiresComponentDefinition: true,
        //    RequiresXVerDefinition: false,
        //    SourceUsedAsContentReference: true);
        //foreach (DbElementOutcome ceo in componentCrEdOutcomes)
        //{
        //    if (ceo.ComponentGenUrl is not null)
        //    {
        //        dict[ceo.SourceElementKey] = ceo.ComponentGenUrl;
        //    }
        //}

        return dict;
    }

    private string buildPurpose(
        XVerIgExportTrackingRecord igTr,
        Dictionary<int, DbElementComparison> elementComparisons,
        DbElement sourceEd,
        DbElementOutcome elementOutcome)
    {
        if (igTr.PackagePair.Distance == 1)
        {
            return $$$"""
                    This extension is part of the cross-version definitions generated to enable use of the
                    element `{{{sourceEd.Id}}}` as defined in FHIR {{{igTr.PackagePair.SourceFhirSequence}}}
                    in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}.

                    The source element is defined as:
                    `{{{sourceEd.Id}}}` {{{sourceEd.FhirCardinalityString}}} `{{{sourceEd.FullCollatedTypeLiteral}}}`

                    Following are the generation technical comments:
                    {{{elementOutcome.Comments}}}
                    """;
        }

        string? mappingTrace = null;
        List<string> mappingTraceLines = [];

        List<DbElementOutcomeTarget> eots = DbElementOutcomeTarget.SelectList(
            _db,
            ElementOutcomeKey: elementOutcome.Key);

        List<int> elementComparisonKeys = eots
            .Where(eot => eot.ElementComparisonKey is not null)
            .Select(eot => eot.ElementComparisonKey!.Value)
            .ToList();

        List<DbElementComparison> possibleComparisons = elementComparisonKeys.Count == 0
            ? []
            : DbElementComparison.SelectList(
                _db,
                KeyValues: elementComparisonKeys);

        foreach (DbElementComparison edComp in possibleComparisons)
        {
            DbElement? r2Ed = edComp.ContentKeyR2 is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR2.Value);
            DbElement? r3Ed = edComp.ContentKeyR3 is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR3.Value);
            DbElement? r4Ed = edComp.ContentKeyR4 is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR4.Value);
            DbElement? r4bEd = edComp.ContentKeyR4B is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR4B.Value);
            DbElement? r5Ed = edComp.ContentKeyR5 is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR5.Value);
            DbElement? r6Ed = edComp.ContentKeyR6 is null
                ? null
                : DbElement.SelectSingle(_db, Key: edComp.ContentKeyR6.Value);

            List<(FhirReleases.FhirSequenceCodes fv, DbElement? ed)> contentPath = (igTr.PackagePair.SourceFhirSequence < igTr.PackagePair.TargetFhirSequence)
                ? [
                    (FhirReleases.FhirSequenceCodes.DSTU2, r2Ed),
                    (FhirReleases.FhirSequenceCodes.STU3, r3Ed),
                    (FhirReleases.FhirSequenceCodes.R4, r4Ed),
                    (FhirReleases.FhirSequenceCodes.R4B, r4bEd),
                    (FhirReleases.FhirSequenceCodes.R5, r5Ed),
                    (FhirReleases.FhirSequenceCodes.R6, r6Ed)]
                : [
                    (FhirReleases.FhirSequenceCodes.R6, r6Ed),
                    (FhirReleases.FhirSequenceCodes.R5, r5Ed),
                    (FhirReleases.FhirSequenceCodes.R4B, r4bEd),
                    (FhirReleases.FhirSequenceCodes.R4, r4Ed),
                    (FhirReleases.FhirSequenceCodes.STU3, r3Ed),
                    (FhirReleases.FhirSequenceCodes.DSTU2, r2Ed)];

            mappingTraceLines.AddRange(contentPath
                    .Where(cp => cp.ed is not null)
                    .Select(cp => $" {cp.fv}: `{cp.ed!.Id}` {cp.ed!.FhirCardinalityString} `{cp.ed!.FullCollatedTypeLiteral}`"));
        }

        if (mappingTraceLines.Count > 0)
        {
            mappingTrace = "* " + string.Join("\n* ", mappingTraceLines);
        }
        else
        {
            mappingTrace = $"* {igTr.PackagePair.SourceFhirSequence} `{sourceEd.Id}` {sourceEd.FhirCardinalityString} `{sourceEd.FullCollatedTypeLiteral}`";
            if (eots.Count != 0)
            {
                foreach (DbElementOutcomeTarget eot in eots)
                {
                    if (eot.TargetElementId is null)
                    {
                        continue;
                    }
                    mappingTrace += $"\n* {igTr.PackagePair.SourceFhirSequence} `{eot.TargetElementId}`";
                }
            }
        }

        return $$$"""
            This extension is part of the cross-version definitions generated to enable use of the
            element `{{{sourceEd.Id}}}` as defined in FHIR {{{igTr.PackagePair.SourceFhirSequence}}}
            in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}.

            The source element is defined as:
            `{{{sourceEd.Id}}}` {{{sourceEd.FhirCardinalityString}}} `{{{sourceEd.FullCollatedTypeLiteral}}}`

            Across FHIR versions, the element set has been mapped as:
            {{{mappingTrace}}}

            Following are the generation technical comments:
            {{{elementOutcome.Comments}}}
            """;
    }

    private StructureDefinition? buildExtSd(
        HashSet<string> generatedExtensionIds,
        XVerIgExportTrackingRecord igTr,
        Dictionary<int, DbElement> sourceElements,
        Dictionary<int, DbElement> targetElements,
        Dictionary<int, DbElementComparison> elementComparisons,
        Dictionary<int, string> contentReferenceExtUrlsByEdKey,
        DbElementOutcome edOutcome,
        DbStructureDefinition sourceSd,
        DbElement sourceEd,
        string purpose,
        List<StructureDefinition.ContextComponent> contexts,
        bool useComponentDefinition)
    {
        string id = edOutcome.GenShortId!;

        if (!generatedExtensionIds.Add(id))
        {
            return null;
        }

        (_, string name) = igTr.GetName(
            edOutcome.GenName!,
            id);

        // build the initial structure definition for the extension
        StructureDefinition extSd = new()
        {
            Id = id,
            Url = edOutcome.GenUrl,
            Name = name,
            Version = _exporter._crossDefinitionVersion,
            FhirVersion = EnumUtility.ParseLiteral<FHIRVersion>(igTr.PackagePair.TargetPackage.PackageVersion),
            Date = _exporter._runTime.ToString("O"),
            Title = edOutcome.GenArtifactShort ?? $"Cross-version Extension `{igTr.PackagePair.SourceFhirSequence}.{sourceEd.Id}` for use in FHIR {igTr.PackagePair.TargetFhirSequence}",
            Description = edOutcome.GenArtifactDescription ?? $"This cross-version extension represents the FHIR {igTr.PackagePair.SourceFhirSequence} element `{sourceEd.Id}` for use in FHIR {igTr.PackagePair.TargetFhirSequence}.",
            Purpose = purpose ?? edOutcome.GenArtifactComment ?? edOutcome.Comments,
            Status = PublicationStatus.Active,
            Experimental = false,
            Kind = StructureDefinition.StructureDefinitionKind.ComplexType,
            Abstract = false,
            Context = contexts,
            Type = "Extension",
            BaseDefinition = "http://hl7.org/fhir/StructureDefinition/Extension",
            Derivation = StructureDefinition.TypeDerivationRule.Constraint,
            Differential = new()
            {
                Element = [],
            },
        };

        // TODO: right now I am setting FHIR-I as the WG responsible since we are creating the extension - should we use the WG from the source resource?
        string wg = "fhir";

        // add the work group extension
        extSd.AddExtension(CommonDefinitions.ExtUrlWorkGroup, new Hl7.Fhir.Model.Code(wg));

        // ensure there is a publisher, use the WG if there is none
        extSd.Publisher = CommonDefinitions.WorkgroupNames[wg];

        // ensure there is a contact point - use the default WG unless there are multiple entries
        if ((extSd.Contact == null) || (extSd.Contact.Count < 2))
        {
            extSd.Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames[wg],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls[wg],
                        },
                    ],
                }
            ];
        }

        extSd.cgAddPackageSource(igTr.PackageId, _exporter._crossDefinitionVersion, igTr.PackageUrl);

        // the version-specific extension is only valid for R4 and later
        if (igTr.PackagePair.TargetFhirSequence >= FhirReleases.FhirSequenceCodes.R4)
        {
            switch (_exporter._versionSpecificExtBehavior)
            {
                case XVerExporter.VersionSpecificExtensionBehaviorCodes.None:
                    break;
                case XVerExporter.VersionSpecificExtensionBehaviorCodes.ShortVersion:
                    {
                        extSd.Extension.Add(new Extension()
                        {
                            Url = CommonDefinitions.ExtUrlVersionSpecificUse,
                            Extension = [
                                new()
                            {
                                Url = CommonDefinitions.ExtUrlVersionSpecificUseStart,
                                Value = new Code(igTr.PackagePair.TargetFhirVersionShort),
                            },
                            new()
                            {
                                Url = CommonDefinitions.ExtUrlVersionSpecificUseEnd,
                                Value = new Code(igTr.PackagePair.TargetFhirVersionShort),
                            },
                        ],
                        });
                    }
                    break;
                case XVerExporter.VersionSpecificExtensionBehaviorCodes.TargetVersion:
                    {
                        // add the version-specific fhir version information
                        string targetVersion = igTr.PackagePair.TargetFhirSequence >= FhirReleases.FhirSequenceCodes.R5
                            ? igTr.PackagePair.TargetFhirVersionShort
                            : igTr.PackagePair.TargetPackage.PackageVersion;

                        extSd.Extension.Add(new Extension()
                        {
                            Url = CommonDefinitions.ExtUrlVersionSpecificUse,
                            Extension = [
                                new()
                        {
                            Url = CommonDefinitions.ExtUrlVersionSpecificUseStart,
                            Value = new Code(targetVersion),
                        },
                        new()
                        {
                            Url = CommonDefinitions.ExtUrlVersionSpecificUseEnd,
                            Value = new Code(targetVersion),
                        },
                    ],
                        });
                    }
                    break;
            }
        }

        // add this element and its children to the differential
        addToDifferentialRecursive(
            extSd,
            igTr,
            sourceElements,
            targetElements,
            elementComparisons,
            contentReferenceExtUrlsByEdKey,
            edOutcome,
            sourceSd,
            sourceEd);

        return extSd;
    }

    private void addToDifferentialRecursive(
        StructureDefinition extSd,
        XVerIgExportTrackingRecord igTr,
        Dictionary<int, DbElement> sourceElements,
        Dictionary<int, DbElement> targetElements,
        Dictionary<int, DbElementComparison> elementComparisons,
        Dictionary<int, string> contentReferenceExtUrlsByEdKey,
        DbElementOutcome edOutcome,
        DbStructureDefinition sourceSd,
        DbElement sourceEd,
        string? extElementId = null,
        string? extElementPath = null,
        Dictionary<string, ElementDefinition>? dtValueElements = null,
        string? dataTypeNameLiteral = null,
        int? dtBindingVsKey = null,
        List<string>? dtTypeProfiles = null,
        List<string>? dtTargetProfiles = null,
        bool excludeExtensionElement = false,
        string? elementUrlOverride = null)
    {
        if (edOutcome.ExtensionDefinitionIsProhibited)
        {
            return;
        }

        bool inTopLevel = extSd.Differential.Element.Count == 0;

        List<DbElementOutcomeTarget> edOutcomeTargets = DbElementOutcomeTarget.SelectList(
            _db,
            ElementOutcomeKey: edOutcome.Key);

        int extMinCard;
        if ((sourceEd.MinCardinality != 0) &&
            (edOutcomeTargets.Count != 0) ||
            edOutcomeTargets.All(eot => (eot.TargetElementId is null) || (eot.TargetMinCardinality == 0)))
        {
            extMinCard = sourceEd.MinCardinality;
        }
        else
        {
            extMinCard = 0;
        }

        extElementId ??= "Extension";
        extElementPath ??= "Extension";
        dtValueElements ??= [];

        if (!igTr.EdOutcomeMapTargets.TryGetValue(edOutcome.Key, out List<EdOutcomeMapTargetRecord>? mapTargetRecs))
        {
            mapTargetRecs = [];
            igTr.EdOutcomeMapTargets[edOutcome.Key] = mapTargetRecs;
        }

        //bool isRoot = edOutcome.ParentElementOutcomeKey is null;
        bool isRoot = extSd.Differential.Element.Count == 0;

        // add the starting element for where we are
        if (isRoot)
        {
            string? modReason = (edOutcome.DefineAsModifier != true)
                ? null
                : edOutcome.ModifierReason
                    ?? sourceEd.IsModifierReason
                    ?? $"This extension is a modifier because the source element `{sourceEd.Id}` is flagged as a modifier element.";

            // add the root extension element for the structure
            ElementDefinition rootElement = new()
            {
                ElementId = extElementId,
                Path = extElementPath,
                Short = edOutcome.GenArtifactShort ?? sourceEd.Short,
                Definition = edOutcome.GenArtifactDescription ?? sourceEd.Definition,
                Comment = edOutcome.GenArtifactComment ?? sourceEd.Comments,
                Min = extMinCard,
                Max = sourceEd.MaxCardinalityString,
                Base = new()
                {
                    Path = "Extension",
                    Min = 0,
                    Max = "*",
                },
                IsModifier = edOutcome.DefineAsModifier,
                IsModifierReason = modReason,
            };

            if (sourceEd.IsDeprecated)
            {
                rootElement.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("deprecated"));
            }

            extSd.Differential.Element.Add(rootElement);

            mapTargetRecs.Add(new()
            {
                sdCanonical = $"{extSd.Url}|{_exporter._crossDefinitionVersion}",
                elementPath = extElementId,
            });
        }
        else if (!excludeExtensionElement)
        {
            // add the primary slice
            ElementDefinition sliceElement = new()
            {
                ElementId = extElementId,
                SliceName = elementUrlOverride ?? edOutcome.SourceNameClean(),  // edOutcome.GenShortId,
                Path = extElementPath,
                Short = edOutcome.GenArtifactShort ?? sourceEd.Short,
                Definition = edOutcome.GenArtifactDescription ?? sourceEd.Definition,
                Comment = edOutcome.GenArtifactComment ?? sourceEd.Comments,
                Requirements = (sourceEd.Requirements is null)
                    ? edOutcome.Comments
                    : sourceEd.Requirements + " " + edOutcome.Comments,
                Min = extMinCard,
                Max = sourceEd.MaxCardinalityString,
                Base = new()
                {
                    Path = "Extension.extension",
                    Min = 0,
                    Max = "*",
                }
            };

            if (sourceEd.IsDeprecated)
            {
                sliceElement.AddExtension(CommonDefinitions.ExtUrlStandardStatus, new Code("deprecated"));
            }

            extSd.Differential.Element.Add(sliceElement);

            mapTargetRecs.Add(new()
            {
                sdCanonical = $"{extSd.Url}|{_exporter._crossDefinitionVersion}",
                elementPath = extElementId,
            });
        }

        // check to see if we are using an extension substitution
        if (edOutcome.ExtensionSubstitutionUrl is not null)
        {
            // add the URL element (always required)
            extSd.Differential.Element.Add(new()
            {
                ElementId = extElementId + ".url",
                Path = extElementPath + ".url",
                Base = new()
                {
                    Path = "Extension.url",
                    Min = 1,
                    Max = "1",
                },
                Min = 1,
                Max = "1",
                Fixed = new FhirUri(edOutcome.ExtensionSubstitutionUrl),
            });

            mapTargetRecs.Add(new()
            {
                sdCanonical = edOutcome.ExtensionSubstitutionUrl,
                elementPath = extElementId,
            });

            // done with this path
            return;
        }

        // check to see if this is an outcome that is a content reference
        if (!isRoot &&
            (edOutcome.ContentReferenceAncestorId is not null))
        {
            // add the URL element (always required)
            extSd.Differential.Element.Add(new()
            {
                ElementId = extElementId + ".url",
                Path = extElementPath + ".url",
                Base = new()
                {
                    Path = "Extension.url",
                    Min = 1,
                    Max = "1",
                },
                Min = 1,
                Max = "1",
                Fixed = new FhirUri(edOutcome.ContentReferenceExtensionUrl),
            });

            // add a contrained out value element
            extSd.Differential.Element.Add(new()
            {
                ElementId = extElementId + ".value[x]",
                Path = extElementPath + ".value[x]",
                Base = new()
                {
                    Path = "Extension.value[x]",
                    Min = 0,
                    Max = "1",
                },
                Min = 0,
                Max = "0",
                Type = [],
            });

            mapTargetRecs.Add(new()
            {
                sdCanonical = $"{edOutcome.ContentReferenceExtensionUrl!}|{_exporter._crossDefinitionVersion}",
                elementPath = extElementId,
            });

            // done with this path
            return;
        }

        // check for needing to add additional alternate exensions
        if (edOutcome.AlternateCanonicalTargetsLiteral is not null)
        {
            mapTargetRecs.Add(new()
            {
                sdCanonical = CommonDefinitions.ExtUrlAlternateCanonical,
                elementPath = "Extension",
            });
        }

        if (edOutcome.AlternateReferenceTargetsLiteral is not null)
        {
            mapTargetRecs.Add(new()
            {
                sdCanonical = CommonDefinitions.ExtUrlAlternateReference,
                elementPath = "Extension",
            });
        }

        // if this is a datatype element, add the necessary meta elements
        if (dataTypeNameLiteral is not null)
        {
            addDataTypeMetaElements(
                igTr.PackagePair.SourceFhirSequence,
                igTr.PackagePair.TargetFhirSequence,
                extSd,
                extElementId,
                extElementPath,
                dataTypeNameLiteral,
                addSliceEd: true,
                addUrlEd: true,
                addValueEd: true);
        }

        // get the child outcomes so we know if there are child elements to process
        List<DbElementOutcome> childOutcomes = DbElementOutcome.SelectList(
            _db,
            ParentElementOutcomeKey: edOutcome.Key,
            //RequiresXVerDefinition: true,             // TODO: verify, but once we are in an element I think we need to keep the whole tree
            orderByProperties: [nameof(DbElementOutcome.SourceResourceOrder)]);

        //bool hasChildren = childOutcomes.Count > 0;
        bool hasChildren = sourceEd.ChildElementCount > 0;

        // get the unmapped source types so we can determine value[x] types vs. _datatype extension slices
        List<DbElementType> sourceTypes = (hasChildren || edOutcome.UnmappedTypeKeysLiteral is null)
            ? []
            : DbElementType.SelectList(
                _db,
                KeyValues: edOutcome.UnmappedTypeKeys);

        // if there are no unmapped types, we need to account for everything (e.g., we are in an element that is a child of a type)
        if ((sourceTypes.Count == 0) &&
            !hasChildren)
        {
            sourceTypes = DbElementType.SelectList(
                _db,
                ElementKey: sourceEd.Key);
        }

        List<(string tn, bool isValid, DbElementType et)> typeValidity = sourceTypes
            .Select(et => getValidValueType(igTr.PackagePair.TargetFhirSequence, et))
            .ToList();

        List<(string tn, DbElementType et)> validValueTypes = typeValidity
            .Where(tv => tv.isValid)
            .Select(tv => (tv.tn, tv.et))
            .ToList();

        List<(string tn, DbElementType et)> invalidValueTypes = typeValidity
            .Where(tv => !tv.isValid)
            .Select(tv => (tv.tn, tv.et))
            .ToList();

        bool needsValueElement = validValueTypes.Count > 0;
        bool needsExtensionElement = hasChildren || (invalidValueTypes.Count > 0);

        List<string> distinctSourceTypeNames = sourceTypes.Select(t => t.TypeName ?? t.Literal).Distinct().ToList();

        bool codeableRefDisableBinding = false;
        bool codeableRefDisableTargets = false;

        // check for a partially-mapped codeablereference
        if ((distinctSourceTypeNames.Count == 1) &&
            (distinctSourceTypeNames[0] == "CodeableReference") &&
            (edOutcome.MappedChildTypeElementNamesLiteral is not null) &&
            (edOutcome.UnmappedChildTypeElementNamesLiteral is not null))
        {
            // handle the case of mapped concept and unmapped reference
            if ((edOutcome.MappedChildTypeElementNamesLiteral == "concept") &&
                (edOutcome.UnmappedChildTypeElementNamesLiteral == "reference"))
            {
                codeableRefDisableBinding = true;
                typeValidity = sourceTypes
                    .Select(et => ("Reference", true, et))
                    .ToList();
            }

            // handle the case of mapped reference and unmapped concept
            if ((edOutcome.MappedChildTypeElementNamesLiteral == "reference") &&
                (edOutcome.UnmappedChildTypeElementNamesLiteral == "concept"))
            {
                codeableRefDisableTargets = true;
                typeValidity = sourceTypes
                    .Select(et => ("CodeableConcept", true, et))
                    .ToList();
            }

            validValueTypes = typeValidity
                .Where(tv => tv.isValid)
                .Select(tv => (tv.tn, tv.et))
                .ToList();

            invalidValueTypes = typeValidity
                .Where(tv => !tv.isValid)
                .Select(tv => (tv.tn, tv.et))
                .ToList();

            needsValueElement = validValueTypes.Count > 0;
            needsExtensionElement = hasChildren || (invalidValueTypes.Count > 0);
        }

        int extensionMinCardinality = childOutcomes.Sum(eo => eo.SourceMinCardinality);
        if (invalidValueTypes.Count > 0)
        {
            extensionMinCardinality += sourceEd.MinCardinality;
        }

        if (needsExtensionElement)
        {
            ElementDefinition extensionElement = new()
            {
                ElementId = extElementId + ".extension",
                Path = extElementPath + ".extension",
                Base = new()
                {
                    Path = "Extension.extension",
                    Min = 0,
                    Max = "*",
                },
                Slicing = new()
                {
                    Discriminator = [
                        new() {
                            Type = ElementDefinition.DiscriminatorType.Value,
                            Path = "url",
                        }
                    ],
                    Ordered = false,
                    Rules = ElementDefinition.SlicingRules.Open,
                },
                Min = extensionMinCardinality,
                Max = "*",
            };

            extSd.Differential.Element.Add(extensionElement);
        }

        // nest into child elements first
        foreach (DbElementOutcome childOutcome in childOutcomes)
        {
            // get the source element
            if (!sourceElements.TryGetValue(childOutcome.SourceElementKey, out DbElement? childSourceEd))
            {
                _logger.LogError($"Source element with key `{childOutcome.SourceElementKey}` not found for child element outcome with key `{childOutcome.Key}`");
                continue;
            }

            if (skipElement(childSourceEd))
            {
                continue;
            }

            //string nextId = extElementId + ".extension:" + childOutcome.GenShortId;
            string nextId = extElementId + ".extension:" + childOutcome.SourceNameClean();
            string nextPath = extElementPath + ".extension";

            addToDifferentialRecursive(
                extSd,
                igTr,
                sourceElements,
                targetElements,
                elementComparisons,
                contentReferenceExtUrlsByEdKey,
                childOutcome,
                sourceSd,
                childSourceEd,
                nextId,
                nextPath,
                dtValueElements);
        }

        List<string> distinctInvalidTypeNames = invalidValueTypes
            .Select(kvp => kvp.et.TypeName ?? kvp.et.Literal)
            .Distinct()
            .ToList();

        if (invalidValueTypes.Count > 0)
        {
            // create a lookup so we can consolidate types
            ILookup<string, (int invalidTypeKey, DbElementType et)> ietsByCode = invalidValueTypes
                .ToLookup(kvp => kvp.et.TypeName ?? kvp.et.Literal, kvp => (kvp.et.Key, kvp.et));

            foreach (string distinctTypeName in distinctInvalidTypeNames)
            {
                List<(int invalidTypeKey, DbElementType et)> iets = ietsByCode[distinctTypeName].ToList();

                // if there is only one type, it might be a content reference
                if ((iets.Count == 1) &&
                    !isRoot &&
                    contentReferenceExtUrlsByEdKey.TryGetValue(iets[0].et.ElementKey, out string? crUrl))
                {
                    // resolve the element
                    DbElement? crEd = DbElement.SelectSingle(_db, Key: iets[0].et.ElementKey);
                    if (crEd is null)
                    {
                        throw new Exception($"Failed to resolve data type content reference element: {crUrl}, {iets[0].et.ElementKey}, {distinctTypeName}");
                    }

                    // add this as a slice
                    addToDifferentialRecursive(
                        extSd,
                        igTr,
                        sourceElements,
                        targetElements,
                        elementComparisons,
                        contentReferenceExtUrlsByEdKey,
                        edOutcome,
                        sourceSd,
                        crEd,
                        extElementId + ".extension:" + crEd.NameClean(),
                        extElementPath + ".extension",
                        dtValueElements);

                    continue;
                }

                // consolidate profile information
                List<string> ietTypeProfiles = iets
                    .Where(kvp => kvp.et.TypeProfile is not null)
                    .Select(kvp => kvp.et.TypeProfile!)
                    .ToList();

                List<string> ietTargetProfiles = iets
                    .Where(kvp => kvp.et.TargetProfile is not null)
                    .Select(kvp => kvp.et.TargetProfile!)
                    .ToList();

                // resolve a type element for this type
                DbElement? ietEd = DbElement.SelectSingle(
                    _db,
                    FhirPackageKey: igTr.PackagePair.SourcePackageKey,
                    Id: distinctTypeName);

                if (ietEd is null)
                {
                    throw new Exception($"Failed to resolve element: {distinctTypeName} in FHIR {igTr.PackagePair.SourceFhirSequence}");
                }

                if (skipElement(ietEd, skipFirstElement: false, skipIds: true, skipExtensions: true, skipModifierExtenions: true))
                {
                    continue;
                }

                if (distinctInvalidTypeNames.Count == 1)
                {
                    List<DbElement> dtElements = DbElement.SelectList(
                        _db,
                        ParentElementKey: ietEd.Key);

                    // if there is a single type, nest into it directly
                    addDataTypeMetaElements(
                        igTr.PackagePair.SourceFhirSequence,
                        igTr.PackagePair.TargetFhirSequence,
                        extSd,
                        extElementId,
                        extElementPath,
                        distinctTypeName,
                        addSliceEd: true,
                        addUrlEd: true,
                        addValueEd: true);

                    foreach (DbElement dtEtEd in dtElements)
                    {
                        if (skipElement(dtEtEd, skipFirstElement: true, skipIds: true, skipExtensions: true, skipModifierExtenions: true))
                        {
                            continue;
                        }

                        // resolve an element outcome for this source
                        DbElementOutcome? etEdOutcome = DbElementOutcome.SelectSingle(
                            _db,
                            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                            SourceElementKey: dtEtEd.Key);

                        if (etEdOutcome is null)
                        {
                            throw new Exception("What do I do here?");
                        }

                        addToDifferentialRecursive(
                            extSd,
                            igTr,
                            sourceElements,
                            targetElements,
                            elementComparisons,
                            contentReferenceExtUrlsByEdKey,
                            etEdOutcome,
                            sourceSd,
                            dtEtEd,
                            extElementId + ".extension:" + dtEtEd.NameClean(),
                            extElementPath + ".extension",
                            dtValueElements,
                            null,
                            sourceEd.BindingValueSetKey,
                            ietTypeProfiles,
                            ietTargetProfiles,
                            excludeExtensionElement: false,
                            elementUrlOverride: dtEtEd.NameClean());
                    }
                }
                else
                {
                    // resolve an element outcome for this source
                    DbElementOutcome? etEdOutcome = DbElementOutcome.SelectSingle(
                        _db,
                        SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                        TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                        SourceElementKey: ietEd.Key);

                    if (etEdOutcome is null)
                    {
                        throw new Exception("What do I do here?");
                    }

                    // if there are more types, nest into them as 'valueX' slices
                    addToDifferentialRecursive(
                        extSd,
                        igTr,
                        sourceElements,
                        targetElements,
                        elementComparisons,
                        contentReferenceExtUrlsByEdKey,
                        etEdOutcome,
                        sourceSd,
                        ietEd,
                        extElementId + ":extension:value" + distinctTypeName,
                        extElementPath + ".extension",
                        dtValueElements,
                        distinctTypeName,
                        sourceEd.BindingValueSetKey,
                        ietTypeProfiles,
                        ietTargetProfiles,
                        excludeExtensionElement: false,
                        elementUrlOverride: $"value{distinctTypeName}");
                }
            }
        }

        // add the URL element (always required)
        extSd.Differential.Element.Add(new()
        {
            ElementId = extElementId + ".url",
            Path = extElementPath + ".url",
            Base = new()
            {
                Path = "Extension.url",
                Min = 1,
                Max = "1",
            },
            Min = 1,
            Max = "1",
            Fixed = new FhirUri(elementUrlOverride ?? (inTopLevel ? edOutcome.GenUrl : edOutcome.GenSliceName)),
        });

        // add the value element - either with proper types or constrained to zero repetitions
        if (validValueTypes.Count == 0)
        {
            ElementDefinition etValueEd = new()
            {
                ElementId = extElementId + ".value[x]",
                Path = extElementPath + ".value[x]",
                Base = new()
                {
                    Path = "Extension.value[x]",
                    Min = 0,
                    Max = "1",
                },
                Min = 0,
                Max = "0",
                Type = [],
            };

            extSd.Differential.Element.Add(etValueEd);
        }
        else
        {
            // need to adjust the min based on whether we have something that is in the extension space
            int valueMin = sourceEd.MinCardinality;
            if ((valueMin != 0) && needsExtensionElement)
            {
                valueMin = 0;
            }

            ElementDefinition etValueEd = new()
            {
                ElementId = extElementId + ".value[x]",
                Path = extElementPath + ".value[x]",
                Short = sourceEd.Short,
                Definition = sourceEd.Definition,
                Comment = sourceEd.Comments,
                Base = new()
                {
                    Path = "Extension.value[x]",
                    Min = 0,
                    Max = "1",
                },
                Min = valueMin,
                Max = "1",
                Type = [],
            };

            bool appliedBinding = false;

            // add a value-set binding (if necessary)
            if ((sourceEd.BindingValueSetKey is not null) &&
                !codeableRefDisableBinding)
            {
                DbValueSetOutcome? vsOutcome = DbValueSetOutcome.SelectSingle(
                    _db,
                    SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                    TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                    SourceValueSetKey: sourceEd.BindingValueSetKey!.Value);

                if (vsOutcome is not null)
                {
                    if (vsOutcome.RequiresXVerDefinition == true)
                    {
                        etValueEd.Binding = new()
                        {
                            Strength = sourceEd.ValueSetBindingStrength ?? BindingStrength.Preferred,
                            Description = sourceEd.BindingDescription,
                            ValueSet = vsOutcome.GenUrl,
                        };
                    }
                    else if (vsOutcome.TargetValueSetKey is not null)
                    {
                        etValueEd.Binding = new()
                        {
                            Strength = sourceEd.ValueSetBindingStrength ?? BindingStrength.Preferred,
                            Description = sourceEd.BindingDescription,
                            ValueSet = vsOutcome.TargetCanonicalVersioned ?? vsOutcome.TargetCanonicalUnversioned,
                        };
                    }

                    appliedBinding = true;
                }
            }

            bool addedDtTypeProfiles = false;
            bool addedDtTargetProfiles = false;

            // add the types
            Dictionary<string, ElementDefinition.TypeRefComponent> typeRefs = [];
            foreach ((string rawTypeName, DbElementType et) in validValueTypes)
            {
                bool skipProfile = false;
                string typeName;
                if ((rawTypeName != "Quantity") || (et.TypeProfile is null))
                {
                    typeName = rawTypeName;
                }
                else
                {
                    string quantityProfile = et.TypeProfile.Split('/')[^1];
                    if (_extensionValueTypeNames[igTr.PackagePair.TargetFhirSequence].Contains(quantityProfile))
                    {
                        typeName = quantityProfile;
                        skipProfile = true;
                    }
                    else
                    {
                        typeName = rawTypeName;
                    }
                }

                if (!typeRefs.TryGetValue(typeName, out ElementDefinition.TypeRefComponent? tr))
                {
                    tr = new()
                    {
                        Code = typeName,
                    };
                    typeRefs[typeName] = tr;
                }

                if ((dtBindingVsKey is not null) &&
                    FhirTypeMappings.CanApplyBindings(typeName) &&
                    !codeableRefDisableBinding)
                {
                    DbValueSetOutcome? vsOutcome = DbValueSetOutcome.SelectSingle(
                        _db,
                        SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                        TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                        SourceValueSetKey: dtBindingVsKey.Value);

                    if (!appliedBinding && (vsOutcome is not null))
                    {
                        if (vsOutcome.RequiresXVerDefinition == true)
                        {
                            etValueEd.Binding = new()
                            {
                                Strength = sourceEd.ValueSetBindingStrength ?? BindingStrength.Preferred,
                                Description = sourceEd.BindingDescription,
                                ValueSet = vsOutcome.GenUrl,
                            };
                        }
                        else if (vsOutcome.TargetValueSetKey is not null)
                        {
                            etValueEd.Binding = new()
                            {
                                Strength = sourceEd.ValueSetBindingStrength ?? BindingStrength.Preferred,
                                Description = sourceEd.BindingDescription,
                                ValueSet = vsOutcome.TargetCanonicalVersioned ?? vsOutcome.TargetCanonicalUnversioned,
                            };
                        }

                        appliedBinding = true;
                    }
                }

                if ((et.TypeProfile is not null) && !skipProfile)
                {
                    tr.ProfileElement ??= [];
                    tr.ProfileElement.AddRange(getValidResourceUrls(igTr.PackagePair.SequencePair, [et.TypeProfile]));
                }

                if (!addedDtTypeProfiles &&
                    (dtTypeProfiles is not null) &&
                    FhirTypeMappings.CanApplyTypeProfiles(typeName))
                {
                    addedDtTypeProfiles = true;
                    tr.ProfileElement ??= [];
                    tr.ProfileElement.AddRange(getValidResourceUrls(igTr.PackagePair.SequencePair, dtTypeProfiles));
                }

                if ((et.TargetProfile is not null) &&
                    !codeableRefDisableTargets)
                {
                    tr.TargetProfileElement ??= [];
                    tr.TargetProfileElement.AddRange(getValidResourceUrls(igTr.PackagePair.SequencePair, [et.TargetProfile]));
                }

                if (!addedDtTargetProfiles &&
                    (dtTargetProfiles is not null) &&
                    FhirTypeMappings.CanApplyTargetProfiles(typeName) &&
                    !codeableRefDisableTargets)
                {
                    addedDtTargetProfiles = true;
                    tr.TargetProfileElement ??= [];
                    tr.TargetProfileElement.AddRange(getValidResourceUrls(igTr.PackagePair.SequencePair, dtTargetProfiles));
                }
            }

            etValueEd.Type = typeRefs.Values.OrderBy(tr => tr.TypeName).ToList();

            // if we applied a binding, make sure we actually wanted to
            if (appliedBinding)
            {
                HashSet<string> sourceTypeHash = new(sourceEd.DistinctTypeLiterals.Split(','));

                bool sourceIsCoded = sourceTypeHash.Contains("CodeableConcept") ||
                    sourceTypeHash.Contains("CodeableReference") ||
                    sourceTypeHash.Contains("Coding") ||
                    sourceTypeHash.Contains("Code");

                bool targetIsCoded = typeRefs.Keys.Contains("CodeableConcept") ||
                    typeRefs.Keys.Contains("CodeableReference") ||
                    typeRefs.Keys.Contains("Coding") ||
                    typeRefs.Keys.Contains("Code");

                if (sourceIsCoded && !targetIsCoded)
                {
                    etValueEd.Binding = null;
                    appliedBinding = false;
                }
            }

            // add our element
            extSd.Differential.Element.Add(etValueEd);
        }
    }

    private List<Canonical> getValidResourceUrls(
        (FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t) ps,
        List<string> urls)
    {
        if (!_resourceReferenceLookup.TryGetValue(ps, out PackagePairStructureMappingTracker? mt) ||
            (mt is null))
        {
            return urls.Select(v => new Canonical(v)).ToList();
        }

        HashSet<string> valids = [];
        foreach (string v in urls)
        {
            List<string> targets = mt.GetTargets(v);
            if (targets.Count == 0)
            {
                valids.Add(v);
                continue;
            }

            foreach (string t in mt.GetTargets(v))
            {
                valids.Add(t);
            }
        }

        return valids.Select(v => new Canonical(v)).ToList();
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

    private void addDataTypeMetaElements(
        FhirReleases.FhirSequenceCodes sourceFhirSequence,
        FhirReleases.FhirSequenceCodes targetFhirSequence,
        StructureDefinition extSd,
        string currentId,
        string currentPath,
        string dataTypeName,
        bool addSliceEd,
        bool addUrlEd,
        bool addValueEd)
    {
        string id = currentId + ".extension:_datatype";
        string path = currentPath + ".extension";

        if (addSliceEd)
        {
            ElementDefinition sliceEd = new()
            {
                ElementId = id,
                Path = path,
                SliceName = "_datatype",
                Short = $"DataType slice for a FHIR {sourceFhirSequence} `{dataTypeName}` value",
                Definition = $"Slice to indicate the presence of a {sourceFhirSequence} `{dataTypeName}` in FHIR {targetFhirSequence}",
                Min = 1,
                Max = "1",
                Base = new()
                {
                    Path = "Extension.extension",
                    Min = 0,
                    Max = "*",
                },
            };
            extSd.Differential.Element.Add(sliceEd);
        }

        if (addUrlEd)
        {
            ElementDefinition urlEd = new()
            {
                ElementId = id + ".url",
                Path = path + ".url",
                Fixed = new FhirUri("http://hl7.org/fhir/StructureDefinition/_datatype"),
                Min = 1,
                Max = "1",
                Base = new()
                {
                    Path = "Extension.url",
                    Min = 1,
                    Max = "1",
                },
            };
            extSd.Differential.Element.Add(urlEd);
        }

        if (addValueEd)
        {
            ElementDefinition valueEd = new()
            {
                ElementId = id + ".value[x]",
                Path = path + ".value[x]",
                Comment = $"Must be: {dataTypeName}",
                Min = 1,
                Max = "1",
                Base = new()
                {
                    Path = "Extension.value[x]",
                    Min = 0,
                    Max = "1",
                },
                Type = [
                    new()
                    {
                        Code = "string",
                    }
                ],
                Fixed = new FhirString(dataTypeName),
            };
            extSd.Differential.Element.Add(valueEd);
        }
    }

    private (string tn, bool isValid, DbElementType et) getValidValueType(FhirReleases.FhirSequenceCodes targetFhirSequence, DbElementType et)
    {
        string desiredType = et.TypeName ?? et.Literal;

        if (_extensionValueTypeNames[targetFhirSequence].Contains(desiredType))
        {
            return (desiredType, true, et);
        }

        if (FhirTypeMappings.PrimitiveTypeFallbacks.TryGetValue(desiredType, out string? fallbackType) &&
            _extensionValueTypeNames[targetFhirSequence].Contains(fallbackType))
        {
            return (fallbackType, true, et);
        }

        return (desiredType, false, et);

        //throw new Exception($"Type `{desiredType}` is not a valid extension value type in FHIR {targetFhirSequence} and has no valid fallback.");
    }
}
