using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.MappingLanguage;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using static Fhir.CodeGen.Comparison.CompareTool.FhirTypeMappings;

namespace Fhir.CodeGen.Comparison.CrossVersionSource;

public class MappingLoader : IDisposable
{
    private bool _disposedValue;

    private ILoggerFactory? _loggerFactory;
    private ILogger _logger;

    private IDbConnection _db;

    private string _fhirCrossVersionRepoPath = string.Empty;
    private PackageLoader? _loader = null;

    private List<DbFhirPackage> _packages = [];

    public MappingLoader(
        IDbConnection db,
        ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MappingLoader>()
            ?? LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MappingLoader>()
            ?? NullLoggerFactory.Instance.CreateLogger(nameof(MappingLoader));

        _db = db;
        _packages = DbFhirPackage.SelectList(_db, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);
    }

    public bool TryLoadCrossVersionSourceMaps(
        string sourcePath,
        bool useInternalTypeMaps)
    {
        if (string.IsNullOrEmpty(sourcePath) ||
            (!Directory.Exists(sourcePath)))
        {
            _logger.LogError($"Invalid map source path: {sourcePath}");
            return false;
        }

        _fhirCrossVersionRepoPath = Path.GetFullPath(sourcePath);

        // sanity check for db access
        if (_db is null)
        {
            _logger.LogError("Database connection is not initialized!");
            return false;
        }

        _loader ??= new(new()
        {
            AutoLoadExpansions = false,
            ResolvePackageDependencies = false,
        }, new()
        {
            JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson,
        });

        ImmutableArray<UnsafeCodeRule> unsafeCodeRules = UnsafeCodeConfigurationLoader.Load(_fhirCrossVersionRepoPath);

        // ensure our tables exist and are empty
        DbMappingClasses.DropTables(_db);
        DbMappingClasses.CreateTables(_db);
        UnsafeCodeInputStore.BeginInputs(_db, "source", _fhirCrossVersionRepoPath);
        UnsafeCodeInputStore.InsertRules(_db, unsafeCodeRules);

        // create our concept map loader
        ConceptMapLoader conceptMapLoader = new(
            this,
            _db,
            _loader,
            _packages,
            _loggerFactory);

        //// complex types need transitive mappings built, primitives are direct only
        //TransitiveMappingBuilder transitiveBuilder = new(_db, _loggerFactory, _packages);

        conceptMapLoader.LoadSourceMaps(sourcePath, "codes", "ConceptMap-*-*.json");
        //transitiveBuilder.BuildTransitiveValueSetMappings();

        // update missing intermediate keys for mappings (prior to adding missing concept mappings)
        //fillPriorStepsVs();

        // ensure every value set mapping has all of its concepts mapped
        //addMissingConceptMappings();

        // update missing intermediate keys for mappings (for newly added concept mappings)
        //fillPriorStepsVs();

        // TODO: determine when we should call this
        if (useInternalTypeMaps)
        {
            conceptMapLoader.LoadInternalTypeMaps(loadPrimitives: true, loadComplex: false);
            conceptMapLoader.LoadSourceMaps(sourcePath, "types", "ConceptMap-types-*.json");

            // complex types need transitive mappings built, primitives are direct only when using internal
            //transitiveBuilder.BuildTransitiveStructureMappings(FhirArtifactClassEnum.ComplexType);
        }
        else
        {
            conceptMapLoader.LoadSourceMaps(sourcePath, "types", "ConceptMap-types-*.json");

            //transitiveBuilder.BuildTransitiveStructureMappings(FhirArtifactClassEnum.PrimitiveType);
            //transitiveBuilder.BuildTransitiveStructureMappings(FhirArtifactClassEnum.ComplexType);
        }

        conceptMapLoader.LoadSourceMaps(sourcePath, "resources", "ConceptMap-resources-*.json");
        //transitiveBuilder.BuildTransitiveStructureMappings(FhirArtifactClassEnum.Resource);

        // reconcile records that have mappings and explicit no-map records
        //reconcileStructureNoMaps();

        conceptMapLoader.LoadSourceMaps(sourcePath, "elements", "ConceptMap-elements-*.json");

        FmlLoader fmlLoader = new(
            this,
            _db,
            _packages,
            _loggerFactory);
        fmlLoader.LoadSourceFml(sourcePath);

        // update missing intermediate keys for mappings
        //fillPriorStepsSd();

        // ensure every element mapping is present
        //addMissingElementMappings();

        // update missing intermediate keys for mappings (for newly added element mappings)
        //fillPriorStepsSd();

        // check for inverses of all mappings
        //checkInversions();

        UnsafeCodeInputStore.CompleteInputs(_db, _packages);
        return true;
    }



    internal enum SourceFileTypeCodes : int
    {
        ConceptMap = 0,
        FML,
    }

    internal int getOrCreateMappingSourceFileKey(
        string filename,
        SourceFileTypeCodes fileType,
        string url)
    {
        string sourceRelativeFilename = Path.GetRelativePath(_fhirCrossVersionRepoPath, filename);

        DbMappingSourceFile? existingRec = DbMappingSourceFile.SelectSingle(
            _db,
            Filename: sourceRelativeFilename);

        if (existingRec is not null)
        {
            return existingRec.Key;
        }

        DbMappingSourceFile newRec = new()
        {
            Key = DbMappingSourceFile.GetIndex(),
            Filename = Path.GetFileName(filename),
            SourceRelativeFilename = sourceRelativeFilename,
            IsConceptMap = (fileType == SourceFileTypeCodes.ConceptMap),
            IsFml = (fileType == SourceFileTypeCodes.FML),
            Url = url,
        };

        newRec.Insert(_db, insertPrimaryKey: true);
        return newRec.Key;
    }

    internal (FhirReleases.FhirSequenceCodes sourceVersion, FhirReleases.FhirSequenceCodes targetVersion) getSequenceFromFilename(string filename)
    {
        string fileOnly = Path.GetFileNameWithoutExtension(filename);
        string[] components = fileOnly.Split('-');

        int initialOffset = (components[0] == "ConceptMap") ? 1 : 0;

        if (!FhirReleases.TryGetSequence(components[initialOffset], out FhirReleases.FhirSequenceCodes sourceVersion))
        {
            throw new Exception($"Invalid source map filename: {filename} (cannot determine source version)!");
        }

        FhirReleases.FhirSequenceCodes targetVersion;

        for (int i = components.Length - 1; i >= 1; i--)
        {
            if (FhirReleases.TryGetSequence(components[i], out targetVersion))
            {
                return (sourceVersion, targetVersion);
            }
        }

        throw new Exception($"Invalid source map filename: {filename} (cannot determine target version)!");

    }

    internal (string sourceVersion, string targetVersion) getVersionsFromFilename(string filename)
    {
        string fileOnly = Path.GetFileNameWithoutExtension(filename);
        string versionPart = fileOnly.Substring(fileOnly.LastIndexOf('-') + 1);

        int toIndex = versionPart.IndexOf("to", StringComparison.OrdinalIgnoreCase);
        if (toIndex < 1)
        {
            throw new Exception($"Invalid source map filename: {filename}!");
        }

        string sourceVersion = versionPart.Substring(0, toIndex);
        string targetVersion = versionPart.Substring(toIndex + 2);

        return (sourceVersion, targetVersion);
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
