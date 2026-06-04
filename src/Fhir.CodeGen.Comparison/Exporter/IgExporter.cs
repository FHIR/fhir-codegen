using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Fhir.CodeGen.Common.Extensions;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Language;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Octokit;
using static Fhir.CodeGen.Packages.Models.PackageIndex;

namespace Fhir.CodeGen.Comparison.Exporter;

public class IgExporter
{
    public class XVerExportTrackingRecord
    {
        public List<XVerIgExportTrackingRecord> XVerIgs { get; set; } = [];
        public List<ValidationIgExportTrackingRecord> ValidationIgs { get; set; } = [];
    }

    public record struct EdOutcomeMapTargetRecord(
        string sdCanonical, string elementPath);

    public class XVerIgExportTrackingRecord
    {
        public required FhirPackageComparisonPair PackagePair { get; set; }
        public required string PackageId { get; set; }
        public string PackageUrl =>
            $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{PackageId}";

        public string? IgRootDir { get; set; } = null;
        public string? InputDir { get; set; } = null;
        public string? IncludesDir { get; set; } = null;

        public XVerIgFileRecord? IgIndexFile { get; set; } = null;

        public string? PageContentDir { get; set; } = null;
        public List<XVerIgFileRecord> ResourceLookupFiles { get; set; } = [];
        public List<XVerIgFileRecord> TypeLookupFiles { get; set; } = [];
        public List<XVerIgFileRecord> VsPageContentFiles { get; set; } = [];
        public List<XVerIgFileRecord> XVerSourcePageContentFiles { get; set; } = [];

        public string? VocabularyDir { get; set; } = null;
        public List<XVerIgFileRecord> CodeSystemFiles { get; set; } = [];
        public List<XVerIgFileRecord> ValueSetFiles { get; set; } = [];
        public string? VocabMapDir { get; set; } = null;
        public List<XVerIgFileRecord> VsConceptMapFiles { get; set; } = [];

        public string? ExtensionDir { get; set; } = null;
        public List<XVerIgFileRecord> ExtensionFiles { get; set; } = [];

        public Dictionary<int, List<EdOutcomeMapTargetRecord>> EdOutcomeMapTargets { get; set;} = [];

        public string? ProfileDir { get; set; } = null;
        public List<XVerIgFileRecord> ProfileFiles { get; set; } = [];

        public string? ResourceMapDir { get; set; } = null;
        public List<XVerIgFileRecord> ResourceMapFiles { get; set; } = [];
        public List<XVerIgFileRecord> TypeMapFiles { get; set; } = [];
        public string? ElementMapDir { get; set; } = null;
        public List<XVerIgFileRecord> ElementMapFiles { get; set; } = [];

        private HashSet<string> _usedNames = [];

        public (bool changed, string name) GetName(string nameRequest, string id)
        {
            if (_usedNames.Add(nameRequest))
            {
                return (false, nameRequest);
            }

            string attempt = id.ToPascalCase();
            if (_usedNames.Add(attempt))
            {
                return (true, attempt);
            }

            for (int i = 2; i < 100; i++)
            {
                attempt = $"{nameRequest}_{i:000}";
                if (_usedNames.Add(attempt))
                {
                    return (true, attempt);
                }
            }

            throw new Exception($"Name: {nameRequest} has more than 100 uses!");
        }

        public PackageContents AsPackageContents()
        {
            if (IgIndexFile is null)
            {
                throw new Exception("IG Index file is required to create PackageContents.");
            }

            List<PackageContents.PackageFile> files = [
                IgIndexFile.AsPackageFile(),
                ];

            files.AddRange(VsPageContentFiles.Select(f => f.AsPackageFile()));

            files.AddRange(CodeSystemFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ValueSetFiles.Select(f => f.AsPackageFile()));
            files.AddRange(VsConceptMapFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ExtensionFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ProfileFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ResourceMapFiles.Select(f => f.AsPackageFile()));
            files.AddRange(TypeMapFiles.Select(f => f.AsPackageFile()));
            files.AddRange(ElementMapFiles.Select(f => f.AsPackageFile()));

            return new PackageContents()
            {
                IndexVersion = 2,
                Files = files,
            };
        }
    }

    public class ValidationIgExportTrackingRecord
    {
        public required DbFhirPackage Package { get; set; }
        public required string PackageId { get; set; }

        public string? IgRootDir { get; set; } = null;
        public string? InputDir { get; set; } = null;
        public string? IncludesDir { get; set; } = null;
        public string? PageContentDir { get; set; } = null;

        public XVerIgFileRecord? IgIndexFile { get; set; } = null;
        public List<XVerIgFileRecord> XVerSourcePageContentFiles { get; set; } = [];

        public PackageContents AsPackageContents()
        {
            if (IgIndexFile is null)
            {
                throw new Exception("IG Index file is required to create PackageContents.");
            }



            return new PackageContents()
            {
                IndexVersion = 2,
                Files = [
                    IgIndexFile.AsPackageFile(),
                    ..XVerSourcePageContentFiles.Select(f => f.AsPackageFile()) ],
            };
        }
    }

    public record class XVerIgFileRecord
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

