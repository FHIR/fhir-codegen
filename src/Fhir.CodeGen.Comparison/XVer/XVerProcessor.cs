// <copyright file="XVerProcessor.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.Data;
using System.Data.Common;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Language;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging;
using static System.Net.Mime.MediaTypeNames;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;
using Tasks = System.Threading.Tasks;

namespace Fhir.CodeGen.Comparison.XVer;

/// <summary>
/// Provides logging message templates for cross-version processing events.
/// </summary>
internal static partial class XVerProcessorLogMessages
{
    /// <summary>
    /// Logs a warning when cross-version maps could not be loaded for a given key.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="cvMapKey">The cross-version map key.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load requested cross-version maps for {cvMapKey}! Processing will be only algorithmic!")]
    internal static partial void LogMapsNotFound(this ILogger logger, string cvMapKey);

    /// <summary>
    /// Logs a warning when a ValueSet could not be expanded for comparison.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="url">The ValueSet URL.</param>
    /// <param name="details">Additional details about the failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to expand ValueSet {url} for comparison: {details}")]
    internal static partial void LogValueSetNotExpanded(this ILogger logger, string url, string? details);

    /// <summary>
    /// Logs a warning when a ValueSet could not be retrieved from a data collection.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="url">The ValueSet URL.</param>
    /// <param name="dcKey">The data collection key.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to retrieve ValueSet {url} from {dcKey}")]
    internal static partial void LogValueSetNotFound(this ILogger logger, string url, string dcKey);
}

/// <summary>
/// Extension methods for cross-version processing.
/// </summary>
internal static class XVerExtensions
{
    /// <summary>
    /// Escapes special characters for Markdown table output.
    /// </summary>
    /// <param name="value">The string value to escape.</param>
    /// <returns>The escaped string for Markdown tables.</returns>

    internal static string ForMdTable(this string? value) => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("|", "\\|").Replace("\n", "<br/>").Replace("\r", "<br/>");

    /// <summary>
    /// Generates a comparison key for a ValueSet and graph identifier.
    /// </summary>
    /// <param name="vs">The ValueSet.</param>
    /// <param name="graphId">The graph identifier.</param>
    /// <returns>The comparison key string.</returns>
    internal static string ComparisonKey(this ValueSet vs, string graphId) => graphId + "_" + vs.Name.ToPascalCase();
}

/// <summary>
/// Processes cross-version FHIR package comparisons, generates artifacts, and manages database operations.
/// </summary>
public partial class XVerProcessor
{
    internal const string _canonicalRootCrossVersion = "http://hl7.org/fhir/uv/xver/";
    private string _crossDefinitionVersion = "0.0.1-snapshot-2";
    private static readonly DateTimeOffset _runTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// The directions for comparison (Up and Down).
    /// </summary>
    internal static readonly ComparisonDirection[] _directions = [ComparisonDirection.Up, ComparisonDirection.Down];

    /// <summary>
    /// Set of ValueSet and CodeSystem URLs to exclude from processing.
    /// </summary>
    internal static readonly HashSet<string> _exclusionSet =
    [
        "http://hl7.org/fhir/ValueSet/ucum-units",
        "http://hl7.org/fhir/ValueSet/all-languages",
        "http://tools.ietf.org/html/bcp47",             // DSTU2 version of all-languages
        "http://hl7.org/fhir/ValueSet/mimetypes",
        "http://www.rfc-editor.org/bcp/bcp13.txt",      // BCP 13 version of mimetypes
        "http://hl7.org/fhir/ValueSet/timezones",
        //"http://terminology.hl7.org/CodeSystem/operation-outcome|2.0.0",    // R5 duplication between core and THO
        //"http://hl7.org/fhir/ValueSet/audit-event-outcome-detail|5.0.0",    // R5 duplication between core and THO
        //"http://hl7.org/fhir/ValueSet/use-context",
        //"http://hl7.org/fhir/ValueSet/jurisdiction",
    ];

    /// <summary>
    /// Set of codes considered as "escape valve" codes (e.g., OTHER, UNKNOWN).
    /// </summary>
    internal static readonly HashSet<string> _escapeValveCodes = [
        "OTHER",
        "Other",
        "other",
        "OTH",      // v3 Null Flavor of other
        "UNKNOWN",
        "Unknown",
        "unknown",
        "UNK",      // v3 Null Flavor of Unknown
        //"NI",       // v3 Null Flavor of No Information
        ];

    private ConfigXVer _config;
    private ILoggerFactory _loggerFactory;
    private ILogger _logger;
    private DefinitionCollection[] _definitions = [];
    private Dictionary<string, int> _definitionIndexes = [];
    private ComparisonDatabase? _db = null;
    private Dictionary<string, HashSet<string>> _vsUrlsToInclude = [];

    private string _dbPath;
    private string? _dbName;

