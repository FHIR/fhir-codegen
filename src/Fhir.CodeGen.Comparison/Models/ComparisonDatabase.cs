using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.ConstrainedExecution;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Antlr4.Runtime.Tree.Xpath;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.MappingLanguage;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification;
using Hl7.Fhir.Utility;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using static Fhir.CodeGen.Comparison.CompareTool.FhirTypeMappings;
using CMR = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship;

namespace Fhir.CodeGen.Comparison.Models;


internal static partial class ContentDatabaseLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to resolve ValueSet {vsUrl} in {sourcePackage}:{sourceVersion}.")]
    internal static partial void LogFailedToResolveVs(this ILogger logger, string vsUrl, string sourcePackage, string sourceVersion);
}


public class ComparisonDatabase : IDisposable
{
    private bool _disposedValue;

    private ILoggerFactory? _loggerFactory;
    private ILogger _logger;

    private class DcInfoRec
    {
        public required FhirReleases.FhirSequenceCodes FhirSequence;
        public required string PackageCanonical;
        public required string RLiteral;
        public required string ShortVersion;
        public required string ShortVersionUrlSegment;
        public required string PackageVersion;
    }

    private (DefinitionCollection dc, DcInfoRec info)[]? _definitions = null;

    private bool _isCoreComparison;
    private bool _isVersionComparison;

    private string _dbPath;
    private string _dbName;

    private IDbConnection _db;

