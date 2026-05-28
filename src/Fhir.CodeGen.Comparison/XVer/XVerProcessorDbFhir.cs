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

    private static string getPackageId(DbFhirPackage? sourcePackage, DbFhirPackage targetPackage) => sourcePackage == null
        ? $"hl7.fhir.uv.xver.{targetPackage.ShortName.ToLowerInvariant()}"
        : $"hl7.fhir.uv.xver-{sourcePackage.ShortName.ToLowerInvariant()}.{targetPackage.ShortName.ToLowerInvariant()}";

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
