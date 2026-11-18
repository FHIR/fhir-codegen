// <copyright file="XVerProfileCatalogTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Fhir.CodeGen.Common.FhirExtensions;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Configuration;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;
using ConceptMap = Hl7.Fhir.Model.ConceptMap;
using ElementDefinition = Hl7.Fhir.Model.ElementDefinition;
using StructureDefinition = Hl7.Fhir.Model.StructureDefinition;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class XVerProfileCatalogTests
{
    private readonly ITestOutputHelper _output;

    public XVerProfileCatalogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string SimpleQuantityJson = """
        {
          "resourceType": "StructureDefinition",
          "id": "SimpleQuantity",
          "url": "http://hl7.org/fhir/StructureDefinition/SimpleQuantity",
          "version": "3.0.2",
          "name": "SimpleQuantity",
          "status": "active",
          "publisher": "HL7",
          "copyright": "Fixture attribution",
          "contact": [{"name": "FHIR Infrastructure", "telecom": [{"system": "url", "value": "http://hl7.org/fhir"}]}],
          "kind": "complex-type",
          "abstract": false,
          "type": "Quantity",
          "baseDefinition": "http://hl7.org/fhir/StructureDefinition/Quantity",
          "derivation": "constraint",
          "snapshot": {
            "element": [
              {
                "id": "Quantity:simplequantity",
                "path": "Quantity",
                "min": 0,
                "max": "*",
                "constraint": [{
                  "key": "sqty-1",
                  "severity": "error",
                  "human": "The comparator is not allowed",
                  "expression": "comparator.empty()",
                  "source": "http://hl7.org/fhir/StructureDefinition/SimpleQuantity"
                }]
              },
              {
                "id": "Quantity:simplequantity.comparator",
                "path": "Quantity.comparator",
                "min": 0,
                "max": "0",
                "type": [{"code": "code"}]
              },
              {
                "id": "Quantity:simplequantity.system",
                "path": "Quantity.system",
                "min": 1,
                "max": "1",
                "type": [{"code": "uri"}],
                "fixedUri": "http://unitsofmeasure.org"
              },
              {
                "id": "Quantity:simplequantity.code",
                "path": "Quantity.code",
                "min": 1,
                "max": "1",
                "type": [{"code": "code"}],
                "patternCode": "mg",
                "binding": {"strength": "required", "valueSet": "http://example.org/ValueSet/units|units-1"}
              },
              {
                "id": "Quantity:simplequantity.extension",
                "path": "Quantity.extension",
                "min": 0,
                "max": "*",
                "slicing": {
                  "discriminator": [{"type": "value", "path": "url"}],
                  "ordered": false,
                  "rules": "closed"
                },
                "type": [{"code": "Extension"}]
              },
              {
                "id": "Quantity:simplequantity.extension:units",
                "path": "Quantity.extension",
                "sliceName": "units",
                "min": 0,
                "max": "1",
                "type": [{"code": "Extension", "profile": ["http://example.org/StructureDefinition/units|extension-1"]}]
              }
            ]
          },
          "differential": {
            "element": [
              {
                "id": "Quantity:simplequantity",
                "path": "Quantity",
                "constraint": [{
                  "key": "sqty-1",
                  "severity": "error",
                  "human": "The comparator is not allowed",
                  "expression": "comparator.empty()"
                }]
              },
              {"id": "Quantity:simplequantity.comparator", "path": "Quantity.comparator", "max": "0"},
              {"id": "Quantity:simplequantity.system", "path": "Quantity.system", "fixedUri": "http://unitsofmeasure.org"},
              {
                "id": "Quantity:simplequantity.code",
                "path": "Quantity.code",
                "patternCode": "mg",
                "binding": {"strength": "required", "valueSet": "http://example.org/ValueSet/units|units-1"}
              },
              {
                "id": "Quantity:simplequantity.extension",
                "path": "Quantity.extension",
                "slicing": {"discriminator": [{"type": "value", "path": "url"}], "rules": "closed"}
              }
            ]
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantityMapsRetainAuthoredProvenanceAndPrecedence(bool fallbackFirst)
    {
        using UnsafeCodeTestFixture fixture = UnsafeCodeTestFixture.CreateProcessingEligibilityInputs();
        FhirPackageComparisonPair pair = new(fixture.Packages[2], fixture.Packages[1]);
        DbStructureDefinition source = fixture.Structure(pair.SourcePackage, "SimpleQuantity");
        DbStructureDefinition target = fixture.Structure(pair.TargetPackage, "SimpleQuantity");
        DbElement sourceUse = fixture.AddElement(fixture.Structure(pair.SourcePackage), "ValueSet.measure", "Quantity");
        fixture.AddType(sourceUse, "Quantity", fixture.Structure(pair.SourcePackage, "Quantity"), source);
        DbElement targetUse = fixture.AddTypedElement(fixture.Structure(pair.TargetPackage), "ValueSet.measure", "SimpleQuantity");
        fixture.PublishAuthoredDefinitions();
        fixture.WriteProcessingTypeMaps();
        bool[] fallbackFlags = [false, true];
        foreach (bool isFallback in fallbackFlags)
        {
            string filename = isFallback ? "ConceptMap-types-fallback.json" : "ConceptMap-types-quantity-5to4B.json";
            ConceptMap map = fixture.ReadMap("types", filename);
            map.Group[0].Element.Add(new()
            {
                Code = "Quantity", Target = [new()
                {
                    Code = "SimpleQuantity",
                    Relationship = isFallback ? ConceptMap.ConceptMapRelationship.NotRelatedTo
                        : ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget,
                    Comment = isFallback ? "Conflicting fallback occurrence authority" : "Selected primary occurrence authority",
                }],
            });
            fixture.WriteInputMap(map, "types", filename);
        }
        using MappingLoader mappingLoader = new(fixture.Connection, NullLoggerFactory.Instance);
        mappingLoader.TryLoadCrossVersionSourceMaps(fixture.SourceRoot, false).ShouldBeTrue();
        using PackageLoader packageLoader = new(new()
        {
            OfflineMode = true, AutoLoadExpansions = false, ResolvePackageDependencies = false,
            LogFactory = NullLoggerFactory.Instance,
        }, new() { JsonModel = LoaderOptions.JsonDeserializationModel.SystemTextJson });
        ConceptMapLoader loader = new(mappingLoader, fixture.Connection, packageLoader,
            DbFhirPackage.SelectList(fixture.Connection), NullLoggerFactory.Instance);
        DbStructureMapping.Delete(fixture.Connection);
        loader.LoadInternalTypeMaps(loadPrimitives: false, loadComplex: true);
        DbStructureMapping internalMap = DbStructureMapping.SelectList(fixture.Connection,
            SourceStructureKey: source.Key, TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem();
        internalMap.ExplicitNoMap.ShouldBeFalse();
        internalMap.TargetStructureKey.ShouldBeNull();
        internalMap.Relationship.ShouldBeNull();
        internalMap.ConceptMapSourceKey.ShouldBeNull();
        internalMap.FmlSourceKey.ShouldBeNull();
        string[] nativeTypes = ["Age", "Count", "Distance", "Duration", "Money"];
        foreach (string native in nativeTypes)
        {
            DbStructureMapping.SelectList(fixture.Connection, SourceStructureKey: fixture.Structure(pair.SourcePackage, native).Key,
                TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem().TargetStructureKey
                .ShouldBe(fixture.Structure(pair.TargetPackage, native).Key);
        }
        new XVerStructureCatalog(fixture.Connection).IsQuantityFamily(fixture.Structure(pair.SourcePackage, "Money")).ShouldBeFalse();

        DbStructureMapping.Delete(fixture.Connection);
        string[] patterns = fallbackFirst
            ? ["ConceptMap-types-fallback.json", "ConceptMap-types-quantity-*.json"]
            : ["ConceptMap-types-quantity-*.json", "ConceptMap-types-fallback.json"];
        foreach (string pattern in patterns)
        {
            loader.LoadSourceMaps(fixture.SourceRoot, "types", pattern);
        }
        HashSet<int> passiveKeys = [.. DbStructureDefinition.SelectList(fixture.Connection, ArtifactClass: FhirArtifactClassEnum.Profile)
            .Where(owner => owner.Id != "SimpleQuantity").Select(owner => owner.Key)];
        DbStructureMapping.SelectList(fixture.Connection).ShouldAllBe(mapping => !passiveKeys.Contains(mapping.SourceStructureKey) &&
            (mapping.TargetStructureKey == null || !passiveKeys.Contains(mapping.TargetStructureKey.Value)));
        DbStructureMapping[] authorities = [.. DbStructureMapping.SelectList(fixture.Connection,
            SourceStructureKey: source.Key, TargetStructureKey: target.Key)];
        authorities.Length.ShouldBe(2);
        DbStructureMapping primary = authorities.Single(mapping => !mapping.IsFallback);
        DbStructureMapping fallback = authorities.Single(mapping => mapping.IsFallback);
        primary.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.Equivalent);
        fallback.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.NotRelatedTo);
        primary.Comments.ShouldBe("Authored primary Quantity authority");
        primary.SourceStructureUrl.ShouldBe(source.UnversionedUrl);
        primary.TargetStructureUrl.ShouldBe(target.UnversionedUrl);
        DbMappingSourceFile.SelectSingle(fixture.Connection, Key: primary.ConceptMapSourceKey)!.Url
            .ShouldBe("http://example.org/ConceptMap/quantity-5to4B");
        DbMappingSourceFile.SelectSingle(fixture.Connection, Key: fallback.ConceptMapSourceKey)!.Url
            .ShouldBe("http://example.org/ConceptMap/processing-fallback");

        fixture.CompareProfilePairs(pair);
        DbStructureComparison comparison = DbStructureComparison.SelectList(fixture.Connection,
            SourceStructureKey: source.Key, TargetStructureKey: target.Key).ShouldHaveSingleItem();
        comparison.StructureMappingKey.ShouldBe(primary.Key);
        comparison.IsBasedOnFallbackMapping.ShouldBeFalse();
        comparison.SourceCanonicalVersioned.ShouldBe(source.VersionedUrl);
        comparison.TargetCanonicalVersioned.ShouldBe(target.VersionedUrl);
        comparison.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget);
        DbElement sourceRoot = DbElement.SelectSingle(fixture.Connection, StructureKey: source.Key, ResourceFieldOrder: 0)!;
        sourceRoot.Id.ShouldBe("Quantity:simplequantity");
        sourceRoot.Path.ShouldBe("Quantity");
        DbElementComparison.SelectList(fixture.Connection, StructureComparisonKey: comparison.Key,
            SourceElementKey: sourceRoot.Key).ShouldHaveSingleItem().SourceElementId.ShouldBe(sourceRoot.Id);
        DbElement code = DbElement.SelectSingle(fixture.Connection, StructureKey: source.Key, Path: "Quantity.code")!;
        DbElementTypeComparison.SelectList(fixture.Connection, SourceElementKey: code.Key,
            TargetFhirPackageKey: pair.TargetPackageKey).ShouldHaveSingleItem().UserMessage.ShouldBe("Authored fallback authority");
        DbComparisonCache<DbElementTypeComparison> cache = new();
        new ElementTypeComparer(fixture.Connection, NullLoggerFactory.Instance, cache)
            .DoTypeComparisons(pair, sourceUse, targetUse, 991);
        DbElementTypeComparison occurrence = cache.ToAdd.ShouldHaveSingleItem();
        occurrence.NotMapped.ShouldBeFalse();
        occurrence.Relationship.ShouldBe(ConceptMap.ConceptMapRelationship.SourceIsNarrowerThanTarget);
        occurrence.UserMessage.ShouldBe("Selected primary occurrence authority");
        ProfileDecisionDocument decision = JsonSerializer.Deserialize<ProfileDecisionDocument>(occurrence.TechnicalMessage!)!;
        decision.ProfileDecisionFormat.ShouldBe(1);
        decision.Decisions.ShouldHaveSingleItem().Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompatibleCatalogIsRejectedBeforeComparisonReset(bool combined)
    {
        using CatalogImport imported = new(UseDefinitions());
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(imported.Connection, DbFhirPackage.SelectList(imported.Connection));
        using IDbCommand sentinel = imported.Connection.CreateCommand();
        sentinel.CommandText = $"CREATE TABLE {DbStructureComparison.DefaultTableName} (Key INTEGER PRIMARY KEY); " +
            $"INSERT INTO {DbStructureComparison.DefaultTableName} VALUES (197);";
        sentinel.ExecuteNonQuery();
        DbUnsafeCodeInputState previousInput = DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)!;
        string previousRevision = previousInput.CompletedInputRevision!;
        previousInput.FormatVersion = 1;
        DbUnsafeCodeInputState.Update(imported.Connection, previousInput);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            new FhirDbComparer(imported.Database, NullLoggerFactory.Instance).Compare(
                processValueSets: combined, processStructures: true));

        error.Message.ShouldContain("Reload");
        sentinel.CommandText = $"SELECT Key FROM {DbStructureComparison.DefaultTableName}";
        Convert.ToInt32(sentinel.ExecuteScalar()).ShouldBe(197);
        DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)!.CompletedInputRevision.ShouldBe(previousRevision);
    }

    [Fact]
    public void MapReloadCannotRepairAnOldCatalog()
    {
        using CatalogImport imported = new(UseDefinitions());
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(imported.Connection, packages);
        DbUnsafeCodeInputState previousInput = DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)!;
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(imported.Connection)!;
        state.FormatVersion = 0;
        DbDefinitionInventoryState.Update(imported.Connection, state);
        string source = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "map-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            using MappingLoader loader = new(imported.Connection, NullLoggerFactory.Instance);
            Should.Throw<InvalidOperationException>(() => loader.TryLoadCrossVersionSourceMaps(source, false))
                .Message.ShouldContain("Reload the database");
            DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)!.CompletedInputRevision.ShouldBe(previousInput.CompletedInputRevision);
            DefinitionInventoryStore.ReadState(imported.Connection)!.FormatVersion.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void PreparatoryNativeOnlyPolicyCannotBindCurrentMapInputsOrResetState()
    {
        using CatalogImport imported = new(UseDefinitions());
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        List<DbFhirPackage> packages = DbFhirPackage.SelectList(imported.Connection);
        DbStructureDefinition.SelectList(imported.Connection, ArtifactClass: FhirArtifactClassEnum.Profile).ShouldBeEmpty();
        DefinitionInventoryPackageCounts[] expected = [new(imported.PackageKey, 0, 1, 1, 0, 10, 10, 0)];
        DefinitionInventoryStore.ReadState(imported.Connection)!.ExpectedPackageCountsJson
            .ShouldBe(JsonSerializer.Serialize(expected));
        DbMappingClasses.CreateTables(imported.Connection);
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(imported.Connection, packages);
        DbUnsafeCodeInputState input = DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)!;
        input.FormatVersion.ShouldBe(2);
        DbMappingSourceFile.Insert(imported.Connection, new DbMappingSourceFile
        {
            Key = 197, Filename = "preparatory.json", SourceRelativeFilename = "input/elements/preparatory.json",
            IsConceptMap = true, IsFml = false, Url = "urn:preparatory-map",
        }, insertPrimaryKey: true);
        using IDbCommand sentinel = imported.Connection.CreateCommand();
        sentinel.CommandText = $"CREATE TABLE {DbStructureComparison.DefaultTableName} (Key INTEGER PRIMARY KEY); " +
            $"INSERT INTO {DbStructureComparison.DefaultTableName} VALUES (197);";
        sentinel.ExecuteNonQuery();
        DbDefinitionInventoryState preparatory = new()
        {
            Key = 1, FormatVersion = 1, PolicyVersion = 1,
            StartedRevision = "completed-native-only-preparatory-import",
            CompletedRevision = "completed-native-only-preparatory-import",
            ExpectedPackageCountsJson = JsonSerializer.Serialize(expected),
            CompletedMapInventoryBindingsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [input.SnapshotId] = "completed-native-only-preparatory-import",
            }),
        };
        DbDefinitionInventoryState.Update(imported.Connection, preparatory);
        string previousInput = JsonSerializer.Serialize(input);
        string previousInventory = JsonSerializer.Serialize(DefinitionInventoryStore.ReadState(imported.Connection));
        string previousMappings = JsonSerializer.Serialize(DbMappingSourceFile.SelectList(imported.Connection));
        string source = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "preparatory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        try
        {
            MappingLoader loader = new(imported.Connection, NullLoggerFactory.Instance);
            AssertRefused(() => loader.TryLoadCrossVersionSourceMaps(source, false));
            AssertRefused(() => UnsafeCodeInputStore.BeginInputs(imported.Connection, "source", source));
            AssertRefused(() => UnsafeCodeInputStore.RequireInputs(imported.Connection));
            AssertRefused(() => DefinitionInventoryStore.BeginMapInputs(imported.Connection, "new-input", preparatory.StartedRevision));
            AssertRefused(() => DefinitionInventoryStore.CompleteInventory(imported.Connection, preparatory.StartedRevision));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }

        void AssertRefused(Action action)
        {
            InvalidOperationException error = Should.Throw<InvalidOperationException>(action);
            error.Message.ShouldContain("Reload the database, load maps, compare, generate outcomes, then export");
            JsonSerializer.Serialize(DbUnsafeCodeInputState.SelectSingle(imported.Connection, Key: 1)).ShouldBe(previousInput);
            JsonSerializer.Serialize(DefinitionInventoryStore.ReadState(imported.Connection)).ShouldBe(previousInventory);
            JsonSerializer.Serialize(DbMappingSourceFile.SelectList(imported.Connection)).ShouldBe(previousMappings);
            sentinel.CommandText = $"SELECT Key FROM {DbStructureComparison.DefaultTableName}";
            Convert.ToInt32(sentinel.ExecuteScalar()).ShouldBe(197);
        }
    }

    [Fact]
    public void IgPreparationIsWriteFreeAndRetainsTrackingState()
    {
        using CatalogImport imported = new(UseDefinitions());
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        DbFhirPackage package = DbFhirPackage.SelectList(imported.Connection).ShouldHaveSingleItem();
        string output = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "prepared-" + Guid.NewGuid().ToString("N"));
        ConfigXVer config = new() { OutputDirectory = output };
        XVerExporter exporter = new(imported.Connection, config);
        IgExporter igExporter = new(imported.Connection, NullLoggerFactory.Instance, output, null, exporter, config);
        IgExporter.ExportSchedule schedule = new([new(package, package)], [package]);
        IgExporter.XVerExportTrackingRecord tracking = igExporter.PrepareXVerIgs(schedule);
        IgExporter.XVerIgExportTrackingRecord pair = tracking.XVerIgs.ShouldHaveSingleItem();
        pair.GetName("Shared", "first").name.ShouldBe("Shared");
        pair.EdOutcomeMapTargets.Add(197, [new("http://example.org/StructureDefinition/emitted", "Quantity.code")]);
        Directory.Exists(output).ShouldBeFalse();
        try
        {
            igExporter.MaterializeXVerIgs(tracking, includeScripts: false);
            tracking.XVerIgs.ShouldHaveSingleItem().ShouldBeSameAs(pair);
            pair.GetName("Shared", "second").name.ShouldBe("Second");
            pair.EdOutcomeMapTargets[197].ShouldHaveSingleItem().elementPath.ShouldBe("Quantity.code");
            Directory.Exists(pair.ProfileDir).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void NormalizedFactsRoundTripConstraintsAndExactRootIds()
    {
        using SqliteConnection connection = OpenDatabase(foreignKeys: true);
        DbContentClasses.CreateTables(connection);
        DbMappingClasses.CreateTables(connection);
        DbFhirPackage package = Package(37);
        DbFhirPackage.Insert(connection, package, insertPrimaryKey: true).ShouldBe(37);
        StructureDefinition profile = new FhirJsonParser().Parse<StructureDefinition>(SimpleQuantityJson);
        StructureDefinition native = new FhirJsonParser().Parse<StructureDefinition>("""
            {
              "resourceType": "StructureDefinition",
              "id": "Quantity",
              "url": "http://hl7.org/fhir/StructureDefinition/Quantity",
              "version": "3.0.2",
              "name": "Quantity",
              "status": "active",
              "kind": "complex-type",
              "abstract": false,
              "type": "Quantity",
              "derivation": "specialization",
              "snapshot": {"element": [{"id": "Quantity", "path": "Quantity", "min": 0, "max": "*"}]}
            }
            """);
        StructureDefinition differentialOnly = (StructureDefinition)profile.DeepCopy();
        differentialOnly.Id = "DifferentialQuantity";
        differentialOnly.Url = "http://example.org/StructureDefinition/DifferentialQuantity";
        differentialOnly.Version = "profile-7";
        differentialOnly.Snapshot = null;
        differentialOnly.Type = null;
        StructureDefinition[] definitions = [profile, native, differentialOnly];
        int[] structureKeys = [901, 7, 100];
        int[] factsKeys = [100, 901, 7];
        List<DbStructureDefinitionFacts> facts = [];
        for (int index = 0; index < definitions.Length; index++)
        {
            DbStructureDefinition structure = new TestStructure(package, structureKeys[index], definitions[index]);
            DbStructureDefinition.Insert(connection, structure, insertPrimaryKey: true).ShouldBe(structureKeys[index]);
            facts.Add(StructureDefinitionFactsStore.CreateFacts(factsKeys[index], package.Key, structure.Key, definitions[index]));
        }

        DbStructureDefinitionFacts.Insert(connection, (IEnumerable<DbStructureDefinitionFacts>)facts, insertPrimaryKey: true);
        facts.Select(row => row.Key).ShouldBe(factsKeys);
        Dictionary<int, DbStructureDefinitionFacts> persisted = DbStructureDefinitionFacts.SelectDict(connection);
        persisted.Keys.Order().ShouldBe([7, 100, 901]);
        for (int index = 0; index < definitions.Length; index++)
        {
            DbStructureDefinitionFacts row = persisted[factsKeys[index]];
            row.StructureKey.ShouldBe(structureKeys[index]);
            row.FhirPackageKey.ShouldBe(37);
            StructureDefinition recovered = StructureDefinitionFactsStore.ReadDefinition(row);
            recovered.ToJson().ShouldBe(definitions[index].ToJson());
        }

        DbStructureDefinitionFacts profileFacts = persisted[100];
        profileFacts.Type.ShouldBe("Quantity");
        profileFacts.Kind.ShouldBe("complex-type");
        profileFacts.Derivation.ShouldBe("constraint");
        profileFacts.BaseDefinition.ShouldBe(native.Url);
        profileFacts.SnapshotAvailability.ShouldBe(DefinitionSnapshotAvailability.Available);
        using JsonDocument payload = JsonDocument.Parse(profileFacts.StructureDefinitionJson);
        JsonElement snapshot = payload.RootElement.GetProperty("snapshot").GetProperty("element");
        snapshot[0].GetProperty("id").GetString().ShouldBe("Quantity:simplequantity");
        snapshot[0].GetProperty("path").GetString().ShouldBe("Quantity");
        snapshot[0].GetProperty("constraint")[0].GetProperty("key").GetString().ShouldBe("sqty-1");
        snapshot[1].GetProperty("max").GetString().ShouldBe("0");
        snapshot[2].GetProperty("fixedUri").GetString().ShouldBe("http://unitsofmeasure.org");
        snapshot[3].GetProperty("patternCode").GetString().ShouldBe("mg");
        snapshot[3].GetProperty("binding").GetProperty("valueSet").GetString().ShouldBe("http://example.org/ValueSet/units|units-1");
        snapshot[4].GetProperty("slicing").GetProperty("rules").GetString().ShouldBe("closed");
        snapshot[5].GetProperty("id").GetString().ShouldBe("Quantity:simplequantity.extension:units");
        StructureDefinitionFactsStore.ReadDefinition(persisted[901]).Snapshot.Element[0].ElementId.ShouldBe("Quantity");
        persisted[7].SnapshotAvailability.ShouldBe(DefinitionSnapshotAvailability.Missing);
        persisted[7].Type.ShouldBeNull();
        StructureDefinition recoveredDifferential = StructureDefinitionFactsStore.ReadDefinition(persisted[7]);
        recoveredDifferential.Snapshot.ShouldBeNull();
        recoveredDifferential.Differential.Element.Count.ShouldBe(5);

        AddOwnerAssociations(connection, package);
        List<DbProfileMapOwnerFacts> owners = DbProfileMapOwnerFacts.SelectList(connection,
            AnnotationKey: 503, ResolutionState: ProfileMapOwnerResolution.Resolved, orderByProperties: ["Key"]);
        owners.Select(row => row.Key).ShouldBe([11, 700]);
        owners.Select(row => row.ElementMappingKey).ShouldBe([401, 9]);
        foreach (DbProfileMapOwnerFacts owner in owners)
        {
            owner.SourceStructureKey.ShouldBe(901);
            owner.TargetStructureKey.ShouldBe(7);
            owner.GroupSourceCanonical.ShouldBe(profile.Url + "|3.0.2");
            owner.GroupSourceVersion.ShouldBe("3.0.2");
            owner.GroupTargetCanonical.ShouldBe(native.Url + "|3.0.2");
            owner.GroupTargetVersion.ShouldBe("3.0.2");
            owner.InputSnapshotId.ShouldBe("input-1");
            owner.SourceOwnerContext.ShouldBe("SimpleQuantity");
            owner.TargetOwnerContext.ShouldBe("Quantity");
            owner.SourceRelativeFilename.ShouldBe("input/elements/quantity.json");
            owner.ConceptMapId.ShouldBe("quantity-map");
            owner.ConceptMapUrl.ShouldBe("http://example.org/ConceptMap/quantity");
            owner.MappingSourceFileKey.ShouldBe(81);
            owner.GroupIndex.ShouldBe(2);
            owner.SourceIndex.ShouldBe(3);
            owner.TargetIndex.ShouldBe(4);
            owner.ResolutionState.ShouldBe(ProfileMapOwnerResolution.Resolved);
        }

        DbStructureDefinitionFacts invalid = StructureDefinitionFactsStore.CreateFacts(999, 37, 999, profile);
        Should.Throw<SqliteException>(() => DbStructureDefinitionFacts.Insert(connection, invalid, insertPrimaryKey: true))
            .SqliteErrorCode.ShouldBe(19);
        DbProfileMapOwnerFacts invalidOwner = OwnerFacts(999, 999);
        Should.Throw<SqliteException>(() => DbProfileMapOwnerFacts.Insert(connection, invalidOwner, insertPrimaryKey: true))
            .SqliteErrorCode.ShouldBe(19);
        DbContentClasses.LoadIndices(connection);
        DbMappingClasses.LoadIndices(connection);
        DbStructureDefinitionFacts.GetIndex().ShouldBe(902);
        DbProfileMapOwnerFacts.GetIndex().ShouldBe(701);
    }

    [Fact]
    public void ContentAndMappingResetsInvalidateOnlyTheirOwnedFacts()
    {
        using SqliteConnection connection = OpenDatabase();
        DbContentClasses.CreateTables(connection);
        DbMappingClasses.CreateTables(connection);
        StructureDefinition profile = new FhirJsonParser().Parse<StructureDefinition>(SimpleQuantityJson);
        profile.Snapshot = null;
        profile.Differential = null;
        profile.Type = null;
        DbFhirPackage package = Package(37);
        DbFhirPackage.Insert(connection, package, insertPrimaryKey: true);
        DbStructureDefinition.Insert(connection, new TestStructure(package, 901, profile), insertPrimaryKey: true);
        DbStructureDefinitionFacts facts = StructureDefinitionFactsStore.CreateFacts(100, 37, 901, profile);
        DbStructureDefinitionFacts.Insert(connection, facts, insertPrimaryKey: true);
        DbProfileMapOwnerFacts.Insert(connection, OwnerFacts(700, 9), insertPrimaryKey: true);
        DefinitionInventoryPackageCounts[] expected = [new(37, 0, 0, 0, 1, 0, 0, 0)];
        string revision = DefinitionInventoryStore.BeginInventory(connection, expected);
        DefinitionInventoryStore.ReadState(connection)!.CompletedRevision.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.CompleteMapInputs(connection, "input-1", revision));
        DefinitionInventoryStore.CompleteInventory(connection, revision);
        DefinitionInventoryStore.BeginMapInputs(connection, "input-1", revision);
        DefinitionInventoryStore.CompleteMapInputs(connection, "input-1", revision);
        DefinitionInventoryStore.BeginMapInputs(connection, "pending-input", revision);
        SeedDownstreamCompletion(connection);
        DbDefinitionInventoryState completed = DefinitionInventoryStore.ReadState(connection)!;
        completed.Key.ShouldBe(1);
        completed.FormatVersion.ShouldBe(DefinitionInventoryStore.FormatVersion);
        completed.PolicyVersion.ShouldBe(DefinitionInventoryStore.PolicyVersion);
        JsonSerializer.Deserialize<DefinitionInventoryPackageCounts[]>(completed.ExpectedPackageCountsJson).ShouldBe(expected);
        JsonSerializer.Deserialize<Dictionary<string, string>>(completed.CompletedMapInventoryBindingsJson)!
            .ShouldContainKeyAndValue("input-1", revision);
        JsonSerializer.Deserialize<Dictionary<string, string>>(completed.PendingMapInventoryBindingsJson)!
            .ShouldContainKeyAndValue("pending-input", revision);

        DbMappingClasses.DropTables(connection);
        DbMappingClasses.CreateTables(connection);
        DbProfileMapOwnerFacts.SelectList(connection).ShouldBeEmpty();
        DbStructureDefinitionFacts.SelectSingle(connection, Key: 100)!.StructureDefinitionJson.ShouldBe(facts.StructureDefinitionJson);
        DbDefinitionInventoryState afterMaps = DefinitionInventoryStore.ReadState(connection)!;
        afterMaps.CompletedRevision.ShouldBe(revision);
        afterMaps.StartedRevision.ShouldBe(revision);
        afterMaps.ExpectedPackageCountsJson.ShouldBe(completed.ExpectedPackageCountsJson);
        afterMaps.PendingMapInventoryBindingsJson.ShouldBe("{}");
        afterMaps.CompletedMapInventoryBindingsJson.ShouldBe("{}");

        DbProfileMapOwnerFacts.Insert(connection, OwnerFacts(700, 9), insertPrimaryKey: true);
        DefinitionInventoryStore.BeginMapInputs(connection, "input-2", revision);
        DefinitionInventoryStore.CompleteMapInputs(connection, "input-2", revision);
        SeedDownstreamCompletion(connection);
        DbContentClasses.DropTables(connection, forPackages: false, forTerminologies: false, forStructures: false, forSupport: true);
        DbContentClasses.CreateTables(connection, forPackages: false, forTerminologies: false, forStructures: false, forSupport: true);
        DbStructureDefinitionFacts.SelectList(connection).ShouldHaveSingleItem().Key.ShouldBe(100);
        DbProfileMapOwnerFacts.SelectList(connection).ShouldHaveSingleItem().Key.ShouldBe(700);
        DbDefinitionInventoryState afterContent = DefinitionInventoryStore.ReadState(connection)!;
        afterContent.CompletedRevision.ShouldBeNull();
        afterContent.PendingMapInventoryBindingsJson.ShouldBe("{}");
        afterContent.CompletedMapInventoryBindingsJson.ShouldBe("{}");
        DbUnsafeCodeInputState.SelectSingle(connection, Key: 1)!.CompletedInputRevision.ShouldBeNull();
        DbUnsafeCodePairState pair = DbUnsafeCodePairState.SelectSingle(connection, Key: 1)!;
        pair.InputRevision.ShouldBeNull();
        pair.ComparisonRevision.ShouldBeNull();
        pair.OutcomeInputRevision.ShouldBeNull();
        pair.OutcomeComparisonRevision.ShouldBeNull();
        pair.OutcomeFormatVersion.ShouldBeNull();

        string replacement = DefinitionInventoryStore.BeginInventory(connection, expected);
        replacement.ShouldNotBe(revision);
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.CompleteInventory(connection, revision));
        DefinitionInventoryStore.CompleteInventory(connection, replacement);
        DbContentClasses.DropTables(connection, forPackages: false, forTerminologies: false, forStructures: true, forSupport: false);
        DbContentClasses.CreateTables(connection, forPackages: false, forTerminologies: false, forStructures: true, forSupport: false);
        DbStructureDefinitionFacts.SelectList(connection).ShouldBeEmpty();
        DefinitionInventoryStore.ReadState(connection).ShouldBeNull();
        DbProfileMapOwnerFacts.SelectList(connection).ShouldHaveSingleItem().Key.ShouldBe(700);
        DbContentClasses.LoadIndices(connection);
        DbStructureDefinitionFacts.GetIndex().ShouldBe(1);
        DbDefinitionInventoryState.GetIndex().ShouldBe(1);
        DbMappingClasses.DropTables(connection);
        DbMappingClasses.CreateTables(connection);
        DbMappingClasses.LoadIndices(connection);
        DbProfileMapOwnerFacts.GetIndex().ShouldBe(1);
    }

    [Fact]
    public void InventoryReadsDoNotCreateSchemaOrHideInvalidState()
    {
        using SqliteConnection connection = OpenDatabase();
        Should.Throw<SqliteException>(() => DefinitionInventoryStore.ReadState(connection));
        DefinitionInventoryStore.TableExists(connection, DbDefinitionInventoryState.DefaultTableName).ShouldBeFalse();
        DefinitionInventoryStore.InvalidateCompletion(connection);
        DefinitionInventoryStore.InvalidateMapInputs(connection);
        StructureDefinitionFactsStore.LoadContentIndices(connection);
        StructureDefinitionFactsStore.LoadMappingIndices(connection);
        DefinitionInventoryStore.TableExists(connection, DbDefinitionInventoryState.DefaultTableName).ShouldBeFalse();
        DbDefinitionInventoryState.CreateTable(connection);
        DbDefinitionInventoryState.Insert(connection, new DbDefinitionInventoryState
        {
            Key = 7,
            FormatVersion = 1,
            PolicyVersion = 1,
            StartedRevision = "invalid",
            ExpectedPackageCountsJson = "[]",
        }, insertPrimaryKey: true);
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.ReadState(connection));
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.InvalidateCompletion(connection));
        DbDefinitionInventoryState.DropTable(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE DefinitionInventoryState (Key INTEGER PRIMARY KEY)";
        command.ExecuteNonQuery();
        Should.Throw<SqliteException>(() => DefinitionInventoryStore.ReadState(connection));
        Should.Throw<SqliteException>(() => DefinitionInventoryStore.InvalidateMapInputs(connection));
    }

    [Fact]
    public void FullImportRetainsAllProfilesWithoutPromotingThem()
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        AddNative(definitions, Definition("Age", "Age", baseDefinition: QuantityUrl));
        AddNative(definitions, Definition("Money", "Money"));
        StructureDefinition quantity = Profile("EligibleQuantity", "Quantity");
        StructureDefinition onAge = Profile("OnAge", "Age", baseDefinition: QuantityUrl.Replace("Quantity", "Age"));
        StructureDefinition coding = Profile("PassiveCoding", "Coding", baseDefinition: "http://example.org/missing");
        StructureDefinition resource = Profile("PassiveResource", "Patient", baseDefinition: "http://example.org/missing");
        resource.Kind = StructureDefinition.StructureDefinitionKind.Resource;
        StructureDefinition excluded = Profile("ExcludedQuantity", "Quantity");
        excluded.Snapshot.Element.Add(new ElementDefinition
        {
            ElementId = "Quantity.code", Path = "Quantity.code", Min = 0, Max = "1",
            Type = [new() { Code = "code" }],
            Binding = new()
            {
                Strength = Hl7.Fhir.Model.BindingStrength.Required,
                ValueSet = "http://example.org/ValueSet/units|1",
                Additional = [new() { Purpose = ElementDefinition.AdditionalBindingPurposeVS.Maximum, ValueSet = "http://example.org/ValueSet/units|2" }],
            },
        });
        StructureDefinition differential = Profile("DifferentialOnly", null);
        differential.Differential = new() { Element = [new() { ElementId = "Quantity.code", Path = "Quantity.code", Max = "0" }] };
        differential.Snapshot = null;
        foreach (StructureDefinition profile in new[] { quantity, onAge, coding, resource, excluded, differential })
        {
            AddProfile(definitions, profile);
        }

        StructureDefinition extension = Profile("NotImportedExtension", "Extension");
        definitions.AddExtension(extension, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        StructureDefinition logical = Definition("NotImportedLogical", "NotImportedLogical");
        definitions.AddLogicalModel(logical, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        HashSet<string> exclusions = [excluded.Url, QuantityUrl.Replace("Quantity", "Money")];
        using CatalogImport full = new(definitions);
        full.Database.TryLoadFromDefinitionCollections(exclusions, []).ShouldBeTrue();
        XVerStructureCatalog catalog = full.Catalog;
        catalog.GetDefinitions(full.PackageKey).Count.ShouldBe(9);
        catalog.GetDefinitions(full.PackageKey).Count(row => row.ArtifactClass == FhirArtifactClassEnum.Profile).ShouldBe(6);
        catalog.GetProcessedStructures(full.PackageKey).Select(row => row.Id).Order().ShouldBe(["Age", "EligibleQuantity", "Quantity"]);
        DbStructureDefinition passive = catalog.ResolveDefinition(full.PackageKey, differential.Url)!;
        DbStructureDefinitionFacts facts = DbStructureDefinitionFacts.SelectSingle(full.Connection, StructureKey: passive.Key)!;
        facts.SnapshotAvailability.ShouldBe(DefinitionSnapshotAvailability.Missing);
        facts.Type.ShouldBeNull();
        StructureDefinitionFactsStore.ReadDefinition(facts).Snapshot.ShouldBeNull();
        DbElement delta = DbElement.SelectList(full.Connection, StructureKey: passive.Key).ShouldHaveSingleItem();
        delta.Id.ShouldBe("Quantity.code");
        delta.ParentElementKey.ShouldBeNull();
        DbElementType.SelectList(full.Connection, StructureKey: passive.Key).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => catalog.ResolveRoot(passive)).Message.ShouldContain("snapshot");
        DbStructureDefinition excludedOwner = catalog.ResolveDefinition(full.PackageKey, excluded.Url)!;
        DbElementAdditionalBinding.SelectList(full.Connection, StructureKey: excludedOwner.Key).ShouldHaveSingleItem();
        DbElementType.SelectList(full.Connection, StructureKey: excludedOwner.Key).Count.ShouldBe(2);
        DbStructureDefinitionFacts.SelectList(full.Connection).Count.ShouldBe(9);
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(full.Connection)!;
        state.CompletedRevision.ShouldBe(state.StartedRevision);
        catalog.ResolveImportLinks(full.Connection, validateProcessedRoots: true);
        catalog.Diagnostics.ShouldContain(message => message.Contains("missing", StringComparison.Ordinal));

        DefinitionCollection r2 = Definitions(FhirReleases.FhirSequenceCodes.DSTU2);
        AddNative(r2, NativeQuantity());
        StructureDefinition derivative = Profile("Age", "Quantity");
        r2.AddComplexType(derivative, r2.FhirSequence, r2.MainPackageId, r2.MainPackageVersion);
        r2.ComplexTypesByName.ContainsKey("Age").ShouldBeFalse();
        r2.ProfilesByUrl.ContainsKey(derivative.Url).ShouldBeTrue();
        using CatalogImport dstu2 = new(r2);
        dstu2.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        dstu2.Catalog.GetProcessedStructures(dstu2.PackageKey).Select(row => row.Id).Order().ShouldBe(["Age", "Quantity"]);
    }

    [Fact]
    public void PassiveBindingsDoNotChangeUnrelatedTerminologyRetention()
    {
        using CatalogImport baseline = new(TerminologyDefinitions(passiveBindings: false));
        using CatalogImport withPassive = new(TerminologyDefinitions(passiveBindings: true));
        foreach (CatalogImport imported in new[] { baseline, withPassive })
        {
            imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
            DbCodeSystem.SelectList(imported.Connection).Select(row => row.Id).Order()
                .ShouldBe(["eligible", "native", "native-additional", "other-tho", "outside"]);
            DbValueSet.SelectList(imported.Connection).Count.ShouldBe(10);
            AssertPrunedTerminologyDependencies(imported);
            DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldNotBeNullOrEmpty();
        }
        DbElement.SelectList(withPassive.Connection, BindingValueSet: TerminologyValueSet("passive")).ShouldHaveSingleItem();
        DbElementAdditionalBinding.SelectList(withPassive.Connection, BindingValueSet: TerminologyValueSet("passive-additional"))
            .ShouldHaveSingleItem();
        BuildVocabularySchedule(withPassive).ShouldBe(BuildVocabularySchedule(baseline));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("target")]
    [InlineData("content")]
    [InlineData("base")]
    public void ReferencedPassiveBindingsRetainInheritedAndAdditionalTerminology(string reference)
    {
        DefinitionCollection definitions = TerminologyDefinitions(passiveBindings: true, reference: reference);
        HashSet<string> exclusions = reference == "base" ? [PassiveTerminologyParentUrl] : [];
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections(exclusions, []).ShouldBeTrue();
        XVerStructureCatalog catalog = new(imported.Connection, exclusions);
        DbStructureDefinition passive = catalog.ResolveDefinition(imported.PackageKey, PassiveTerminologyUrl + "|passive-definition")!;
        DbStructureDefinition parent = catalog.ResolveDefinition(imported.PackageKey, PassiveTerminologyParentUrl + "|definition-1")!;
        if (reference == "base")
        {
            catalog.IsProcessed(parent).ShouldBeFalse();
            catalog.IsProcessed(passive).ShouldBeTrue();
        }
        else
        {
            catalog.IsProcessed(passive).ShouldBeFalse();
            catalog.IsProcessed(parent).ShouldBeFalse();
        }
        DbCodeSystem.SelectList(imported.Connection).Select(row => row.Id).Order().ShouldBe(
            ["eligible", "inherited", "inherited-additional", "native", "native-additional", "other-tho", "outside", "passive", "passive-additional"]);
        DbElement inherited = catalog.ResolveOwnedElement(passive, catalog.ResolveRoot(passive).Id + ".code")!;
        inherited.BaseStructureKey.ShouldBe(parent.Key);
        inherited.BindingValueSetKey.ShouldBeNull();
        foreach (DbElementAdditionalBinding binding in DbElementAdditionalBinding.SelectList(imported.Connection))
        {
            binding.BindingValueSetKey.ShouldBe(DbValueSet.SelectSingle(imported.Connection, VersionedUrl: binding.BindingValueSet)!.Key);
        }
        if (reference is "type" or "target")
        {
            DbElementType use = DbElementType.SelectList(imported.Connection).Single(type =>
                (reference == "type" ? type.TypeProfile : type.TargetProfile) == PassiveTerminologyUrl + "|passive-definition");
            (reference == "type" ? use.TypeProfileStructureKey : use.TargetProfileStructureKey).ShouldBe(passive.Key);
        }
        if (reference == "content")
        {
            DbStructureDefinition quantity = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
            DbElement use = catalog.ResolveOwnedElement(quantity, "Quantity.borrowed")!;
            use.ContentReferenceSourceKey.ShouldBe(inherited.Key);
            catalog.ResolveContentReference(use).StructureKey.ShouldBe(passive.Key);
        }
        AssertPrunedTerminologyDependencies(imported);
        foreach (string id in new[] { "passive", "passive-additional", "inherited", "inherited-additional" })
        {
            DbValueSet valueSet = DbValueSet.SelectSingle(imported.Connection, VersionedUrl: TerminologyValueSet(id))!;
            DbValueSetConcept concept = DbValueSetConcept.SelectList(imported.Connection, ValueSetKey: valueSet.Key).ShouldHaveSingleItem();
            concept.CodeSystemConceptKey.ShouldNotBeNull();
            DbCodeSystemConcept systemConcept = DbCodeSystemConcept.SelectSingle(imported.Connection, Key: concept.CodeSystemConceptKey!.Value)!;
            DbCodeSystem.SelectSingle(imported.Connection, Key: systemConcept.CodeSystemKey)!.UnversionedUrl.ShouldBe(TerminologySystem(id));
        }
        BuildVocabularySchedule(imported).ShouldContain("CodeSystem|" + TerminologySystem("passive") + "|terms-1");
    }

    [Fact]
    public void PassiveBindingReferencesDoNotSubstituteThePackageVersion()
    {
        DefinitionCollection definitions = TerminologyDefinitions(passiveBindings: true, reference: "type");
        ElementDefinition.TypeRefComponent use = definitions.ComplexTypesByName["Quantity"].Snapshot.Element
            .Single(element => element.Path == "Quantity.extension").Type[0];
        use.Profile = [PassiveTerminologyUrl + "|5.0.0"];
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        DbElementType stored = DbElementType.SelectSingle(imported.Connection, TypeProfile: use.Profile.Single())!;
        stored.TypeProfileStructureKey.ShouldBeNull();
        imported.Catalog.ResolveDefinition(imported.PackageKey, PassiveTerminologyUrl + "|passive-definition")!.Version.ShouldBe("passive-definition");
        DbCodeSystem.SelectList(imported.Connection).Select(row => row.Id).Order()
            .ShouldBe(["eligible", "native", "native-additional", "other-tho", "outside"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantityEligibilityUsesDeclaredTypeAndValidatedAncestry(bool reverse)
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        AddNative(definitions, Definition("Age", "Age", baseDefinition: QuantityUrl));
        AddNative(definitions, Definition("Money", "Money"));
        StructureDefinition parent = Profile("ParentQuantity", "Quantity");
        StructureDefinition child = Profile("ChildQuantity", "Quantity", baseDefinition: parent.Url);
        StructureDefinition onAge = Profile("ProfileOnAge", "Age", baseDefinition: QuantityUrl.Replace("Quantity", "Age"));
        StructureDefinition wrongCase = Profile("WrongCase", "quantity");
        StructureDefinition noType = Profile("Quantity", null);
        noType.Url = "http://example.org/no-declared-type";
        foreach (StructureDefinition profile in reverse ? new[] { noType, wrongCase, onAge, parent, child } : new[] { child, parent, onAge, wrongCase, noType })
        {
            AddProfile(definitions, profile);
        }

        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        imported.Catalog.GetProcessedStructures(imported.PackageKey).Select(row => row.UnversionedUrl).Order()
            .ShouldBe(new[] { QuantityUrl, QuantityUrl.Replace("Quantity", "Age"), QuantityUrl.Replace("Quantity", "Money"), parent.Url, child.Url }.Order());
        imported.Catalog.ResolveNativeType(imported.PackageKey, "Quantity")!.ArtifactClass.ShouldBe(FhirArtifactClassEnum.ComplexType);

        foreach (bool cycle in new[] { false, true })
        {
            DefinitionCollection invalid = Definitions();
            AddNative(invalid, NativeQuantity());
            StructureDefinition first = Profile("BrokenQuantity", "Quantity", baseDefinition: "http://example.org/missing");
            if (cycle)
            {
                StructureDefinition second = Profile("CycleQuantity", "Quantity", baseDefinition: first.Url);
                first.BaseDefinition = second.Url;
                AddProfile(invalid, second);
            }
            AddProfile(invalid, first);
            using CatalogImport failed = new(invalid);
            InvalidOperationException error = Should.Throw<InvalidOperationException>(() => failed.Database.TryLoadFromDefinitionCollections([], []));
            error.Message.ShouldContain(cycle ? "cyclic" : "missing");
            error.Message.ShouldContain(cycle ? "CycleQuantity" : first.Url);
            DefinitionInventoryStore.ReadState(failed.Connection)!.CompletedRevision.ShouldBeNull();
            DbStructureDefinitionFacts.SelectList(failed.Connection).Count.ShouldBe(cycle ? 3 : 2);
        }
    }

    [Fact]
    public void CanonicalVersionsAndForeignKeysResolveExactly()
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        StructureDefinition versionOne = Definition("SameId", "NativeOne", version: "definition-1");
        versionOne.Name = "NativeOne";
        versionOne.Url = "http://example.org/StructureDefinition/Versioned";
        StructureDefinition versionTwo = Definition("SameId", "NativeTwo", version: "definition-2");
        versionTwo.Name = "NativeTwo";
        versionTwo.Url = versionOne.Url;
        AddNative(definitions, versionTwo);
        AddNative(definitions, versionOne);
        StructureDefinition holder = Profile("Holder", "Coding", baseDefinition: "http://example.org/missing");
        holder.Snapshot.Element.Add(new ElementDefinition
        {
            ElementId = "Coding.value", Path = "Coding.value", Min = 0, Max = "1",
            Type = [new()
            {
                Code = "NativeOne",
                Profile = [versionOne.Url + "|definition-1"],
                TargetProfile = [versionTwo.Url + "|definition-2"],
            }],
        });
        AddProfile(definitions, holder);
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition one = catalog.ResolveDefinition(imported.PackageKey, versionOne.Url + "|definition-1")!;
        DbStructureDefinition two = catalog.ResolveDefinition(imported.PackageKey, versionTwo.Url + "|definition-2")!;
        Should.Throw<InvalidOperationException>(() => catalog.ResolveDefinition(imported.PackageKey, versionOne.Url)).Message.ShouldContain("ambiguous");
        Should.Throw<InvalidOperationException>(() => catalog.ResolveDefinition(imported.PackageKey, versionOne.Url + "|4.0.1")).Message.ShouldContain("missing");
        Should.Throw<InvalidOperationException>(() => catalog.ResolveDefinition(imported.PackageKey, versionOne.Url.ToUpperInvariant() + "|definition-1"));
        Should.Throw<InvalidOperationException>(() => catalog.ResolveDefinition(imported.PackageKey + 1, versionOne.Url + "|definition-1", one.Key));
        DbElementType use = DbElementType.SelectSingle(imported.Connection, TypeName: "NativeOne", TypeProfile: versionOne.Url + "|definition-1")!;
        use.TypeStructureKey.ShouldBe(one.Key);
        use.TypeProfileStructureKey.ShouldBe(one.Key);
        use.TargetProfileStructureKey.ShouldBe(two.Key);
        catalog.ResolveTypeUse(use).Key.ShouldBe(one.Key);
        use.TypeProfileStructureKey = two.Key;
        Should.Throw<InvalidOperationException>(() => catalog.ResolveTypeUse(use)).Message.ShouldContain("foreign key");
        use.TypeProfileStructureKey = null;
        use.TypeProfile = versionOne.Url + "|4.0.1";
        Should.Throw<InvalidOperationException>(() => catalog.ResolveTypeUse(use)).Message.ShouldContain("missing");
        use.TypeProfile = "http://example.org/not-imported/NativeOne";
        Should.Throw<InvalidOperationException>(() => catalog.ResolveTypeUse(use)).Message.ShouldContain("not-imported");
        use.TypeProfile = versionOne.Url + "|definition-1";
        use.TypeProfileStructureKey = two.Key;
        DbElementType.Update(imported.Connection, use);
        Should.Throw<InvalidOperationException>(() => imported.Connection.UpdateElementTypeProfileStructureKeys(imported.PackageKey)).Message.ShouldContain("foreign key");
        use.TypeProfileStructureKey = null;
        DbElementType.Update(imported.Connection, use);
        using IDbCommand copy = imported.Connection.CreateCommand();
        copy.CommandText = "CREATE TABLE AlternateTypes AS SELECT * FROM ElementTypes; CREATE TABLE AlternateStructures AS SELECT * FROM Structures";
        copy.ExecuteNonQuery();
        imported.Connection.UpdateElementTypeProfileStructureKeys(imported.PackageKey, "AlternateTypes", "AlternateStructures").ShouldBe(1);
        DbElementType.SelectSingle(imported.Connection, dbTableName: "AlternateTypes", Key: use.Key)!.TypeProfileStructureKey.ShouldBe(one.Key);
        DbElementType.SelectSingle(imported.Connection, Key: use.Key)!.TypeProfileStructureKey.ShouldBeNull();
        use.TypeProfileStructureKey = one.Key;
        use.TypeStructureKey = two.Key;
        Should.Throw<InvalidOperationException>(() => catalog.ResolveNativeType(imported.PackageKey, use.TypeName, use.TypeStructureKey))
            .Message.ShouldContain("foreign key");
    }

    [Fact]
    public void SlicedRootAndContentReferencesStayWithTheirOwner()
    {
        DefinitionCollection definitions = Definitions(FhirReleases.FhirSequenceCodes.STU3);
        AddNative(definitions, NativeQuantity());
        StructureDefinition profile = new FhirJsonParser().Parse<StructureDefinition>(SimpleQuantityJson);
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity:simplequantity.first", Path = "Quantity.first", ContentReference = "#Quantity:simplequantity.second" });
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity:simplequantity.second", Path = "Quantity.second", Min = 0, Max = "1", Type = [new() { Code = "code" }] });
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity:simplequantity.external", Path = "Quantity.external", ContentReference = QuantityUrl + "|definition-1#Quantity.code" });
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity:simplequantity.extension:units.url", Path = "Quantity.extension.url", Type = [new() { Code = "uri" }] });
        profile.Snapshot.Element[3].Base = new() { Path = "Quantity.code", Min = 0, Max = "1" };
        AddProfile(definitions, profile);
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        DbStructureDefinition native = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        DbElement root = catalog.ResolveRoot(owner);
        root.Id.ShouldBe("Quantity:simplequantity");
        root.Path.ShouldBe("Quantity");
        root.ChildElementCount.ShouldBe(8);
        DbElementType.SelectList(imported.Connection, ElementKey: root.Key).ShouldHaveSingleItem().TypeName.ShouldBe("Quantity");
        DbElement first = catalog.ResolveOwnedElement(owner, "Quantity:simplequantity.first")!;
        DbElement second = catalog.ResolveOwnedElement(owner, "Quantity:simplequantity.second")!;
        first.ContentReferenceSourceKey.ShouldBe(second.Key);
        second.UsedAsContentReference.ShouldBe(true);
        first.ParentElementKey.ShouldBe(root.Key);
        DbElement external = catalog.ResolveOwnedElement(owner, "Quantity:simplequantity.external")!;
        DbElement nativeCode = catalog.ResolveOwnedElement(native, "Quantity.code")!;
        external.ContentReferenceSourceKey.ShouldBe(nativeCode.Key);
        DbElement code = catalog.ResolveOwnedElement(owner, "Quantity:simplequantity.code")!;
        code.BaseElementKey.ShouldBe(nativeCode.Key);
        code.BaseStructureKey.ShouldBe(native.Key);
        code.BaseElementKey.ShouldNotBe(code.Key);
        DbElement slice = catalog.ResolveOwnedElement(owner, "Quantity:simplequantity.extension:units")!;
        slice.ChildElementCount.ShouldBe(1);
        catalog.ResolveOwnedElement(owner, slice.Id + ".url")!.ParentElementKey.ShouldBe(slice.Key);
        DbElementType.SelectList(imported.Connection, ElementKey: first.Key).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameElementIdsNeverResolveAcrossOwners(bool reverse)
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        StructureDefinition first = Profile("SharedId", "Quantity");
        first.Url = "http://example.org/profile/first";
        StructureDefinition second = Profile("SharedId", "Quantity");
        second.Url = "http://example.org/profile/second";
        foreach (StructureDefinition profile in new[] { first, second })
        {
            profile.Snapshot.Element.Add(new() { ElementId = "Quantity.code", Path = "Quantity.code", Type = [new() { Code = "code" }] });
            profile.Snapshot.Element.Add(new() { ElementId = "Quantity.ref", Path = "Quantity.ref", ContentReference = "#Quantity.code" });
        }
        foreach (StructureDefinition profile in reverse ? new[] { second, first } : new[] { first, second })
        {
            AddProfile(definitions, profile);
        }
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition firstOwner = catalog.ResolveDefinition(imported.PackageKey, first.Url)!;
        DbStructureDefinition secondOwner = catalog.ResolveDefinition(imported.PackageKey, second.Url)!;
        DbElement firstCode = catalog.ResolveOwnedElement(firstOwner, "Quantity.code")!;
        DbElement secondCode = catalog.ResolveOwnedElement(secondOwner, "Quantity.code")!;
        firstCode.Key.ShouldNotBe(secondCode.Key);
        catalog.ResolveOwnedElement(firstOwner, "Quantity.ref")!.ContentReferenceSourceKey.ShouldBe(firstCode.Key);
        catalog.ResolveOwnedElement(secondOwner, "Quantity.ref")!.ContentReferenceSourceKey.ShouldBe(secondCode.Key);
        Should.Throw<InvalidOperationException>(() => catalog.ResolveOwnedElement(firstOwner, "Quantity.code", secondCode.Key)).Message.ShouldContain(first.Url);
        Should.Throw<InvalidOperationException>(() => catalog.ResolveNativeType(imported.PackageKey, "SharedId")).Message.ShouldContain("missing");
        catalog.ResolveRoot(firstOwner).StructureKey.ShouldBe(firstOwner.Key);
        catalog.ResolveRoot(secondOwner).StructureKey.ShouldBe(secondOwner.Key);
        using IDbCommand corrupt = imported.Connection.CreateCommand();
        corrupt.CommandText = "CREATE TABLE AlternateElements AS SELECT * FROM Elements; UPDATE AlternateElements SET BaseElementKey = NULL, BaseStructureKey = NULL";
        corrupt.ExecuteNonQuery();
        imported.Connection.UpdateElementBaseKeys(imported.PackageKey, "AlternateElements").ShouldBeGreaterThan(0);
        DbElement.SelectSingle(imported.Connection, dbTableName: "AlternateElements", Key: firstCode.Key)!.BaseElementKey
            .ShouldBe(firstCode.BaseElementKey);
        corrupt.CommandText = $"UPDATE Elements SET ContentReferenceSourceKey = {secondCode.Key} WHERE StructureKey = {firstOwner.Key} AND Id = 'Quantity.ref'";
        corrupt.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(() => imported.Catalog.ResolveImportLinks(imported.Connection, true)).Message.ShouldContain("foreign key");
        corrupt.CommandText = $"UPDATE Elements SET ContentReferenceSourceKey = {firstCode.Key} WHERE StructureKey = {firstOwner.Key} AND Id = 'Quantity.ref'; UPDATE Elements SET BaseElementKey = {secondCode.Key}, BaseStructureKey = {secondOwner.Key} WHERE Key = {firstCode.Key}";
        corrupt.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(() => imported.Catalog.ResolveImportLinks(imported.Connection, true)).Message.ShouldContain("BaseDefinition ancestry");

        DefinitionCollection missing = Definitions();
        AddNative(missing, NativeQuantity());
        StructureDefinition missingOwner = Profile("MissingOwner", "Quantity");
        missingOwner.Snapshot.Element.Add(new() { ElementId = "Quantity.ref", Path = "Quantity.ref", ContentReference = "#Quantity.onlyElsewhere" });
        StructureDefinition otherOwner = Profile("OtherOwner", "Quantity");
        otherOwner.Snapshot.Element.Add(new() { ElementId = "Quantity.onlyElsewhere", Path = "Quantity.onlyElsewhere", Type = [new() { Code = "code" }] });
        AddProfile(missing, otherOwner);
        AddProfile(missing, missingOwner);
        using CatalogImport unresolved = new(missing);
        Should.Throw<InvalidOperationException>(() => unresolved.Database.TryLoadFromDefinitionCollections([], [])).Message.ShouldContain(missingOwner.Url);
        DefinitionInventoryStore.ReadState(unresolved.Connection)!.CompletedRevision.ShouldBeNull();
    }

    [Fact]
    public void NestedComponentBasePathsFollowExactParentDatatypeAncestry()
    {
        DefinitionCollection definitions = Definitions();
        StructureDefinition element = Definition("Element", "Element");
        element.Snapshot.Element.Add(new() { ElementId = "Element.id", Path = "Element.id", Type = [new() { Code = "string" }] });
        AddNative(definitions, element);
        StructureDefinition backbone = Definition("BackboneElement", "BackboneElement", baseDefinition: element.Url);
        backbone.Snapshot.Element.Add(new() { ElementId = "BackboneElement.modifierExtension", Path = "BackboneElement.modifierExtension", Type = [new() { Code = "Extension" }] });
        AddNative(definitions, backbone);
        StructureDefinition unrelated = Definition("Unrelated", "Unrelated");
        unrelated.Snapshot.Element.Add(new() { ElementId = "Unrelated.id", Path = "Unrelated.id", Type = [new() { Code = "string" }] });
        AddNative(definitions, unrelated);
        StructureDefinition resource = Definition("Resource", "Resource");
        StructureDefinition domainResource = Definition("DomainResource", "DomainResource", baseDefinition: resource.Url);
        StructureDefinition patient = Definition("Patient", "Patient", baseDefinition: domainResource.Url);
        foreach (string parentId in new[] { "Patient.contact", "Patient.contact:family" })
        {
            patient.Snapshot.Element.Add(new()
            {
                ElementId = parentId, Path = "Patient.contact", SliceName = parentId.Contains(':') ? "family" : null,
                Type = [new() { Code = "BackboneElement" }],
            });
            patient.Snapshot.Element.Add(new()
            {
                ElementId = parentId + ".id", Path = "Patient.contact.id",
                Base = new() { Path = "Element.id", Min = 0, Max = "1" },
                Type = [new() { Code = "string" }],
            });
            patient.Snapshot.Element.Add(new()
            {
                ElementId = parentId + ".modifierExtension", Path = "Patient.contact.modifierExtension",
                Base = new() { Path = "BackboneElement.modifierExtension", Min = 0, Max = "*" },
                Type = [new() { Code = "Extension" }],
            });
        }
        foreach (StructureDefinition definition in new[] { patient, domainResource, resource })
        {
            definition.Kind = StructureDefinition.StructureDefinitionKind.Resource;
            definitions.AddStructureDefinition(definition, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        }

        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition owner = catalog.ResolveNativeType(imported.PackageKey, "Patient")!;
        DbStructureDefinition elementOwner = catalog.ResolveNativeType(imported.PackageKey, "Element")!;
        DbStructureDefinition backboneOwner = catalog.ResolveNativeType(imported.PackageKey, "BackboneElement")!;
        catalog.GetAncestors(owner).Select(row => row.Id).ShouldBe(["DomainResource", "Resource"]);
        foreach (string parentId in new[] { "Patient.contact", "Patient.contact:family" })
        {
            DbElement parent = catalog.ResolveOwnedElement(owner, parentId)!;
            DbElement id = catalog.ResolveOwnedElement(owner, parentId + ".id")!;
            id.ParentElementKey.ShouldBe(parent.Key);
            id.BaseElementKey.ShouldBe(catalog.ResolveOwnedElement(elementOwner, "Element.id")!.Key);
            id.BaseStructureKey.ShouldBe(elementOwner.Key);
            catalog.ResolveOwnedElement(owner, parentId + ".modifierExtension")!.BaseElementKey
                .ShouldBe(catalog.ResolveOwnedElement(backboneOwner, "BackboneElement.modifierExtension")!.Key);
        }

        DbElement slicedId = catalog.ResolveOwnedElement(owner, "Patient.contact:family.id")!;
        DbElementType parentType = DbElementType.SelectList(imported.Connection,
            ElementKey: slicedId.ParentElementKey).ShouldHaveSingleItem();
        parentType.TypeStructureKey = catalog.ResolveNativeType(imported.PackageKey, "Unrelated")!.Key;
        DbElementType.Update(imported.Connection, parentType);
        Should.Throw<InvalidOperationException>(() => imported.Catalog.ResolveBaseElement(slicedId)).Message.ShouldContain("foreign key");
        parentType.TypeName = "Unrelated";
        DbElementType.Update(imported.Connection, parentType);
        slicedId.BaseElementKey = null;
        slicedId.BaseStructureKey = null;
        Should.Throw<InvalidOperationException>(() => imported.Catalog.ResolveBaseElement(slicedId)).Message.ShouldContain("missing inherited BasePath");
    }

    [Fact]
    public void PublicImportRejectsNativeUnresolvedContentReferenceButRetainsPassiveDiagnostics()
    {
        DefinitionCollection definitions = Definitions();
        StructureDefinition patient = Definition("Patient", "Patient");
        patient.Kind = StructureDefinition.StructureDefinitionKind.Resource;
        patient.Snapshot.Element.Add(new() { ElementId = "Patient.contact", Path = "Patient.contact", ContentReference = "#Patient.missing" });
        definitions.AddStructureDefinition(patient, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        using (CatalogImport imported = new(definitions))
        {
            InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
                imported.Database.TryLoadFromDefinitionCollections([], []));
            error.Message.ShouldContain(patient.Url);
            error.Message.ShouldContain("Patient.missing");
            DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBeNull();
        }

        DefinitionCollection passiveDefinitions = Definitions();
        AddNative(passiveDefinitions, NativeQuantity());
        StructureDefinition passive = Profile("UnrelatedIncomplete", "Patient", baseDefinition: "http://example.org/missing-base");
        passive.Snapshot.Element.Add(new() { ElementId = "Patient.contact", Path = "Patient.contact", ContentReference = "#Patient.missing" });
        AddProfile(passiveDefinitions, passive);
        using CatalogImport retained = new(passiveDefinitions);
        retained.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = retained.Catalog;
        DbStructureDefinition passiveOwner = catalog.ResolveDefinition(retained.PackageKey, passive.Url)!;
        catalog.IsProcessed(passiveOwner).ShouldBeFalse();
        DbElement unresolved = catalog.ResolveOwnedElement(passiveOwner, "Patient.contact")!;
        unresolved.DefinedAsContentReference.ShouldBeTrue();
        unresolved.ContentReferenceSourceKey.ShouldBeNull();
        unresolved.ContentReferenceSourceId.ShouldBe("#Patient.missing");
        catalog.ResolveImportLinks(retained.Connection, validateProcessedRoots: true);
        catalog.Diagnostics.ShouldContain(message => message.Contains(passive.Url) && message.Contains("Patient.missing"));
        DefinitionInventoryStore.ReadState(retained.Connection)!.CompletedRevision.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void InterruptedImportCannotPublishCompletion()
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        using CatalogImport imported = new(definitions);
        using IDbCommand interrupt = imported.Connection.CreateCommand();
        interrupt.CommandText = """
            CREATE TRIGGER RejectInventoryCompletion BEFORE UPDATE ON DefinitionInventoryState
            WHEN NEW.CompletedRevision IS NOT NULL
            BEGIN SELECT RAISE(ABORT, 'injected late completion failure'); END
            """;
        interrupt.ExecuteNonQuery();
        SqliteException late = Should.Throw<SqliteException>(() =>
            imported.Database.TryLoadFromDefinitionCollections([], []));
        late.Message.ShouldContain("late completion");
        DbStructureDefinitionFacts.SelectList(imported.Connection).ShouldHaveSingleItem();
        imported.Catalog.ResolveRoot(imported.Catalog.ResolveNativeType(imported.PackageKey, "Quantity")!).Id.ShouldBe("Quantity");
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(imported.Connection)!;
        state.CompletedRevision.ShouldBeNull();
        interrupt.CommandText = "DROP TRIGGER RejectInventoryCompletion";
        interrupt.ExecuteNonQuery();
        DefinitionInventoryPackageCounts expected = JsonSerializer.Deserialize<DefinitionInventoryPackageCounts[]>(state.ExpectedPackageCountsJson)!.Single();
        string wrongRevision = DefinitionInventoryStore.BeginInventory(imported.Connection, [expected with { ElementCount = expected.ElementCount + 1 }]);
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.CompleteInventory(imported.Connection, wrongRevision))
            .Message.ShouldContain("expected source counts");
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBeNull();
        string validRevision = DefinitionInventoryStore.BeginInventory(imported.Connection, [expected]);
        DefinitionInventoryStore.CompleteInventory(imported.Connection, validRevision);
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBe(validRevision);
        using IDbCommand corrupt = imported.Connection.CreateCommand();
        corrupt.CommandText = "UPDATE ElementTypes SET StructureKey = 999 WHERE Key = (SELECT MIN(Key) FROM ElementTypes)";
        corrupt.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.CompleteInventory(imported.Connection, validRevision)).Message.ShouldContain("invalid owner");
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBeNull();

        DefinitionCollection cycle = Definitions();
        StructureDefinition recursive = Definition("Recursive", "Recursive");
        recursive.Snapshot.Element.Add(new() { ElementId = "Recursive.a", Path = "Recursive.a", ContentReference = "#Recursive.b" });
        recursive.Snapshot.Element.Add(new() { ElementId = "Recursive.b", Path = "Recursive.b", ContentReference = "#Recursive.a" });
        AddNative(cycle, recursive);
        using CatalogImport cyclic = new(cycle);
        Should.Throw<InvalidOperationException>(() => cyclic.Database.TryLoadFromDefinitionCollections([], [])).Message.ShouldContain("cyclic contentReference");
        DefinitionInventoryStore.ReadState(cyclic.Connection)!.CompletedRevision.ShouldBeNull();

        DefinitionCollection duplicate = Definitions();
        AddNative(duplicate, NativeQuantity());
        StructureDefinition conflict = Profile("ConflictingId", "Quantity");
        conflict.Url = QuantityUrl;
        AddProfile(duplicate, conflict);
        using CatalogImport conflicting = new(duplicate);
        Should.Throw<InvalidOperationException>(() => conflicting.Database.TryLoadFromDefinitionCollections([], []))
            .Message.ShouldContain("identity");
        DefinitionInventoryStore.ReadState(conflicting.Connection)!.CompletedRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeededRootsNeverUseDifferentialOrMalformedSnapshot(bool differentialOnly)
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        StructureDefinition profile = Profile("NeededQuantity", "Quantity");
        if (differentialOnly)
        {
            profile.Differential = new() { Element = [new() { ElementId = "Quantity.code", Path = "Quantity.code", Max = "0" }] };
            profile.Snapshot = null;
        }
        else
        {
            profile.Snapshot.Element[0].Path = "Quantity.invalid";
        }
        AddProfile(definitions, profile);
        using CatalogImport imported = new(definitions);
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => imported.Database.TryLoadFromDefinitionCollections([], []));
        error.Message.ShouldContain(profile.Url);
        error.Message.ShouldContain(differentialOnly ? "snapshot" : "top-level Path");
        DbStructureDefinitionFacts facts = DbStructureDefinitionFacts.SelectList(imported.Connection)
            .Single(row => row.Derivation == "constraint");
        facts.SnapshotAvailability.ShouldBe(differentialOnly ? DefinitionSnapshotAvailability.Missing : DefinitionSnapshotAvailability.Available);
        StructureDefinitionFactsStore.ReadDefinition(facts).Type.ShouldBe("Quantity");
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionRequiresEveryFactsOwnerAndExactPackageSet(bool extraPackage)
    {
        DefinitionCollection definitions = Definitions();
        AddNative(definitions, NativeQuantity());
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        string revision = DefinitionInventoryStore.ReadState(imported.Connection)!.StartedRevision;
        if (extraPackage)
        {
            DbFhirPackage.Insert(imported.Connection, Package(999), insertPrimaryKey: true);
        }
        else
        {
            DbStructureDefinitionFacts.Delete(imported.Connection);
        }
        Should.Throw<InvalidOperationException>(() => DefinitionInventoryStore.CompleteInventory(imported.Connection, revision))
            .Message.ShouldContain(extraPackage ? "package set" : "one package-consistent facts row");
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBeNull();
    }

    [Fact]
    public async Task PublicCoreImportPreservesNativeInventory()
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages");
        string[][] entries = [TestCommon.EntriesR2, TestCommon.EntriesR3, TestCommon.EntriesR4, TestCommon.EntriesR4B, TestCommon.EntriesR5];
        foreach (string[] directives in entries)
        {
            string moniker = directives[0];
            string[] parts = moniker.Split('#');
            File.Exists(Path.Combine(cache, moniker, "package", "package.json")).ShouldBeTrue(
                $"Required cached-core prerequisite absent: {moniker}; no package acquisition attempted.");
            if (!moniker.StartsWith("hl7.fhir.r2.core#", StringComparison.Ordinal))
            {
                string expansions = parts[0].Replace(".core", ".expansions") + "#" + parts[1];
                File.Exists(Path.Combine(cache, expansions, "package", "package.json")).ShouldBeTrue(
                    $"Required cached-expansions prerequisite absent: {expansions}; no package acquisition attempted.");
            }

            using PackageLoader loader = new(new()
            {
                OfflineMode = true, ResolvePackageDependencies = false, AutoLoadExpansions = true,
                LogFactory = NullLoggerFactory.Instance,
            });
            DefinitionCollection definitions = (await loader.LoadPackages(directives))!;
            definitions.ShouldNotBeNull();
            using CatalogImport imported = new(definitions);
            Stopwatch elapsed = Stopwatch.StartNew();
            imported.Database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
            elapsed.Stop();
            List<DbStructureDefinition> nativeRows = [.. DbStructureDefinition.SelectList(imported.Connection)
                .Where(row => row.ArtifactClass != FhirArtifactClassEnum.Profile)];
            nativeRows.Count.ShouldBe(definitions.PrimitiveTypesByName.Count + definitions.ComplexTypesByName.Count + definitions.ResourcesByName.Count);
            foreach ((IEnumerable<StructureDefinition> inventory, FhirArtifactClassEnum artifactClass) in new[]
            {
                (definitions.PrimitiveTypesByName.Values.AsEnumerable(), FhirArtifactClassEnum.PrimitiveType),
                (definitions.ComplexTypesByName.Values.AsEnumerable(), FhirArtifactClassEnum.ComplexType),
                (definitions.ResourcesByName.Values.AsEnumerable(), FhirArtifactClassEnum.Resource),
            })
            {
                nativeRows.Where(row => row.ArtifactClass == artifactClass).Select(row => (row.UnversionedUrl, row.Version)).Order()
                    .ShouldBe(inventory.Select(row => (row.Url, row.Version)).Order());
            }
            _output.WriteLine($"{moniker}: primitives={definitions.PrimitiveTypesByName.Count}; complex={definitions.ComplexTypesByName.Count}; resources={definitions.ResourcesByName.Count}");
            _output.WriteLine($"  native definitions={nativeRows.Count}; retained source profiles={definitions.ProfilesByUrl.Count}; elements={DbElement.SelectList(imported.Connection).Count}; import ms={elapsed.ElapsedMilliseconds}");
            _output.WriteLine($"  code systems source/stored={definitions.CodeSystemsByUrl.Count}/{DbCodeSystem.SelectList(imported.Connection).Count}; value sets source/stored={definitions.ValueSetsByVersionedUrl.Count}/{DbValueSet.SelectList(imported.Connection).Count}");
        }
    }

    [Theory]
    [InlineData("hl7.fhir.r2.core#1.0.2")]
    [InlineData("hl7.fhir.r3.core#3.0.2")]
    [InlineData("hl7.fhir.r4.core#4.0.1")]
    [InlineData("hl7.fhir.r4b.core#4.3.0")]
    [InlineData("hl7.fhir.r5.core#5.0.0")]
    public async Task PublicFullImportRequiresPinnedCachedCore(string moniker)
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages");
        List<string> prerequisites = [moniker];
        // PackageLoader deliberately does not auto-load expansions for DSTU2.
        if (!moniker.StartsWith("hl7.fhir.r2.core#", StringComparison.Ordinal))
        {
            prerequisites.Add(moniker.Replace(".core#", ".expansions#"));
        }
        foreach (string prerequisite in prerequisites)
        {
            File.Exists(Path.Combine(cache, prerequisite, "package", "package.json")).ShouldBeTrue(
                $"Required cached-core prerequisite absent: {prerequisite} under {cache}. Populate the documented cache explicitly; no package acquisition attempted.");
        }

        using PackageLoader loader = new(new()
        {
            OfflineMode = true, ResolvePackageDependencies = false, AutoLoadExpansions = true,
            LogFactory = NullLoggerFactory.Instance,
        });
        DefinitionCollection definitions = (await loader.LoadPackages([moniker]))!;
        definitions.ShouldNotBeNull();
        using CatalogImport imported = new(definitions);
        Stopwatch elapsed = Stopwatch.StartNew();
        imported.Database.TryLoadFromDefinitionCollections(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
        elapsed.Stop();
        XVerStructureCatalog catalog = new(imported.Connection, XVerProcessor._exclusionSet);
        catalog.GetDefinitions(imported.PackageKey).Count.ShouldBe(definitions.PrimitiveTypesByName.Count +
            definitions.ComplexTypesByName.Count + definitions.ResourcesByName.Count + definitions.ProfilesByUrl.Count);
        catalog.GetDefinitions(imported.PackageKey).Count(row => row.ArtifactClass == FhirArtifactClassEnum.Profile)
            .ShouldBe(definitions.ProfilesByUrl.Count);
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(imported.Connection)!;
        state.CompletedRevision.ShouldBe(state.StartedRevision);
        state.CompletedRevision.ShouldNotBeNullOrEmpty();
        DbStructureDefinition patient = catalog.ResolveNativeType(imported.PackageKey, "Patient")!;
        DbElement contactId = catalog.ResolveOwnedElement(patient, "Patient.contact.id")!;
        if (definitions.FhirSequence != FhirReleases.FhirSequenceCodes.DSTU2)
        {
            contactId.BasePath.ShouldBe("Element.id");
            DbStructureDefinition element = catalog.ResolveNativeType(imported.PackageKey, "Element")!;
            contactId.BaseElementKey.ShouldBe(catalog.ResolveOwnedElement(element, "Element.id")!.Key);
            contactId.BaseStructureKey.ShouldBe(element.Key);
        }
        catalog.ValidateStoredLinks(DbElementType.SelectList(imported.Connection));
        _output.WriteLine($"{moniker}: public full import completed; definitions={catalog.GetDefinitions(imported.PackageKey).Count}; profiles={definitions.ProfilesByUrl.Count}; elements={DbElement.SelectList(imported.Connection).Count}; import ms={elapsed.ElapsedMilliseconds}");
    }

    private const string QuantityUrl = "http://hl7.org/fhir/StructureDefinition/Quantity";

    [Fact]
    public void LocalProhibitionsAndBindingsOverrideInheritedFacts()
    {
        StructureDefinition inherited = ConstraintProfile("InheritedQuantity");
        inherited.Snapshot.Element.Single(element => element.Path == "Quantity.code").Binding = new()
        {
            Strength = Hl7.Fhir.Model.BindingStrength.Required, ValueSet = "http://example.org/units|1",
        };
        StructureDefinition local = (StructureDefinition)inherited.DeepCopy();
        local.Id = local.Name = "LocalQuantity";
        local.Url = "http://example.org/LocalQuantity";
        local.BaseDefinition = inherited.Url;
        local.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        local.Snapshot.Element.Single(element => element.Path == "Quantity.code").Binding.ValueSet = "http://example.org/units|2";
        using CatalogImport imported = ImportUses(inherited, local);
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileUse use = QuantityUse(imported, facts, local);
        use.Diagnostics.ShouldBeEmpty();
        EffectiveProfileBranch branch = use.Branches.ShouldHaveSingleItem();
        branch.Diagnostics.ShouldBeEmpty();
        branch.Elements[".comparator"].Maximum.ShouldBe(0);
        use.Permits(".comparator").ShouldBeFalse();
        branch.Elements[".code"].Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Binding)
            .Select(fact => ProfileUseFacts.Text(fact.Value, "valueSet")).Distinct().ShouldBe(["http://example.org/units|1", "http://example.org/units|2"]);
        UnsafeCodeIdentity mg = new("http://unitsofmeasure.org", "ucum-2", "mg");
        ProfileQualification qualification = facts.QualifiedIdentities(branch, ".code",
            new Dictionary<string, ImmutableArray<UnsafeCodeIdentity>>
            {
                ["http://example.org/units|1"] = [mg, new("http://unitsofmeasure.org", "ucum-1", "mg")],
                ["http://example.org/units|2"] = [mg],
            });
        qualification.Identities.ShouldBe([mg]);
        qualification.Diagnostics.ShouldBeEmpty();
        facts.QualifiedIdentities(branch, ".code", new Dictionary<string, ImmutableArray<UnsafeCodeIdentity>>())
            .Kind.ShouldBe(ProfileProofKind.Unsupported);
    }

    [Fact]
    public void ProfileAlternativesDoNotConjoinBranchRestrictions()
    {
        StructureDefinition prohibited = ConstraintProfile("NoComparator");
        prohibited.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        StructureDefinition permitted = ConstraintProfile("AllowsComparator");
        DefinitionCollection definitions = UseDefinitions(prohibited, permitted);
        StructureDefinition holder = definitions.ResourcesByName["UseHolder"];
        holder.Snapshot.Element.Single(element => element.Path == "UseHolder.quantity").Type[0].Profile = [prohibited.Url, permitted.Url];
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = imported.Catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        DbElement element = imported.Catalog.ResolveOwnedElement(owner, "UseHolder.quantity")!;
        DbElementType.SelectList(imported.Connection, ElementKey: element.Key).Count.ShouldBe(2);
        EffectiveProfileUse use = facts.AtUse(owner, element, 0);
        use.Diagnostics.ShouldBeEmpty();
        use.Branches.Count().ShouldBe(2);
        use.Branches.Select(branch => branch.Key.ProfileAlternative).ShouldBe([prohibited.Url, permitted.Url]);
        use.Branches.Single(branch => branch.Key.ProfileAlternative == prohibited.Url).Elements[".comparator"].Prohibited.ShouldBeTrue();
        use.Branches.Single(branch => branch.Key.ProfileAlternative == permitted.Url).Elements[".comparator"].Prohibited.ShouldBeFalse();
        use.Permits(".comparator").ShouldBeTrue();
        facts.AtUse(owner, element, 0).ShouldBeSameAs(use);
        EffectiveProfileBranch allowed = use.Branches.Single(branch => branch.Key.ProfileAlternative == permitted.Url);
        DbStructureDefinition native = imported.Catalog.ResolveDefinition(imported.PackageKey, permitted.Url)!;
        ProfileRepresentationProof allowedProof = facts.Prove(allowed, native, nativeContext: allowed);
        allowedProof.Kind.ShouldBe(ProfileProofKind.NativeReuse);
        allowedProof.RequiredFacts[".comparator"].Maximum.ShouldBe(1);
        ProfileRepresentationProof narrower = facts.Prove(
            use.Branches.Single(branch => branch.Key.ProfileAlternative == prohibited.Url), native, nativeContext: allowed);
        narrower.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        narrower.PreservesRestrictions.ShouldBeFalse();
        narrower.RequiredFacts[".comparator"].Maximum.ShouldBe(0);
    }

    [Fact]
    public void InheritedNarrowingAndSliceContextsRemainApplicable()
    {
        StructureDefinition parent = ConstraintProfile("SlicedQuantity");
        ElementDefinition extension = parent.Snapshot.Element.Single(element => element.Path == "Quantity.extension");
        extension.Slicing = new()
        {
            Rules = ElementDefinition.SlicingRules.Closed, Ordered = false,
            Discriminator = [new() { Type = ElementDefinition.DiscriminatorType.Value, Path = "url" }],
        };
        foreach (string slice in new[] { "units", "other" })
        {
            parent.Snapshot.Element.Add(new()
            {
                ElementId = "Quantity.extension:" + slice, Path = "Quantity.extension", SliceName = slice,
                Min = 0, Max = "1", Type = [new() { Code = "Extension" }],
            });
            parent.Snapshot.Element.Add(new()
            {
                ElementId = "Quantity.extension:" + slice + ".url", Path = "Quantity.extension.url",
                Min = 1, Max = "1", Type = [new() { Code = "uri" }], Fixed = new Hl7.Fhir.Model.FhirUri("http://example.org/" + slice),
            });
        }
        StructureDefinition child = (StructureDefinition)parent.DeepCopy();
        child.Id = child.Name = "InheritedSlices";
        child.Url = "http://example.org/InheritedSlices";
        child.BaseDefinition = parent.Url;
        child.Snapshot.Element.Single(element => element.ElementId == "Quantity.extension:units").Max = "0";
        using CatalogImport imported = ImportUses(parent, child);
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileUse use = QuantityUse(imported, facts, child);
        EffectiveProfileBranch branch = use.Branches.ShouldHaveSingleItem();
        branch.Diagnostics.ShouldBeEmpty();
        branch.Elements.Keys.ShouldContain(".extension:units.url");
        branch.Elements.Keys.ShouldContain(".extension:other.url");
        branch.Elements[".extension:units.url"].Minimum.ShouldBe(1);
        branch.Elements[".extension:units.url"].Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Fixed)
            .Select(fact => fact.Value.GetString()).Distinct().ShouldBe(["http://example.org/units"]);
        branch.Elements[".extension:other.url"].Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Fixed)
            .Select(fact => fact.Value.GetString()).Distinct().ShouldBe(["http://example.org/other"]);
        use.Permits(".extension:units").ShouldBeFalse();
        use.Permits(".extension:units.url").ShouldBeFalse();
        use.Permits(".extension:other").ShouldBeTrue();
        branch.Elements[".extension"].Restrictions.ShouldContain(fact => fact.Kind == ProfileRestrictionKind.Slicing);
    }

    [Fact]
    public void UnprovenNumericAndLengthBoundsRefuseNativeReuse()
    {
        StructureDefinition bounded = ConstraintProfile("BoundedQuantity");
        bounded.Snapshot.Element.Single(element => element.Path == "Quantity.value").MinValue = new Hl7.Fhir.Model.FhirDecimal(0);
        bounded.Snapshot.Element.Single(element => element.Path == "Quantity.value").MaxValue = new Hl7.Fhir.Model.FhirDecimal(100);
        bounded.Snapshot.Element.Single(element => element.Path == "Quantity.unit").MaxLength = 8;
        using CatalogImport imported = ImportUses(bounded);
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = QuantityUse(imported, facts, bounded).Branches.ShouldHaveSingleItem();
        DbStructureDefinition target = imported.Catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        ProfileRepresentationProof comparison = facts.Prove(branch, target);
        comparison.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        comparison.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.OwnerProjectionRequired);
        comparison.RequiredFacts.ShouldBeSameAs(branch.Elements);
        comparison.NativeFacts.ShouldNotBeEmpty();
        comparison.TargetCanonical.ShouldBeNull();
        comparison.Projection.ShouldBeEmpty();
        comparison.PreservesRestrictions.ShouldBeFalse();
        comparison.Diagnostics.ShouldContain(message => message.Contains("minValueDecimal"));
        comparison.Diagnostics.ShouldContain(message => message.Contains("maxLength"));
        PersistedProfileDecision persisted = ProfileUseFacts.ReadDecisions(ProfileUseFacts.SerializeDecisions([comparison])).ShouldHaveSingleItem();
        persisted.Input.ShouldBe(comparison.Input);
        persisted.TargetPackageKey.ShouldBe(comparison.TargetPackageKey);
        persisted.Reasons.Select(reason => reason.Category).ShouldBe(comparison.Reasons.Select(reason => reason.Category));
        persisted.RequiredFacts[".value"].Restrictions.ShouldContain(restriction => restriction.Name == "minValueDecimal");
        persisted.RequiredFacts[".unit"].Restrictions.ShouldContain(restriction => restriction.Name == "maxLength");
        persisted.TargetCanonical.ShouldBeNull();
        DbStructureDefinition holder = imported.Catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        DbElement targetUse = imported.Catalog.ResolveOwnedElement(holder, "UseHolder.native")!;
        ProfileRepresentationProof projection = facts.Prove(branch, target, holder, targetUse, new HashSet<int> { holder.Key });
        projection.Kind.ShouldBe(ProfileProofKind.OwnerProjection);
        projection.Projection.Single(element => element.TargetElementId == "UseHolder.native.value").Restrictions
            .ShouldContain(fact => fact.Name == "minValueDecimal");
        projection.Projection.Single(element => element.TargetElementId == "UseHolder.native.unit").Restrictions
            .ShouldContain(fact => fact.Name == "maxLength");
    }

    [Fact]
    public void PassiveProfilesConstrainUsesWithoutStandaloneOwners()
    {
        StructureDefinition coding = Definition("Coding", "Coding");
        coding.Snapshot.Element.Add(new() { ElementId = "Coding.code", Path = "Coding.code", Min = 0, Max = "1", Type = [new() { Code = "code" }] });
        StructureDefinition passive = (StructureDefinition)coding.DeepCopy();
        passive.Id = passive.Name = "PassiveUseCoding";
        passive.Url = "http://example.org/PassiveUseCoding";
        passive.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
        passive.BaseDefinition = coding.Url;
        passive.Snapshot.Element[1].Min = 1;
        passive.Snapshot.Element[1].Fixed = new Hl7.Fhir.Model.Code("qualified");
        DefinitionCollection definitions = UseDefinitions();
        AddNative(definitions, coding);
        AddProfile(definitions, passive);
        definitions.ResourcesByName["UseHolder"].Snapshot.Element.Add(new()
        {
            ElementId = "UseHolder.coding", Path = "UseHolder.coding", Min = 0, Max = "1",
            Type = [new() { Code = "Coding", Profile = [passive.Url] }],
        });
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition profileOwner = catalog.ResolveDefinition(imported.PackageKey, passive.Url)!;
        catalog.IsProcessed(profileOwner).ShouldBeFalse();
        catalog.GetProcessedStructures(imported.PackageKey).ShouldNotContain(owner => owner.Key == profileOwner.Key);
        DbStructureDefinition holder = catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileUse use = facts.AtUse(holder, catalog.ResolveOwnedElement(holder, "UseHolder.coding")!, 0);
        use.Branches.ShouldHaveSingleItem().Elements[".code"].Minimum.ShouldBe(1);
        use.Branches[0].Elements[".code"].Restrictions.ShouldContain(fact => fact.Name == "fixedCode" && fact.Value.GetString() == "qualified");
    }

    [Fact]
    public void OpaqueInvariantIsNotQualificationOrCompatibility()
    {
        StructureDefinition profile = ConstraintProfile("OpaqueQuantity");
        profile.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Human = "Not a comparator theorem",
            Expression = "code.exists() implies system.exists()",
            Extension = [new() { Url = ProfileUseFacts.XPathExtensionUrl, Value = new Hl7.Fhir.Model.FhirString("f:code/f:unexpected") }],
            Source = profile.Url + "|definition-1",
        });
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        ProfileInvariant invariant = branch.Elements[""].Invariants.ShouldHaveSingleItem();
        invariant.Key.ShouldBe("sqty-1");
        invariant.Expression.ShouldBe(profile.Snapshot.Element[0].Constraint[0].Expression);
        invariant.XPath.ShouldBe("f:code/f:unexpected");
        invariant.Human.ShouldBe("Not a comparator theorem");
        invariant.Source.ShouldBe(profile.Url + "|definition-1");
        invariant.Origin.OwnerKey.ShouldBe(owner.Key);
        facts.QualifiedIdentities(branch, ".code", new Dictionary<string, ImmutableArray<UnsafeCodeIdentity>>())
            .Kind.ShouldBe(ProfileProofKind.Unsupported);
        ProfileRepresentationProof negative = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!);
        negative.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        negative.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.InvariantTransformation);
        negative.RequiredFacts[""].Invariants.ShouldHaveSingleItem().Value.GetRawText().ShouldBe(invariant.Value.GetRawText());
        negative.PreservesRestrictions.ShouldBeFalse();
        negative.TargetCanonical.ShouldBeNull();
        negative.Projection.ShouldBeEmpty();
        ProfileRepresentationProof exact = facts.Prove(branch, owner);
        exact.Kind.ShouldBe(ProfileProofKind.NativeReuse);
        exact.TargetCanonical.ShouldBe(profile.Url + "|definition-1");
        exact.RequiredFacts[""].Invariants.ShouldHaveSingleItem().Value.GetRawText().ShouldBe(invariant.Value.GetRawText());
    }

    [Fact]
    public void NativeReuseOrProjectionMustPreserveAllRequiredRestrictions()
    {
        StructureDefinition simple = ConstraintProfile("SlicedSimple", slicedRoot: true);
        simple.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        simple.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Human = "No comparator",
            Expression = "comparator.empty()", Source = simple.Url,
            Extension = [new() { Url = ProfileUseFacts.XPathExtensionUrl, Value = new Hl7.Fhir.Model.FhirString("not(exists(f:comparator))") }],
        });
        using CatalogImport imported = ImportUses(simple);
        XVerStructureCatalog catalog = imported.Catalog;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch source = QuantityUse(imported, facts, simple).Branches.ShouldHaveSingleItem();
        source.Root.Id.ShouldBe("Quantity:fixture");
        DbStructureDefinition holder = catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        DbElement targetUse = catalog.ResolveOwnedElement(holder, "UseHolder.native")!;
        ProfileRepresentationProof proof = facts.Prove(source, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!, holder, targetUse,
            new HashSet<int> { holder.Key });
        proof.Kind.ShouldBe(ProfileProofKind.OwnerProjection);
        proof.PreservesRestrictions.ShouldBeTrue();
        proof.SourceRoot.ElementId.ShouldBe("Quantity:fixture");
        proof.Projection.Single(element => element.TargetElementId == targetUse.Id + ".comparator").Maximum.ShouldBe(0);
        ProfileRestriction projected = proof.Projection.Single(element => element.TargetElementId == targetUse.Id).Restrictions
            .Single(fact => fact.Kind == ProfileRestrictionKind.Invariant);
        projected.Value.GetProperty("expression").GetString().ShouldBe("comparator.empty()");
        ProfileUseFacts.InvariantXPath(projected.Value).ShouldBe("not(exists(f:comparator))");
        projected.Origin.ElementId.ShouldBe("Quantity:fixture");
        facts.Prove(source, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!).PreservesRestrictions.ShouldBeFalse();

        simple.Snapshot.Element.Single(element => element.Path == "Quantity.value").Extension.Add(new()
        {
            Url = "http://example.org/required-semantic-extension", Value = new Hl7.Fhir.Model.FhirBoolean(true),
        });
        using CatalogImport unsupported = ImportUses(simple);
        ProfileUseFacts unknown = new(unsupported.Catalog, unsupported.PackageKey, unsupported.PackageKey);
        EffectiveProfileBranch required = QuantityUse(unsupported, unknown, simple).Branches.ShouldHaveSingleItem();
        ProfileRepresentationProof refusal = unknown.Prove(required, unsupported.Catalog.ResolveNativeType(unsupported.PackageKey, "Quantity")!);
        refusal.Kind.ShouldBe(ProfileProofKind.Unsupported);
        refusal.IsAcceptedDecision.ShouldBeFalse();
        refusal.RequiredFacts.ShouldBeSameAs(required.Elements);
        refusal.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.UnclassifiedRestriction);
        refusal.Diagnostics.ShouldContain(message => message.Contains("pair") && message.Contains(simple.Url) &&
            message.Contains("Quantity:fixture.value") && message.Contains("extension"));
        refusal.PreservesRestrictions.ShouldBeFalse();
    }

    [Fact]
    public void XPathOnlyComparatorRuleHasBoundedProjectionWhenActuallyPresent()
    {
        StructureDefinition simple = ConstraintProfile("XPathQuantity");
        simple.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        simple.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Human = "No comparator",
            Extension = [new() { Url = ProfileUseFacts.XPathExtensionUrl, Value = new Hl7.Fhir.Model.FhirString("not(exists(f:comparator))") }],
        });
        using CatalogImport imported = ImportUses(simple);
        XVerStructureCatalog catalog = imported.Catalog;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = QuantityUse(imported, facts, simple).Branches.ShouldHaveSingleItem();
        branch.Elements[""].Invariants.ShouldHaveSingleItem().XPath.ShouldBe("not(exists(f:comparator))");
        DbStructureDefinition holder = catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!,
            holder, catalog.ResolveOwnedElement(holder, "UseHolder.native")!, new HashSet<int> { holder.Key });
        proof.Kind.ShouldBe(ProfileProofKind.OwnerProjection);
        ProfileRestriction constraint = proof.Projection.Single(element => element.TargetElementId == "UseHolder.native")
            .Restrictions.Single(fact => fact.Kind == ProfileRestrictionKind.Invariant);
        ProfileUseFacts.Text(constraint.Value, "expression").ShouldBe("comparator.empty()");
        ProfileUseFacts.InvariantXPath(constraint.Value).ShouldBe("not(exists(f:comparator))");
    }

    [Fact]
    public void ConvertedDstu2XPathSurvivesPersistedFactsAndEffectiveInheritance()
    {
        const string xpath = "not(exists(f:comparator))";
        string json = """
            {
              "resourceType":"StructureDefinition","id":"RetainedQuantity",
              "url":"http://example.org/RetainedQuantity","version":"fixture-2","name":"RetainedQuantity",
              "status":"active","kind":"datatype","abstract":false,"constrainedType":"Quantity",
              "base":"http://hl7.org/fhir/StructureDefinition/Quantity",
              "snapshot":{"element":[{"path":"Quantity","min":0,"max":"*","constraint":[{
                "key":"original-key","severity":"warning","human":"Original human text",
                "source":"http://example.org/original|origin-2","xpath":"not(exists(f:comparator))"
              }]}]},
              "differential":{"element":[{"path":"Quantity","constraint":[{
                "key":"original-key","severity":"warning","human":"Original human text",
                "source":"http://example.org/original|origin-2","xpath":"not(exists(f:comparator))"
              }]}]}
            }
            """;
        Fhir.CodeGen.CrossVersionLoader.Converter_20_50 converter = new();
        StructureDefinition profile = converter.Convert(FhirJsonNode.Parse(json)).ShouldBeOfType<StructureDefinition>();
        profile.Snapshot.Element[0].Constraint[0].Expression.ShouldBeNull();
        profile.Snapshot.Element[0].Constraint[0].Extension.ShouldHaveSingleItem().Url
            .ShouldBe(CommonDefinitions.ExtUrlEdConstraintXPath);
        StructureDefinition child = ConstraintProfile("InheritedRetainedQuantity");
        child.BaseDefinition = profile.Url;
        using CatalogImport imported = ImportUses(profile, child);
        DbStructureDefinition owner = imported.Catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        DbStructureDefinitionFacts persisted = DbStructureDefinitionFacts.SelectList(imported.Connection)
            .Single(row => row.StructureKey == owner.Key);
        StructureDefinition recovered = new FhirJsonParser().Parse<StructureDefinition>(persisted.StructureDefinitionJson);
        recovered.Differential.Element.ShouldHaveSingleItem().Constraint.ShouldHaveSingleItem()
            .Extension.ShouldHaveSingleItem().Value.ShouldBeOfType<Hl7.Fhir.Model.FhirString>().Value.ShouldBe(xpath);
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch effective = facts.ForDefinition(imported.Catalog.ResolveDefinition(imported.PackageKey, child.Url)!);
        ProfileInvariant invariant = effective.Elements[""].Invariants.ShouldHaveSingleItem();
        invariant.Key.ShouldBe("original-key");
        invariant.Severity.ShouldBe("warning");
        invariant.Human.ShouldBe("Original human text");
        invariant.Source.ShouldBe("http://example.org/original|origin-2");
        invariant.XPath.ShouldBe(xpath);
        invariant.Expression.ShouldBeNull();
        invariant.Origin.OwnerKey.ShouldBe(owner.Key);
        invariant.Origin.Canonical.ShouldBe(profile.Url);
        invariant.Origin.Version.ShouldBe("fixture-2");
        invariant.Origin.ElementId.ShouldBe("Quantity");
        facts.Prove(effective, imported.Catalog.ResolveDefinition(imported.PackageKey, child.Url)!)
            .Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-value")]
    [InlineData("duplicate")]
    [InlineData("duplicate-with-expression")]
    [InlineData("unknown-url")]
    [InlineData("old-guessed-url")]
    public void MalformedOrUnknownXPathCannotAuthorizeProof(string scenario)
    {
        StructureDefinition profile = ConstraintProfile("MalformedXPath");
        Hl7.Fhir.Model.Extension extension = new()
        {
            Url = CommonDefinitions.ExtUrlEdConstraintXPath,
            Value = new Hl7.Fhir.Model.FhirString("not(exists(f:comparator))"),
        };
        ElementDefinition.ConstraintComponent constraint = new()
        {
            Key = "sqty-1", Human = "A name is not semantic evidence.", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error,
        };
        if (scenario != "missing")
        {
            constraint.Extension.Add(extension);
        }
        if (scenario == "wrong-value")
        {
            extension.Value = new Hl7.Fhir.Model.FhirBoolean(true);
        }
        if (scenario.StartsWith("duplicate", StringComparison.Ordinal))
        {
            constraint.Extension.Add((Hl7.Fhir.Model.Extension)extension.DeepCopy());
        }
        if (scenario == "duplicate-with-expression")
        {
            constraint.Expression = "comparator.empty()";
        }
        if (scenario == "unknown-url")
        {
            extension.Url = "http://example.org/extension-ElementDefinition.constraint.xpath";
        }
        if (scenario == "old-guessed-url")
        {
            extension.Url = "http://hl7.org/fhir/4.0/StructureDefinition/extension-ElementDefinition.constraint.xpath";
        }
        profile.Snapshot.Element[0].Constraint.Add(constraint);
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Elements[""].Invariants.ShouldHaveSingleItem().XPath.ShouldBeNull();
        string? revision = DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision;
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.Kind.ShouldBe(ProfileProofKind.Unsupported);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.RequiredFacts.ShouldBeSameAs(branch.Elements);
        proof.NativeFacts.ShouldNotBeEmpty();
        proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.UnclassifiedRestriction);
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.Diagnostics.ShouldContain(message => message.Contains(profile.Url) && message.Contains("pair"));
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBe(revision);
        catalog.ReadPayload(owner).ToJson().ShouldBe(profile.ToJson());
    }

    [Fact]
    public void ProjectionRequiresTheActualEmittedOwnerSchedule()
    {
        StructureDefinition profile = ConstraintProfile("RequiresProjection");
        profile.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = QuantityUse(imported, facts, profile).Branches.ShouldHaveSingleItem();
        DbStructureDefinition holder = catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        DbElement occurrence = catalog.ResolveOwnedElement(holder, "UseHolder.native")!;
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!, holder, occurrence);
        proof.Kind.ShouldBe(ProfileProofKind.Unsupported);
        proof.Diagnostics.ShouldContain(message => message.Contains("actual emitted owner schedule"));
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("unknown-extension", false)]
    [InlineData("unknown-extension", true)]
    [InlineData("binding-extension", false)]
    [InlineData("binding-extension", true)]
    [InlineData("missing-binding-domain", false)]
    [InlineData("missing-invariant-semantics", true)]
    [InlineData("unknown-slicing", false)]
    [InlineData("type-obligation", true)]
    [InlineData("default-value", false)]
    public void UnclassifiedFactsCannotHideBehindAKnownNegative(string scenario, bool onTarget)
    {
        StructureDefinition source = ConstraintProfile("KnownNarrowing");
        source.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        StructureDefinition target = ConstraintProfile("UnknownTarget");
        ElementDefinition element = (onTarget ? target : source).Snapshot.Element.Single(element => element.Path == "Quantity.code");
        switch (scenario)
        {
            case "unknown-extension":
                element.Extension.Add(new("http://example.org/unknown-semantics", new Hl7.Fhir.Model.FhirBoolean(true)));
                break;
            case "binding-extension":
            case "missing-binding-domain":
                element.Binding = new() { Strength = Hl7.Fhir.Model.BindingStrength.Required };
                if (scenario == "binding-extension")
                {
                    element.Binding.ValueSet = "http://example.org/domain|1";
                    element.Binding.Extension.Add(new("http://example.org/unknown-binding", new Hl7.Fhir.Model.FhirBoolean(true)));
                }
                break;
            case "missing-invariant-semantics":
                element.Constraint.Add(new() { Key = "unknown", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Human = "No semantics." });
                break;
            case "unknown-slicing":
                element.Slicing = new() { Rules = ElementDefinition.SlicingRules.Closed,
                    Discriminator = [new() { Type = ElementDefinition.DiscriminatorType.Value }] };
                break;
            case "type-obligation":
                element.Type[0].Aggregation = [ElementDefinition.AggregationMode.Contained];
                break;
            case "default-value":
                element.DefaultValue = new Hl7.Fhir.Model.Code("implicit");
                break;
        }
        using CatalogImport imported = ImportUses(source, target);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, source.Url)!);
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveDefinition(imported.PackageKey, target.Url)!);
        proof.Kind.ShouldBe(ProfileProofKind.Unsupported);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.RequiredFacts.ShouldBeSameAs(branch.Elements);
        proof.NativeFacts.ShouldNotBeEmpty();
        proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.UnclassifiedRestriction &&
            reason.Origin.Canonical == (onTarget ? target.Url : source.Url));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingRootFactsAndInvalidOwnersRemainUnresolved(bool missingRoot)
    {
        StructureDefinition profile = ConstraintProfile("InvalidDecision");
        profile.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, profile.Url)!);
        branch = missingRoot ? branch with { Elements = branch.Elements.Remove("") } :
            branch with { Key = branch.Key with { OwnerKey = -1 } };
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!);
        proof.Kind.ShouldBe(missingRoot ? ProfileProofKind.MissingFacts : ProfileProofKind.ConflictingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.ProjectionOwner.ShouldBeNull();
        proof.RequiredFacts.ShouldBeSameAs(branch.Elements);
        proof.NativeFacts.ShouldNotBeEmpty();
    }

    [Fact]
    public void BindingMetadataDoesNotEraseAdditionalDomainObligations()
    {
        StructureDefinition profile = ConstraintProfile("AdditionalDomain");
        profile.Snapshot.Element[0].Binding = new()
        {
            Extension = [new(CommonDefinitions.ExtUrlBindingName, new Hl7.Fhir.Model.FhirString("Only a name"))],
        };
        ElementDefinition code = profile.Snapshot.Element.Single(element => element.Path == "Quantity.code");
        code.Binding = new()
        {
            Strength = Hl7.Fhir.Model.BindingStrength.Required, ValueSet = "http://example.org/domain|1",
            Additional = [new() { Purpose = ElementDefinition.AdditionalBindingPurposeVS.Maximum,
                ValueSet = "http://example.org/maximum|2", Documentation = "Retained additional domain." }],
        };
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Elements[""].Restrictions.ShouldNotContain(fact => fact.Kind == ProfileRestrictionKind.Binding);
        ProfileRepresentationProof negative = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!);
        negative.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        negative.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.Binding);
        JsonElement additional = negative.RequiredFacts[".code"].Restrictions.Single(fact => fact.Kind == ProfileRestrictionKind.Binding)
            .Value.GetProperty("additional")[0];
        additional.GetProperty("purpose").GetString().ShouldBe("maximum");
        additional.GetProperty("valueSet").GetString().ShouldBe("http://example.org/maximum|2");
        additional.GetProperty("documentation").GetString().ShouldBe("Retained additional domain.");
        negative.PreservesRestrictions.ShouldBeFalse();
        negative.TargetCanonical.ShouldBeNull();
        negative.Projection.ShouldBeEmpty();
        facts.Prove(branch, owner).Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Fact]
    public void OccurrenceBindingSupplementsRatherThanWeakensDatatypeDomain()
    {
        StructureDefinition profile = ConstraintProfile("RequiredUnits");
        profile.Snapshot.Element[0].Binding = new()
        {
            Strength = Hl7.Fhir.Model.BindingStrength.Required, ValueSet = "http://example.org/units|1",
        };
        DefinitionCollection definitions = UseDefinitions(profile);
        definitions.ResourcesByName["UseHolder"].Snapshot.Element.Single(element => element.Path == "UseHolder.quantity").Binding = new()
        {
            Strength = Hl7.Fhir.Model.BindingStrength.Extensible, ValueSet = "http://example.org/occurrence|2",
        };
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = QuantityUse(imported, facts, profile).Branches.ShouldHaveSingleItem();
        branch.Diagnostics.ShouldBeEmpty();
        ProfileRestriction[] bindings = [.. branch.Elements[""].Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Binding)];
        bindings.Select(fact => ProfileUseFacts.Text(fact.Value, "strength")).ShouldBe(["required", "extensible"]);
        bindings.Select(fact => ProfileUseFacts.Text(fact.Value, "valueSet")).ShouldBe(["http://example.org/units|1", "http://example.org/occurrence|2"]);
        ProfileRepresentationProof negative = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!);
        negative.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        negative.RequiredFacts[""].Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Binding).ShouldBe(bindings);
        negative.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.Binding);
        negative.PreservesRestrictions.ShouldBeFalse();
        negative.TargetCanonical.ShouldBeNull();
        negative.Projection.ShouldBeEmpty();
    }

    [Fact]
    public void IncompleteTargetProjectionIsNotEvidenceOfAnAbsentField()
    {
        StructureDefinition source = ConstraintProfile("CompleteSource");
        StructureDefinition target = ConstraintProfile("IncompleteTarget");
        using CatalogImport imported = ImportUses(source, target);
        DbStructureDefinition targetOwner = imported.Catalog.ResolveDefinition(imported.PackageKey, target.Url)!;
        DbStructureDefinitionFacts payload = DbStructureDefinitionFacts.SelectList(imported.Connection)
            .Single(row => row.StructureKey == targetOwner.Key);
        target.Snapshot.Element.Add(new() { ElementId = "Quantity.unprojected", Path = "Quantity.unprojected",
            Min = 0, Max = "1", Type = [new() { Code = "code" }] });
        payload.StructureDefinitionJson = new FhirJsonSerializer().SerializeToString(target);
        DbStructureDefinitionFacts.Update(imported.Connection, payload);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, source.Url)!);
        string? revision = DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision;
        ProfileRepresentationProof proof = facts.Prove(branch, targetOwner);
        proof.Kind.ShouldBe(ProfileProofKind.MissingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.Diagnostics.ShouldContain(message => message.Contains("projection") && message.Contains(target.Url));
        DefinitionInventoryStore.ReadState(imported.Connection)!.CompletedRevision.ShouldBe(revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingDeclaredDatatypeOrKindCannotBecomeANegativeDecision(bool missingType)
    {
        StructureDefinition profile = ConstraintProfile("MissingDeclaration");
        using CatalogImport imported = ImportUses(profile);
        DbStructureDefinition owner = imported.Catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        DbStructureDefinitionFacts stored = DbStructureDefinitionFacts.SelectList(imported.Connection)
            .Single(row => row.StructureKey == owner.Key);
        if (missingType)
        {
            profile.Type = stored.Type = null;
        }
        else
        {
            profile.Kind = null;
            stored.Kind = null;
        }
        stored.StructureDefinitionJson = new FhirJsonSerializer().SerializeToString(profile);
        DbStructureDefinitionFacts.Update(imported.Connection, stored);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, profile.Url)!);
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!);
        proof.Kind.ShouldBe(ProfileProofKind.MissingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.RequiredFacts.ShouldBeSameAs(branch.Elements);
        proof.NativeFacts.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("id", false)]
    [InlineData("extension", true)]
    [InlineData("value", true)]
    public void ElementExistenceTransformIsBoundedToTheActualQuantityShape(string content, bool expected)
    {
        const string xpath = "@value|f:*|h:div";
        const string expression = "hasValue() or (children().count() > id.count()) or $this is Parameters";
        StructureDefinition legacy = ConstraintProfile("LegacyElementRule");
        legacy.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "legacy-key", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Human = "An XML child is required.",
            Extension = [new() { Url = CommonDefinitions.ExtUrlEdConstraintXPath, Value = new Hl7.Fhir.Model.FhirString(xpath) }],
        });
        StructureDefinition modern = ConstraintProfile("ModernElementRule");
        modern.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "different-key", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error,
            Human = "A non-id child is required.", Expression = expression,
        });
        DefinitionCollection definitions = UseDefinitions(legacy, modern);
        definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Add(new()
        {
            ElementId = "Quantity.id", Path = "Quantity.id", Min = 0, Max = "1",
            Representation = [ElementDefinition.PropertyRepresentation.XmlAttr], Type = [new() { Code = "id" }],
        });
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch branch = facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, legacy.Url)!);
        ProfileRepresentationProof proof = facts.Prove(branch, catalog.ResolveDefinition(imported.PackageKey, modern.Url)!);
        proof.Kind.ShouldBe(ProfileProofKind.NativeReuse);
        proof.RequiredFacts[""].Invariants.ShouldHaveSingleItem().XPath.ShouldBe(xpath);
        proof.RequiredFacts[""].Invariants.ShouldHaveSingleItem().Key.ShouldBe("legacy-key");
        proof.NativeFacts[""].Invariants.ShouldHaveSingleItem().Key.ShouldBe("different-key");
        string xmlChild = content switch
        {
            "extension" => """<extension url="http://example.org/test"><valueBoolean value="true"/></extension>""",
            "value" => """<value value="1"/>""",
            _ => "",
        };
        XDocument xml = XDocument.Parse($"""<Quantity xmlns="http://hl7.org/fhir" id="fixture">{xmlChild}</Quantity>""");
        XmlNamespaceManager namespaces = new(new NameTable());
        namespaces.AddNamespace("f", "http://hl7.org/fhir");
        namespaces.AddNamespace("h", "http://www.w3.org/1999/xhtml");
        ((bool)xml.Root!.XPathEvaluate("boolean(" + xpath + ")", namespaces)).ShouldBe(expected);
        Hl7.Fhir.Model.Quantity instance = new() { ElementId = "fixture" };
        if (content == "value")
        {
            instance.Value = 1;
        }
        else if (content == "extension")
        {
            instance.Extension.Add(new("http://example.org/test", new Hl7.Fhir.Model.FhirBoolean(true)));
        }
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        compiler.Compile(expression)(instance.ToTypedElement(), new EvaluationContext())
            .ShouldHaveSingleItem().Value.ShouldBe(expected);
    }

    [Fact]
    public void ContradictoryOrUnknownXPathIsNotAnExpressionAlias()
    {
        StructureDefinition source = ConstraintProfile("ContradictoryQuantity");
        source.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Human = "The key and expression do not excuse another unknown representation.",
            Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Expression = "comparator.empty()",
            Extension = [new() { Url = CommonDefinitions.ExtUrlEdConstraintXPath, Value = new Hl7.Fhir.Model.FhirString("f:unexpected") }],
        });
        StructureDefinition target = ConstraintProfile("ExpressionOnlyQuantity");
        target.Snapshot.Element[0].Constraint.Add(new()
        {
            Key = "sqty-1", Human = "No comparator.", Severity = Hl7.Fhir.Model.ConstraintSeverity.Error, Expression = "comparator.empty()",
        });
        using CatalogImport imported = ImportUses(source, target);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveDefinition(imported.PackageKey, source.Url)!),
            catalog.ResolveDefinition(imported.PackageKey, target.Url)!);
        proof.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.InvariantTransformation);
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.Diagnostics.ShouldContain(message => message.Contains("unsupported projection of constraint"));
    }

    [Fact]
    public void LocalUnboundedMaximumCannotLoosenAnInheritedLimit()
    {
        StructureDefinition parent = ConstraintProfile("LimitedQuantity");
        StructureDefinition child = (StructureDefinition)parent.DeepCopy();
        child.Id = child.Name = "IllegalLoosening";
        child.Url = "http://example.org/IllegalLoosening";
        child.BaseDefinition = parent.Url;
        child.Snapshot.Element.Single(element => element.Path == "Quantity.code").Max = "*";
        using CatalogImport imported = ImportUses(parent, child);
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileUse use = QuantityUse(imported, facts, child);
        EffectiveProfileBranch branch = use.Branches.ShouldHaveSingleItem();
        branch.Elements[".code"].Maximum.ShouldBe(1);
        branch.Diagnostics.ShouldContain(message => message.Contains("illegally loosens"));
        use.Permits(".code").ShouldBeFalse();
        facts.Prove(branch, imported.Catalog.ResolveNativeType(imported.PackageKey, "Quantity")!)
            .Kind.ShouldBe(ProfileProofKind.ConflictingFacts);
    }

    [Fact]
    public void ProjectionCannotIgnoreRestrictionsAtTheActualTargetOccurrence()
    {
        StructureDefinition profile = ConstraintProfile("SourceQuantity");
        profile.Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        DefinitionCollection definitions = UseDefinitions(profile);
        definitions.ResourcesByName["UseHolder"].Snapshot.Element.Add(new()
        {
            ElementId = "UseHolder.native.system", Path = "UseHolder.native.system", Min = 1, Max = "1",
            Type = [new() { Code = "uri" }], Fixed = new Hl7.Fhir.Model.FhirUri("http://example.org/target-only"),
        });
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        EffectiveProfileBranch source = QuantityUse(imported, facts, profile).Branches.ShouldHaveSingleItem();
        DbStructureDefinition holder = imported.Catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        ProfileRepresentationProof refusal = facts.Prove(source,
            imported.Catalog.ResolveNativeType(imported.PackageKey, "Quantity")!, holder,
            imported.Catalog.ResolveOwnedElement(holder, "UseHolder.native")!, new HashSet<int> { holder.Key });
        refusal.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
        refusal.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.RestrictionTransformation);
        refusal.PreservesRestrictions.ShouldBeFalse();
        refusal.TargetCanonical.ShouldBeNull();
        refusal.Projection.ShouldBeEmpty();
        refusal.Diagnostics.ShouldContain(message => message.Contains("target restriction not entailed") &&
            message.Contains("UseHolder.native.system"));
    }

    [Theory]
    [InlineData("different", false)]
    [InlineData("different", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("qualified", false)]
    [InlineData("qualified", true)]
    [InlineData("wrong-version", false)]
    [InlineData("wrong-version", true)]
    [InlineData("identical", false)]
    [InlineData("identical", true)]
    public void ChildProfileReuseRequiresResolvedApplicableFacts(string scenario, bool targetProfile)
    {
        DefinitionCollection sourceDefinitions = ChildProfileDefinitions("source", "http://example.org/source", targetProfile);
        DefinitionCollection targetDefinitions = ChildProfileDefinitions("target",
            scenario == "different" ? "http://example.org/target" : "http://example.org/source", targetProfile);
        if (scenario == "different")
        {
            targetDefinitions.ProfilesByUrl[ChildProfileUrl].Version = "child-2";
        }
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            StructureDefinition quantity = definitions.ComplexTypesByName["Quantity"];
            ElementDefinition.TypeRefComponent type = quantity.Snapshot.Element.Single(element =>
                element.Path == (targetProfile ? "Quantity.reference" : "Quantity.extension")).Type[0];
            if (scenario is "qualified" or "wrong-version")
            {
                string canonical = ChildProfileUrl + (scenario == "wrong-version" ? "|absent-version" : "|child-1");
                if (targetProfile)
                {
                    type.TargetProfile = [canonical];
                }
                else
                {
                    type.Profile = [canonical];
                }
            }
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        if (scenario is "missing" or "ambiguous" or "qualified")
        {
            foreach (DbStructureDefinition child in DbStructureDefinition.SelectList(imported.Connection)
                .Where(owner => owner.UnversionedUrl == ChildProfileUrl))
            {
                if (scenario == "missing")
                {
                    using IDbCommand delete = imported.Connection.CreateCommand();
                    delete.CommandText = $"DELETE FROM StructureDefinitionFacts WHERE StructureKey = {child.Key}; DELETE FROM Structures WHERE Key = {child.Key}";
                    delete.ExecuteNonQuery();
                }
                else
                {
                    StructureDefinition extra = (StructureDefinition)(child.FhirPackageKey == imported.PackageKey
                        ? sourceDefinitions : targetDefinitions).ProfilesByUrl[ChildProfileUrl].DeepCopy();
                    extra.Version = "child-2";
                    extra.Snapshot.Element[1].Fixed = new Hl7.Fhir.Model.FhirUri("http://example.org/other-version");
                    int key = DbStructureDefinition.SelectList(imported.Connection).Max(owner => owner.Key) + 1;
                    DbFhirPackage package = DbFhirPackage.SelectSingle(imported.Connection, Key: child.FhirPackageKey)!;
                    DbStructureDefinition.Insert(imported.Connection, new TestStructure(package, key, extra), insertPrimaryKey: true);
                    DbStructureDefinitionFacts.Insert(imported.Connection,
                        StructureDefinitionFactsStore.CreateFacts(key, package.Key, key, extra), insertPrimaryKey: true);
                    // Copy the exact owner projection with fresh keys, without introducing another native Extension.
                    foreach (DbElement element in DbElement.SelectList(imported.Connection, StructureKey: child.Key).OrderBy(row => row.ResourceFieldOrder))
                    {
                        int? parentOrder = element.ParentElementKey is null ? null : 0;
                        element.Key = DbElement.SelectList(imported.Connection).Max(row => row.Key) + 1;
                        element.StructureKey = key;
                        element.ParentElementKey = parentOrder is null ? null :
                            DbElement.SelectList(imported.Connection, StructureKey: key).Single(row => row.ResourceFieldOrder == parentOrder).Key;
                        DbElement.Insert(imported.Connection, element, insertPrimaryKey: true);
                    }
                }
            }
            // Stale importer links cannot be used to disambiguate an unversioned declaration.
            foreach (DbElementType type in DbElementType.SelectList(imported.Connection).Where(type =>
                UnsafeCodeCanonical.Parse(targetProfile ? type.TargetProfile : type.TypeProfile).Url == ChildProfileUrl))
            {
                type.TypeProfileStructureKey = null;
                type.TargetProfileStructureKey = null;
                DbElementType.Update(imported.Connection, type);
            }
        }
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        DbStructureDefinition source = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        DbStructureDefinition target = catalog.ResolveNativeType(targetPackage, "Quantity")!;
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(source), target);
        if (scenario is "missing" or "ambiguous" or "wrong-version")
        {
            proof.Kind.ShouldBe(ProfileProofKind.MissingFacts);
            proof.IsAcceptedDecision.ShouldBeFalse();
            proof.Diagnostics.ShouldContain(message => message.Contains(ChildProfileUrl) &&
                message.Contains(scenario == "wrong-version" ? "absent-version" : scenario));
        }
        else if (scenario == "different")
        {
            proof.Kind.ShouldBe(ProfileProofKind.Unrepresentable);
            proof.IsAcceptedDecision.ShouldBeTrue();
            proof.PreservesRestrictions.ShouldBeFalse();
            proof.Reasons.ShouldContain(reason => reason.Restriction != null &&
                reason.Restriction.Name == "fixedUri" && reason.Origin.Canonical == ChildProfileUrl);
            proof.ChildProofs.ShouldContain(child => child.TargetRoot!.Version == "child-2" &&
                child.RequiredFacts.Values.Any(element => element.Restrictions.Any(fact => fact.Name == "fixedUri")));
        }
        else
        {
            proof.Kind.ShouldBe(ProfileProofKind.NativeReuse, string.Join("; ", proof.Diagnostics));
            proof.PreservesRestrictions.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrdinaryChildTypesAreNotAcceptedNegatives(bool onTarget)
    {
        DefinitionCollection sourceDefinitions = UseDefinitions();
        DefinitionCollection targetDefinitions = UseDefinitions();
        targetDefinitions.MainPackageId = "example.target";
        (onTarget ? targetDefinitions : sourceDefinitions).ComplexTypesByName["Quantity"]
            .Snapshot.Element.Single(element => element.Path == "Quantity.code").Type.Clear();
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        proof.Kind.ShouldBe(ProfileProofKind.MissingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.Diagnostics.ShouldContain(message => message.Contains("Quantity.code") && message.Contains("type"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewOptionalSlicesDoNotInheritTheAggregateMinimum(bool patterned)
    {
        StructureDefinition parent = ConstraintProfile("AggregateQuantity");
        ElementDefinition group = parent.Snapshot.Element.Single(element => element.Path == "Quantity.extension");
        group.Min = 1;
        group.Max = "*";
        group.Pattern = patterned ? new Hl7.Fhir.Model.Extension { Url = "http://example.org/group-pattern" } : null;
        group.Slicing = new() { Rules = ElementDefinition.SlicingRules.Open,
            Discriminator = [new() { Type = ElementDefinition.DiscriminatorType.Value, Path = "url" }] };
        StructureDefinition child = (StructureDefinition)parent.DeepCopy();
        child.Id = child.Name = "OptionalSlices";
        child.Url = "http://example.org/OptionalSlices";
        child.BaseDefinition = parent.Url;
        foreach (string slice in new[] { "first", "second" })
        {
            child.Snapshot.Element.Add(new() { ElementId = "Quantity.extension:" + slice, Path = "Quantity.extension",
                SliceName = slice, Min = 0, Max = "1", Type = [new() { Code = "Extension" }] });
        }
        DefinitionCollection definitions = UseDefinitions(parent, child);
        definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.extension").Max = "*";
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, child.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Diagnostics.ShouldBeEmpty();
        branch.Elements[".extension"].Minimum.ShouldBe(1);
        foreach (string slice in new[] { "first", "second" })
        {
            EffectiveProfileElement element = branch.Elements[".extension:" + slice];
            element.Minimum.ShouldBe(0);
            element.Maximum.ShouldBe(1);
            element.Types.ShouldHaveSingleItem().Code.ShouldBe("Extension");
            if (patterned)
            {
                ProfileRestriction[] patterns = [.. element.Restrictions.Where(fact => fact.Kind == ProfileRestrictionKind.Pattern)];
                patterns.ShouldNotBeEmpty();
                foreach (ProfileRestriction pattern in patterns)
                {
                    pattern.Name.ShouldBe("patternExtension");
                    pattern.Value.GetProperty("url").GetString().ShouldBe("http://example.org/group-pattern");
                }
            }
            element.Restrictions.ShouldContain(fact => fact.Kind == ProfileRestrictionKind.Slicing);
        }
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.Kind.ShouldBe(patterned ? ProfileProofKind.Unsupported : ProfileProofKind.NativeReuse);
        proof.IsAcceptedDecision.ShouldBe(!patterned);
    }

    [Fact]
    public void ExactInheritedSliceMinimumCannotBeLoosenedOrBorrowedByAnotherSlice()
    {
        StructureDefinition parent = ConstraintProfile("RequiredSlice");
        parent.Snapshot.Element.Add(new() { ElementId = "Quantity.extension:required", Path = "Quantity.extension",
            SliceName = "required", Min = 1, Max = "1", Type = [new() { Code = "Extension" }] });
        StructureDefinition child = (StructureDefinition)parent.DeepCopy();
        child.Id = child.Name = "LoosenedSlice";
        child.Url = "http://example.org/LoosenedSlice";
        child.BaseDefinition = parent.Url;
        child.Snapshot.Element.Single(element => element.SliceName == "required").Min = 0;
        child.Snapshot.Element.Add(new() { ElementId = "Quantity.extension:optional", Path = "Quantity.extension",
            SliceName = "optional", Min = 0, Max = "1", Type = [new() { Code = "Extension" }] });
        using CatalogImport imported = ImportUses(parent, child);
        using ProfileUseFacts facts = new(imported.Catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = imported.Catalog.ResolveDefinition(imported.PackageKey, child.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Elements[".extension:required"].Minimum.ShouldBe(1);
        branch.Elements[".extension:optional"].Minimum.ShouldBe(0);
        branch.Diagnostics.ShouldContain(message => message.Contains("required") && message.Contains("illegally loosens"));
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.Kind.ShouldBe(ProfileProofKind.ConflictingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildProfileAlternativesRemainAUnionDuringProof(bool targetProfile)
    {
        DefinitionCollection sourceDefinitions = ChildProfileDefinitions("source", "http://example.org/first", targetProfile);
        DefinitionCollection targetDefinitions = ChildProfileDefinitions("target", "http://example.org/first", targetProfile);
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            StructureDefinition other = (StructureDefinition)definitions.ProfilesByUrl[ChildProfileUrl].DeepCopy();
            other.Id = other.Name = "OtherExtension";
            other.Url = "http://example.org/OtherExtension";
            other.Snapshot.Element[1].Fixed = new Hl7.Fhir.Model.FhirUri("http://example.org/second");
            AddProfile(definitions, other);
            ElementDefinition.TypeRefComponent type = definitions.ComplexTypesByName["Quantity"].Snapshot.Element
                .Single(element => element.Path == (targetProfile ? "Quantity.reference" : "Quantity.extension")).Type[0];
            string[] alternatives = definitions == sourceDefinitions ? [ChildProfileUrl, other.Url] : [other.Url, ChildProfileUrl];
            if (targetProfile)
            {
                type.TargetProfile = [.. alternatives];
            }
            else
            {
                type.Profile = [.. alternatives];
            }
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        proof.Kind.ShouldBe(ProfileProofKind.NativeReuse, string.Join("; ", proof.Diagnostics));
        proof.ChildProofs.ShouldNotBeEmpty();
        proof.ChildProofs.ShouldAllBe(child => child.Kind == ProfileProofKind.NativeReuse);
    }

    [Fact]
    public void RecursiveChildProfileObligationsNeverAssumeTheirOwnProof()
    {
        DefinitionCollection definitions = ChildProfileDefinitions("recursive", "http://example.org/recursive", targetProfile: false);
        definitions.ProfilesByUrl[ChildProfileUrl].Snapshot.Element.Add(new()
        {
            ElementId = "Extension.extension", Path = "Extension.extension", Min = 0, Max = "*",
            Type = [new() { Code = "Extension", Profile = [ChildProfileUrl] }],
        });
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition quantity = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(quantity), quantity);
        proof.Kind.ShouldBe(ProfileProofKind.Unsupported);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.Diagnostics.ShouldContain(message => message.Contains("recursive"));
    }

    [Fact]
    public void ValidContentReferenceSuppliesEffectiveFactsWithoutInventingAType()
    {
        DefinitionCollection definitions = UseDefinitions();
        ElementDefinition code = definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.code");
        definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.system").Min = 1;
        code.Type.Clear();
        code.ContentReference = "#Quantity.system";
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition quantity = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        EffectiveProfileBranch branch = facts.ForDefinition(quantity);
        branch.Elements[".code"].Types.ShouldHaveSingleItem().Code.ShouldBe("uri");
        branch.Elements[".code"].Minimum.ShouldBe(0);
        ProfileRepresentationProof proof = facts.Prove(branch, quantity);
        proof.Kind.ShouldBe(ProfileProofKind.NativeReuse, string.Join("; ", proof.Diagnostics));
    }

    [Fact]
    public void UnrelatedRecursiveOwnerChildrenDoNotBlockAQuantityOccurrence()
    {
        DefinitionCollection definitions = UseDefinitions();
        StructureDefinition holder = definitions.ResourcesByName["UseHolder"];
        holder.Snapshot.Element.Add(new() { ElementId = "UseHolder.group", Path = "UseHolder.group", Min = 0, Max = "*",
            Type = [new() { Code = "BackboneElement" }] });
        holder.Snapshot.Element.Add(new() { ElementId = "UseHolder.group.again", Path = "UseHolder.group.again",
            Min = 0, Max = "*", ContentReference = "#UseHolder.group" });
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        EffectiveProfileUse use = facts.AtUse(owner, catalog.ResolveOwnedElement(owner, "UseHolder.quantity")!, 0);
        use.Diagnostics.ShouldBeEmpty();
        EffectiveProfileBranch branch = use.Branches.ShouldHaveSingleItem();
        branch.Diagnostics.ShouldBeEmpty();
        facts.Prove(branch, catalog.ResolveNativeType(imported.PackageKey, "Quantity")!, nativeContext: branch)
            .Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Fact]
    public void DeclaredRootsAndProhibitedChildrenNeedNoInventedTypes()
    {
        DefinitionCollection definitions = UseDefinitions();
        ElementDefinition code = definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.code");
        code.Type.Clear();
        code.Max = "0";
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition quantity = catalog.ResolveNativeType(imported.PackageKey, "Quantity")!;
        EffectiveProfileBranch branch = facts.ForDefinition(quantity);
        branch.Elements[""].Types.ShouldBeEmpty();
        branch.Elements[".code"].Types.ShouldBeEmpty();
        facts.Prove(branch, quantity).Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Fact]
    public void OmittedLocalTypesRequireActualInheritedDatatypeFacts()
    {
        StructureDefinition profile = ConstraintProfile("InheritedDatatype");
        profile.Snapshot.Element.Single(element => element.Path == "Quantity.code").Type.Clear();
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Elements[".code"].Types.ShouldHaveSingleItem().Code.ShouldBe("code");
        branch.Elements[".code"].Origins.ShouldContain(origin => origin.Canonical == QuantityUrl);
        facts.Prove(branch, owner).Kind.ShouldBe(ProfileProofKind.NativeReuse);
    }

    [Fact]
    public void NewSlicesStillRespectTheUnslicedMaximum()
    {
        StructureDefinition profile = ConstraintProfile("ExcessiveSlice");
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity.extension:excessive", Path = "Quantity.extension",
            SliceName = "excessive", Min = 0, Max = "2", Type = [new() { Code = "Extension" }] });
        using CatalogImport imported = ImportUses(profile);
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveDefinition(imported.PackageKey, profile.Url)!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        branch.Elements[".extension:excessive"].Maximum.ShouldBe(1);
        branch.Diagnostics.ShouldContain(message => message.Contains("excessive") && message.Contains("illegally loosens"));
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.Kind.ShouldBe(ProfileProofKind.ConflictingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IncompleteChildProfilesCannotHideBehindKnownOuterNegatives(bool unknownRestriction, bool onTarget)
    {
        DefinitionCollection sourceDefinitions = ChildProfileDefinitions("source", "http://example.org/first", targetProfile: false);
        DefinitionCollection targetDefinitions = ChildProfileDefinitions("target", "http://example.org/first", targetProfile: false);
        sourceDefinitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.comparator").Max = "0";
        DefinitionCollection incomplete = onTarget ? targetDefinitions : sourceDefinitions;
        if (unknownRestriction)
        {
            incomplete.ProfilesByUrl[ChildProfileUrl].Snapshot.Element[1].Extension.Add(
                new("http://example.org/unclassified", new Hl7.Fhir.Model.FhirBoolean(true)));
        }
        else
        {
            incomplete.ProfilesByUrl[ChildProfileUrl].Snapshot.Element[1].Type.Clear();
            incomplete.ComplexTypesByName["Extension"].Snapshot.Element[1].Type.Clear();
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        proof.Kind.ShouldBe(unknownRestriction ? ProfileProofKind.Unsupported : ProfileProofKind.MissingFacts);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Diagnostics.ShouldContain(message => message.Contains(ChildProfileUrl));
        if (unknownRestriction)
        {
            proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.UnclassifiedRestriction &&
                reason.Restriction!.Value.GetProperty("url").GetString() == "http://example.org/unclassified");
        }
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("no-regex")]
    [InlineData("regex")]
    [InlineData("representation")]
    [InlineData("context")]
    [InlineData("legacy-regex")]
    [InlineData("regex-url")]
    [InlineData("direct-code")]
    [InlineData("core-bridge")]
    [InlineData("string-integer-bridge")]
    public void PrimitiveTypeObligationsRequireEqualSemanticsInProvenDatatypeContexts(string scenario)
    {
        DefinitionCollection sourceDefinitions = PrimitiveObligationDefinitions("source");
        DefinitionCollection targetDefinitions = PrimitiveObligationDefinitions("target", scenario == "context" ? "OtherPrimitive" : "dateTime");
        ElementDefinition.TypeRefComponent sourceType = sourceDefinitions.PrimitiveTypesByName["dateTime"].Snapshot.Element[1].Type[0];
        ElementDefinition.TypeRefComponent targetType = targetDefinitions.PrimitiveTypesByName.Values.Single().Snapshot.Element[1].Type[0];
        if (scenario == "no-regex")
        {
            sourceType.Extension.RemoveAt(0);
            targetType.Extension.RemoveAt(0);
        }
        else if (scenario == "regex")
        {
            targetType.Extension[0].Value = new Hl7.Fhir.Model.FhirString("[0-9]+");
        }
        else if (scenario == "representation")
        {
            targetType.Extension[1].Value = new Hl7.Fhir.Model.FhirUrl("instant");
            targetType.Extension[3].Value = new Hl7.Fhir.Model.FhirUrl("xs:dateTime");
        }
        else if (scenario is "legacy-regex" or "regex-url")
        {
            targetType.Extension[0].Url = CommonDefinitions.ExtUrlSdRegex2;
            if (scenario == "legacy-regex")
            {
                sourceType.Extension[0].Url = CommonDefinitions.ExtUrlSdRegex2;
            }
        }
        else if (scenario == "direct-code")
        {
            foreach (ElementDefinition.TypeRefComponent type in new[] { sourceType, targetType })
            {
                type.Code = "dateTime";
                type.Extension.RemoveRange(1, 3);
            }
        }
        else if (scenario == "core-bridge")
        {
            sourceType.Extension.RemoveRange(2, 2);
            targetType.Extension.RemoveRange(2, 2);
        }
        else if (scenario == "string-integer-bridge")
        {
            foreach (ElementDefinition.TypeRefComponent type in new[] { sourceType, targetType })
            {
                type.Code = "http://hl7.org/fhirpath/System.String";
                type.Extension[1].Value = new Hl7.Fhir.Model.FhirUrl("positiveInt");
                type.Extension[2].Value = new Hl7.Fhir.Model.FhirUrl("number");
                type.Extension[3].Value = new Hl7.Fhir.Model.FhirUrl("xs:positiveInteger");
            }
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        DbStructureDefinition source = catalog.ResolveNativeType(imported.PackageKey, "dateTime")!;
        DbStructureDefinition target = catalog.ResolveNativeType(targetPackage, scenario == "context" ? "OtherPrimitive" : "dateTime")!;
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(source), target);
        bool equal = scenario is "equal" or "no-regex" or "legacy-regex" or "direct-code" or "core-bridge" or "string-integer-bridge";
        proof.Kind.ShouldBe(equal ? ProfileProofKind.NativeReuse : ProfileProofKind.Unrepresentable,
            string.Join("; ", proof.Diagnostics));
        proof.IsAcceptedDecision.ShouldBeTrue();
        proof.PreservesRestrictions.ShouldBe(equal);
        ProfileRestriction retained = proof.RequiredFacts[".value"].Restrictions.ShouldHaveSingleItem();
        retained.Name.ShouldBe("primitive type obligations");
        retained.Origin.ShouldBe(new(imported.PackageKey, source.Key, source.UnversionedUrl, source.Version, "dateTime.value"));
        using JsonDocument expectedType = JsonDocument.Parse(sourceType.ToJson());
        JsonElement.DeepEquals(retained.Value, expectedType.RootElement).ShouldBeTrue();
        retained.Value.GetProperty("code").GetString().ShouldBe(sourceType.Code);
        if (!equal)
        {
            proof.TargetCanonical.ShouldBeNull();
            proof.Projection.ShouldBeEmpty();
            proof.ProjectionOwner.ShouldBeNull();
            proof.Reasons.ShouldContain(reason => scenario == "context"
                ? reason.Category == ProfileReasonCategory.DatatypeContext
                : reason.Category == ProfileReasonCategory.RestrictionTransformation &&
                    reason.Restriction != null && reason.Restriction.Name == retained.Name);
        }
    }

    [Theory]
    [InlineData("missing-fhir", false)]
    [InlineData("missing-json", false)]
    [InlineData("missing-xml", false)]
    [InlineData("missing-all", false)]
    [InlineData("wrong-system", false)]
    [InlineData("unknown-system", false)]
    [InlineData("wrong-value-kind", false)]
    [InlineData("empty-regex", false)]
    [InlineData("empty-xml", false)]
    [InlineData("unknown-fhir", false)]
    [InlineData("unknown-json", false)]
    [InlineData("unknown-xml", false)]
    [InlineData("unknown-url", false)]
    [InlineData("duplicate-regex", false)]
    [InlineData("duplicate-fhir", false)]
    [InlineData("duplicate-json", false)]
    [InlineData("duplicate-xml", false)]
    [InlineData("duplicate-regex-alias", false)]
    [InlineData("nested", false)]
    [InlineData("missing-fhir", true)]
    [InlineData("unknown-url", true)]
    [InlineData("duplicate-regex", true)]
    public void InvalidPrimitiveTypeObligationsCannotCertifyEqualOrDifferentContexts(string scenario, bool differentContext)
    {
        DefinitionCollection sourceDefinitions = PrimitiveObligationDefinitions("source");
        DefinitionCollection targetDefinitions = PrimitiveObligationDefinitions("target", differentContext ? "OtherPrimitive" : "dateTime");
        ElementDefinition.TypeRefComponent invalid = targetDefinitions.PrimitiveTypesByName.Values.Single().Snapshot.Element[1].Type[0];
        switch (scenario)
        {
            case "missing-fhir": invalid.Extension.RemoveAt(1); break;
            case "missing-json": invalid.Extension.RemoveAt(2); break;
            case "missing-xml": invalid.Extension.RemoveAt(3); break;
            case "missing-all": invalid.Extension.Clear(); break;
            case "wrong-system": invalid.Code = "http://hl7.org/fhirpath/System.String"; break;
            case "unknown-system": invalid.Code = "http://example.org/System.DateTime"; break;
            case "wrong-value-kind": invalid.Extension[0].Value = new Hl7.Fhir.Model.FhirUrl("[0-9]+"); break;
            case "empty-regex": invalid.Extension[0].Value = new Hl7.Fhir.Model.FhirString(" "); break;
            case "empty-xml": invalid.Extension[3].Value = new Hl7.Fhir.Model.FhirUrl(" "); break;
            case "unknown-fhir": invalid.Extension[1].Value = new Hl7.Fhir.Model.FhirUrl("unknown"); break;
            case "unknown-json": invalid.Extension[2].Value = new Hl7.Fhir.Model.FhirUrl("object"); break;
            case "unknown-xml": invalid.Extension[3].Value = new Hl7.Fhir.Model.FhirUrl("xs:unknown"); break;
            case "unknown-url": invalid.Extension[0].Url = "http://example.org/StructureDefinition/regex"; break;
            case "duplicate-regex": invalid.Extension.Add((Hl7.Fhir.Model.Extension)invalid.Extension[0].DeepCopy()); break;
            case "duplicate-fhir": invalid.Extension.Add((Hl7.Fhir.Model.Extension)invalid.Extension[1].DeepCopy()); break;
            case "duplicate-json": invalid.Extension.Add((Hl7.Fhir.Model.Extension)invalid.Extension[2].DeepCopy()); break;
            case "duplicate-xml": invalid.Extension.Add((Hl7.Fhir.Model.Extension)invalid.Extension[3].DeepCopy()); break;
            case "duplicate-regex-alias": invalid.Extension.Add(new(CommonDefinitions.ExtUrlSdRegex2, new Hl7.Fhir.Model.FhirString("[0-9]+"))); break;
            case "nested": invalid.Extension[0].Extension.Add(new("http://example.org/nested", new Hl7.Fhir.Model.FhirString("unknown"))); break;
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "dateTime")!),
            catalog.ResolveNativeType(targetPackage, differentContext ? "OtherPrimitive" : "dateTime")!);
        proof.Kind.ShouldBe(ProfileProofKind.Unsupported);
        proof.IsAcceptedDecision.ShouldBeFalse();
        proof.PreservesRestrictions.ShouldBeFalse();
        proof.TargetCanonical.ShouldBeNull();
        proof.Projection.ShouldBeEmpty();
        proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.UnclassifiedRestriction &&
            reason.Origin.PackageKey == targetPackage && reason.Restriction != null);
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("different")]
    [InlineData("absent")]
    public void RepetitionOrderMeaningIsAnOpaqueEqualityOnlyObligation(string scenario)
    {
        DefinitionCollection sourceDefinitions = UseDefinitions();
        DefinitionCollection targetDefinitions = UseDefinitions();
        targetDefinitions.MainPackageId = "example.target";
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            ElementDefinition unit = definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.unit");
            unit.Max = "*";
            unit.OrderMeaning = definitions == sourceDefinitions || scenario == "equal"
                ? "Units appear in presentation order" : scenario == "different" ? "Units appear in reverse order" : null;
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        proof.Kind.ShouldBe(scenario == "equal" ? ProfileProofKind.NativeReuse : ProfileProofKind.Unrepresentable);
        proof.IsAcceptedDecision.ShouldBeTrue();
        proof.PreservesRestrictions.ShouldBe(scenario == "equal");
        proof.RequiredFacts[".unit"].Restrictions.ShouldContain(fact => fact.Name == "orderMeaning" &&
            fact.Value.GetString() == "Units appear in presentation order");
        if (scenario != "equal")
        {
            proof.TargetCanonical.ShouldBeNull();
            proof.Projection.ShouldBeEmpty();
            proof.Reasons.ShouldContain(reason => reason.Restriction != null && reason.Restriction.Name == "orderMeaning" &&
                reason.Category == ProfileReasonCategory.RestrictionTransformation);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NumericDefaultsAreRetainedWithoutInferringAMissingValueTransform(bool integer, bool different)
    {
        DefinitionCollection sourceDefinitions = UseDefinitions();
        DefinitionCollection targetDefinitions = UseDefinitions();
        targetDefinitions.MainPackageId = "example.target";
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            ElementDefinition value = definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.value");
            value.Type[0].Code = integer ? "integer" : "decimal";
            int number = definitions == targetDefinitions && different ? 2 : 1;
            value.DefaultValue = integer ? new Hl7.Fhir.Model.Integer(number) : new Hl7.Fhir.Model.FhirDecimal(number);
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        proof.Kind.ShouldBe(different ? ProfileProofKind.Unrepresentable : ProfileProofKind.NativeReuse);
        proof.IsAcceptedDecision.ShouldBeTrue();
        proof.PreservesRestrictions.ShouldBe(!different);
        proof.RequiredFacts[".value"].Restrictions.ShouldContain(fact =>
            fact.Name == (integer ? "defaultValueInteger" : "defaultValueDecimal") && fact.Value.GetDecimal() == 1);
        if (different)
        {
            proof.TargetCanonical.ShouldBeNull();
            proof.Projection.ShouldBeEmpty();
            proof.Reasons.ShouldContain(reason => reason.Category == ProfileReasonCategory.RestrictionTransformation &&
                reason.Restriction != null && reason.Restriction.Name.StartsWith("defaultValue", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("different")]
    [InlineData("absent")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    public void BestPracticeInvariantAnnotationsRemainRequiredAndStrict(string scenario)
    {
        DefinitionCollection sourceDefinitions = UseDefinitions();
        DefinitionCollection targetDefinitions = UseDefinitions();
        targetDefinitions.MainPackageId = "example.target";
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            ElementDefinition.ConstraintComponent invariant = new()
            {
                Key = "ordering-advice", Severity = Hl7.Fhir.Model.ConstraintSeverity.Warning,
                Human = "An opaque recommendation", Expression = "value.exists()",
                Extension =
                [
                    new("http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice", new Hl7.Fhir.Model.FhirBoolean(true)),
                    new("http://hl7.org/fhir/StructureDefinition/elementdefinition-bestpractice-explanation", new Hl7.Fhir.Model.Markdown("Retain this exact explanation")),
                ],
            };
            if (definitions == targetDefinitions)
            {
                switch (scenario)
                {
                    case "different": invariant.Extension[0].Value = new Hl7.Fhir.Model.FhirBoolean(false); break;
                    case "absent": invariant.Extension.Clear(); break;
                    case "unknown": invariant.Extension[0].Url = "http://example.org/elementdefinition-bestpractice"; break;
                    case "malformed": invariant.Extension[0].Value = new Hl7.Fhir.Model.FhirString("true"); break;
                    case "duplicate": invariant.Extension.Add((Hl7.Fhir.Model.Extension)invariant.Extension[0].DeepCopy()); break;
                }
            }
            definitions.ComplexTypesByName["Quantity"].Snapshot.Element[0].Constraint.Add(invariant);
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            catalog.ResolveNativeType(targetPackage, "Quantity")!);
        bool invalid = scenario is "unknown" or "malformed" or "duplicate";
        proof.Kind.ShouldBe(invalid ? ProfileProofKind.Unsupported :
            scenario == "equal" ? ProfileProofKind.NativeReuse : ProfileProofKind.Unrepresentable);
        proof.IsAcceptedDecision.ShouldBe(!invalid);
        proof.PreservesRestrictions.ShouldBe(scenario == "equal");
        proof.RequiredFacts[""].Invariants.ShouldHaveSingleItem().Value.GetProperty("extension").GetArrayLength().ShouldBe(2);
        if (scenario != "equal")
        {
            proof.TargetCanonical.ShouldBeNull();
            proof.Projection.ShouldBeEmpty();
            proof.Reasons.ShouldContain(reason => reason.Restriction != null &&
                reason.Category == (invalid ? ProfileReasonCategory.UnclassifiedRestriction : ProfileReasonCategory.InvariantTransformation));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimitiveInheritanceUsesOnlyExplicitValidatedBridgeIdentity(bool missingBridge)
    {
        DefinitionCollection definitions = Definitions();
        StructureDefinition parent = Definition("string", "string");
        parent.Kind = StructureDefinition.StructureDefinitionKind.PrimitiveType;
        parent.Snapshot.Element.Add(new()
        {
            ElementId = "string.value", Path = "string.value", Min = 0, Max = "1",
            Type = [new()
            {
                Code = "http://hl7.org/fhirpath/System.String",
                Extension = missingBridge ? [] :
                [
                    new(CommonDefinitions.ExtUrlFhirType, new Hl7.Fhir.Model.FhirUrl("string")),
                    new(CommonDefinitions.ExtUrlJsonType, new Hl7.Fhir.Model.FhirUrl("string")),
                    new(CommonDefinitions.ExtUrlXmlType, new Hl7.Fhir.Model.FhirUrl("xs:string")),
                ],
            }],
        });
        definitions.AddPrimitiveType(parent, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        StructureDefinition child = Definition("id", "id", parent.Url);
        child.Kind = StructureDefinition.StructureDefinitionKind.PrimitiveType;
        child.Snapshot.Element.Add(new()
        {
            ElementId = "id.value", Path = "id.value", Min = 0, Max = "1", Type = [new() { Code = "string" }],
        });
        definitions.AddPrimitiveType(child, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveNativeType(imported.PackageKey, "id")!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.IsAcceptedDecision.ShouldBe(!missingBridge);
        proof.PreservesRestrictions.ShouldBe(!missingBridge);
        if (!missingBridge)
        {
            proof.Kind.ShouldBe(ProfileProofKind.NativeReuse, string.Join("; ", proof.Diagnostics));
            branch.Elements[".value"].Restrictions.ShouldContain(fact => fact.Name == "primitive type obligations" &&
                fact.Origin.Canonical == parent.Url);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativePrimitiveStringIdStorageRetainsTheInheritedIdDomain(bool primitive)
    {
        DefinitionCollection definitions = Definitions();
        StructureDefinition parent = Definition("Element", "Element");
        parent.Snapshot.Element.Add(new()
        {
            ElementId = "Element.id", Path = "Element.id", Min = 0, Max = "1",
            Type = [new() { Code = "http://hl7.org/fhirpath/System.String",
                Extension = [new(CommonDefinitions.ExtUrlFhirType, new Hl7.Fhir.Model.FhirUrl("id"))] }],
        });
        AddNative(definitions, parent);
        StructureDefinition child = Definition("dateTime", "dateTime", parent.Url);
        child.Kind = primitive ? StructureDefinition.StructureDefinitionKind.PrimitiveType : StructureDefinition.StructureDefinitionKind.ComplexType;
        child.Snapshot.Element.Add(new()
        {
            ElementId = "dateTime.id", Path = "dateTime.id", Min = 0, Max = "1",
            Type = [new() { Code = "http://hl7.org/fhirpath/System.String",
                Extension = [new(CommonDefinitions.ExtUrlFhirType, new Hl7.Fhir.Model.FhirUrl("string"))] }],
        });
        if (primitive)
        {
            definitions.AddPrimitiveType(child, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        }
        else
        {
            AddNative(definitions, child);
        }
        using CatalogImport imported = new(definitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, imported.PackageKey);
        DbStructureDefinition owner = catalog.ResolveNativeType(imported.PackageKey, "dateTime")!;
        EffectiveProfileBranch branch = facts.ForDefinition(owner);
        ProfileRepresentationProof proof = facts.Prove(branch, owner);
        proof.IsAcceptedDecision.ShouldBe(primitive);
        proof.PreservesRestrictions.ShouldBe(primitive);
        if (primitive)
        {
            branch.Elements[".id"].Types.ShouldHaveSingleItem().FhirType.ShouldBe("id");
            branch.Elements[".id"].Origins.ShouldContain(origin => origin.Canonical == parent.Url && origin.ElementId == "Element.id");
            branch.Elements[".id"].Origins.ShouldContain(origin => origin.Canonical == child.Url && origin.ElementId == "dateTime.id");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Integer64BoundsRetainExactStringValuesWithoutProjection(bool different, bool malformed)
    {
        DefinitionCollection sourceDefinitions = UseDefinitions();
        DefinitionCollection targetDefinitions = UseDefinitions();
        targetDefinitions.MainPackageId = "example.target";
        foreach (DefinitionCollection definitions in new[] { sourceDefinitions, targetDefinitions })
        {
            ElementDefinition value = definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Single(element => element.Path == "Quantity.value");
            value.Type[0].Code = "integer64";
            value.MinValue = new Hl7.Fhir.Model.Integer64(long.MinValue);
            value.MaxValue = new Hl7.Fhir.Model.Integer64(definitions == targetDefinitions && different ? long.MaxValue - 1 : long.MaxValue);
        }
        using CatalogImport imported = new(sourceDefinitions, targetDefinitions);
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        XVerStructureCatalog catalog = imported.Catalog;
        int targetPackage = DbFhirPackage.SelectList(imported.Connection).Single(package => package.Key != imported.PackageKey).Key;
        using ProfileUseFacts facts = new(catalog, imported.PackageKey, targetPackage);
        DbStructureDefinition target = catalog.ResolveNativeType(targetPackage, "Quantity")!;
        EffectiveProfileBranch targetBranch = facts.ForDefinition(target);
        if (malformed)
        {
            EffectiveProfileElement value = targetBranch.Elements[".value"];
            targetBranch = targetBranch with { Elements = targetBranch.Elements.SetItem(".value", value with
            {
                Restrictions = [.. value.Restrictions.Select(fact => fact.Name == "maxValueInteger64"
                    ? fact with { Value = JsonSerializer.SerializeToElement("9223372036854775808") } : fact)],
            }) };
        }
        ProfileRepresentationProof proof = facts.Prove(facts.ForDefinition(catalog.ResolveNativeType(imported.PackageKey, "Quantity")!),
            target, nativeContext: targetBranch);
        proof.Kind.ShouldBe(malformed ? ProfileProofKind.Unsupported : different ? ProfileProofKind.Unrepresentable : ProfileProofKind.NativeReuse);
        proof.IsAcceptedDecision.ShouldBe(!malformed);
        proof.PreservesRestrictions.ShouldBe(!different && !malformed);
        proof.RequiredFacts[".value"].Restrictions.ShouldContain(fact => fact.Name == "minValueInteger64" &&
            fact.Value.ValueKind == JsonValueKind.String && fact.Value.GetString() == "-9223372036854775808");
        if (different || malformed)
        {
            proof.TargetCanonical.ShouldBeNull();
            proof.Projection.ShouldBeEmpty();
            proof.Reasons.ShouldContain(reason => reason.Restriction != null && reason.Restriction.Name == "maxValueInteger64" &&
                reason.Category == (malformed ? ProfileReasonCategory.UnclassifiedRestriction : ProfileReasonCategory.RestrictionTransformation));
        }
    }

    private static DefinitionCollection PrimitiveObligationDefinitions(string package, string name = "dateTime")
    {
        DefinitionCollection definitions = Definitions();
        definitions.MainPackageId = "example." + package;
        StructureDefinition primitive = Definition(name, name);
        primitive.Kind = StructureDefinition.StructureDefinitionKind.PrimitiveType;
        primitive.Snapshot.Element.Add(new()
        {
            ElementId = name + ".value", Path = name + ".value", Min = 0, Max = "1",
            Type = [new()
            {
                Code = "http://hl7.org/fhirpath/System.DateTime",
                Extension =
                [
                    new("http://hl7.org/fhir/StructureDefinition/regex", new Hl7.Fhir.Model.FhirString(@"\i\c*")),
                    new(CommonDefinitions.ExtUrlFhirType, new Hl7.Fhir.Model.FhirUrl("dateTime")),
                    new(CommonDefinitions.ExtUrlJsonType, new Hl7.Fhir.Model.FhirUrl("string")),
                    new(CommonDefinitions.ExtUrlXmlType, new Hl7.Fhir.Model.FhirUrl("xs:dateTime, xs:date, xs:gYearMonth, xs:gYear")),
                ],
            }],
        });
        definitions.AddPrimitiveType(primitive, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        return definitions;
    }

    private const string PassiveTerminologyUrl = "http://example.org/StructureDefinition/PassiveTerminology";
    private const string PassiveTerminologyParentUrl = "http://example.org/StructureDefinition/InheritedTerminology";

    private static string TerminologySystem(string id) => "http://example.org/CodeSystem/" + id;
    private static string TerminologyValueSet(string id) => "http://example.org/ValueSet/" + id + "|terms-1";

    private static ElementDefinition.ElementDefinitionBindingComponent TerminologyBinding(string primary, string? additional = null) => new()
    {
        Strength = Hl7.Fhir.Model.BindingStrength.Required,
        ValueSet = TerminologyValueSet(primary),
        Additional = additional is null ? [] : [new()
        {
            Purpose = ElementDefinition.AdditionalBindingPurposeVS.Maximum, ValueSet = TerminologyValueSet(additional),
        }],
    };

    private static DefinitionCollection TerminologyDefinitions(bool passiveBindings, string? reference = null)
    {
        DefinitionCollection definitions = Definitions(FhirReleases.FhirSequenceCodes.R5);
        definitions.FhirVersion = Hl7.Fhir.Model.FHIRVersion.N5_0_0;
        definitions.FhirVersionLiteral = definitions.MainPackageVersion = "5.0.0";
        definitions.MainPackageId = "hl7.fhir.r5.core";
        foreach (string id in new[] { "native", "native-additional", "eligible", "inherited", "inherited-additional",
            "passive", "passive-additional", "unrelated", "outside", "other-tho" })
        {
            Hl7.Fhir.Model.CodeSystem system = new()
            {
                Id = id, Name = id.Replace("-", ""), Url = TerminologySystem(id), Version = "terms-1",
                Status = Hl7.Fhir.Model.PublicationStatus.Active, Content = Hl7.Fhir.Model.CodeSystemContentMode.Complete,
                CaseSensitive = true, Count = 1,
                Property = [new() { Code = "flag", Type = Hl7.Fhir.Model.CodeSystem.PropertyType.Boolean }],
                Filter = [new() { Code = "flag", Operator = [Hl7.Fhir.Model.FilterOperator.Equal], Value = "true" }],
                Concept = [new() { Code = "retained", Display = "Retained",
                    Property = [new() { Code = "flag", Value = new Hl7.Fhir.Model.FhirBoolean(true) }] }],
            };
            definitions.AddCodeSystem(system, id == "outside" ? definitions.MainPackageId : "hl7.terminology",
                id == "outside" ? definitions.MainPackageVersion : id == "other-tho" ? "5.2.0" : "5.1.0");
            Hl7.Fhir.Model.ValueSet valueSet = new()
            {
                Id = id, Name = id.Replace("-", ""), Url = TerminologyValueSet(id).Split('|')[0], Version = "terms-1",
                Status = Hl7.Fhir.Model.PublicationStatus.Active,
                Compose = new() { Include = [new() { System = system.Url, Version = system.Version,
                    Concept = [new() { Code = "retained", Display = "Retained" }] }] },
                Expansion = new() { Total = 1, Contains = [new()
                    { System = system.Url, Version = system.Version, Code = "retained", Display = "Retained" }] },
            };
            definitions.AddValueSet(valueSet, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        }

        StructureDefinition quantity = NativeQuantity();
        quantity.Snapshot.Element.Single(element => element.Path == "Quantity.code").Binding = TerminologyBinding("native", "native-additional");
        quantity.Snapshot.Element.Add(new() { ElementId = "Quantity.status", Path = "Quantity.status",
            Min = 0, Max = "1", Type = [new() { Code = "code" }] });
        string datatype = reference == "target" ? "Patient" : reference == "base" ? "Quantity" : "Coding";
        StructureDefinition native = datatype == "Quantity" ? quantity : Definition(datatype, datatype);
        if (datatype != "Quantity")
        {
            native.Kind = datatype == "Patient" ? StructureDefinition.StructureDefinitionKind.Resource : StructureDefinition.StructureDefinitionKind.ComplexType;
            native.Snapshot.Element.Add(new() { ElementId = datatype + ".code", Path = datatype + ".code",
                Min = 0, Max = "1", Type = [new() { Code = "code" }] });
            native.Snapshot.Element.Add(new() { ElementId = datatype + ".status", Path = datatype + ".status",
                Min = 0, Max = "1", Type = [new() { Code = "code" }] });
        }
        StructureDefinition parent = (StructureDefinition)native.DeepCopy();
        parent.Id = parent.Name = "InheritedTerminology";
        parent.Url = PassiveTerminologyParentUrl;
        parent.BaseDefinition = native.Url;
        parent.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
        parent.Snapshot.Element.Single(element => element.Path == datatype + ".code").Binding =
            passiveBindings ? TerminologyBinding("inherited", "inherited-additional") : null;
        StructureDefinition passive = (StructureDefinition)parent.DeepCopy();
        passive.Id = passive.Name = "SameName";
        passive.Url = PassiveTerminologyUrl;
        passive.Version = "passive-definition";
        passive.BaseDefinition = parent.Url + "|" + parent.Version;
        passive.Snapshot.Element.Single(element => element.Path == datatype + ".status").Binding =
            passiveBindings ? TerminologyBinding("passive", "passive-additional") : null;
        passive.Snapshot.Element.Single(element => element.Path == datatype + ".code").Binding = null;
        StructureDefinition unrelated = Profile("SameName", "Coding", baseDefinition: QuantityUrl.Replace("Quantity", "Coding"));
        unrelated.Url = "http://example.org/StructureDefinition/SameName";
        unrelated.Version = passive.Version;
        unrelated.Snapshot.Element[0].Binding = TerminologyBinding("unrelated");
        if (reference == "type")
        {
            ElementDefinition.TypeRefComponent use = quantity.Snapshot.Element.Single(element => element.Path == "Quantity.extension").Type[0];
            use.Code = datatype;
            use.Profile = [passive.Url + "|" + passive.Version];
        }
        else if (reference == "target")
        {
            AddNative(definitions, Definition("Reference", "Reference"));
            quantity.Snapshot.Element.Add(new() { ElementId = "Quantity.reference", Path = "Quantity.reference", Min = 0, Max = "1",
                Type = [new() { Code = "Reference", TargetProfile = [passive.Url + "|" + passive.Version] }] });
        }
        else if (reference == "content")
        {
            quantity.Snapshot.Element.Add(new() { ElementId = "Quantity.borrowed", Path = "Quantity.borrowed", Min = 0, Max = "1",
                ContentReference = passive.Url + "|" + passive.Version + "#" + datatype + ".code" });
        }
        AddNative(definitions, quantity);
        if (datatype == "Patient")
        {
            definitions.AddResourceDefinition(native, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        }
        else if (datatype != "Quantity")
        {
            AddNative(definitions, native);
        }
        if (datatype != "Coding")
        {
            AddNative(definitions, Definition("Coding", "Coding"));
        }
        StructureDefinition eligible = ConstraintProfile("TerminologyQuantity");
        eligible.Snapshot.Element.Single(element => element.Path == "Quantity.code").Binding = TerminologyBinding("eligible");
        foreach (StructureDefinition profile in new[] { parent, passive, unrelated, eligible })
        {
            AddProfile(definitions, profile);
        }
        return definitions;
    }

    private static void AssertPrunedTerminologyDependencies(CatalogImport imported)
    {
        HashSet<int> systems = [.. DbCodeSystem.SelectList(imported.Connection).Select(row => row.Key)];
        List<DbCodeSystemConcept> concepts = DbCodeSystemConcept.SelectList(imported.Connection);
        List<DbCodeSystemFilter> filters = DbCodeSystemFilter.SelectList(imported.Connection);
        List<DbCodeSystemPropertyDefinition> properties = DbCodeSystemPropertyDefinition.SelectList(imported.Connection);
        List<DbCodeSystemConceptProperty> values = DbCodeSystemConceptProperty.SelectList(imported.Connection);
        concepts.Count.ShouldBe(systems.Count);
        filters.Count.ShouldBe(systems.Count);
        properties.Count.ShouldBe(systems.Count);
        values.Count.ShouldBe(systems.Count);
        concepts.ShouldAllBe(row => systems.Contains(row.CodeSystemKey));
        filters.ShouldAllBe(row => systems.Contains(row.CodeSystemKey));
        properties.ShouldAllBe(row => systems.Contains(row.CodeSystemKey));
        values.ShouldAllBe(row => concepts.Any(concept => concept.Key == row.CodeSystemConceptKey) &&
            properties.Any(property => property.Key == row.CodeSystemPropertyDefinitionKey));
    }

    private static string[] BuildVocabularySchedule(CatalogImport imported)
    {
        DbComparisonClasses.CreateTables(imported.Connection, forStructures: false);
        DbOutcomeClasses.CreateTables(imported.Connection, forStructures: false);
        DbFhirPackage package = DbFhirPackage.SelectList(imported.Connection).ShouldHaveSingleItem();
        string output = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "vocabulary-plan-" + Guid.NewGuid().ToString("N"));
        ConfigXVer config = new() { OutputDirectory = output };
        XVerExporter exporter = new(imported.Connection, config);
        IgExporter igExporter = new(imported.Connection, NullLoggerFactory.Instance, output, null, exporter, config);
        IgExporter.XVerExportTrackingRecord tracking = igExporter.PrepareXVerIgs(new([new(package, package)], [package]));
        VocabularyFhirExporter vocabulary = new(exporter, imported.Connection, NullLoggerFactory.Instance);
        IReadOnlyList<VocabularyFhirExporter.BuiltFile> files = vocabulary.Build(tracking);
        Directory.Exists(output).ShouldBeFalse();
        files.ShouldNotBeEmpty();
        List<string> schedule = [];
        foreach (VocabularyFhirExporter.BuiltFile file in files)
        {
            using JsonDocument document = JsonDocument.Parse(file.Json);
            JsonElement resource = document.RootElement;
            schedule.Add(resource.GetProperty("resourceType").GetString() + "|" + resource.GetProperty("url").GetString() +
                "|" + (resource.TryGetProperty("version", out JsonElement version) ? version.GetString() : ""));
        }
        return [.. schedule.Order()];
    }

    private const string ChildProfileUrl = "http://example.org/PassiveExtension";

    private static DefinitionCollection ChildProfileDefinitions(string package, string fixedUrl, bool targetProfile)
    {
        DefinitionCollection definitions = UseDefinitions();
        definitions.MainPackageId = "example." + package;
        string datatype = targetProfile ? "Endpoint" : "Extension";
        string field = targetProfile ? "address" : "url";
        StructureDefinition extension = Definition(datatype, datatype);
        extension.Snapshot.Element.Add(new() { ElementId = datatype + "." + field, Path = datatype + "." + field, Min = 1, Max = "1",
            Type = [new() { Code = "uri" }] });
        if (targetProfile)
        {
            extension.Kind = StructureDefinition.StructureDefinitionKind.Resource;
            definitions.AddStructureDefinition(extension, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
            AddNative(definitions, Definition("Reference", "Reference"));
            definitions.ComplexTypesByName["Quantity"].Snapshot.Element.Add(new()
            {
                ElementId = "Quantity.reference", Path = "Quantity.reference", Min = 0, Max = "1",
                Type = [new() { Code = "Reference", TargetProfile = [ChildProfileUrl] }],
            });
        }
        else
        {
            AddNative(definitions, extension);
        }
        StructureDefinition profile = (StructureDefinition)extension.DeepCopy();
        profile.Id = profile.Name = "PassiveExtension";
        profile.Url = ChildProfileUrl;
        profile.Version = "child-1";
        profile.BaseDefinition = extension.Url;
        profile.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
        profile.Snapshot.Element[1].Fixed = new Hl7.Fhir.Model.FhirUri(fixedUrl);
        AddProfile(definitions, profile);
        if (!targetProfile)
        {
            ElementDefinition.TypeRefComponent type = definitions.ComplexTypesByName["Quantity"].Snapshot.Element
                .Single(element => element.Path == "Quantity.extension").Type[0];
            type.Profile = [profile.Url];
        }
        return definitions;
    }

    private static StructureDefinition ConstraintProfile(string id, bool slicedRoot = false)
    {
        StructureDefinition profile = NativeQuantity();
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity.value", Path = "Quantity.value", Min = 0, Max = "1", Type = [new() { Code = "decimal" }] });
        profile.Snapshot.Element.Add(new() { ElementId = "Quantity.unit", Path = "Quantity.unit", Min = 0, Max = "1", Type = [new() { Code = "string" }] });
        profile.Id = profile.Name = id;
        profile.Url = "http://example.org/" + id;
        profile.BaseDefinition = QuantityUrl;
        profile.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
        if (slicedRoot)
        {
            foreach (ElementDefinition element in profile.Snapshot.Element)
            {
                element.ElementId = "Quantity:fixture" + element.ElementId["Quantity".Length..];
            }
        }
        return profile;
    }

    private static DefinitionCollection UseDefinitions(params StructureDefinition[] profiles)
    {
        DefinitionCollection definitions = Definitions();
        StructureDefinition native = NativeQuantity();
        native.Snapshot.Element.Add(new() { ElementId = "Quantity.value", Path = "Quantity.value", Min = 0, Max = "1", Type = [new() { Code = "decimal" }] });
        native.Snapshot.Element.Add(new() { ElementId = "Quantity.unit", Path = "Quantity.unit", Min = 0, Max = "1", Type = [new() { Code = "string" }] });
        AddNative(definitions, native);
        foreach (StructureDefinition profile in profiles)
        {
            AddProfile(definitions, profile);
        }
        StructureDefinition holder = Definition("UseHolder", "UseHolder");
        holder.Kind = StructureDefinition.StructureDefinitionKind.Resource;
        holder.Snapshot.Element.Add(new()
        {
            ElementId = "UseHolder.quantity", Path = "UseHolder.quantity", Min = 0, Max = "1",
            Type = [new() { Code = "Quantity", Profile = profiles.Length == 0 ? [] : [profiles[^1].Url] }],
        });
        holder.Snapshot.Element.Add(new()
        {
            ElementId = "UseHolder.native", Path = "UseHolder.native", Min = 0, Max = "1", Type = [new() { Code = "Quantity" }],
        });
        definitions.AddStructureDefinition(holder, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);
        return definitions;
    }

    private static CatalogImport ImportUses(params StructureDefinition[] profiles)
    {
        CatalogImport imported = new(UseDefinitions(profiles));
        imported.Database.TryLoadFromDefinitionCollections([], []).ShouldBeTrue();
        return imported;
    }

    private static EffectiveProfileUse QuantityUse(CatalogImport imported, ProfileUseFacts facts, StructureDefinition profile)
    {
        DbStructureDefinition holder = imported.Catalog.ResolveNativeType(imported.PackageKey, "UseHolder")!;
        EffectiveProfileUse use = facts.AtUse(holder, imported.Catalog.ResolveOwnedElement(holder, "UseHolder.quantity")!, 0);
        use.Branches.ShouldHaveSingleItem().Key.ProfileAlternative.ShouldBe(profile.Url);
        return use;
    }

    private static DefinitionCollection Definitions(FhirReleases.FhirSequenceCodes sequence = FhirReleases.FhirSequenceCodes.R4) => new()
    {
        Logger = NullLogger.Instance, Name = "Catalog fixture", FhirSequence = sequence,
        FhirVersion = Hl7.Fhir.Model.FHIRVersion.N4_0_1, FhirVersionLiteral = "4.0.1",
        MainPackageId = "hl7.fhir.r4.core", MainPackageVersion = "4.0.1", MainPackageCanonical = "http://hl7.org/fhir",
    };

    private static StructureDefinition Definition(string id, string? type, string? baseDefinition = null, string version = "definition-1") => new()
    {
        Id = id, Name = id, Url = "http://hl7.org/fhir/StructureDefinition/" + id, Version = version,
        Status = Hl7.Fhir.Model.PublicationStatus.Active, Kind = StructureDefinition.StructureDefinitionKind.ComplexType,
        Abstract = false, Type = type, BaseDefinition = baseDefinition, Derivation = StructureDefinition.TypeDerivationRule.Specialization,
        Snapshot = new() { Element = [new() { ElementId = type ?? id, Path = type ?? id, Min = 0, Max = "*" }] },
    };

    private static StructureDefinition Profile(string id, string? type, string? baseDefinition = QuantityUrl)
    {
        StructureDefinition definition = Definition(id, type, baseDefinition);
        definition.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
        return definition;
    }

    private static StructureDefinition NativeQuantity()
    {
        StructureDefinition definition = Definition("Quantity", "Quantity");
        foreach ((string name, string type) in new[] { ("comparator", "code"), ("system", "uri"), ("code", "code"), ("extension", "Extension") })
        {
            definition.Snapshot.Element.Add(new() { ElementId = "Quantity." + name, Path = "Quantity." + name, Min = 0, Max = "1", Type = [new() { Code = type }] });
        }
        return definition;
    }

    private static void AddNative(DefinitionCollection definitions, StructureDefinition definition) =>
        definitions.AddComplexType(definition, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);

    private static void AddProfile(DefinitionCollection definitions, StructureDefinition definition) =>
        definitions.AddProfile(definition, definitions.FhirSequence, definitions.MainPackageId, definitions.MainPackageVersion);

    private sealed class CatalogImport : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fhir-codegen-tests", "catalog-" + Guid.NewGuid().ToString("N"));

        internal CatalogImport(DefinitionCollection definitions, DefinitionCollection? targetDefinitions = null)
        {
            Database = new(targetDefinitions is null ? [definitions] : [definitions, targetDefinitions],
                _directory, "catalog.sqlite", NullLoggerFactory.Instance);
            PackageKey = DbFhirPackage.SelectList(Connection).First().Key;
        }

        internal ComparisonDatabase Database { get; }
        internal IDbConnection Connection => Database.DbConnection;
        internal int PackageKey { get; }
        internal XVerStructureCatalog Catalog => new(Connection);

        public void Dispose()
        {
            SqliteConnection connection = (SqliteConnection)Connection;
            ((IDisposable)Database).Dispose();
            SqliteConnection.ClearPool(connection);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private static SqliteConnection OpenDatabase(bool foreignKeys = false)
    {
        SqliteConnection connection = new($"Data Source=:memory:;Foreign Keys={foreignKeys}");
        connection.Open();
        return connection;
    }

    private static DbFhirPackage Package(int key) => new()
    {
        Key = key,
        Name = "R3",
        PackageId = "hl7.fhir.r3.core",
        PackageVersion = "3.0.2",
        FhirVersionShort = "3.0",
        CanonicalUrl = "http://hl7.org/fhir",
        ShortName = "R3",
        Dependencies = null,
        DefinitionFhirSequence = FhirReleases.FhirSequenceCodes.STU3,
    };

    private static void AddOwnerAssociations(SqliteConnection connection, DbFhirPackage package)
    {
        DbMappingSourceFile.Insert(connection, new DbMappingSourceFile
        {
            Key = 81,
            Filename = "quantity.json",
            SourceRelativeFilename = "input/elements/quantity.json",
            IsConceptMap = true,
            IsFml = false,
            Url = "http://example.org/ConceptMap/quantity",
        }, insertPrimaryKey: true);
        DbUnsafeCodeElementAnnotation.Insert(connection, new DbUnsafeCodeElementAnnotation
        {
            Key = 503,
            SourceFhirPackageKey = package.Key,
            TargetFhirPackageKey = package.Key,
            SourceElementId = "Quantity:simplequantity.code",
            TargetElementId = "Quantity.code",
            RequiresOmittedMandatoryCode = true,
            SourceRelativeFilename = "input/elements/quantity.json",
            ConceptMapId = "quantity-map",
            ConceptMapUrl = "http://example.org/ConceptMap/quantity",
            GroupIndex = 2,
            SourceIndex = 3,
            TargetIndex = 4,
        }, insertPrimaryKey: true);
        foreach (int key in new[] { 9, 401 })
        {
            DbElementMapping.Insert(connection, new DbElementMapping
            {
                Key = key,
                SourceFhirPackageKey = package.Key,
                SourceFhirSequence = package.DefinitionFhirSequence,
                TargetFhirPackageKey = package.Key,
                TargetFhirSequence = package.DefinitionFhirSequence,
                ConceptMapSourceKey = 81,
                ConceptMapFilename = "quantity.json",
                FmlSourceKey = null,
                FmlFilename = null,
                Relationship = Hl7.Fhir.Model.ConceptMap.ConceptMapRelationship.Equivalent,
                ExplicitNoMap = false,
                StructureMappingKey = null,
                SourceStructureKey = 901,
                SourceElementKey = null,
                SourceElementId = "Quantity:simplequantity.code",
                TargetStructureKey = 7,
                TargetElementKey = null,
                TargetElementId = "Quantity.code",
                FmlIsSimpleCopy = null,
                ConceptDomainRelationship = null,
                ValueDomainRelationship = null,
            }, insertPrimaryKey: true);
        }

        List<DbProfileMapOwnerFacts> owners = [OwnerFacts(700, 9), OwnerFacts(11, 401)];
        DbProfileMapOwnerFacts.Insert(connection, owners, insertPrimaryKey: true);
        DbProfileMapOwnerFacts unresolved = OwnerFacts(3, 9);
        unresolved.AnnotationKey = 503;
        unresolved.ElementMappingKey = null;
        unresolved.SourceStructureKey = null;
        unresolved.TargetStructureKey = null;
        unresolved.ResolutionState = ProfileMapOwnerResolution.Unnormalized;
        DbProfileMapOwnerFacts.Insert(connection, unresolved, insertPrimaryKey: true);
        DbProfileMapOwnerFacts.SelectSingle(connection, Key: 3)!.ResolutionState.ShouldBe(ProfileMapOwnerResolution.Unnormalized);
        DbProfileMapOwnerFacts.SelectSingle(connection, Key: 3)!.ElementMappingKey.ShouldBeNull();
    }

    private static DbProfileMapOwnerFacts OwnerFacts(int key, int mappingKey) => new()
    {
        Key = key,
        AnnotationKey = 503,
        InputSnapshotId = "input-1",
        SourceFhirPackageKey = 37,
        TargetFhirPackageKey = 37,
        GroupSourceCanonical = "http://hl7.org/fhir/StructureDefinition/SimpleQuantity|3.0.2",
        GroupSourceVersion = "3.0.2",
        GroupTargetCanonical = "http://hl7.org/fhir/StructureDefinition/Quantity|3.0.2",
        GroupTargetVersion = "3.0.2",
        SourceOwnerContext = "SimpleQuantity",
        TargetOwnerContext = "Quantity",
        SourceStructureKey = 901,
        TargetStructureKey = 7,
        ElementMappingKey = mappingKey,
        ResolutionState = ProfileMapOwnerResolution.Resolved,
        SourceElementId = "Quantity:simplequantity.code",
        TargetElementId = "Quantity.code",
        MappingSourceFileKey = 81,
        SourceRelativeFilename = "input/elements/quantity.json",
        ConceptMapId = "quantity-map",
        ConceptMapUrl = "http://example.org/ConceptMap/quantity",
        GroupIndex = 2,
        SourceIndex = 3,
        TargetIndex = 4,
    };

    private static void SeedDownstreamCompletion(SqliteConnection connection)
    {
        DbUnsafeCodeInputState.Delete(connection);
        DbUnsafeCodePairState.Delete(connection);
        DbUnsafeCodeInputState.Insert(connection, new DbUnsafeCodeInputState
        {
            Key = 1,
            SnapshotId = "input-1",
            FormatVersion = 1,
            SourceMode = "fixture",
            CompletedInputRevision = "input-complete",
        }, insertPrimaryKey: true);
        DbUnsafeCodePairState.Insert(connection, new DbUnsafeCodePairState
        {
            Key = 1,
            SourceFhirPackageKey = 37,
            TargetFhirPackageKey = 38,
            InputRevision = "input-complete",
            ComparisonRevision = "comparison-complete",
            OutcomeInputRevision = "input-complete",
            OutcomeComparisonRevision = "comparison-complete",
            OutcomeFormatVersion = 1,
        }, insertPrimaryKey: true);
    }

    private sealed class TestStructure : DbStructureDefinition
    {
        [SetsRequiredMembers]
        internal TestStructure(DbFhirPackage package, int key, StructureDefinition definition)
        {
            Key = key;
            FhirPackageKey = package.Key;
            Id = definition.Id;
            Name = definition.Name;
            Version = definition.Version;
            UnversionedUrl = definition.Url;
            VersionedUrl = definition.Url + "|" + definition.Version;
            Status = definition.Status;
            SnapshotCount = definition.Snapshot?.Element.Count ?? 0;
            DifferentialCount = definition.Differential?.Element.Count ?? 0;
            ArtifactClass = definition.Derivation == StructureDefinition.TypeDerivationRule.Constraint
                ? FhirArtifactClassEnum.Profile : FhirArtifactClassEnum.ComplexType;
        }
    }
}
