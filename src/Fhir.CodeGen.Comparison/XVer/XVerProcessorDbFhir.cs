using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Model.CdsHooks;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging;
using Octokit;
using static System.Runtime.InteropServices.JavaScript.JSType;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;
using Tasks = System.Threading.Tasks;

namespace Fhir.CodeGen.Comparison.XVer;

public partial class XVerProcessor
{
    private record class XVerIgFileRecord
    {
        [JsonPropertyName("filename")]
        public required string FileName { get; init; }

        [JsonIgnore]
        public required string FileNameWithoutExtension { get; init; }

        [JsonIgnore]
        public required bool IsPageContentFile { get; init; }

        [JsonIgnore]
        public required string Name { get; init; }

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public required string? Id { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public required string? Url { get; init; }

        [JsonPropertyName("resourceType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public required string? ResourceType { get; init; }

        /// <summary>
        /// Resource `version` value, if applicable *and* different from the IG itself (e.g., CodeSystem.version).
        /// </summary>
        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version { get; init; } = null;

        [JsonIgnore]
        public required string Description { get; init; }

        [JsonIgnore]
        public string? GroupingId { get; init; } = null;

        [JsonIgnore]
        public bool? IsExample { get; init; } = null;

        [JsonIgnore]
        public List<string>? Profiles { get; init; } = null;

        /// <summary>
        /// Resource `kind` value, if applicable (e.g., CodeSystem.kind).
        /// </summary>
        [JsonPropertyName("kind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? KindValue { get; init; } = null;

        /// <summary>
        /// Resource `type` value, if applicable (e.g., StructureDefinition.type).
        /// </summary>
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TypeValue { get; init; } = null;

        /// <summary>
        /// Resource `derivation` value, if applicable (e.g., StructureDefinition.derivation).
        /// </summary>
        [JsonPropertyName("derivation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DerivationValue { get; init; } = null;

        /// <summary>
        /// Resource `valueSet` value, if applicable (e.g., CodeSystem.valueSet).
        /// </summary>
        [JsonPropertyName("valueSet")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ValueSetValue { get; init; } = null;

        [JsonPropertyName("hasSnapshot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HasSnapshot { get; init; } = null;

        [JsonPropertyName("hasExpansion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HasExpansion { get; init; } = null;

        public PackageContents.PackageFile AsPackageFile() => new()
        {
            FileName = FileName,
            ResourceType = ResourceType,
            Id = Id,
            Url = Url,
            Version = Version,
            Kind = KindValue,
            Type = TypeValue,
            Derivation = DerivationValue,
        };
    }


    /// <summary>
    /// Represents index information for a cross-version FHIR sourcePackage, including references to supporting structures and value sets.
    /// </summary>
    private class XverPackageIndexInfo
    {
        /// <summary>
        /// Gets or sets the source sourcePackage support information.
        /// </summary>
        public required PackageXverSupport SourcePackageSupport { get; set; }

        /// <summary>
        /// Gets or sets the target sourcePackage support information.
        /// </summary>
        public required PackageXverSupport TargetPackageSupport { get; set; }

        /// <summary>
        /// Gets or sets the unique sourcePackage identifier for this cross-version sourcePackage.
        /// </summary>
        public required string PackageId { get; set; }

        public List<XVerIgFileRecord> ExtensionFiles { get; set; } = [];
        public List<XVerIgFileRecord> ProfileFiles { get; set; } = [];
        public List<XVerIgFileRecord> CodeSystemFiles { get; set; } = [];
        public List<XVerIgFileRecord> ValueSetFiles { get; set; } = [];

        public List<XVerIgFileRecord> ResourceLookupFiles { get; set; } = [];
        public List<XVerIgFileRecord> ValueSetLookupFiles { get; set; } = [];

        public XVerIgFileRecord? IgIndexFile { get; set; } = null;

        public PackageContents AsPackageContents()
        {
            if (IgIndexFile is null)
            {
                throw new Exception("IG Index file is required to create PackageContents.");
            }

            List<PackageContents.PackageFile> files = [
                IgIndexFile.AsPackageFile(),
                ];

            files.AddRange(CodeSystemFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ValueSetFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ExtensionFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ProfileFiles.Select(f => f.AsPackageFile()));

            return new PackageContents()
            {
                IndexVersion = 2,
                Files = files,
            };
        }
    }

    private static Dictionary<string, string> _publisherScripts = [];
    private static Lock _publisherScriptsLock = new();

    public void WriteFhirFromDbOutcomes(string? version = null, string? outputDir = null)
    {
        // check for no database
        if (_db == null)
        {
            throw new Exception("Cannot generate FHIR artifacts without a loaded database!");
        }

        // prefer the map path if it is set
        if (!string.IsNullOrEmpty(_config.CrossVersionMapSourcePath))
        {
            outputDir = _config.CrossVersionMapSourcePath;
        }
        else
        {
            outputDir ??= _config.OutputDirectory;
        }

        // check for no output location
        if (string.IsNullOrEmpty(outputDir))
        {
            throw new Exception("Cannot write FHIR artifacts without output or map source folder!");
        }

        string fhirDir = Path.Combine(outputDir, "fhir");
        if (Directory.Exists(fhirDir))
        {
            Directory.Delete(fhirDir, true);
        }

        Directory.CreateDirectory(fhirDir);

        if (string.IsNullOrEmpty(version))
        {
            _crossDefinitionVersion = _config.XverArtifactVersion;
        }
        else
        {
            _crossDefinitionVersion = version;
        }

        _logger.LogInformation($"Writing cross-version FHIR artifacts to {fhirDir} with version {_crossDefinitionVersion}");

        // grab the FHIR Packages we are processing
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(_db.DbConnection, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);
        ConcurrentDictionary<int, string> differentialVsBySourceKey = [];

        // iterate across the packages as source packages
        foreach ((DbFhirPackage sourcePackage, int index) in packages.Select((p, i) => (p, i)))
        {
            _logger.LogInformation($"Processing source package {index + 1} of {packages.Count}: {sourcePackage.ShortName}");

            // starting from the current package, iterate over earlier packages as target packages
            if (index > 0)
            {
                for (int targetIndex = index - 1; targetIndex >= 0; targetIndex--)
                {
                    DbFhirPackage targetPackage = packages[targetIndex];
                    _logger.LogInformation($"  Processing target package: {targetPackage.ShortName}");

                    // build our package id
                    string packageId = getPackageId(sourcePackage, targetPackage);

                    // create the export package directory
                    string packageDir = createExportPackageDir(fhirDir, sourcePackage, targetPackage);

                    // write all of the source-defined code systems
                    List<XVerIgFileRecord> csFiles = writeXverCodeSystems(sourcePackage, targetPackage, packageId, packageDir);

                    // write the differential value sets
                    List<XVerIgFileRecord> vsFiles = writeXverValueSets(
                        sourcePackage,
                        targetPackage,
                        packageId,
                        packageDir);

                    List<XVerIgFileRecord> sdFiles = writeXverStructures(
                        sourcePackage,
                        targetPackage,
                        packageId,
                        packageDir);
                }
            }

            // starting from the current package, iterate over later packages as target packages
            if (index < packages.Count - 1)
            {
                for (int targetIndex = index + 1; targetIndex < packages.Count; targetIndex++)
                {
                    DbFhirPackage targetPackage = packages[targetIndex];
                    _logger.LogInformation($"  Processing target package: {targetPackage.ShortName}");
                }
            }
        }
    }

    private List<XVerIgFileRecord> writeXverStructures(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        string packageId,
        string packageDir)
    {
        throw new NotImplementedException("Transitioning outcome structures...");
#if false
        _logger.LogInformation($"  Writing Value Sets for: {packageId}");

        string extensionDir = _config.XverExportForPublisher
            ? Path.Combine(packageDir, "input", "extensions")
            : Path.Combine(packageDir, "package");

        if (!Directory.Exists(extensionDir))
        {
            Directory.CreateDirectory(extensionDir);
        }

        string profileDir = _config.XverExportForPublisher
            ? Path.Combine(packageDir, "input", "profiles")
            : Path.Combine(packageDir, "package");

        if (!Directory.Exists(profileDir))
        {
            Directory.CreateDirectory(profileDir);
        }

        List<XVerIgFileRecord> exported = [];

        // traverse the list of structure outcomes for this package pair
        foreach (DbStructureOutcome outcome in
            DbStructureOutcome.SelectEnumerable(
                _db!.DbConnection,
                SourceFhirPackageKey: sourcePackage.Key,
                TargetFhirPackageKey: targetPackage.Key))
        {
            // resolve the database structure record
            DbStructureDefinition? sourceSd = DbStructureDefinition.SelectSingle(
                _db.DbConnection,
                Key: outcome.SourceStructureKey);

            if (sourceSd is null)
            {
                throw new Exception($"Could not find source Structure with key {outcome.SourceStructureKey} for outcome record {outcome.Key}!");
            }

            DbStructureDefinition? targetSd = outcome.TargetStructureKey is null
                ? null
                : DbStructureDefinition.SelectSingle(
                    _db.DbConnection,
                    Key: outcome.TargetStructureKey);

            // if the target is not null, we want to make a profile
            StructureDefinition? fhirProfileSd = null;
            if (targetSd is not null)
            {
                // TODO: create profile
                fhirProfileSd = new()
                {
                };
            }

            // each source element only needs to be processed once per source:target pair - generation takes all outcomes into account
            HashSet<int> processedSourceElements = [];

            // get the list of element outcomes for this structure outcome that need extensions
            List<DbElementOutcome> elementOutcomes = DbElementOutcome.SelectList(
                _db.DbConnection,
                StructureOutcomeKey: outcome.Key,
                OutcomeAction: OutcomeElementActionCodes.UseExtension,
                orderByProperties: [nameof(DbElementOutcome.SourceResourceOrder)]);

            foreach (DbElementOutcome elementOutcome in elementOutcomes)
            {
                // skip elements that do not need extensions
                if (elementOutcome.OutcomeAction != OutcomeElementActionCodes.UseExtension)
                {
                    continue;
                }

                // only process elements that have not been processed here
                if (!processedSourceElements.Add(elementOutcome.SourceElementKey))
                {
                    continue;
                }

                StructureDefinition extensionSd = createExtension(
                    sourcePackage,
                    targetPackage,
                    packageId,
                    packageDir,
                    outcome,
                    sourceSd,
                    targetSd,
                    elementOutcome);

                // check for substitution



            }

        }


        return exported;
#endif
    }

    private StructureDefinition createExtension(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        string packageId,
        string packageDir,
        DbStructureOutcome structureOutcome,
        DbStructureDefinition sourceSd,
        DbStructureDefinition? targetSd,
        DbElementOutcome elementOutcome)
    {
        throw new NotImplementedException("Transitioning outcome structures...");
#if false
        // resolve the the element definitions
        DbElement sourceElement = DbElement.SelectSingle(_db!.DbConnection, Key: elementOutcome.SourceElementKey)
            ?? throw new Exception($"Failed to resolve source element with key {elementOutcome.SourceElementKey} for outcome {elementOutcome.Key}!");

        DbElement? targetElement = elementOutcome.TargetElementKey is null
            ? null
            : DbElement.SelectSingle(_db.DbConnection, Key: elementOutcome.TargetElementKey);

        // get all the outcomes for this source element into this target package that have a target element
        List<DbElementOutcome> elementOutcomesWithTargets = DbElementOutcome.SelectList(
            _db.DbConnection,
            SourceElementKey: sourceElement.ParentElementKey!.Value,
            TargetFhirPackageKey: targetPackage.Key,
            TargetIdIsNull: false);

        List<DbElement> contextElements = [];
        List<StructureDefinition.ContextComponent> contexts = [];

        // determine the contexts for this extensions
        if (sourceElement.ResourceFieldOrder == 0)
        {
            // if we have a target structure, that is the context
            if (targetSd is not null)
            {
                // use the target structure as the context
                contexts.Add(new()
                {
                    Type = StructureDefinition.ExtensionContextType.Element,
                    Expression = targetSd.Name,
                });

                // resolve the root element for the target structure so we have it later
                DbElement targetRootElement = DbElement.SelectSingle(
                    _db.DbConnection,
                    StructureKey: targetSd.Key,
                    ResourceFieldOrder: 0)
                    ?? throw new Exception($"Failed to resolve root element for target StructureDefinition with key {targetSd.Key}!");

                contextElements.Add(targetRootElement);
            }
        }
        else if (elementOutcomesWithTargets.Count == 0)
        {
            if (targetSd is not null)
            {
                // use the target structure as the context
                contexts.Add(new()
                {
                    Type = StructureDefinition.ExtensionContextType.Element,
                    Expression = targetSd.Name,
                });

                // resolve the root element for the target structure so we have it later
                DbElement targetRootElement = DbElement.SelectSingle(
                    _db.DbConnection,
                    StructureKey: targetSd.Key,
                    ResourceFieldOrder: 0)
                    ?? throw new Exception($"Failed to resolve root element for target StructureDefinition with key {targetSd.Key}!");

                contextElements.Add(targetRootElement);
            }
        }
        else
        {
            HashSet<string> usedContextPaths = [];
            foreach (DbElementOutcome contextOutcome in elementOutcomesWithTargets)
            {
                if (!usedContextPaths.Add(contextOutcome.TargetId!))
                {
                    continue;
                }

                // resolve the element
                DbElement targetContextElement = DbElement.SelectSingle(
                    _db.DbConnection,
                    Key: contextOutcome.TargetElementKey!)
                    ?? throw new Exception($"Failed to resolve target context element with key {contextOutcome.TargetElementKey} for outcome {contextOutcome.Key}!");

                contexts.Add(new()
                {
                    Type = StructureDefinition.ExtensionContextType.Element,
                    Expression = targetContextElement.Path,
                });
                contextElements.Add(targetContextElement);
            }
        }
        
        if (contexts.Count == 0)
        {
            // if there is no known target, this is an extension that can appear anywhere (e.g., datatype)
            contexts.Add(new()
            {
                Type = StructureDefinition.ExtensionContextType.Element,
                Expression = "Element",
            });
        }

        bool allowModifier = false;

        // if the extension wants to be a modifier, check the contexts
        if (sourceElement.IsModifier == true)
        {
            allowModifier = true;

            /*
             * Determine if this extension should really be a modifier based on context:
             *  * Modifier element -> Modifier element : extension
             *  * Modifier element -> Backbone element (not modifier) : modifier extension
             *  * Modifier element -> Primitive-type element (not modifier) : modifier extension, context moves up a level
             *  * Modifier element -> Primitive-type element (array, not modifier) : currently unresolvable, but also has not happened yet
             */

            List<(DbElement original, DbElement replacement)> ctxElementReplacements = [];
            foreach (DbElement contextElement in contextElements)
            {
                // check if the source element is a modifier - do not need a modifier extension
                if (contextElement.IsModifier)
                {
                    allowModifier = false;
                    continue;
                }

                // check to see if the context element has children, can stay how it is
                if (contextElement.ChildElementCount > 0)
                {
                    continue;
                }

                // if element has ONLY primitive types, we need to move the context up a level
                if (_db!.DbConnection.ElementHasNonPrimitiveTypes(contextElement) == false)
                {
                    if (contextElement.ParentElementKey == null)
                    {
                        throw new Exception($"Cannot have a root element with only primitive types!");
                    }

                    DbElement replacement = DbElement.SelectSingle(_db!.DbConnection, Key: contextElement.ParentElementKey)
                        ?? throw new Exception($"Could not resolve Element: {contextElement.ParentElementKey} (parent of Element: {sourceElement.Key} - '{sourceElement.Id}')");

                    ctxElementReplacements.Add((contextElement, replacement));
                }
            }

            // resolve any context replacements
            if (ctxElementReplacements.Count != 0)
            {
                foreach ((DbElement original, DbElement replacement) in ctxElementReplacements)
                {
                    // look for the matching extension context
                    foreach (StructureDefinition.ContextComponent ctx in contexts)
                    {
                        if (ctx.Expression != original.Path)
                        {
                            continue;
                        }

                        ctx.Expression = replacement.Path;
                    }
                }
            }
        }

        // build the initial scaffold for the extension
        StructureDefinition extSd = new()
        {
            Id = elementOutcome.PotentialGenLongId,
            Url = elementOutcome.PotentialGenUrl,
            Name = FhirSanitizationUtils.ReformatIdForName(elementOutcome.PotentialGenLongId!),
            Version = _crossDefinitionVersion,
            Date = _runTime.ToString("O"),
            Title = $"Cross-version Extension {sourcePackage.ShortName}.{sourceSd.Name}.{sourceElement.Id} for use in FHIR {targetPackage.ShortName}",
            Description = $"This cross-version extension represents {sourceElement.Path} from {sourceSd.VersionedUrl} for use in FHIR {targetPackage.ShortName}.",
            Purpose = elementOutcome.Comments,
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

        extSd.cgAddPackageSource(packageId, _crossDefinitionVersion, $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{packageId}");

        // need to find the difference between the source element and *all* the elements is is mapped to
        // thinking an extension function to use the DB and select the element types and target types not found from
        // the source element or provided list and the list of target elements.

        //elementOutcomesWithTargets



        // get the list of outcomes that are sub-extensions for this extension
        List<DbElementOutcome> subElementOutcomes = DbElementOutcome.SelectList(
            _db.DbConnection,
            StructureOutcomeKey: structureOutcome.Key,
            OutcomeAction: OutcomeElementActionCodes.UseExtensionFromAncestor,
            RelatedAncestorOutcomeKey: elementOutcome.Key,
            orderByProperties: [nameof(DbElementOutcome.SourceResourceOrder)]);



        return extSd;
#endif
    }


    private List<XVerIgFileRecord> writeXverValueSets(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        string packageId,
        string packageDir)
    {
        throw new NotImplementedException("Transitioning outcome structures...");
#if false
        _logger.LogInformation($"  Writing Value Sets for: {packageId}");

        string vocabDir = _config.XverExportForPublisher
            ? Path.Combine(packageDir, "input", "vocabulary")
            : Path.Combine(packageDir, "package");

        if (!Directory.Exists(vocabDir))
        {
            Directory.CreateDirectory(vocabDir);
        }

        List<XVerIgFileRecord> exported = [];

        // write the relevant externally-included value sets
        foreach (DbExternalInclusion inclusion in DbExternalInclusion.SelectEnumerable(_db!.DbConnection, ResourceType: Hl7.Fhir.Model.FHIRAllTypes.ValueSet))
        {
            if ((inclusion.IncludeInPackages is not null) &&
                !inclusion.GetIncludeInPackagesList().Contains(packageId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // write the code system to a file
            string filename = $"ValueSet-{inclusion.Id}.json";
            string path = Path.Combine(vocabDir, filename);
            File.WriteAllText(path, inclusion.Json);

            exported.Add(new()
            {
                FileName = filename,
                FileNameWithoutExtension = filename[..^5],
                IsPageContentFile = false,
                Name = inclusion.Name,
                Id = inclusion.Id,
                Url = inclusion.UnversionedUrl,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ValueSet.GetLiteral(),
                Version = inclusion.Version,
                Description = $"Externally-defined ValueSet: {inclusion.Name}",
            });
        }

        // traverse the list of value set outcomes for this package pair
        foreach (DbValueSetOutcome outcome in
            DbValueSetOutcome.SelectEnumerable(
                _db!.DbConnection,
                SourceFhirPackageKey: sourcePackage.Key,
                TargetFhirPackageKey: targetPackage.Key))
        {
            // skip records we do not need to generate for
            if ((outcome.OutcomeAction == OutcomeValueSetActionCodes.UseValueSetSameName) ||
                (outcome.OutcomeAction == OutcomeValueSetActionCodes.UseValueSetRenamed))
            {
                continue;
            }

            // resolve the database value set record
            DbValueSet? sourceVs = DbValueSet.SelectSingle(
                _db.DbConnection,
                Key: outcome.SourceValueSetKey);

            if (sourceVs is null)
            {
                throw new Exception($"Could not find source ValueSet with key {outcome.SourceValueSetKey} for outcome record {outcome.Key}!");
            }

            DbValueSet? targetVs = outcome.TargetValueSetKey is null
                ? null
                : DbValueSet.SelectSingle(
                    _db.DbConnection,
                    Key: outcome.TargetValueSetKey);

            ValueSet fhirVs = new()
            {
                Url = outcome.PotentialGenUrl,
                Id = outcome.PotentialGenLongId,
                Version = _crossDefinitionVersion,
                Name = FhirSanitizationUtils.ReformatIdForName(outcome.PotentialGenLongId!),
                Title = $"Cross-version ValueSet {sourcePackage.ShortName}.{sourceVs.Name} for use in FHIR {targetPackage.ShortName}",
                Status = PublicationStatus.Active,
                Experimental = false,
                UseContext = sourceVs.UseContexts,
                Jurisdiction = sourceVs.Jurisdictions,
                DateElement = new FhirDateTime(DateTimeOffset.Now),
                Description = (targetVs is null)
                    ? $"This cross-version ValueSet represents content from {sourceVs.VersionedUrl} for use in FHIR {targetPackage.ShortName}."
                    : $"This cross-version ValueSet represents content from {sourceVs.VersionedUrl} for use in FHIR {targetPackage.ShortName}" +
                        $" that is appropriate for use but unavailable in {targetVs.VersionedUrl}.",
                Compose = new()
                {
                    Include = [],
                },
                Expansion = new()
                {
                    TimestampElement = new FhirDateTime(DateTimeOffset.Now),
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

                // traverse concepts
                foreach (DbValueSetConceptOutcome conceptOutcome in
                    DbValueSetConceptOutcome.SelectEnumerable(
                        _db.DbConnection,
                        ValueSetOutcomeKey: outcome.Key))
                {
                    // ignore concepts that should not be included
                    if ((conceptOutcome.OutcomeAction == OutcomeValueSetConceptActionCodes.UseConceptSameCode) ||
                        (conceptOutcome.OutcomeAction == OutcomeValueSetConceptActionCodes.UseConceptChangedCode) ||
                        (conceptOutcome.OutcomeAction == OutcomeValueSetConceptActionCodes.MappedElsewhere))
                    {
                        continue;
                    }

                    // resolve this concept
                    DbValueSetConcept concept = DbValueSetConcept.SelectSingle(
                        _db.DbConnection,
                        Key: conceptOutcome.SourceValueSetConceptKey)
                        ?? throw new Exception($"Failed to resolve concept with key {conceptOutcome.SourceValueSetConceptKey} for ValueSet outcome {outcome.Key}!");

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

            fhirVs.cgAddPackageSource(packageId, _crossDefinitionVersion, null);

            // write the code system to a file
            string filename = $"ValueSet-{fhirVs.Id}.json";
            string path = Path.Combine(vocabDir, filename);
            File.WriteAllText(path, fhirVs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));

            exported.Add(new()
            {
                FileName = filename,
                FileNameWithoutExtension = filename[..^5],
                IsPageContentFile = false,
                Name = fhirVs.Name,
                Id = fhirVs.Id,
                Url = fhirVs.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.ValueSet.GetLiteral(),
                Version = fhirVs.Version,
                Description = fhirVs.Description ?? fhirVs.Title ?? $"ValueSet: {fhirVs.Url}|{fhirVs.Version}",
            });
        }

        return exported;
#endif
    }

    private List<XVerIgFileRecord> writeXverCodeSystems(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        string packageId,
        string packageDir)
    {
        _logger.LogInformation($"  Writing Code Systems for: {packageId}");

        string vocabDir = _config.XverExportForPublisher
            ? Path.Combine(packageDir, "input", "vocabulary")
            : Path.Combine(packageDir, "package");

        if (!Directory.Exists(vocabDir))
        {
            Directory.CreateDirectory(vocabDir);
        }

        List<XVerIgFileRecord> exported = [];

        // write the relevant externally-included code systems
        foreach (DbExternalInclusion inclusion in DbExternalInclusion.SelectEnumerable(_db!.DbConnection, ResourceType: Hl7.Fhir.Model.FHIRAllTypes.CodeSystem))
        {
            if ((inclusion.IncludeInPackages is not null) &&
                !inclusion.GetIncludeInPackagesList().Contains(packageId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // write the code system to a file
            string filename = $"CodeSystem-{inclusion.Id}.json";
            string path = Path.Combine(vocabDir, filename);
            File.WriteAllText(path, inclusion.Json);

            exported.Add(new()
            {
                FileName = filename,
                FileNameWithoutExtension = filename[..^5],
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
            _db!.DbConnection,
            FhirPackageKey: sourcePackage.Key);


        // iterate over the code systems to them
        foreach (DbCodeSystem dbCs in codeSystems)
        {
            // create the FHIR CodeSystem
            CodeSystem fhirCs = new()
            {
                Id = dbCs.Id,
                Url = dbCs.UnversionedUrl,
                Name = dbCs.Name,
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
            if (targetPackage.DefinitionFhirSequence < FhirReleases.FhirSequenceCodes.R5)
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
                            fhirCs.cgAddPackageSource(packageId, _crossDefinitionVersion, null);
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
                _db!.DbConnection,
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
                _db!.DbConnection,
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
            string filename = $"CodeSystem-{fhirCs.Id}.json";
            string path = Path.Combine(vocabDir, filename);
            File.WriteAllText(path, fhirCs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));

            exported.Add(new()
            {
                FileName = filename,
                FileNameWithoutExtension = filename[..^5],
                IsPageContentFile = false,
                Name = fhirCs.Name,
                Id = fhirCs.Id,
                Url = fhirCs.Url,
                ResourceType = Hl7.Fhir.Model.FHIRAllTypes.CodeSystem.GetLiteral(),
                Version = fhirCs.Version,
                Description = fhirCs.Description ?? fhirCs.Title ?? $"CodeSystem: {fhirCs.Url}|{fhirCs.Version}",
            });
        }

        return exported;
    }














    /// <summary>
    /// Generates cross-version FHIR artifacts from the loaded database, including CodeSystems,
    /// ValueSets, StructureDefinitions, and ImplementationGuides.
    /// </summary>
    /// <param name="version">Optional artifact version to use; if null, uses the configured artifact version.</param>
    /// <param name="outputDir">Optional output directory; if null, uses the configured map source path.</param>
    /// <exception cref="Exception">
    /// Thrown if the database is not loaded or if the output directory is not specified.
    /// </exception>
    [Obsolete]
    public void WriteFhirFromDatabase(string? version = null, string? outputDir = null)
    {
        // check for no database
        if (_db == null)
        {
            throw new Exception("Cannot generate FHIR artifacts without a loaded database!");
        }

        // prefer the map path if it is set
        if (!string.IsNullOrEmpty(_config.CrossVersionMapSourcePath))
        {
            outputDir = _config.CrossVersionMapSourcePath;
        }
        else
        {
            outputDir ??= _config.OutputDirectory;
        }

        // check for no output location
        if (string.IsNullOrEmpty(outputDir))
        {
            throw new Exception("Cannot write FHIR artifacts without output or map source folder!");
        }

        string fhirDir = Path.Combine(outputDir, "fhir");
        if (Directory.Exists(fhirDir))
        {
            Directory.Delete(fhirDir, true);
        }

        Directory.CreateDirectory(fhirDir);

        if (string.IsNullOrEmpty(version))
        {
            _crossDefinitionVersion = _config.XverArtifactVersion;
        }
        else
        {
            _crossDefinitionVersion = version;
        }

        _logger.LogInformation($"Writing cross-version FHIR artifacts to {fhirDir} with version {_crossDefinitionVersion}");

        // grab the FHIR Packages we are processing
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(_db.DbConnection, orderByProperties: [nameof(DbFhirPackage.ShortName)]);
        List<FhirPackageComparisonPair> packageComparisonPairs = FhirPackageComparisonPair.GetPairs(packages)
            .OrderBy(p => p.SortKey)
            .ToList();

        ConcurrentDictionary<int, string> differentialVsBySourceKey = [];

        Dictionary<int, HashSet<string>> basicElementPathsByPackageKey = [];
        Dictionary<(int sourcePackageIndex, string sdName), List<List<XverOutcome>>> xverOutcomes = [];

        List<PackageXverSupport> packageSupports = [];

        List<XverPackageIndexInfo> allIndexInfos = [];

        // iterate over the packages to build the Basic resource element paths
        foreach ((DbFhirPackage package, int index) in packages.Select((p, i) => (p, i)))
        {
            // need to create a definition collection with the matching core sourcePackage so that we can build everything
            string packageDirective = $"{package.PackageId}#{package.PackageVersion}";

            // create a loader because these are all different FHIR core versions
            using Lib.Loader.PackageLoader loader = new(_config, new()
            {
                JsonModel = Lib.Loader.LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });

            //DefinitionCollection coreDc = loader.LoadPackages([packageDirective]).Result
            //    ?? throw new Exception($"Could not load sourcePackage: {packageDirective}");

            PackageXverSupport packageSupport = new()
            {
                PackageIndex = index,
                Package = package,
                BasicElements = [],
                //CoreDC = coreDc,
                //SnapshotGenerator = new(coreDc),
            };

            packageSupports.Add(packageSupport);

            // check for a basic structure
            DbStructureDefinition? basicResource = DbStructureDefinition.SelectSingle(
                _db.DbConnection,
                FhirPackageKey: package.Key,
                Name: "Basic",
                ArtifactClass: FhirArtifactClassEnum.Resource);
            if (basicResource != null)
            {
                // get the elements for this structure
                List<DbElement> basicElements = DbElement.SelectList(
                    _db.DbConnection,
                    StructureKey: basicResource.Key);

                // iterate over the elements
                foreach (DbElement element in basicElements)
                {
                    // skip root, elements with empty paths, and `code` element
                    if ((element.ResourceFieldOrder == 0) ||
                        string.IsNullOrEmpty(element.Path) ||
                        element.Path.Equals("Basic.code", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // add the path to the dictionary, but strip "Basic" from the front
                    packageSupport.BasicElements.Add(element.Path.Substring(5), element.BasePath);
                }
            }

            // check for an extension structure
            DbStructureDefinition? extensionStructure = DbStructureDefinition.SelectSingle(
                _db.DbConnection,
                FhirPackageKey: package.Key,
                Name: "Extension",
                ArtifactClass: FhirArtifactClassEnum.ComplexType);

            if (extensionStructure != null)
            {
                // check for the value[x] element
                DbElement? extValueElement = DbElement.SelectSingle(
                    _db.DbConnection,
                    FhirPackageKey: package.Key,
                    StructureKey: extensionStructure.Key,
                    Id: "Extension.value[x]");

                if (extValueElement != null)
                {
                    // get the types for this element
                    List<DbElementType> extValueTypes = DbElementType.SelectList(
                        _db.DbConnection,
                        ElementKey: extValueElement.Key);

                    // iterate over the types
                    foreach (DbElementType extValueType in extValueTypes)
                    {
                        if (!string.IsNullOrEmpty(extValueType.TypeName))
                        {
                            packageSupport.AllowedExtensionTypes.Add(extValueType.TypeName);
                        }
                    }
                }
            }
        }

        // iterate over the list of packages
        for (int focusPackageIndex = 0; focusPackageIndex < packages.Count; focusPackageIndex++)
        {
            // ignore DSTU2 for now
            //if (packageSupports[focusPackageIndex].Package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.DSTU2)
            //{
            //    continue;
            //}

            //if (focusPackageIndex != packages.Count - 1)
            //{
            //    continue;
            //}

            _logger.LogInformation($"Processing source package {focusPackageIndex + 1} of {packages.Count}: {packages[focusPackageIndex].ShortName}");

            Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets = buildXverValueSets(packages, focusPackageIndex);

            writeXverValueSets(packages, focusPackageIndex, xverValueSets, fhirDir);

            Dictionary<(int sourceElementKey, int targetPackageId), (StructureDefinition, DbExtensionSubstitution?)> xverExtensions = [];
            Dictionary<(int sourceStructureKey, int targetPackageId), StructureDefinition> xverProfiles = [];

            buildXverStructures(packageSupports, focusPackageIndex, xverValueSets, xverExtensions, xverProfiles, xverOutcomes, FhirArtifactClassEnum.ComplexType);
            buildXverStructures(packageSupports, focusPackageIndex, xverValueSets, xverExtensions, xverProfiles, xverOutcomes, FhirArtifactClassEnum.Resource);

            writeXverStructures(packageSupports, focusPackageIndex, xverExtensions, xverProfiles, fhirDir);

            // write the source code systems for this sourcePackage
            Dictionary<(int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)> xverCodeSystems = writeXverSourceCodeSystemsFromDb(
                packages,
                focusPackageIndex,
                fhirDir);

            if (_config.XverExportForPublisher)
            {
                List<XverPackageIndexInfo> focusedInfos = buildInitialPackageInfo(
                    packageSupports,
                    focusPackageIndex,
                    xverValueSets,
                    xverCodeSystems,
                    xverExtensions,
                    xverProfiles,
                    fhirDir);
                allIndexInfos.AddRange(focusedInfos);
            }
        }

        // write all of our outcome lists
        writeXverOutcomes(
            packageSupports,
            xverOutcomes,
            allIndexInfos,
            outputDir);

        // write our combined sourcePackage support files
        if (_config.XverExportForPublisher)
        {
            // write the individual sourcePackage support files
            for (int focusPackageIndex = 0; focusPackageIndex < packages.Count; focusPackageIndex++)
            {
                // write the publisher config files
                writePublisherSinglePackageConfig(packageSupports, focusPackageIndex, fhirDir, allIndexInfos);
            }

            // write the validation sourcePackage support files
            writePublisherValidationPackageConfig(packageSupports, allIndexInfos, fhirDir);
        }
        else
        {
            writeXverValidationPackageSupportFiles(packageSupports, allIndexInfos, fhirDir);
        }

        if ((_config.XverExportForPublisher == false) &&
            (_config.XverGenerateNpms == true))
        {
            // make the make sourcePackage tgz files
            foreach (DbFhirPackage focusPackage in packages)
            {
                // TODO: until verified, only write R4 and later packages
                if ((focusPackage.ShortName == "R2") ||
                    (focusPackage.ShortName == "R3"))
                {
                    continue;
                }

                string validationPackageId = $"hl7.fhir.uv.xver.{focusPackage.ShortName.ToLowerInvariant()}";

                // create the validation sourcePackage
                createTgzFromDirectory(
                    Path.Combine(fhirDir, focusPackage.ShortName),
                    Path.Combine(fhirDir, $"{validationPackageId}.{_crossDefinitionVersion}.tgz"));

                // look for all combination packages, using the focus as the target
                foreach (DbFhirPackage sourcePackage in packages)
                {
                    if (sourcePackage.Key == focusPackage.Key)
                    {
                        continue;
                    }

                    string packageId = $"hl7.fhir.uv.xver-{sourcePackage.ShortName.ToLowerInvariant()}.{focusPackage.ShortName.ToLowerInvariant()}";

                    // create the validation sourcePackage
                    createTgzFromDirectory(
                        Path.Combine(fhirDir, $"{sourcePackage.ShortName}-for-{focusPackage.ShortName}"),
                        Path.Combine(fhirDir, $"{packageId}.{_crossDefinitionVersion}.tgz"));
                }
            }
        }
    }

    [Obsolete]
    private Dictionary<(int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)> writeXverSourceCodeSystemsFromDb(
        List<DbFhirPackage> packages,
        int focusPackageIndex,
        string fhirDir)
    {
        Dictionary<(int sourceCsKey, int targetPackageId), (CodeSystem? cs, int? externalInclusionKey)> xverCodeSystems = [];

        DbFhirPackage focusPackage = packages[focusPackageIndex];

        // iterate over the target packages
        foreach (DbFhirPackage targetPackage in packages)
        {
            if (focusPackage.Key == targetPackage.Key)
            {
                continue;
            }

            string packageId = getPackageId(focusPackage, targetPackage);

            string dir = createExportPackageDir(fhirDir, focusPackage, targetPackage);
            dir = _config.XverExportForPublisher
                ? Path.Combine(dir, "input", "vocabulary")
                : Path.Combine(dir, "package");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // write any external inclusion code systems
            List<DbExternalInclusion> externalInclusions = DbExternalInclusion.SelectList(
                _db!.DbConnection,
                ResourceType: Hl7.Fhir.Model.FHIRAllTypes.CodeSystem);

            foreach (DbExternalInclusion inclusion in externalInclusions)
            {
                // check to see if we should be including this
                if ((inclusion.IncludeInPackages == null) ||
                    inclusion.GetIncludeInPackagesList().Contains(focusPackage.NpmId, StringComparer.OrdinalIgnoreCase))
                {
                    // write the code system to a file
                    string filename = $"CodeSystem-{inclusion.Id}.json";
                    string path = Path.Combine(dir, filename);
                    File.WriteAllText(path, inclusion.Json);

                    xverCodeSystems[(-1 * inclusion.Key, targetPackage.Key)] = (null, inclusion.Key);
                }
            }

            // get the list of code systems in the source sourcePackage
            List<DbCodeSystem> codeSystems = DbCodeSystem.SelectList(
                _db!.DbConnection,
                FhirPackageKey: focusPackage.Key);

            // iterate over the code systems to them
            foreach (DbCodeSystem dbCs in codeSystems)
            {
                // create the FHIR CodeSystem
                CodeSystem fhirCs = new()
                {
                    Id = dbCs.Id,
                    Url = dbCs.UnversionedUrl,
                    Name = dbCs.Name,
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
                if (targetPackage.DefinitionFhirSequence < FhirReleases.FhirSequenceCodes.R5)
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
                                fhirCs.cgAddPackageSource(packageId, _crossDefinitionVersion, null);
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
                    _db!.DbConnection,
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
                    _db!.DbConnection,
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
                string filename = $"CodeSystem-{fhirCs.Id}.json";
                string path = Path.Combine(dir, filename);
                File.WriteAllText(path, fhirCs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));

                xverCodeSystems[(dbCs.Key, targetPackage.Key)] = (fhirCs, null);
            }
        }

        return xverCodeSystems;
    }

    private void addDbCodeSystemConcepts(
        List<CodeSystem.ConceptDefinitionComponent> concepts,
        int dbCsKey,
        int? parentConceptKey = null)
    {
        List<DbCodeSystemConcept> dbConcepts = (parentConceptKey == null)
            ? DbCodeSystemConcept.SelectList(
                _db!.DbConnection,
                CodeSystemKey: dbCsKey,
                ParentConceptKeyIsNull: true,
                orderByProperties: [nameof(DbCodeSystemConcept.FlatOrder)])
            : DbCodeSystemConcept.SelectList(
                _db!.DbConnection,
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

    private static string getPackageId(DbFhirPackage? sourcePackage, DbFhirPackage targetPackage) => sourcePackage == null
        ? $"hl7.fhir.uv.xver.{targetPackage.ShortName.ToLowerInvariant()}"
        : $"hl7.fhir.uv.xver-{sourcePackage.ShortName.ToLowerInvariant()}.{targetPackage.ShortName.ToLowerInvariant()}";

    private void writeXverValueSets(
        List<DbFhirPackage> packages,
        int focusPackageIndex,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets,
        string fhirDir)
    {
        //Dictionary<int, DbFhirPackage> packageDict = packages.ToDictionary(p => p.Key);
        DbFhirPackage focusPackage = packages[focusPackageIndex];

        // iterate over the target packages
        foreach (DbFhirPackage targetPackage in packages)
        {
            if (focusPackage.Key == targetPackage.Key)
            {
                continue;
            }

            string dir = createExportPackageDir(fhirDir, focusPackage, targetPackage);

            dir = _config.XverExportForPublisher
                ? Path.Combine(dir, "input", "vocabulary")
                : Path.Combine(dir, "package");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // write any external inclusion value sets
            List<DbExternalInclusion> externalInclusions = DbExternalInclusion.SelectList(
                _db!.DbConnection,
                ResourceType: Hl7.Fhir.Model.FHIRAllTypes.ValueSet);

            foreach (DbExternalInclusion inclusion in externalInclusions)
            {
                // check to see if we should be including this
                if ((inclusion.IncludeInPackages == null) ||
                    inclusion.GetIncludeInPackagesList().Contains(focusPackage.NpmId, StringComparer.OrdinalIgnoreCase))
                {
                    // write the value set to a file
                    string filename = $"ValueSet-{inclusion.Id}.json";
                    string path = Path.Combine(dir, filename);
                    File.WriteAllText(path, inclusion.Json);
                }
            }

            // iterate over the value sets
            foreach (((int sourceVsKey, int targetPackageId), ValueSet vs) in xverValueSets)
            {
                if (targetPackageId != targetPackage.Key)
                {
                    continue;
                }

                // write the value set to a file
                string filename = $"ValueSet-{vs.Id}.json";
                string path = Path.Combine(dir, filename);
                File.WriteAllText(path, vs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
            }
        }

        //// iterate over the value sets
        //foreach (((int sourceVsKey, int targetPackageId), ValueSet originalDbVs) in xverValueSets)
        //{
        //    DbFhirPackage targetPackage = packageDict[targetPackageId];

        //    string packageDir = createExportPackageDir(fhirDir, focusPackage, targetPackage);

        //    packageDir = _config.XverExportForPublisher
        //        ? Contents.Combine(packageDir, "input", "vocabulary")
        //        : Contents.Combine(packageDir, "sourcePackage");
        //    if (!Directory.Exists(packageDir))
        //    {
        //        Directory.CreateDirectory(packageDir);
        //    }

        //    // write the value set to a file
        //    string filename = $"ValueSet-{originalDbVs.IdLong}.json";
        //    string path = Contents.Combine(packageDir, filename);
        //    File.WriteAllText(path, originalDbVs.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
        //}
    }

    //[Obsolete]
    private void writeXverStructures(
        List<PackageXverSupport> packageSupports,
        int focusPackageIndex,
        Dictionary<(int sourceElementKey, int targetPackageId), (StructureDefinition, DbExtensionSubstitution?)> xverExtensions,
        Dictionary<(int sourceStructureKey, int targetPackageId), StructureDefinition> xverProfiles,
        string fhirDir)
    {
        Dictionary<int, DbFhirPackage> packageDict = packageSupports.Select(ps => ps.Package).ToDictionary(p => p.Key);
        DbFhirPackage focusPackage = packageSupports[focusPackageIndex].Package;

        ILookup<int, SnapshotGenerator?> generatorsById = packageSupports.ToLookup(ps => ps.Package.Key, ps => ps.SnapshotGenerator);

        Dictionary<int, (string packageId, string packageDir)> packageWriteSupport = [];
        foreach (PackageXverSupport ps in packageSupports)
        {
            if (ps.PackageIndex == focusPackageIndex)
            {
                continue;
            }

            string packageId = getPackageId(focusPackage, ps.Package);
            string dir = createExportPackageDir(fhirDir, focusPackage, ps.Package);

            packageWriteSupport.Add(ps.Package.Key, (packageId, dir));
        }

        // iterate over the extensions
        foreach (((int sourceKey, int targetPackageId), (StructureDefinition sd, DbExtensionSubstitution? extensionSubstitution)) in xverExtensions)
        {
            // if this extension is substituted out, we do not need to write it
            if (extensionSubstitution != null)
            {
                continue;
            }

            if (_config.XverGenerateSnapshots)
            {
                try
                {
                    if (sd.Snapshot == null)
                    {
                        // create a new snapshot
                        sd.Snapshot = new StructureDefinition.SnapshotComponent();
                    }

                    // a valid snapshot will always have at least the root element
                    if (sd.Snapshot.Element.Count == 0)
                    {
                        //sd.Snapshot.Element = packageSupports[targetPackageId].SnapshotGenerator?.GenerateAsync(sd).Result ?? [];
                        sd.Snapshot.Element = generatorsById[targetPackageId]?.FirstOrDefault()?.GenerateAsync(sd).Result ?? [];
                    }
                }
                catch (Exception) { }
            }

            DbFhirPackage targetPackage = packageDict[targetPackageId];
            (string packageId, string dir) = packageWriteSupport[targetPackageId];

            dir = _config.XverExportForPublisher
                ? Path.Combine(dir, "input", "extensions")
                : Path.Combine(dir, "package");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // write the structure to a file
            string filename = $"StructureDefinition-{sd.Id}.json";

            string path = Path.Combine(dir, filename);
            File.WriteAllText(path, sd.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
        }

        // iterate over the profiles
        foreach (((int sourceStructureKey, int targetPackageId), StructureDefinition sd) in xverProfiles)
        {
            if (_config.XverGenerateSnapshots)
            {
                try
                {
                    if (sd.Snapshot == null)
                    {
                        // create a new snapshot
                        sd.Snapshot = new StructureDefinition.SnapshotComponent();
                    }

                    // a valid snapshot will always have at least the root element
                    if (sd.Snapshot.Element.Count == 0)
                    {
                        //sd.Snapshot.Element = packageSupports[targetPackageId].SnapshotGenerator?.GenerateAsync(sd).Result ?? [];
                        sd.Snapshot.Element = generatorsById[targetPackageId]?.FirstOrDefault()?.GenerateAsync(sd).Result ?? [];
                    }
                }
                catch (Exception) { }
            }

            DbFhirPackage targetPackage = packageDict[targetPackageId];
            (string packageId, string dir) = packageWriteSupport[targetPackageId];

            dir = _config.XverExportForPublisher
                ? Path.Combine(dir, "input", "profiles")
                : Path.Combine(dir, "package");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // write the structure to a file
            string filename = $"StructureDefinition-{sd.Id}.json";

            string path = Path.Combine(dir, filename);
            File.WriteAllText(path, sd.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
        }
    }


    private void buildXverStructures(
        List<PackageXverSupport> packageSupports,
        int sourcePackageIndex,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets,
        Dictionary<(int sourceElementKey, int targetPackageId), (StructureDefinition, DbExtensionSubstitution?)> xverExtensions,
        Dictionary<(int sourceStructureKey, int targetPackageId), StructureDefinition> xverProfiles,
        Dictionary<(int sourcePackageIndex, string sdName), List<List<XverOutcome>>> xverOutcomes,
        FhirArtifactClassEnum artifactClass)
    {
        DbFhirPackage sourcePackage = packageSupports[sourcePackageIndex].Package;

        // resolve the extension types for this version of FHIR
        HashSet<string> allowedExtensionTypes = getAllowedExtensionTypes(sourcePackage.Key);

        // get the list of structures in this version
        List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(
                _db!.DbConnection,
                FhirPackageKey: sourcePackage.Key,
                ArtifactClass: artifactClass);

        // iterate over the structures
        foreach (DbStructureDefinition sd in structures)
        {
            if (!xverOutcomes.ContainsKey((sourcePackageIndex, sd.Name)))
            {
                xverOutcomes[(sourcePackageIndex, sd.Name)] = [];
                for (int i = 0; i < packageSupports.Count; i++)
                {
                    xverOutcomes[(sourcePackageIndex, sd.Name)].Add([]);
                }
            }

            // build a graph for this structure
            DbGraphSd sdGraph = new()
            {
                DB = _db!.DbConnection,
                Packages = packageSupports.Select(ps => ps.Package).ToList(),
                KeySd = sd,
            };

            // build a dictionary of all element projections, by element key
            Dictionary<int, List<DbGraphSd.DbElementRow>> elementProjectionDict = [];
            foreach (DbGraphSd.DbSdRow sdRow in sdGraph.Projection)
            {
                foreach (DbGraphSd.DbElementRow sdElementRow in sdRow.Projection)
                {
                    if (sdElementRow.KeyCell == null)
                    {
                        continue;
                    }

                    if (!elementProjectionDict.TryGetValue(sdElementRow.KeyCell.Element.Key, out List<DbGraphSd.DbElementRow>? elementList))
                    {
                        elementList = [];
                        elementProjectionDict.Add(sdElementRow.KeyCell.Element.Key, elementList);
                    }

                    elementList.Add(sdElementRow);
                }
            }

            List<HashSet<int>> generatedElementKeys = [];
            for (int i = 0; i < packageSupports.Count; i++)
            {
                generatedElementKeys.Add([]);
            }

            List<bool> structureMapsToBasic = [];
            for (int i = 0; i < packageSupports.Count; i++)
            {
                if (sdGraph.Projection.Any(sdRow => sdRow[i] != null))
                {
                    structureMapsToBasic.Add(false);
                    continue;
                }

                structureMapsToBasic.Add(true);
            }

            // iterate over the elements of our structure
            foreach (DbElement element in DbElement.SelectList(_db!.DbConnection, StructureKey: sd.Key, orderByProperties: [nameof(DbElement.ResourceFieldOrder)]))
            {
                // do not build extensions for simple-type elements (e.g., Element.id, Extension.url, etc.)
                if (element.IsSimpleType == true)
                {
                    continue;
                }

                // do not build extensions for extension or base elements
                switch (element.FullCollatedTypeLiteral)
                {
                    case "Extension":
                    case "Base":
                        continue;
                }

                // resolve the projection rows for this element
                List<DbGraphSd.DbElementRow> elementProjection = elementProjectionDict[element.Key];

                bool extensionNeeded = false;

                // work upwards first
                for (int currentIndex = sourcePackageIndex; currentIndex < (packageSupports.Count - 1); currentIndex++)
                {
                    int targetPackageIndex = currentIndex + 1;
                    DbFhirPackage targetPackage = packageSupports[targetPackageIndex].Package;

                    // do not generate if this element is part of a mapped structure and has an equivalent in the target's basic resource definition
                    if (structureMapsToBasic[targetPackageIndex] &&
                        (element.ParentElementKey != null) &&
                        packageSupports[targetPackageIndex].BasicElements.TryGetValue(element.Path.Substring(sd.Name.Length), out string? basicBasePath))
                    {
                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementBasePath = element.BasePath,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseBasicElement,
                            TargetElementId = basicBasePath ?? "Basic" + element.Path.Substring(sd.Name.Length),
                            TargetExtensionUrl = null,
                            TargetExtensionId = null,
                            TargetElementBasePath = null,
                            ReplacementExtensionUrl = null,
                        });
                        continue;
                    }

                    // if we already generated the parent of this element, flag it as added and move on
                    if ((element.ParentElementKey != null) &&
                        generatedElementKeys[targetPackageIndex].Contains(element.ParentElementKey.Value))
                    {
                        // add this element to the generated list
                        generatedElementKeys[targetPackageIndex].Add(element.Key);
                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            SourceElementBasePath = element.BasePath,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseExtensionFromAncestor,
                            TargetElementId = null,
                            TargetElementBasePath = null,
                            TargetExtensionUrl = null,
                            TargetExtensionId = null,
                            ReplacementExtensionUrl = null,
                        });
                        continue;
                    }

                    List<DbElementComparison> comparisons = [];

                    // resolve the current column
                    List<DbGraphSd.DbElementCell?> sourceCells = elementProjection
                        .Select(row => row[currentIndex])
                        .ToList();

                    // if we have not hit a need for the extension yet in this direction, test the curent pair
                    if (!extensionNeeded)
                    {
                        // check to see if this element has already been mapped in the previous version
                        if ((currentIndex > sourcePackageIndex) &&
                            generatedElementKeys[currentIndex - 1].Contains(element.Key))
                        {
                            extensionNeeded = true;
                        }
                        // only generate entire structures if there is no mappable structure in the target
                        else if (element.ResourceFieldOrder == 0)
                        {
                            extensionNeeded = structureMapsToBasic[targetPackageIndex];
                        }
                        // if we have no mappings, we need a new extension
                        else if (sourceCells.Count == 0)
                        {
                            extensionNeeded = true;
                        }
                        // if all cells or right projections are null, we need an extension
                        else if (sourceCells.All(cell => cell?.RightCell == null))
                        {
                            extensionNeeded = true;
                        }
                        // need to check aggregate relationship
                        else
                        {
                            // easier to check inverse here
                            extensionNeeded = true;

                            List<DbGraphSd.DbElementCell> matchedCells = sourceCells
                                .Where(c => (c?.RightComparison?.Relationship == CMR.Equivalent) || (c?.RightComparison?.Relationship == CMR.SourceIsNarrowerThanTarget))
                                .Select(c => c!)
                                .ToList();

                            // TODO: one-of might also need an extension...
                            if (matchedCells.Count != 0)
                            {
                                extensionNeeded = false;
                                XverOutcomeCodes oc = matchedCells.Count > 1
                                    ? XverOutcomeCodes.UseOneOf
                                    : matchedCells[0].RightCell?.Element.Id == element.Id
                                        ? XverOutcomeCodes.UseElementSameName
                                        : XverOutcomeCodes.UseElementRenamed;

                                xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                                {
                                    SourcePackageKey = sourcePackage.Key,
                                    SourceStructureName = sd.Name,
                                    SourceElementId = element.Id,
                                    SourceElementBasePath = element.BasePath,
                                    SourceElementFieldOrder = element.ResourceFieldOrder,
                                    TargetPackageKey = targetPackage.Key,
                                    OutcomeCode = oc,
                                    TargetElementId = string.Join(',', matchedCells.Select(c => c.RightCell?.Element.Id)),
                                    TargetElementBasePath = string.Join(',', matchedCells.Select(c => c.RightCell?.Element.BasePath)),
                                    TargetExtensionUrl = null,
                                    TargetExtensionId = null,
                                    ReplacementExtensionUrl = null,
                                });
                            }
                        }
                    }

                    // if we still do not need an extension, go to next sourcePackage
                    if (!extensionNeeded)
                    {
                        continue;
                    }

                    // check to see if we have already generated this extension
                    if (xverExtensions.ContainsKey((element.Key, targetPackage.Key)))
                    {
                        continue;
                    }

                    foreach (DbGraphSd.DbElementCell? cell in sourceCells)
                    {
                        if (cell?.RightComparison == null)
                        {
                            continue;
                        }

                        comparisons.Add(cell.RightComparison);
                    }

                    // build an extension for the original source element to target the current target version
                    StructureDefinition? extSd = createExtensionSd(
                        sourcePackageIndex,
                        packageSupports[sourcePackageIndex],
                        targetPackageIndex,
                        packageSupports[targetPackageIndex],
                        sd,
                        element,
                        comparisons,
                        elementProjectionDict,
                        xverValueSets);

                    if (extSd != null)
                    {
                        DbExtensionSubstitution? extSub = DbExtensionSubstitution.SelectSingle(_db!.DbConnection, SourceElementId: element.Id);

                        xverExtensions.Add((element.Key, packageSupports[targetPackageIndex].Package.Key), (extSd, extSub));
                        generatedElementKeys[targetPackageIndex].Add(element.Key);

                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementBasePath = element.BasePath,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseExtension,
                            TargetElementId = null,
                            TargetElementBasePath = null,
                            TargetExtensionUrl = extSd.Url,
                            TargetExtensionId = extSd.Id,
                            ReplacementExtensionUrl = extSub?.ReplacementUrl
                        });

                        // check to see if this extension maps to the root of the Basic resource
                        if ((sd.ArtifactClass == FhirArtifactClassEnum.Resource) &&
                            extSd.Context.Any(c => c.Expression == "Basic"))
                        {
                            // need to create a profile for this extension
                            StructureDefinition profileSd = createBasicProfileForExtension(sourcePackage, targetPackage, sd, extSd);
                            xverProfiles.Add((sd.Key, targetPackage.Key), profileSd);
                        }
                    }
                }

                extensionNeeded = false;

                // then work downwards
                for (int currentIndex = sourcePackageIndex; currentIndex > 0; currentIndex--)
                {
                    int targetPackageIndex = currentIndex - 1;
                    DbFhirPackage targetPackage = packageSupports[targetPackageIndex].Package;

                    // do not generate if this element is equivalent in the target basic resource
                    if (structureMapsToBasic[targetPackageIndex] &&
                        (element.ParentElementKey != null) &&
                        packageSupports[targetPackageIndex].BasicElements.TryGetValue(element.Path.Substring(sd.Name.Length), out string? basicBasePath))
                    {
                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementBasePath = element.BasePath,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseBasicElement,
                            TargetElementId = "Basic" + element.Path.Substring(sd.Name.Length),
                            TargetElementBasePath = basicBasePath ?? "Basic" + element.Path.Substring(sd.Name.Length),
                            TargetExtensionUrl = null,
                            TargetExtensionId = null,
                            ReplacementExtensionUrl = null,
                        });
                        continue;
                    }

                    // if we already generated the parent of this element, flag it as added and move on
                    if ((element.ParentElementKey != null) &&
                        generatedElementKeys[targetPackageIndex].Contains(element.ParentElementKey.Value))
                    {
                        // add this element to the generated list
                        generatedElementKeys[targetPackageIndex].Add(element.Key);
                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementBasePath = element.BasePath,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseExtensionFromAncestor,
                            TargetElementId = null,
                            TargetElementBasePath = null,
                            TargetExtensionUrl = null,
                            TargetExtensionId = null,
                            ReplacementExtensionUrl = null,
                        });
                        continue;
                    }

                    List<DbElementComparison> comparisons = [];

                    // resolve the current column
                    List<DbGraphSd.DbElementCell?> sourceCells = elementProjection
                        .Select(row => row[currentIndex])
                        .ToList();

                    // if we have not hit a need for the extension yet in this direction, test the curent pair
                    if (!extensionNeeded)
                    {
                        // check to see if this element has been mapped in the previous version
                        if ((currentIndex < sourcePackageIndex) &&
                            generatedElementKeys[currentIndex + 1].Contains(element.Key))
                        {
                            extensionNeeded = true;
                        }
                        // only generate entire structures if there is no mappable structure in the target
                        else if (element.ResourceFieldOrder == 0)
                        {
                            extensionNeeded = structureMapsToBasic[targetPackageIndex];
                        }
                        // if we have no mappings, we need a new extension
                        else if (sourceCells.Count == 0)
                        {
                            extensionNeeded = true;
                        }
                        // if all cells or left projections are null, we need an extension
                        else if (sourceCells.All(cell => cell?.LeftComparison == null))
                        {
                            extensionNeeded = true;
                        }
                        // need to check aggregate relationship
                        else
                        {
                            // easier to check inverse here
                            extensionNeeded = true;

                            List<DbGraphSd.DbElementCell> matchedCells = sourceCells
                                .Where(c => (c?.LeftComparison?.Relationship == CMR.Equivalent) || (c?.LeftComparison?.Relationship == CMR.SourceIsNarrowerThanTarget))
                                .Select(c => c!)
                                .ToList();

                            // TODO: one-of might also need an extension...
                            if (matchedCells.Count != 0)
                            {
                                extensionNeeded = false;
                                XverOutcomeCodes oc = matchedCells.Count > 1
                                    ? XverOutcomeCodes.UseOneOf
                                    : matchedCells[0].LeftCell?.Element.Id == element.Id
                                        ? XverOutcomeCodes.UseElementSameName
                                        : XverOutcomeCodes.UseElementRenamed;

                                xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                                {
                                    SourcePackageKey = sourcePackage.Key,
                                    SourceStructureName = sd.Name,
                                    SourceElementId = element.Id,
                                    SourceElementBasePath = element.BasePath,
                                    SourceElementFieldOrder = element.ResourceFieldOrder,
                                    TargetPackageKey = targetPackage.Key,
                                    OutcomeCode = oc,
                                    TargetElementId = string.Join(',', matchedCells.Select(c => c.LeftCell?.Element.Id)),
                                    TargetElementBasePath = string.Join(',', matchedCells.Select(c => c.LeftCell?.Element.BasePath)),
                                    TargetExtensionUrl = null,
                                    TargetExtensionId = null,
                                    ReplacementExtensionUrl = null,
                                });
                            }
                        }
                    }

                    // if we still do not need an extension, go to next sourcePackage
                    if (!extensionNeeded)
                    {
                        continue;
                    }

                    // check to see if we have already generated this extension
                    if (xverExtensions.ContainsKey((element.Key, targetPackage.Key)))
                    {
                        continue;
                    }

                    foreach (DbGraphSd.DbElementCell? cell in sourceCells)
                    {
                        if (cell?.LeftComparison == null)
                        {
                            continue;
                        }

                        comparisons.Add(cell.LeftComparison);
                    }

                    // build an extension for the original source element to target the current target version
                    StructureDefinition? extSd = createExtensionSd(
                        sourcePackageIndex,
                        packageSupports[sourcePackageIndex],
                        targetPackageIndex,
                        packageSupports[targetPackageIndex],
                        sd,
                        element,
                        comparisons,
                        elementProjectionDict,
                        xverValueSets);

                    if (extSd != null)
                    {
                        DbExtensionSubstitution? extSub = DbExtensionSubstitution.SelectSingle(_db!.DbConnection, SourceElementId: element.Id);

                        xverExtensions.Add((element.Key, targetPackage.Key), (extSd, extSub));
                        generatedElementKeys[targetPackageIndex].Add(element.Key);

                        xverOutcomes[(sourcePackageIndex, sd.Name)][targetPackageIndex].Add(new()
                        {
                            SourcePackageKey = sourcePackage.Key,
                            SourceStructureName = sd.Name,
                            SourceElementId = element.Id,
                            SourceElementBasePath = element.BasePath,
                            SourceElementFieldOrder = element.ResourceFieldOrder,
                            TargetPackageKey = targetPackage.Key,
                            OutcomeCode = XverOutcomeCodes.UseExtension,
                            TargetElementId = null,
                            TargetElementBasePath = null,
                            TargetExtensionUrl = extSd.Url,
                            TargetExtensionId = extSd.Id,
                            ReplacementExtensionUrl = extSub?.ReplacementUrl,
                        });

                        // check to see if this extension maps to the root of the Basic resource
                        if ((sd.ArtifactClass == FhirArtifactClassEnum.Resource) &&
                            extSd.Context.Any(c => c.Expression == "Basic"))
                        {
                            // need to create a profile for this extension
                            StructureDefinition profileSd = createBasicProfileForExtension(sourcePackage, targetPackage, sd, extSd);
                            xverProfiles.Add((sd.Key, targetPackage.Key), profileSd);
                        }
                    }
                }
            }
        }

        return;

        HashSet<string> getAllowedExtensionTypes(int packageKey)
        {
            // resolve the 'extension' structure definition
            DbStructureDefinition? extSd = DbStructureDefinition.SelectSingle(
                _db!.DbConnection,
                FhirPackageKey: packageKey,
                Name: "Extension");

            if (extSd == null)
            {
                return [];
            }

            // get the 'value[x]' element
            DbElement? extValueElement = DbElement.SelectSingle(
                _db!.DbConnection,
                StructureKey: extSd.Key,
                Id: "Extension.value[x]");

            if (extValueElement == null)
            {
                return [];
            }

            // get the types allowed in the Extension.value element
            List<DbElementType> extValueTypes = DbElementType.SelectList(
                _db!.DbConnection,
                ElementKey: extValueElement.Key);

            return new HashSet<string>(extValueTypes.Select(et => et.TypeName!));
        }
    }

    private StructureDefinition createBasicProfileForExtension(
        DbFhirPackage sourcePackage,
        DbFhirPackage targetPackage,
        DbStructureDefinition sourceStructure,
        StructureDefinition extSd)
    {
        // TODO: add profiles for other resource types
        string targetStructureName = "Basic";       // extSd.Context.First().Expression;
        string xverPackageId = getPackageId(sourcePackage, targetPackage);

        string profileId = $"{sourcePackage.ShortName}-{sourceStructure.Name}-for-{targetPackage.ShortName}";
        StructureDefinition profileSd = new()
        {
            Id = profileId,
            Url = $"http://hl7.org/fhir/{targetPackage.FhirVersionShort}/StructureDefinition/{profileId}",
            Name = FhirSanitizationUtils.ReformatIdForName(profileId),
            Version = _crossDefinitionVersion,
            FhirVersion = EnumUtility.ParseLiteral<FHIRVersion>(targetPackage.PackageVersion) ?? FHIRVersion.N5_0_0,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version Profile for {sourcePackage.ShortName}.{sourceStructure.Name} for use in FHIR {targetPackage.ShortName}",
            Description = $"This cross-version profile on the {targetPackage.ShortName}.{targetStructureName} resource can be used to represent a FHIR {sourcePackage.ShortName}.{sourceStructure.Name} resource.",
            Status = PublicationStatus.Active,
            Experimental = false,
            Kind = StructureDefinition.StructureDefinitionKind.Resource,
            Abstract = false,
            Type = targetStructureName,
            BaseDefinition = $"http://hl7.org/fhir/StructureDefinition/{targetStructureName}",
            Derivation = StructureDefinition.TypeDerivationRule.Constraint,
            Differential = new()
            {
                Element = [
                    new ElementDefinition()
                    {
                        ElementId = "Basic.extension",
                        Path = "Basic.extension",
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
                        Base = new ElementDefinition.BaseComponent()
                        {
                            Path = "DomainResource.extension",
                            Min = 0,
                            Max = "*",
                        },
                        Min = 1,
                        Max = "*",
                    },
                    new ElementDefinition()
                    {
                        ElementId = $"Basic.extension:{sourceStructure.Id}",
                        Path = "Basic.extension",
                        SliceName = sourceStructure.Id,
                        Short = $"Cross-version extension for {sourceStructure.Name} from {sourcePackage.ShortName} for use in FHIR {targetPackage.ShortName}",
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
                                Profile = [ extSd.Url ],
                            },
                        ],
                    },
                    new ElementDefinition()
                    {
                        ElementId = "Basic.code",
                        Path = "Basic.code",
                        Pattern = new CodeableConcept("http://hl7.org/fhir/fhir-types", sourceStructure.Id),
                        Base = new ElementDefinition.BaseComponent()
                        {
                            Path = "Basic.code",
                            Min = 1,
                            Max = "*",
                        }
                    },
                ],
            },
        };

        string wg = CommonDefinitions.ResolveWorkgroup(extSd.cgWorkGroup(), "fhir");

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

        profileSd.cgAddPackageSource(xverPackageId, _crossDefinitionVersion, null);

        return profileSd;
    }

    private StructureDefinition? createExtensionSd(
        int sourceIndex,
        PackageXverSupport sourcePackageSupport,
        int targetIndex,
        PackageXverSupport targetPackageSupport,
        DbStructureDefinition sd,
        DbElement element,
        List<DbElementComparison> relevantComparisons,
        Dictionary<int, List<DbGraphSd.DbElementRow>> elementProjectionDict,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets)
    {
        DbFhirPackage sourcePackage = sourcePackageSupport.Package;
        DbFhirPackage targetPackage = targetPackageSupport.Package;

        string xverPackageId = getPackageId(sourcePackage, targetPackage);

        //string sdId = $"{focusPackage.ShortName}-{element.Contents}-for-{targetPackage.ShortName}";
        string sdId = $"ext-{sourcePackage.ShortName}-{collapsePathForId(element.Path)}";

        bool isRootElement = element.ResourceFieldOrder == 0;
        int elementPathLen = element.Path.Length;

        bool isExtensionOnBasic = false;
        List<DbElement> contextElements = [];
        List<StructureDefinition.ContextComponent> contexts = [];

        // if our source element is a resource or datatype, we can only apply it to the basic resource
        if (isRootElement)
        {
            contexts.Add(new()
            {
                Type = StructureDefinition.ExtensionContextType.Element,
                Expression = "Basic",
            });

            DbElement? basicElement = DbElement.SelectSingle(
                _db!.DbConnection,
                FhirPackageKey: targetPackage.Key,
                Id: "Basic");

            if (basicElement != null)
            {
                // add the basic element to the context elements
                contextElements.Add(basicElement);
                isExtensionOnBasic = true;
            }
        }
        else
        {
            HashSet<string> contextElementPaths = [];

            contextElements = discoverContexts(contextElementPaths, sourceIndex, targetIndex, targetPackageSupport, element, elementProjectionDict);

            if (contextElementPaths.Count != 0)
            {
                foreach (string path in contextElementPaths.Distinct().Order())
                {
                    contexts.Add(new()
                    {
                        Type = StructureDefinition.ExtensionContextType.Element,
                        Expression = path,
                    });
                }
            }
        }

        // fallback to element if we have no contexts
        if (contexts.Count == 0)
        {
            contexts.Add(new()
            {
                Type = StructureDefinition.ExtensionContextType.Element,
                Expression = "Element",
            });
        }

        bool allowModifier = false;

        // if the extension wants to be a modifier, check the contexts
        if (element.IsModifier == true)
        {
            allowModifier = true;

            /*
             * Determine if this extension should really be a modifier based on context:
             *  * Modifier element -> Modifier element : extension
             *  * Modifier element -> Backbone element (not modifier) : modifier extension
             *  * Modifier element -> Primitive-type element (not modifier) : modifier extension, context moves up a level
             *  * Modifier element -> Primitive-type element (array, not modifier) : currently unresolvable, but also has not happened yet
             */

            List<(DbElement original, DbElement replacement)> ctxElementReplacements = [];
            foreach (DbElement contextElement in contextElements)
            {
                // check if the source element is a modifier - do not need a modifier extension
                if (contextElement.IsModifier)
                {
                    allowModifier = false;
                    continue;
                }

                // check to see if the context element has children, can stay how it is
                if (contextElement.ChildElementCount > 0)
                {
                    continue;
                }

                // if element has ONLY primitive types, we need to move the context up a level
                if (_db!.DbConnection.ElementHasNonPrimitiveTypes(contextElement) == false)
                {
                    if (contextElement.ParentElementKey == null)
                    {
                        throw new Exception($"Cannot have a root element with only primitive types!");
                    }

                    DbElement replacement = DbElement.SelectSingle(_db!.DbConnection, Key: contextElement.ParentElementKey)
                        ?? throw new Exception($"Could not resolve Element: {contextElement.ParentElementKey} (parent of Element: {element.Key} - '{element.Id}')");

                    ctxElementReplacements.Add((contextElement, replacement));
                }
            }

            // resolve any context replacements
            if (ctxElementReplacements.Count != 0)
            {
                foreach ((DbElement original, DbElement replacement) in ctxElementReplacements)
                {
                    // look for the matching extension context
                    foreach (StructureDefinition.ContextComponent ctx in contexts)
                    {
                        if (ctx.Expression != original.Path)
                        {
                            continue;
                        }

                        ctx.Expression = replacement.Path;
                    }
                }
            }
        }

        StructureDefinition extSd = new()
        {
            Id = sdId,
            //CanonicalUrl = $"http://hl7.org/fhir/uv/xver/{focusPackage.FhirVersionShort}/StructureDefinition/extension-{element.Contents.Replace("[x]", string.Empty)}",
            Url = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/StructureDefinition/extension-{element.Path.Replace("[x]", string.Empty)}",
            Name = FhirSanitizationUtils.ReformatIdForName(sdId),
            Version = _crossDefinitionVersion,
            FhirVersion = EnumUtility.ParseLiteral<FHIRVersion>(targetPackageSupport!.Package.PackageVersion) ?? FHIRVersion.N5_0_0,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Title = $"Cross-version Extension for {sourcePackage.ShortName}.{element.Path} for use in FHIR {targetPackage.ShortName}",
            Description = $"This cross-version extension represents {element.Path} from {sd.VersionedUrl} for use in FHIR {targetPackage.ShortName}.",
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

        extSd.cgAddPackageSource(xverPackageId, _crossDefinitionVersion, null);

        // add this element to the structure, including the child elements
        ExtElementBuilderRecord? extRec = addElementToExtension(
            extSd,
            "Extension",
            "Extension",
            null,
            sourcePackageSupport,
            targetPackageSupport,
            sd,
            element,
            relevantComparisons,
            xverValueSets,
            contextElements,
            isExtensionOnBasic);

        if (extRec == null)
        {
            // we should not be in a scenario where we do not have any properties
            throw new Exception($"Extension build for {extSd.Id} failed!");
        }

        addExtRecToSd(extSd, extRec, allowModifier, true);

        return extSd;
    }

    private void addExtRecToSd(
        StructureDefinition sd,
        ExtElementBuilderRecord extRec,
        bool allowModifier,
        bool addRootElement = false)
    {
        string extElementId = extRec.ElementId.Replace("[x]", string.Empty);
        string extElementPath = extRec.Path.Replace("[x]", string.Empty);

        if (addRootElement)
        {
            bool? isMod = allowModifier == false
                ? null
                : extRec.SourceElement.IsModifier;

            string? modReason = isMod != true
                ? null
                : extRec.SourceElement.IsModifierReason
                    ?? $"This extension is a modifier because the target element {extRec.SourceElement.Id} is flagged IsModifier";

            ElementDefinition rootElement = new()
            {
                ElementId = extElementId,
                Path = extElementPath,
                Short = extRec.ShortText,
                Definition = extRec.Definition,
                Comment = extRec.Comment,
                Min = extRec.SourceElement.MinCardinality,
                Max = extRec.SourceElement.MaxCardinalityString,
                Base = new()
                {
                    Path = "Extension",
                    Min = 0,
                    Max = "*",
                },
                IsModifier = isMod,
                IsModifierReason = modReason,
            };

            sd.Differential.Element.Add(rootElement);
        }
        else if (extRec.SliceName != null)
        {
            // add the primary slice
            ElementDefinition sliceElement = new()
            {
                ElementId = extElementId,
                Path = extElementPath,
                SliceName = extRec.SliceName,
                Short = extRec.ShortText,
                Definition = extRec.Definition,
                Comment = extRec.Comment,
                Min = extRec.SourceElement.MinCardinality,
                Max = extRec.SourceElement.MaxCardinalityString,
                Base = new()
                {
                    Path = "Extension.extension",
                    Min = 0,
                    Max = "*",
                }
            };

            sd.Differential.Element.Add(sliceElement);
        }

        bool hasDatatypeExtension = (extRec.DatatypeSliceElement != null) && (extRec.DatatypeValueElement != null);

        // if there are any extensions, we need to build the slicing element
        if ((extRec.Extensions.Count > 0) ||
            hasDatatypeExtension)
        {
            sd.Differential.Element.Add(new()
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
                    Rules = ElementDefinition.SlicingRules.Closed,
                },
                Min = extRec.Extensions.Sum(ebr => ebr.SourceElement.MinCardinality),
                Max = "*",
            });
        }

        // recursively add any extensions
        foreach (ExtElementBuilderRecord child in extRec.Extensions)
        {
            addExtRecToSd(sd, child, allowModifier);
        }

        // if we have a datatype slice and value, we need to add them
        if (hasDatatypeExtension)
        {
            sd.Differential.Element.Add(extRec.DatatypeSliceElement);
            sd.Differential.Element.Add(extRec.DatatypeValueElement);
        }

        // add the URL element (always required)
        sd.Differential.Element.Add(new()
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
            Fixed = new FhirUri(extRec.Url),
        });

        // either constrain or add the value element
        if (extRec.ValueElement is null)
        {
            sd.Differential.Element.Add(new()
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
            });
        }
        else
        {
            sd.Differential.Element.Add(extRec.ValueElement);
        }
    }

    private record class ExtElementBuilderRecord
    {
        public required DbElement SourceElement { get; set; }
        public required string? UserMessages { get; set; }
        public required string ShortText { get; set; }
        public required string Definition { get; set; }
        public required string? Comment { get; set; }
        public required string Url { get; set; }
        public required string ElementId { get; set; }
        public required string Path { get; set; }
        public required string? SliceName { get; set; }
        public ElementDefinition? ValueElement { get; set; } = null;
        public List<ExtElementBuilderRecord> Extensions { get; set; } = [];
        public ElementDefinition? DatatypeSliceElement { get; set; } = null;
        public ElementDefinition? DatatypeValueElement { get; set; } = null;
        public List<string> ExtendedDatatypeNames = [];
    }


    private ExtElementBuilderRecord? addElementToExtension(
        StructureDefinition extSd,
        string extElementId,
        string extElementPath,
        string? sliceName,
        PackageXverSupport sourcePackageSupport,
        PackageXverSupport targetPackageSupport,
        DbStructureDefinition sd,
        DbElement element,
        List<DbElementComparison> relevantComparisons,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets,
        List<DbElement>? contextElements,
        bool isExtensionOnBasic)
    {
        // do not build extensions for extension or base elements
        switch (element.FullCollatedTypeLiteral)
        {
            case "Extension":
            case "Base":
                return null;
        }

        // skip id elements, they are part of every element and do not need to be written
        if (extElementId.EndsWith(".extension:id", StringComparison.Ordinal))
        {
            return null;
        }

        // check to see if this element is in the 'basic' resource of this version (do not add)
        if ((isExtensionOnBasic == true) &&
            (sliceName != null) &&
            (element.Path.Length > sd.Name.Length) &&
            targetPackageSupport.BasicElements.ContainsKey(element.Path.Substring(sd.Name.Length)))
        {
            return null;
        }

        (string? edShortText, string? edDefinition, string? edComment) = getTextForExtensionElement(
            element,
            relevantComparisons.Count == 0 ? null : string.Join(' ', relevantComparisons.Select(c => c.UserMessage ?? string.Empty)));

        ExtElementBuilderRecord extBuilderRec = new()
        {
            SourceElement = element,
            UserMessages = relevantComparisons.Count == 0 ? null : string.Join(' ', relevantComparisons.Select(c => c.UserMessage ?? string.Empty)),
            ShortText = edShortText ?? $"Cross-version extension for {element.Name} from {sd.VersionedUrl} for use in FHIR {targetPackageSupport.Package.ShortName}",
            Definition = edDefinition ?? $"This cross-version extension represents {element.Path} from {sd.VersionedUrl} for use in FHIR {targetPackageSupport.Package.ShortName}.",
            Comment = edComment,
            Url = sliceName == null ? extSd.Url : sliceName,
            ElementId = extElementId,
            Path = extElementPath,
            SliceName = sliceName,
        };

        int sourceCol = sourcePackageSupport.PackageIndex;
        int targetCol = targetPackageSupport.PackageIndex;

        // if there are child elements, process them
        if (element.ChildElementCount != 0)
        {
            // iterate over our child elements and add them
            foreach (DbElement childElement in DbElement.SelectList(
                _db!.DbConnection,
                ParentElementKey: element.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]))
            {
                // add this child element to the extension
                ExtElementBuilderRecord? child = addElementToExtension(
                    extSd,
                    $"{extElementId}.extension:{childElement.Name}",
                    $"{extElementPath}.extension",
                    childElement.Name,
                    sourcePackageSupport,
                    targetPackageSupport,
                    sd,
                    childElement,
                    relevantComparisons,
                    xverValueSets,
                    null,
                    isExtensionOnBasic);

                if (child != null)
                {
                    extBuilderRec.Extensions.Add(child);
                }
            }

            // don't have to do anything past processing children
            return extBuilderRec;
        }

        ElementDefinition extensionEdValue = new()
        {
            ElementId = extElementId + ".value[x]",
            Path = extElementPath + ".value[x]",
            Short = extBuilderRec.ShortText,
            Definition = extBuilderRec.Definition,
            Comment = extBuilderRec.Comment,
            Base = new()
            {
                Path = "Extension.value[x]",
                Min = 0,
                Max = "1",
            },
            Type = [],
        };

        // check to see if we need to add a binding
        if ((element.ValueSetBindingStrength != null) &&
            (element.BindingValueSet != null))
        {
            string? vsUrl = null;

            if ((element.BindingValueSetKey != null) &&
                xverValueSets.TryGetValue((element.BindingValueSetKey.Value, targetPackageSupport.Package.Key), out ValueSet? vs))
            {
                vsUrl = vs.Url;
            }
            else
            {
                List<(string unversionedUrl, string version)> mappedUrls = _db!.DbConnection.GetMappedValueSetUrls(
                    sourcePackageSupport.Package.Key,
                    element.BindingValueSet,
                    targetPackageSupport.Package.Key);

                if (mappedUrls.Count == 0)
                {
                    // try to get a matching VS
                    DbValueSet? targetVs = DbValueSet.SelectSingle(
                        _db!.DbConnection,
                        FhirPackageKey: targetPackageSupport.Package.Key,
                        UnversionedUrl: element.BindingValueSet);

                    targetVs ??= DbValueSet.SelectSingle(
                        _db!.DbConnection,
                        FhirPackageKey: targetPackageSupport.Package.Key,
                        VersionedUrl: element.BindingValueSet);

                    if (targetVs == null)
                    {
                        DbValueSet? sourceVs = DbValueSet.SelectSingle(
                            _db!.DbConnection,
                            Key: element.BindingValueSetKey);

                        if (sourceVs != null)
                        {
                            targetVs = DbValueSet.SelectSingle(
                                _db!.DbConnection,
                                FhirPackageKey: targetPackageSupport.Package.Key,
                                Id: sourceVs.Id);
                        }
                    }

                    // if we have a target VS, use it
                    if (targetVs != null)
                    {
                        vsUrl = targetVs.UnversionedUrl + "|" + targetVs.Version;
                    }
                    // if this is an example binding, just leave it unbound
                    else if (element.ValueSetBindingStrength == BindingStrength.Extensible)
                    {
                        vsUrl = null;
                    }
                    else
                    {
                        // TODO: use the original binding value set URL - it is likely unexpandable
                        // Note that this will cause publisher warnings, but we do not have a strategy for resolving yet
                        vsUrl = element.BindingValueSet;
                    }
                }
                else
                {
                    vsUrl = mappedUrls[0].unversionedUrl + "|" + mappedUrls[0].version;
                }
            }

            if (vsUrl != null)
            {
                extensionEdValue.Binding = new()
                {
                    Strength = element.ValueSetBindingStrength,
                    Description = element.BindingDescription,
                    ValueSet = vsUrl,
                };
            }
        }

        // build the value types
        List<DbElementType> elementValueTypes = DbElementType.SelectList(
            _db!.DbConnection,
            ElementKey: element.Key);

        // setup a lookup by type name
        ILookup<string, DbElementType> collectedValueTypes = elementValueTypes.ToLookup(t => t.TypeName ?? string.Empty);
        List<string> extAllowedTypes = [];
        Dictionary<string, List<string>> extReplaceableTypes = [];
        List<string> extMappedTypes = [];

        HashSet<string> quantityProfilesMovedToTypes = [];

        // categorize the types based on how we process them
        foreach (string valueTypeName in elementValueTypes.Select(t => t.TypeName ?? string.Empty).Distinct())
        {
            if (targetPackageSupport.AllowedExtensionTypes.Contains(valueTypeName))
            {
                // check for this being the "Quantity" type to do special type profile handling
                if (valueTypeName == "Quantity")
                {
                    List<string> typeProfiles = collectedValueTypes[valueTypeName].Select(t => t.TypeProfile).Where(t => t != null)!.ToList<string>();

                    if (typeProfiles.Count > 0)
                    {
                        // check the profiled types
                        foreach (string typeProfile in typeProfiles)
                        {
                            string tpShort = typeProfile.Split('/')[^1];
                            if (targetPackageSupport.AllowedExtensionTypes.Contains(tpShort))
                            {
                                quantityProfilesMovedToTypes.Add(tpShort);
                                extAllowedTypes.Add(tpShort);
                            }
                        }

                        // skip this quantity if it was only this type
                        if (typeProfiles.Count == quantityProfilesMovedToTypes.Count)
                        {
                            continue;
                        }
                    }
                }

                extAllowedTypes.Add(valueTypeName);
                continue;
            }

            if (FhirTypeMappings.PrimitiveTypeFallbacks.TryGetValue(valueTypeName, out string? replacementType))
            {
                if (!extReplaceableTypes.TryGetValue(replacementType, out List<string>? replaceableTypes))
                {
                    replaceableTypes = [];
                    extReplaceableTypes.Add(replacementType, replaceableTypes);
                }
                extReplaceableTypes[replacementType].Add(valueTypeName);
                continue;
            }

            extMappedTypes.Add(valueTypeName);
        }

        HashSet<string> usedTypes = [];

        // process mapped types (extension before value)
        foreach (string typeName in extMappedTypes)
        {
            addDatatypeExtension(
                extSd,
                element,
                sourcePackageSupport,
                ref extBuilderRec,
                extElementId,           //extElementId + ".value[x].extension",
                extElementPath,         //extElementPath + ".value[x].extension",
                typeName);

            // resolve this structure
            DbStructureDefinition? etSd = DbStructureDefinition.SelectSingle(
                _db!.DbConnection,
                FhirPackageKey: sourcePackageSupport.Package.Key,
                Name: typeName);

            if (etSd == null)
            {
                continue;
            }

            // get the root element of the structure
            DbElement etRootElement = DbElement.SelectSingle(_db!.DbConnection, StructureKey: etSd.Key, ResourceFieldOrder: 0)
                ?? throw new Exception($"Failed to resolve the root element of {etSd.Name} ({etSd.Key})");

            // get the child elements for the structure
            List<DbElement> etElements = DbElement.SelectList(
                _db!.DbConnection,
                StructureKey: etSd.Key,
                ParentElementKey: etRootElement.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

            // iterate over the elements to add them to the extension
            foreach (DbElement etElement in etElements)
            {
                ExtElementBuilderRecord? childRec = addElementToExtension(
                    extSd,
                    $"{extElementId}.extension:{etElement.Name}",
                    $"{extElementPath}.extension",
                    etElement.Name,
                    sourcePackageSupport,
                    targetPackageSupport,
                    sd,
                    etElement,
                    relevantComparisons,
                    xverValueSets,
                    null,
                    isExtensionOnBasic);

                if (childRec != null)
                {
                    extBuilderRec.Extensions.Add(childRec);
                }
            }
        }

        // process replaced quantity types
        foreach (string typeName in quantityProfilesMovedToTypes)
        {
            if (usedTypes.Contains(typeName))
            {
                continue;
            }

            // add the value element if we are supposed to
            extBuilderRec.ValueElement = extensionEdValue;

            // consolidate profiles
            List<string> typeProfiles = collectedValueTypes.Contains(typeName) ? collectedValueTypes[typeName].Select(t => t.TypeProfile).Where(t => t != null)!.ToList<string>() : [];
            List<string> targetProfiles = collectedValueTypes.Contains(typeName) ? collectedValueTypes[typeName].Select(t => t.TargetProfile).Where(t => t != null)!.ToList<string>() : [];

            // create a new type reference
            ElementDefinition.TypeRefComponent? edValueType = new()
            {
                Code = typeName,
                ProfileElement = typeProfiles.Select(v => new Canonical(v)).ToList(),
                TargetProfileElement = targetProfiles.Select(v => new Canonical(v)).ToList(),
            };

            extensionEdValue.Type.Add(edValueType);

            // check to see if we use the type to contain data from another type too (need extensions)
            if (extReplaceableTypes.TryGetValue(typeName, out List<string>? replaceableTypes))
            {
                // add each of the replaceable types
                foreach (string rt in replaceableTypes)
                {
                    addDatatypeExtension(
                        extSd,
                        element,
                        sourcePackageSupport,
                        ref extBuilderRec,
                        extElementId,           //extElementId + ".value[x].extension",
                        extElementPath,         //extElementPath + ".value[x].extension",
                        rt);
                }
            }

            usedTypes.Add(typeName);
        }

        HashSet<string> contextReferenceTargets = [];
        if (contextElements != null)
        {
            // add the context elements to the extension
            foreach (DbElement contextElement in contextElements)
            {
                // get any reference element types for this element
                List<DbElementType> referenceTypes = DbElementType.SelectList(
                    _db!.DbConnection,
                    ElementKey: contextElement.Key,
                    TypeName: "Reference");

                foreach (DbElementType rt in referenceTypes)
                {
                    // if we have a target profile, add it to the extension
                    if (!string.IsNullOrEmpty(rt.TargetProfile))
                    {
                        contextReferenceTargets.Add(rt.TargetProfile);
                    }
                }
            }
        }

        // process allowed and replaceable types
        foreach (string typeName in extAllowedTypes)
        {
            if (usedTypes.Contains(typeName))
            {
                continue;
            }

            // add the value element if we are supposed to
            extBuilderRec.ValueElement = extensionEdValue;

            // consolidate profiles
            List<string> typeProfiles = collectedValueTypes[typeName].Select(t => t.TypeProfile).Where(t => t != null)!.ToList<string>();
            HashSet<string> targetProfiles = [];   //  collectedValueTypes[typeName].Select(t => t.TargetProfile).Where(t => t != null)!.ToList<string>();

            // build our target profiles - if the target resource is available, we can use that, otherwise use the profile we are creating
            foreach (string? tp in collectedValueTypes[typeName].Select(t => t.TargetProfile))
            {
                if (tp == null)
                {
                    continue;
                }

                // get the mapped structure URLs for the target sourcePackage
                List<string> mappedUrls = _db!.DbConnection.GetMappedStructureUrls(sourcePackageSupport.Package.Key, tp, targetPackageSupport.Package.Key);

                foreach (string unversionedUrl in mappedUrls)
                {
                    // only add targets that do not exist on the context reference targets
                    if (contextReferenceTargets.Contains(unversionedUrl))
                    {
                        continue;
                    }

                    targetProfiles.Add(unversionedUrl);
                }
            }

            // remove any quantity type profiles that got promoted
            if ((typeName == "Quantity") &&
                (typeProfiles.Count > 0) &&
                (quantityProfilesMovedToTypes.Count > 0))
            {
                List<string> toRemove = typeProfiles.Where(tp => quantityProfilesMovedToTypes.Contains(tp.Split('/')[^1])).ToList();
                foreach (string tr in toRemove)
                {
                    typeProfiles.Remove(tr);
                }
            }

            // create a new type reference
            ElementDefinition.TypeRefComponent? edValueType = new()
            {
                Code = typeName,
                ProfileElement = typeProfiles.Select(v => new Canonical(v)).ToList(),
                TargetProfileElement = targetProfiles.Select(v => new Canonical(v)).ToList(),
            };

            extensionEdValue.Type.Add(edValueType);

            // check to see if we use the type to contain data from another type too (need extensions)
            if (extReplaceableTypes.TryGetValue(typeName, out List<string>? replaceableTypes))
            {
                // add each of the replaceable types
                foreach (string rt in replaceableTypes)
                {
                    addDatatypeExtension(
                        extSd,
                        element,
                        sourcePackageSupport,
                        ref extBuilderRec,
                        extElementId,           //extElementId + ".value[x].extension",
                        extElementPath,         //extElementPath + ".value[x].extension",
                        rt);
                }
            }

            usedTypes.Add(typeName);
        }

        // check for any missed replaceable types
        foreach ((string typeName, List<string> replaceableTypes) in extReplaceableTypes)
        {
            if (usedTypes.Contains(typeName))
            {
                continue;
            }

            // add the value element if we are supposed to
            extBuilderRec.ValueElement = extensionEdValue;

            // create a new type reference
            ElementDefinition.TypeRefComponent? edValueType = new()
            {
                Code = typeName,
            };

            extensionEdValue.Type.Add(edValueType);

            // add each of the replaceable types
            foreach (string rt in replaceableTypes)
            {
                addDatatypeExtension(
                    extSd,
                    element,
                    sourcePackageSupport,
                    ref extBuilderRec,
                    extElementId,           //extElementId + ".value[x].extension",
                    extElementPath,         //extElementPath + ".value[x].extension",
                    rt);
            }

            usedTypes.Add(typeName);
        }

        return extBuilderRec;
    }


    private void addDatatypeExtension(
        StructureDefinition extSd,
        DbElement sourceDbElement,
        PackageXverSupport sourcePackageSupport,
        ref ExtElementBuilderRecord extBuilderRecord,
        string parentId,
        string parentPath,
        string typeName)
    {
        // if we don't have the element already, we need to create the whole set
        if (extBuilderRecord.DatatypeValueElement is null)
        {
            extBuilderRecord.DatatypeSliceElement = new()
            {
                ElementId = parentId + ".extension:_datatype",
                Path = parentPath + ".extension",
                SliceName = "_datatype",
                Short = $"Data type name for {sourceDbElement.Id} from FHIR {sourcePackageSupport.Package.ShortName}",
                Definition = $"Data type name for {sourceDbElement.Id} from FHIR {sourcePackageSupport.Package.ShortName}",
                Min = 0,
                Max = "1",
                Type = [
                    new()
                    {
                        Code = "Extension",
                        Profile = ["http://hl7.org/fhir/StructureDefinition/_datatype"],
                    }
                ],
            };

            extBuilderRecord.DatatypeValueElement = new()
            {
                ElementId = parentId + ".extension:_datatype.value[x]",
                Path = parentPath + ".extension.value[x]",
                Comment = $"Must be: {typeName}",
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
                Fixed = new FhirString(typeName),
            };

            extBuilderRecord.ExtendedDatatypeNames = [typeName];

            // done
            return;
        }

        // need to add this type
        extBuilderRecord.ExtendedDatatypeNames.Add(typeName);
        extBuilderRecord.DatatypeValueElement.Fixed = null;
        extBuilderRecord.DatatypeValueElement.Comment += "|" + typeName;
    }

    [Obsolete]
    private List<DbElement> discoverContexts(
        HashSet<string> contextElementPaths,
        int sourceIndex,
        int targetIndex,
        PackageXverSupport targetPackageSupport,
        DbElement element,
        Dictionary<int, List<DbGraphSd.DbElementRow>> elementProjectionDict)

    {
        List<DbElement> contextElements = [];

        // iterate over the element projection rows
        foreach (DbGraphSd.DbElementRow elementRow in elementProjectionDict[element.Key])
        {
            // extract the element cell for this target
            DbGraphSd.DbElementCell? eCell = elementRow[targetIndex];

            // if the cell is not null, use the target path from the cell
            if (eCell != null)
            {
                contextElements.Add(eCell.Element);
                contextElementPaths.Add(eCell.Element.Path);
                continue;
            }

            // need to try and find a parent for a path
            bool addedSomething = false;
            int? parentKey = element.ParentElementKey;
            while (parentKey != null)
            {
                int key = parentKey.Value;
                parentKey = null;

                if (elementProjectionDict.TryGetValue(key, out List<DbGraphSd.DbElementRow>? parentRows))
                {
                    foreach (DbGraphSd.DbElementRow parentRow in parentRows)
                    {
                        // only match the equivalent row number
                        if (parentRow.SdRowRowId != elementRow.SdRowRowId)
                        {
                            continue;
                        }

                        // extract the element cell for this target
                        DbGraphSd.DbElementCell? parentCell = parentRow[targetIndex];
                        if (parentCell != null)
                        {
                            if (!contextElementPaths.Contains(parentCell.Element.Path))
                            {
                                contextElements.Add(parentCell.Element);
                                contextElementPaths.Add(parentCell.Element.Path);
                            }
                            addedSomething = true;
                            break;
                        }

                        DbGraphSd.DbElementCell? contextCell = parentRow[sourceIndex];
                        if (contextCell != null)
                        {
                            if (contextCell.Element.ResourceFieldOrder == 0)
                            {
                                // add this as the context
                                if (!contextElementPaths.Contains(contextCell.Element.Path))
                                {
                                    contextElements.Add(contextCell.Element);
                                    contextElementPaths.Add(contextCell.Element.Path);
                                }
                                addedSomething = true;
                                break;
                            }

                            parentKey = contextCell.Element.ParentElementKey;
                            break;
                        }
                    }
                }
            }

            if (!addedSomething)
            {
                // if we can't find anything that matches, see if this structure exists in the target
                string name = element.Path.Split('.')[0];

                if ((DbStructureDefinition.SelectCount(_db!.DbConnection, FhirPackageKey: targetPackageSupport.Package.Key, Id: name) != 0) ||
                    (targetPackageSupport.CoreDC?.ComplexTypesByName.ContainsKey(name) == true) ||
                    (targetPackageSupport.CoreDC?.ResourcesByName.ContainsKey(name) == true))
                {
                    if (!contextElementPaths.Contains(name))
                    {
                        contextElementPaths.Add(name);
                        DbElement? dbElement = DbElement.SelectSingle(
                            _db!.DbConnection,
                            FhirPackageKey: targetPackageSupport.Package.Key,
                            Id: name);
                        if (dbElement != null)
                        {
                            contextElements.Add(dbElement);
                        }
                    }
                }

                // if we do not find *anything* that matches, the caller will default by adding Element
            }
        }

        return contextElements;
    }


    private (string? shortText, string? definition, string? comment) getTextForExtensionElement(DbElement ed, string? reason)
    {
        List<string> strings = [];

        if (!string.IsNullOrEmpty(ed.Short))
        {
            strings.Add(ed.Short);
        }

        if (!string.IsNullOrEmpty(ed.Definition) &&
            !ed.Definition.Equals(ed.Short, StringComparison.Ordinal) &&
            !ed.Definition.Equals(ed.Short + ".", StringComparison.Ordinal))
        {
            strings.Add(ed.Definition);
        }

        if (!string.IsNullOrEmpty(reason))
        {
            strings.Add(reason!);
        }

        switch (strings.Count)
        {
            case 0:
                return (null, null, null);

            case 1:
                return (strings[0], null, null);

            case 2:
                return (strings[0], strings[1], null);

            default:
                return (strings[0], strings[1], string.Join("\n", strings.Skip(2)));
        }
    }


    private string collapsePathForId(string path)
    {
        string pathClean = path.Replace("[x]", string.Empty);
        string[] components = pathClean.Replace("[x]", string.Empty).Split('.');
        switch (components.Length)
        {
            case 0:
                return pathClean;

            case 1:
                return pathClean;

            case 2:
                {
                    if (pathClean.Length > 45)
                    {
                        string rName = (components[0].Length > 20)
                            ? new string(components[0].Where(char.IsUpper).ToArray())
                            : components[0];

                        string eName = (components[1].Length > 20)
                            ? $"{components[1][0]}" + new string(components[1].Where(char.IsUpper).ToArray())
                            : components[1];

                        return rName + "." + eName;
                    }

                    return pathClean;
                }

            default:
                {
                    // use the full first and last, and one character from each in-between
                    if (components[0].Length > 20)
                    {
                        components[0] = new string(components[0].Where(char.IsUpper).ToArray());
                    }

                    for (int i = 1; i < components.Length - 1; i++)
                    {
                        if (components[i].Length > 3)
                        {
                            components[i] = $"{components[i][0]}{components[i][1]}";
                        }
                    }

                    if (components.Last().Length > 20)
                    {
                        components[components.Length - 1] = $"{components[components.Length - 1][0]}" + new string(components[0].Where(char.IsUpper).ToArray());
                    }

                    return string.Join('.', components);
                }

        }
    }


    private Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> buildXverValueSets(
        List<DbFhirPackage> packages,
        int sourcePackageIndex)
    {
        DbFhirPackage sourcePackage = packages[sourcePackageIndex];

        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets = [];

        // get the list of value sets in this version that have a required binding
        List<DbValueSet> valueSets = DbValueSet.SelectList(
            _db!.DbConnection,
            FhirPackageKey: sourcePackage.Key);

        // iterate over the value sets
        foreach (DbValueSet vs in valueSets)
        {
            // skip excluded content and value sets that cannot expand
            if (_exclusionSet.Contains(vs.UnversionedUrl) ||
                _exclusionSet.Contains(vs.VersionedUrl) ||
                (vs.IsExcluded == true))
            {
                continue;
            }

            // build a graph for this value set
            DbGraphVs vsGraph = new()
            {
                DB = _db!.DbConnection,
                Packages = packages,
                KeyVs = vs,
            };

            // build a dictionary of all concept projections, by concept key
            Dictionary<int, List<DbGraphVs.DbVsConceptRow>> conceptProjectionDict = [];

            if (vs.CanExpand)
            {
                foreach (DbGraphVs.DbVsRow vsRow in vsGraph.Projection)
                {
                    List<DbGraphVs.DbVsConceptRow> conceptProjections = vsRow.Projection;
                    foreach (DbGraphVs.DbVsConceptRow vsConceptRow in conceptProjections)
                    {
                        if (vsConceptRow.KeyCell == null)
                        {
                            continue;
                        }

                        if (!conceptProjectionDict.TryGetValue(vsConceptRow.KeyCell.Concept.Key, out List<DbGraphVs.DbVsConceptRow>? conceptList))
                        {
                            conceptList = [];
                            conceptProjectionDict.Add(vsConceptRow.KeyCell.Concept.Key, conceptList);
                        }

                        conceptList.Add(vsConceptRow);
                    }
                }
            }

            // build the value sets for this sourcePackage
            buildXverValueSets(
                packages,
                sourcePackageIndex,
                vs,
                conceptProjectionDict,
                xverValueSets);
        }

        return xverValueSets;
    }

    private void buildXverValueSets(
        List<DbFhirPackage> packages,
        int sourcePackageIndex,
        DbValueSet sourceVs,
        Dictionary<int, List<DbGraphVs.DbVsConceptRow>> conceptProjectionDict,
        Dictionary<(int sourceVsKey, int targetPackageId), ValueSet> xverValueSets,
        HashSet<int>? conceptsWithoutEquivalent = null,
        ValueSet? xverVs = null,
        int currentPackageIndex = -1,
        int targetPackageIndex = -1)
    {
        // check for starting conditions
        if ((currentPackageIndex == -1) ||
            (targetPackageIndex == -1))
        {
            // if we are not the last sourcePackage, build upwards
            if (sourcePackageIndex < (packages.Count - 1))
            {
                buildXverValueSets(
                    packages,
                    sourcePackageIndex,
                    sourceVs,
                    conceptProjectionDict,
                    xverValueSets,
                    conceptsWithoutEquivalent,
                    xverVs,
                    currentPackageIndex: sourcePackageIndex,
                    targetPackageIndex: sourcePackageIndex + 1);
            }

            // if we are not the first sourcePackage, build downwards
            if (sourcePackageIndex > 0)
            {
                buildXverValueSets(
                    packages,
                    sourcePackageIndex,
                    sourceVs,
                    conceptProjectionDict,
                    xverValueSets,
                    conceptsWithoutEquivalent,
                    xverVs,
                    currentPackageIndex: sourcePackageIndex,
                    targetPackageIndex: sourcePackageIndex - 1);
            }

            // done
            return;
        }

        bool testingRight = currentPackageIndex < targetPackageIndex;
        bool testingLeft = !testingRight;
        conceptsWithoutEquivalent ??= [];

        DbFhirPackage sourcePackage = packages[sourcePackageIndex];
        DbFhirPackage targetPackage = packages[targetPackageIndex];

        string xverPackageId = getPackageId(sourcePackage, targetPackage);

        //string sourceDashTarget = $"{focusPackage.ShortName}-{targetPackage.ShortName}";
        string vsIdLong = $"{sourcePackage.ShortName}-{sourceVs.Id}-for-{targetPackage.ShortName}";
        string vsId;
        //string vsId = $"{sourceDashTarget}-{sourceSd.IdLong}";

        if (vsIdLong.Length > 64)
        {
            string[] sourceIdComponents = sourceVs.Id.Split('-');
            if (sourceVs.Id.StartsWith("v3-", StringComparison.Ordinal) ||
                sourceVs.Id.StartsWith("v2-", StringComparison.Ordinal))
            {
                // the second component is a PascalCase name, extract it into components - e.g. ActInvoiceElementModifier -> [Act, Invoice, Element, Modifier]
                string[] pascalComponents = Regex.Matches(sourceIdComponents[1], @"([A-Z][a-z0-9]+)")
                    .Select(m => m.Value)
                    .ToArray();

                // use the prefix (v2 or v3) plus the first word, capitals in the middle, and the last word
                // e.g. v3-ActInvoiceElementModifier -> v3ActIEModifier
                vsId = $"{sourcePackage.ShortName}" +
                    $"-{sourceIdComponents[0]}" +
                    $"{pascalComponents[0]}" +
                    $"{string.Join(string.Empty, pascalComponents[1..^1].Select(c => c[0]))}" +
                    $"{pascalComponents[^1]}" +
                    $"-for-{targetPackage.ShortName}";

            }
            else if (sourceIdComponents.Length > 2)
            {
                // use the first and last components completely, but abbreviate the middle components
                vsId = $"{sourcePackage.ShortName}" +
                    $"-{sourceIdComponents[0]}" +
                    $"-{string.Join('-', sourceIdComponents.Skip(1).Take(sourceIdComponents.Length - 2).Select(c => c.Substring(0, 3)))}" +
                    $"-{sourceIdComponents[^1]}" +
                    $"-for-{targetPackage.ShortName}";
            }
            else
            {
                // truncate the source ID so it all fits
                vsId = $"{sourcePackage.ShortName}-{sourceVs.Id.Substring(0, 50)}-for-{targetPackage.ShortName}";
            }
        }
        else
        {
            vsId = vsIdLong;
        }

        ValueSet vs = new()
        {
            Url = $"http://hl7.org/fhir/{sourcePackage.FhirVersionShort}/ValueSet/{vsIdLong}",
            Id = vsId,
            Version = _crossDefinitionVersion,
            Name = FhirSanitizationUtils.ReformatIdForName(vsId),
            Title = $"Cross-version VS for {sourcePackage.ShortName}.{sourceVs.Name} for use in FHIR {targetPackage.ShortName}",
            Status = PublicationStatus.Active,
            Experimental = false,
            UseContext = sourceVs.UseContexts,
            Jurisdiction = sourceVs.Jurisdictions,
            DateElement = new FhirDateTime(DateTimeOffset.Now),
            Description = $"This cross-version ValueSet represents concepts from {sourceVs.VersionedUrl} for use in FHIR {targetPackage.ShortName}." +
                    $" Concepts not present here have direct `equivalent` mappings crossing all versions from {sourcePackage.ShortName} to {targetPackage.ShortName}.",
            Compose = new()
            {
                Include = [],
            },
            Expansion = new()
            {
                TimestampElement = new FhirDateTime(DateTimeOffset.Now),
                Contains = [],
            },
        };

        // check to see if we should set various root extensions
        if (sourceVs.FhirMaturity != null)
        {
            vs.AddExtension(CommonDefinitions.ExtUrlFmm, new Integer(sourceVs.FhirMaturity));
        }

        // FHIR-I is the default WG responsible if none are specified
        string wg = CommonDefinitions.ResolveWorkgroup(sourceVs.WorkGroup, "fhir");

        // add the work group extension
        vs.AddExtension(CommonDefinitions.ExtUrlWorkGroup, new Hl7.Fhir.Model.Code(wg));

        // ensure there is a publisher, use the WG if there is none
        vs.Publisher = CommonDefinitions.WorkgroupNames[wg];

        // ensure there is a contact point - use the default WG unless there are multiple entries
        if ((vs.Contact == null) || (vs.Contact.Count < 2))
        {
            vs.Contact = [
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

        vs.cgAddPackageSource(xverPackageId, _crossDefinitionVersion, null);

        // check for unexpandable value sets (use the compose)
        if ((sourceVs.CanExpand == false) ||
            (conceptProjectionDict.Count == 0))
        {
            // use the existing compose
            vs.Compose = sourceVs.Compose;

            // will not have an expansion
            vs.Expansion = null;

            // add this value set to the dictionary
            xverValueSets.Add((sourceVs.Key, targetPackage.Key), vs);
        }
        else
        {
            Dictionary<string, ValueSet.ConceptSetComponent> composeIncludes = [];

            // if we have an existing VS, start with the compose and expansion from that one (note that nonEquivalentConceptKeys will already be populated)
            if (xverVs != null)
            {
                vs.Compose = (ValueSet.ComposeComponent)xverVs.Compose.DeepCopy();
                foreach (ValueSet.ConceptSetComponent composeInclude in vs.Compose.Include)
                {
                    composeIncludes.Add(composeInclude.System + "|" + composeInclude.Version, composeInclude);
                }

                vs.Expansion = (ValueSet.ExpansionComponent)xverVs.Expansion.DeepCopy();
            }

            // iterate over the projections
            foreach ((int sourceConceptKey, List<DbGraphVs.DbVsConceptRow> conceptProjections) in conceptProjectionDict)
            {
                // skip if we know this concept has already mapped out
                if (conceptsWithoutEquivalent.Contains(sourceConceptKey))
                {
                    continue;
                }

                // check to see if we have any equivalent mappings
                if (testingRight &&
                    conceptProjections.Any((DbGraphVs.DbVsConceptRow vsConceptRow) => vsConceptRow[currentPackageIndex]?.RightComparison?.Relationship == CMR.Equivalent))
                {
                    continue;
                }

                if (testingLeft &&
                    conceptProjections.Any((DbGraphVs.DbVsConceptRow vsConceptRow) => vsConceptRow[currentPackageIndex]?.LeftComparison?.Relationship == CMR.Equivalent))
                {
                    continue;
                }

                // add this concept as not directly equivalent
                conceptsWithoutEquivalent.Add(sourceConceptKey);

                // check to see if we have this concept
                DbValueSetConcept concept = conceptProjections[0].KeyCell?.Concept ?? throw new Exception($"Failed to resolve concept for {sourceConceptKey} in {sourceVs.Name}!");

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
                    vs.Compose.Include.Add(composeInclude);
                }

                composeInclude.Concept.Add(new()
                {
                    Code = concept.Code,
                    Display = concept.Display,
                });

                // add this concept to the expansion
                vs.Expansion.Contains.Add(new()
                {
                    System = concept.System,
                    Version = concept.SystemVersion,
                    Code = concept.Code,
                    Display = concept.Display,
                });
            }

            // add the compose includes to the value set
            vs.Compose.Include = composeIncludes.Values.ToList();

            // add this value set to the dictionary if it has any concepts
            if (vs.Expansion.Contains.Count > 0)
            {
                xverValueSets.Add((sourceVs.Key, targetPackage.Key), vs);
            }
        }

        // check for continuing to the next sourcePackage to the right
        if (testingRight &&
            (targetPackageIndex < packages.Count - 1))
        {
            // build the value set for this sourcePackage
            buildXverValueSets(
                packages,
                sourcePackageIndex,
                sourceVs,
                conceptProjectionDict,
                xverValueSets,
                conceptsWithoutEquivalent,
                vs,
                currentPackageIndex: targetPackageIndex,
                targetPackageIndex: targetPackageIndex + 1);
        }

        // check for continuing to the next sourcePackage to the left
        if (testingLeft &&
            (targetPackageIndex > 0))
        {
            // build the value set for this sourcePackage
            buildXverValueSets(
                packages,
                sourcePackageIndex,
                sourceVs,
                conceptProjectionDict,
                xverValueSets,
                conceptsWithoutEquivalent,
                vs,
                currentPackageIndex: targetPackageIndex,
                targetPackageIndex: targetPackageIndex - 1);
        }

        return;
    }



}
