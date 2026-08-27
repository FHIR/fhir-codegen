using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Xml.Linq;
using Fhir.CodeGen.Lib.Packaging;
using Fhir.CodeGen.LangSQLite.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Microsoft.Data.Sqlite;
using Fhir.CodeGen.Lib.FhirExtensions;
using Fhir.CodeGen.Lib.Models;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Common.Utils;
using System.Text.Json;
using Fhir.CodeGen.Common.FhirExtensions;

namespace Fhir.CodeGen.Lib.Language.SQLite;

public class LangSQLite : ILanguage
{
    private const string _languageName = "SQLite";
    public string Name => _languageName;

    public Type ConfigType => typeof(ExportSQLiteOptions);

    /// <summary>Gets the FHIR primitive type map.</summary>
    public Dictionary<string, string> FhirPrimitiveTypeMap => [];

    public bool IsIdempotent => true;

    private ExportSQLiteOptions _options = null!;
    private bool _packageIsFhirCore = false;

    private IDbConnection _db = null!;

    internal static readonly HashSet<string> _exclusionSet = [
        "http://hl7.org/fhir/ValueSet/ucum-units",
        "http://hl7.org/fhir/ValueSet/all-languages",
        "http://tools.ietf.org/html/bcp47",             // DSTU2 version of all-languages
        "http://hl7.org/fhir/ValueSet/mimetypes",
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


    public void Export(object untypedConfig, DefinitionCollection dc)
    {
        if (untypedConfig is not ExportSQLiteOptions config)
        {
            throw new ArgumentException($"Expected config to be of type {nameof(ExportSQLiteOptions)}");
        }

        _options = config;

        string dbFilename = determineDatabaseFilename();

        // create our connection
        _db = new SqliteConnection($"Data Source={dbFilename};");

        // open our connection
        _db.Open();

        if (config.DropTables)
        {
            dropTables();
        }

        // ensure tables exist
        createTables();

        _packageIsFhirCore = FhirPackageUtils.PackageIsFhirCore(dc.MainPackageId);

        // check to see if we are replacing an existing package
        CgDbPackage? package = removeExistingPackageContent(dc);

        // update our index values so we can insert appropriately
        getCurrentIndexes();

        if (package == null)
        {
            string shortName = _packageIsFhirCore
                ? dc.FhirSequence.ToString()
                : FhirPackageUtils.GetShortName(dc.MainPackageId, dc.MainPackageVersion);

            List<string> deps = dc.Manifests.Keys
                .Where(key => !key.Id.StartsWith(dc.MainPackageId, StringComparison.OrdinalIgnoreCase))
                .Select(key => key.Id + "@" + key.Version)
                .ToList();

            if (!dc.TryGetManifest(dc.MainPackageId, dc.MainPackageVersion, out CodeGenPackageManifest? mainManifest))
            {
                throw new Exception($"Failed to get main package manifest: {dc.MainPackageId}@{dc.MainPackageVersion}");
            }

            string? webUrl = string.IsNullOrEmpty(mainManifest.WebPublicationUrl)
                ? null
                : mainManifest.WebPublicationUrl.EndsWith('/')
                    ? mainManifest.WebPublicationUrl
                    : mainManifest.WebPublicationUrl + "/";

            if (_packageIsFhirCore)
            {
                string coreUrl = "http://hl7.org/fhir/" + dc.FhirSequence.ToLiteral() + "/";
                if (webUrl != coreUrl)
                {
                    webUrl = coreUrl;
                }
            }

            // create a new database package record
            package = new()
            {
                Key = CgDbPackage.GetIndex(),
                Name = dc.Name,
                Title = mainManifest.Title,
                Description = mainManifest.Description,
                PackageId = dc.MainPackageId,
                PackageVersion = dc.MainPackageVersion,
                FhirVersionShort = dc.FhirSequence.ToShortVersion(),
                CanonicalUrl = dc.MainPackageCanonical,
                WebUrl = webUrl,
                ShortName = shortName,
                Dependencies = deps.Count > 0 ? string.Join(",", deps) : null,
                DefinitionFhirSequence = dc.FhirSequence,
                ProcessDate = DateTimeOffset.UtcNow,
            };

            // insert the package now for sanity
            package.Insert(_db);
        }

        // add code systems
        addCodeSystems(package, dc);

        // add value sets
        addValueSets(package, dc);

        // add structures and elements
        addStructures(package, dc);

        // add search parameters
        addSearchParameters(package, dc);

        // add operation definitions
        addOperations(package, dc);
    }

    private void getCurrentIndexes()
    {
        CgDbPackage.LoadMaxKey(_db);

        CgDbCodeSystem.LoadMaxKey(_db);
        CgDbCodeSystemConcept.LoadMaxKey(_db);
        CgDbCodeSystemConceptProperty.LoadMaxKey(_db);
        CgDbCodeSystemFilter.LoadMaxKey(_db);
        CgDbCodeSystemPropertyDefinition.LoadMaxKey(_db);

        CgDbValueSet.LoadMaxKey(_db);
        CgDbValueSetConcept.LoadMaxKey(_db);
        CgDbValueSetSystem.LoadMaxKey(_db);

        CgDbStructure.LoadMaxKey(_db);

        CgDbElement.LoadMaxKey(_db);
        CgDbElementAdditionalBinding.LoadMaxKey(_db);
        CgDbElementCollatedType.LoadMaxKey(_db);
        CgDbElementType.LoadMaxKey(_db);

        CgDbSearchParameter.LoadMaxKey(_db);
        CgDbSearchParameterComponent.LoadMaxKey(_db);

        CgDbOperation.LoadMaxKey(_db);
        CgDbOperationParameter.LoadMaxKey(_db);
    }

    private CgDbPackage? removeExistingPackageContent(DefinitionCollection dc)
    {
        CgDbPackage? existing = CgDbPackage.SelectSingle(
            _db,
            PackageId: dc.MainPackageId,
            PackageVersion: dc.MainPackageVersion);

        if (existing == null)
        {
            // no existing package, nothing to do
            return null;
        }

        // delete all related content from the package
        CgDbCodeSystem.Delete(_db, PackageKey: existing.Key);
        CgDbCodeSystemConcept.Delete(_db, PackageKey: existing.Key);
        CgDbCodeSystemConceptProperty.Delete(_db, PackageKey: existing.Key);
        CgDbCodeSystemFilter.Delete(_db, PackageKey: existing.Key);
        CgDbCodeSystemPropertyDefinition.Delete(_db, PackageKey: existing.Key);

        CgDbValueSet.Delete(_db, PackageKey: existing.Key);
        CgDbValueSetConcept.Delete(_db, PackageKey: existing.Key);
        CgDbValueSetSystem.Delete(_db, PackageKey: existing.Key);

        CgDbStructure.Delete(_db, PackageKey: existing.Key);

        CgDbElement.Delete(_db, PackageKey: existing.Key);
        CgDbElementAdditionalBinding.Delete(_db, PackageKey: existing.Key);
        CgDbElementCollatedType.Delete(_db, PackageKey: existing.Key);
        CgDbElementType.Delete(_db, PackageKey: existing.Key);

        CgDbSearchParameter.Delete(_db, PackageKey: existing.Key);
        CgDbSearchParameterComponent.Delete(_db, PackageKey: existing.Key);

        CgDbOperation.Delete(_db, PackageKey: existing.Key);
        CgDbOperationParameter.Delete(_db, PackageKey: existing.Key);

        // return the existing package so we can load contents in-place instead of creating a new one
        return existing;
    }

    private void dropTables()
    {
        CgDbPackage.DropTable(_db);

        CgDbCodeSystem.DropTable(_db);
        CgDbCodeSystemConcept.DropTable(_db);
        CgDbCodeSystemConceptProperty.DropTable(_db);
        CgDbCodeSystemFilter.DropTable(_db);
        CgDbCodeSystemPropertyDefinition.DropTable(_db);

        CgDbValueSet.DropTable(_db);
        CgDbValueSetConcept.DropTable(_db);
        CgDbValueSetSystem.DropTable(_db);

        CgDbStructure.DropTable(_db);

        CgDbElement.DropTable(_db);
        CgDbElementAdditionalBinding.DropTable(_db);
        CgDbElementCollatedType.DropTable(_db);
        CgDbElementType.DropTable(_db);

        CgDbSearchParameter.DropTable(_db);
        CgDbSearchParameterComponent.DropTable(_db);

        CgDbOperation.DropTable(_db);
        CgDbOperationParameter.DropTable(_db);
    }

    private void createTables()
    {
        CgDbPackage.CreateTable(_db);

        CgDbCodeSystem.CreateTable(_db);
        CgDbCodeSystemConcept.CreateTable(_db);
        CgDbCodeSystemConceptProperty.CreateTable(_db);
        CgDbCodeSystemFilter.CreateTable(_db);
        CgDbCodeSystemPropertyDefinition.CreateTable(_db);

        CgDbValueSet.CreateTable(_db);
        CgDbValueSetConcept.CreateTable(_db);
        CgDbValueSetSystem.CreateTable(_db);

        CgDbStructure.CreateTable(_db);

        CgDbElement.CreateTable(_db);
        CgDbElementAdditionalBinding.CreateTable(_db);
        CgDbElementCollatedType.CreateTable(_db);
        CgDbElementType.CreateTable(_db);

        CgDbSearchParameter.CreateTable(_db);
        CgDbSearchParameterComponent.CreateTable(_db);

        CgDbOperation.CreateTable(_db);
        CgDbOperationParameter.CreateTable(_db);
    }

    private string determineDatabaseFilename()
    {
        if (!string.IsNullOrEmpty(_options.OutputFilename))
        {
            return _options.OutputFilename;
        }

        if (!string.IsNullOrEmpty(_options.OutputDirectory))
        {
            return System.IO.Path.Combine(_options.OutputDirectory, "fhir.sqlite");
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, "fhir.sqlite");
    }

    private string? processTextForLinks(string? input, CgDbPackage package)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // Process special markdown links first, then HTML links, then core links.
        string? output = FhirSanitizationUtils.ProcessFhirSpecialMdLinks(input, package.DefinitionFhirSequence.ToLiteral());
        output = FhirSanitizationUtils.ProcessEmbeddedHtmlLinks(output);

        if (package.WebUrl != null)
        {
            output = FhirSanitizationUtils.ProcessRelativeMdLinks(output, package.WebUrl);
        }

        return output;
    }

