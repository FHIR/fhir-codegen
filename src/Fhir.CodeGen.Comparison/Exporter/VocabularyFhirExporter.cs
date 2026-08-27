using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.Exporter.IgExporter;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Exporter;

public class VocabularyFhirExporter
{
    private readonly XVerExporter _exporter;
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    public VocabularyFhirExporter(
        XVerExporter exporter,
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VocabularyFhirExporter>();
        _db = db;
        _exporter = exporter;
    }

    public void Export(XVerExportTrackingRecord tr)
    {
        // iterate over the XVer IGs
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            // export code systems
            exportCodeSystems(igTr);

            // export value sets
            exportValueSets(igTr);

            // export concept maps
            exportConceptMaps(igTr);
        }
    }

    private void exportConceptMaps(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.ConceptMapToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        CrossVersionExporter.ConceptMapToR4? exporterR4 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R5)
            ? new()
            : null;

        if (igTr.VocabMapDir is null)
        {
            throw new Exception("VocabMapDir is null");
        }

        string dir = igTr.VocabMapDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing Value Set Concept Maps for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the souce value sets for the source package
        List<DbValueSet> sourceValueSets = DbValueSet.SelectList(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // list over the value sets to build concept maps
        foreach (DbValueSet sourceVs in sourceValueSets)
        {
            if (XVerProcessor._exclusionSet.Contains(sourceVs.VersionedUrl) ||
                XVerProcessor._exclusionSet.Contains(sourceVs.UnversionedUrl))
            {
                continue;
            }

            // get the value set outcomes for this value set that need exporting
            List<DbValueSetOutcome> vsOutcomes = DbValueSetOutcome.SelectList(
                _db,
                SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
                TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
                SourceValueSetKey: sourceVs.Key,
                RequiresXVerDefinition: true);

            if (vsOutcomes.Count == 0)
            {
                // nothing to export
                continue;
            }

            // iterate over the outcomes
            foreach (DbValueSetOutcome vsOutcome in vsOutcomes)
            {
                if (XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalVersioned) ||
                    XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalUnversioned))
                {
                    continue;
                }

                // check for no target (no concept map possible)
                if (vsOutcome.TargetValueSetKey is null)
                {
                    continue;
                }

                // resolve the target value set
                DbValueSet? targetVs = DbValueSet.SelectSingle(
                    _db,
                    Key: vsOutcome.TargetValueSetKey.Value);

                if (targetVs is null)
                {
                    _logger.LogError($"Could not resolve target ValueSet `{vsOutcome.TargetCanonicalVersioned}`");
                    continue;
                }

                ConceptMap vsCm = createVsConceptMap(
                    igTr,
                    sourceVs,
                    targetVs,
                    vsOutcome);

                // add contents
                addConceptMapContents(
                    igTr,
                    sourceVs,
                    targetVs,
                    vsOutcome,
                    vsCm);

                // write the concept map to a file
                string filename = vsOutcome.ConceptMapFileName ?? throw new ArgumentNullException(nameof(vsOutcome.ConceptMapFileName));
                string path = Path.Combine(dir, filename + ".json");
                if (exporterR3 is not null)
                {
                    File.WriteAllText(path, exporterR3.ToJson(vsCm, new SerializerSettings() { Pretty = true }));
                }
                else if (exporterR4 is not null)
                {
                    File.WriteAllText(path, exporterR4.ToJson(vsCm, new SerializerSettings() { Pretty = true }));
                }
                else
                {
                    File.WriteAllText(path, vsCm.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
                }
                exported.Add(new()
                {
                    FileName = filename + ".json",
                    FileNameWithoutExtension = filename,
                    IsPageContentFile = false,
                    Name = vsCm.Name,
                    Id = vsCm.Id,
                    Url = vsCm.Url,
                    ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ConceptMap.GetLiteral(),
                    Version = vsCm.Version,
                    Description = vsCm.Description ?? vsCm.Title ?? $"Value Set Concept Map: {vsCm.Url}",
                });
            }
        }

        // save our changes
        _logger.LogInformation($"Wrote {exported.Count} Value Set Concept Maps for `{igTr.PackageId}`");
        igTr.VsConceptMapFiles = exported;
    }

    private void addConceptMapContents(
        XVerIgExportTrackingRecord igTr,
        DbValueSet sourceVs,
        DbValueSet targetVs,
        DbValueSetOutcome vsOutcome,
        ConceptMap vsCm)
    {
        // get all concept map outcomes for this value set outcome
        List<DbValueSetConceptOutcome> vscOutcomes = DbValueSetConceptOutcome.SelectList(
            _db,
            ValueSetOutcomeKey: vsOutcome.Key,
            orderByProperties: [
                nameof(DbValueSetConceptOutcome.SourceSystem),
                nameof(DbValueSetConceptOutcome.TargetSystem),
                nameof(DbValueSetConceptOutcome.SourceCode),
                nameof(DbValueSetConceptOutcome.TargetCode)]);

        string? lastSourceSystem = null;
        string? lastTargetSystem = null;

        string? lastSourceCode = null;

        ConceptMap.GroupComponent? currentGroup = null;
        ConceptMap.SourceElementComponent? currentSourceElement = null;

        // iterate over the concept map outcomes
        foreach (DbValueSetConceptOutcome vscOutcome in vscOutcomes)
        {
            // check if we need a new group
            if ((currentGroup is null) ||
                (lastSourceSystem != vscOutcome.SourceSystem) ||
                (lastTargetSystem != vscOutcome.TargetSystem))
            {
                string sourceCanonical = vscOutcome.SourceSystemVersion is null
                    ? vscOutcome.SourceSystem
                    : (vscOutcome.SourceSystem + "|" + vscOutcome.SourceSystemVersion);

                string? targetCanonical = vscOutcome.TargetSystem is null
                    ? null
                    : vscOutcome.TargetSystemVersion is null
                    ? vscOutcome.TargetSystem
                    : (vscOutcome.TargetSystem + "|" + vscOutcome.TargetSystemVersion);

                // create a new group
                currentGroup = new()
                {
                    Source = sourceCanonical,
                    Target = targetCanonical,
                    Element = [],
                };
                vsCm.Group.Add(currentGroup);
                lastSourceSystem = vscOutcome.SourceSystem;
                lastTargetSystem = vscOutcome.TargetSystem;

                lastSourceCode = null;
                currentSourceElement = null;
            }

            // check if we need a new source element
            if ((currentSourceElement is null) ||
                (lastSourceCode != vscOutcome.SourceCode))
            {
                // create a new source element
                currentSourceElement = new()
                {
                    Code = vscOutcome.SourceCode,
                    Display = vscOutcome.SourceDisplay,
                    Target = [],
                };
                currentGroup.Element.Add(currentSourceElement);
                lastSourceCode = vscOutcome.SourceCode;
            }

            // check for no-map
            if ((vscOutcome.TargetValueSetKey is null) ||
                (vscOutcome.TargetCode is null))
            {
                // flag as no-map
                currentSourceElement.NoMap = true;

                // no mapping
                continue;
            }

            CMR relationship;
            if (vscOutcome.IsIdentical || vscOutcome.IsEquivalent)
            {
                relationship = CMR.Equivalent;
            }
            else if (vscOutcome.IsBroaderThanTarget)
            {
                relationship = CMR.SourceIsBroaderThanTarget;
            }
            else if (vscOutcome.IsNarrowerThanTarget)
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
                Code = vscOutcome.TargetCode!,
                Display = vscOutcome.TargetDisplay,
                Relationship = relationship,
                Comment = vscOutcome.Comments,
            };

            currentSourceElement.Target.Add(targetElement);
        }

        // if there is only one group of proper maps, move any no-maps into it
        if (vsCm.Group.Count == 2)
        {
            ConceptMap.GroupComponent? mappedGroup = null;
            ConceptMap.GroupComponent? unmappedGroup = null;
            foreach (ConceptMap.GroupComponent group in vsCm.Group)
            {
                if (string.IsNullOrEmpty(group.Target))
                {
                    unmappedGroup = group;
                    continue;
                }

                mappedGroup = group;
            }

            if ((unmappedGroup is not null) &&
                (mappedGroup is not null))
            {
                // move the no-maps
                foreach (ConceptMap.SourceElementComponent sourceElement in unmappedGroup.Element)
                {
                    mappedGroup.Element.Add(sourceElement);
                }

                // remove the no-map group
                vsCm.Group.Remove(unmappedGroup);
            }
        }
    }

    private ConceptMap createVsConceptMap(
        XVerIgExportTrackingRecord igTr,
        DbValueSet sourceVs,
        DbValueSet targetVs,
        DbValueSetOutcome vsOutcome)
    {
        if ((vsOutcome.ConceptMapLongId is null) ||
            (vsOutcome.ConceptMapUrl is null) ||
            (vsOutcome.ConceptMapName is null))
        {
            throw new ArgumentNullException($"Missing required ValueSet Outcome content!");
        }

        (_, string name) = igTr.GetName(vsOutcome.ConceptMapName!, vsOutcome.ConceptMapLongId);

        ConceptMap vsCm = new()
        {
            Id = vsOutcome.ConceptMapLongId,
            Url = vsOutcome.ConceptMapUrl,
            Name = name,
            Version = _exporter._crossDefinitionVersion,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version ConceptMap for ValueSet {vsOutcome.GenLongId} from FHIR {igTr.PackagePair.SourceFhirSequence} to FHIR {igTr.PackagePair.TargetFhirSequence}",
            Description = $"This ConceptMap represents the cross-version mapping of concepts from ValueSet `{sourceVs.VersionedUrl}` for use in FHIR {igTr.PackagePair.TargetFhirSequence}.",
            Status = PublicationStatus.Active,
            Experimental = false,
            SourceScope = new Canonical(sourceVs.VersionedUrl),
            //SourceScope = new FhirUri(sourceVs.VersionedUrl),
            TargetScope = new Canonical(targetVs.VersionedUrl),
            //TargetScope = new FhirUri(targetVs.VersionedUrl),
        };

        return vsCm;
    }

    private void exportValueSets(XVerIgExportTrackingRecord igTr)
    {
        CrossVersionExporter.ValueSetToR3? exporterR3 = (_exporter._versionSpecificExport == XVerExporter.VersionSpecificExportCodes.TargetVersion) &&
            (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R4)
            ? new()
            : null;

        if (igTr.VocabularyDir is null)
        {
            throw new Exception("VocabularyDir is null");
        }

        string dir = igTr.VocabularyDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing Value Sets for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // write the relevant externally-included value sets
        foreach (DbExternalInclusion inclusion in DbExternalInclusion.SelectEnumerable(_db, ResourceType: Hl7.Fhir.Model.FHIRAllTypes.ValueSet))
        {
            if ((inclusion.IncludeInPackages is not null) &&
                !inclusion.GetIncludeInPackagesList().Contains(igTr.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // write the code system to a file
            string filename = $"ValueSet-{inclusion.Id}";
            string path = Path.Combine(dir, filename + ".json");
            File.WriteAllText(path, inclusion.Json);

            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = inclusion.Name,
                Id = inclusion.Id,
                Url = inclusion.UnversionedUrl,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ValueSet.GetLiteral(),
                Version = inclusion.Version,
                Description = $"Externally-defined ValueSet: {inclusion.Name}",
            });
        }

        // get the value sets in the source package
        Dictionary<int, DbValueSet> sourceValueSets = DbValueSet.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // get the value sets in the target package
        Dictionary<int, DbValueSet> targetValueSets = DbValueSet.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.TargetPackageKey);

        // get the value set concepts in the source package
        Dictionary<int, DbValueSetConcept> sourceConcepts = DbValueSetConcept.SelectDict(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // get the value set comparisons for this package pair
        Dictionary<int, DbValueSetComparison> vsComparisons = DbValueSetComparison.SelectDict(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey);

        // get the value set outcomes for this package pair that need exporting
        List<DbValueSetOutcome> vsOutcomes = DbValueSetOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            RequiresXVerDefinition: true,
            orderByProperties: [nameof(DbValueSetOutcome.GenLongId)]);

        HashSet<string> vsIds = [];

        // iterate over the value set outcomes we need to export
        foreach (DbValueSetOutcome vsOutcome in vsOutcomes)
        {
            if (XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalVersioned) ||
                XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalUnversioned))
            {
                continue;
            }

            if (!sourceValueSets.TryGetValue(vsOutcome.SourceValueSetKey, out DbValueSet? sourceVs))
            {
                _logger.LogError($"Could not resolve source ValueSet `{vsOutcome.SourceCanonicalVersioned}`");
                continue;
            }

            DbValueSet? targetVs = null;
            if ((vsOutcome.TargetValueSetKey is not null) &&
                !targetValueSets.TryGetValue(vsOutcome.TargetValueSetKey.Value, out targetVs))
            {
                _logger.LogError($"Could not resolve target ValueSet `{vsOutcome.TargetCanonicalVersioned}`");
                continue;
            }

            // get the concept outcomes for this vs outcome that need a definition
            List<DbValueSetConceptOutcome> conceptOutcomes = DbValueSetConceptOutcome.SelectList(
                _db,
                ValueSetOutcomeKey: vsOutcome.Key,
                RequiresXVerDefinition: true,
                orderByProperties: [nameof(DbValueSetConceptOutcome.SourceSystem), nameof(DbValueSetConceptOutcome.SourceCode)]);

            if (conceptOutcomes.Count == 0)
            {
                // nothing to include
                continue;
            }

            List<DbValueSetConceptOutcome> nonGeneratedConceptOutcomes = DbValueSetConceptOutcome.SelectList(
                _db,
                ValueSetOutcomeKey: vsOutcome.Key,
                RequiresXVerDefinition: false,
                orderByProperties: [nameof(DbValueSetConceptOutcome.SourceSystem), nameof(DbValueSetConceptOutcome.SourceCode)]);

            List<DbElement> sourceVsBoundElements = DbElement.SelectList(
                _db,
                BindingValueSetKey: sourceVs.Key,
                orderByProperties: [nameof(DbElement.Id)]);

            string nonGeneratedOutcomeText;
            if (nonGeneratedConceptOutcomes.Count == 0)
            {
                nonGeneratedOutcomeText = "Note that all concepts are included in this cross-version definition because no concepts have compatible representations";
            }
            else if (nonGeneratedConceptOutcomes.Count <= 10)
            {
                nonGeneratedOutcomeText = "The following concepts are not included in this cross-version definition because they have valid representations:\n" +
                    $"\n* {string.Join("\n* ", nonGeneratedConceptOutcomes.Select(nco => $"`{nco.SourceSystem}#{nco.SourceCode}`"))}";
            }
            else
            {
                nonGeneratedOutcomeText = $"Note that there are {nonGeneratedConceptOutcomes.Count} concepts not included in this cross-version definition because they have valid representations.";
            }

            string techComments;
            if (vsOutcome.Comments.Length < 1000)
            {
                techComments = vsOutcome.Comments;
            }
            else
            {
                techComments = vsOutcome.Comments[..1000];
                int trimIndex = techComments.LastIndexOf('\n');
                if (trimIndex == -1)
                {
                    trimIndex = techComments.LastIndexOf(' ');
                }

                if (trimIndex == -1)
                {
                    techComments = techComments + "...";
                }
                else
                {
                    techComments = techComments[..trimIndex] + "...";
                }
            }

            string ? purpose = null;
            if ((igTr.PackagePair.Distance == 1) ||
                (vsOutcome.ValueSetComparisonKey is null))
            {
                purpose = $$$"""
                    This value set is part of the cross-version definitions generated to enable use of the
                    value set `{{{sourceVs.VersionedUrl}}}` as defined in FHIR {{{igTr.PackagePair.SourceFhirSequence}}}
                    in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}.

                    The source value set is bound to the following FHIR {{{igTr.PackagePair.SourceFhirSequence}}} elements:
                    * {{{string.Join("\n* ", sourceVsBoundElements.Select(ed => $"`{ed.Id}`"))}}}

                    {{{nonGeneratedOutcomeText}}}

                    Following are the generation technical comments:
                    {{{techComments}}}
                    """;
            }
            else
            {
                string? mappingTrace = null;

                if (vsComparisons.TryGetValue(vsOutcome.ValueSetComparisonKey!.Value, out DbValueSetComparison? vsComp))
                {
                    DbValueSet? r2Vs = vsComp.ContentKeyR2 is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR2.Value);
                    DbValueSet? r3Vs = vsComp.ContentKeyR3 is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR3.Value);
                    DbValueSet? r4Vs = vsComp.ContentKeyR4 is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR4.Value);
                    DbValueSet? r4bVs = vsComp.ContentKeyR4B is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR4B.Value);
                    DbValueSet? r5Vs = vsComp.ContentKeyR5 is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR5.Value);
                    DbValueSet? r6Vs = vsComp.ContentKeyR6 is null
                        ? null
                        : DbValueSet.SelectSingle(_db, Key: vsComp.ContentKeyR6.Value);

                    List<DbValueSet?> contentPath = (igTr.PackagePair.SourceFhirSequence < igTr.PackagePair.TargetFhirSequence)
                        ? [r2Vs, r3Vs, r4Vs, r4bVs, r5Vs, r6Vs]
                        : [r6Vs, r5Vs, r4bVs, r4Vs, r3Vs, r2Vs];

                    mappingTrace = "* " + string.Join("\n* ", contentPath.Where(vs => vs is not null).Select(vs => $"`{vs!.VersionedUrl}`"));
                }

                purpose = $$$"""
                    This value set is part of the cross-version definitions generated to enable use of the
                    value set `{{{sourceVs.VersionedUrl}}}` as defined in FHIR {{{igTr.PackagePair.SourceFhirSequence}}}
                    in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}.

                    The source value set is bound to the following FHIR {{{igTr.PackagePair.SourceFhirSequence}}} elements:
                    * {{{string.Join("\n* ", sourceVsBoundElements.Select(ed => $"`{ed.Id}` as {ed.ValueSetBindingStrength}"))}}}

                    Across FHIR versions, the value set has been mapped as:
                    {{{mappingTrace}}}

                    {{{nonGeneratedOutcomeText}}}

                    Following are the generation technical comments:
                    {{{techComments}}}
                    """;
            }

            (_, string name) = igTr.GetName(vsOutcome.GenName!, vsOutcome.GenLongId!);

            // create our vs
            ValueSet fhirVs = new()
            {
                Url = vsOutcome.GenUrl,
                Id = vsOutcome.GenLongId,
                Version = _exporter._crossDefinitionVersion,
                Name = name,
                Title = $"Cross-version ValueSet {igTr.PackagePair.SourceFhirSequence}.{sourceVs.Name} for use in FHIR {igTr.PackagePair.TargetFhirSequence}",
                Status = PublicationStatus.Active,
                Experimental = false,
                UseContext = sourceVs.UseContexts,
                Jurisdiction = sourceVs.Jurisdictions,
                DateElement = new FhirDateTime(_exporter._runTime),
                Description = (targetVs is null)
                    ? $"This cross-version ValueSet represents content from `{sourceVs.VersionedUrl}` for use in FHIR {igTr.PackagePair.TargetFhirSequence}."
                    : $"This cross-version ValueSet represents content from {sourceVs.VersionedUrl} for use in FHIR {igTr.PackagePair.TargetFhirSequence}" +
                        $" that is appropriate for use but unavailable in `{targetVs.VersionedUrl}`.",
                Purpose = purpose,
                Compose = new()
                {
                    Include = [],
                },
                Expansion = new()
                {
                    TimestampElement = new FhirDateTime(_exporter._runTime),
                    Contains = [],
                },
            };

            // check to see if we should set various root extensions
            if (sourceVs.FhirMaturity != null)
            {
                fhirVs.AddExtension(CommonDefinitions.ExtUrlFmm, new Integer(sourceVs.FhirMaturity));
            }

            // FHIR-I is the default WG responsible if none are specified
            string wg = CommonDefinitions.ResolveWorkgroup(sourceVs.WorkGroup, "fhir");

            // add the work group extension
            fhirVs.AddExtension(CommonDefinitions.ExtUrlWorkGroup, new Hl7.Fhir.Model.Code(wg));

            // ensure there is a publisher, use the WG if there is none
            fhirVs.Publisher = CommonDefinitions.WorkgroupNames[wg];

            // ensure there is a contact point - use the default WG unless there are multiple entries
            if ((fhirVs.Contact == null) || (fhirVs.Contact.Count < 2))
            {
                fhirVs.Contact = [
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

            // check for unexpandable value sets (are stuck using the compose - even though it is likely incorrect)
            if ((sourceVs.CanExpand == false) ||
                (sourceVs.ActiveConcreteConceptCount == 0))
            {
                // use the existing compose
                fhirVs.Compose = sourceVs.Compose;

                // will not have an expansion
                fhirVs.Expansion = null;
            }
            else
            {
                Dictionary<string, ValueSet.ConceptSetComponent> composeIncludes = [];

                HashSet<string> usedFhirKeys = [];

                // traverse concepts
                foreach (DbValueSetConceptOutcome conceptOutcome in conceptOutcomes)
                {
                    // resolve this concept
                    if (!sourceConcepts.TryGetValue(conceptOutcome.SourceValueSetConceptKey, out DbValueSetConcept? concept))
                    {
                        throw new Exception($"Failed to resolve concept with key {conceptOutcome.SourceValueSetConceptKey} for ValueSet outcome {vsOutcome.Key}!");
                    }

                    string composeKey = concept.System + "|" + concept.SystemVersion;

                    if (!composeIncludes.TryGetValue(composeKey, out ValueSet.ConceptSetComponent? composeInclude))
                    {
                        // create a new include for this concept
                        composeInclude = new()
                        {
                            System = concept.System,
                            Version = concept.SystemVersion,
                            Concept = [],
                        };
                        composeIncludes.Add(composeKey, composeInclude);
                        fhirVs.Compose.Include.Add(composeInclude);
                    }

                    if (!usedFhirKeys.Add(concept.FhirKey))
                    {
                        continue;
                    }

                    composeInclude.Concept.Add(new()
                    {
                        Code = concept.Code,
                        Display = concept.Display,
                    });

                    // add this concept to the expansion
                    fhirVs.Expansion.Contains.Add(new()
                    {
                        System = concept.System,
                        Version = concept.SystemVersion,
                        Code = concept.Code,
                        Display = concept.Display,
                    });
                }

                // add the compose includes to the value set
                fhirVs.Compose.Include = composeIncludes.Values.ToList();

                // if we have no concepts, do not write this value set
                if (composeIncludes.Count == 0)
                {
                    continue;
                }
            }

            fhirVs.cgAddPackageSource(igTr.PackageId, _exporter._crossDefinitionVersion, igTr.PackageUrl);

            // write the code system to a file
            string filename = vsOutcome.GenFileName ?? throw new ArgumentNullException(nameof(vsOutcome.GenFileName));
            string path = Path.Combine(dir, filename + ".json");
            
            if (exporterR3 is not null)
            {
                File.WriteAllText(path, exporterR3.ToJson(fhirVs, new SerializerSettings() { Pretty = true }));
            }
            else
            {
                File.WriteAllText(path, fhirVs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
            }

            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = fhirVs.Name ?? $"{vsOutcome.SourceName}_{vsOutcome.TargetName}",
                Id = fhirVs.Id,
                Url = fhirVs.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ValueSet.GetLiteral(),
                Version = fhirVs.Version,
                Description = fhirVs.Description ?? fhirVs.Title ?? $"ValueSet: {fhirVs.Url}|{fhirVs.Version}",
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} Value Sets for `{igTr.PackageId}`");
        igTr.ValueSetFiles = exported;
    }

    private void exportCodeSystems(XVerIgExportTrackingRecord igTr)
    {
        if (igTr.VocabularyDir is null)
        {
            throw new Exception("VocabularyDir is null");
        }

        string dir = igTr.VocabularyDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing Code Systems for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // write the relevant externally-included code systems
        foreach (DbExternalInclusion inclusion in DbExternalInclusion.SelectEnumerable(_db, ResourceType: Hl7.Fhir.Model.FHIRAllTypes.CodeSystem))
        {
            if ((inclusion.IncludeInPackages is not null) &&
                !inclusion.GetIncludeInPackagesList().Contains(igTr.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // write the code system to a file
            string filename = $"CodeSystem-{inclusion.Id}";
            string path = Path.Combine(dir, filename + ".json");
            File.WriteAllText(path, inclusion.Json);

            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = inclusion.Name,
                Id = inclusion.Id,
                Url = inclusion.UnversionedUrl,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.CodeSystem.GetLiteral(),
                Version = inclusion.Version,
                Description = $"Externally-defined CodeSystem: {inclusion.Name}",
            });
        }

        // get the list of code systems in the source sourcePackage
        List<DbCodeSystem> codeSystems = DbCodeSystem.SelectList(
            _db,
            FhirPackageKey: igTr.PackagePair.SourcePackageKey);

        // iterate over the code systems to them
        foreach (DbCodeSystem dbCs in codeSystems)
        {
            // skip code systems that are experimental and have 'example' in their id
            if ((dbCs.IsExperimental == true) &&
                dbCs.Id.Contains("example", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? overrideId = null;
            string? overrideName = null;

            // special case - need to reassign the ids for some code systems
            switch (dbCs.VersionedUrl)
            {
                case "http://terminology.hl7.org/CodeSystem/operation-outcome|2.0.0":
                    overrideId = "operation-outcome-tho";
                    overrideName = "OperationOutcomeTHO";
                    break;
            }

            (_, string name) = igTr.GetName(overrideName ?? dbCs.Name, overrideId ?? dbCs.Id);

            // create the FHIR CodeSystem
            CodeSystem fhirCs = new()
            {
                Id = overrideId ?? dbCs.Id,
                Url = dbCs.UnversionedUrl,
                Name = name,
                Version = dbCs.Version,
                VersionAlgorithm =
                    (dbCs.VersionAlgorithmString != null)
                    ? new FhirString(dbCs.VersionAlgorithmString)
                    : (dbCs.VersionAlgorithmCoding != null)
                    ? dbCs.VersionAlgorithmCoding
                    : null,
                Status = dbCs.Status,
                Title = dbCs.Title,
                Description = dbCs.Description,
                Purpose = dbCs.Purpose,
                Text = dbCs.Narrative,
                Experimental = dbCs.IsExperimental,
                DateElement = (dbCs.LastChangedDate != null) ? new FhirDateTime(dbCs.LastChangedDate.Value) : null,
                Publisher = dbCs.Publisher,
                Copyright = dbCs.Copyright,
                CopyrightLabel = dbCs.CopyrightLabel,
                ApprovalDate = dbCs.ApprovalDate,
                LastReviewDate = dbCs.LastReviewDate,
                EffectivePeriod = (dbCs.EffectivePeriodStart != null || dbCs.EffectivePeriodEnd != null)
                    ? new Period()
                    {
                        StartElement = (dbCs.EffectivePeriodStart != null) ? new FhirDateTime(dbCs.EffectivePeriodStart.Value) : null,
                        EndElement = (dbCs.EffectivePeriodEnd != null) ? new FhirDateTime(dbCs.EffectivePeriodEnd.Value) : null,
                    }
                    : null,
                Topic = dbCs.Topic,
                RelatedArtifact = dbCs.RelatedArtifacts,
                Jurisdiction = dbCs.Jurisdictions,
                UseContext = dbCs.UseContexts,
                Contact = dbCs.Contacts,
                Author = dbCs.Authors,
                Editor = dbCs.Editors,
                Reviewer = dbCs.Reviewers,
                CaseSensitive = dbCs.IsCaseSensitive,
                ValueSet = dbCs.ValueSetVersioned,
                HierarchyMeaning = dbCs.HierarchyMeaning,
                Compositional = dbCs.IsCompositional,
                VersionNeeded = dbCs.VersionNeeded,
                Content = dbCs.Content,
                Supplements = dbCs.SupplementsVersioned,
                Count = dbCs.Count,
            };

            // remove pre-R5 elements if we are in an earlier version
            if (igTr.PackagePair.TargetFhirSequence < FhirReleases.FhirSequenceCodes.R5)
            {
                fhirCs.ApprovalDate = null;
                fhirCs.LastReviewDate = null;
                fhirCs.EffectivePeriod = null;
                fhirCs.Topic = null;
                fhirCs.Author = null;
                fhirCs.Editor = null;
                fhirCs.Reviewer = null;
                fhirCs.RelatedArtifact = null;
            }

            string? wg = null;

            // add standard extensions
            if (dbCs.RootExtensions != null)
            {
                foreach (Hl7.Fhir.Model.Extension ext in dbCs.RootExtensions)
                {
                    switch (ext.Url)
                    {
                        case CommonDefinitions.ExtUrlWorkGroup:
                            {
                                switch (ext.Value)
                                {
                                    case FhirString fhirString:
                                        wg = fhirString.Value;
                                        break;
                                    case Hl7.Fhir.Model.Code code:
                                        wg = code.Value;
                                        break;
                                    case Markdown markdown:
                                        wg = markdown.Value;
                                        break;
                                    default:
                                        continue;
                                }
                            }
                            break;

                        case CommonDefinitions.ExtUrlPackageSource:
                            fhirCs.cgAddPackageSource(igTr.PackageId, _exporter._crossDefinitionVersion, igTr.PackageUrl);
                            break;

                        default:
                            // copy any extensions we have not specifically handled
                            fhirCs.Extension.Add(ext);
                            break;
                    }
                }
            }

            // default to fhir infrastructure work group if none is present
            wg = CommonDefinitions.ResolveWorkgroup(wg, "fhir");

            // add the work group extension
            fhirCs.AddExtension(CommonDefinitions.ExtUrlWorkGroup, new Hl7.Fhir.Model.Code(wg));

            // ensure the publisher matches the WG
            fhirCs.Publisher = CommonDefinitions.WorkgroupNames[wg];

            // ensure there is a contact point - use the default WG unless there are multiple entries
            if ((fhirCs.Contact == null) || (fhirCs.Contact.Count < 2))
            {
                fhirCs.Contact = [
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

            // add filters
            List<DbCodeSystemFilter> csFilters = DbCodeSystemFilter.SelectList(
                _db,
                CodeSystemKey: dbCs.Key);

            foreach (DbCodeSystemFilter dbFilter in csFilters)
            {
                fhirCs.Filter.Add(new CodeSystem.FilterComponent()
                {
                    Code = dbFilter.Code,
                    Description = dbFilter.Description,
                    Operator = dbFilter.Operators.Split('|').Select(op => EnumUtility.ParseLiteral<FilterOperator>(op, true)).ToList(),
                    Value = dbFilter.Value,
                });
            }

            // add property definitions
            List<DbCodeSystemPropertyDefinition> csPropertyDefinitions = DbCodeSystemPropertyDefinition.SelectList(
                _db,
                CodeSystemKey: dbCs.Key);

            foreach (DbCodeSystemPropertyDefinition dbPropDef in csPropertyDefinitions)
            {
                fhirCs.Property.Add(new CodeSystem.PropertyComponent()
                {
                    Code = dbPropDef.Code,
                    Uri = dbPropDef.Uri,
                    Description = dbPropDef.Description,
                    Type = dbPropDef.Type,
                });
            }

            // recursively add concepts
            addDbCodeSystemConcepts(fhirCs.Concept, dbCs.Key);

            // write the code system to a file
            string filename = $"CodeSystem-{fhirCs.Id}";
            string path = Path.Combine(dir, filename + ".json");
            File.WriteAllText(path, fhirCs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));

            exported.Add(new()
            {
                FileName = filename + ".json",
                FileNameWithoutExtension = filename,
                IsPageContentFile = false,
                Name = fhirCs.Name,
                Id = fhirCs.Id,
                Url = fhirCs.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.CodeSystem.GetLiteral(),
                Version = fhirCs.Version,
                Description = fhirCs.Description ?? fhirCs.Title ?? $"CodeSystem: {fhirCs.Url}|{fhirCs.Version}",
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} Code Systems for `{igTr.PackageId}`");

        igTr.CodeSystemFiles = exported;
    }

    private void addDbCodeSystemConcepts(
        List<CodeSystem.ConceptDefinitionComponent> concepts,
        int dbCsKey,
        int? parentConceptKey = null)
    {
        List<DbCodeSystemConcept> dbConcepts = (parentConceptKey == null)
            ? DbCodeSystemConcept.SelectList(
                _db,
                CodeSystemKey: dbCsKey,
                ParentConceptKeyIsNull: true,
                orderByProperties: [nameof(DbCodeSystemConcept.FlatOrder)])
            : DbCodeSystemConcept.SelectList(
                _db,
                CodeSystemKey: dbCsKey,
                ParentConceptKey: parentConceptKey.Value,
                orderByProperties: [nameof(DbCodeSystemConcept.FlatOrder)]);

        foreach (DbCodeSystemConcept dbConcept in dbConcepts)
        {
            // create the concept
            CodeSystem.ConceptDefinitionComponent fhirConcept = new CodeSystem.ConceptDefinitionComponent()
            {
                Code = dbConcept.Code,
                Display = dbConcept.Display,
                Definition = dbConcept.Definition,
                Designation = dbConcept.Designations,
                Property = dbConcept.Properties,
            };

            concepts.Add(fhirConcept);

            // recursively add child concepts
            if (dbConcept.ChildConceptCount != 0)
            {
                addDbCodeSystemConcepts(fhirConcept.Concept, dbCsKey, dbConcept.Key);
            }
        }
    }
}
