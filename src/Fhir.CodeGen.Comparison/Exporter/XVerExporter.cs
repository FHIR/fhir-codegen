using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Lib.Configuration;
using Microsoft.Extensions.Logging;

namespace Fhir.CodeGen.Comparison.Exporter;

public class XVerExporter
{
    public enum VersionSpecificExtensionBehaviorCodes
    {
        None,
        ShortVersion,
        TargetVersion,
    }

    public enum VersionSpecificExportCodes
    {
        CurrentVersion,
        TargetVersion,
    }


    private readonly IDbConnection _db;

    private ConfigXVer _config;

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private string _outputPath;
    private string? _crossVersionSourcePath;

    internal string _crossDefinitionVersion;
    internal readonly DateTimeOffset _runTime = DateTimeOffset.UtcNow;

    internal VersionSpecificExtensionBehaviorCodes _versionSpecificExtBehavior = VersionSpecificExtensionBehaviorCodes.ShortVersion;
    internal VersionSpecificExportCodes _versionSpecificExport = VersionSpecificExportCodes.TargetVersion;

    public XVerExporter(
        IDbConnection db,
        ConfigXVer config)
    {
        _loggerFactory = config.LogFactory;
        _logger = _loggerFactory.CreateLogger<XVerExporter>();

        _db = db;
        _config = config;

        _outputPath = config.OutputDirectory;
        _crossVersionSourcePath = string.IsNullOrEmpty(config.CrossVersionMapSourcePath) ? null : config.CrossVersionMapSourcePath;
        _crossDefinitionVersion = string.IsNullOrEmpty(config.XverArtifactVersion) ? "0.1.0" : config.XverArtifactVersion;
    }

    public void Export(
        bool includeIgScripts = true,
        bool processVocabulary = true,
        bool processStructures = true,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        RefreshConfiguration();
        HashSet<(FhirReleases.FhirSequenceCodes, FhirReleases.FhirSequenceCodes)>? invocationPairs =
            specificPairs is null ? null : [.. specificPairs];
        using XVerPreparedOperation operation = XVerPreparedOperation.Prepare(_db,
            new(XVerPreparationKind.ExportContent, maxStepSize: maxStepSize, specificPairs: invocationPairs,
                processVocabulary: false, includeIgScripts: includeIgScripts, configuration: _config));
        // build the main package structure and supporting files
        IgExporter igExporter = new IgExporter(
            _db,
            _loggerFactory,
            _outputPath,
            _crossVersionSourcePath,
            this,
            _config);

        IgExporter.ExportSchedule schedule = igExporter.BuildExportSchedule(maxStepSize, invocationPairs);
        if (processStructures)
        {
            UnsafeCodeInputStore.RequireExportState(_db, schedule.PackagePairs);
        }
        operation.CaptureExportOperands(
            new(processStructures && schedule.PackagePairs.Count > 0 ? XVerPreparationKind.Export : XVerPreparationKind.ExportContent,
                XVer.XVerProcessor._exclusionSet, maxStepSize: maxStepSize, specificPairs: invocationPairs,
                processVocabulary: processVocabulary, includeIgScripts: includeIgScripts, configuration: _config,
                includeStructurePages: processStructures));
        if (processStructures)
        {
            IReadOnlyList<FhirPackageComparisonPair> verified = UnsafeCodeInputStore.RequireExportReady(_db, schedule.PackagePairs, operation);
            if (verified.Count != schedule.PackagePairs.Count)
            {
                if (verified.Count == 0) return;
                schedule = schedule with { PackagePairs = verified, ValidationPackages = [.. schedule.ValidationPackages.Where(
                    package => verified.Any(pair => pair.TargetPackageKey == package.Key))] };
            }
        }

        IgExporter.XVerExportTrackingRecord tr = igExporter.PrepareXVerIgs(schedule);
        VocabularyFhirExporter vocabularyExporter = new(this, _db, _loggerFactory);
        IReadOnlyList<VocabularyFhirExporter.BuiltFile> vocabulary = processVocabulary ? vocabularyExporter.Build(tr) : [];
        StructureFhirExporter? structureExporter = null;
        List<(IgExporter.XVerIgExportTrackingRecord Tracking, StructureFhirExporter.BuiltDefinitions Definitions)> structures = [];
        StructurePageExporter? structurePageExporter = null;
        if (processStructures)
        {
            structureExporter = new(this, _db, _loggerFactory);
            foreach (IgExporter.XVerIgExportTrackingRecord igTr in tr.XVerIgs)
            {
                structures.Add((igTr, structureExporter.BuildDefinitions(igTr, operation)));
            }
            structurePageExporter = new(this, _db, _loggerFactory, operation);
        }

        IReadOnlyList<FhirPackageComparisonPair> audited = UnsafeCodeInputStore.AuditExportReady(_db, processStructures ? schedule.PackagePairs : [], operation);
        if (processStructures && audited.Count != schedule.PackagePairs.Count)
        {
            // The schedule changed after construction; do not publish its stale artifacts.
            return;
        }
        igExporter.MaterializeXVerIgs(tr, includeIgScripts);

        // export vocabulary (if requested)
        if (processVocabulary)
        {
            vocabularyExporter.Emit(vocabulary);

            VocabularyPageExporter vocabularyPageExporter = new VocabularyPageExporter(
                this,
                _db,
                _loggerFactory);
            vocabularyPageExporter.Export(tr);
        }

        // export structures (if requested)
        if (structureExporter is not null)
        {
            foreach ((IgExporter.XVerIgExportTrackingRecord igTr, StructureFhirExporter.BuiltDefinitions definitions) in structures)
            {
                structureExporter.Emit(igTr, definitions, operation);
            }

            structurePageExporter!.Export(tr, operation);
        }

        // finalize the IG package
        igExporter.FinalizeXVerIgs(tr);
    }

    internal ConfigXVer Configuration => _config;

    internal void RefreshConfiguration()
    {
        _outputPath = _config.OutputDirectory;
        _crossVersionSourcePath = string.IsNullOrEmpty(_config.CrossVersionMapSourcePath) ? null : _config.CrossVersionMapSourcePath;
        _crossDefinitionVersion = string.IsNullOrEmpty(_config.XverArtifactVersion) ? "0.1.0" : _config.XverArtifactVersion;
    }
}