    public ComparisonDatabase(
        DefinitionCollection[] definitions,
        string dbPath,
        string? dbName,
        ILoggerFactory? loggerFactory = null)
    {
        if (definitions.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definitions));
        }

        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<ComparisonDatabase>()
            ?? definitions[0].Logger
            ?? LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ComparisonDatabase>();
        _definitions = definitions.Select(dc => (dc, new DcInfoRec()
        {
            FhirSequence = dc.FhirSequence,
            PackageCanonical = dc.MainPackageCanonical,
            RLiteral = dc.FhirSequence.ToRLiteral(),
            ShortVersion = dc.FhirSequence.ToShortVersion(),
            ShortVersionUrlSegment = "/" + dc.FhirSequence.ToShortVersion() + "/",
            PackageVersion = dc.FhirVersionLiteral,
        })).ToArray();

        _dbPath = dbPath;
        if (!Directory.Exists(_dbPath))
        {
            Directory.CreateDirectory(_dbPath);
        }

        _isCoreComparison = definitions.All(dc => FhirPackageUtils.PackageIsFhirCore(dc.MainPackageId));
        _isVersionComparison = definitions.Select(dc => dc.MainPackageId).Distinct().Count() == 1;

        if (!string.IsNullOrEmpty(dbName))
        {
            _dbName = dbName;
        }
        else
        {
            switch (definitions.Length)
            {
                case 1:
                    _dbName = $"{definitions[0].MainPackageId.ToPascalCase()}-V{definitions[0].MainPackageVersion.Replace('.', '_')}.sqlite";
                    break;
                case 2:
                    {
                        if (_isVersionComparison)
                        {
                            _dbName = string.Join('_', definitions.Select(dc => $"V{dc.MainPackageVersion.Replace('.', '_')}")) + ".sqlite";
                        }
                        else if (_isCoreComparison)
                        {
                            _dbName = string.Join('_', definitions.Select(dc => $"{dc.FhirSequence.ToRLiteral()}")) + ".sqlite";
                        }
                        else
                        {
                            _dbName = string.Join('_', definitions.Select(dc => $"{dc.MainPackageId.ToPascalCase()}-V{dc.MainPackageVersion.Replace('.', '_')}")) + ".sqlite";
                        }
                    }
                    break;
                default:
                    _dbName = "fhir-comparison.sqlite";
                    break;
            }
        }

        string connectionString = new SqliteConnectionStringBuilder()
        {
            DataSource = Path.Combine(_dbPath, _dbName),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _db = new SqliteConnection(connectionString);
        _db.Open();

        _logger.LogInformation($"Opened database: `{connectionString}`");

        initNewDb(ensureDeleted: true);
    }

    public ComparisonDatabase(
        string dbPath,
        string dbName,
        ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<ComparisonDatabase>()
            ?? LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ComparisonDatabase>()
            ?? NullLoggerFactory.Instance.CreateLogger(nameof(ComparisonDatabase));

        _dbPath = dbPath;
        if (!Directory.Exists(_dbPath))
        {
            Directory.CreateDirectory(_dbPath);
        }

        _dbName = dbName;
        string connectionString = new SqliteConnectionStringBuilder()
        {
            DataSource = Path.Combine(_dbPath, _dbName),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _logger.LogInformation($"Opened database: `{connectionString}`");

        _db = new SqliteConnection(connectionString);
        _db.Open();

        DbContentClasses.LoadIndices(_db);
    }

    public bool IsCoreComparison => _isCoreComparison;
    public bool IsVersionComparison => _isVersionComparison;
    public string DbFileName => _dbName;
    public string DbFilePath => _dbPath;

    public IDbConnection DbConnection => _db;

    public bool TryLoadFhirTypeValueSets(
        string crossVersionMapSourcePath)
    {
        string filename = Path.Combine(crossVersionMapSourcePath, "input", "ig-support", "valueSetsOfFhirTypes.json");
        if (!File.Exists(filename))
        {
            throw new Exception($"Could not find FHIR-type value set list source file at {filename}!");
        }

        List<string>? vsUrls;

        try
        {
            _logger.LogInformation($"Loading FHIR-type value set URLs from `{filename}`");
            using FileStream jsonFs = new(filename, System.IO.FileMode.Open, FileAccess.Read);
            {
                vsUrls = JsonSerializer.Deserialize<List<string>>(jsonFs);
                if ((vsUrls is null) ||
                    (vsUrls.Count == 0))
                {
                    _logger.LogWarning($"No value set URLs found in {filename}!");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Attempt to load content from {filename} failed! {ex.Message}");
            return false;
        }

        _logger.LogInformation($"Found {vsUrls.Count} substitution requests");

        // recreate our substitution table
        DbFhirTypeValueSet.DropTable(_db);
        DbFhirTypeValueSet.CreateTable(_db);
        DbFhirTypeValueSet.LoadMaxKey(_db);

        List<DbFhirTypeValueSet> toAdd = vsUrls
            .Select(v => new DbFhirTypeValueSet() { Key = DbFhirTypeValueSet.GetIndex(), UnversionedUrl = v })
            .ToList();

        toAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);

        return true;
    }

    public bool TryLoadExtensionSubstitutions(
        string crossVersionMapSourcePath,
        ConfigRoot? config = null)
    {
        string filename = Path.Combine(crossVersionMapSourcePath, "input", "ig-support", "extensionSubstitutions.json");
        if (!File.Exists(filename))
        {
            throw new Exception($"Could not find extension substitution source file at {filename}!");
        }

        List<DbExtensionSubstitution>? substitutionSource;

        try
        {
            _logger.LogInformation($"Loading extension substitutions from `{filename}`");
            using FileStream jsonFs = new(filename, System.IO.FileMode.Open, FileAccess.Read);
            {
                substitutionSource = JsonSerializer.Deserialize<List<DbExtensionSubstitution>>(jsonFs);
                if ((substitutionSource is null) ||
                    (substitutionSource.Count == 0))
                {
                    _logger.LogWarning($"No substitutions found in {filename}!");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Attempt to load content from {filename} failed! {ex.Message}");
            return false;
        }

        _logger.LogInformation($"Found {substitutionSource.Count} substitution requests");

        // recreate our substitution table
        DbExtensionSubstitution.DropTable(_db);
        DbExtensionSubstitution.CreateTable(_db);
        DbExtensionSubstitution.LoadMaxKey(_db);

        DbRecordCache<DbExtensionSubstitution> extCache = new();

        Dictionary<string, DefinitionCollection> sources = [];

        List<DbFhirPackage> packages = DbFhirPackage.SelectList(_db, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);

        // load necessary definitions to expand content
        foreach (DbExtensionSubstitution jsonRec in substitutionSource)
        {
            if ((jsonRec.SourceFhirSequence is null) && (jsonRec.SourceVersion is not null))
            {
                jsonRec.SourceFhirSequence = FhirReleases.FhirVersionToSequence(jsonRec.SourceVersion);
            }

            // if there is no source package, nothing else for this record
            if (jsonRec.ReplacementSourcePackage is null)
            {
                jsonRec.Key = DbExtensionSubstitution.GetIndex();
                extCache.CacheAdd(jsonRec);
                continue;
            }

            if (!sources.TryGetValue(jsonRec.ReplacementSourcePackage, out DefinitionCollection? sourceDc))
            {
                string directive;
                if (jsonRec.ReplacementSourcePackage.Contains('#') || jsonRec.ReplacementSourcePackage.Contains('@'))
                {
                    directive = jsonRec.ReplacementSourcePackage;
                }
                else
                {
                    directive = jsonRec.ReplacementSourcePackage + "@latest";
                }

                _logger.LogInformation($"Resolving package `{directive}`...");

                // create a loader because these are all different FHIR core versions
                using PackageLoader loader = new(config, new()
                {
                    JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
                });

                sourceDc = loader
                    .LoadPackages([directive], autoLoadExpansionsValue: false, resolveDependenciesValue: false)
                    .Result
                    ?? throw new Exception($"Could not load package: {directive}");

                sources.Add(jsonRec.ReplacementSourcePackage, sourceDc);
            }

            // resolve the extension canonical in the source package
            if (!sourceDc.TryResolveByCanonicalUri(jsonRec.ReplacementUrl, out Resource? resolved))
            {
                throw new Exception($"Could not resolve canonical {jsonRec.ReplacementUrl} in source package {jsonRec.ReplacementSourcePackage} for substitution!");
            }

            if (resolved is not StructureDefinition extSd)
            {
                throw new Exception($"Resolved resource for canonical {jsonRec.ReplacementUrl} in source package {jsonRec.ReplacementSourcePackage} is not a StructureDefinition!");
            }

            List<StructureDefinition.ContextComponent> sourceContexts = extSd.Context;

            List<DbFhirPackage> applicablePackages = jsonRec.SourceFhirSequence is null
                ? packages
                : packages.Where(p => p.DefinitionFhirSequence == jsonRec.SourceFhirSequence).ToList();

            // iterate over the FHIR packages to resolve contexts and necessary sources
            foreach (DbFhirPackage package in applicablePackages)
            {
                // build the list of contexts that apply to this FHIR version
                List<StructureDefinition.ContextComponent> applicableContexts = [];
                foreach (StructureDefinition.ContextComponent ctx in sourceContexts)
                {
                    // check for version specific uses
                    Extension? versionExt = ctx.GetExtension(CommonDefinitions.ExtUrlVersionSpecificUse);
                    if (versionExt is not null)
                    {
                        string? startVersion = versionExt.GetExtensionValue<Code>(CommonDefinitions.ExtUrlVersionSpecificUseStart)?.Value;
                        string? endVersion = versionExt.GetExtensionValue<Code>(CommonDefinitions.ExtUrlVersionSpecificUseEnd)?.Value;

                        FhirReleases.FhirSequenceCodes minFhirVersion = startVersion is null
                            ? FhirReleases.FhirSequenceCodes.DSTU2
                            : FhirReleases.FhirVersionToSequence(startVersion);

                        FhirReleases.FhirSequenceCodes maxFhirVersion = endVersion is null
                            ? FhirReleases.FhirSequenceCodes.R6
                            : FhirReleases.FhirVersionToSequence(endVersion);

                        if ((package.DefinitionFhirSequence < minFhirVersion) ||
                            (package.DefinitionFhirSequence > maxFhirVersion))
                        {
                            continue;
                        }
                    }

                    applicableContexts.Add(ctx);
                }

                if (applicableContexts.Count == 0)
                {
                    // nothing to add for this version
                    continue;
                }

                // if we have an element or type, we can add the record now
                if ((jsonRec.SourceElementId is not null) ||
                    (jsonRec.SourceTypeReplacement is not null))
                {
                    DbExtensionSubstitution rec = new()
                    {
                        Key = DbExtensionSubstitution.GetIndex(),
                        ReplacementUrl = jsonRec.ReplacementUrl,
                        ReplacementName = jsonRec.ReplacementName ?? extSd.Name,
                        ReplacementSourcePackage = jsonRec.ReplacementSourcePackage,
                        SourceVersion = jsonRec.SourceVersion,
                        SourceFhirSequence = package.DefinitionFhirSequence,
                        SourceElementId = jsonRec.SourceElementId,
                        SourceTypeReplacement = jsonRec.SourceTypeReplacement,
                        SourceFromContextElement = jsonRec.SourceFromContextElement,
                        IsModifier = jsonRec.IsModifier,
                        Contexts = applicableContexts.Select(c => c.Expression).ToList(),
                    };

                    extCache.CacheAdd(rec);
                    continue;
                }

                // if we do not have a context replacement, this is an invalid request
                if (jsonRec.SourceFromContextElement is null)
                {
                    throw new Exception($"Substitution record with key {jsonRec.Key} is missing both a context replacement and an element/type replacement!");
                }

                // build the context replacement records for each context
                List<string> contextExpanded = applicableContexts
                    .Select(c => c.Expression + jsonRec.SourceFromContextElement)
                    .ToList();

                // add this record
                DbExtensionSubstitution expandedRec = new()
                {
                    Key = DbExtensionSubstitution.GetIndex(),
                    ReplacementUrl = jsonRec.ReplacementUrl,
                    ReplacementName = jsonRec.ReplacementName ?? extSd.Name,
                    ReplacementSourcePackage = jsonRec.ReplacementSourcePackage,
                    SourceVersion = jsonRec.SourceVersion,
                    SourceFhirSequence = package.DefinitionFhirSequence,
                    SourceElementId = jsonRec.SourceElementId,
                    SourceTypeReplacement = jsonRec.SourceTypeReplacement,
                    SourceFromContextElement = jsonRec.SourceFromContextElement,
                    SourceFromContextExpanded = contextExpanded,
                    IsModifier = jsonRec.IsModifier,
                    Contexts = applicableContexts.Select(c => c.Expression).ToList(),
                };

                extCache.CacheAdd(expandedRec);
            }
        }

        // insert our records
        _logger.LogInformation($"Adding {extCache.ToAddCount} extension substitutions");
        extCache.ToAdd.Insert(_db, ignoreDuplicates: true, insertPrimaryKey: true);

        return true;
    }

    private void loadDefaultExternalInclusions()
    {
        List<DbExternalInclusion> inclusions = [
            new()
            {
                Key = DbExternalInclusion.GetIndex(),
                ResourceType = FHIRAllTypes.CodeSystem,
                Id = "designation-usage",
                Name = "DesignationUsage",
                Version = "4.2.0",
                OverrideVersion = false,
                UnversionedUrl = "http://terminology.hl7.org/CodeSystem/designation-usage",
                VersionedUrl = "http://terminology.hl7.org/CodeSystem/designation-usage|4.2.0",
                Reason = "This CodeSystem was only published in hl7.terminology@1.0.0. Currently working through resolution.",
                IncludeInPackages = null,
                Json = """
                {
                  "resourceType": "CodeSystem",
                  "id": "designation-usage",
                  "meta": {
                    "profile": [
                      "http://hl7.org/fhir/StructureDefinition/shareablecodesystem"
                    ]
                  },
                  "text": {
                    "status": "generated",
                    "div": "<div xmlns=\"http://www.w3.org/1999/xhtml\"><h2>Designation Usage</h2><div><p>Preferred value set for Condition Categories.</p>\n</div><p>This code system http://terminology.hl7.org/CodeSystem/designation-usage defines the following codes:</p><table class=\"codes\"><tr><td style=\"white-space:nowrap\"><b>Code</b></td><td><b>Display</b></td><td><b>Definition</b></td></tr><tr><td style=\"white-space:nowrap\">display<a name=\"designation-usage-display\"> </a></td><td>Display</td><td>A deisgnation suitable for display to an end-user</td></tr></table></div>"
                  },
                  "extension": [
                    {
                      "url": "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                      "valueCode": "fhir"
                    }
                  ],
                  "url": "http://terminology.hl7.org/CodeSystem/designation-usage",
                  "version": "4.2.0",
                  "name": "DesignationUsage",
                  "title": "Designation Usage",
                  "status": "draft",
                  "experimental": false,
                  "date": "2020-05-09T12:49:00-04:00",
                  "publisher": "FHIR Project team",
                  "contact": [
                    {
                      "telecom": [
                        {
                          "system": "url",
                          "value": "http://hl7.org/fhir"
                        }
                      ]
                    }
                  ],
                  "description": "Preferred value set for Condition Categories.",
                  "caseSensitive": true,
                  "valueSet": "http://terminology.hl7.org/ValueSet/designation-usage",
                  "content": "complete",
                  "concept": [
                    {
                      "code": "display",
                      "display": "Display",
                      "definition": "A deisgnation suitable for display to an end-user"
                    }
                  ]
                }
                """,
            },
            new()
            {
                Key = DbExternalInclusion.GetIndex(),
                ResourceType = FHIRAllTypes.ValueSet,
                Id = "designation-usage",
                Name = "VsDesignationUsage",
                Version = "4.1.0",
                OverrideVersion = false,
                UnversionedUrl = "http://terminology.hl7.org/ValueSet/designation-usage",
                VersionedUrl = "http://terminology.hl7.org/ValueSet/designation-usage|4.1.0",
                Reason = "This ValueSet was only published in hl7.terminology@1.0.0. Currently working through resolution.",
                IncludeInPackages = null,
                Json = """
                {
                  "resourceType": "ValueSet",
                  "id": "designation-usage",
                  "meta": {
                    "profile": [
                      "http://hl7.org/fhir/StructureDefinition/shareablevalueset"
                    ]
                  },
                  "text": {
                    "status": "generated",
                    "div": "<div xmlns=\"http://www.w3.org/1999/xhtml\"><h2>Designation Usage</h2><div><p>Preferred value set for Condition Categories.</p>\n</div><ul><li>Include all codes defined in <a href=\"CodeSystem-designation-usage.html\"><code>http://terminology.hl7.org/CodeSystem/designation-usage</code></a></li></ul></div>"
                  },
                  "extension": [
                    {
                      "url": "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                      "valueCode": "fhir"
                    }
                  ],
                  "url": "http://terminology.hl7.org/ValueSet/designation-usage",
                  "version": "4.1.0",
                  "name": "DesignationUsage",
                  "title": "Designation Usage",
                  "status": "draft",
                  "experimental": false,
                  "date": "2020-02-23T20:41:39-05:00",
                  "publisher": "HL7 (FHIR Project)",
                  "contact": [
                    {
                      "telecom": [
                        {
                          "system": "url",
                          "value": "http://hl7.org/fhir"
                        },
                        {
                          "system": "email",
                          "value": "fhir@lists.hl7.org"
                        }
                      ]
                    }
                  ],
                  "description": "Preferred value set for Condition Categories.",
                  "immutable": true,
                  "compose": {
                    "include": [
                      {
                        "system": "http://terminology.hl7.org/CodeSystem/designation-usage"
                      }
                    ]
                  }
                }
                """,
            }
        ];

        inclusions.Insert(_db, insertPrimaryKey: true);
    }

    /// <summary>
    /// Initializes the database connection and sets up the necessary tables and metadata.
    /// </summary>
    /// <param name="createdNew">Outputs a boolean indicating whether a new database was created.</param>
    /// <exception cref="Exception">Thrown when the package information does not match or the database connection is not initialized.</exception>
    private void initNewDb(
        bool ensureDeleted)
    {
        if (_definitions == null)
        {
            throw new Exception("Cannot initialize clean database without packages!");
        }

        if (ensureDeleted)
        {
            DbContentClasses.DropTables(_db);
        }

        // create all our tables (non-destructive)
        DbContentClasses.CreateTables(_db);

        foreach ((DefinitionCollection dc, DcInfoRec _) in _definitions)
        {
            string shortName;
            if (_isCoreComparison)
            {
                shortName = dc.FhirSequence.ToRLiteral();
            }
            else if (_isVersionComparison)
            {
                shortName = $"V{dc.MainPackageVersion.Replace('.', '_')}";
            }
            else
            {
                shortName = $"{dc.MainPackageId.ToPascalCase()}-V{dc.MainPackageVersion.Replace('.', '_')}";
            }

            List<string> deps = dc.Manifests.Keys
                .Where(key => !key.Id.StartsWith(dc.MainPackageId, StringComparison.OrdinalIgnoreCase))
                .Select(key => key.Id + "@" + key.Version)
                .ToList();

            // add data about our packages
            if (DbFhirPackage.SelectSingle(_db, PackageId: dc.MainPackageId, PackageVersion: dc.MainPackageVersion) is not DbFhirPackage pm)
            {
                pm = new()
                {
                    Name = dc.Name,
                    ShortName = shortName,
                    PackageId = dc.MainPackageId,
                    PackageVersion = dc.MainPackageVersion,
                    FhirVersionShort = dc.FhirSequence.ToShortVersion(),
                    CanonicalUrl = dc.MainPackageCanonical,
                    DefinitionFhirSequence = dc.FhirSequence,
                    Dependencies = deps.Count > 0 ? string.Join(",", deps) : null,
                };

                _db.Insert(pm);
            }
        }

        loadDefaultExternalInclusions();
    }

    public bool TryLoadFromDefinitionCollections(
        HashSet<string> _exclusionSet,
        HashSet<string> _escapeValveCodes)
    {
        if (_definitions == null)
        {
            _logger.LogError("Cannot export contents without a DefinitionCollection!");
            return false;
        }

        List<DbFhirPackage> r2Packages = [];

        foreach ((DefinitionCollection dc, DcInfoRec _) in _definitions)
        {
            // get the package metadata for this definition collection
            DbFhirPackage pm = DbFhirPackage.SelectSingle(_db, PackageId: dc.MainPackageId, PackageVersion: dc.MainPackageVersion)
                    ?? throw new Exception($"Package {dc.MainPackageId}@{dc.MainPackageVersion} was not found in the database!");

            // load our code systems
            addCodeSystemsToDb(pm, dc, _exclusionSet);

            // load our value sets
            addValueSetsToDb(pm, dc, _exclusionSet, _escapeValveCodes);

            // load our structures
            addStructuresToDb(pm, dc, _exclusionSet);

            if (dc.FhirSequence == FhirReleases.FhirSequenceCodes.DSTU2)
            {
                r2Packages.Add(pm);
            }
        }

        // do value set post-processing
        doValueSetPostProcessing(_escapeValveCodes);

        // do element post-processing
        findInheritedElements(r2Packages);
        fixContentReferences();
        //expandContentReferenceElements();

        // do code-system post-processing
        doCodeSystemPostProcessing();

        return true;
    }

    private void doCodeSystemPostProcessing()
    {
        DbFhirPackage? r5 = DbFhirPackage.SelectSingle(
            _db,
            PackageId: "hl7.fhir.r5.core");
        
        if (r5 != null)
        {
            IDbCommand command = _db.CreateCommand();
            command.CommandText = $"""
                delete from {DbCodeSystem.DefaultTableName}
                where {nameof(DbCodeSystem.FhirPackageKey)} = {r5.Key}
                and {nameof(DbCodeSystem.SourcePackageMoniker)} = 'hl7.terminology@5.1.0'
                and {nameof(DbCodeSystem.UnversionedUrl)} not in (
                    select distinct {nameof(DbValueSetConcept.System)}
                    from {DbValueSetConcept.DefaultTableName}
                    where {nameof(DbValueSetConcept.FhirPackageKey)} = {r5.Key}
                    and {nameof(DbValueSetConcept.ValueSetKey)} in (
                        select distinct {nameof(DbElement.BindingValueSetKey)}
                        from {DbElement.DefaultTableName}
                        where {nameof(DbElement.FhirPackageKey)} = {r5.Key}
                        and {nameof(DbElement.BindingValueSetKey)} is not null
                    )
                    and {nameof(DbValueSetConcept.System)} is not null
                )
                """;
            command.ExecuteNonQuery();

            command = _db.CreateCommand();
            command.CommandText =
                $"delete from {DbCodeSystemConcept.DefaultTableName}" +
                $" where {nameof(DbCodeSystemConcept.CodeSystemKey)} not in (select {nameof(DbCodeSystem.Key)} from {DbCodeSystem.DefaultTableName})";
            command.ExecuteNonQuery();

            command = _db.CreateCommand();
            command.CommandText =
                $"delete from {DbCodeSystemFilter.DefaultTableName}" +
                $" where {nameof(DbCodeSystemFilter.CodeSystemKey)} not in (select {nameof(DbCodeSystem.Key)} from {DbCodeSystem.DefaultTableName})";
            command.ExecuteNonQuery();

            command = _db.CreateCommand();
            command.CommandText =
                $"delete from {DbCodeSystemPropertyDefinition.DefaultTableName}" +
                $" where {nameof(DbCodeSystemPropertyDefinition.CodeSystemKey)} not in (select {nameof(DbCodeSystem.Key)} from {DbCodeSystem.DefaultTableName})";
            command.ExecuteNonQuery();

            command = _db.CreateCommand();
            command.CommandText =
                $"delete from {DbCodeSystemConceptProperty.DefaultTableName}" +
                $" where {nameof(DbCodeSystemConceptProperty.CodeSystemPropertyDefinitionKey)}" +
                $" not in (select {nameof(DbCodeSystemPropertyDefinition.Key)} from {DbCodeSystemPropertyDefinition.DefaultTableName})";
            command.ExecuteNonQuery();
        }

        return;
    }

    private void findInheritedElements(List<DbFhirPackage> r2Packages)
    {
        foreach (DbFhirPackage package in r2Packages)
        {
            // resolve the 'Element.id' element
            DbElement? idElement = DbElement.SelectSingle(
                _db,
                FhirPackageKey: package.Key,
                Id: "Element.id");

            if (idElement is not null)
            {
                // get elements with matching definition
                List<DbElement> elements = DbElement.SelectList(
                    _db,
                    FhirPackageKey: package.Key,
                    Short: idElement.Short,
                    FullCollatedTypeLiteral: idElement.FullCollatedTypeLiteral);

                elements.RemoveAll(e => e.Key == idElement.Key);

                // update element information
                foreach (DbElement ed in elements)
                {
                    ed.BasePath = idElement.Path;
                    ed.BaseElementKey = idElement.Key;
                    ed.BaseStructureKey = idElement.StructureKey;
                    ed.IsSimpleType = true;
                }

                elements.Update(_db);
            }

            // resolve the 'Extension' element
            DbElement? extElement = DbElement.SelectSingle(
                _db,
                FhirPackageKey: package.Key,
                Id: "Element.extension");

            if (extElement is not null)
            {
                // get elements with matching definition
                List<DbElement> elements = DbElement.SelectList(
                    _db,
                    FhirPackageKey: package.Key,
                    Short: extElement.Short,
                    FullCollatedTypeLiteral: extElement.FullCollatedTypeLiteral);

                elements.RemoveAll(e => e.Key == extElement.Key);

                // update element information
                foreach (DbElement ed in elements)
                {
                    ed.BasePath = extElement.Path;
                    ed.BaseElementKey = extElement.Key;
                    ed.BaseStructureKey = extElement.StructureKey;
                }

                elements.Update(_db);
            }
        }
    }

    private void fixContentReferences()
    {
        DbContentWithIdCache<DbElement> edCache = new();

        // iterate over resources (need to handle individually since we are inserting elements and need to update the resource field order)
        List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(
            _db,
            ArtifactClass: FhirArtifactClassEnum.Resource);
        foreach (DbStructureDefinition dbSd in structures)
        {
            // get the elements for this structure
            List<DbElement> elements = DbElement.SelectList(
                _db,
                StructureKey: dbSd.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

            // build a lookup for the elements by id
            ILookup<string, DbElement> elementsById = elements.ToLookup(e => e.Id);

            foreach (DbElement dbEd in elements)
            {
                // if this is not a content reference, we are done
                if (!dbEd.DefinedAsContentReference)
                {
                    continue;
                }

                // resolve the content reference to the root element it references (the one with the same id as the full collated type literal)
                DbElement? crElement = elementsById[dbEd.FullCollatedTypeLiteral].FirstOrDefault();

                if (crElement is null)
                {
                    // get the root cr element
                    crElement = DbElement.SelectSingle(
                        _db,
                        FhirPackageKey: dbEd.FhirPackageKey,
                        Id: dbEd.FullCollatedTypeLiteral);
                }

                if (crElement is not null)
                {
                    dbEd.ContentReferenceSourceKey = crElement.Key;
                    dbEd.ContentReferenceSourceId = crElement.Id;
                    edCache.CacheUpdate(dbEd);

                    if (crElement.UsedAsContentReference != true)
                    {
                        crElement.UsedAsContentReference = true;
                        edCache.CacheUpdate(crElement);
                    }
                }

                if (crElement is null)
                {
                    throw new Exception($"Could not find content reference base element for {dbEd.Id} in structure {dbSd.Name} ({dbSd.VersionedUrl})!");
                }
            }
        }

        // commit our changes
        if (edCache.ToUpdateCount > 0)
        {
            DbElement.Update(_db, edCache.ToUpdate);
            _logger.LogInformation($"Expanded content reference elements updated {edCache.ToUpdateCount} existing elements.");
        }

        if (edCache.ToAddCount > 0)
        {
            DbElement.Insert(_db, edCache.ToAdd, ignoreDuplicates: true, insertPrimaryKey: true);
            _logger.LogInformation($"Expanded content reference elements with {edCache.ToAddCount} new elements.");
        }
    }

    private void expandContentReferenceElements()
    {
        // update elements that use content references to expand out their child elements
        DbContentWithIdCache<DbStructureDefinition> sdCache = new();
        DbContentWithIdCache<DbElement> edCache = new();
        
        // iterate over resources (need to handle individually since we are inserting elements and need to update the resource field order)
        List<DbStructureDefinition> structures = DbStructureDefinition.SelectList(
            _db,
            ArtifactClass: FhirArtifactClassEnum.Resource);
        foreach (DbStructureDefinition dbSd in structures)
        {
            // get the elements for this structure
            List<DbElement> elements = DbElement.SelectList(
                _db,
                StructureKey: dbSd.Key,
                orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

            int resourceFieldOrderAdjustment = 0;

            foreach (DbElement dbEd in elements)
            {
                // if we have an adjustment, need to apply it
                if (resourceFieldOrderAdjustment != 0)
                {
                    dbEd.ResourceFieldOrder += resourceFieldOrderAdjustment;
                    edCache.CacheUpdate(dbEd);
                }

                // if this is not a content reference, we are done
                if (!dbEd.DefinedAsContentReference)
                {
                    continue;
                }

                // get the root cr element
                DbElement? crElement = DbElement.SelectSingle(
                    _db,
                    FhirPackageKey: dbEd.FhirPackageKey,
                    Id: dbEd.FullCollatedTypeLiteral);
                if (crElement is not null)
                {
                    dbEd.ContentReferenceSourceKey = crElement.Key;
                    dbEd.ContentReferenceSourceId = crElement.Id;
                    edCache.CacheUpdate(dbEd);

                    if (crElement.UsedAsContentReference != true)
                    {
                        crElement.UsedAsContentReference = true;
                        edCache.CacheUpdate(crElement);
                    }
                }

                if (crElement is null)
                {
                    throw new Exception($"Could not find content reference base element for {dbEd.Id} in structure {dbSd.Name} ({dbSd.VersionedUrl})!");
                }

                // do NOT nest into recursive content references
                if (dbEd.Id.StartsWith(dbEd.FullCollatedTypeLiteral))
                {
                    continue;
                }

                // get all the elements that start at the content reference location
                List<DbElement> crChildElements = DbElement.SelectList(
                    _db,
                    FhirPackageKey: dbEd.FhirPackageKey,
                    StructureKey: dbEd.StructureKey,
                    Id: dbEd.FullCollatedTypeLiteral + ".%",
                    compareStringsWithLike: true,
                    orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);

                if (crChildElements.Count == 0)
                {
                    throw new Exception($"Could not find content reference elements for {dbEd.Id} in structure {dbSd.Name} ({dbSd.VersionedUrl})!");
                }

                // update properties on the original element to match the content reference base element
                dbEd.ChildElementCount = crElement.ChildElementCount;
                dbEd.FullCollatedTypeLiteral = crElement.FullCollatedTypeLiteral;

                edCache.CacheUpdate(dbEd);

                Dictionary<int, int> parentKeyMappingLookup = [];
                parentKeyMappingLookup[crElement.Key] = dbEd.Key;

                int prefixLen = crElement.Id.Length;

                // iterate over the elements to create copies with the correct paths (skip the actual content reference element)
                foreach (DbElement crEd in crChildElements)
                {
                    resourceFieldOrderAdjustment++;
                    dbSd.SnapshotCount++;
                    dbSd.DifferentialCount++;

                    int key = DbElement.GetIndex();
                    parentKeyMappingLookup[crEd.Key] = key;

                    int? mappedParentKey = (crEd.ParentElementKey is not null) && parentKeyMappingLookup.TryGetValue(crEd.ParentElementKey.Value, out int value)
                        ? value
                        : null;

                    // can update this object and just insert since we are not changing the original
                    crEd.Key = key;
                    crEd.Id = dbEd.Id + crEd.Id[prefixLen..];
                    crEd.Path = dbEd.Path + crEd.Path[prefixLen..];
                    crEd.ResourceFieldOrder = dbEd.ResourceFieldOrder + resourceFieldOrderAdjustment;
                    crEd.ParentElementKey = mappedParentKey;

                    edCache.CacheAdd(crEd);
                }
            }

            if (resourceFieldOrderAdjustment != 0)
            {
                sdCache.CacheUpdate(dbSd);
            }
        }

        // commit our changes
        if (sdCache.ToUpdateCount > 0)
        {
            DbStructureDefinition.Update(_db, sdCache.ToUpdate);
            _logger.LogInformation($"Expanded content reference elements in {sdCache.ToUpdateCount} structure definitions.");
        }

        if (edCache.ToUpdateCount > 0)
        {
            DbElement.Update(_db, edCache.ToUpdate);
            _logger.LogInformation($"Expanded content reference elements updated {edCache.ToUpdateCount} existing elements.");
        }

        if (edCache.ToAddCount > 0)
        {
            DbElement.Insert(_db, edCache.ToAdd, ignoreDuplicates: true, insertPrimaryKey: true);
            _logger.LogInformation($"Expanded content reference elements with {edCache.ToAddCount} new elements.");
        }
    }

    private void addCodeSystemsToDb(
        DbFhirPackage pm,
        DefinitionCollection dc,
        HashSet<string> _exclusionSet)
    {
        List<DbCodeSystem> dbCodeSystems = [];
        List<DbCodeSystemFilter> dbCodeSystemFilters = [];
        List<DbCodeSystemPropertyDefinition> dbCodeSystemPropertyDefinitions = [];
        List<DbCodeSystemConcept> allDbConcepts = [];
        List<DbCodeSystemConceptProperty> allDbConceptProperties = [];

        string fhirVersionLiteral = pm.DefinitionFhirSequence.ToString();

        // iterate over the code systems in the definition collection
        foreach ((string codeSystemUrl, CodeSystem cs) in dc.CodeSystemsByUrl.OrderBy(kvp => kvp.Key))
        {
            DbCodeSystem? existingDbCs = DbCodeSystem.SelectSingle(
                _db,
                FhirPackageKey: pm.Key,
                UnversionedUrl: codeSystemUrl);

            // check to see if this code system already fmlExists
            if (existingDbCs != null)
            {
                continue;
            }

            bool isExcluded = _exclusionSet.Contains(codeSystemUrl);

            // will not further process code systems we know we will not process
            if (isExcluded || (cs == null))
            {
                if (cs == null)
                {
                    continue;
                }

                int cseVsPipeIndex = string.IsNullOrEmpty(cs.ValueSet) ? -1 : cs.ValueSet.LastIndexOf('|');
                int cseSuppPipeIndex = string.IsNullOrEmpty(cs.Supplements) ? -1 : cs.Supplements.LastIndexOf('|');

                // still add a metadata record for excluded or null code systems
                DbCodeSystem excludedDbCodeSystem = new()
                {
                    Key = DbCodeSystem.GetIndex(),
                    FhirPackageKey = pm.Key,
                    Id = cs.Id,
                    VersionedUrl = cs.Url + (string.IsNullOrEmpty(cs.Version) ? "" : "|" + cs.Version),
                    UnversionedUrl = cs.Url ?? codeSystemUrl,
                    SourcePackageMoniker = cs.cgPackageSourceAsMoniker(),
                    Name = cs.Name ?? cs.Id,
                    Version = cs.Version ?? pm.PackageVersion,
                    VersionAlgorithmString = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is FhirString cseVaFs) ? cseVaFs.Value : null,
                    VersionAlgorithmCoding = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is Coding cseVaC) ? cseVaC : null,
                    Status = cs.Status,
                    Title = cs.Title.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Description = cs.Description.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Purpose = cs.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Narrative = cs.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                    StandardStatus = cs.cgStandardStatus(),
                    WorkGroup = cs.cgWorkGroup(),
                    FhirMaturity = cs.cgMaturityLevel(),
                    IsExperimental = cs.Experimental,
                    LastChangedDate = cs.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                    Publisher = cs.Publisher,
                    Copyright = cs.Copyright,
                    CopyrightLabel = cs.CopyrightLabel,
                    ApprovalDate = cs.ApprovalDate,
                    LastReviewDate = cs.LastReviewDate,
                    EffectivePeriodStart = cs.EffectivePeriod?.StartElement?.ToDateTimeOffset(TimeSpan.Zero),
                    EffectivePeriodEnd = cs.EffectivePeriod?.EndElement?.ToDateTimeOffset(TimeSpan.Zero),
                    Topic = cs.Topic,
                    RelatedArtifacts = cs.RelatedArtifact,
                    Jurisdictions = cs.Jurisdiction,
                    UseContexts = cs.UseContext,
                    Contacts = cs.Contact,
                    Authors = cs.Author,
                    Editors = cs.Editor,
                    Reviewers = cs.Reviewer,
                    Endorsers = cs.Endorser,
                    RootExtensions = cs.Extension,
                    IsCaseSensitive = cs.CaseSensitive,
                    ValueSetVersioned = cs.ValueSet,
                    ValueSetUnversioned = string.IsNullOrEmpty(cs.ValueSet) ? null : (cseVsPipeIndex == -1 ? cs.ValueSet : cs.ValueSet[0..cseVsPipeIndex]),
                    HierarchyMeaning = cs.HierarchyMeaning,
                    IsCompositional = cs.Compositional,
                    VersionNeeded = cs.VersionNeeded,
                    Content = cs.Content,
                    SupplementsVersioned = cs.Supplements,
                    SupplementsUnversioned = string.IsNullOrEmpty(cs.Supplements) ? null : (cseSuppPipeIndex == -1 ? cs.Supplements : cs.Supplements[0..cseSuppPipeIndex]),
                    Count = 0, // no concepts processed for excluded items
                };

                dbCodeSystems.Add(excludedDbCodeSystem);

                continue;
            }

            int csVsPipeIndex = string.IsNullOrEmpty(cs.ValueSet) ? -1 : cs.ValueSet.LastIndexOf('|');
            int csSuppPipeIndex = string.IsNullOrEmpty(cs.Supplements) ? -1 : cs.Supplements.LastIndexOf('|');

            // create the DbCodeSystem record
            DbCodeSystem dbCodeSystem = new()
            {
                Key = DbCodeSystem.GetIndex(),
                FhirPackageKey = pm.Key,
                Id = cs.Id,
                VersionedUrl = cs.Url + (string.IsNullOrEmpty(cs.Version) ? "" : "|" + cs.Version),
                UnversionedUrl = cs.Url ?? codeSystemUrl,
                SourcePackageMoniker = cs.cgPackageSourceAsMoniker(),
                Name = cs.Name ?? cs.Id,
                Version = cs.Version ?? pm.PackageVersion,
                VersionAlgorithmString = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is FhirString csVaFs) ? csVaFs.Value : null,
                VersionAlgorithmCoding = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is Coding csVaC) ? csVaC : null,
                Status = cs.Status,
                Title = cs.Title.ProcessCoreTextForLinks(fhirVersionLiteral),
                Description = cs.Description.ProcessCoreTextForLinks(fhirVersionLiteral),
                Purpose = cs.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                Narrative = cs.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                StandardStatus = cs.cgStandardStatus(),
                WorkGroup = cs.cgWorkGroup(),
                FhirMaturity = cs.cgMaturityLevel(),
                IsExperimental = cs.Experimental,
                LastChangedDate = cs.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                Publisher = cs.Publisher,
                Copyright = cs.Copyright,
                CopyrightLabel = cs.CopyrightLabel,
                ApprovalDate = cs.ApprovalDate,
                LastReviewDate = cs.LastReviewDate,
                EffectivePeriodStart = cs.EffectivePeriod?.StartElement?.ToDateTimeOffset(TimeSpan.Zero),
                EffectivePeriodEnd = cs.EffectivePeriod?.EndElement?.ToDateTimeOffset(TimeSpan.Zero),
                Topic = cs.Topic,
                RelatedArtifacts = cs.RelatedArtifact,
                Jurisdictions = cs.Jurisdiction,
                UseContexts = cs.UseContext,
                Contacts = cs.Contact,
                Authors = cs.Author,
                Editors = cs.Editor,
                Reviewers = cs.Reviewer,
                Endorsers = cs.Endorser,
                RootExtensions = cs.Extension,
                IsCaseSensitive = cs.CaseSensitive,
                ValueSetVersioned = cs.ValueSet,
                ValueSetUnversioned = string.IsNullOrEmpty(cs.ValueSet) ? null : (csVsPipeIndex == -1 ? cs.ValueSet : cs.ValueSet[0..csVsPipeIndex]),
                HierarchyMeaning = cs.HierarchyMeaning,
                IsCompositional = cs.Compositional,
                VersionNeeded = cs.VersionNeeded,
                Content = cs.Content,
                SupplementsVersioned = cs.Supplements,
                SupplementsUnversioned = string.IsNullOrEmpty(cs.Supplements) ? null : (csSuppPipeIndex == -1 ? cs.Supplements : cs.Supplements[0..csSuppPipeIndex]),
                Count = cs.Count,
            };

            dbCodeSystems.Add(dbCodeSystem);

            // add defined filters to the list
            foreach (CodeSystem.FilterComponent filter in cs.Filter)
            {
                DbCodeSystemFilter dbFilter = new()
                {
                    Key = DbCodeSystemFilter.GetIndex(),
                    FhirPackageKey = pm.Key,
                    CodeSystemKey = dbCodeSystem.Key,
                    Code = filter.Code,
                    Description = filter.Description,
                    Operators = string.Join("|", filter.Operator?.Select(op => op.GetLiteral()) ?? []),
                    Value = filter.Value,
                };

                dbCodeSystemFilters.Add(dbFilter);
            }

            // add property definitions to the list
            foreach (CodeSystem.PropertyComponent? property in cs.Property)
            {
                DbCodeSystemPropertyDefinition dbPropertyDefinition = new()
                {
                    Key = DbCodeSystemPropertyDefinition.GetIndex(),
                    FhirPackageKey = pm.Key,
                    CodeSystemKey = dbCodeSystem.Key,
                    Code = property.Code,
                    Uri = property.Uri,
                    Description = property.Description,
                    Type = property.Type ?? Hl7.Fhir.Model.CodeSystem.PropertyType.Code,
                };

                dbCodeSystemPropertyDefinitions.Add(dbPropertyDefinition);
            }
            // add concepts to the list (handling hierarchy)
            List<DbCodeSystemConcept> conceptsForThisCodeSystem = [];
            List<DbCodeSystemConceptProperty> conceptPropertiesForThisCodeSystem = [];
            
            // create a lookup for property definitions by code for this code system
            ILookup<string, DbCodeSystemPropertyDefinition> propertyDefsByCode = dbCodeSystemPropertyDefinitions
                .Where(pd => pd.CodeSystemKey == dbCodeSystem.Key)
                .ToLookup(pd => pd.Code);

            int globalOrder = allDbConcepts.Count;
            processConceptHierarchy(
                cs.Concept,
                dbCodeSystem.Key,
                pm.Key,
                null,
                0,
                ref globalOrder, 
                conceptsForThisCodeSystem,
                conceptPropertiesForThisCodeSystem,
                propertyDefsByCode,
                fhirVersionLiteral);

            allDbConcepts.AddRange(conceptsForThisCodeSystem);
            allDbConceptProperties.AddRange(conceptPropertiesForThisCodeSystem);

        }

        _logger.LogInformation($"Inserting CodeSystems for {pm.PackageId}@{pm.PackageVersion} into database...");

        _db.Insert(dbCodeSystems, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbCodeSystems.Count} CodeSystems");

        _db.Insert(dbCodeSystemFilters, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbCodeSystemFilters.Count} CodeSystem Filters");

        _db.Insert(dbCodeSystemPropertyDefinitions, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbCodeSystemPropertyDefinitions.Count} CodeSystem Property Definitions");

        _db.Insert(allDbConcepts, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {allDbConcepts.Count} CodeSystem Concepts");

        _db.Insert(allDbConceptProperties, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {allDbConceptProperties.Count} CodeSystem Concept Properties");

        return;
    }

    private void processConceptHierarchy(
        IList<CodeSystem.ConceptDefinitionComponent> concepts,
        int codeSystemKey,
        int fhirPackageKey,
        int? parentConceptKey,
        int relativeOrder,
        ref int globalOrder,
        List<DbCodeSystemConcept> allConcepts,
        List<DbCodeSystemConceptProperty> allConceptProperties,
        ILookup<string, DbCodeSystemPropertyDefinition> propertyDefsByCode,
        string fhirVersionLiteral)
    {
        foreach (CodeSystem.ConceptDefinitionComponent concept in concepts)
        {
            // skip concepts without valid codes - I have no way of correctly passing them through
            if (string.IsNullOrEmpty(concept.Code))
            {
                continue;
            }

            // create the DbCodeSystemConcept record
            DbCodeSystemConcept dbConcept = new()
            {
                Key = DbCodeSystemConcept.GetIndex(),
                FhirPackageKey = fhirPackageKey,
                CodeSystemKey = codeSystemKey,
                FlatOrder = globalOrder++,
                RelativeOrder = relativeOrder,
                Code = concept.Code,
                Display = concept.Display,
                Definition = concept.Definition.ProcessCoreTextForLinks(fhirVersionLiteral),
                Designations = concept.Designation,
                Properties = concept.Property,
                ParentConceptKey = parentConceptKey,
                ChildConceptCount = concept.Concept?.Count ?? 0,
            };

            allConcepts.Add(dbConcept);

            // process concept properties
            foreach (CodeSystem.ConceptPropertyComponent conceptProperty in concept.Property)
            {
                // find the corresponding property definition
                DbCodeSystemPropertyDefinition? propertyDef = propertyDefsByCode[conceptProperty.Code].FirstOrDefault();
                if (propertyDef != null)
                {
                    DbCodeSystemConceptProperty dbConceptProperty = new()
                    {
                        Key = DbCodeSystemConceptProperty.GetIndex(),
                        FhirPackageKey = fhirPackageKey,
                        CodeSystemConceptKey = dbConcept.Key,
                        CodeSystemPropertyDefinitionKey = propertyDef.Key,
                        Code = conceptProperty.Code,
                        Type = getPropertyTypeFromValue(conceptProperty.Value),
                        Value = getPropertyValueString(conceptProperty.Value),
                    };

                    allConceptProperties.Add(dbConceptProperty);
                }
            }

            // recursively process child concepts
            if (concept.Concept?.Count > 0)
            {
                processConceptHierarchy(
                    concept.Concept,
                    codeSystemKey,
                    fhirPackageKey,
                    dbConcept.Key,
                    0, // reset relative order for children
                    ref globalOrder,
                    allConcepts,
                    allConceptProperties,
                    propertyDefsByCode,
                    fhirVersionLiteral);
            }

            relativeOrder++;
        }
    }

    private static Hl7.Fhir.Model.CodeSystem.PropertyType getPropertyTypeFromValue(DataType? value)
    {
        return value switch
        {
            Code => Hl7.Fhir.Model.CodeSystem.PropertyType.Code,
            Coding => Hl7.Fhir.Model.CodeSystem.PropertyType.Coding,
            FhirString => Hl7.Fhir.Model.CodeSystem.PropertyType.String,
            Integer => Hl7.Fhir.Model.CodeSystem.PropertyType.Integer,
            FhirBoolean => Hl7.Fhir.Model.CodeSystem.PropertyType.Boolean,
            FhirDateTime => Hl7.Fhir.Model.CodeSystem.PropertyType.DateTime,
            FhirDecimal => Hl7.Fhir.Model.CodeSystem.PropertyType.Decimal,
            _ => Hl7.Fhir.Model.CodeSystem.PropertyType.Code
        };
    }

    private static string getPropertyValueString(DataType? value)
    {
        return value switch
        {
            Code c => c.Value ?? "",
            FhirString s => s.Value ?? "",
            Integer i => i.Value?.ToString() ?? "",
            FhirBoolean b => b.Value?.ToString() ?? "",
            FhirDateTime dt => dt.Value ?? "",
            FhirDecimal d => d.Value?.ToString() ?? "",
            Coding coding => $"{coding.System}|{coding.Code}|{coding.Display}",
            _ => value?.ToString() ?? ""
        };
    }

    private void doValueSetPostProcessing(HashSet<string> _escapeValveCodes)
    {
        // link code systems to value sets where possible
        {
            IDbCommand command = _db.CreateCommand();
            command.CommandText = $"""
                UPDATE {DbValueSetConcept.DefaultTableName} 
                SET {nameof(DbValueSetConcept.CodeSystemConceptKey)} = (
                    SELECT csc.{nameof(DbCodeSystemConcept.Key)}
                    FROM {DbCodeSystem.DefaultTableName} cs
                    JOIN {DbCodeSystemConcept.DefaultTableName} csc ON cs.{nameof(DbCodeSystem.Key)} = csc.{nameof(DbCodeSystemConcept.CodeSystemKey)}
                    WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.FhirPackageKey)} = cs.{nameof(DbCodeSystem.FhirPackageKey)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.System)} = cs.{nameof(DbCodeSystem.UnversionedUrl)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.SystemVersion)} = cs.{nameof(DbCodeSystem.Version)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Code)} = csc.{nameof(DbCodeSystemConcept.Code)}
                )
                WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.CodeSystemConceptKey)} IS NULL
                  AND EXISTS (
                    SELECT 1 
                    FROM {DbCodeSystem.DefaultTableName} cs
                    JOIN {DbCodeSystemConcept.DefaultTableName} csc ON cs.{nameof(DbCodeSystem.Key)} = csc.{nameof(DbCodeSystemConcept.CodeSystemKey)}
                    WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.FhirPackageKey)} = cs.{nameof(DbCodeSystem.FhirPackageKey)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.System)} = cs.{nameof(DbCodeSystem.UnversionedUrl)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.SystemVersion)} = cs.{nameof(DbCodeSystem.Version)}
                      AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Code)} = csc.{nameof(DbCodeSystemConcept.Code)}
                  )
                """;

            command.ExecuteNonQuery();
        }

        // because we have the wrong version of THO (correct version no longer exists), we need to remove some duplicate definitions
        {
            // get the list of value sets rooted in THO that are not bound to structures
            List<DbValueSet> thoVsList = DbValueSet.SelectList(
                _db,
                UnversionedUrl: "http://terminology.hl7.org/ValueSet/%",
                BindingCountExtended: 0,
                compareStringsWithLike: true);

            List<DbValueSet> toDelete = [];

            // traverse the list of THO value sets and see if there is a matching HL7.org value set
            foreach (DbValueSet thoVs in thoVsList)
            {
                DbValueSet? hl7Vs = DbValueSet.SelectSingle(
                    _db,
                    FhirPackageKey: thoVs.FhirPackageKey,
                    Id: thoVs.Id,
                    UnversionedUrl: "http://hl7.org/fhir/ValueSet/%",
                    compareStringsWithLike: true);

                if (hl7Vs is not null)
                {
                    toDelete.Add(thoVs);
                }
            }

            // delete the offending duplicate value sets
            if (toDelete.Count > 0)
            {
                _logger.LogInformation($"Removing {toDelete.Count} duplicate THO-rooted value sets that have HL7.org counterparts...");
                DbValueSet.Delete(_db, toDelete);

                // also need to delete associated concepts
                IDbCommand command = _db.CreateCommand();
                command.CommandText = $"""
                delete from {DbValueSetConcept.DefaultTableName}
                where {nameof(DbValueSetConcept.ValueSetKey)} not in
                (
                  select distinct {nameof(DbValueSet.Key)}
                  from {DbValueSet.DefaultTableName}
                )
                """;

                int count = command.ExecuteNonQuery();
                _logger.LogInformation($" Removed {count} associated ValueSetConcept records.");
            }
        }

        // mark value sets that contain escape valve codes
        {
            IDbCommand command = _db.CreateCommand();
            command.CommandText = $"""
                update {DbValueSet.DefaultTableName}
                set {nameof(DbValueSet.HasEscapeValveCode)} = 1
                where {nameof(DbValueSet.Key)} in
                (
                  select distinct {nameof(DbValueSetConcept.ValueSetKey)}
                  from {DbValueSetConcept.DefaultTableName}
                  where {nameof(DbValueSetConcept.Code)} in ({string.Join(", ", _escapeValveCodes.Select(v => "'" + v + "'"))})
                )
                """;

            command.ExecuteNonQuery();
        }

        // fix known system URL issues
        {
            IDbCommand command = _db.CreateCommand();
            command.CommandText = $"""
                update {DbValueSetConcept.DefaultTableName}
                set {nameof(DbValueSetConcept.System)} = 'http://hl7.org/fhir/sample-security-structural-roles'
                where {nameof(DbValueSetConcept.System)} = 'sample-security-structural-roles'
                and {nameof(DbValueSetConcept.SystemVersion)} = '5.0.0'
                """;

            command.ExecuteNonQuery();
        }

        // fill in missing display values from code systems
        {
            // update display values from code systems where possible
            IDbCommand command = _db.CreateCommand();
            command.CommandText = $"""
                UPDATE {DbValueSetConcept.DefaultTableName} 
                SET {nameof(DbValueSetConcept.Display)} = (
                    SELECT csc.{nameof(DbCodeSystemConcept.Display)}
                    FROM {DbCodeSystemConcept.DefaultTableName} csc
                    WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.CodeSystemConceptKey)} = csc.{nameof(DbCodeSystemConcept.Key)}
                )
                WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Display)} IS NULL
                  AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.CodeSystemConceptKey)} IS NOT NULL
                """;

            command.ExecuteNonQuery();

            //IDbCommand command = _db.CreateCommand();
            //command.CommandText = $"""
            //    UPDATE {DbValueSetConcept.DefaultTableName} 
            //    SET {nameof(DbValueSetConcept.Display)} = (
            //        SELECT csc.{nameof(DbCodeSystemConcept.Display)}
            //        FROM {DbCodeSystem.DefaultTableName} cs
            //        JOIN {DbCodeSystemConcept.DefaultTableName} csc ON cs.{nameof(DbCodeSystem.Key)} = csc.{nameof(DbCodeSystemConcept.CodeSystemKey)}
            //        WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.FhirPackageKey)} = cs.{nameof(DbCodeSystem.FhirPackageKey)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.System)} = cs.{nameof(DbCodeSystem.UnversionedUrl)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.SystemVersion)} = cs.{nameof(DbCodeSystem.Version)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Code)} = csc.{nameof(DbCodeSystemConcept.Code)}
            //    )
            //    WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Display)} IS NULL
            //      AND EXISTS (
            //        SELECT 1 
            //        FROM {DbCodeSystem.DefaultTableName} cs
            //        JOIN {DbCodeSystemConcept.DefaultTableName} csc ON cs.{nameof(DbCodeSystem.Key)} = csc.{nameof(DbCodeSystemConcept.CodeSystemKey)}
            //        WHERE {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.FhirPackageKey)} = cs.{nameof(DbCodeSystem.FhirPackageKey)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.System)} = cs.{nameof(DbCodeSystem.UnversionedUrl)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.SystemVersion)} = cs.{nameof(DbCodeSystem.Version)}
            //          AND {DbValueSetConcept.DefaultTableName}.{nameof(DbValueSetConcept.Code)} = csc.{nameof(DbCodeSystemConcept.Code)}
            //      )
            //    """;

            //command.ExecuteNonQuery();
        }

        return;
    }

    private void addValueSetsToDb(
        DbFhirPackage pm,
        DefinitionCollection dc,
        HashSet<string> _exclusionSet,
        HashSet<string> _escapeValveCodes)
    {
        List<DbValueSet> dbValueSets = [];
        List<DbValueSetConcept> allDbConcepts = [];

        string fhirVersionLiteral = pm.DefinitionFhirSequence.ToString();

        // iterate over the value sets in the definition collection
        foreach ((string unversionedUrl, string[] versions) in dc.ValueSetVersions.OrderBy(kvp => kvp.Key))
        {
            // only use the highest version in the package
            string vsVersion = versions.OrderDescending().First();
            string versionedUrl = unversionedUrl + "|" + vsVersion;

            DbValueSet? existingDbVs = DbValueSet.SelectSingle(
                _db,
                FhirPackageKey: pm.Key,
                UnversionedUrl: unversionedUrl,
                Version: vsVersion);

            // check to see if this value set already fmlExists
            if (existingDbVs != null)
            {
                continue;
            }

            // try to expand this value set
            if (!dc.ValueSetsByVersionedUrl.TryGetValue(versionedUrl, out ValueSet? uvs))
            {
                throw new Exception($"Failed to resolve ValueSet {versionedUrl} in {dc.MainPackageId}:{dc.MainPackageVersion}.");
            }

            bool canExpand = dc.TryExpandVs(versionedUrl, out ValueSet? vs, out string? expandMessage);

            bool? hasEscapeCode = !canExpand
                ? null
                : vs?.cgHasCode(_escapeValveCodes);

            IEnumerable<StructureElementCollection> coreBindings = dc.CoreBindingsForVs(versionedUrl);
            BindingStrength? strongestBindingCore = dc.StrongestBinding(coreBindings);
            IReadOnlyDictionary<string, BindingStrength> coreBindingStrengthByType = dc.BindingStrengthByType(coreBindings);

            IEnumerable<StructureElementCollection> extendedBindings = dc.ExtendedBindingsForVs(versionedUrl);
            BindingStrength? strongestBindingExtended = dc.StrongestBinding(extendedBindings);
            IReadOnlyDictionary<string, BindingStrength> extendedBindingStrengthByType = dc.BindingStrengthByType(extendedBindings);

            bool isExcluded = _exclusionSet.Contains(unversionedUrl);
            
            // will not further process value sets we know we will not process
            if (isExcluded ||
                !canExpand ||
                (vs == null))
            {
                // still add a metadata record
                DbValueSet vsmExcluded = new()
                {
                    Key = DbValueSet.GetIndex(),
                    FhirPackageKey = pm.Key,
                    Id = uvs.Id,
                    VersionedUrl = versionedUrl,
                    UnversionedUrl = unversionedUrl,
                    SourcePackageMoniker = uvs.cgPackageSourceAsMoniker(),
                    Name = uvs.Name,
                    Version = vsVersion,
                    VersionAlgorithmString = (uvs.VersionAlgorithm != null) && (uvs.VersionAlgorithm is FhirString vsmVaFs) ? vsmVaFs.Value : null,
                    VersionAlgorithmCoding = (uvs.VersionAlgorithm != null) && (uvs.VersionAlgorithm is Coding vsmVaC) ? vsmVaC : null,
                    Status = uvs.Status,
                    Title = uvs.Title.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Description = uvs.Description.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Purpose = uvs.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Narrative = uvs.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                    StandardStatus = uvs.cgStandardStatus(),
                    WorkGroup = uvs.cgWorkGroup(),
                    FhirMaturity = uvs.cgMaturityLevel(),
                    IsExperimental = uvs.Experimental,
                    LastChangedDate = uvs.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                    Publisher = uvs.Publisher,
                    Copyright = uvs.Copyright,
                    CopyrightLabel = uvs.CopyrightLabel,
                    ApprovalDate = uvs.ApprovalDate,
                    LastReviewDate = uvs.LastReviewDate,
                    EffectivePeriodStart = uvs.EffectivePeriod?.StartElement?.ToDateTimeOffset(TimeSpan.Zero),
                    EffectivePeriodEnd = uvs.EffectivePeriod?.EndElement?.ToDateTimeOffset(TimeSpan.Zero),
                    Topic = uvs.Topic,
                    RelatedArtifacts = uvs.RelatedArtifact,
                    Jurisdictions = uvs.Jurisdiction,
                    UseContexts = uvs.UseContext,
                    Contacts = uvs.Contact,
                    Authors = uvs.Author,
                    Editors = uvs.Editor,
                    Reviewers = uvs.Reviewer,
                    Endorsers = uvs.Endorser,
                    RootExtensions = uvs.Extension,
                    IsExcluded = isExcluded,
                    CanExpand = canExpand,
                    ConceptCount = 0,
                    ActiveConcreteConceptCount = 0,
                    HasEscapeValveCode = hasEscapeCode,
                    Message = expandMessage,
                    ReferencedSystems = string.Join(", ", uvs.cgReferencedCodeSystems()),
                    BindingCountCore = coreBindings.Count(),
                    StrongestBindingCore = strongestBindingCore,
                    StrongestBindingCoreCode = coreBindingStrengthByType.TryGetValue("code", out BindingStrength ebscCode) ? ebscCode : null,
                    StrongestBindingCoreCoding = coreBindingStrengthByType.TryGetValue("Coding", out BindingStrength ebscCoding) ? ebscCoding : null,
                    BindingCountExtended = extendedBindings.Count(),
                    StrongestBindingExtended = strongestBindingExtended,
                    StrongestBindingExtendedCode = extendedBindingStrengthByType.TryGetValue("code", out BindingStrength ebseCode) ? ebseCode : null,
                    StrongestBindingExtendedCoding = extendedBindingStrengthByType.TryGetValue("Coding", out BindingStrength ebseCoding) ? ebseCoding : null,
                    Compose = uvs.Compose,
                };

                dbValueSets.Add(vsmExcluded);

                continue;
            }

            DbValueSet dbVs = new()
            {
                Key = DbValueSet.GetIndex(),
                FhirPackageKey = pm.Key,
                Id = vs.Id,
                VersionedUrl = versionedUrl,
                UnversionedUrl = unversionedUrl,
                SourcePackageMoniker = vs.cgPackageSourceAsMoniker(),
                Name = vs.Name,
                Version = vsVersion,
                VersionAlgorithmString = (vs.VersionAlgorithm != null) && (vs.VersionAlgorithm is FhirString vsVaFs) ? vsVaFs.Value : null,
                VersionAlgorithmCoding = (vs.VersionAlgorithm != null) && (vs.VersionAlgorithm is Coding vsVaC) ? vsVaC : null,
                Status = vs.Status,
                Title = vs.Title.ProcessCoreTextForLinks(fhirVersionLiteral),
                Description = vs.Description.ProcessCoreTextForLinks(fhirVersionLiteral),
                Purpose = vs.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                Narrative = vs.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                StandardStatus = vs.cgStandardStatus(),
                WorkGroup = vs.cgWorkGroup(),
                FhirMaturity = vs.cgMaturityLevel(),
                IsExperimental = vs.Experimental,
                LastChangedDate = vs.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                Publisher = vs.Publisher,
                Copyright = vs.Copyright,
                CopyrightLabel = vs.CopyrightLabel,
                ApprovalDate = vs.ApprovalDate,
                LastReviewDate = vs.LastReviewDate,
                EffectivePeriodStart = vs.EffectivePeriod?.StartElement?.ToDateTimeOffset(TimeSpan.Zero),
                EffectivePeriodEnd = vs.EffectivePeriod?.EndElement?.ToDateTimeOffset(TimeSpan.Zero),
                Topic = vs.Topic,
                RelatedArtifacts = vs.RelatedArtifact,
                Jurisdictions = vs.Jurisdiction,
                UseContexts = vs.UseContext,
                Contacts = vs.Contact,
                Authors = vs.Author,
                Editors = vs.Editor,
                Reviewers = vs.Reviewer,
                Endorsers = vs.Endorser,
                RootExtensions = vs.Extension,
                IsExcluded = isExcluded,
                CanExpand = canExpand,
                ConceptCount = 0,
                ActiveConcreteConceptCount = 0,
                HasEscapeValveCode = hasEscapeCode,
                Message = expandMessage,
                ReferencedSystems = string.Join(", ", vs.cgReferencedCodeSystems()),
                BindingCountCore = coreBindings.Count(),
                StrongestBindingCore = strongestBindingCore,
                StrongestBindingCoreCode = coreBindingStrengthByType.TryGetValue("code", out BindingStrength bscCode) ? bscCode : null,
                StrongestBindingCoreCoding = coreBindingStrengthByType.TryGetValue("Coding", out BindingStrength bscCoding) ? bscCoding : null,
                BindingCountExtended = extendedBindings.Count(),
                StrongestBindingExtended = strongestBindingExtended,
                StrongestBindingExtendedCode = extendedBindingStrengthByType.TryGetValue("code", out BindingStrength bseCode) ? bseCode : null,
                StrongestBindingExtendedCoding = extendedBindingStrengthByType.TryGetValue("Coding", out BindingStrength bseCoding) ? bseCoding : null,
                Compose = vs.Compose,
            };

            dbValueSets.Add(dbVs);

            List<DbValueSetConcept> dbConcepts = [];
            int conceptCount = 0;
            int activeConcreteConceptCount = 0;

            // iterate over all the contents of the value set
            foreach (FhirConcept fc in vs.cgGetFlatConcepts(dc))
            {
                conceptCount++;

                // check for inactive or abstract
                if ((fc.IsInactive != true) &&
                    (fc.IsAbstract != true))
                {
                    activeConcreteConceptCount++;
                }

                // check for this record already cdRelationship
                if (DbValueSetConcept.SelectSingle(_db, FhirPackageKey: pm.Key, ValueSetKey: dbVs.Key, System: fc.System, Code: fc.Code) != null)
                {
                    continue;
                }

                // create a new content record
                DbValueSetConcept dbConcept = new()
                {
                    Key = DbValueSetConcept.GetIndex(),
                    FhirPackageKey = pm.Key,
                    ValueSetKey = dbVs.Key,
                    System = fc.System,
                    SystemVersion = fc.Version,
                    Code = fc.Code,
                    Display = fc.Display,
                    Inactive = (fc.IsInactive == true),
                    Abstract = (fc.IsAbstract == true),
                    Properties = fc.Properties.Length == 0 ? null : string.Join(", ", fc.Properties.Select(p => $"{p.Code}={p.Value}")),
                };

                dbConcepts.Add(dbConcept);
            }

            dbVs.ConceptCount = conceptCount;
            dbVs.ActiveConcreteConceptCount = activeConcreteConceptCount;

            allDbConcepts.AddRange(dbConcepts);
        }

        _logger.LogInformation($"Inserting ValueSets for {pm.PackageId}@{pm.PackageVersion} into database...");

        _db.Insert(dbValueSets, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbValueSets.Count} ValueSets");

        _db.Insert(allDbConcepts, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {allDbConcepts.Count} ValueSet Concepts");

        return;
    }

    private void addStructuresToDb(
        DbFhirPackage pm,
        DefinitionCollection dc,
        HashSet<string> _exclusionSet)
    {
        Dictionary<string, DbStructureDefinition> dbStructures = [];
        Dictionary<string, DbElement> dbElements = [];
        List<DbElementType> dbElementTypes = [];
        List<DbElementAdditionalBinding> dbAdditionalBindings = [];

        string fhirVersionLiteral = pm.DefinitionFhirSequence.ToString();

        // iterate over the types of structures
        foreach ((IEnumerable<StructureDefinition> structures, FhirArtifactClassEnum cgClass) in getStructures(dc))
        {
            foreach (StructureDefinition sd in structures)
            {
                string? sdImplements = sd.cgImplementsJoined();

                // will not further process value sets we know we will not process
                if (_exclusionSet.Contains(sd.Url))
                {
                    // still add a metadata record
                    DbStructureDefinition sdmExcluded = new()
                    {
                        Key = DbStructureDefinition.GetIndex(),
                        FhirPackageKey = pm.Key,
                        Id = sd.Id,
                        VersionedUrl = sd.Url + "|" + sd.Version,
                        UnversionedUrl = sd.Url,
                        SourcePackageMoniker = sd.cgPackageSourceAsMoniker(),
                        Name = FhirSanitizationUtils.SanitizeForProperty(sd.Name, replacements: []),
                        Version = sd.Version,
                        VersionAlgorithmString = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is FhirString sdeVaFs) ? sdeVaFs.Value : null,
                        VersionAlgorithmCoding = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is Coding sdeVaC) ? sdeVaC : null,
                        Status = sd.Status,
                        Title = (sd.Title ?? sd.Snapshot?.Element.FirstOrDefault()?.Short).ProcessCoreTextForLinks(fhirVersionLiteral),
                        Description = (sd.Description ?? sd.Snapshot?.Element.FirstOrDefault()?.Definition).ProcessCoreTextForLinks(fhirVersionLiteral),
                        Purpose = sd.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                        Narrative = sd.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                        StandardStatus = sd.cgStandardStatus(),
                        WorkGroup = sd.cgWorkGroup(),
                        FhirMaturity = sd.cgMaturityLevel(),
                        IsExperimental = sd.Experimental,
                        LastChangedDate = sd.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                        Publisher = sd.Publisher,
                        Copyright = sd.Copyright,
                        CopyrightLabel = sd.CopyrightLabel,
                        ApprovalDate = null,
                        LastReviewDate = null,
                        EffectivePeriodStart = null,
                        EffectivePeriodEnd = null,
                        Topic = null,
                        RelatedArtifacts = null,
                        Jurisdictions = sd.Jurisdiction,
                        UseContexts = sd.UseContext,
                        Contacts = sd.Contact,
                        Authors = null,
                        Editors = null,
                        Reviewers = null,
                        Endorsers = null,
                        RootExtensions = sd.Extension,
                        Comment = sd.Snapshot?.Element.FirstOrDefault()?.Comment.ProcessCoreTextForLinks(fhirVersionLiteral),
                        ArtifactClass = cgClass,
                        Message = "Manually excluded",
                        SnapshotCount = sd.Snapshot?.Element.Count ?? 0,
                        DifferentialCount = sd.Differential?.Element.Count ?? 0,
                        Implements = sdImplements,
                    };

                    dbStructures.Add(sd.Id, sdmExcluded);

                    continue;
                }

                // create a new metadata record
                DbStructureDefinition dbStructure = new()
                {
                    Key = DbStructureDefinition.GetIndex(),
                    FhirPackageKey = pm.Key,
                    Id = sd.Id,
                    VersionedUrl = sd.Url + "|" + sd.Version,
                    UnversionedUrl = sd.Url,
                    SourcePackageMoniker = sd.cgPackageSourceAsMoniker(),
                    Name = sd.Name,
                    Version = sd.Version,
                    VersionAlgorithmString = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is FhirString sdVaFs) ? sdVaFs.Value : null,
                    VersionAlgorithmCoding = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is Coding sdVaC) ? sdVaC : null,
                    Status = sd.Status,
                    Title = (sd.Title ?? sd.Snapshot?.Element.FirstOrDefault()?.Short).ProcessCoreTextForLinks(fhirVersionLiteral),
                    Description = (sd.Description ?? sd.Snapshot?.Element.FirstOrDefault()?.Definition).ProcessCoreTextForLinks(fhirVersionLiteral),
                    Purpose = sd.Purpose.ProcessCoreTextForLinks(fhirVersionLiteral),
                    Narrative = sd.Text.ProcessCoreTextForLinks(fhirVersionLiteral),
                    StandardStatus = sd.cgStandardStatus(),
                    WorkGroup = sd.cgWorkGroup(),
                    FhirMaturity = sd.cgMaturityLevel(),
                    IsExperimental = sd.Experimental,
                    LastChangedDate = sd.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                    Publisher = sd.Publisher,
                    Copyright = sd.Copyright,
                    CopyrightLabel = sd.CopyrightLabel,
                    ApprovalDate = null,
                    LastReviewDate = null,
                    EffectivePeriodStart = null,
                    EffectivePeriodEnd = null,
                    Topic = null,
                    RelatedArtifacts = null,
                    Jurisdictions = sd.Jurisdiction,
                    UseContexts = sd.UseContext,
                    Contacts = sd.Contact,
                    Authors = null,
                    Editors = null,
                    Reviewers = null,
                    Endorsers = null,
                    RootExtensions = sd.Extension,
                    Comment = (sd.Snapshot?.Element.FirstOrDefault()?.Comment).ProcessCoreTextForLinks(fhirVersionLiteral),
                    ArtifactClass = cgClass,
                    Message = string.Empty,
                    SnapshotCount = sd.Snapshot?.Element.Count ?? 0,
                    DifferentialCount = sd.Differential?.Element.Count ?? 0,
                    Implements = sdImplements,
                };

                dbStructures.Add(sd.Id, dbStructure);

                // iterate over all the elements of the structure
                foreach (ElementDefinition ed in sd.cgElements(skipSlices: false))
                {
                    addElementAndTypes(dbStructure, sd, ed);
                }
            }
        }

        // save changes
        _logger.LogInformation($"Inserting Structures for {pm.PackageId}@{pm.PackageVersion} into database...");

        _db.Insert(dbStructures.Values, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbStructures.Count} Structures");

        _db.Insert(dbElements.Values, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbElements.Count} Elements");

        _db.Insert(dbElementTypes, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbElementTypes.Count} Discrete Element Types");

        _db.Insert(dbAdditionalBindings, insertPrimaryKey: true);
        _logger.LogInformation($" <<< added {dbAdditionalBindings.Count} Additional Bindings");

        int affectedRows;

        // after all the records are inserted, execute any remaining key-resolution queries
        //affectedRows = _db.UpdateCollatedTypeStructureKeys(pm.Key);
        //_logger.LogInformation($" <<< updated {affectedRows} Collated Type Structure Keys");

        affectedRows = _db.UpdateElementTypeStructureKeys(pm.Key);
        _logger.LogInformation($" <<< updated {affectedRows} Element Type Structure Keys");

        affectedRows = _db.UpdateElementTypeProfileStructureKeys(pm.Key);
        _logger.LogInformation($" <<< updated {affectedRows} Element Type Profile Structure Keys");

        affectedRows = _db.UpdateElementTargetProfileStructureKeys(pm.Key);
        _logger.LogInformation($" <<< updated {affectedRows} Element Target Profile Structure Keys");

        affectedRows = _db.UpdateElementBaseKeys(pm.Key);
        _logger.LogInformation($" <<< updated {affectedRows} Element Base Keys");

        return;

        // TODO(ginoc): For now, exclude extensions, profiles, and logical models - we will want them for generic packages, but do not care for core
        (IEnumerable<StructureDefinition> structures, FhirArtifactClassEnum cgClass)[] getStructures(DefinitionCollection dc) => [
            (dc.PrimitiveTypesByName.Values, FhirArtifactClassEnum.PrimitiveType),
            (dc.ComplexTypesByName.Values, FhirArtifactClassEnum.ComplexType),
            (dc.ResourcesByName.Values, FhirArtifactClassEnum.Resource),
            //(dc.ExtensionsByUrl.Values, FhirArtifactClassEnum.Extension),
            //(dc.ProfilesByUrl.Values, FhirArtifactClassEnum.Profile),
            //(dc.LogicalModelsByUrl.Values, FhirArtifactClassEnum.LogicalModel),
            ];
        
        void addElementAndTypes(
            DbStructureDefinition dbStructure,
            StructureDefinition sd,
            ElementDefinition ed)
        {
            int elementKey = DbElement.GetIndex();

            // check for children
            int childCount = sd.cgElements(
                ed.Path,
                topLevelOnly: true,
                includeRoot: false,
                skipSlices: true).Count();

            List<DbElementType> currentElementTypes = [];
            Dictionary<string, (List<string> targetProfiles, List<string> typeProfiles)> literalAccumulator = [];

            int? bindingVsKey = ed.Binding?.ValueSet == null
                ? null
                : (DbValueSet.SelectSingle(_db, FhirPackageKey: pm.Key, UnversionedUrl: ed.Binding?.ValueSet)?.Key
                  ?? DbValueSet.SelectSingle(_db, FhirPackageKey: pm.Key, VersionedUrl: ed.Binding?.ValueSet)?.Key);

            IEnumerable<ElementDefinition.TypeRefComponent> elementTypes = ed.Type.Select(tr => tr.cgAsR5());
            foreach (ElementDefinition.TypeRefComponent tr in elementTypes)
            {
                string typeName = tr.cgName();

                if (!literalAccumulator.TryGetValue(typeName, out (List<string> targetProfiles, List<string> typeProfiles) accumulator))
                {
                    accumulator = ([], []);
                    literalAccumulator.Add(typeName, accumulator);
                }

                if ((tr.ProfileElement.Count == 0) &&
                    (tr.TargetProfileElement.Count == 0))
                {
                    //string tl = literalForType(tr.cgName(), null, null);

                    DbElementType et = new()
                    {
                        Key = DbElementType.GetIndex(),
                        FhirPackageKey = dbStructure.FhirPackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        TypeName = typeName,
                        TypeStructureKey = null,
                        TypeProfile = null,
                        TypeProfileStructureKey = null,
                        TargetProfile = null,
                        TargetProfileStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add( et);

                    continue;
                }

                if (tr.ProfileElement.Count == 0)
                {
                    foreach (Canonical tp in tr.TargetProfile)
                    {
                        //string tl = literalForType(tr.cgName(), null, tp.Value);
                        DbElementType et = new()
                        {
                            Key = DbElementType.GetIndex(),
                            FhirPackageKey = dbStructure.FhirPackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            TypeName = typeName,
                            TypeStructureKey = null,
                            TypeProfile = null,
                            TypeProfileStructureKey = null,
                            TargetProfile = tp.Value,
                            TargetProfileStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        accumulator.targetProfiles.Add(tp.Value);
                    }

                    continue;
                }

                if (tr.TargetProfileElement.Count == 0)
                {
                    foreach (Canonical p in tr.Profile)
                    {
                        //string tl = literalForType(tr.cgName(), p.Value, null);
                        DbElementType et = new()
                        {
                            Key = DbElementType.GetIndex(),
                            FhirPackageKey = dbStructure.FhirPackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            TypeName = typeName,
                            TypeStructureKey = null,
                            TypeProfile = p.Value,
                            TypeProfileStructureKey = null,
                            TargetProfile = null,
                            TargetProfileStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        accumulator.typeProfiles.Add(p.Value);
                    }

                    continue;
                }

                foreach (Canonical p in tr.Profile)
                {
                    foreach (Canonical tp in tr.TargetProfile)
                    {
                        //string tl = literalForType(tr.cgName(), p.Value, tp.Value);
                        DbElementType et = new()
                        {
                            Key = DbElementType.GetIndex(),
                            FhirPackageKey = dbStructure.FhirPackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            TypeName = typeName,
                            TypeStructureKey = null,
                            TypeProfile = p.Value,
                            TypeProfileStructureKey = null,
                            TargetProfile = tp.Value,
                            TargetProfileStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        accumulator.targetProfiles.Add(tp.Value);
                        accumulator.typeProfiles.Add(p.Value);
                        //literalComponents.Add($"{p.Value}[{tp.Value}]");
                    }
                }
            }

            if (currentElementTypes.Count == 0)
            {
                string resolvedTypeName;

                if (ed.ElementId == sd.Id)
                {
                    resolvedTypeName = sd.Id;
                    //string tl = literalForType(sd.Id, null, null);
                    DbElementType et = new()
                    {
                        Key = DbElementType.GetIndex(),
                        FhirPackageKey = dbStructure.FhirPackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        TypeName = sd.Id,
                        TypeStructureKey = null,
                        TypeProfile = null,
                        TypeProfileStructureKey = null,
                        TargetProfile = null,
                        TargetProfileStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add(et);
                }
                else
                {
                    string btn = ed.cgBaseTypeName(dc, true);
                    resolvedTypeName = btn;
                    //string tl = literalForType(btn, null, null);

                    DbElementType et = new()
                    {
                        Key = DbElementType.GetIndex(),
                        FhirPackageKey = dbStructure.FhirPackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        TypeName = btn,
                        TypeStructureKey = null,
                        TypeProfile = null,
                        TypeProfileStructureKey = null,
                        TargetProfile = null,
                        TargetProfileStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add(et);
                }

                if (!literalAccumulator.TryGetValue(resolvedTypeName, out (List<string> targetProfiles, List<string> typeProfiles) accumulator))
                {
                    accumulator = ([], []);
                    literalAccumulator.Add(resolvedTypeName, accumulator);
                }
            }

            int additionalBindingCount = 0;

            // check for additional bindings
            if (ed.Binding?.Additional.Count > 0)
            {
                foreach (ElementDefinition.AdditionalComponent additional in ed.Binding.Additional)
                {
                    DbElementAdditionalBinding dbAdditionalBinding = new()
                    {
                        Key = DbElementAdditionalBinding.GetIndex(),
                        FhirPackageKey = pm.Key,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        FhirKey = null,     // TODO: R6 added additional.Key
                        Purpose = additional.Purpose,
                        BindingValueSet = additional.ValueSet,
                        BindingValueSetKey = string.IsNullOrEmpty(additional.ValueSet) ? null : DbValueSet.SelectSingle(_db, FhirPackageKey: pm.Key, UnversionedUrl: additional.ValueSet)?.Key,
                        Documentation = additional.Documentation.ProcessCoreTextForLinks(fhirVersionLiteral),
                        ShortDocumentation = additional.ShortDoco.ProcessCoreTextForLinks(fhirVersionLiteral),
                        CollatedUsageContexts = additional.Usage.Count == 0
                            ? null
                            : string.Join(", ", additional.Usage.Select(uc => uc.Code.System + "#" + uc.Code.Code + ": `" + uc.Value.ToString() + "`")),
                        SatisfiedBySingleRepetition = additional.Any,
                    };
                    dbAdditionalBindings.Add(dbAdditionalBinding);
                    additionalBindingCount++;
                }
            }

            HashSet<string> typeNames = [];
            bool isInherited = ed.cgIsInherited(sd);
            string basePath = ed.Base?.Path ?? ed.Path;

            List<string> completeLiteralComponents = [];
            foreach ((string typeName, (List<string> targetProfiles, List<string> typeProfiles)) in literalAccumulator)
            {
                typeNames.Add(typeName);
                string current = typeName;

                if (typeProfiles.Count != 0)
                {
                    current += "[" + string.Join(',', typeProfiles) + "]";
                }

                if (targetProfiles.Count != 0)
                {
                    current += "(" + string.Join(',', targetProfiles) + ")";
                }

                completeLiteralComponents.Add(current);
            }

            string fullTypeLiteral = string.Join(", ", completeLiteralComponents.Order());

            int resourceFieldOrder = ed.cgFieldOrder();
            int? parentElementDbKey = null;

            if (resourceFieldOrder != 0)
            {
                string parentKey = dbStructure.Key.ToString() + ":" + ed.ElementId.Substring(0, ed.ElementId.LastIndexOf('.'));
                if (dbElements.TryGetValue(parentKey, out DbElement? parentElement))
                {
                    parentElementDbKey = parentElement.Key;
                }
            }

            bool definedAsContentReference = !string.IsNullOrEmpty(ed.ContentReference);
            DbElement? crEd = null;
            if (definedAsContentReference)
            {
                if (ed.ContentReference.StartsWith('#') &&
                    dbElements.TryGetValue(ed.ContentReference[1..], out crEd))
                {
                    crEd.UsedAsContentReference = true;
                }
                else if (dbElements.TryGetValue(ed.ContentReference, out crEd))
                {
                    crEd.UsedAsContentReference = true;
                }
            }

            DbElement dbElement = new()
            {
                Key = elementKey,
                FhirPackageKey = pm.Key,
                StructureKey = dbStructure.Key,
                ParentElementKey = parentElementDbKey,
                ResourceFieldOrder = resourceFieldOrder,
                ComponentFieldOrder = ed.cgComponentFieldOrder(),
                Id = ed.ElementId,
                Path = ed.Path,
                ChildElementCount = childCount,
                Name = ed.cgName(),
                Short = ed.Short.ProcessCoreTextForLinks(fhirVersionLiteral),
                Definition = ed.Definition.ProcessCoreTextForLinks(fhirVersionLiteral),
                Comments = ed.Comment.ProcessCoreTextForLinks(fhirVersionLiteral),
                Requirements = ed.Requirements.ProcessCoreTextForLinks(fhirVersionLiteral),
                MinCardinality = ed.cgCardinalityMin(),
                MaxCardinality = ed.cgCardinalityMax(),
                MaxCardinalityString = ed.Max ?? "*",
                SliceName = ed.SliceName,
                ValueSetBindingStrength = ed.Binding?.Strength,
                BindingValueSet = ed.Binding?.ValueSet,
                BindingValueSetKey = bindingVsKey,
                BindingDescription = ed.Binding?.Description,
                AdditionalBindingCount = additionalBindingCount,
                FullCollatedTypeLiteral = fullTypeLiteral,
                DistinctTypeCount = typeNames.Count,
                DistinctTypeLiterals = string.Join(',', typeNames.Order()),
                IsInherited = isInherited,
                BasePath = basePath,
                BaseElementKey = null,
                BaseStructureKey = null,
                DefinedAsContentReference = !string.IsNullOrEmpty(ed.ContentReference),
                ContentReferenceSourceKey = crEd?.Key,
                ContentReferenceSourceId = crEd?.Id,
                UsedAsContentReference = null,
                IsSimpleType = ed.cgIsSimple(),
                IsChoiceType = typeNames.Count > 1,
                IsModifier = ed.IsModifier == true,
                IsModifierReason = ed.IsModifierReason,
                IsDeprecated = ed.cgStandardStatus()?.Equals("deprecated", StringComparison.OrdinalIgnoreCase) == true,
            };

            dbElements.Add(dbStructure.Key.ToString() + ":" + ed.ElementId, dbElement);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (_db != null)
                {
                    _db.Close();
                    _db.Dispose();
                }
            }

            _disposedValue = true;
        }
    }

    void IDisposable.Dispose()
    {
        // Do not vdRelationship this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
