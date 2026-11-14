// <copyright file="XVerProfileCatalogTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.Extensions;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.XVer;
using Fhir.CodeGen.Lib.Loader;
using Fhir.CodeGen.Lib.Models;
using Hl7.Fhir.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;
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
        full.Database.TryLoadFullCatalogCandidate(exclusions, []).ShouldBeTrue();
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
        catalog.ResolveImportLinks(full.Connection, validateProcessedRoots: false);
        catalog.Diagnostics.ShouldContain(message => message.Contains("missing", StringComparison.Ordinal));

        using CatalogImport ordinary = new(definitions);
        ordinary.Database.TryLoadFromDefinitionCollections(exclusions, []).ShouldBeTrue();
        DbStructureDefinition.SelectList(ordinary.Connection).Count.ShouldBe(3);
        DbStructureDefinition.SelectList(ordinary.Connection, ArtifactClass: FhirArtifactClassEnum.Profile).ShouldBeEmpty();
        DefinitionInventoryStore.ReadState(ordinary.Connection).ShouldBeNull();

        DefinitionCollection r2 = Definitions(FhirReleases.FhirSequenceCodes.DSTU2);
        AddNative(r2, NativeQuantity());
        StructureDefinition derivative = Profile("Age", "Quantity");
        r2.AddComplexType(derivative, r2.FhirSequence, r2.MainPackageId, r2.MainPackageVersion);
        r2.ComplexTypesByName.ContainsKey("Age").ShouldBeFalse();
        r2.ProfilesByUrl.ContainsKey(derivative.Url).ShouldBeTrue();
        using CatalogImport dstu2 = new(r2);
        dstu2.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
        dstu2.Catalog.GetProcessedStructures(dstu2.PackageKey).Select(row => row.Id).Order().ShouldBe(["Age", "Quantity"]);
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
            InvalidOperationException error = Should.Throw<InvalidOperationException>(() => failed.Database.TryLoadFullCatalogCandidate([], []));
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
        Should.Throw<InvalidOperationException>(() => unresolved.Database.TryLoadFullCatalogCandidate([], [])).Message.ShouldContain(missingOwner.Url);
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
    public void PublicNativeImportRejectsUnresolvedContentReferenceButCandidateRetainsPassiveDiagnostics()
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
        }

        DefinitionCollection passiveDefinitions = Definitions();
        AddNative(passiveDefinitions, NativeQuantity());
        StructureDefinition passive = Profile("UnrelatedIncomplete", "Patient", baseDefinition: "http://example.org/missing-base");
        passive.Snapshot.Element.Add(new() { ElementId = "Patient.contact", Path = "Patient.contact", ContentReference = "#Patient.missing" });
        AddProfile(passiveDefinitions, passive);
        using CatalogImport retained = new(passiveDefinitions);
        retained.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
        InvalidOperationException late = Should.Throw<InvalidOperationException>(() =>
            imported.Database.TryLoadFullCatalogCandidate([], [], connection =>
            {
                DbStructureDefinitionFacts.SelectList(connection).ShouldHaveSingleItem();
                DefinitionInventoryStore.ReadState(connection)!.CompletedRevision.ShouldBeNull();
                throw new InvalidOperationException("injected late post-processing failure");
            }));
        late.Message.ShouldContain("late post-processing");
        DbDefinitionInventoryState state = DefinitionInventoryStore.ReadState(imported.Connection)!;
        state.CompletedRevision.ShouldBeNull();
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
        Should.Throw<InvalidOperationException>(() => cyclic.Database.TryLoadFullCatalogCandidate([], [])).Message.ShouldContain("cyclic contentReference");
        DefinitionInventoryStore.ReadState(cyclic.Connection)!.CompletedRevision.ShouldBeNull();

        DefinitionCollection duplicate = Definitions();
        AddNative(duplicate, NativeQuantity());
        StructureDefinition conflict = Profile("ConflictingId", "Quantity");
        conflict.Url = QuantityUrl;
        AddProfile(duplicate, conflict);
        using CatalogImport conflicting = new(duplicate);
        Should.Throw<InvalidOperationException>(() => conflicting.Database.TryLoadFullCatalogCandidate([], []))
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
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => imported.Database.TryLoadFullCatalogCandidate([], []));
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
        imported.Database.TryLoadFullCatalogCandidate([], []).ShouldBeTrue();
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
    public async Task NativeOnlyPinnedCoreBaseline()
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fhir", "packages");
        string[][] entries = [TestCommon.EntriesR2, TestCommon.EntriesR3, TestCommon.EntriesR4, TestCommon.EntriesR4B, TestCommon.EntriesR5];
        foreach (string[] directives in entries)
        {
            string moniker = directives[0];
            string[] parts = moniker.Split('#');
            if (!Directory.Exists(Path.Combine(cache, moniker)) ||
                !Directory.Exists(Path.Combine(cache, parts[0].Replace(".core", ".expansions") + "#" + parts[1])))
            {
                _output.WriteLine($"Baseline prerequisite missing: {moniker} and/or its pinned expansions; no package acquisition attempted.");
                continue;
            }

            PackageLoader loader = new(new()
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
            List<DbStructureDefinition> nativeRows = DbStructureDefinition.SelectList(imported.Connection);
            nativeRows.Count(row => row.ArtifactClass == FhirArtifactClassEnum.Profile).ShouldBe(0);
            _output.WriteLine($"{moniker}: primitives={definitions.PrimitiveTypesByName.Count}; complex={definitions.ComplexTypesByName.Count}; resources={definitions.ResourcesByName.Count}");
            _output.WriteLine($"  native definitions={nativeRows.Count}; retained source profiles={definitions.ProfilesByUrl.Count}; imported profiles=0; elements={DbElement.SelectList(imported.Connection).Count}; import ms={elapsed.ElapsedMilliseconds}");
            _output.WriteLine($"  code systems source/stored={definitions.CodeSystemsByUrl.Count}/{DbCodeSystem.SelectList(imported.Connection).Count}; value sets source/stored={definitions.ValueSetsByVersionedUrl.Count}/{DbValueSet.SelectList(imported.Connection).Count}");
        }
    }

    [Theory]
    [InlineData("hl7.fhir.r2.core#1.0.2")]
    [InlineData("hl7.fhir.r3.core#3.0.2")]
    [InlineData("hl7.fhir.r4.core#4.0.1")]
    [InlineData("hl7.fhir.r4b.core#4.3.0")]
    [InlineData("hl7.fhir.r5.core#5.0.0")]
    public async Task FullCandidateRequiresPinnedCachedCore(string moniker)
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
        imported.Database.TryLoadFullCatalogCandidate(XVerProcessor._exclusionSet, XVerProcessor._escapeValveCodes).ShouldBeTrue();
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
        _output.WriteLine($"{moniker}: full candidate completed; definitions={catalog.GetDefinitions(imported.PackageKey).Count}; profiles={definitions.ProfilesByUrl.Count}; elements={DbElement.SelectList(imported.Connection).Count}; import ms={elapsed.ElapsedMilliseconds}");
    }

    private const string QuantityUrl = "http://hl7.org/fhir/StructureDefinition/Quantity";

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

        internal CatalogImport(DefinitionCollection definitions)
        {
            Database = new([definitions], _directory, "catalog.sqlite", NullLoggerFactory.Instance);
            PackageKey = DbFhirPackage.SelectList(Connection).Single().Key;
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
            Directory.Delete(_directory, recursive: true);
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