    /// <summary>
    /// Initializes a new instance of the <see cref="XVerProcessor"/> class using configuration.
    /// </summary>
    /// <param name="config">The cross-version configuration.</param>
    public XVerProcessor(ConfigXVer config)
    {
        _config = config;
        _loggerFactory = config.LogFactory;
        _logger = config.LogFactory.CreateLogger<XVerProcessor>();

        string path = string.IsNullOrEmpty(_config.CrossVersionDbPath)
            ? Path.Combine(_config.CrossVersionMapSourcePath, "db")
            : _config.CrossVersionDbPath;

        if (path.EndsWith(".sqlite") || path.EndsWith(".db"))
        {
            _dbPath = Path.GetDirectoryName(path) ?? path;
            _dbName = Path.GetFileName(path) ?? path;
        }
        else
        {
            _dbPath = path;
            _dbName = null;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XVerProcessor"/> class using an existing database.
    /// </summary>
    /// <param name="db">The comparison database.</param>
    /// <param name="outputDirectory">The output directory for artifacts.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public XVerProcessor(ComparisonDatabase db, string outputDirectory, ILoggerFactory loggerFactory)
    {
        _config = new()
        {
            CrossVersionDbPath = Path.Combine(db.DbFilePath, db.DbFileName),
            OutputDirectory = outputDirectory,
            LogFactory = loggerFactory,
        };

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<XVerProcessor>();

        _dbPath = db.DbFilePath;
        _dbName = db.DbFileName;

        _db = db;
    }

    /// <summary>
    /// Loads FHIR package definition collections based on configuration.
    /// </summary>
    private void loadDefinitionCollections()
    {
        List<DefinitionCollection> definitions = [];

        foreach (string directive in _config.ComparePackages)
        {
            if (FhirPackageUtils.PackageIsFhirCore(directive))
            {
                throw new Exception($"Package {directive} is not a FHIR Core package!");
            }

            // create a loader because these are all different FHIR core versions
            using Lib.Loader.PackageLoader loader = new(_config, new()
            {
                JsonModel = Lib.Loader.LoaderOptions.JsonDeserializationModel.SystemTextJson,
            });

            List<string> loadDirectives = FhirReleases.TryGetSequence(directive, out FhirReleases.FhirSequenceCodes packageSequence)
                ? packageSequence switch
                {
                    FhirReleases.FhirSequenceCodes.R5 => [ directive, "hl7.terminology@5.1.0"],
                    _ => [ directive ]
                }
                : [directive];

            DefinitionCollection loaded = loader.LoadPackages(loadDirectives).Result
                ?? throw new Exception($"Could not load package: {directive}");

            definitions.Add(loaded);
        }

        _definitions = definitions.ToArray();
        _definitions.ForEach((DefinitionCollection dc, int i) =>
        {
            _definitionIndexes.Add(dc.Key, i);
            return true;
        });
    }

    /// <summary>
    /// Processes a command to perform cross-version operations such as database creation, comparison, or documentation generation.
    /// </summary>
    /// <param name="command">The command to process.</param>
    public void ProcessCommand(string? command)
    {
        switch (command)
        {
            case "wip":

                HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null;
                specificPairs = [
                    //(FhirReleases.FhirSequenceCodes.DSTU2, FhirReleases.FhirSequenceCodes.R4),
                    //(FhirReleases.FhirSequenceCodes.DSTU2, FhirReleases.FhirSequenceCodes.STU3),
                    //(FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B),
                    //(FhirReleases.FhirSequenceCodes.R4B, FhirReleases.FhirSequenceCodes.R4),
                    //(FhirReleases.FhirSequenceCodes.R5, FhirReleases.FhirSequenceCodes.R4B),
                    //(FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R5),
                    (FhirReleases.FhirSequenceCodes.R5, FhirReleases.FhirSequenceCodes.R4),
                ];

                //UpdateValueSetMaps();

                //LoadDatabase(true);

                //LoadFhirCrossVersionMaps();

                //LoadExtensionSubstitutions();
                //LoadFhirTypeValueSets();

                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.ValueSet, maxStepSize: 1);
                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.ValueSet);
                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.ValueSet, specificPairs: specificPairs);
                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.Resource, maxStepSize: 1);
                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.Resource, specificPairs: specificPairs);
                //CompareInDatabase(artifactFilter: FhirArtifactClassEnum.Resource);
                //CompareInDatabase();

                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet, maxStepSize: 1);
                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet, specificPairs: specificPairs);
                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet);
                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource, maxStepSize: 1);
                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource, specificPairs: specificPairs);
                //GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource);
                //GenerateOutcomes(specificPairs: specificPairs);
                //GenerateOutcomes();

                //ExportOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet, maxStepSize: 1, includeIgScripts: false);
                //ExportOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet, includeIgScripts: false);
                //ExportOutcomes(artifactFilter: FhirArtifactClassEnum.Resource, maxStepSize: 1, includeIgScripts: false, specificPairs: specificPairs);
                //ExportOutcomes(artifactFilter: FhirArtifactClassEnum.Resource, includeIgScripts: false, specificPairs: specificPairs);
                //ExportOutcomes(includeIgScripts: true, specificPairs: specificPairs);
                //ExportOutcomes(includeIgScripts: true, specificPairs: specificPairs);
                //ExportOutcomes(includeIgScripts: false);
                ExportOutcomes();

                break;

            case "update-vs-maps":
                UpdateValueSetMaps();
                break;

            case "load":
                LoadDatabase(true);
                LoadFhirCrossVersionMaps();
                LoadExtensionSubstitutions();
                LoadFhirTypeValueSets();
                break;

            case "load-base":
                LoadDatabase(true);
                LoadExtensionSubstitutions();
                break;

            case "load-maps":
                //LoadDatabase(_config.ReloadDatabase, true);
                LoadFhirCrossVersionMaps();
                LoadFhirTypeValueSets();
                break;

            case "load-substitutions":
                LoadExtensionSubstitutions();
                break;

            case "compare":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                CompareInDatabase();
                break;

            case "compare-vs":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                CompareInDatabase(FhirArtifactClassEnum.ValueSet);
                break;

            case "compare-sd":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                CompareInDatabase(FhirArtifactClassEnum.Resource);
                break;

            case "outcomes":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                GenerateOutcomes();
                break;

            case "outcomes-vs":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.ValueSet);
                break;

            case "outcomes-sd":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                GenerateOutcomes(artifactFilter: FhirArtifactClassEnum.Resource);
                break;


            case "export":
                if (_config.ReloadDatabase)
                {
                    LoadDatabase(_config.ReloadDatabase);
                    LoadFhirCrossVersionMaps();
                    LoadExtensionSubstitutions();
                    LoadFhirTypeValueSets();
                }

                ExportOutcomes();
                break;

            default:
                LoadDatabase(_config.ReloadDatabase);
                LoadFhirCrossVersionMaps();
                LoadExtensionSubstitutions();
                LoadFhirTypeValueSets();
                CompareInDatabase();
                GenerateOutcomes();
                ExportOutcomes();
                break;
        }
    }

    public void UpdateValueSetMaps()
    {
        if (string.IsNullOrEmpty(_config.CrossVersionMapSourcePath))
        {
            throw new Exception("Cannot update source maps without a source path!");
        }

        // load definitions if we have not done so
        if (_definitions.Length == 0)
        {
            loadDefinitionCollections();
        }

        // iterate over all the packages in our collection
        for (int i = 0; i < _definitions.Length; i++)
        {
            DefinitionCollection sourceDc = _definitions[i];

            // iterate up
            for (int j = i + 1; j < _definitions.Length; j++)
            {
                DefinitionCollection targetDc = _definitions[j];

                _logger.LogInformation($"Updating value set maps for {sourceDc.Name} -> {targetDc.Name}");

                // create our map collection
                CrossVersionMapCollection mapCollection = new(
                    sourceDc,
                    targetDc,
                    _dbPath,
                    _config.LogFactory);

                // load only the value set concepts
                if (mapCollection.TryLoadOfficialConceptMaps(_config.CrossVersionMapSourcePath, "codes"))
                {
                    mapCollection.SaveValueSetConceptMaps(
                        Path.Combine(_config.CrossVersionMapSourcePath, "input", "codes-v2"),
                        includeMapSubdir: false);
                }
            }

            // iterate down
            for (int j = i - 1; j >= 0; j--)
            {
                DefinitionCollection targetDc = _definitions[j];

                _logger.LogInformation($"Updating value set maps for {sourceDc.Name} -> {targetDc.Name}");

                // create our map collection
                CrossVersionMapCollection mapCollection = new(
                    sourceDc,
                    targetDc,
                    _dbPath,
                    _config.LogFactory);

                // load only the value set concepts
                if (mapCollection.TryLoadOfficialConceptMaps(_config.CrossVersionMapSourcePath, "codes"))
                {
                    mapCollection.SaveValueSetConceptMaps(
                        Path.Combine(_config.CrossVersionMapSourcePath, "input", "codes-v2"),
                        includeMapSubdir: false);
                }
            }
        }
    }

    /// <summary>
    /// Loads or creates the comparison database, optionally filtering by artifact type.
    /// </summary>
    /// <param name="forceCreate">Whether to force creation of the database.</param>
    /// <param name="allowSourceCopy">Whether to allow copying from a source database.</param>
    /// <param name="artifactFilter">Optional artifact type filter.</param>
    public void LoadDatabase(
        bool forceCreate,
        FhirArtifactClassEnum? artifactFilter = null)
    {
        // check if we have a database filename
        if (!forceCreate &&
            !string.IsNullOrEmpty(_dbName))
        {
            // try loading an existing database
            _db = new(_dbPath, _dbName);
            if (_db != null)
            {
                return;
            }
        }

        // load definitions if we have not done so
        if (_definitions.Length == 0)
        {
            loadDefinitionCollections();
        }

        // creating the database with defintions loads all the content
        _db = new(_definitions, _dbPath, _dbName, _config.LogFactory);
        _dbName = _db.DbFileName;

        // save the definition content in the database
        if (!_db.TryLoadFromDefinitionCollections(_exclusionSet, _escapeValveCodes))
        {
            throw new Exception($"Failed to load FHIR-based definitions into the database: {string.Join(", ", _definitions.Select(d => d.Key))}");
        }

        return;
    }

    /// <summary>
    /// Builds the comparison pairs in the loaded database, can filter by artifact type.
    /// </summary>
    /// <param name="artifactFilter"></param>
    /// <exception cref="Exception"></exception>
    [Obsolete]
    public void BuildComparisonPairs(FhirArtifactClassEnum? artifactFilter = null)
    {
        if (_db == null)
        {
            throw new Exception("Cannot build comparison pairs without a loaded database!");
        }

        //FhirDbComparer dbComparer = new(_db, _config.LogFactory);
        //dbComparer.BuildComparisonPairs(artifactFilter, _config.ComparisonPairFilterKeys);
    }

    public void ExportOutcomes(
        FhirArtifactClassEnum? artifactFilter = null,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null,
        bool? includeIgScripts = null)
    {
        if (_db is null)
        {
            LoadDatabase(false);
        }

        if (_db is null)
        {
            throw new Exception("Cannot export outcomes without a loaded database!");
        }

        XVerExporter exporter = new(
            _db.DbConnection,
            _config);
        switch (artifactFilter)
        {
            case FhirArtifactClassEnum.CodeSystem:
            case FhirArtifactClassEnum.ValueSet:
                exporter.Export(
                    includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
                    processVocabulary: true,
                    processStructures: false,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            case FhirArtifactClassEnum.PrimitiveType:
            case FhirArtifactClassEnum.ComplexType:
            case FhirArtifactClassEnum.Resource:
            case FhirArtifactClassEnum.Profile:
            case FhirArtifactClassEnum.Extension:
                exporter.Export(
                    includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
                    processVocabulary: false,
                    processStructures: true,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            default:
                exporter.Export(
                    includeIgScripts: includeIgScripts ?? _config.XverIncludeScripts,
                    processVocabulary: true,
                    processStructures: true,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;
        }

        _logger.LogInformation($"Finished exporting; outputDirectory: `{_config.OutputDirectory}`");
    }

    public void GenerateOutcomes(
        FhirArtifactClassEnum? artifactFilter = null,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        if (_db is null)
        {
            LoadDatabase(false);
        }

        if (_db is null)
        {
            throw new Exception("Cannot generate outcomes without a loaded database!");
        }

        OutcomeGenerator generator = new(_db, _config.LogFactory);
        switch (artifactFilter)
        {
            case FhirArtifactClassEnum.CodeSystem:
            case FhirArtifactClassEnum.ValueSet:
                generator.GenerateOutcomes(
                    processValueSets: true,
                    processStructures: false,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            case FhirArtifactClassEnum.PrimitiveType:
            case FhirArtifactClassEnum.ComplexType:
            case FhirArtifactClassEnum.Resource:
            case FhirArtifactClassEnum.Profile:
            case FhirArtifactClassEnum.Extension:
                generator.GenerateOutcomes(
                    processValueSets: false,
                    processStructures: true,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            default:
                generator.GenerateOutcomes(
                    processValueSets: true,
                    processStructures: true,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;
        }
    }


    /// <summary>
    /// Runs the comparison process in the loaded database, can filter by artifact type.
    /// </summary>
    /// <param name="artifactFilter">Optional artifact type filter.</param>
    public void CompareInDatabase(
        FhirArtifactClassEnum? artifactFilter = null,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        if (_db is null)
        {
            LoadDatabase(false);
        }

        if (_db is null)
        {
            throw new Exception("Cannot compare without a loaded database!");
        }

        //FhirMappingComparerVs vsComparer = new(_db.DbConnection, _config.LogFactory);
        //vsComparer.CompareValueSets();

        FhirDbComparer comparer = new(_db, _config.LogFactory);
        switch (artifactFilter)
        {
            case FhirArtifactClassEnum.CodeSystem:
            case FhirArtifactClassEnum.ValueSet:
                comparer.Compare(
                    processValueSets: true,
                    processStructures: false,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            case FhirArtifactClassEnum.PrimitiveType:
            case FhirArtifactClassEnum.ComplexType:
            case FhirArtifactClassEnum.Resource:
            case FhirArtifactClassEnum.Profile:
            case FhirArtifactClassEnum.Extension:
                comparer.Compare(
                    processValueSets: false,
                    processStructures: true,
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;

            default:
                comparer.Compare(
                    maxStepSize: maxStepSize,
                    specificPairs: specificPairs);
                break;
        }
    }

    /// <summary>
    /// Represents support information for a FHIR package during cross-version processing.
    /// </summary>
    private record class PackageXverSupport
    {
        /// <summary>
        /// Gets the package index.
        /// </summary>
        public required int PackageIndex { get; init; }
        /// <summary>
        /// Gets the FHIR package.
        /// </summary>
        public required DbFhirPackage Package { get; init; }
        /// <summary>
        /// Gets the set of basic element paths.
        /// </summary>
        public Dictionary<string, string?> BasicElements { get; init; } = [];
        /// <summary>
        /// Gets the set of allowed extension types.
        /// </summary>
        public HashSet<string> AllowedExtensionTypes { get; init; } = [];
        /// <summary>
        /// Gets or sets the core definition collection.
        /// </summary>
        public DefinitionCollection? CoreDC { get; set; } = null;
        /// <summary>
        /// Gets or sets the snapshot exporter.
        /// </summary>
        public Hl7.Fhir.Specification.Snapshot.SnapshotGenerator? SnapshotGenerator { get; set; } = null;
    }

    public void LoadExtensionSubstitutions()
    {
        if (_db == null)
        {
            LoadDatabase(false);
            if (_db == null)
            {
                throw new Exception($"Failed to create or load a comparison database!");
            }
        }

        if (!_db.TryLoadExtensionSubstitutions(_config.CrossVersionMapSourcePath))
        {
            throw new Exception($"Failed to load extension substitutions from source path: {_config.CrossVersionMapSourcePath}");
        }
    }

    public void LoadFhirTypeValueSets()
    {
        if (_db == null)
        {
            LoadDatabase(false);
            if (_db == null)
            {
                throw new Exception($"Failed to create or load a comparison database!");
            }
        }

        if (!_db.TryLoadFhirTypeValueSets(_config.CrossVersionMapSourcePath))
        {
            throw new Exception($"Failed to load extension FHIR-type value set list from source path: {_config.CrossVersionMapSourcePath}");
        }
    }

    /// <summary>
    /// Loads the definitions and initializes the comparison cache.
    /// </summary>
    /// <remarks>
    /// This is only used to convert origin maps into the database.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when there are less than two definitions available for comparison.</exception>
    public void LoadFhirCrossVersionMaps()
    {
        if (_db == null)
        {
            LoadDatabase(false);
            if (_db == null)
            {
                throw new Exception($"Failed to create or load a comparison database!");
            }
        }

        MappingLoader mappingLoader = new(_db.DbConnection, _loggerFactory);
        if (!string.IsNullOrEmpty(_config.CrossVersionMapSourcePath))
        {
            _ = mappingLoader.TryLoadCrossVersionSourceMaps(
                _config.CrossVersionMapSourcePath,
                _config.UseInternalTypeMaps);
            //_ = _db.TryLoadFhirCrossVersionMaps(_config.CrossVersionMapSourcePath);
        }
    }

    private void writeMarkdownRootPrimitiveMaps(string dir)
    {
        string overviewFilename = Path.Combine(dir, "PrimitiveTypes.md");

        using ExportStreamWriter writer = createMarkdownWriter(overviewFilename, true, true);

        writer.Write($"""
            ## Primitive Type Mappings

            Primitive types are mapped across all versions using the following table.

            Note that in this table, "concept" refers to the FHIR concept domain and "value" refers to the FHIR value domain.

            The statement: "`typeA` and `typeB` are conceptually interchangeable where appropriate" means that when an *element* provides
            the appropriate context, the concepts are not so disparate that they cannot be used.  For example, an element that
            was defined as a `id` in one version could be defined as a `code` in another version. While the types do not
            *inherently* have a conceptual overlap, the context of the element allows the substitution.  This is different than if
            an element was defined as an `boolean` and changed to a `dateTime`, which do not have the ability to be conceptually mapped.

            | Source Type | Target Type | Concept Relationship | Concept Comment | Value Relationship | Value Comment |
            | --- | --- | --- | --- | --- | --- |

            """);

        foreach (FhirTypeMappings.CodeGenTypeMapping mapping in FhirTypeMappings.PrimitiveMappings)
        {
            writer.WriteLine(
                $"| `{mapping.SourceType}` " +
                $"| `{mapping.TargetType}` " +
                $"| `{mapping.ConceptDomainRelationship}` " +
                $"| {mapping.ConceptDomainComment} " +
                $"| `{mapping.ValueDomainRelationship}` " +
                $"| {mapping.ValueDomainComment} " +
                $"|");
        }
    }


    private void writeTableColumns(
        ExportStreamWriter writer,
        string value,
        int count,
        bool appendNewline = true,
        int? valueOnlyInColumn = null)
    {
        if (valueOnlyInColumn == null)
        {
            for (int i = 0; i < count; i++)
            {
                if (appendNewline && (i == count - 1))
                {
                    writer.WriteLine(" | " + value);
                }
                else if (i == 0)
                {
                    writer.Write("| " + value);
                }
                else
                {
                    writer.Write(" | " + value);
                }
            }

            return;
        }


        for (int i = 0; i < count; i++)
        {
            if (appendNewline && (i == count - 1))
            {
                if (valueOnlyInColumn == i)
                {
                    writer.WriteLine(" | " + value);
                }
                else
                {
                    writer.WriteLine(" | ");
                }
            }
            else if (i == 0)
            {
                if (valueOnlyInColumn == i)
                {
                    writer.Write("| " + value);
                }
                else
                {
                    writer.Write("| ");
                }
            }
            else
            {
                if (valueOnlyInColumn == i)
                {
                    writer.Write(" | " + value);
                }
                else
                {
                    writer.Write(" | ");
                }
            }
        }
    }

    private string getSdFilename(string sourceName, FhirArtifactClassEnum artifactClass, bool includeRelativeDir = true)
    {
        string artifactPascal = artifactClass switch
        {
            FhirArtifactClassEnum.PrimitiveType => "PrimitiveTypes",
            FhirArtifactClassEnum.ComplexType => "ComplexTypes",
            FhirArtifactClassEnum.Resource => "Resources",
            _ => throw new InvalidOperationException("Invalid artifact class."),
        };

        return includeRelativeDir
            ? $"{artifactPascal}/{FhirSanitizationUtils.SanitizeForProperty(sourceName, convertToConvention: FhirNameConventionExtensions.NamingConvention.PascalCase)}.md"
            : FhirSanitizationUtils.SanitizeForProperty(sourceName, convertToConvention: FhirNameConventionExtensions.NamingConvention.PascalCase) + ".md";
    }

    private string getVsFilename(string sourceVsName, bool includeRelativeDir = true)
    {
        return includeRelativeDir
            ? $"ValueSets/{FhirSanitizationUtils.SanitizeForProperty(sourceVsName, convertToConvention: FhirNameConventionExtensions.NamingConvention.PascalCase)}.md"
            : FhirSanitizationUtils.SanitizeForProperty(sourceVsName, convertToConvention: FhirNameConventionExtensions.NamingConvention.PascalCase) + ".md";

        //return includeRelativeDir
        //    ? $"ValueSets/{sourceVsName}_{cd.TargetDefinition.FhirSequence.ToRLiteral()}_{cd.Target?.Name.ToPascalCase()}"
        //    : $"{sourceVsName}_{cd.TargetDefinition.FhirSequence.ToRLiteral()}_{cd.Target?.Name.ToPascalCase()}";
    }

    private ExportStreamWriter createMarkdownWriter(string filename, bool writeGenerationHeader = true, bool includeGenerationTime = false)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                ExportStreamWriter writer = new(filename);

                if (writeGenerationHeader)
                {
                    writer.WriteLine($"Comparison of {string.Join(", ", _definitions.Select(dc => dc.Key))}");

                    if (includeGenerationTime)
                    {
                        writer.WriteLine($"Generated at {DateTime.Now.ToString("F")}");
                    }

                    writer.WriteLine();
                }

                return writer;
            }
            catch (IOException)
            {
                // wait a bit and try again
                Thread.Sleep(1000);
            }
        }

        throw new IOException("Failed to create file after 3 attempts.");
    }

    private ExportStreamWriter createHtmlWriter(string filename, bool writeGenerationHeader = true, bool includeGenerationTime = false)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                ExportStreamWriter writer = new(filename);

                if (writeGenerationHeader)
                {
                    writer.WriteLine($"<h2>Comparison of {string.Join(", ", _definitions.Select(dc => dc.Key))}</h2>");

                    if (includeGenerationTime)
                    {
                        writer.WriteLine($"Generated at {DateTime.Now.ToString("F")}");
                    }

                    writer.WriteLine();
                }

                return writer;
            }
            catch (IOException)
            {
                // wait a bit and try again
                Thread.Sleep(1000);
            }
        }

        throw new IOException("Failed to create file after 3 attempts.");
    }


    internal static (string idLong, string idShort, string name) GenerateArtifactId(
        string sourcePackageShortName,
        string sourceArtifactId,
        string targetPackageShortName,
        string? prefixLong = null,
        string? prefixShort = null)
    {
        string saName = sourceArtifactId.ToPascalCase();
        string name = prefixLong is null
            ? $"{sourcePackageShortName.ToPascalCase()}{saName}For{targetPackageShortName.ToPascalCase()}"
            : $"{prefixLong.ToPascalCase()}{sourcePackageShortName.ToPascalCase()}{saName}For{targetPackageShortName.ToPascalCase()}";

        string idLong = prefixLong is null
            ? $"{sourcePackageShortName}-{sourceArtifactId}-for-{targetPackageShortName}"
            : $"{prefixLong}-{sourcePackageShortName}-{sourceArtifactId}-for-{targetPackageShortName}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        string sp = prefixShort is null
            ? string.Empty
            : (prefixShort + "-");

        string idShort;

        string[] sourceIdComponents = sourceArtifactId.Split('-');
        if (sourceArtifactId.StartsWith("v3-", StringComparison.Ordinal) ||
            sourceArtifactId.StartsWith("v2-", StringComparison.Ordinal))
        {
            // the second component is a PascalCase name, extract it into components - e.g. ActInvoiceElementModifier -> [Act, Invoice, Element, Modifier]
            string[] pascalComponents = Regex.Matches(sourceIdComponents[1], @"([A-Z][a-z0-9]+)")
                .Select(m => m.Value)
                .ToArray();

            // use the prefixLong (v2 or v3) plus the first word, capitals in the middle, and the last word
            // e.g. v3-ActInvoiceElementModifier -> v3ActIEModifier
            idShort =
                $"{sp}{sourcePackageShortName}" +
                $"-{sourceIdComponents[0]}" +
                $"{pascalComponents[0]}" +
                $"{string.Join(string.Empty, pascalComponents[1..^1].Select(c => c[0]))}" +
                $"{pascalComponents[^1]}" +
                $"-for-{targetPackageShortName}";

        }
        else if (sourceIdComponents.Length > 2)
        {
            // use the first and last components completely, but abbreviate the middle components
            idShort =
                $"{sp}{sourcePackageShortName}" +
                $"-{sourceIdComponents[0]}" +
                $"-{string.Join('-', sourceIdComponents.Skip(1).Take(sourceIdComponents.Length - 2).Select(c => c.Substring(0, int.Min(c.Length, 3))))}" +
                $"-{sourceIdComponents[^1]}" +
                $"-for-{targetPackageShortName}";
        }
        else
        {
            // truncate the source ID so it all fits
            idShort = $"{sp}{sourcePackageShortName}-{sourceArtifactId.Substring(0, 50)}-for-{targetPackageShortName}";
        }

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateArtifactId(
        string sourcePackageShortName,
        string sourceArtifactId,
        string targetPackageShortName,
        string? targetArtifactId,
        string? prefixLong = null,
        string? prefixShort = null)
    {
        if ((targetArtifactId is null) ||
            sourceArtifactId.Equals(targetArtifactId, StringComparison.OrdinalIgnoreCase))
        {
            return GenerateArtifactId(
                sourcePackageShortName,
                sourceArtifactId,
                targetPackageShortName,
                prefixLong: prefixLong,
                prefixShort: prefixShort);
        }

        string saName = sourceArtifactId.ToPascalCase();
        string taName = targetArtifactId.ToPascalCase();
        string name = prefixLong is null
            ? $"{sourcePackageShortName.ToPascalCase()}{saName}For{targetPackageShortName.ToPascalCase()}{taName}"
            : $"{prefixLong.ToPascalCase()}{sourcePackageShortName.ToPascalCase()}{saName}For{targetPackageShortName.ToPascalCase()}{taName}";

        string idLong = prefixLong is null
            ? $"{sourcePackageShortName}-{sourceArtifactId}-for-{targetPackageShortName}-{targetArtifactId}"
            : $"{prefixLong}-{sourcePackageShortName}-{sourceArtifactId}-for-{targetPackageShortName}-{targetArtifactId}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        string sp = prefixShort is null
            ? string.Empty
            : (prefixShort + "-");

        string shortSource;

        if (sourceArtifactId.Length <= 25)
        {
            shortSource = sourceArtifactId;
        }
        else
        {
            string[] sourceIdComponents = sourceArtifactId.Split('-');
            if (sourceArtifactId.StartsWith("v3-", StringComparison.Ordinal) ||
                sourceArtifactId.StartsWith("v2-", StringComparison.Ordinal))
            {
                // the second component is a PascalCase name, extract it into components - e.g. ActInvoiceElementModifier -> [Act, Invoice, Element, Modifier]
                string[] pascalComponents = Regex.Matches(sourceIdComponents[1], @"([A-Z][a-z0-9]+)")
                    .Select(m => m.Value)
                    .ToArray();

                if (pascalComponents.Length == 1)
                {
                    if (pascalComponents[0].Length <= 20)
                    {
                        // just use the full name
                        shortSource =
                            $"{sourceIdComponents[0]}" +
                            $"{pascalComponents[0]}";
                    }
                    else
                    {
                        // just truncate the name
                        shortSource =
                            $"{sourceIdComponents[0]}" +
                            $"{pascalComponents[0][0..20]}";
                    }
                }
                else
                {
                    // use the prefixLong (v2 or v3) plus the first word, capitals in the middle, and the last word
                    // e.g. v3-ActInvoiceElementModifier -> v3ActIEModifier
                    shortSource =
                        $"{sourceIdComponents[0]}" +
                        $"{pascalComponents[0]}" +
                        $"{string.Join(string.Empty, pascalComponents[1..^1].Select(c => c[0]))}" +
                        $"{pascalComponents[^1]}";
                }
            }
            else if (sourceIdComponents.Length > 2)
            {
                // use the first and last components completely, but abbreviate the middle components
                shortSource =
                    $"{sourceIdComponents[0]}" +
                    $"-{string.Join('-', sourceIdComponents.Skip(1).Take(sourceIdComponents.Length - 2).Select(c => c.Substring(0, int.Min(c.Length, 3))))}" +
                    $"-{sourceIdComponents[^1]}";
            }
            else
            {
                // truncate the source ID so it all fits
                shortSource = sourceArtifactId[0..25];
            }
        }

        string shortTarget;

        if (targetArtifactId.Length <= 25)
        {
            shortTarget = targetArtifactId;
        }
        else
        {
            string[] targetIdComponents = targetArtifactId.Split('-');
            if (targetArtifactId.StartsWith("v3-", StringComparison.Ordinal) ||
                targetArtifactId.StartsWith("v2-", StringComparison.Ordinal))
            {
                // the second component is a PascalCase name, extract it into components - e.g. ActInvoiceElementModifier -> [Act, Invoice, Element, Modifier]
                string[] pascalComponents = Regex.Matches(targetIdComponents[1], @"([A-Z][a-z0-9]+)")
                    .Select(m => m.Value)
                    .ToArray();

                if (pascalComponents.Length == 1)
                {
                    if (pascalComponents[0].Length <= 20)
                    {
                        // just use the full name
                        shortTarget =
                            $"{targetIdComponents[0]}" +
                            $"{pascalComponents[0]}";
                    }
                    else
                    {
                        // just truncate the name
                        shortTarget =
                            $"{targetIdComponents[0]}" +
                            $"{pascalComponents[0][0..20]}";
                    }
                }
                else
                {
                    // use the prefixLong (v2 or v3) plus the first word, capitals in the middle, and the last word
                    // e.g. v3-ActInvoiceElementModifier -> v3ActIEModifier
                    shortTarget =
                        $"{targetIdComponents[0]}" +
                        $"{pascalComponents[0]}" +
                        $"{string.Join(string.Empty, pascalComponents[1..^1].Select(c => c[0]))}" +
                        $"{pascalComponents[^1]}";
                }
            }
            else if (targetIdComponents.Length > 2)
            {
                // use the first and last components completely, but abbreviate the middle components
                shortTarget =
                    $"{targetIdComponents[0]}" +
                    $"-{string.Join('-', targetIdComponents.Skip(1).Take(targetIdComponents.Length - 2).Select(c => c.Substring(0, int.Min(c.Length, 3))))}" +
                    $"-{targetIdComponents[^1]}";
            }
            else
            {
                // truncate the target ID so it all fits
                shortTarget = targetArtifactId[0..25];
            }
        }

        string idShort = $"{sp}{sourcePackageShortName}-{shortSource}-for-{targetPackageShortName}-{shortTarget}";

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateSdElementMapId(
        string sourcePackageShortName,
        string sourceArtifactId,
        string targetPackageShortName)
    {
        string name =
            //$"ConceptMap" +
            $"{sourcePackageShortName.ToPascalCase()}" +
            $"{sourceArtifactId.ToPascalCase()}" +
            $"ElementMapTo" +
            $"{targetPackageShortName.ToPascalCase()}";

        //string idLong = $"ConceptMap-{sourcePackageShortName}-{sourceArtifactId}-elements-for-{targetPackageShortName}";
        string idLong = $"{sourcePackageShortName}-{sourceArtifactId}-element-map-to-{targetPackageShortName}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        //string idShort = $"Cm-{sourcePackageShortName}-{sourceArtifactId}-ef-{targetPackageShortName}";
        string idShort = $"{sourcePackageShortName}-{sourceArtifactId}-ed-map-{targetPackageShortName}";

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateSdElementMapId(
        string sourcePackageShortName,
        string sourceArtifactId,
        string targetPackageShortName,
        string? targetArtifactId)
    {
        if (targetArtifactId is null)
        {
            return GenerateSdElementMapId(sourcePackageShortName, sourceArtifactId, targetPackageShortName);
        }

        string name =
            //$"ConceptMap" +
            $"{sourcePackageShortName.ToPascalCase()}" +
            $"{sourceArtifactId.ToPascalCase()}" +
            $"ElementMapTo" +
            $"{targetPackageShortName.ToPascalCase()}" +
            $"{targetArtifactId.ToPascalCase()}";

        string idLong =
            //$"ConceptMap-{sourcePackageShortName}" +
            $"{sourcePackageShortName}" +
            $"-{sourceArtifactId}" +
            $"-element-map-to" +
            $"-{targetPackageShortName}" +
            $"-{targetArtifactId}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        string idShort =
            //$"Cm-{sourcePackageShortName}" +
            $"{sourcePackageShortName}" +
            $"-{sourceArtifactId}" +
            $"-ed-map" +
            $"-{targetPackageShortName}" +
            $"-{targetArtifactId}";

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateProfileId(
        FhirReleases.FhirSequenceCodes sourceFhirSequence,
        string sourcePackageShortName,
        string sourceArtifactId,
        FhirReleases.FhirSequenceCodes targetFhirSequence,
        string targetPackageShortName)
    {
        string name = $"Profile" +
            $"_{sourceFhirSequence.ToRLiteral()}" +
            $"_{sourceArtifactId.ToPascalCase()}" +
            $"_{targetFhirSequence.ToRLiteral()}";
        string idLong = $"profile-{sourceArtifactId}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        string idShort = $"prfl-{sourceArtifactId}";

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateProfileId(
        FhirReleases.FhirSequenceCodes sourceFhirSequence,
        string sourcePackageShortName,
        string sourceArtifactId,
        FhirReleases.FhirSequenceCodes targetFhirSequence,
        string targetPackageShortName,
        string? targetArtifactId)
    {
        if ((targetArtifactId is null) ||
            (sourceArtifactId.Equals(targetArtifactId, StringComparison.OrdinalIgnoreCase)))
        {
            return GenerateProfileId(
                sourceFhirSequence,
                sourcePackageShortName,
                sourceArtifactId,
                targetFhirSequence,
                targetPackageShortName);
        }

        string name = $"Profile" +
            $"_{sourceFhirSequence.ToRLiteral()}" +
            $"_{sourceArtifactId.ToPascalCase()}" +
            $"_{targetFhirSequence.ToRLiteral()}" +
            $"_{targetArtifactId.ToPascalCase()}";
        string idLong = $"profile-{sourceArtifactId}-for-{targetArtifactId}";

        if (idLong.Length <= 64)
        {
            return (idLong, idLong, name);
        }

        string idShort = $"prfl-{sourceArtifactId}-for-{targetArtifactId}";
        if (idShort.Length > 64)
        {
            throw new Exception("Cannot generate a short profile ID within length constraints.");
        }

        return (idLong, idShort, name);
    }

    internal static (string idLong, string idShort, string name) GenerateExtensionId(
        string sourcePackageShortName,
        string sourceElementPath)
    {
        string name =
            $"Extension" +
            $"{sourceElementPath.Split('.').ToPascalCaseWord(joinDelimiter: "_").Replace("[x]", string.Empty)}";
        string idLong = $"extension-{sourceElementPath.Replace("[x]", string.Empty)}";
        string idShort = $"ext-{sourcePackageShortName}-{collapsePathForId(sourceElementPath)}";

        return (idLong, idShort, name);

        string collapsePathForId(string path)
        {
            string pathClean = path.Replace("[x]", string.Empty);
            string[] components = pathClean.Split('.');
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

                        // abbreviate middle components
                        for (int i = 1; i < components.Length - 1; i++)
                        {
                            if (components[i].Length > 3)
                            {
                                components[i] = components[i][0..3];
                            }
                        }

                        // abbreviate last component if needed
                        if (components.Last().Length > 20)
                        {
                            components[components.Length - 1] =
                                $"{components[components.Length - 1][0]}{new string(components[components.Length - 1].Where(char.IsUpper).ToArray())}";
                        }

                        return string.Join('.', components);
                    }
            }
        }
    }

}
