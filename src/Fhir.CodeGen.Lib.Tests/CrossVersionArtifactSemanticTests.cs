// <copyright file="CrossVersionArtifactSemanticTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.Model;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Fhir.CodeGen.Lib.Tests;

public class CrossVersionArtifactSemanticTests
{
    [Fact]
    public void XVerResourceAndTypeConceptMapsSeparateArtifactClasses()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        List<string> resourceSources = GetConceptMapSourceCodes(fixture.ResourceConceptMapPath);
        List<string> typeSources = GetConceptMapSourceCodes(fixture.TypeConceptMapPath);

        resourceSources.ShouldContain("Patient");
        resourceSources.ShouldNotContain("Address");
        typeSources.ShouldContain("Address");
    }

    [Fact]
    public void XVerTypeMapRepresentsUnmappedComplexTypesWithoutBasic()
    {
        using SemanticFixture fixture = SemanticFixture.Create();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.TypeConceptMapPath));

        JsonElement sourceElement = FindConceptMapSourceElement(document, "UnmappedType");

        if (sourceElement.TryGetProperty("target", out JsonElement targets))
        {
            targets.EnumerateArray()
                .Select(target => target.GetProperty("code").GetString())
                .ShouldNotContain("Basic");
        }
    }

    [Fact]
    public void XVerResourceNoMapStillUsesBasic()
    {
        using SemanticFixture fixture = SemanticFixture.Create();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.ResourceConceptMapPath));

        JsonElement sourceElement = FindConceptMapSourceElement(document, "UnmappedResource");

        sourceElement.GetProperty("target")
            .EnumerateArray()
            .Select(target => target.GetProperty("code").GetString())
            .ShouldContain("Basic");
    }

    [Fact]
    public void XVerExcludesDataTypeAndPrimitiveTypeFromTypeIndex()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        File.Exists(Path.Combine(fixture.ResourceDirectory, "StructureDefinition-r4-datatype-to-r5-nomap.json")).ShouldBeFalse();
        File.Exists(Path.Combine(fixture.ResourceDirectory, "StructureDefinition-r4-primitivetype-to-r5-nomap.json")).ShouldBeFalse();

        File.Exists(Path.Combine(fixture.PageContentDirectory, "lookup-sd-r4-datatype-to-r5-nomap.md")).ShouldBeFalse();
        File.Exists(Path.Combine(fixture.PageContentDirectory, "lookup-sd-r4-primitivetype-to-r5-nomap.md")).ShouldBeFalse();

        string typeLookupIndex = File.ReadAllText(fixture.TypeLookupIndexPath);
        typeLookupIndex.ShouldNotContain("DataType");
        typeLookupIndex.ShouldNotContain("PrimitiveType");

        List<string> typeSources = GetConceptMapSourceCodes(fixture.TypeConceptMapPath);
        typeSources.ShouldNotContain("DataType");
        typeSources.ShouldNotContain("PrimitiveType");
    }

    [Fact]
    public void XVerComplexTypeProfilesAreNotEmitted()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        File.Exists(fixture.AddressProfilePath).ShouldBeFalse();
        File.Exists(fixture.UnmappedTypeProfilePath).ShouldBeFalse();
    }

    [Fact]
    public void XVerTypeLookupIndexHasNoProfileColumn()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        string typeLookupIndex = File.ReadAllText(fixture.TypeLookupIndexPath);

        string[] lines = typeLookupIndex.Split('\n');
        string? headerLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("| R4 Type", StringComparison.Ordinal));
        headerLine.ShouldNotBeNull("Expected a `| R4 Type` header row in the type lookup index.");
        headerLine!.ShouldNotContain("Profile");
    }

    [Fact]
    public void XVerUnmappedComplexTypeElementMapDoesNotTargetBasic()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        if (!File.Exists(fixture.UnmappedTypeElementMapPath))
        {
            return;
        }

        string elementMapJson = File.ReadAllText(fixture.UnmappedTypeElementMapPath);
        elementMapJson.ShouldNotContain("Basic");
    }

    [Fact]
    public void XVerTypeLookupIndexLinksTypeMap()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        string lookupIndex = File.ReadAllText(fixture.TypeLookupIndexPath);

        lookupIndex.ShouldContain("ConceptMap-R4-type-map-to-R5.html");
        lookupIndex.ShouldNotContain("resource-map");
        lookupIndex.ShouldNotContain("ConceptMap-ConceptMap-");
    }

    [Fact]
    public void XVerTypeLookupPagesUseTypeWording()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        string lookupPage = File.ReadAllText(fixture.AddressTypeLookupPath);

        lookupPage.ShouldContain("complex type");
        lookupPage.ShouldNotContain("resource is represented");
    }

    [Fact]
    public void XVerUnmappedTypeLookupDoesNotLinkBasic()
    {
        using SemanticFixture fixture = SemanticFixture.Create();

        string lookupPage = File.ReadAllText(fixture.UnmappedTypeLookupPath);

        lookupPage.ShouldNotContain("Basic.html");
        lookupPage.ShouldNotContain("Basic resource");
        lookupPage.ShouldContain("no direct target type");
    }

    private static List<string> GetConceptMapSourceCodes(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<string> sourceCodes = [];

        foreach (JsonElement group in document.RootElement.GetProperty("group").EnumerateArray())
        {
            foreach (JsonElement element in group.GetProperty("element").EnumerateArray())
            {
                sourceCodes.Add(element.GetProperty("code").GetString()!);
            }
        }

        return sourceCodes;
    }

    private static JsonElement FindConceptMapSourceElement(JsonDocument document, string sourceCode)
    {
        foreach (JsonElement group in document.RootElement.GetProperty("group").EnumerateArray())
        {
            foreach (JsonElement element in group.GetProperty("element").EnumerateArray())
            {
                if (element.GetProperty("code").GetString() == sourceCode)
                {
                    return element;
                }
            }
        }

        throw new ShouldAssertException($"ConceptMap source element `{sourceCode}` was not found.");
    }

    private sealed class SemanticFixture : IDisposable
    {
        private const string SourceShortName = "R4";
        private const string TargetShortName = "R5";
        private readonly SqliteConnection _connection;

        private SemanticFixture(SqliteConnection connection, string outputRoot)
        {
            _connection = connection;
            OutputRoot = outputRoot;
        }

        public string OutputRoot { get; }

        public string PackageRoot => Path.Combine(OutputRoot, "hl7.fhir.uv.xver-r4.r5");

        public string ResourceDirectory => Path.Combine(PackageRoot, "input", "resources");

        public string PageContentDirectory => Path.Combine(PackageRoot, "input", "pagecontent");

        public string ResourceConceptMapPath => Path.Combine(ResourceDirectory, $"{SourceShortName}-resource-map-to-{TargetShortName}.json");

        public string TypeConceptMapPath => Path.Combine(ResourceDirectory, $"{SourceShortName}-type-map-to-{TargetShortName}.json");

        public string AddressProfilePath => Path.Combine(ResourceDirectory, "StructureDefinition-r4-address-to-r5-address.json");

        public string UnmappedTypeProfilePath => Path.Combine(ResourceDirectory, "StructureDefinition-r4-unmappedtype-to-r5-nomap.json");

        public string UnmappedTypeElementMapPath => Path.Combine(ResourceDirectory, $"{SourceShortName}-UnmappedType-elements-for-{TargetShortName}-NoMap.json");

        public string TypeLookupIndexPath => Path.Combine(PageContentDirectory, "lookup-sd-types.md");

        public string AddressTypeLookupPath => Path.Combine(PageContentDirectory, "lookup-sd-r4-address-to-r5-address.md");

        public string UnmappedTypeLookupPath => Path.Combine(PageContentDirectory, "lookup-sd-r4-unmappedtype-to-r5-nomap.md");

        public static SemanticFixture Create()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            connection.Open();

            DbContentClasses.CreateTables(connection);
            DbComparisonClasses.CreateTables(connection);
            DbOutcomeClasses.CreateTables(connection);

            string outputRoot = Path.Combine(Path.GetTempPath(), $"fhir-codegen-xver-semantic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputRoot);

            SeedDatabase(connection);

            ConfigXVer config = new()
            {
                OutputDirectory = outputRoot,
                XverArtifactVersion = "0.1.0-test",
            };

            XVerExporter exporter = new(connection, config);
            exporter.Export(
                includeIgScripts: false,
                processVocabulary: true,
                processStructures: true,
                specificPairs: [(FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R5)]);

            return new(connection, outputRoot);
        }

        public void Dispose()
        {
            _connection.Dispose();

            if (Directory.Exists(OutputRoot))
            {
                Directory.Delete(OutputRoot, recursive: true);
            }
        }

        private static void SeedDatabase(IDbConnection connection)
        {
            DbFhirPackage sourcePackage = InsertPackage(
                connection,
                FhirReleases.FhirSequenceCodes.R4,
                SourceShortName,
                "hl7.fhir.r4.core",
                "4.0.1");
            DbFhirPackage targetPackage = InsertPackage(
                connection,
                FhirReleases.FhirSequenceCodes.R5,
                TargetShortName,
                "hl7.fhir.r5.core",
                "5.0.0");

            DbStructureDefinition sourcePatient = InsertStructure(connection, sourcePackage, "Patient", FhirArtifactClassEnum.Resource);
            DbStructureDefinition sourceAddress = InsertStructure(connection, sourcePackage, "Address", FhirArtifactClassEnum.ComplexType);
            DbStructureDefinition sourceUnmappedType = InsertStructure(connection, sourcePackage, "UnmappedType", FhirArtifactClassEnum.ComplexType);
            DbStructureDefinition sourceUnmappedResource = InsertStructure(connection, sourcePackage, "UnmappedResource", FhirArtifactClassEnum.Resource);
            DbStructureDefinition sourceDataType = InsertStructure(connection, sourcePackage, "DataType", FhirArtifactClassEnum.ComplexType);
            DbStructureDefinition sourcePrimitiveType = InsertStructure(connection, sourcePackage, "PrimitiveType", FhirArtifactClassEnum.ComplexType);

            InsertRootElement(connection, sourcePackage, sourcePatient);
            InsertRootElement(connection, sourcePackage, sourceAddress);
            InsertRootElement(connection, sourcePackage, sourceUnmappedType);
            InsertRootElement(connection, sourcePackage, sourceUnmappedResource);
            InsertRootElement(connection, sourcePackage, sourceDataType);
            InsertRootElement(connection, sourcePackage, sourcePrimitiveType);

            DbStructureDefinition targetPatient = InsertStructure(connection, targetPackage, "Patient", FhirArtifactClassEnum.Resource);
            DbStructureDefinition targetAddress = InsertStructure(connection, targetPackage, "Address", FhirArtifactClassEnum.ComplexType);
            DbStructureDefinition targetBasic = InsertStructure(connection, targetPackage, "Basic", FhirArtifactClassEnum.Resource);
            DbStructureDefinition targetElement = InsertStructure(connection, targetPackage, "Element", FhirArtifactClassEnum.ComplexType);
            DbStructureDefinition targetExtension = InsertStructure(connection, targetPackage, "Extension", FhirArtifactClassEnum.ComplexType);

            InsertRootElement(connection, targetPackage, targetPatient);
            InsertUrlElement(connection, targetPackage, targetPatient);
            InsertRootElement(connection, targetPackage, targetAddress);
            InsertRootElement(connection, targetPackage, targetBasic);
            InsertUrlElement(connection, targetPackage, targetBasic);
            InsertRootElement(connection, targetPackage, targetElement);
            InsertRootElement(connection, targetPackage, targetExtension);
            DbElement extensionValue = InsertElement(connection, targetPackage, targetExtension, "Extension.value[x]", "value[x]", 1, isChoiceType: true);
            InsertElementType(connection, targetPackage, targetExtension, extensionValue, "string", null);

            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourcePatient, targetPatient);
            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourceAddress, targetAddress);
            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourceUnmappedType, null);
            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourceUnmappedResource, null);
            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourceDataType, null);
            InsertStructureOutcome(connection, sourcePackage, targetPackage, sourcePrimitiveType, null);
        }

        private static DbFhirPackage InsertPackage(
            IDbConnection connection,
            FhirReleases.FhirSequenceCodes sequence,
            string shortName,
            string packageId,
            string packageVersion)
        {
            DbFhirPackage package = new()
            {
                Name = $"FHIR {shortName}",
                PackageId = packageId,
                PackageVersion = packageVersion,
                FhirVersionShort = sequence.ToShortVersion(),
                CanonicalUrl = "http://hl7.org/fhir",
                ShortName = shortName,
                Dependencies = null,
                DefinitionFhirSequence = sequence,
            };

            package.Key = DbFhirPackage.Insert(connection, package);
            return package;
        }

        private static DbStructureDefinition InsertStructure(
            IDbConnection connection,
            DbFhirPackage package,
            string id,
            FhirArtifactClassEnum artifactClass)
        {
            string unversionedUrl = $"http://hl7.org/fhir/StructureDefinition/{id}";
            DbStructureDefinition structure = new()
            {
                FhirPackageKey = package.Key,
                Id = id,
                VersionedUrl = $"{unversionedUrl}|{package.PackageVersion}",
                UnversionedUrl = unversionedUrl,
                Name = id,
                Version = package.PackageVersion,
                VersionAlgorithmString = null,
                VersionAlgorithmCoding = null,
                Status = PublicationStatus.Active,
                Title = id,
                Description = null,
                Purpose = null,
                Narrative = null,
                StandardStatus = null,
                WorkGroup = "fhir",
                FhirMaturity = null,
                IsExperimental = false,
                LastChangedDate = null,
                Publisher = "HL7",
                Copyright = null,
                CopyrightLabel = null,
                ApprovalDate = null,
                LastReviewDate = null,
                EffectivePeriodStart = null,
                EffectivePeriodEnd = null,
                Topic = [],
                RelatedArtifacts = [],
                Jurisdictions = [],
                UseContexts = [],
                Contacts = [],
                Authors = [],
                Editors = [],
                Reviewers = [],
                Endorsers = [],
                RootExtensions = [],
                SourcePackageMoniker = null,
                Comment = null,
                Message = null,
                ArtifactClass = artifactClass,
                SnapshotCount = 1,
                DifferentialCount = 0,
                Implements = null,
            };

            structure.Key = DbStructureDefinition.Insert(connection, structure);
            return structure;
        }

        private static DbElement InsertRootElement(
            IDbConnection connection,
            DbFhirPackage package,
            DbStructureDefinition structure) =>
            InsertElement(connection, package, structure, structure.Id, structure.Name, 0);

        private static DbElement InsertUrlElement(
            IDbConnection connection,
            DbFhirPackage package,
            DbStructureDefinition structure) =>
            InsertElement(connection, package, structure, $"{structure.Id}.url", "url", 1, typeLiteral: "uri");

        private static DbElement InsertElement(
            IDbConnection connection,
            DbFhirPackage package,
            DbStructureDefinition structure,
            string id,
            string name,
            int order,
            string typeLiteral = "",
            bool isChoiceType = false)
        {
            DbElement element = new()
            {
                FhirPackageKey = package.Key,
                StructureKey = structure.Key,
                ParentElementKey = null,
                ResourceFieldOrder = order,
                ComponentFieldOrder = order,
                Id = id,
                Path = id,
                ChildElementCount = 0,
                Name = name,
                Short = null,
                Definition = null,
                Comments = null,
                Requirements = null,
                MinCardinality = 0,
                MaxCardinality = 1,
                MaxCardinalityString = "1",
                SliceName = null,
                FullCollatedTypeLiteral = typeLiteral,
                DistinctTypeCount = string.IsNullOrEmpty(typeLiteral) ? 0 : 1,
                DistinctTypeLiterals = typeLiteral,
                ValueSetBindingStrength = null,
                BindingValueSet = null,
                BindingValueSetKey = null,
                AdditionalBindingCount = 0,
                BindingDescription = null,
                IsInherited = false,
                BasePath = null,
                BaseElementKey = null,
                BaseStructureKey = null,
                DefinedAsContentReference = false,
                ContentReferenceSourceKey = null,
                ContentReferenceSourceId = null,
                UsedAsContentReference = false,
                IsSimpleType = false,
                IsChoiceType = isChoiceType,
                IsModifier = false,
                IsModifierReason = null,
                IsDeprecated = false,
            };

            element.Key = DbElement.Insert(connection, element);
            return element;
        }

        private static void InsertElementType(
            IDbConnection connection,
            DbFhirPackage package,
            DbStructureDefinition structure,
            DbElement element,
            string typeName,
            DbStructureDefinition? typeStructure)
        {
            DbElementType elementType = new()
            {
                FhirPackageKey = package.Key,
                StructureKey = structure.Key,
                ElementKey = element.Key,
                TypeName = typeName,
                TypeStructureKey = typeStructure?.Key,
                TypeProfile = null,
                TypeProfileStructureKey = null,
                TargetProfile = null,
                TargetProfileStructureKey = null,
            };

            elementType.Key = DbElementType.Insert(connection, elementType);
        }

        private static void InsertStructureOutcome(
            IDbConnection connection,
            DbFhirPackage sourcePackage,
            DbFhirPackage targetPackage,
            DbStructureDefinition sourceStructure,
            DbStructureDefinition? targetStructure)
        {
            string targetSuffix = targetStructure?.Id ?? "NoMap";
            string profileId = $"{sourcePackage.ShortName.ToLowerInvariant()}-{sourceStructure.Id.ToLowerInvariant()}-to-{targetPackage.ShortName.ToLowerInvariant()}-{targetSuffix.ToLowerInvariant()}";
            string elementMapId = $"{sourcePackage.ShortName}-{sourceStructure.Id}-elements-for-{targetPackage.ShortName}-{targetSuffix}";
            bool hasTarget = targetStructure is not null;

            DbStructureOutcome outcome = new()
            {
                SourceFhirPackageKey = sourcePackage.Key,
                SourceFhirSequence = sourcePackage.DefinitionFhirSequence,
                TargetFhirPackageKey = targetPackage.Key,
                TargetFhirSequence = targetPackage.DefinitionFhirSequence,
                RequiresXVerDefinition = true,
                TotalTargetCount = hasTarget ? 1 : 0,
                TotalSourceCount = 1,
                IsRenamed = false,
                IsUnmapped = !hasTarget,
                IsIdentical = hasTarget && (sourceStructure.Id == targetStructure!.Id),
                IsEquivalent = hasTarget && (sourceStructure.Id != targetStructure!.Id),
                IsBroaderThanTarget = false,
                IsNarrowerThanTarget = false,
                FullyMapsToThisTarget = hasTarget,
                FullyMapsAcrossAllTargets = hasTarget,
                Comments = hasTarget ? "Equivalent test mapping" : "No direct target mapping",
                GenLongId = profileId,
                GenShortId = profileId,
                GenUrl = $"http://hl7.org/fhir/uv/xver/StructureDefinition/{profileId}",
                GenName = ToPascalName(profileId),
                GenFileName = $"StructureDefinition-{profileId}",
                GenArtifactShort = null,
                GenArtifactDescription = null,
                GenArtifactComment = null,
                GenMappingComment = null,
                SourceCanonicalVersioned = sourceStructure.VersionedUrl,
                SourceCanonicalUnversioned = sourceStructure.UnversionedUrl,
                SourceId = sourceStructure.Id,
                SourceName = sourceStructure.Name,
                SourceVersion = sourceStructure.Version,
                TargetCanonicalVersioned = targetStructure?.VersionedUrl,
                TargetCanonicalUnversioned = targetStructure?.UnversionedUrl,
                TargetId = targetStructure?.Id,
                TargetName = targetStructure?.Name,
                TargetVersion = targetStructure?.Version,
                StructureComparisonKey = null,
                SourceStructureKey = sourceStructure.Key,
                SourceArtifactClass = sourceStructure.ArtifactClass,
                TargetStructureKey = targetStructure?.Key,
                TargetArtifactClass = targetStructure?.ArtifactClass,
                ElementConceptMapLongId = elementMapId,
                ElementConceptMapShortId = elementMapId,
                ElementConceptMapUrl = $"http://hl7.org/fhir/uv/xver/ConceptMap/{elementMapId}",
                ElementConceptMapName = ToPascalName(elementMapId),
                ElementConceptMapFileName = elementMapId,
            };

            outcome.Key = DbStructureOutcome.Insert(connection, outcome);
        }

        private static string ToPascalName(string value) =>
            string.Concat(value.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => part[..1].ToUpperInvariant() + part[1..]));
    }
}
