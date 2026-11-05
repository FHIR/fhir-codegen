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
        // build the main package structure and supporting files
        IgExporter igExporter = new IgExporter(
            _db,
            _loggerFactory,
            _outputPath,
            _crossVersionSourcePath,
            this,
            _config);

        IgExporter.ExportSchedule schedule = igExporter.BuildExportSchedule(maxStepSize, specificPairs);
        if (processStructures)
        {
            UnsafeCodeInputStore.RequireExportReady(_db, schedule.PackagePairs);
        }

        IgExporter.XVerExportTrackingRecord tr = igExporter.CreateInitialXVerIgs(schedule, includeIgScripts);

        // export vocabulary (if requested)
        if (processVocabulary)
        {
            VocabularyFhirExporter vocabularyExporter = new VocabularyFhirExporter(
                this,
                _db,
                _loggerFactory);
            vocabularyExporter.Export(tr);

            VocabularyPageExporter vocabularyPageExporter = new VocabularyPageExporter(
                this,
                _db,
                _loggerFactory);
            vocabularyPageExporter.Export(tr);
        }

        // export structures (if requested)
        if (processStructures)
        {
            StructureFhirExporter structureExporter = new StructureFhirExporter(
                this,
                _db,
                _loggerFactory);
            structureExporter.Export(tr);

            StructurePageExporter structurePageExporter = new StructurePageExporter(
                this,
                _db,
                _loggerFactory);
            structurePageExporter.Export(tr);
        }

        // finalize the IG package
        igExporter.FinalizeXVerIgs(tr);
    }
}