    private Narrative? processTextForLinks(Narrative? input, CgDbPackage package)
    {
        if (string.IsNullOrEmpty(input?.Div))
        {
            return input;
        }

        // Process special markdown links first, then HTML links, then core links.
        string? output = FhirSanitizationUtils.ProcessFhirSpecialMdLinks(input.Div, package.DefinitionFhirSequence.ToLiteral());
        output = FhirSanitizationUtils.ProcessEmbeddedHtmlLinks(output);

        if (package.WebUrl != null)
        {
            output = FhirSanitizationUtils.ProcessRelativeMdLinks(output, package.WebUrl);
        }

        return new Narrative()
        {
            Status = input.Status,
            Div = output,
        };
    }

    private void addCodeSystems(
        CgDbPackage package,
        DefinitionCollection dc)
    {
        List<CgDbCodeSystem> dbCodeSystems = [];
        List<CgDbCodeSystemFilter> dbCodeSystemFilters = [];
        List<CgDbCodeSystemPropertyDefinition> dbCodeSystemPropertyDefinitions = [];
        List<CgDbCodeSystemConcept> allDbConcepts = [];
        List<CgDbCodeSystemConceptProperty> allDbConceptProperties = [];

        string fhirVersionLiteral = package.DefinitionFhirSequence.ToString();

        // iterate over the code systems in the definition collection
        foreach ((string codeSystemUrl, CodeSystem cs) in dc.CodeSystemsByUrl.OrderBy(kvp => kvp.Key))
        {
            CgDbCodeSystem? existingDbCs = CgDbCodeSystem.SelectSingle(
                _db,
                PackageKey: package.Key,
                UnversionedUrl: codeSystemUrl);

            // check to see if this code system already exists
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
                CgDbCodeSystem excludedDbCodeSystem = new()
                {
                    Key = CgDbCodeSystem.GetIndex(),
                    PackageKey = package.Key,
                    Id = cs.Id,
                    VersionedUrl = cs.Url + (string.IsNullOrEmpty(cs.Version) ? "" : "|" + cs.Version),
                    UnversionedUrl = cs.Url ?? codeSystemUrl,
                    SourcePackageMoniker = cs.cgPackageSourceAsMoniker(),
                    Name = cs.Name ?? cs.Id,
                    Version = cs.Version ?? package.PackageVersion,
                    VersionAlgorithmString = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is FhirString cseVaFs) ? cseVaFs.Value : null,
                    VersionAlgorithmCoding = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is Coding cseVaC) ? cseVaC : null,
                    Status = cs.Status,
                    Title = processTextForLinks(cs.Title, package),
                    Description = processTextForLinks(cs.Description, package),
                    Purpose = processTextForLinks(cs.Purpose, package),
                    Narrative = processTextForLinks(cs.Text, package),
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

            // create the database record
            CgDbCodeSystem dbCodeSystem = new()
            {
                Key = CgDbCodeSystem.GetIndex(),
                PackageKey = package.Key,
                Id = cs.Id,
                VersionedUrl = cs.Url + (string.IsNullOrEmpty(cs.Version) ? "" : "|" + cs.Version),
                UnversionedUrl = cs.Url ?? codeSystemUrl,
                SourcePackageMoniker = cs.cgPackageSourceAsMoniker(),
                Name = cs.Name ?? cs.Id,
                Version = cs.Version ?? package.PackageVersion,
                VersionAlgorithmString = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is FhirString csVaFs) ? csVaFs.Value : null,
                VersionAlgorithmCoding = (cs.VersionAlgorithm != null) && (cs.VersionAlgorithm is Coding csVaC) ? csVaC : null,
                Status = cs.Status,
                Title = processTextForLinks(cs.Title, package),
                Description = processTextForLinks(cs.Description, package),
                Purpose = processTextForLinks(cs.Purpose, package),
                Narrative = processTextForLinks(cs.Text, package),
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
                CgDbCodeSystemFilter dbFilter = new()
                {
                    Key = CgDbCodeSystemFilter.GetIndex(),
                    PackageKey = package.Key,
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
                CgDbCodeSystemPropertyDefinition dbPropertyDefinition = new()
                {
                    Key = CgDbCodeSystemPropertyDefinition.GetIndex(),
                    PackageKey = package.Key,
                    CodeSystemKey = dbCodeSystem.Key,
                    Code = property.Code,
                    Uri = property.Uri,
                    Description = property.Description,
                    Type = property.Type ?? Hl7.Fhir.Model.CodeSystem.PropertyType.Code,
                };

                dbCodeSystemPropertyDefinitions.Add(dbPropertyDefinition);
            }
            // add concepts to the list (handling hierarchy)
            List<CgDbCodeSystemConcept> conceptsForThisCodeSystem = [];
            List<CgDbCodeSystemConceptProperty> conceptPropertiesForThisCodeSystem = [];

            // create a lookup for property definitions by code for this code system
            ILookup<string, CgDbCodeSystemPropertyDefinition> propertyDefsByCode = dbCodeSystemPropertyDefinitions
                .Where(pd => pd.CodeSystemKey == dbCodeSystem.Key)
                .ToLookup(pd => pd.Code);

            int globalOrder = allDbConcepts.Count;
            processConceptHierarchy(
                cs.Concept,
                dbCodeSystem.Key,
                package,
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

        Console.WriteLine($"Inserting CodeSystems for {package.PackageId}@{package.PackageVersion} into database...");

        _db.Insert(dbCodeSystems);
        Console.WriteLine($" <<< added {dbCodeSystems.Count} CodeSystems");

        _db.Insert(dbCodeSystemFilters);
        Console.WriteLine($" <<< added {dbCodeSystemFilters.Count} CodeSystem Filters");

        _db.Insert(dbCodeSystemPropertyDefinitions);
        Console.WriteLine($" <<< added {dbCodeSystemPropertyDefinitions.Count} CodeSystem Property Definitions");

        _db.Insert(allDbConcepts);
        Console.WriteLine($" <<< added {allDbConcepts.Count} CodeSystem Concepts");

        _db.Insert(allDbConceptProperties);
        Console.WriteLine($" <<< added {allDbConceptProperties.Count} CodeSystem Concept Properties");

        return;
    }

    private void processConceptHierarchy(
        IList<CodeSystem.ConceptDefinitionComponent> concepts,
        int codeSystemKey,
        CgDbPackage package,
        int? parentConceptKey,
        int relativeOrder,
        ref int globalOrder,
        List<CgDbCodeSystemConcept> allConcepts,
        List<CgDbCodeSystemConceptProperty> allConceptProperties,
        ILookup<string, CgDbCodeSystemPropertyDefinition> propertyDefsByCode,
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
            CgDbCodeSystemConcept dbConcept = new()
            {
                Key = CgDbCodeSystemConcept.GetIndex(),
                PackageKey = package.Key,
                CodeSystemKey = codeSystemKey,
                FlatOrder = globalOrder++,
                RelativeOrder = relativeOrder,
                Code = concept.Code,
                Display = concept.Display,
                Definition = processTextForLinks(concept.Definition, package),
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
                CgDbCodeSystemPropertyDefinition? propertyDef = propertyDefsByCode[conceptProperty.Code].FirstOrDefault();
                if (propertyDef != null)
                {
                    CgDbCodeSystemConceptProperty dbConceptProperty = new()
                    {
                        Key = CgDbCodeSystemConceptProperty.GetIndex(),
                        PackageKey = package.Key,
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
                    package,
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

    private void addValueSets(
        CgDbPackage package,
        DefinitionCollection dc)
    {
        List<CgDbValueSet> dbValueSets = [];
        List<CgDbValueSetConcept> allDbConcepts = [];
        Dictionary<int, List<string>> valueSetCodeSystems = [];

        string fhirVersionLiteral = package.DefinitionFhirSequence.ToString();

        // iterate over the value sets in the definition collection
        foreach ((string unversionedUrl, string[] versions) in dc.ValueSetVersions.OrderBy(kvp => kvp.Key))
        {
            // only use the highest version in the package
            string vsVersion = versions.OrderDescending().First();
            string versionedUrl = unversionedUrl + "|" + vsVersion;

            CgDbValueSet? existingDbVs = CgDbValueSet.SelectSingle(
                _db,
                PackageKey: package.Key,
                UnversionedUrl: unversionedUrl,
                Version: vsVersion);

            // check to see if this value set already exists
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

            int vsKey = CgDbValueSet.GetIndex();

            valueSetCodeSystems[vsKey] = vs?.cgReferencedCodeSystems().Where(v => v is not null).ToList() ?? [];

            // will not further process value sets we know we will not process
            if (isExcluded ||
                !canExpand ||
                (vs == null))
            {
                // still add a metadata record
                CgDbValueSet vsmExcluded = new()
                {
                    Key = vsKey,
                    PackageKey = package.Key,
                    Id = uvs.Id,
                    VersionedUrl = versionedUrl,
                    UnversionedUrl = unversionedUrl,
                    SourcePackageMoniker = uvs.cgPackageSourceAsMoniker(),
                    Name = uvs.Name,
                    Version = vsVersion,
                    VersionAlgorithmString = (uvs.VersionAlgorithm != null) && (uvs.VersionAlgorithm is FhirString vsmVaFs) ? vsmVaFs.Value : null,
                    VersionAlgorithmCoding = (uvs.VersionAlgorithm != null) && (uvs.VersionAlgorithm is Coding vsmVaC) ? vsmVaC : null,
                    Status = uvs.Status,
                    Title = processTextForLinks(uvs.Title, package),
                    Description = processTextForLinks(uvs.Description, package),
                    Purpose = processTextForLinks(uvs.Purpose, package),
                    Narrative = processTextForLinks(uvs.Text, package),
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

            CgDbValueSet dbVs = new()
            {
                Key = vsKey,
                PackageKey = package.Key,
                Id = vs.Id,
                VersionedUrl = versionedUrl,
                UnversionedUrl = unversionedUrl,
                SourcePackageMoniker = vs.cgPackageSourceAsMoniker(),
                Name = vs.Name,
                Version = vsVersion,
                VersionAlgorithmString = (vs.VersionAlgorithm != null) && (vs.VersionAlgorithm is FhirString vsVaFs) ? vsVaFs.Value : null,
                VersionAlgorithmCoding = (vs.VersionAlgorithm != null) && (vs.VersionAlgorithm is Coding vsVaC) ? vsVaC : null,
                Status = vs.Status,
                Title = processTextForLinks(vs.Title, package),
                Description = processTextForLinks(vs.Description, package),
                Purpose = processTextForLinks(vs.Purpose, package),
                Narrative = processTextForLinks(vs.Text, package),
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

            List<CgDbValueSetConcept> dbConcepts = [];
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

                // check for this record already existing
                if (CgDbValueSetConcept.SelectSingle(_db, PackageKey: package.Key, ValueSetKey: dbVs.Key, System: fc.System, Code: fc.Code) != null)
                {
                    continue;
                }

                // create a new content record
                CgDbValueSetConcept dbConcept = new()
                {
                    Key = CgDbValueSetConcept.GetIndex(),
                    PackageKey = package.Key,
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

        Console.WriteLine($"Inserting ValueSets for {package.PackageId}@{package.PackageVersion} into database...");

        _db.Insert(dbValueSets);
        Console.WriteLine($" <<< added {dbValueSets.Count} ValueSets");

        _db.Insert(allDbConcepts);
        Console.WriteLine($" <<< added {allDbConcepts.Count} ValueSet Concepts");

        List<CgDbValueSetSystem> vsCodeSystems = [];

        // process the added value sets to resolve the code systems
        foreach (CgDbValueSet vs in dbValueSets)
        {
            if (!valueSetCodeSystems.TryGetValue(vs.Key, out List<string>? referencedCodeSystems))
            {
                continue;
            }

            // iterate over the code systems
            foreach (string csLit in referencedCodeSystems)
            {
                string unversionedUrl;

                int versionLoc = csLit.LastIndexOf('|');
                if (versionLoc == -1)
                {
                    unversionedUrl = csLit;
                }
                else
                {
                    unversionedUrl = csLit[0..versionLoc];
                }

                // try to resolve this code system record
                CgDbCodeSystem? cs = CgDbCodeSystem.SelectSingle(
                    _db,
                    PackageKey: vs.PackageKey,
                    UnversionedUrl: unversionedUrl);
                if (cs is null)
                {
                    continue;
                }

                CgDbValueSetSystem vsCs = new()
                {
                    Key = CgDbValueSetSystem.GetIndex(),
                    PackageKey = vs.PackageKey,
                    ValueSetKey = vs.Key,
                    CodeSystemKey = cs.Key,
                    System = cs.UnversionedUrl,
                    Version = cs.Version,
                };

                vsCodeSystems.Add(vsCs);
            }
        }

        _db.Insert(vsCodeSystems);
        Console.WriteLine($" <<< added {vsCodeSystems.Count} ValueSet Code Systems");

        return;
    }


    private void addStructures(
        CgDbPackage package,
        DefinitionCollection dc)
    {
        Dictionary<string, CgDbStructure> dbStructures = [];
        Dictionary<string, CgDbElement> dbElements = [];
        List<CgDbElementAdditionalBinding> dbAdditionalBindings = [];
        List<CgDbElementCollatedType> dbCollatedTypes = [];
        List<CgDbElementType> dbElementTypes = [];

        string fhirVersionLiteral = package.DefinitionFhirSequence.ToString();

        // iterate over the types of structures
        foreach ((IEnumerable<StructureDefinition> structures, FhirArtifactClassEnum cgClass) in getStructures(dc))
        {
            foreach (StructureDefinition sd in structures)
            {
                string? sdImplements = sd.cgImplementsJoined();
                string? sdBase = sd.BaseDefinition;
                string? sdBaseShort = sdBase?.Split('/').LastOrDefault();

                // will not further process value sets we know we will not process
                if (_exclusionSet.Contains(sd.Url))
                {
                    // still add a metadata record
                    CgDbStructure sdmExcluded = new()
                    {
                        Key = CgDbStructure.GetIndex(),
                        PackageKey = package.Key,
                        Id = sd.Id,
                        VersionedUrl = sd.Url + "|" + sd.Version,
                        UnversionedUrl = sd.Url,
                        SourcePackageMoniker = sd.cgPackageSourceAsMoniker(),
                        Name = FhirSanitizationUtils.SanitizeForProperty(sd.Name, replacements: []),
                        Version = sd.Version,
                        VersionAlgorithmString = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is FhirString sdeVaFs) ? sdeVaFs.Value : null,
                        VersionAlgorithmCoding = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is Coding sdeVaC) ? sdeVaC : null,
                        Status = sd.Status,
                        Title = processTextForLinks(sd.Title ?? sd.Snapshot?.Element.FirstOrDefault()?.Short, package),
                        Description = processTextForLinks(sd.Description ?? sd.Snapshot?.Element.FirstOrDefault()?.Definition, package),
                        Purpose = processTextForLinks(sd.Purpose, package),
                        Narrative = processTextForLinks(sd.Text, package),
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
                        Comment = processTextForLinks(sd.Snapshot?.Element.FirstOrDefault()?.Comment, package),
                        ArtifactClass = cgClass,
                        Message = "Manually excluded",
                        SnapshotCount = sd.Snapshot?.Element.Count ?? 0,
                        DifferentialCount = sd.Differential?.Element.Count ?? 0,
                        Implements = sdImplements,
                        Kind = sd.Kind is null ? null : EnumUtility.GetLiteral(sd.Kind),
                        IsAbstract = sd.Abstract,
                        FhirType = sd.Type,
                        BaseDefinition = sdBase,
                        BaseDefinitionShort = sdBaseShort,
                        Derivation = sd.Derivation is null ? null : EnumUtility.GetLiteral(sd.Derivation),
                    };

                    dbStructures.Add(sd.Id, sdmExcluded);

                    continue;
                }

                // create a new metadata record
                CgDbStructure dbStructure = new()
                {
                    Key = CgDbStructure.GetIndex(),
                    PackageKey = package.Key,
                    Id = sd.Id,
                    VersionedUrl = sd.Url + "|" + sd.Version,
                    UnversionedUrl = sd.Url,
                    SourcePackageMoniker = sd.cgPackageSourceAsMoniker(),
                    Name = sd.Name,
                    Version = sd.Version,
                    VersionAlgorithmString = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is FhirString sdVaFs) ? sdVaFs.Value : null,
                    VersionAlgorithmCoding = (sd.VersionAlgorithm != null) && (sd.VersionAlgorithm is Coding sdVaC) ? sdVaC : null,
                    Status = sd.Status,
                    Title = processTextForLinks(sd.Title ?? sd.Snapshot?.Element.FirstOrDefault()?.Short, package),
                    Description = processTextForLinks(sd.Description ?? sd.Snapshot?.Element.FirstOrDefault()?.Definition, package),
                    Purpose = processTextForLinks(sd.Purpose, package),
                    Narrative = processTextForLinks(sd.Text, package),
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
                    Comment = processTextForLinks(sd.Snapshot?.Element.FirstOrDefault()?.Comment, package),
                    ArtifactClass = cgClass,
                    Message = string.Empty,
                    SnapshotCount = sd.Snapshot?.Element.Count ?? 0,
                    DifferentialCount = sd.Differential?.Element.Count ?? 0,
                    Implements = sdImplements,
                    Kind = sd.Kind is null ? null : EnumUtility.GetLiteral(sd.Kind),
                    IsAbstract = sd.Abstract,
                    FhirType = sd.Type,
                    BaseDefinition = sdBase,
                    BaseDefinitionShort = sdBaseShort,
                    Derivation = sd.Derivation is null ? null : EnumUtility.GetLiteral(sd.Derivation),
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
        Console.WriteLine($"Inserting Structures for {package.PackageId}@{package.PackageVersion} into database...");

        _db.Insert(dbStructures.Values);
        Console.WriteLine($" <<< added {dbStructures.Count} Structures");

        _db.Insert(dbElements.Values);
        Console.WriteLine($" <<< added {dbElements.Count} Elements");

        _db.Insert(dbCollatedTypes);
        Console.WriteLine($" <<< added {dbCollatedTypes.Count} Collated Element Types");

        _db.Insert(dbElementTypes);
        Console.WriteLine($" <<< added {dbElementTypes.Count} Discrete Element Types");

        _db.Insert(dbAdditionalBindings);
        Console.WriteLine($" <<< added {dbAdditionalBindings.Count} Additional Bindings");

        int affectedRows;

        // after all the records are inserted, execute any remaining key-resolution queries
        affectedRows = updateCollatedTypeStructureKeys(package.Key);
        Console.WriteLine($" <<< updated {affectedRows} Collated Type Structure Keys");

        affectedRows = updateElementTypeStructureKeys(package.Key);
        Console.WriteLine($" <<< updated {affectedRows} Element Type Structure Keys");

        affectedRows = updateElementBaseKeys(package.Key);
        Console.WriteLine($" <<< updated {affectedRows} Element Base Keys");

        return;

        // TODO(ginoc): For now, exclude extensions, profiles, and logical models - we will want them for generic packages, but do not care for core
        (IEnumerable<StructureDefinition> structures, FhirArtifactClassEnum cgClass)[] getStructures(DefinitionCollection dc) =>
            _options.IncludeExtendedStructures
            ? [ (dc.PrimitiveTypesByName.Values, FhirArtifactClassEnum.PrimitiveType),
                (dc.ComplexTypesByName.Values, FhirArtifactClassEnum.ComplexType),
                (dc.ResourcesByName.Values, FhirArtifactClassEnum.Resource),
                (dc.InterfacesByName.Values, FhirArtifactClassEnum.Interface),
                (dc.ExtensionsByUrl.Values, FhirArtifactClassEnum.Extension),
                (dc.ProfilesByUrl.Values, FhirArtifactClassEnum.Profile),
                //(dc.LogicalModelsByUrl.Values, FhirArtifactClassEnum.LogicalModel),
                ]
            : [ (dc.PrimitiveTypesByName.Values, FhirArtifactClassEnum.PrimitiveType),
                (dc.ComplexTypesByName.Values, FhirArtifactClassEnum.ComplexType),
                (dc.ResourcesByName.Values, FhirArtifactClassEnum.Resource),
                ];

        string literalForType(string? typeName, string? typeProfile, string? targetProfile) =>
            (string.IsNullOrEmpty(typeName) ? string.Empty : typeName) +
            (string.IsNullOrEmpty(typeProfile) ? string.Empty : $"[{typeProfile}]") +
            (string.IsNullOrEmpty(targetProfile) ? string.Empty : $"({targetProfile})");

        void addElementAndTypes(
            CgDbStructure dbStructure,
            StructureDefinition sd,
            ElementDefinition ed)
        {
            int elementKey = CgDbElement.GetIndex();

            // check for children
            int childCount = sd.cgElements(
                ed.Path,
                topLevelOnly: true,
                includeRoot: false,
                skipSlices: true).Count();

            Dictionary<string, CgDbElementCollatedType> currentCollatedTypes = [];
            List<CgDbElementType> currentElementTypes = [];
            Dictionary<string, List<string>> literalAccumulator = [];

            int? bindingVsKey = ed.Binding?.ValueSet == null
                ? null
                : (CgDbValueSet.SelectSingle(_db, PackageKey: package.Key, UnversionedUrl: ed.Binding?.ValueSet)?.Key
                  ?? CgDbValueSet.SelectSingle(_db, PackageKey: package.Key, VersionedUrl: ed.Binding?.ValueSet)?.Key);

            IEnumerable<ElementDefinition.TypeRefComponent> definedTypes = ed.Type.Select(tr => tr.cgAsR5());
            foreach (ElementDefinition.TypeRefComponent tr in definedTypes)
            {
                string typeName = tr.cgName();

                if (!currentCollatedTypes.TryGetValue(typeName, out CgDbElementCollatedType? collatedType))
                {
                    collatedType = new()
                    {
                        Key = CgDbElementCollatedType.GetIndex(),
                        PackageKey = dbStructure.PackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        TypeName = typeName,
                        CollatedLiteral = string.Empty,
                        TypeStructureKey = null,
                    };
                    currentCollatedTypes.Add(typeName, collatedType);
                    dbCollatedTypes.Add(collatedType);
                }
                if (!literalAccumulator.TryGetValue(collatedType.TypeName, out List<string>? literalComponents))
                {
                    literalComponents = [];
                    literalAccumulator.Add(collatedType.TypeName, literalComponents);
                }

                if ((tr.ProfileElement.Count == 0) &&
                    (tr.TargetProfileElement.Count == 0))
                {
                    string tl = literalForType(tr.cgName(), null, null);

                    CgDbElementType et = new()
                    {
                        Key = CgDbElementType.GetIndex(),
                        PackageKey = dbStructure.PackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        CollatedTypeKey = collatedType.Key,
                        TypeName = typeName,
                        TypeProfile = null,
                        TargetProfile = null,
                        TypeStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add(et);

                    continue;
                }

                if (tr.ProfileElement.Count == 0)
                {
                    foreach (Canonical tp in tr.TargetProfile)
                    {
                        string tl = literalForType(tr.cgName(), null, tp.Value);
                        CgDbElementType et = new()
                        {
                            Key = CgDbElementType.GetIndex(),
                            PackageKey = dbStructure.PackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            CollatedTypeKey = collatedType.Key,
                            TypeName = typeName,
                            TypeProfile = null,
                            TargetProfile = tp.Value,
                            TypeStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        literalComponents.Add(tp.Value);
                    }

                    continue;
                }

                if (tr.TargetProfileElement.Count == 0)
                {
                    foreach (Canonical p in tr.Profile)
                    {
                        string tl = literalForType(tr.cgName(), p.Value, null);
                        CgDbElementType et = new()
                        {
                            Key = CgDbElementType.GetIndex(),
                            PackageKey = dbStructure.PackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            CollatedTypeKey = collatedType.Key,
                            TypeName = typeName,
                            TypeProfile = p.Value,
                            TargetProfile = null,
                            TypeStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        literalComponents.Add(p.Value);
                    }

                    continue;
                }

                foreach (Canonical p in tr.Profile)
                {
                    foreach (Canonical tp in tr.TargetProfile)
                    {
                        string tl = literalForType(tr.cgName(), p.Value, tp.Value);
                        CgDbElementType et = new()
                        {
                            Key = CgDbElementType.GetIndex(),
                            PackageKey = dbStructure.PackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            CollatedTypeKey = collatedType.Key,
                            TypeName = typeName,
                            TypeProfile = p.Value,
                            TargetProfile = tp.Value,
                            TypeStructureKey = null,
                        };
                        currentElementTypes.Add(et);
                        dbElementTypes.Add(et);
                        literalComponents.Add($"{p.Value}[{tp.Value}]");
                    }
                }
            }

            if (currentElementTypes.Count == 0)
            {
                if (ed.ElementId == sd.Id)
                {
                    string tl = literalForType(sd.Id, null, null);
                    if (!currentCollatedTypes.TryGetValue(tl, out CgDbElementCollatedType? collatedType))
                    {
                        collatedType = new()
                        {
                            Key = CgDbElementCollatedType.GetIndex(),
                            PackageKey = dbStructure.PackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            TypeName = tl,
                            CollatedLiteral = string.Empty,
                            TypeStructureKey = null,
                        };
                        currentCollatedTypes.Add(tl, collatedType);
                        dbCollatedTypes.Add(collatedType);
                    }

                    CgDbElementType et = new()
                    {
                        Key = CgDbElementType.GetIndex(),
                        PackageKey = dbStructure.PackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        CollatedTypeKey = collatedType.Key,
                        TypeName = sd.Id,
                        TypeProfile = null,
                        TargetProfile = null,
                        TypeStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add(et);
                }
                else
                {
                    string btn = ed.cgBaseTypeName(dc, true);
                    string tl = literalForType(btn, null, null);
                    if (!currentCollatedTypes.TryGetValue(tl, out CgDbElementCollatedType? collatedType))
                    {
                        collatedType = new()
                        {
                            Key = CgDbElementCollatedType.GetIndex(),
                            PackageKey = dbStructure.PackageKey,
                            StructureKey = dbStructure.Key,
                            ElementKey = elementKey,
                            TypeName = tl,
                            CollatedLiteral = string.Empty,
                            TypeStructureKey = null,
                        };
                        currentCollatedTypes.Add(tl, collatedType);
                        dbCollatedTypes.Add(collatedType);
                    }

                    CgDbElementType et = new()
                    {
                        Key = CgDbElementType.GetIndex(),
                        PackageKey = dbStructure.PackageKey,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        CollatedTypeKey = collatedType.Key,
                        TypeName = btn,
                        TypeProfile = null,
                        TargetProfile = null,
                        TypeStructureKey = null,
                    };
                    currentElementTypes.Add(et);
                    dbElementTypes.Add(et);
                }
            }

            int additionalBindingCount = 0;

            // check for additional bindings
            if (ed.Binding?.Additional.Count > 0)
            {
                foreach (ElementDefinition.AdditionalComponent additional in ed.Binding.Additional)
                {
                    CgDbElementAdditionalBinding dbAdditionalBinding = new()
                    {
                        Key = CgDbElementAdditionalBinding.GetIndex(),
                        PackageKey = package.Key,
                        StructureKey = dbStructure.Key,
                        ElementKey = elementKey,
                        FhirKey = null,     // TODO: R6 added additional.Key
                        Purpose = additional.Purpose,
                        BindingValueSet = additional.ValueSet,
                        BindingValueSetKey = string.IsNullOrEmpty(additional.ValueSet) ? null : CgDbValueSet.SelectSingle(_db, PackageKey: package.Key, UnversionedUrl: additional.ValueSet)?.Key,
                        Documentation = processTextForLinks(additional.Documentation, package),
                        ShortDocumentation = processTextForLinks(additional.ShortDoco, package),
                        CollatedUsageContexts = additional.Usage.Count == 0
                            ? null
                            : string.Join(", ", additional.Usage.Select(uc => uc.Code.System + "#" + uc.Code.Code + ": `" + uc.Value.ToString() + "`")),
                        SatisfiedBySingleRepetition = additional.Any,
                    };
                    dbAdditionalBindings.Add(dbAdditionalBinding);
                    additionalBindingCount++;
                }
            }

            bool isInherited = ed.cgIsInherited(sd);
            string? basePath = ed.Base?.Path;

            List<string> completeLiteralComponents = [];
            // build our collated type literals
            foreach ((string typeName, CgDbElementCollatedType collatedType) in currentCollatedTypes)
            {
                if (!literalAccumulator.TryGetValue(typeName, out List<string>? literalComponents))
                {
                    literalComponents = [];
                    literalAccumulator.Add(typeName, literalComponents);
                }

                if (literalComponents.Count == 0)
                {
                    collatedType.CollatedLiteral = typeName; // no components, just the type name
                }
                else
                {
                    // multiple components, sort and join them
                    collatedType.CollatedLiteral = typeName + "(" + string.Join(", ", literalComponents.OrderBy(lc => lc)) + ")";
                }

                completeLiteralComponents.Add(collatedType.CollatedLiteral);
            }

            string typeGroupLiteral = string.Join(", ", completeLiteralComponents.Order());

            int resourceFieldOrder = ed.cgFieldOrder();
            int? parentElementDbKey = null;

            if (resourceFieldOrder != 0)
            {
                string parentKey = dbStructure.Key.ToString() + ":" + ed.ElementId.Substring(0, ed.ElementId.LastIndexOf('.'));
                if (dbElements.TryGetValue(parentKey, out CgDbElement? parentElement))
                {
                    parentElementDbKey = parentElement.Key;
                }
            }

            string? fixedValue = ed.Fixed != null ? JsonSerializer.Serialize(ed.Fixed) : null;
            string? patternValue = ed.Pattern != null ? JsonSerializer.Serialize(ed.Pattern) : null;

            CgDbElement dbElement = new()
            {
                Key = elementKey,
                PackageKey = package.Key,
                StructureKey = dbStructure.Key,
                ParentElementKey = parentElementDbKey,
                ResourceFieldOrder = resourceFieldOrder,
                ComponentFieldOrder = ed.cgComponentFieldOrder(),
                Id = ed.ElementId,
                Path = ed.Path,
                ChildElementCount = childCount,
                Name = ed.cgName(),
                Short = processTextForLinks(ed.Short, package),
                Definition = processTextForLinks(ed.Definition, package),
                MinCardinality = ed.cgCardinalityMin(),
                MaxCardinality = ed.cgCardinalityMax(),
                MaxCardinalityString = ed.Max ?? "*",
                SliceName = ed.SliceName,
                ValueSetBindingStrength = ed.Binding?.Strength,
                BindingValueSet = ed.Binding?.ValueSet,
                BindingValueSetKey = bindingVsKey,
                BindingDescription = ed.Binding?.Description,
                AdditionalBindingCount = additionalBindingCount,
                FullCollatedTypeLiteral = typeGroupLiteral,
                IsInherited = isInherited,
                BasePath = basePath,
                BaseElementKey = null,
                BaseStructureKey = null,
                IsSimpleType = ed.cgIsSimple(),
                IsModifier = ed.IsModifier == true,
                IsModifierReason = ed.IsModifierReason,
                StandardStatus = ed.cgStandardStatus(),
                FixedValue = fixedValue,
                PatternValue = patternValue,
                MeaningWhenMissing = string.IsNullOrEmpty(ed.MeaningWhenMissing) ? null : ed.MeaningWhenMissing,
            };

            try
            {
                dbElements.Add(dbStructure.Key.ToString() + ":" + ed.ElementId, dbElement);
            }
            catch (Exception)
            {
                if (ed.ElementId == "ExplanationOfBenefit.item.informationSequence")
                {
                    // current known issue
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private int updateCollatedTypeStructureKeys(
        int packageKey,
        string? collatedTypeTableName = null,
        string? structureTableName = null)
    {
        collatedTypeTableName ??= CgDbElementCollatedType.DefaultTableName;
        structureTableName ??= CgDbStructure.DefaultTableName;

        IDbCommand command = _db.CreateCommand();
        command.CommandText = $$$"""
            UPDATE {{{collatedTypeTableName}}}
            SET
                {{{nameof(CgDbElementCollatedType.TypeStructureKey)}}} = S.{{{nameof(CgDbStructure.Name)}}}
            FROM {{{structureTableName}}} S
            WHERE {{{collatedTypeTableName}}}.{{{nameof(CgDbElementCollatedType.TypeName)}}} = S.{{{nameof(CgDbStructure.Id)}}}
            AND {{{collatedTypeTableName}}}.{{{nameof(CgDbElementCollatedType.PackageKey)}}} = S.{{{nameof(CgDbStructure.PackageKey)}}}
            AND {{{collatedTypeTableName}}}.{{{nameof(CgDbElementCollatedType.PackageKey)}}} = {{{packageKey}}}
            """;

        return command.ExecuteNonQuery();
    }

    private int updateElementTypeStructureKeys(
        int packageKey,
        string? elementTypeTableName = null,
        string? structureTableName = null)
    {
        elementTypeTableName ??= CgDbElementType.DefaultTableName;
        structureTableName ??= CgDbStructure.DefaultTableName;

        IDbCommand command = _db.CreateCommand();
        command.CommandText = $$$"""
            UPDATE {{{elementTypeTableName}}}
            SET
                {{{nameof(CgDbElementType.TypeStructureKey)}}} = S.{{{nameof(CgDbStructure.Key)}}}
            FROM {{{structureTableName}}} S
            WHERE {{{elementTypeTableName}}}.{{{nameof(CgDbElementType.TypeName)}}} = S.{{{nameof(CgDbStructure.Id)}}}
            AND {{{elementTypeTableName}}}.{{{nameof(CgDbElementType.PackageKey)}}} = S.{{{nameof(CgDbStructure.PackageKey)}}}
            AND {{{elementTypeTableName}}}.{{{nameof(CgDbElementType.PackageKey)}}} = {{{packageKey}}}
            """;

        return command.ExecuteNonQuery();
    }


    private int updateElementBaseKeys(
        int packageKey,
        string? dbTableName = null)
    {
        dbTableName ??= CgDbElement.DefaultTableName;

        IDbCommand command = _db.CreateCommand();
        command.CommandText = $$$"""
            UPDATE {{{dbTableName}}}
            SET
                {{{nameof(CgDbElement.BaseElementKey)}}} = E.{{{nameof(CgDbElement.Key)}}},
                {{{nameof(CgDbElement.BaseStructureKey)}}} = E.{{{nameof(CgDbElement.StructureKey)}}}
            FROM {{{dbTableName}}} E
            WHERE {{{dbTableName}}}.{{{nameof(CgDbElement.Key)}}} = E.{{{nameof(CgDbElement.Key)}}}
            AND {{{dbTableName}}}.{{{nameof(CgDbElement.BasePath)}}} is not NULL
            AND {{{dbTableName}}}.{{{nameof(CgDbElement.BaseElementKey)}}} is NULL
            AND {{{dbTableName}}}.{{{nameof(CgDbElement.PackageKey)}}} = {{{packageKey}}}
            """;

        return command.ExecuteNonQuery();
    }

    private void addSearchParameters(
        CgDbPackage package,
        DefinitionCollection dc)
    {
        List<CgDbSearchParameter> dbSearchParameters = [];
        List<CgDbSearchParameterComponent> dbSearchParameterComponents = [];

        // iterate over the search parameters
        foreach (SearchParameter sp in dc.SearchParametersByUrl.Values)
        {
            // skip anything in the exclusion set
            if (_exclusionSet.Contains(sp.Url))
            {
                continue;
            }

            string url = sp.Url;
            string version = string.IsNullOrEmpty(sp.Version)
                ? package.PackageVersion
                : sp.Version;

            if (url.EndsWith("|" + version))
            {
                url = url.Substring(0, url.Length - (version.Length + 1));
            }

            // check for this record already existing
            if (CgDbSearchParameter.SelectSingle(_db, PackageKey: package.Key, UnversionedUrl: url) != null)
            {
                continue;
            }

            List<string> additionalBaseTypes = [];
            // check for a single base with a replacement resource type
            foreach (Code<VersionIndependentResourceTypesAll> baseElement in sp.BaseElement)
            {
                if (baseElement is null)
                {
                    continue;
                }

                Code? extCode = baseElement.GetExtensionValue<Code>(CommonDefinitions.ExtUrlSearchParameterBaseType);

                if (extCode is not null)
                {
                    additionalBaseTypes.Add(extCode.Value);
                }
            }

            // create a new metadata record
            CgDbSearchParameter dbSp = new()
            {
                Key = CgDbSearchParameter.GetIndex(),
                PackageKey = package.Key,
                Id = sp.Id,
                VersionedUrl = url + "|" + version,
                UnversionedUrl = url,
                Version = version,
                VersionAlgorithmString = (sp.VersionAlgorithm != null) && (sp.VersionAlgorithm is FhirString cseVaFs) ? cseVaFs.Value : null,
                VersionAlgorithmCoding = (sp.VersionAlgorithm != null) && (sp.VersionAlgorithm is Coding cseVaC) ? cseVaC : null,
                SourcePackageMoniker = sp.cgPackageSourceAsMoniker(),
                Name = sp.Name,
                Status = sp.Status,
                Title = processTextForLinks(sp.Title, package),
                Description = processTextForLinks(sp.Description, package),
                Purpose = processTextForLinks(sp.Purpose, package),
                Narrative = processTextForLinks(sp.Text, package),
                StandardStatus = sp.cgStandardStatus(),
                WorkGroup = sp.cgWorkGroup(),
                FhirMaturity = sp.cgMaturityLevel(),
                IsExperimental = sp.Experimental,
                Copyright = sp.Copyright,
                CopyrightLabel = sp.CopyrightLabel,
                ApprovalDate = null,
                LastReviewDate = null,
                EffectivePeriodStart = null,
                EffectivePeriodEnd = null,
                Topic = null,
                RelatedArtifacts = null,
                Jurisdictions = sp.Jurisdiction,
                UseContexts = sp.UseContext,
                Authors = null,
                Editors = null,
                Reviewers = null,
                Endorsers = null,
                RootExtensions = sp.Extension,

                DerivedFromCanonical = sp.DerivedFrom,
                Code = sp.Code,
                SearchType = sp.TypeElement?.ObjectValue?.ToString(),
                Expression = sp.Expression,
                ProcessingMode = sp.ProcessingMode,
                SearchParameterConstraint = sp.Constraint,
                MultipleOr = sp.MultipleOr,
                MultipleAnd = sp.MultipleAnd,
                ComponentCount = sp.Component.Count,
                Contacts = sp.Contact,
                LastChangedDate = sp.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                Publisher = sp.Publisher,

                AliasCodes = null,
                BaseResources = string.Empty,
                AdditionalBaseResources = null,
                ReferenceTargets = null,
                Comparators = null,
                Modifiers = null,
                ChainableSearchParameters = null,
            };

            // set list-based properties (overrides previous nulls)
            dbSp.AliasCodeList = [];        // TODO: added in R6
            dbSp.BaseResourceList = sp.BaseElement;
            dbSp.AdditionalBaseResourceList = additionalBaseTypes;
            dbSp.ReferenceTargetList = sp.TargetElement.Select(t => t.ObjectValue?.ToString() ?? string.Empty).ToList();
            dbSp.ComparatorList = sp.ComparatorElement;
            dbSp.ModifierList = sp.ModifierElement;
            dbSp.ChainableSearchParameterList = sp.Chain.ToList();

            dbSearchParameters.Add(dbSp);

            // iterate over any chaining components
            foreach (SearchParameter.ComponentComponent comp in sp.Component)
            {
                CgDbSearchParameterComponent dbComp = new()
                {
                    Key = CgDbSearchParameterComponent.GetIndex(),
                    PackageKey = package.Key,
                    SearchParameterKey = dbSp.Key,
                    DefinitionCanonical = comp.Definition,
                    Expression = comp.Expression,
                };
                dbSearchParameterComponents.Add(dbComp);
            }
        }

        Console.WriteLine($"Inserting SearchParameters for {package.PackageId}@{package.PackageVersion} into database...");

        _db.Insert(dbSearchParameters);
        Console.WriteLine($" <<< added {dbSearchParameters.Count} SearchParameters");

        _db.Insert(dbSearchParameterComponents);
        Console.WriteLine($" <<< added {dbSearchParameterComponents.Count} SearchParameter Components");
    }

    private void addOperations(
        CgDbPackage package,
        DefinitionCollection dc)
    {
        List<CgDbOperation> dbOperations = [];
        List<CgDbOperationParameter> dbOperationParameters = [];

        // iterate over the search parameters
        foreach (OperationDefinition op in dc.OperationsByUrl.Values)
        {
            // skip anything in the exclusion set
            if (_exclusionSet.Contains(op.Url))
            {
                continue;
            }

            string url = op.Url;
            string version = string.IsNullOrEmpty(op.Version)
                ? package.PackageVersion
                : op.Version;

            if (url.EndsWith("|" + version))
            {
                url = url.Substring(0, url.Length - (version.Length + 1));
            }

            // check for this record already existing
            if (CgDbOperation.SelectSingle(_db, PackageKey: package.Key, UnversionedUrl: url) != null)
            {
                continue;
            }

            List<string> additionalResourceTypes = [];
            // check for a single base with a replacement resource type
            foreach (Code<VersionIndependentResourceTypesAll> resourceElement in op.ResourceElement)
            {
                if (resourceElement is null)
                {
                    continue;
                }

                Code? extCode = resourceElement.GetExtensionValue<Code>(CommonDefinitions.ExtUrlOperationDefinitionAllowedType);

                if (extCode is not null)
                {
                    additionalResourceTypes.Add(extCode.Value);
                }
            }

            // create a new metadata record
            CgDbOperation dbOp = new()
            {
                Key = CgDbOperation.GetIndex(),
                PackageKey = package.Key,
                Id = op.Id,
                VersionedUrl = url + "|" + version,
                UnversionedUrl = url,
                Version = version,
                VersionAlgorithmString = (op.VersionAlgorithm != null) && (op.VersionAlgorithm is FhirString cseVaFs) ? cseVaFs.Value : null,
                VersionAlgorithmCoding = (op.VersionAlgorithm != null) && (op.VersionAlgorithm is Coding cseVaC) ? cseVaC : null,
                SourcePackageMoniker = op.cgPackageSourceAsMoniker(),
                Name = op.Name,
                Status = op.Status,
                Title = processTextForLinks(op.Title, package),
                Description = processTextForLinks(op.Description, package),
                Purpose = processTextForLinks(op.Purpose, package),
                Narrative = processTextForLinks(op.Text, package),
                StandardStatus = op.cgStandardStatus(),
                WorkGroup = op.cgWorkGroup(),
                FhirMaturity = op.cgMaturityLevel(),
                IsExperimental = op.Experimental,
                Copyright = op.Copyright,
                CopyrightLabel = op.CopyrightLabel,
                ApprovalDate = null,
                LastReviewDate = null,
                EffectivePeriodStart = null,
                EffectivePeriodEnd = null,
                Topic = null,
                RelatedArtifacts = null,
                Jurisdictions = op.Jurisdiction,
                UseContexts = op.UseContext,
                Authors = null,
                Editors = null,
                Reviewers = null,
                Endorsers = null,
                RootExtensions = op.Extension,

                Contacts = op.Contact,
                LastChangedDate = op.DateElement?.ToDateTimeOffset(TimeSpan.Zero),
                Publisher = op.Publisher,

                Kind = op.Kind ?? OperationDefinition.OperationKind.Operation,
                AffectsState = op.AffectsState,
                Synchronicity = null,                       // TODO: Added in R6, ValueSet in Extensions package
                Code = op.Code,
                Comment = processTextForLinks(op.Comment, package),
                BaseCanonical = op.Base,
                ResourceTypes = null,
                AdditionalResourceTypes = null,
                InvokeOnSystem = op.System ?? false,
                InvokeOnType = op.Type ?? false,
                InvokeOnInstance = op.Instance ?? false,
                InputProfileCanonical = op.InputProfile,
                OutputProfileCanonical = op.OutputProfile,
                ParameterCount = op.Parameter.Count,
                Overloads = op.Overload.Count == 0 ? null : op.Overload,
            };

            // set list-based properties (overrides previous nulls)
            dbOp.ResourceTypeList = op.ResourceElement;
            dbOp.AdditionalResourceTypeList = additionalResourceTypes;

            int parameterIndex = 0;
            addOpParams(dbOp.Key, op.Parameter, ref parameterIndex);

            dbOperations.Add(dbOp);
        }

        Console.WriteLine($"Inserting OperationDefinitions for {package.PackageId}@{package.PackageVersion} into database...");

        _db.Insert(dbOperations);
        Console.WriteLine($" <<< added {dbOperations.Count} OperationDefinitions");

        _db.Insert(dbOperationParameters);
        Console.WriteLine($" <<< added {dbOperationParameters.Count} OperationParameter Components");

        return;

        void addOpParams(int opKey, List<OperationDefinition.ParameterComponent> opParams, ref int operationParamIndex, int? parentParamKey = null)
        {
            int localOrder = 0;

            foreach (OperationDefinition.ParameterComponent? param in opParams)
            {
                CgDbOperationParameter dbOpParam = new()
                {
                    Key = CgDbOperationParameter.GetIndex(),
                    PackageKey = package.Key,
                    OperationKey = opKey,
                    Name = param.Name,
                    Use = param.Use ?? OperationParameterUse.In,
                    Scopes = null,
                    Min = param.Min ?? 0,
                    Max = param.Max ?? "*",
                    Documentation = processTextForLinks(param.Documentation, package),
                    Type = param.Type?.ToString(),
                    AllowedTypes = null,
                    TargetProfileCanonicals = null,
                    SearchType = param.SearchType,
                    BindingStrength = param.Binding?.Strength,
                    BindingValueSetCanonical = param.Binding?.ValueSet,
                    ReferencedFrom = param.ReferencedFrom.Count == 0 ? null : param.ReferencedFrom,
                    ParentParameterKey = parentParamKey,
                    ChildParameterCount = param.Part.Count,
                    OperationParameterOrder = operationParamIndex++,
                    ParameterPartOrder = localOrder++,
                };

                dbOpParam.ScopeList = param.ScopeElement;
                dbOpParam.AllowedTypeList = param.AllowedTypeElement;
                dbOpParam.TargetProfileCanonicalList = param.TargetProfile.ToList();

                dbOperationParameters.Add(dbOpParam);

                // add any nested parameters
                if (param.Part.Count > 0)
                {
                    addOpParams(opKey, param.Part, ref operationParamIndex, dbOpParam.Key);
                }
            }
        }
    }
}
