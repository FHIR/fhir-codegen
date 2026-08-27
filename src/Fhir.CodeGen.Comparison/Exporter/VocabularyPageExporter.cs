using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading.Tasks;
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
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Comparison.Exporter.IgExporter;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Exporter;

public class VocabularyPageExporter
{
    private readonly XVerExporter _exporter;
    private readonly IDbConnection _db;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    public VocabularyPageExporter(
        XVerExporter exporter,
        IDbConnection db,
        ILoggerFactory loggerFactory)
    {
        _exporter = exporter;
        _db = db;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VocabularyPageExporter>();
    }

    public void Export(XVerExportTrackingRecord tr)
    {
        // iterate over the XVer IGs
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            // export package value set index page
            exportVsIndexPage(igTr);

            // export individual value set lookup pages
            exportVsLookupPages(igTr);
        }
    }

    private void exportVsLookupPages(XVerIgExportTrackingRecord igTr)
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

        _logger.LogInformation($"Writing value set lookup pages for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        // get the value set outcomes for this pair
        List<DbValueSetOutcome> vsOutcomes = DbValueSetOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            orderByProperties: [nameof(DbValueSetOutcome.SourceId), nameof(DbValueSetOutcome.TargetId)]);

        string sourceBaseUrl = igTr.PackagePair.SourceFhirSequence.ToWebUrlRoot();
        string targetBaseUrl = igTr.PackagePair.TargetFhirSequence.ToWebUrlRoot();

        // iterate over the outcomes to create lookup pages
        foreach (DbValueSetOutcome vsOutcome in vsOutcomes)
        {
            if (XVer.XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalVersioned) ||
                XVer.XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalUnversioned))
            {
                continue;
            }

            string id = vsOutcome.GenLongId ?? vsOutcome.SourceId;
            id = id.StartsWith("valueset-", StringComparison.OrdinalIgnoreCase)
                ? id["valueset-".Length..]
                : id.StartsWith("valueset", StringComparison.OrdinalIgnoreCase)
                ? id["valueset".Length..]
                : id.StartsWith("vs-", StringComparison.OrdinalIgnoreCase)
                ? id["vs-".Length..]
                : id.StartsWith("vs", StringComparison.OrdinalIgnoreCase)
                ? id["vs".Length..]
                : id;

            // create the lookup file
            string filename = Path.Combine(dir, $"lookup-vs-{id}.md");
            using ExportStreamWriter mdWriter = createMarkdownWriter(filename);

            // write a header
            mdWriter.WriteLine(
                $"### Lookup for [FHIR {igTr.PackagePair.SourceFhirSequence}]({sourceBaseUrl})" +
                $" ValueSet {vsOutcome.SourceName}:[`{vsOutcome.SourceCanonicalUnversioned}`]({sourceBaseUrl}valueset-{vsOutcome.SourceId}.html)" +
                $" for use in [FHIR {igTr.PackagePair.TargetFhirSequence}]({targetBaseUrl})");
            mdWriter.WriteLine();

            if (vsOutcome.TargetId is null)
            {
                mdWriter.WriteLine(
                    $"The FHIR {igTr.PackagePair.SourceFhirSequence} ValueSet {vsOutcome.SourceName} has no mapping " +
                    $" to FHIR {igTr.PackagePair.TargetFhirSequence}.");
            }
            else
            {
                mdWriter.WriteLine(
                    $"The FHIR {igTr.PackagePair.SourceFhirSequence} ValueSet {vsOutcome.SourceName} maps to the " +
                    $" FHIR {igTr.PackagePair.TargetFhirSequence} ValueSet" +
                    $" {vsOutcome.TargetName}:[`{vsOutcome.TargetCanonicalUnversioned}`]({targetBaseUrl}valueset-{vsOutcome.TargetId}.html)");
            }

            if (vsOutcome.RequiresXVerDefinition &&
                (vsOutcome.TargetValueSetKey is not null)) 
            {
                mdWriter.WriteLine();
                mdWriter.WriteLine(
                    $"A computable version of the following mapping information is available in:" +
                    $" [{vsOutcome.GenName}]({vsOutcome.ConceptMapFileName}.html)");
            }

            mdWriter.WriteLine();
            mdWriter.WriteLine($"| Source System | Code | Display | Has XVer | Target System | Code | Display |");
            mdWriter.WriteLine("| -------------- | ---- | ------- | -------- | ------------- | ---- | ------- |");

            // get the concept outcomes for this vs outcome
            List<DbValueSetConceptOutcome> conceptOutcomes = DbValueSetConceptOutcome.SelectList(
                _db,
                ValueSetOutcomeKey: vsOutcome.Key,
                orderByProperties: [
                    nameof(DbValueSetConceptOutcome.SourceSystem),
                    nameof(DbValueSetConceptOutcome.SourceCode),
                    nameof(DbValueSetConceptOutcome.TargetSystem),
                    nameof(DbValueSetConceptOutcome.TargetCode)]);

            // iterate over the element outcomes
            foreach (DbValueSetConceptOutcome conceptOutcome in conceptOutcomes)
            {
                mdWriter.WriteLine(
                    $"| `{conceptOutcome.SourceSystem}`" +
                    $" | `{conceptOutcome.SourceCode.ForMdTable()}`" +
                    $" | {conceptOutcome.SourceDisplay.ForMdTable()}" +
                    $" | {(conceptOutcome.RequiresXVerDefinition ? "Yes" : "No")}" +
                    $" | {(conceptOutcome.TargetSystem is null ? string.Empty : $"`{conceptOutcome.TargetSystem}`")}" +
                    $" | {(conceptOutcome.TargetCode is null ? string.Empty : $"`{conceptOutcome.TargetCode.ForMdTable()}`")}" +
                    $" | {conceptOutcome.TargetDisplay.ForMdTable()}" +
                    $" |");
            }
            mdWriter.WriteLine("{: .grid }");
            mdWriter.WriteLine();

            // finish writing the file
            mdWriter.Close();

            string fn = Path.GetFileName(filename);
            exported.Add(new()
            {
                FileName = fn,
                FileNameWithoutExtension = fn[..^3],
                IsPageContentFile = true,
                Name = vsOutcome.GenName!,
                Id = null,
                Url = null,
                ResourceType = null,
                Version = null,
                Description = vsOutcome.GenName!,
            });
        }

        _logger.LogInformation($"Wrote {exported.Count} value set lookup pages for `{igTr.PackageId}`");
        igTr.VsPageContentFiles.AddRange(exported);
    }

    private void exportVsIndexPage(XVerIgExportTrackingRecord igTr)
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

        _logger.LogInformation($"Writing value set index page for `{igTr.PackageId}`...");

        List<XVerIgFileRecord> exported = [];

        string sourceBaseUrl = igTr.PackagePair.SourceFhirSequence.ToWebUrlRoot();
        string targetBaseUrl = igTr.PackagePair.TargetFhirSequence.ToWebUrlRoot();

        // create the lookup file
        string filename = Path.Combine(dir, "index-vs.md");
        using ExportStreamWriter mdWriter = createMarkdownWriter(filename);

        mdWriter.WriteLine($"### FHIR {igTr.PackageId} Cross-Version Value Set Lookup");
        mdWriter.WriteLine();
        //mdWriter.WriteLine("The following table links to documentation for the source version of FHIR, for implementers to understand if there is an extension for the element they are trying to use.");
        //mdWriter.WriteLine($"These are structures defined in FHIR {igTr.PackagePair.SourceFhirSequence} (the source package), with applicable usage as mapped into FHIR {igTr.PackagePair.TargetFhirSequence} (the target package).");

        mdWriter.WriteLine();
        mdWriter.WriteLine(
            $"| {igTr.PackagePair.SourceFhirSequence} ValueSet" +
            $" | {igTr.PackagePair.TargetFhirSequence} ValueSet" +
            $" | Lookup File" +
            $" | XVer Concept Map" +
            $" |");
        mdWriter.WriteLine("| --------- | ----------- | ----------- | ----------- |");

        // get the structure outcomes for this pair
        List<DbValueSetOutcome> vsOutcomes = DbValueSetOutcome.SelectList(
            _db,
            SourceFhirPackageKey: igTr.PackagePair.SourcePackageKey,
            TargetFhirPackageKey: igTr.PackagePair.TargetPackageKey,
            orderByProperties: [nameof(DbValueSetOutcome.SourceId), nameof(DbStructureOutcome.TargetId)]);

        // iterate over the outcomes
        foreach (DbValueSetOutcome vsOutcome in vsOutcomes)
        {
            if (XVer.XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalVersioned) ||
                XVer.XVerProcessor._exclusionSet.Contains(vsOutcome.SourceCanonicalUnversioned))
            {
                continue;
            }

            string target = vsOutcome.TargetId is null
                ? "n/a"
                : $"[{vsOutcome.TargetId}]({targetBaseUrl}valueset-{vsOutcome.TargetId}.html)";

            string id = vsOutcome.GenLongId ?? vsOutcome.SourceId;
            id = id.StartsWith("valueset-", StringComparison.OrdinalIgnoreCase)
                ? id["valueset-".Length..]
                : id.StartsWith("valueset", StringComparison.OrdinalIgnoreCase)
                ? id["valueset".Length..]
                : id.StartsWith("vs-", StringComparison.OrdinalIgnoreCase)
                ? id["vs-".Length..]
                : id.StartsWith("vs", StringComparison.OrdinalIgnoreCase)
                ? id["vs".Length..]
                : id;

            string cm = (vsOutcome.RequiresXVerDefinition && (vsOutcome.TargetValueSetKey is not null))
                ? $"[ConceptMap: {vsOutcome.ConceptMapName}]({vsOutcome.ConceptMapFileName}.html)"
                : "n/a";

            mdWriter.WriteLine(
                $"| [{vsOutcome.SourceId}]({sourceBaseUrl}valueset-{vsOutcome.SourceId}.html)" +
                $" | {target}" +
                $" | [Lookup: {vsOutcome.GenName}](lookup-vs-{id}.html)" +
                $" | {cm}" +
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
            Name = "ValueSet Lookup",
            Id = null,
            Url = null,
            ResourceType = null,
            Version = null,
            Description = "ValueSet Lookup",
        });

        _logger.LogInformation($"Wrote {exported.Count} value set index pages for `{igTr.PackageId}`");
        igTr.VsPageContentFiles.AddRange(exported);
    }
}
