// <copyright file="XVerProfileCatalogTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Models;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Shouldly;
using StructureDefinition = Hl7.Fhir.Model.StructureDefinition;

namespace Fhir.CodeGen.Lib.Tests;

[Collection(UnsafeCodeProfileCollection.Name)]
public class XVerProfileCatalogTests
{
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
        DbStructureDefinitionFacts facts = StructureDefinitionFactsStore.CreateFacts(100, 37, 901, profile);
        DbStructureDefinitionFacts.Insert(connection, facts, insertPrimaryKey: true);
        DbProfileMapOwnerFacts.Insert(connection, OwnerFacts(700, 9), insertPrimaryKey: true);
        DefinitionInventoryPackageCounts[] expected = [new(37, 0, 1, 0, 1, 7, 5, 0)];
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
