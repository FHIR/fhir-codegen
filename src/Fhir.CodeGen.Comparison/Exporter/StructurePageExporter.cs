using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Language;
using Hl7.Fhir.Language.Debugging;
using Hl7.Fhir.Model;
using Hl7.Fhir.Model.CdsHooks;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.Exporter.IgExporter;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Exporter;

public class StructurePageExporter
{
    private readonly XVerExporter _exporter;
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private static readonly HashSet<string> _exportExclusions = [
        "Base",
        "BackboneType",
        "BackboneElement",
        "Element",
        ];

    public StructurePageExporter(
        XVerExporter exporter,
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _exporter = exporter;
        _db = db;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StructurePageExporter>();
    }

    public void Export(XVerExportTrackingRecord tr)
    {
        // iterate over the XVer IGs
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            // export package structure index page
            exportStructureIndexPage(igTr);

            // export individual structure lookup pages
            exportStructureLookupPages(igTr);
        }
    }

    private void exportStructureLookupPages(XVerIgExportTrackingRecord igTr)
    {
        if (igTr.PageContentDir is null)
        {
            throw new Exception($"IG `{igTr.PackageId}` page content directory not set");
        }

        string dir = igTr.PageContentDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing structure lookup pages for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the structure outcomes for this pair
        List<DbStructureOutcome> sdOutcomes = DbStructureOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            orderByProperties: [nameof(DbStructureOutcome.SourceName), nameof(DbStructureOutcome.TargetName)]);

        string sourceBaseUrl = igTr.PackagePair.SourceFhirSequence.ToWebUrlRoot();
        string targetBaseUrl = igTr.PackagePair.TargetFhirSequence.ToWebUrlRoot();

        // iterate over the outcomes to create lookup pages
        foreach (DbStructureOutcome sdOutcome in sdOutcomes)
        {
            if ((sdOutcome.SourceArtifactClass != FhirArtifactClassEnum.Resource) ||
                _exportExclusions.Contains(sdOutcome.SourceId) ||
                _exportExclusions.Contains(sdOutcome.SourceName))
            {
                continue;
            }

            string id = sdOutcome.GenShortId!;
            id = id.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)
                ? id["profile-".Length..]
                : id.StartsWith("profile", StringComparison.OrdinalIgnoreCase)
                ? id["profile".Length..]
                : id.StartsWith("prfl-", StringComparison.OrdinalIgnoreCase)
                ? id["prfl-".Length..]
                : id.StartsWith("prfl", StringComparison.OrdinalIgnoreCase)
                ? id["prfl".Length..]
                : id;

            // create the lookup file
            string filename = Path.Combine(dir, $"lookup-sd-{id}.md");
            using ExportStreamWriter mdWriter = createMarkdownWriter(filename);

            string targetId = sdOutcome.TargetId ?? "Basic";
            //string elementCmId = $"ConceptMap-{igTr.PackagePair.SourcePackageShortName}-{sdOutcome.SourceId}-elements-for-{igTr.PackagePair.TargetPackageShortName}-{targetId}";
            //string elementCmId = $"conceptmap-{igTr.PackagePair.SourcePackageShortName}-{sdOutcome.SourceId}-elements-for-{igTr.PackagePair.TargetPackageShortName}-{targetId}";

            // write a header
            mdWriter.WriteLine(
                $"### Lookup for [FHIR {igTr.PackagePair.SourceFhirSequence}]({sourceBaseUrl})" +
                $" [{sdOutcome.SourceName}]({sourceBaseUrl}{sdOutcome.SourceName}.html)" +
                $" for use in [FHIR {igTr.PackagePair.TargetFhirSequence}]({targetBaseUrl})");
            mdWriter.WriteLine();
            mdWriter.WriteLine(
                $"The FHIR {igTr.PackagePair.SourceFhirSequence} resource is represented in" +
                $" FHIR {igTr.PackagePair.TargetFhirSequence} via the {targetId} resource.");
            mdWriter.WriteLine();

            mdWriter.WriteLine(
                $"Note that there is a profile defined to simplify use of this cross-version resource representation:" +
                $"[Profile: {id}]({sdOutcome.GenFileName}.html)");
            mdWriter.WriteLine();

            mdWriter.WriteLine(
                $"A computable version of the following element information is available in:" +
                $" [{sdOutcome.ElementConceptMapName}]({sdOutcome.ElementConceptMapFileName!}.html)");
            mdWriter.WriteLine();
            mdWriter.WriteLine($"| Source Element (FHIR {igTr.PackagePair.SourceFhirSequence}) | Target(s) | Comments |");
            mdWriter.WriteLine("| -------------- | ---- | -------- |");

            // get the element outcomes for this structure outcome
            List<DbElementOutcome> edOutcomes = DbElementOutcome.SelectList(
                _db,
                SourceFhirPackageKey: sdOutcome.SourceFhirPackageKey,
                TargetFhirPackageKey: sdOutcome.TargetFhirPackageKey,
                SourceStructureKey: sdOutcome.SourceStructureKey,
                orderByProperties: [nameof(DbElementOutcome.SourceResourceOrder)]);
            ILookup<int, DbElementOutcome> edOutcomesByKey = edOutcomes.ToLookup(edo => edo.Key);

            writeElementTable(
                mdWriter,
                sdOutcome,
                edOutcomes,
                edOutcomesByKey,
                filterToTargetSd: true,
                sourceBaseUrl,
                targetBaseUrl,
                out int targetStructureCount);

            if (targetStructureCount > 1)
            {
                mdWriter.WriteLine();
                mdWriter.WriteLine(
                    $"Note that the FHIR {igTr.PackagePair.SourceFhirSequence} {sdOutcome.SourceId}" +
                    $" maps to multiple resources in FHIR {igTr.PackagePair.TargetFhirSequence}." +
                    $" The following table contains the the combined lookup information for reference.");
                mdWriter.WriteLine();
                mdWriter.WriteLine($"| Source Element (FHIR {igTr.PackagePair.SourceFhirSequence}) | Target(s) | Comments |");
                mdWriter.WriteLine("| -------------- | ---- | -------- |");

                writeElementTable(
                    mdWriter,
                    sdOutcome,
                    edOutcomes,
                    edOutcomesByKey,
                    filterToTargetSd: false,
                    sourceBaseUrl,
                    targetBaseUrl,
                    out _);
            }

            // finish writing the file
            mdWriter.Close();

            string fn = Path.GetFileName(filename);
            exported.Add(new()
            {
                FileName = fn,
                FileNameWithoutExtension = fn[..^3],
                IsPageContentFile = true,
                Name = sdOutcome.GenName!,
                Id = null,
                Url = null,
                ResourceType = null,
                Version = null,
                Description = sdOutcome.GenName!,
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} structure lookup pages for `{igTr.PackageId}`");
        igTr.ResourceLookupFiles.AddRange(exported);
    }

    private void writeElementTable(
        ExportStreamWriter mdWriter,
        DbStructureOutcome sdOutcome,
        List<DbElementOutcome> edOutcomes,
        ILookup<int, DbElementOutcome> edOutcomesByKey,
        bool filterToTargetSd,
        string sourceBaseUrl,
        string targetBaseUrl,
        out int targetStructureCount)
    {
        HashSet<int> targetStructureKeys = sdOutcome.TargetStructureKey is not null
            ? [sdOutcome.TargetStructureKey!.Value]
            : [];
        Dictionary<int, List<(string label, string link)>> outcomeAccumulator = [];

        // iterate over the element outcomes that target this structure
        foreach (DbElementOutcome edOutcome in edOutcomes)
        {
            if (edOutcome.SourceResourceOrder == 0)
            {
                mdWriter.WriteLine(
                    $"| [`{edOutcome.SourceId}`]({sourceBaseUrl}{sdOutcome.SourceName}.html#resource)" +
                    $" | " +
                    $" | " +
                    $" |");
                continue;
            }

            if (edOutcome.ExtensionDefinitionIsProhibited)
            {
                mdWriter.WriteLine(
                    $"| [`{edOutcome.SourceId}`]({sourceBaseUrl}{sdOutcome.SourceName}.html#resource)" +
                    $" | <i>Not Available</i>" +
                    $" | {edOutcome.ExtensionProhibitionReason?.ForMdTable()}" +
                    $" |");

                continue;
            }

            List<string> targetLines = [];
            HashSet<string> usedLinks = [];

            // get the targets for this outcome
            List<DbElementOutcomeTarget> edTargets = DbElementOutcomeTarget.SelectList(
                _db,
                ElementOutcomeKey: edOutcome.Key,
                orderByProperties: [nameof(DbElementOutcomeTarget.TargetElementId)]);

            int allowedTargets = 0;
            foreach (DbElementOutcomeTarget edTarget in edTargets)
            {
                if (edTarget.TargetStructureKey is not null)
                {
                    targetStructureKeys.Add(edTarget.TargetStructureKey.Value);

                    if (filterToTargetSd &&
                        (edTarget.TargetStructureKey != sdOutcome.TargetStructureKey))
                    {
                        continue;
                    }
                }

                allowedTargets++;

                if (edTarget.TargetElementId is not null)
                {
                    // check to see if there is a root target and an extension, in which case the element should not be listed
                    if ((!edOutcome.RequiresExtensionDefinition) ||
                        (edOutcome.RequiresExtensionDefinition && (edTarget.TargetResourceOrder != 0)))
                    {
                        targetLines.Add($"[{edTarget.TargetElementId}]({targetBaseUrl}{edTarget.TargetElementId.Split('.')[0]}.html#resource)");
                    }
                }
            }

            if (allowedTargets == 0)
            {
                mdWriter.WriteLine(
                    $"| [`{edOutcome.SourceId}`]({sourceBaseUrl}{sdOutcome.SourceName}.html#resource)" +
                    $" | <i>Not Available</i>" +
                    $" | " +
                    $" |");

                continue;
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
                string targetLabel;
                string targetLink;

                targetLabel = edOutcome.BasicElementId;
                targetLink = $"{targetBaseUrl}Basic.html#resource";

                if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                {
                    outcomeLines = [];
                    outcomeAccumulator[edOutcome.Key] = outcomeLines;
                }

                outcomeLines.Add((targetLabel, targetLink));
                targetLines.Add($"[{targetLabel}]({targetLink})");
            }

            // check for exporting this outcome as an extension
            if (edOutcome.RequiresExtensionDefinition)
            {
                string targetLabel;
                string targetLink;

                targetLabel = edOutcome.DefineAsModifier
                    ? $"Modifier Extension: {edOutcome.GenName ?? edOutcome.GenUrl!}"
                    : $"Extension: {edOutcome.GenName ?? edOutcome.GenUrl!}";
                targetLink = edOutcome.GenFileName! + ".html";

                if (usedLinks.Add(targetLink))
                {
                    if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                    {
                        outcomeLines = [];
                        outcomeAccumulator[edOutcome.Key] = outcomeLines;
                    }

                    outcomeLines.Add((targetLabel, targetLink));
                    targetLines.Add($"[{targetLabel}]({targetLink})");
                }
            }

            // check for exporting as a slice
            if (edOutcome.RequiresSliceDefinition)
            {
                // check to see if we have a slice definition
                if ((edOutcome.ParentElementOutcomeKey is not null) &&
                    outcomeAccumulator.TryGetValue(edOutcome.ParentElementOutcomeKey.Value, out List<(string label, string link)>? parentOutcomeLines))
                {
                    foreach ((string parentLabel, string parentLink) in parentOutcomeLines)
                    {
                        string targetLabel;
                        string targetLink;

                        if (edOutcomesByKey.Contains(edOutcome.ParentElementOutcomeKey.Value))
                        {
                            if (edOutcome.GenUrl!.StartsWith("http:", StringComparison.Ordinal))
                            {
                                targetLabel = $"Extension: {edOutcomesByKey[edOutcome.ParentElementOutcomeKey.Value].First().GenName} Slice:{edOutcome.SourceNameClean()}";
                            }
                            else
                            {
                                targetLabel = $"Extension: {edOutcomesByKey[edOutcome.ParentElementOutcomeKey.Value].First().GenName} Slice:{edOutcome.GenUrl!}";
                            }
                            targetLink = parentLink;
                        }
                        else
                        {
                            targetLabel = $"Slice: {edOutcome.GenUrl!}";
                            targetLink = parentLink;
                        }

                        if (usedLinks.Add(parentLink))
                        {
                            if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                            {
                                outcomeLines = [];
                                outcomeAccumulator[edOutcome.Key] = outcomeLines;
                            }

                            outcomeLines.Add((targetLabel, targetLink));
                            targetLines.Add($"[{targetLabel}]({targetLink})");
                        }
                    }
                }
                else
                {
                    throw new Exception($"Failed to resolve parent outcome when slice definition is declared!");
                }
            }

            string extLiteral = edOutcome.DefineAsModifier
                ? "Modifier Extension"
                : "Extension";

            // check for extension substitution
            if (edOutcome.ExtensionSubstitutionUrl is not null)
            {
                string targetLabel;
                string targetLink;

                if (isAlternateCanonical)
                {
                    targetLabel = "Standard Extension: alternate-canonical";
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else if (isAlternateReference)
                {
                    targetLabel = "Standard Extension: alternate-reference";
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else if (edOutcome.ExtensionSubstitutionUrl.StartsWith("http://hl7.org/fhir/tools/", StringComparison.Ordinal))
                {
                    targetLabel = $"Tooling {extLiteral}: {edOutcome.ExtensionSubstitutionUrl.Split('/')[^1]}";
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else if (edOutcome.ExtensionSubstitutionUrl.StartsWith("http://hl7.org/fhir/StructureDefinition/", StringComparison.Ordinal))
                {
                    targetLabel = $"Standard {extLiteral}: {edOutcome.ExtensionSubstitutionUrl.Split('/')[^1]}";
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else if (edOutcome.ExtensionSubstitutionUrl.StartsWith("http://hl7.org/fhir/uv/subscriptions-backport/", StringComparison.Ordinal))
                {
                    targetLabel = $"Subscription-Backport {extLiteral}: {edOutcome.ExtensionSubstitutionUrl.Split('/')[^1].Replace("backport-", string.Empty)}";
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else if ((edOutcome.ExtensionSubstitutionKey is not null) &&
                    (DbExtensionSubstitution.SelectSingle(_db, Key: edOutcome.ExtensionSubstitutionKey!.Value) is DbExtensionSubstitution extSub))
                {
                    if (extSub.ReplacementSourcePackage is not null)
                    {
                        targetLabel = edOutcome.DefineAsModifier
                            ? extSub.ReplacementSourcePackage + ": Modifier "
                            : extSub.ReplacementSourcePackage + ": ";
                    }
                    else
                    {
                        targetLabel = $"External {extLiteral}: ";
                    }

                    if (extSub.ReplacementName is not null)
                    {
                        targetLabel += extSub.ReplacementName;
                    }
                    else
                    {
                        targetLabel += edOutcome.ExtensionSubstitutionUrl;
                    }

                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }
                else
                {
                    targetLabel = edOutcome.DefineAsModifier
                        ? ("Modifier " + edOutcome.ExtensionSubstitutionUrl)
                        : edOutcome.ExtensionSubstitutionUrl;
                    targetLink = edOutcome.ExtensionSubstitutionUrl;
                }

                if (usedLinks.Add(targetLink))
                {
                    if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                    {
                        outcomeLines = [];
                        outcomeAccumulator[edOutcome.Key] = outcomeLines;
                    }

                    outcomeLines.Add((targetLabel, targetLink));
                    targetLines.Add($"[{targetLabel}]({targetLink})");
                }
            }

            // check for a content reference link
            if ((edOutcome.RequiresDefinitionAsContentReference == true) &&
                (edOutcome.ContentReferenceExtensionUrl is not null))
            {
                string targetLabel;
                string targetLink;

                if ((edOutcome.ContentReferenceOutcomeKey is not null) &&
                    edOutcomesByKey.Contains(edOutcome.ContentReferenceOutcomeKey.Value))
                {
                    DbElementOutcome crOutcome = edOutcomesByKey[edOutcome.ContentReferenceOutcomeKey.Value].First();
                    targetLabel = $"{extLiteral}: {crOutcome.GenName ?? crOutcome.GenUrl!}";
                    targetLink = crOutcome.GenFileName! + ".html";
                }
                else
                {
                    targetLabel = edOutcome.DefineAsModifier
                        ? ("Modifier " + edOutcome.ContentReferenceExtensionUrl)
                        : edOutcome.ContentReferenceExtensionUrl;
                    targetLink = edOutcome.ContentReferenceExtensionUrl;
                }

                if (usedLinks.Add(targetLink))
                {
                    if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                    {
                        outcomeLines = [];
                        outcomeAccumulator[edOutcome.Key] = outcomeLines;
                    }

                    outcomeLines.Add((targetLabel, targetLink));
                    targetLines.Add($"[{targetLabel}]({targetLink})");
                }
            }

            // check for additional alternate canonical
            if (additionalAlternateCanonical &&
                usedLinks.Add(CommonDefinitions.ExtUrlAlternateCanonical))
            {
                string targetLabel = "Standard Extension: alternate-canonical";
                string targetLink = CommonDefinitions.ExtUrlAlternateCanonical;

                if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                {
                    outcomeLines = [];
                    outcomeAccumulator[edOutcome.Key] = outcomeLines;
                }

                outcomeLines.Add((targetLabel, targetLink));
                targetLines.Add($"[{targetLabel}]({targetLink})");
            }

            // check for additional alternate reference
            if (additionalAlternateReference &&
                usedLinks.Add(CommonDefinitions.ExtUrlAlternateReference))
            {
                string targetLabel = "Standard Extension: alternate-reference";
                string targetLink = CommonDefinitions.ExtUrlAlternateReference;

                if (!outcomeAccumulator.TryGetValue(edOutcome.Key, out List<(string label, string link)>? outcomeLines))
                {
                    outcomeLines = [];
                    outcomeAccumulator[edOutcome.Key] = outcomeLines;
                }

                outcomeLines.Add((targetLabel, targetLink));
                targetLines.Add($"[{targetLabel}]({targetLink})");
            }

            mdWriter.WriteLine(
                $"| [`{edOutcome.SourceId}`]({sourceBaseUrl}{sdOutcome.SourceName}.html#resource)" +
                $" | {string.Join("<br/>", targetLines.Distinct())}" +
                $" | {edOutcome.Comments.ForMdTable()}" +
                $" |");
        }
        mdWriter.WriteLine("{: .grid }");
        mdWriter.WriteLine();

        // set our final structure target count
        targetStructureCount = targetStructureKeys.Count;
    }

    private void exportStructureIndexPage(XVerIgExportTrackingRecord igTr)
    {
        if (igTr.PageContentDir is null)
        {
            throw new Exception($"IG `{igTr.PackageId}` page content directory not set");
        }

        string dir = igTr.PageContentDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Writing structure index page for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        string sourceBaseUrl = igTr.PackagePair.SourceFhirSequence.ToWebUrlRoot();
        string targetBaseUrl = igTr.PackagePair.TargetFhirSequence.ToWebUrlRoot();

        // create the lookup file
        string filename = Path.Combine(dir, "lookup-sd.md");
        using ExportStreamWriter mdWriter = createMarkdownWriter(filename);

        mdWriter.WriteLine($"### FHIR {igTr.PackageId} Cross-Version Artifact Lookup");
        mdWriter.WriteLine();
        mdWriter.WriteLine("The following table links to documentation for the source version of FHIR, for implementers to understand if there is an extension for the element they are trying to use.");
        mdWriter.WriteLine($"These are structures defined in FHIR {igTr.PackagePair.SourceFhirSequence} (the source package), with applicable usage as mapped into FHIR {igTr.PackagePair.TargetFhirSequence} (the target package).");
        mdWriter.WriteLine();

        string cmId = $"ConceptMap-{igTr.PackagePair.SourcePackageShortName}-resources-for-{igTr.PackagePair.TargetPackageShortName}";
        string cmName =
            $"ConceptMap" +
            $"{igTr.PackagePair.SourcePackageShortName.ToPascalCase()}" +
            $"ResourcesFor" +
            $"{igTr.PackagePair.TargetPackageShortName.ToPascalCase()}";

        mdWriter.WriteLine(
            $"A computable version of the following element information is available in:" +
            $" [{cmName}](ConceptMap-{cmId}.html)");
        mdWriter.WriteLine();
        mdWriter.WriteLine(
            $"| {igTr.PackagePair.SourceFhirSequence} Structure" +
            $" | {igTr.PackagePair.TargetFhirSequence} Structure" +
            $" | Lookup File" +
            $" | Profile" +
            $" |");
        mdWriter.WriteLine("| --------- | ----------- | ----------- | ----------- |");

        // get the structure outcomes for this pair
        List<DbStructureOutcome> sdOutcomes = DbStructureOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            orderByProperties: [nameof(DbStructureOutcome.SourceName), nameof(DbStructureOutcome.TargetName)]);

        // iterate over the outcomes
        foreach (DbStructureOutcome sdOutcome in sdOutcomes)
        {
            if ((sdOutcome.SourceArtifactClass != FhirArtifactClassEnum.Resource) ||
                _exportExclusions.Contains(sdOutcome.SourceId) ||
                _exportExclusions.Contains(sdOutcome.SourceName))
            {
                continue;
            }

            string targetId = sdOutcome.TargetId ?? "Basic";

            string id = sdOutcome.GenShortId!;
            id = id.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)
                ? id["profile-".Length..]
                : id.StartsWith("profile", StringComparison.OrdinalIgnoreCase)
                ? id["profile".Length..]
                : id.StartsWith("prfl-", StringComparison.OrdinalIgnoreCase)
                ? id["prfl-".Length..]
                : id.StartsWith("prfl", StringComparison.OrdinalIgnoreCase)
                ? id["prfl".Length..]
                : id;

            mdWriter.WriteLine(
                $"| [{igTr.PackagePair.SourceFhirSequence} {sdOutcome.SourceName}]({sourceBaseUrl}{sdOutcome.SourceId}.html)" +
                $" | [{igTr.PackagePair.TargetFhirSequence} {targetId}]({targetBaseUrl}{targetId}.html)" +
                $" | [XVer Lookup: {id}](lookup-sd-{id}.html)" +
                $" | [XVer Profile: {id}](StructureDefinition-{sdOutcome.GenShortId}.html)" +
                $" |");
        }

        mdWriter.WriteLine("{: .grid }");
        mdWriter.WriteLine();

        mdWriter.Close();

        string fn = Path.GetFileName(filename);
        exported.Add(new()
        {
            FileName = fn,
            FileNameWithoutExtension = fn[..^3],
            IsPageContentFile = true,
            Name = "Structure Lookup",
            Id = null,
            Url = null,
            ResourceType = null,
            Version = null,
            Description = "Structure Lookup",
        });

        _logger.LogInformation($"Wrote {exported.Count} structure index pages for `{igTr.PackageId}`");
        igTr.ResourceLookupFiles.AddRange(exported);
    }
}