    internal readonly record struct XverIgDependencyRec
    {
        [JsonPropertyName("packageId")]
        public required string PackageId { get; init; }

        [JsonPropertyName("packageVersion")]
        public required string PackageVersion { get; init; }

        [JsonPropertyName("canonicalUrl")]
        public required string CanonicalUrl { get; init; }

        [JsonPropertyName("versionSpecificPackages")]
        public required bool VersionSpecificPackages { get; init; }

        [JsonPropertyName("hasR4B")]
        public required bool HasR4B { get; init; }

        [JsonPropertyName("neededForPublisher")]
        public required bool NeededForPublisher { get; init; }

        public string AsYamlProp(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            if (!VersionSpecificPackages)
            {
                return $"{PackageId} : {PackageVersion}";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            return $"{PackageId}.{suffix} : {PackageVersion}";
        }

        public string AsSushiYaml(FhirReleases.FhirSequenceCodes fhirSequence, string levelIndent = "    ")
        {
            if (!VersionSpecificPackages)
            {
                if (!string.IsNullOrEmpty(CanonicalUrl))
                {
                    return $"""
                        {levelIndent}{PackageId}:
                        {levelIndent}{levelIndent}id: {PackageId.Replace('.', '_')}
                        {levelIndent}{levelIndent}uri: {CanonicalUrl}
                        {levelIndent}{levelIndent}version: {PackageVersion}
                        """;
                }

                return $"{levelIndent}{PackageId} : {PackageVersion}";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            if (!string.IsNullOrEmpty(CanonicalUrl))
            {
                return $"""
                    {levelIndent}{PackageId}.{suffix}:
                    {levelIndent}{levelIndent}id: {(PackageId.Replace('.', '_') + "_" + suffix)}
                    {levelIndent}{levelIndent}uri: {CanonicalUrl}
                    {levelIndent}{levelIndent}version: {PackageVersion}
                    """;
            }

            return $"{levelIndent}{PackageId}.{suffix} : {PackageVersion}";
        }

        public string AsJsonProp(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            if (!VersionSpecificPackages)
            {
                return $"\"{PackageId}\" : \"{PackageVersion}\"";
            }

            string suffix = (fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B)
                ? "r4"
                : fhirSequence.ToString().ToLowerInvariant();

            return $"\"{PackageId}.{suffix}\" : \"{PackageVersion}\"";
        }

        public string AsJsonIgDependency(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            string dotSuffix;
            if (!VersionSpecificPackages)
            {
                dotSuffix = string.Empty;
            }
            else if ((fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B))
            {
                dotSuffix = ".r4";
            }
            else
            {
                dotSuffix = "." + fhirSequence.ToString().ToLowerInvariant();
            }

            string packageId = PackageId + dotSuffix;
            string id = packageId.Replace('.', '_');

            return $$$"""{ "packageId":"{{{packageId}}}", "version":"{{{PackageVersion}}}", "uri":"{{{CanonicalUrl}}}", "id":"{{{id}}}" }""";
        }

        public ImplementationGuide.DependsOnComponent AsIgDependsOn(FhirReleases.FhirSequenceCodes fhirSequence)
        {
            string dotSuffix;
            if (!VersionSpecificPackages)
            {
                dotSuffix = string.Empty;
            }
            else if ((fhirSequence == FhirReleases.FhirSequenceCodes.R4B) && (!HasR4B))
            {
                dotSuffix = ".r4";
            }
            else
            {
                dotSuffix = "." + fhirSequence.ToString().ToLowerInvariant();
            }

            string packageId = PackageId + dotSuffix;
            string id = packageId.Replace('.', '_');

            return new ImplementationGuide.DependsOnComponent
            {
                ElementId = id,
                PackageId = packageId,
                Version = PackageVersion,
                Uri = CanonicalUrl,
            };
        }
    }

    private List<XverIgDependencyRec> _xverDependencies = [];

    private class IgParameterValue
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }
        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    internal class XverPackageExportConfig
    {
        [JsonPropertyName("packageVersion")]
        public string? PackageVersion { get; set; }

        [JsonPropertyName("template")]
        public string? Template { get; set; }

        [JsonPropertyName("dependencies")]
        public List<XverIgDependencyRec>? Dependencies { get; set; }
    }


    // codes described at: https://build.fhir.org/ig/FHIR/fhir-tools-ig/branches/master/CodeSystem-ig-parameters.html
    private List<IgParameterValue> _xverIgParameters = [];

    private XverPackageExportConfig? _xverPackageExportConfig = null;

    private readonly XVerExporter _exporter;

    private readonly IDbConnection _db;

    private HashSet<FhirReleases.FhirSequenceCodes> _allowedExportVersions = [];

    private ILoggerFactory _loggerFactory;
    private ILogger _logger;

    private List<DbFhirPackage> _packages = [];

    private string _outputPath;
    private string? _crossVersionSourcePath;

    private static Dictionary<string, string> _publisherScripts = [];
    private static Lock _publisherScriptsLock = new();

    public IgExporter(
        IDbConnection db,
        ILoggerFactory loggerFactory,
        string outputPath,
        string? crossVersionSourcePath,
        XVerExporter exporter,
        ConfigXVer config)

    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IgExporter>();

        _db = db;

        _crossVersionSourcePath = crossVersionSourcePath;
        _outputPath = outputPath;
        if (!Directory.Exists(_outputPath))
        {
            Directory.CreateDirectory(_outputPath);
        }

        _exporter = exporter;

        if (config.ExportR2)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.DSTU2);
        }

        if (config.ExportR3)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.STU3);
        }

        if (config.ExportR4)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.R4);
        }

        if (config.ExportR4B)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.R4B);
        }

        if (config.ExportR5)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.R5);
        }

        if (config.ExportR6)
        {
            _allowedExportVersions.Add(FhirReleases.FhirSequenceCodes.R6);
        }

        // load any support files
        loadIgParams();

        // load the export package config
        loadPackageExportConfig();

        // update our export version (if necessary)
        if (_xverPackageExportConfig?.PackageVersion is not null)
        {
            if (string.IsNullOrEmpty(config.XverArtifactVersion))
            {
                _exporter._crossDefinitionVersion = _xverPackageExportConfig.PackageVersion;
                _logger.LogInformation("Setting cross-version export version to {Version} based on package export config", _exporter._crossDefinitionVersion);
            }
        }

        // set dependencies
        if (_xverDependencies.Count == 0)
        {
            if (_xverPackageExportConfig?.Dependencies != null)
            {
                _xverDependencies = _xverPackageExportConfig.Dependencies;
            }
            else
            {
                _xverDependencies = [
                    new()
                    {
                        PackageId = "hl7.terminology",
                        PackageVersion = "7.1.0",
                        CanonicalUrl = "http://terminology.hl7.org/ImplementationGuide/hl7.terminology",                // "http://terminology.hl7.org"
                        VersionSpecificPackages = true,
                        HasR4B = false,
                        NeededForPublisher = false,
                    },
                    new()
                    {
                        PackageId = "hl7.fhir.uv.extensions",
                        PackageVersion = "5.3.0",
                        CanonicalUrl = "http://hl7.org/fhir/extensions/ImplementationGuide/hl7.fhir.uv.extensions",     // "http://hl7.org/fhir/extensions"
                        VersionSpecificPackages = true,
                        HasR4B = true,
                        NeededForPublisher = false,
                    },
                    new()
                    {
                        PackageId = "hl7.fhir.uv.tools",
                        PackageVersion = "1.1.2",
                        CanonicalUrl = "http://hl7.org/fhir/tools/ImplementationGuide/hl7.fhir.uv.tools",                // "http://hl7.org/fhir/tools"
                        VersionSpecificPackages = true,
                        HasR4B = false,
                        NeededForPublisher = false,
                    }
                ];
            }
        }
    }

    private void loadPackageExportConfig()
    {
        if (_crossVersionSourcePath is null)
        {
            return;
        }

        string filename = Path.Combine(_crossVersionSourcePath, "input", "ig-support", "xver-package-config.json");
        if (!File.Exists(filename))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(filename);
            var config = JsonSerializer.Deserialize<XverPackageExportConfig>(json);
            if (config is not null)
            {
                _xverPackageExportConfig = config;
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading package export config from {Filename}", filename);
        }
    }

    private void loadIgParams()
    {
        if (_crossVersionSourcePath is null)
        {
            return;
        }

        string filename = Path.Combine(_crossVersionSourcePath, "input", "ig-support", "igParameters.json");
        if (!File.Exists(filename))
        {
            return;
        }

        try
        {
            using FileStream jsonFs = new(filename, System.IO.FileMode.Open, FileAccess.Read);
            {
                List<IgParameterValue>? igParams = JsonSerializer.Deserialize<List<IgParameterValue>>(jsonFs);
                if ((igParams is not null) &&
                    (igParams.Count > 0))
                {
                    _xverIgParameters = igParams;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Attempt to load content from {filename} failed! {ex.Message}");
        }
    }



    public XVerExportTrackingRecord CreateInitialXVerIgs(
        bool includeScripts = true,
        int? maxStepSize = null,
        HashSet<(FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t)>? specificPairs = null)
    {
        XVerExportTrackingRecord tr = new();
        
        // get the list of packages
        _packages = DbFhirPackage.SelectList(_db, orderByProperties: [nameof(DbFhirPackage.PackageVersion)]);

        List<FhirPackageComparisonPair> packagePairs = [];
        maxStepSize ??= _packages.Count - 1;

        // we want to process closer versions first, so we do a stepped approach
        for (int stepSize = 1; stepSize <= maxStepSize; stepSize++)
        {
            for (int i = 0; i < _packages.Count - stepSize; i++)
            {
                DbFhirPackage sourcePackage = _packages[i];
                DbFhirPackage targetPackage = _packages[i + stepSize];

                if (_allowedExportVersions.Contains(targetPackage.DefinitionFhirSequence) &&
                    ((specificPairs is null) ||
                        specificPairs.Contains((sourcePackage.DefinitionFhirSequence, targetPackage.DefinitionFhirSequence))))
                {
                    packagePairs.Add(new(sourcePackage, targetPackage));
                }

                if (_allowedExportVersions.Contains(sourcePackage.DefinitionFhirSequence) &&
                    ((specificPairs is null) ||
                    specificPairs.Contains((targetPackage.DefinitionFhirSequence, sourcePackage.DefinitionFhirSequence))))
                {
                    packagePairs.Add(new(targetPackage, sourcePackage));
                }
            }
        }

        // iterate over our pairs in the order we built them
        foreach (FhirPackageComparisonPair packagePair in packagePairs)
        {
            tr.XVerIgs.Add(createInitialXVerIg(packagePair, includeScripts));
        }

        HashSet<FhirReleases.FhirSequenceCodes>? targetFhirVersions = null;
        if (specificPairs is not null)
        {
            targetFhirVersions = [];
            foreach ((FhirReleases.FhirSequenceCodes s, FhirReleases.FhirSequenceCodes t) in specificPairs)
            {
                if (_allowedExportVersions.Contains(t))
                {
                    targetFhirVersions.Add(t);
                }
            }
        }

        // iterate over our packages for validation Igs
        foreach (DbFhirPackage package in _packages)
        {
            if ((targetFhirVersions is not null) &&
                !targetFhirVersions.Contains(package.DefinitionFhirSequence))
            {
                continue;
            }

            if (!_allowedExportVersions.Contains(package.DefinitionFhirSequence))
            {
                continue;
            }

            tr.ValidationIgs.Add(createInitialValidationIg(package, includeScripts));
        }

        return tr;
    }

    public void FinalizeXVerIgs(XVerExportTrackingRecord tr)
    {
        // iterate over the component IGs
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            writeIgIni(igTr.IgRootDir!, igTr.PackageId);
            writeIgJson(igTr);
            writeMenuXml(igTr);
        }

        // iterate over the validation IGs
        foreach (ValidationIgExportTrackingRecord vTr in tr.ValidationIgs)
        {
            writeValidationExampleBundle(vTr);
            writeIgIni(vTr.IgRootDir!, vTr.PackageId);
            writeIgJson(vTr, tr);
            writeMenuXml(vTr);
        }
    }

    private void writeValidationExampleBundle(ValidationIgExportTrackingRecord vTr)
    {
        string dir = Path.Combine(vTr.InputDir!, "resources");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = $$$"""
            {
                "resourceType": "Bundle",
                "id": "bundle-placeholder",
                "type": "collection"
            }
            """;

        string filename = Path.Combine(dir, "bundle-placeholder.json");
        File.WriteAllText(filename, json);
    }

    private void writeMenuXml(ValidationIgExportTrackingRecord vTr)
    {
        string contents = $$$"""
            <ul xmlns="http://www.w3.org/1999/xhtml" class="nav navbar-nav">
              <li>
                <a href="toc.html">Contents</a>
              </li>
              <li>
                <a href="index.html">Home</a>
              </li>
              <li class="dropdown">
                <a data-toggle="dropdown" href="#" class="dropdown-toggle">Support
                  <b class="caret"></b>
                </a>
                <ul class="dropdown-menu">
                  <li>
                    <a href="downloads.html">Downloads</a>
                  </li>
                  <li>
                    <a href="changelog.html">Change Log</a>
                  </li>
                  <li>
                    <a href="https://confluence.hl7.org/spaces/FHIRI/pages/413256623/FAQs">FAQs</a>
                  </li>
                </ul>
              </li>
            </ul>
            """;

        string filename = Path.Combine(vTr.IncludesDir!, "menu.xml");
        File.WriteAllText(filename, contents);
    }

    private void writeMenuXml(XVerIgExportTrackingRecord igTr)
    {
        string contents = $$$"""
            <ul xmlns="http://www.w3.org/1999/xhtml" class="nav navbar-nav">
              <li>
                <a href="toc.html">Contents</a>
              </li>
              <li>
                <a href="index.html">Home</a>
              </li>
              <li>
                <a href="index-resources.html">Resource Lookup</a>
              </li>
              <li>
                <a href="index-types.html">Type Lookup</a>
              </li>
              <li>
                <a href="index-vs.html">ValueSet Lookup</a>
              </li>
              <li>
                <a href="artifacts.html">Artifacts</a>
              </li>
              <li class="dropdown">
                <a data-toggle="dropdown" href="#" class="dropdown-toggle">Support
                  <b class="caret"></b>
                </a>
                <ul class="dropdown-menu">
                  <li>
                    <a href="downloads.html">Downloads</a>
                  </li>
                  <li>
                    <a href="changelog.html">Change Log</a>
                  </li>
                  <li>
                    <a href="https://confluence.hl7.org/spaces/FHIRI/pages/413256623/FAQs">FAQs</a>
                  </li>
                </ul>
              </li>
            </ul>
            """;

        string filename = Path.Combine(igTr.IncludesDir!, "menu.xml");
        File.WriteAllText(filename, contents);
    }

    private void writeIgJson(ValidationIgExportTrackingRecord vTr, XVerExportTrackingRecord tr)
    {
        switch (vTr.Package.DefinitionFhirSequence)
        {
            case FhirReleases.FhirSequenceCodes.DSTU2:
            case FhirReleases.FhirSequenceCodes.STU3:
            case FhirReleases.FhirSequenceCodes.R4:
            case FhirReleases.FhirSequenceCodes.R4B:
                writeIgJsonR4(vTr, tr);
                break;

            case FhirReleases.FhirSequenceCodes.R5:
            case FhirReleases.FhirSequenceCodes.R6:
                writeIgJsonR5(vTr, tr);
                break;
        }
    }

    private void writeIgJsonR4(ValidationIgExportTrackingRecord vTr, XVerExportTrackingRecord tr)
    {
        HashSet<string> skipPages = [
            "index",
            "index-resources",
            "index-types",
            "index-vs",
            "downloads",
            "changelog",
        ];

        List<string> resourceDefinitions = [];
        resourceDefinitions.Add($$$"""
                {
                    "reference" : {
                        "reference" : "Bundle/bundle-placeholder"
                    },
                    "name" : "bundle-placeholder",
                    "description" : "Placeholder Bundle since an artifact is required to package."
                }
            """);

        StringBuilder pageBuilder = new();
        pageBuilder.AppendLine(""" "page" : """);
        pageBuilder.AppendLine("""  { "nameUrl" : "index.html", "title" : "Home", "generation" : "markdown" , "page" : [ """);
        //pageBuilder.AppendLine("""  { "nameUrl" : "faqs.html", "title" : "FAQs", "generation" : "markdown" },""");

        pageBuilder.AppendLine("""  { "nameUrl" : "downloads.html", "title" : "Downloads", "generation" : "markdown" },""");
        pageBuilder.AppendLine("""  { "nameUrl" : "changelog.html", "title" : "Change Log", "generation" : "markdown" }""");

        pageBuilder.AppendLine("""]},""");  // close index

        string igParams = string.Join(",\n", _xverIgParameters.Select(cv =>
            $$$"""    { "code" : "{{{cv.Code}}}", "value" : "{{{cv.Value}}}" }"""));

        List<string> deps = _xverDependencies
            .Where(d => d.NeededForPublisher)
            .Select(d => d.AsJsonIgDependency(vTr.Package.DefinitionFhirSequence))
            .ToList();

        // add the related xver packages as dependencies
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            if (igTr.PackagePair.TargetFhirSequence != vTr.Package.DefinitionFhirSequence)
            {
                continue;
            }

            deps.Add(
                $$$"""{ "packageId":"{{{igTr.PackageId}}}", "version":"{{{_exporter._crossDefinitionVersion}}}", "uri":"{{{igTr.PackageUrl}}}", "id":"{{{igTr.PackageId.Replace('.', '_')}}}" }""");
        }

        string dependencies = deps.Count == 0
            ? string.Empty
            : $$$"""
                "dependsOn" : [
                {{{string.Join(",\n", deps)}}}
                ],
                """;

        string igJson = $$$"""
            {
              "resourceType" : "ImplementationGuide",
              "id" : "{{{vTr.PackageId}}}",
              "extension" : [{
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                "valueCode" : "trial-use"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                "valueCode" : "fhir"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                "valueInteger" : 0
              }],
              "url" : "http://hl7.org/fhir/uv/xver/ImplementationGuide/{{{vTr.PackageId}}}",
              "version" : "{{{_exporter._crossDefinitionVersion}}}",
              "name" : "{{{FhirSanitizationUtils.ReformatIdForName(vTr.PackageId)}}}",
              "title" : "Validation package consolidating extensions for Using Data Elements in FHIR {{{vTr.Package.DefinitionFhirSequence}}}",
              "status" : "active",
              "date" : "{{{_exporter._runTime.ToString("O")}}}",
              "publisher" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
              "contact" : [{
                "name" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
                "telecom" : [{
                  "system" : "url",
                  "value" : "{{{CommonDefinitions.WorkgroupUrls["fhir"]}}}"
                }]
              }],
              "description" : "Cross Version Extensions to in FHIR {{{vTr.Package.DefinitionFhirSequence}}}",
              "jurisdiction" : [{
                "coding" : [{
                  "system" : "http://unstats.un.org/unsd/methods/m49/m49.htm",
                  "code" : "001",
                  "display" : "World"
                }]
              }],
              "packageId" : "{{{vTr.PackageId}}}",
              "license" : "{{{EnumUtility.GetLiteral(ImplementationGuide.SPDXLicense.CC01_0)}}}",
              "fhirVersion" : ["{{{vTr.Package.PackageVersion}}}"],
              {{{dependencies}}}
              "definition" : {
                {{{pageBuilder.ToString()}}}
                "resource" : [
                {{{string.Join("\n,  ", resourceDefinitions)}}}
                ],
                "parameter" : [
                {{{igParams}}}
                ]
              }
            }
            """;

        string filename = Path.Combine(vTr.InputDir!, $"ig-{vTr.PackageId}.json");
        File.WriteAllText(filename, igJson);
    }


    private void writeIgJsonR5(ValidationIgExportTrackingRecord vTr, XVerExportTrackingRecord tr)
    {
        List<ImplementationGuide.DependsOnComponent> deps = _xverDependencies
            .Where(d => d.NeededForPublisher)
            .Select(d => d.AsIgDependsOn(vTr.Package.DefinitionFhirSequence))
            .ToList();

        // add the related xver packages as dependencies
        foreach (XVerIgExportTrackingRecord igTr in tr.XVerIgs)
        {
            if (igTr.PackagePair.TargetFhirSequence != vTr.Package.DefinitionFhirSequence)
            {
                continue;
            }

            deps.Add(new()
            {
                PackageId = igTr.PackageId,
                Version = _exporter._crossDefinitionVersion,
                Uri = igTr.PackageUrl,
                ElementId = igTr.PackageId.Replace('.', '_'),
            });
        }

        HashSet<string> skipPages = [
            "index",
            "index-resources",
            "index-types",
            "index-vs",
            "downloads",
            "changelog",
        ];

        ImplementationGuide.PageComponent igPage = new()
        {
            Source = new FhirUrl("index.md"),
            Name = "index.html",
            Title = "Home",
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = [
                new()
                {
                    Source = new FhirUrl("downloads.md"),
                    Name = "downloads.html",
                    Title = "Downloads",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,

                },
                new()
                {
                    Source = new FhirUrl("changelog.md"),
                    Name = "changelog.html",
                    Title = "Change Log",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                }
            ],
        };

        List<ImplementationGuide.ParameterComponent> igParams = _xverIgParameters
            .Select(cv => new ImplementationGuide.ParameterComponent
            {
                Code = new Coding()
                {
                    System = "http://hl7.org/fhir/tools/CodeSystem/ig-parameters",
                    Code = cv.Code,
                },
                Value = cv.Value,
            })
            .ToList();


        ImplementationGuide ig = new()
        {
            Id = vTr.PackageId,
            Extension = [
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                    Value = new Code("trial-use"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                    Value = new Code("fhir"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                    Value = new Integer(0),
                }
            ],
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{vTr.PackageId}",
            Version = _exporter._crossDefinitionVersion,
            Name = FhirSanitizationUtils.ReformatIdForName(vTr.PackageId),
            Title = $"Validation package consolidating extensions for Using Data Elements in FHIR {vTr.Package.DefinitionFhirSequence}",
            Status = PublicationStatus.Active,
            Date = _exporter._runTime.ToString("O"),
            Publisher = CommonDefinitions.WorkgroupNames["fhir"],
            Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames["fhir"],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls["fhir"],
                        },
                    ],
                }
            ],
            Description = $"Cross Version Extensions to in FHIR {vTr.Package.DefinitionFhirSequence}",
            Jurisdiction = [
                new()
                {
                    Coding = [
                        new()
                        {
                            System = "http://unstats.un.org/unsd/methods/m49/m49.htm",
                            Code = "001",
                            Display = "World",
                        }
                    ],
                }
            ],
            PackageId = vTr.PackageId,
            License = ImplementationGuide.SPDXLicense.CC01_0,
            FhirVersion = [EnumUtility.ParseLiteral<FHIRVersion>(vTr.Package.PackageVersion) ?? FHIRVersion.N5_0_0],
            DependsOn = deps,
            Definition = new()
            {
                Resource = [
                    new()
                    {
                        Reference = new ResourceReference("Bundle/bundle-placeholder"),
                        Name = "bundle-placeholder",
                        Description = "Placeholder Bundle since an artifact is required to package.",
                    }
                    ],
                Page = igPage,
                Parameter = igParams,
            }
        };

        string filename = Path.Combine(vTr.InputDir!, $"ig-{vTr.PackageId}.json");
        File.WriteAllText(filename, ig.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
    }


    private void writeIgJson(XVerIgExportTrackingRecord igTr)
    {
        switch (igTr.PackagePair.TargetFhirSequence)
        {
            case FhirReleases.FhirSequenceCodes.DSTU2:
            case FhirReleases.FhirSequenceCodes.STU3:
            case FhirReleases.FhirSequenceCodes.R4:
            case FhirReleases.FhirSequenceCodes.R4B:
                writeIgJsonR4(igTr);
                break;

            case FhirReleases.FhirSequenceCodes.R5:
            case FhirReleases.FhirSequenceCodes.R6:
                writeIgJsonR5(igTr);
                break;
        }
    }

    private void writeIgJsonR4(XVerIgExportTrackingRecord igTr)
    {
        List<string> resourceDefinitions = [];

        // process extensions
        foreach (XVerIgFileRecord fileRec in igTr.ExtensionFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "StructureDefinition:extension"
                        }],
                        "reference" : {
                            "reference" : "StructureDefinition/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process profiles
        foreach (XVerIgFileRecord fileRec in igTr.ProfileFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "StructureDefinition:profile"
                        }],
                        "reference" : {
                            "reference" : "StructureDefinition/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process code systems
        foreach (XVerIgFileRecord fileRec in igTr.CodeSystemFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "CodeSystem"
                        }],
                        "reference" : {
                            "reference" : "CodeSystem/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process value sets
        foreach (XVerIgFileRecord fileRec in igTr.ValueSetFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ValueSet"
                        }],
                        "reference" : {
                            "reference" : "ValueSet/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process value set concept maps
        foreach (XVerIgFileRecord fileRec in igTr.VsConceptMapFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ConceptMap"
                        }],
                        "reference" : {
                            "reference" : "ConceptMap/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process resource concept maps
        foreach (XVerIgFileRecord fileRec in igTr.ResourceMapFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ConceptMap"
                        }],
                        "reference" : {
                            "reference" : "ConceptMap/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process type concept maps
        foreach (XVerIgFileRecord fileRec in igTr.TypeMapFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ConceptMap"
                        }],
                        "reference" : {
                            "reference" : "ConceptMap/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        // process element concept maps
        foreach (XVerIgFileRecord fileRec in igTr.ElementMapFiles)
        {
            resourceDefinitions.Add($$$"""
                    {
                        "extension" : [{
                            "url" : "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                            "valueString" : "ConceptMap"
                        }],
                        "reference" : {
                            "reference" : "ConceptMap/{{{fileRec.Id}}}"
                        },
                        "name" : "{{{fileRec.Name}}}",
                        "description" : "{{{FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description)}}}"
                    }
                """);
        }

        HashSet<string> skipPages = [
            "index",
            "index-resources",
            "index-types",
            "index-vs",
            "downloads",
            "changelog",
        ];

        StringBuilder pageBuilder = new();
        pageBuilder.AppendLine(""" "page" : """);
        pageBuilder.AppendLine("""  { "nameUrl" : "index.html", "title" : "Home", "generation" : "markdown" , "page" : [ """);
        //pageBuilder.AppendLine("""  { "nameUrl" : "faqs.html", "title" : "FAQs", "generation" : "markdown" },""");

        if (igTr.ResourceLookupFiles.Count == 1)
        {
            XVerIgFileRecord sdp = igTr.ResourceLookupFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{sdp.FileNameWithoutExtension}}}.html", "title" : "{{{sdp.Description}}}", "generation" : "markdown" }, """);
        }
        else if (igTr.ResourceLookupFiles.Count > 1)
        {
            XVerIgFileRecord sdp = igTr.ResourceLookupFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{sdp.FileNameWithoutExtension}}}.html", "title" : "{{{sdp.Description}}}", "generation" : "markdown" , "page" : [ """);

            foreach (XVerIgFileRecord fileRec in igTr.ResourceLookupFiles[1..^1])
            {
                pageBuilder.AppendLine(
                    $$$"""    { "nameUrl" : "{{{fileRec.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{fileRec.Name}}}", "generation" : "markdown" },""");
            }

            XVerIgFileRecord last = igTr.ResourceLookupFiles[^1];
            pageBuilder.AppendLine(
                $$$"""    { "nameUrl" : "{{{last.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{last.Name}}}", "generation" : "markdown" }""");

            pageBuilder.AppendLine("""]},""");  // close lookup
        }

        if (igTr.TypeLookupFiles.Count == 1)
        {
            XVerIgFileRecord tlp = igTr.TypeLookupFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{tlp.FileNameWithoutExtension}}}.html", "title" : "{{{tlp.Description}}}", "generation" : "markdown" }, """);
        }
        else if (igTr.TypeLookupFiles.Count > 1)
        {
            XVerIgFileRecord tlp = igTr.TypeLookupFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{tlp.FileNameWithoutExtension}}}.html", "title" : "{{{tlp.Description}}}", "generation" : "markdown" , "page" : [ """);

            foreach (XVerIgFileRecord fileRec in igTr.TypeLookupFiles[1..^1])
            {
                pageBuilder.AppendLine(
                    $$$"""    { "nameUrl" : "{{{fileRec.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{fileRec.Name}}}", "generation" : "markdown" },""");
            }

            XVerIgFileRecord last = igTr.TypeLookupFiles[^1];
            pageBuilder.AppendLine(
                $$$"""    { "nameUrl" : "{{{last.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{last.Name}}}", "generation" : "markdown" }""");

            pageBuilder.AppendLine("""]},""");  // close type lookup
        }

        if (igTr.VsPageContentFiles.Count == 1)
        {
            XVerIgFileRecord sdp = igTr.VsPageContentFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{sdp.FileNameWithoutExtension}}}.html", "title" : "{{{sdp.Description}}}", "generation" : "markdown" }, """);
        }
        else if (igTr.VsPageContentFiles.Count > 1)
        {
            XVerIgFileRecord vdp = igTr.VsPageContentFiles[0];
            pageBuilder.AppendLine(
                $$$"""  { "nameUrl" : "{{{vdp.FileNameWithoutExtension}}}.html", "title" : "{{{vdp.Description}}}", "generation" : "markdown" , "page" : [ """);

            foreach (XVerIgFileRecord fileRec in igTr.VsPageContentFiles[1..^1])
            {
                pageBuilder.AppendLine(
                    $$$"""    { "nameUrl" : "{{{fileRec.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{fileRec.Name}}}", "generation" : "markdown" },""");
            }

            XVerIgFileRecord last = igTr.VsPageContentFiles[^1];
            pageBuilder.AppendLine(
                $$$"""    { "nameUrl" : "{{{last.FileNameWithoutExtension}}}.html", "title" : "Lookup for {{{last.Name}}}", "generation" : "markdown" }""");

            pageBuilder.AppendLine("""]},""");  // close lookup
        }

        pageBuilder.AppendLine("""  { "nameUrl" : "downloads.html", "title" : "Downloads", "generation" : "markdown" },""");
        pageBuilder.AppendLine("""  { "nameUrl" : "changelog.html", "title" : "Change Log", "generation" : "markdown" }""");

        pageBuilder.AppendLine("""]},""");  // close index

        string igParams = string.Join(",\n", _xverIgParameters.Select(cv =>
            $$$"""    { "code" : "{{{cv.Code}}}", "value" : "{{{cv.Value}}}" }"""));

        List<string> deps = _xverDependencies
            .Where(d => d.NeededForPublisher)
            .Select(d => d.AsJsonIgDependency(igTr.PackagePair.TargetFhirSequence))
            .ToList();

        string dependencies = deps.Count == 0
            ? string.Empty
            : $$$"""
                "dependsOn" : [
                {{{string.Join(",\n", deps)}}}
                ],
                """;

        string igJson = $$$"""
            {
              "resourceType" : "ImplementationGuide",
              "id" : "{{{igTr.PackageId}}}",
              "extension" : [{
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                "valueCode" : "trial-use"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                "valueCode" : "fhir"
              },
              {
                "url" : "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                "valueInteger" : 0
              }],
              "url" : "http://hl7.org/fhir/uv/xver/ImplementationGuide/{{{igTr.PackageId}}}",
              "version" : "{{{_exporter._crossDefinitionVersion}}}",
              "name" : "{{{FhirSanitizationUtils.ReformatIdForName(igTr.PackageId)}}}",
              "title" : "Extensions for Using Data Elements from FHIR {{{igTr.PackagePair.SourceFhirSequence}}} in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}",
              "status" : "active",
              "date" : "{{{_exporter._runTime.ToString("O")}}}",
              "publisher" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
              "contact" : [{
                "name" : "{{{CommonDefinitions.WorkgroupNames["fhir"]}}}",
                "telecom" : [{
                  "system" : "url",
                  "value" : "{{{CommonDefinitions.WorkgroupUrls["fhir"]}}}"
                }]
              }],
              "description" : "Extensions for Using Data Elements from FHIR {{{igTr.PackagePair.SourceFhirSequence}}} in FHIR {{{igTr.PackagePair.TargetFhirSequence}}}",
              "jurisdiction" : [{
                "coding" : [{
                  "system" : "http://unstats.un.org/unsd/methods/m49/m49.htm",
                  "code" : "001",
                  "display" : "World"
                }]
              }],
              "packageId" : "{{{igTr.PackageId}}}",
              "license" : "{{{EnumUtility.GetLiteral(ImplementationGuide.SPDXLicense.CC01_0)}}}",
              "fhirVersion" : ["{{{igTr.PackagePair.TargetPackage.PackageVersion}}}"],
              {{{dependencies}}}
              "definition" : {
                {{{pageBuilder.ToString()}}}
                "resource" : [
                {{{string.Join("\n,  ", resourceDefinitions)}}}
                ],
                "parameter" : [
                {{{igParams}}}
                ]
              }
            }
            """;

        string filename = Path.Combine(igTr.InputDir!, $"ig-{igTr.PackageId}.json");
        File.WriteAllText(filename, igJson);
    }

    private void writeIgJsonR5(XVerIgExportTrackingRecord igTr)
    {
        List<ImplementationGuide.DependsOnComponent> deps = _xverDependencies
            .Where(d => d.NeededForPublisher)
            .Select(d => d.AsIgDependsOn(igTr.PackagePair.TargetFhirSequence))
            .ToList();

        if (igTr.ResourceLookupFiles.Count < 1)
        {
            throw new Exception($"No StructureDefinition page content files found for IG '{igTr.PackageId}'");
        }

        HashSet<string> skipPages = [
            "index",
            "index-resources",
            "index-types",
            "index-vs",
            "downloads",
            "changelog",
        ];

        XVerIgFileRecord sdLookupFileRec = igTr.ResourceLookupFiles[0];

        ImplementationGuide.PageComponent sdLookupPage = new()
        {
            Source = new FhirUrl(sdLookupFileRec.FileName),
            Name = sdLookupFileRec.FileNameWithoutExtension + ".html",
            Title = sdLookupFileRec.Description,
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = [],
        };

        foreach (XVerIgFileRecord fileRec in igTr.ResourceLookupFiles)
        {
            if (skipPages.Contains(fileRec.FileNameWithoutExtension))
            {
                continue;
            }

            sdLookupPage.Page.Add(new()
            {
                Source = new FhirUrl(fileRec.FileName),
                Name = $"{fileRec.FileNameWithoutExtension}.html",
                Title = $"Lookup for {fileRec.Name}",
                Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            });
        }

        // build the optional type lookup page tree (may be empty if no complex-type outcomes)
        ImplementationGuide.PageComponent? typeLookupPage = null;
        if (igTr.TypeLookupFiles.Count > 0)
        {
            XVerIgFileRecord typeLookupIndexFileRec = igTr.TypeLookupFiles[0];
            typeLookupPage = new()
            {
                Source = new FhirUrl(typeLookupIndexFileRec.FileName),
                Name = typeLookupIndexFileRec.FileNameWithoutExtension + ".html",
                Title = typeLookupIndexFileRec.Description,
                Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                Page = [],
            };

            foreach (XVerIgFileRecord fileRec in igTr.TypeLookupFiles)
            {
                if (skipPages.Contains(fileRec.FileNameWithoutExtension))
                {
                    continue;
                }

                typeLookupPage.Page.Add(new()
                {
                    Source = new FhirUrl(fileRec.FileName),
                    Name = $"{fileRec.FileNameWithoutExtension}.html",
                    Title = $"Lookup for {fileRec.Name}",
                    Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                });
            }
        }

        if (igTr.VsPageContentFiles.Count < 1)
        {
            throw new Exception($"No ValueSet page content files found for IG '{igTr.PackageId}'");
        }

        XVerIgFileRecord vsLookupFileRec = igTr.VsPageContentFiles[0];

        ImplementationGuide.PageComponent vsLookupPage = new()
        {
            Source = new FhirUrl(vsLookupFileRec.FileName),
            Name = vsLookupFileRec.FileNameWithoutExtension + ".html",
            Title = vsLookupFileRec.Description,
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = [],
        };

        foreach (XVerIgFileRecord fileRec in igTr.VsPageContentFiles)
        {
            if (skipPages.Contains(fileRec.FileNameWithoutExtension))
            {
                continue;
            }

            vsLookupPage.Page.Add(new()
            {
                Source = new FhirUrl(fileRec.FileName),
                Name = $"{fileRec.FileNameWithoutExtension}.html",
                Title = $"Lookup for {fileRec.Name}",
                Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            });
        }

        ImplementationGuide.PageComponent igPage = new()
        {
            Source = new FhirUrl("index.md"),
            Name = "index.html",
            Title = "Home",
            Generation = ImplementationGuide.GuidePageGeneration.Markdown,
            Page = typeLookupPage is null
                ? [
                    sdLookupPage,
                    vsLookupPage,
                    new()
                    {
                        Source = new FhirUrl("downloads.md"),
                        Name = "downloads.html",
                        Title = "Downloads",
                        Generation = ImplementationGuide.GuidePageGeneration.Markdown,

                    },
                    new()
                    {
                        Source = new FhirUrl("changelog.md"),
                        Name = "changelog.html",
                        Title = "Change Log",
                        Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                    }
                ]
                : [
                    sdLookupPage,
                    typeLookupPage,
                    vsLookupPage,
                    new()
                    {
                        Source = new FhirUrl("downloads.md"),
                        Name = "downloads.html",
                        Title = "Downloads",
                        Generation = ImplementationGuide.GuidePageGeneration.Markdown,

                    },
                    new()
                    {
                        Source = new FhirUrl("changelog.md"),
                        Name = "changelog.html",
                        Title = "Change Log",
                        Generation = ImplementationGuide.GuidePageGeneration.Markdown,
                    }
                ],
        };

        List<ImplementationGuide.ParameterComponent> igParams = _xverIgParameters
            .Select(cv => new ImplementationGuide.ParameterComponent
            {
                Code = new Coding()
                {
                    System = "http://hl7.org/fhir/tools/CodeSystem/ig-parameters",
                    Code = cv.Code,
                },
                Value = cv.Value,
            })
            .ToList();


        ImplementationGuide ig = new()
        {
            Id = igTr.PackageId,
            Extension = [
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-standards-status",
                    Value = new Code("trial-use"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-wg",
                    Value = new Code("fhir"),
                },
                new()
                {
                    Url = "http://hl7.org/fhir/StructureDefinition/structuredefinition-fmm",
                    Value = new Integer(0),
                }
            ],
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{igTr.PackageId}",
            Version = _exporter._crossDefinitionVersion,
            Name = FhirSanitizationUtils.ReformatIdForName(igTr.PackageId),
            Title = $"Extensions for Using Data Elements from FHIR {igTr.PackagePair.SourceFhirSequence} in FHIR {igTr.PackagePair.TargetFhirSequence}",
            Status = PublicationStatus.Active,
            Date = _exporter._runTime.ToString("O"),
            Publisher = CommonDefinitions.WorkgroupNames["fhir"],
            Contact = [
                new()
                {
                    Name = CommonDefinitions.WorkgroupNames["fhir"],
                    Telecom = [
                        new()
                        {
                            System = ContactPoint.ContactPointSystem.Url,
                            Value = CommonDefinitions.WorkgroupUrls["fhir"],
                        },
                    ],
                }
            ],
            Description = $"Cross Version Extensions to use FHIR {igTr.PackagePair.SourceFhirSequence} in FHIR {igTr.PackagePair.TargetFhirSequence}",
            Jurisdiction = [
                new()
                {
                    Coding = [
                        new()
                        {
                            System = "http://unstats.un.org/unsd/methods/m49/m49.htm",
                            Code = "001",
                            Display = "World",
                        }
                    ],
                }
            ],
            PackageId = igTr.PackageId,
            License = ImplementationGuide.SPDXLicense.CC01_0,
            FhirVersion = [EnumUtility.ParseLiteral<FHIRVersion>(igTr.PackagePair.TargetPackage.PackageVersion) ?? FHIRVersion.N5_0_0],
            DependsOn = deps,
            Definition = new()
            {
                Resource = [],
                Page = igPage,
                Parameter = igParams,
            }
        };


        // add our extensions
        foreach (XVerIgFileRecord fileRec in igTr.ExtensionFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"StructureDefinition/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("StructureDefinition:extension"),
                    },
                ],
            });
        }

        // add our profiles
        foreach (XVerIgFileRecord fileRec in igTr.ProfileFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"StructureDefinition/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("StructureDefinition:profile"),
                    },
                ],
            });
        }

        // add our code systems
        foreach (XVerIgFileRecord fileRec in igTr.CodeSystemFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"CodeSystem/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("CodeSystem"),
                    },
                ],
            });
        }

        // add our value sets
        foreach (XVerIgFileRecord fileRec in igTr.ValueSetFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ValueSet/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ValueSet"),
                    },
                ],
            });
        }

        // add our vs concept maps
        foreach (XVerIgFileRecord fileRec in igTr.VsConceptMapFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ConceptMap/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ConceptMap"),
                    },
                ],
            });
        }

        // add our resource concept maps
        foreach (XVerIgFileRecord fileRec in igTr.ResourceMapFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ConceptMap/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ConceptMap"),
                    },
                ],
            });
        }

        // add our type concept maps
        foreach (XVerIgFileRecord fileRec in igTr.TypeMapFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ConceptMap/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ConceptMap"),
                    },
                ],
            });
        }

        // add our element concept maps
        foreach (XVerIgFileRecord fileRec in igTr.ElementMapFiles)
        {
            ig.Definition.Resource.Add(new()
            {
                Reference = new ResourceReference($"ConceptMap/{fileRec.Id}"),
                Name = fileRec.Name,
                Description = FhirSanitizationUtils.SanitizeForJsonValue(fileRec.Description),
                Extension = [
                    new()
                    {
                        Url = "http://hl7.org/fhir/tools/StructureDefinition/resource-information",
                        Value = new FhirString("ConceptMap"),
                    },
                ],
            });
        }

        string filename = Path.Combine(igTr.InputDir!, $"ig-{igTr.PackageId}.json");
        File.WriteAllText(filename, ig.ToJson(new FhirJsonSerializationSettings() { Pretty = true }));
    }

    private void writeIgIni(string dir, string packageId)
    {
        string template = _xverPackageExportConfig?.Template ?? "hl7.fhir.template#current";

        string filename = Path.Combine(dir, "ig.ini");
        string contents = $$$"""
                    [IG]
                    ig = input/ig-{{{packageId}}}.json
                    template = {{{template}}}
                    """;
        File.WriteAllText(filename, contents);
    }

    private static string getXVerPackageId(FhirPackageComparisonPair pair) =>
        $"hl7.fhir.uv.xver-{pair.SourcePackageShortName.ToLowerInvariant()}.{pair.TargetPackageShortName.ToLowerInvariant()}";

    private static string getValidationPackageId(DbFhirPackage package) =>
        $"hl7.fhir.uv.xver.{package.ShortName.ToLowerInvariant()}";

    private XVerIgExportTrackingRecord createInitialXVerIg(
        FhirPackageComparisonPair packagePair,
        bool includeScripts = true)
    {
        string packageId = getXVerPackageId(packagePair);

        DbFhirPackage sourcePackage = packagePair.SourcePackage;
        DbFhirPackage targetPackage = packagePair.TargetPackage;

        XVerIgExportTrackingRecord igTr = new()
        {
            PackageId = packageId,
            PackagePair = packagePair,
        };

        igTr.IgIndexFile ??= new()
        {
            FileName = $"ImplementationGuide-{packageId}.json",
            FileNameWithoutExtension = $"ImplementationGuide-{packageId}",
            IsPageContentFile = false,
            Name = FhirSanitizationUtils.ReformatIdForName(packageId),
            Id = packageId,
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{packageId}",
            ResourceType = "ImplementationGuide",
            Version = _exporter._crossDefinitionVersion,
            Description = $"Extensions for Using Data Elements from FHIR {igTr.PackagePair.SourceFhirSequence} in FHIR {igTr.PackagePair.TargetFhirSequence}",
        };

        _logger.LogInformation($"Creating XVer IG Directories, output path: {_outputPath}...");

        createXVerIgDirectories(igTr);
        igTr.XVerSourcePageContentFiles = copyIgSourceContent(igTr.InputDir!, igTr.PageContentDir!);

        if (includeScripts &&
            (igTr.IgRootDir is not null))
        {
            writeScriptFiles(igTr.IgRootDir);
        }

        return igTr;
    }

    private ValidationIgExportTrackingRecord createInitialValidationIg(
        DbFhirPackage package,
        bool includeScripts = true)
    {
        string packageId = getValidationPackageId(package);

        ValidationIgExportTrackingRecord vTr = new()
        {
            PackageId = packageId,
            Package = package,
        };

        vTr.IgIndexFile ??= new()
        {
            FileName = $"ImplementationGuide-{packageId}.json",
            FileNameWithoutExtension = $"ImplementationGuide-{packageId}",
            IsPageContentFile = false,
            Name = FhirSanitizationUtils.ReformatIdForName(packageId),
            Id = packageId,
            Url = $"http://hl7.org/fhir/uv/xver/ImplementationGuide/{packageId}",
            ResourceType = "ImplementationGuide",
            Version = _exporter._crossDefinitionVersion,
            Description = $"Extensions for Using Data Elements from any FHIR version in FHIR {package.DefinitionFhirSequence}",
        };

        createValidationIgDirectories(vTr);
        vTr.XVerSourcePageContentFiles = copyIgSourceContent(vTr.InputDir!, vTr.PageContentDir!);

        if (includeScripts &&
            (vTr.IgRootDir is not null))
        {
            writeScriptFiles(vTr.IgRootDir);
        }

        return vTr;
    }

    private void writeScriptFiles(string igRootDir)
    {
        Dictionary<string, string> scriptFiles = getCurrentPublisherScripts();
        _logger.LogInformation($"Writing {scriptFiles.Comparer} files from GitHub repository to {igRootDir}");

        foreach ((string filePath, string fileContent) in scriptFiles)
        {
            try
            {
                string fullPath = Path.Combine(igRootDir, filePath);
                string? directory = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, fileContent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to write GitHub file: {filePath}");
            }
        }
    }

    /// <summary>
    /// Determines whether a file should be skipped when copying from the GitHub repository.
    /// </summary>
    /// <param name="filePath">The relative file path from the repository root.</param>
    /// <returns>True if the file should be skipped, false otherwise.</returns>
    private static bool shouldSkipScriptRepoFile(string filePath)
    {
        // Skip hidden files and directories
        if (filePath.StartsWith(".") || filePath.Contains("/."))
        {
            return true;
        }

        // skip repository README
        if (filePath.Equals("README.md", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Skip common binary file extensions
        string[] skipExtensions = { ".exe", ".dll", ".bin", ".jar", ".zip", ".tar", ".gz", ".7z",
                                   ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
                                   ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (skipExtensions.Contains(extension))
        {
            return true;
        }

        // Skip node_modules and other common directories
        string[] skipDirectories = { "node_modules/", ".git/", ".vs/", ".vscode/", "bin/", "obj/" };
        foreach (string skipDir in skipDirectories)
        {
            if (filePath.Contains(skipDir))
            {
                return true;
            }
        }

        // Skip very large files (> 1MB) - these are likely not useful for IG publishing
        // Note: We can't check file size here since we only have the path, 
        // but we'll handle this in the fetch method

        return false;
    }


    /// <summary>
    /// Recursively gets all contents from a GitHub repository.
    /// </summary>
    private List<RepositoryContent> getRepositoryContentsRecursive(
        GitHubClient client,
        string owner,
        string repo,
        string? path = null)
    {
        List<RepositoryContent> allContents = [];

        try
        {
            IReadOnlyList<RepositoryContent> contents = path == null
                ? client.Repository.Content.GetAllContents(owner, repo).Result
                : client.Repository.Content.GetAllContents(owner, repo, path).Result;

            foreach (var content in contents)
            {
                if (content.Type == ContentType.File)
                {
                    allContents.Add(content);
                }
                else if (content.Type == ContentType.Dir && !shouldSkipScriptRepoFile(content.Path))
                {
                    // Recursively get directory contents
                    List<RepositoryContent> subContents = getRepositoryContentsRecursive(client, owner, repo, content.Path);
                    allContents.AddRange(subContents);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get repository contents for path: {Path}", path);
        }

        return allContents;
    }

    /// <summary>
    /// Fetches all files from the HL7 ig-publisher-scripts GitHub repository.
    /// </summary>
    /// <returns>A dictionary where keys are relative file paths and values are file contents.</returns>
    private Dictionary<string, string> getCurrentPublisherScripts()
    {
        const string owner = "HL7";
        const string repo = "ig-publisher-scripts";

        lock (_publisherScriptsLock)
        {
            if (_publisherScripts.Count > 0)
            {
                _logger.LogInformation("Using cached publisher scripts from GitHub repository: {Owner}/{Repo}", owner, repo);
                return _publisherScripts;
            }

            try
            {
                _logger.LogInformation("Fetching files from GitHub repository: {Owner}/{Repo}", owner, repo);

                GitHubClient client = new(new ProductHeaderValue("fhir-codegen"));

                // Get the repository contents recursively
                List<RepositoryContent> contents = getRepositoryContentsRecursive(client, owner, repo);

                foreach (RepositoryContent content in contents)
                {
                    if (content.Type == ContentType.File && !shouldSkipScriptRepoFile(content.Path))
                    {
                        try
                        {
                            // Skip files larger than 1MB
                            if (content.Size > 1024 * 1024)
                            {
                                _logger.LogDebug("Skipping large file: {Path} ({Size} bytes)", content.Path, content.Size);
                                continue;
                            }

                            // Get the file content
                            byte[] fileContent = client.Repository.Content.GetRawContent(owner, repo, content.Path).Result;
                            string contentString = System.Text.Encoding.UTF8.GetString(fileContent);

                            _publisherScripts[content.Path] = contentString;
                            _logger.LogDebug("Fetched file: {Path}", content.Path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch file content for: {Path}", content.Path);
                        }
                    }
                    else if (content.Type == ContentType.File)
                    {
                        _logger.LogDebug("Skipping file: {Path}", content.Path);
                    }
                }

                _logger.LogInformation("Successfully fetched {Count} files from GitHub repository", _publisherScripts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch files from GitHub repository: {Owner}/{Repo}", owner, repo);
            }
        }

        return _publisherScripts;
    }

    internal static ExportStreamWriter createMarkdownWriter(
        string filename,
        string? headerText = null,
        bool includeGenerationTime = false)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                ExportStreamWriter writer = new(filename);

                if (headerText is not null)
                {
                    writer.WriteLine(headerText);

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

    private List<XVerIgFileRecord> copyIgSourceContent(string inputDir, string? pageContentDir)
    {
        // check for a source path we can copy from
        if (string.IsNullOrEmpty(_crossVersionSourcePath))
        {
            return [];
        }

        if (!Directory.Exists(_crossVersionSourcePath))
        {
            _logger.LogWarning($"Cross-version IG source path '{_crossVersionSourcePath}' does not exist; skipping copy of IG source content");
            return [];
        }

        string igSourceDir = Path.Combine(_crossVersionSourcePath, "input", "ig-source");
        if (!Directory.Exists(igSourceDir))
        {
            _logger.LogWarning($"Cross-version IG source content path '{igSourceDir}' does not exist; skipping copy of IG source content");
            return [];
        }

        if (string.IsNullOrEmpty(inputDir))
        {
            _logger.LogWarning($"IG input directory is not set; skipping copy of IG source content");
            return [];
        }

        // copy the contents of this directory into the target input directory
        recursiveCopyContents(igSourceDir, inputDir);

        if (string.IsNullOrEmpty(pageContentDir) ||
            !Directory.Exists(pageContentDir))
        {
            return [];
        }

        List<XVerIgFileRecord> exported = [];

        // discover files in the page content dir
        string[] files = Directory.GetFiles(pageContentDir, "*.*", SearchOption.TopDirectoryOnly);
        foreach (string filePath in files)
        {
            string fileName = Path.GetFileName(filePath);

            string fnNoExt = Path.GetFileNameWithoutExtension(fileName);

            string description = fnNoExt switch
            {
                "index" => "Home",
                //"faqs" => "FAQs",
                "toc" => "Contents",
                "downloads" => "Downlaods",
                "changelog" => "Change Log",
                _ => fnNoExt,
            };

            exported.Add(new()
            {
                FileName = fileName,
                FileNameWithoutExtension = fnNoExt,
                IsPageContentFile = true,
                Id = null,
                Name = FhirSanitizationUtils.ReformatIdForName(Path.GetFileNameWithoutExtension(fileName)),
                Url = null,
                ResourceType = null,
                Description = description,
            });
        }

        return exported;
    }

    private void recursiveCopyContents(string sourceDir, string targetDir)
    {
        // copy all files in this directory
        foreach (string sourceFilePath in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(sourceFilePath);
            string targetFilePath = Path.Combine(targetDir, fileName);
            File.Copy(sourceFilePath, targetFilePath, overwrite: true);
        }

        // recurse into sub-directories
        foreach (string sourceSubDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(sourceSubDir);
            string targetSubDir = Path.Combine(targetDir, dirName);
            if (!Directory.Exists(targetSubDir))
            {
                Directory.CreateDirectory(targetSubDir);
            }
            recursiveCopyContents(sourceSubDir, targetSubDir);
        }
    }

    private void createValidationIgDirectories(ValidationIgExportTrackingRecord vTr)
    {
        vTr.IgRootDir = Path.Combine(_outputPath, vTr.PackageId);
        if (!Directory.Exists(vTr.IgRootDir))
        {
            Directory.CreateDirectory(vTr.IgRootDir);
        }

        vTr.InputDir = Path.Combine(vTr.IgRootDir, "input");
        if (!Directory.Exists(vTr.InputDir))
        {
            Directory.CreateDirectory(vTr.InputDir);
        }

        vTr.IncludesDir = Path.Combine(vTr.InputDir, "includes");
        if (!Directory.Exists(vTr.IncludesDir))
        {
            Directory.CreateDirectory(vTr.IncludesDir);
        }

        vTr.PageContentDir = Path.Combine(vTr.InputDir, "pagecontent");
        if (!Directory.Exists(vTr.PageContentDir))
        {
            Directory.CreateDirectory(vTr.PageContentDir);
        }
    }

    private void createXVerIgDirectories(XVerIgExportTrackingRecord igTr)
    {
        igTr.IgRootDir = Path.Combine(_outputPath, igTr.PackageId);
        if (Directory.Exists(igTr.IgRootDir))
        {
            Directory.Delete(igTr.IgRootDir, recursive: true);
        }
        Directory.CreateDirectory(igTr.IgRootDir);

        igTr.InputDir = Path.Combine(igTr.IgRootDir, "input");
        if (!Directory.Exists(igTr.InputDir))
        {
            Directory.CreateDirectory(igTr.InputDir);
        }

        igTr.IncludesDir = Path.Combine(igTr.InputDir, "includes");
        if (!Directory.Exists(igTr.IncludesDir))
        {
            Directory.CreateDirectory(igTr.IncludesDir);
        }

        igTr.PageContentDir = Path.Combine(igTr.InputDir, "pagecontent");
        if (!Directory.Exists(igTr.PageContentDir))
        {
            Directory.CreateDirectory(igTr.PageContentDir);
        }

        igTr.VocabularyDir = Path.Combine(igTr.InputDir, "vocabulary");
        igTr.VocabMapDir = igTr.VocabularyDir;
        if (!Directory.Exists(igTr.VocabularyDir))
        {
            Directory.CreateDirectory(igTr.VocabularyDir);
        }

        igTr.ExtensionDir = Path.Combine(igTr.InputDir, "resources");
        igTr.ProfileDir = igTr.ExtensionDir;
        igTr.ResourceMapDir = igTr.ExtensionDir;
        igTr.ElementMapDir = igTr.ExtensionDir;
        if (!Directory.Exists(igTr.ExtensionDir))
        {
            Directory.CreateDirectory(igTr.ExtensionDir);
        }

    }
}
