// <copyright file="UnsafeCodeTestFixture.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

extern alias r4;
extern alias r4b;

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fhir.CodeGen.Common.Models;
using Fhir.CodeGen.Common.Packaging;
using Fhir.CodeGen.Comparison.CompareTool;
using Fhir.CodeGen.Comparison.CrossVersionSource;
using Fhir.CodeGen.Comparison.Exporter;
using Fhir.CodeGen.Comparison.Models;
using Fhir.CodeGen.Comparison.Outcomes;
using Fhir.CodeGen.Lib.Configuration;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Hl7.FhirPath.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using BindingStrength = Hl7.Fhir.Model.BindingStrength;
using ConceptMap = Hl7.Fhir.Model.ConceptMap;
using FhirBoolean = Hl7.Fhir.Model.FhirBoolean;
using PublicationStatus = Hl7.Fhir.Model.PublicationStatus;
using ElementDefinition = Hl7.Fhir.Model.ElementDefinition;
using StructureDefinition = Hl7.Fhir.Model.StructureDefinition;

namespace Fhir.CodeGen.Lib.Tests;

internal sealed class UnsafeCodeTestFixture : IDisposable
{
    internal const string FilterSystem = "http://hl7.org/fhir/filter-operator";
    internal const string FilterValueSet = "http://hl7.org/fhir/ValueSet/filter-operator";
    internal const string FilterElement = "ValueSet.compose.include.filter.op";
    internal const string ImportedNativeElement = "ValueSet.jurisdiction";
    internal const string ImportedSourceSystem = "urn:example:source";
    internal const string ImportedSystemVersion = "system-version-7";

    internal enum ImportedNativeScenario
    {
        IdentityChanging,
        CodeOnly,
        SystemOnly,
        SystemVersionOnly,
        IdentityPreserving,
        SourceNonmember,
        TargetNonmember,
        SourceValueSetVersionMismatch,
        TargetValueSetVersionMismatch,
        SourceSystemVersionMismatch,
        TargetSystemVersionMismatch,
    }

    private readonly bool _persistDatabase;
    private string _persistedStage = "creation";
    private readonly Dictionary<int, (string Type, StructureDefinition.StructureDefinitionKind Kind, string? Base)> _authoredDefinitions = [];
    private readonly Dictionary<int, Action<StructureDefinition>> _authoredRestrictions = [];

    internal SqliteConnection Connection { get; private set; }
    internal int ConnectionReopens { get; private set; }
    internal string SourceRoot { get; }
    internal string ConfigurationFilename => Path.Combine(SourceRoot, "input", "ig-support", "unsafe-code-mappings.json");
    internal List<DbFhirPackage> Packages { get; } = [];
    internal DbUnsafeCodeInputState InputState => DbUnsafeCodeInputState.SelectSingle(Connection, Key: 1)!;

    internal UnsafeCodeTestFixture(bool initializeIndices = false, bool persistDatabase = false, bool publishDefinitions = true,
        bool includeLegacyPackages = false)
    {
        _persistDatabase = persistDatabase;
        SourceRoot = Path.Combine(Path.GetTempPath(), "fhir-codegen-unsafe-" + Guid.NewGuid().ToString("N"));
        Connection = new(persistDatabase
            ? new SqliteConnectionStringBuilder { DataSource = Path.Combine(SourceRoot, "snapshot.sqlite"), Pooling = false }.ToString()
            : "Data Source=:memory:");
        try
        {
            Directory.CreateDirectory(SourceRoot);
            Connection.Open();
            string input = Path.Combine(AppContext.BaseDirectory, "TestData", "XverUnsafeCodes");
            foreach (string filename in Directory.GetFiles(Path.Combine(input, "input"), "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(SourceRoot, Path.GetRelativePath(input, filename));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(filename, destination);
            }

            if (initializeIndices)
            {
                DbContentClasses.LoadIndices(Connection);
                DbMappingClasses.LoadIndices(Connection);
                DbComparisonClasses.LoadIndices(Connection);
                DbOutcomeClasses.LoadIndices(Connection);
            }

            DbContentClasses.CreateTables(Connection);
            DbMappingClasses.CreateTables(Connection);
            FhirReleases.FhirSequenceCodes[] sequences = [FhirReleases.FhirSequenceCodes.R4, FhirReleases.FhirSequenceCodes.R4B, FhirReleases.FhirSequenceCodes.R5];
            foreach (FhirReleases.FhirSequenceCodes sequence in sequences)
            {
                DbFhirPackage package = new()
                {
                    Name = sequence.ToCorePackageId(),
                    PackageId = sequence.ToCorePackageId(),
                    PackageVersion = sequence.ToLongVersion(),
                    FhirVersionShort = sequence.ToShortVersion(),
                    CanonicalUrl = "http://hl7.org/fhir",
                    ShortName = sequence.ToRLiteral(),
                    Dependencies = null,
                    DefinitionFhirSequence = sequence,
                };
                package.Key = DbFhirPackage.Insert(Connection, package);
                Packages.Add(package);
                InsertStructure(package);
                InsertValueSet(package);
            }

            // The transitive comparer indexes all five releases, even for these three-release fixtures.
            foreach (FhirReleases.FhirSequenceCodes sequence in includeLegacyPackages
                ? new[] { FhirReleases.FhirSequenceCodes.DSTU2, FhirReleases.FhirSequenceCodes.STU3 } : [])
            {
                DbFhirPackage.Insert(Connection, new DbFhirPackage
                {
                    Name = sequence.ToCorePackageId(),
                    PackageId = sequence.ToCorePackageId(),
                    PackageVersion = sequence.ToLongVersion(),
                    FhirVersionShort = sequence.ToShortVersion(),
                    CanonicalUrl = "http://hl7.org/fhir",
                    ShortName = sequence.ToRLiteral(),
                    Dependencies = null,
                    DefinitionFhirSequence = sequence,
                });
            }
            if (publishDefinitions)
            {
                PublishAuthoredDefinitions();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool Load() => new MappingLoader(Connection, NullLoggerFactory.Instance).TryLoadCrossVersionSourceMaps(SourceRoot, false);

    internal void DeclareProfile(DbStructureDefinition profile, DbStructureDefinition basis, string? declaredType = null)
    {
        (string type, StructureDefinition.StructureDefinitionKind kind, _) = _authoredDefinitions[basis.Key];
        _authoredDefinitions[profile.Key] = (declaredType ?? type, kind, basis.VersionedUrl);
    }

    internal void DeclareBaseDefinition(DbStructureDefinition owner, DbStructureDefinition basis)
    {
        (string type, StructureDefinition.StructureDefinitionKind kind, _) = _authoredDefinitions[owner.Key];
        _authoredDefinitions[owner.Key] = (type, kind, basis.VersionedUrl);
    }

    internal void ConfigureDefinition(DbStructureDefinition owner, Action<StructureDefinition> configure) =>
        _authoredRestrictions.Add(owner.Key, configure);

    // Explicit arrangement only: never call from Load, comparison, outcomes, or export.
    // Counts come from the fixture-owned normalized snapshots, not a census offered as import proof.
    internal void PublishAuthoredDefinitions()
    {
        List<DbStructureDefinition> owners = DbStructureDefinition.SelectList(Connection);
        List<(DbStructureDefinition Owner, StructureDefinition Definition)> definitions = [];
        foreach (DbStructureDefinition owner in owners)
        {
            (string type, StructureDefinition.StructureDefinitionKind kind, string? baseDefinition) = _authoredDefinitions[owner.Key];
            List<DbElement> elements = DbElement.SelectList(Connection, StructureKey: owner.Key, orderByProperties: [nameof(DbElement.ResourceFieldOrder)]);
            if (elements.Count == 0)
            {
                AddElement(owner, type);
                elements = DbElement.SelectList(Connection, StructureKey: owner.Key);
            }

            StructureDefinition definition = new()
            {
                Id = owner.Id,
                Url = owner.UnversionedUrl,
                Version = owner.Version,
                Name = owner.Name,
                Status = owner.Status,
                Type = type,
                Kind = kind,
                Abstract = false,
                Derivation = owner.ArtifactClass == FhirArtifactClassEnum.Profile
                    ? StructureDefinition.TypeDerivationRule.Constraint : StructureDefinition.TypeDerivationRule.Specialization,
                BaseDefinition = baseDefinition,
                Snapshot = new() { Element = [] },
            };
            foreach (DbElement element in elements)
            {
                List<DbElementType> types = DbElementType.SelectList(Connection, ElementKey: element.Key);
                if (types.Count == 0 && !element.DefinedAsContentReference)
                {
                    string[] names = element.ResourceFieldOrder == 0 ? [type]
                        : element.DistinctTypeLiterals.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string name in names)
                    {
                        DbStructureDefinition? native = owners.SingleOrDefault(candidate => candidate.FhirPackageKey == owner.FhirPackageKey &&
                            candidate.ArtifactClass != FhirArtifactClassEnum.Profile && candidate.Id == name);
                        if (native is not null)
                        {
                            AddType(element, name, native);
                        }
                        else
                        {
                            DbElementType.Insert(Connection, new DbElementType
                            {
                                FhirPackageKey = element.FhirPackageKey,
                                StructureKey = element.StructureKey,
                                ElementKey = element.Key,
                                TypeName = name,
                                TypeStructureKey = null,
                                TypeProfile = null,
                                TypeProfileStructureKey = null,
                                TargetProfile = null,
                                TargetProfileStructureKey = null,
                            });
                        }
                    }
                    types = DbElementType.SelectList(Connection, ElementKey: element.Key);
                }

                ElementDefinition normalized = new()
                {
                    ElementId = element.Id,
                    Path = element.Path,
                    SliceName = element.SliceName,
                    Min = element.MinCardinality,
                    Max = element.MaxCardinalityString,
                    IsModifier = element.IsModifier,
                    Short = element.Short,
                    Definition = element.Definition,
                    Base = element.BasePath is null ? null : new()
                    {
                        Path = element.BasePath,
                        Min = element.MinCardinality,
                        Max = element.MaxCardinalityString,
                    },
                    Type = [.. types.Select(row => new ElementDefinition.TypeRefComponent
                    {
                        Code = row.TypeName,
                        Profile = row.TypeProfile is null ? [] : [row.TypeProfile],
                        TargetProfile = row.TargetProfile is null ? [] : [row.TargetProfile],
                    })],
                    Binding = element.BindingValueSet is null ? null : new()
                    {
                        Strength = element.ValueSetBindingStrength,
                        ValueSet = element.BindingValueSet,
                        Description = element.BindingDescription,
                    },
                };
                if (element.DefinedAsContentReference && element.ContentReferenceSourceKey is int referenceKey)
                {
                    DbElement referenced = DbElement.SelectSingle(Connection, Key: referenceKey)!;
                    DbStructureDefinition referencedOwner = owners.Single(candidate => candidate.Key == referenced.StructureKey);
                    normalized.ContentReference = (referencedOwner.Key == owner.Key ? "" : referencedOwner.VersionedUrl) + "#" + referenced.Id;
                    element.ContentReferenceSourceId = referenced.Id;
                    DbElement.Update(Connection, [element]);
                }
                definition.Snapshot.Element.Add(normalized);
            }
            if (_authoredRestrictions.TryGetValue(owner.Key, out Action<StructureDefinition>? configure))
            {
                configure(definition);
            }

            owner.SnapshotCount = definition.Snapshot.Element.Count;
            owner.DifferentialCount = 0;
            DbStructureDefinition.Update(Connection, [owner]);
            definitions.Add((owner, definition));
        }

        PublishAuthoredDefinitions(Connection, definitions);
    }

    internal static void PublishAuthoredDefinitions(
        IDbConnection connection, IReadOnlyList<(DbStructureDefinition Owner, StructureDefinition Definition)> definitions)
    {
        DefinitionInventoryPackageCounts[] counts = [.. DbFhirPackage.SelectList(connection).Select(package =>
        {
            (DbStructureDefinition Owner, StructureDefinition Definition)[] owned = [.. definitions.Where(entry => entry.Owner.FhirPackageKey == package.Key)];
            return new DefinitionInventoryPackageCounts(package.Key,
                owned.Count(entry => entry.Owner.ArtifactClass == FhirArtifactClassEnum.PrimitiveType),
                owned.Count(entry => entry.Owner.ArtifactClass == FhirArtifactClassEnum.ComplexType),
                owned.Count(entry => entry.Owner.ArtifactClass == FhirArtifactClassEnum.Resource),
                owned.Count(entry => entry.Owner.ArtifactClass == FhirArtifactClassEnum.Profile),
                owned.Sum(entry => entry.Definition.Snapshot.Element.Count),
                owned.Sum(entry => entry.Definition.Snapshot.Element.Sum(element => element.Type.Sum(type =>
                    Math.Max(1, type.ProfileElement.Count) * Math.Max(1, type.TargetProfileElement.Count)))),
                owned.Sum(entry => entry.Definition.Snapshot.Element.Sum(element => element.Binding?.Additional.Count ?? 0)));
        })];
        string revision = DefinitionInventoryStore.BeginInventory(connection, counts);
        DbStructureDefinitionFacts.Delete(connection);
        foreach ((DbStructureDefinition owner, StructureDefinition definition) in definitions)
        {
            DbStructureDefinitionFacts.Insert(connection,
                StructureDefinitionFactsStore.CreateFacts(owner.Key, owner.FhirPackageKey, owner.Key, definition), insertPrimaryKey: true);
        }
        DefinitionInventoryStore.CompleteInventory(connection, revision);
    }

    internal void CompleteAuthoredArrangement(FhirPackageComparisonPair pair)
    {
        PublishAuthoredDefinitions();
        UnsafeCodeInputStore.BeginInputs(Connection, "fixture", null);
        UnsafeCodeInputStore.CompleteInputs(Connection, DbFhirPackage.SelectList(Connection));
        foreach (DbProfileMapOwnerFacts owner in DbProfileMapOwnerFacts.SelectList(Connection))
        {
            owner.InputSnapshotId = InputState.SnapshotId;
            DbProfileMapOwnerFacts.Update(Connection, owner);
        }
        StampComparison(pair);
    }

    internal const string MixedChoiceElement = "Parameters.parameter.value[x]";
    internal const string MixedCodingSystem = "urn:example:codes";
    internal const string MixedPrimitiveSystem = "urn:example:primitive";
    internal const string MixedPrimitiveVersion = "primitive-v1";

    internal static UnsafeCodeTestFixture CreateMixedChoiceInputs(
        int targetIndex, string primitiveAlternative, string primitiveQualification, bool enableRules,
        int[]? codingProfilePackageIndices = null, bool profilesFirst = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetIndex, 1);
        if (primitiveAlternative is not ("boolean" or "code") ||
            primitiveQualification is not ("qualified" or "missing-binding" or "multi-system" or "unknown-version" or "nonmember" or "version-nonmatch"))
        {
            throw new ArgumentException("Unknown mixed-choice input variant.");
        }

        UnsafeCodeTestFixture fixture = new(initializeIndices: true, persistDatabase: true, publishDefinitions: false, includeLegacyPackages: true);
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input"), recursive: true);
        DbStructureDefinition.Delete(fixture.Connection);
        fixture._authoredDefinitions.Clear();
        DbValueSet.Delete(fixture.Connection);
        DbOutcomeClasses.CreateTables(fixture.Connection, forValueSets: true, forStructures: false);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "boolean", "code", "string", "uri" })
            {
                DbStructureDefinition structure = fixture.AddStructure(package, primitive, FhirArtifactClassEnum.PrimitiveType);
                fixture.AddMixedChoiceElement(structure, primitive, [], min: 0);
            }

            bool includeProfile = codingProfilePackageIndices?.Contains(fixture.Packages.IndexOf(package)) == true;
            DbStructureDefinition? profile = includeProfile && profilesFirst ? fixture.AddMixedChoiceOwnerProfile(package) : null;
            DbStructureDefinition coding = fixture.AddStructure(package, "Coding");
            DbElement codingRoot = fixture.AddMixedChoiceElement(coding, "Coding", []);
            fixture.AddMixedChoiceElement(coding, "Coding.system", ["uri"]);
            fixture.AddMixedChoiceElement(coding, "Coding.version", ["string"]);
            fixture.AddMixedChoiceElement(coding, "Coding.code", ["code"]);
            if (includeProfile)
            {
                profile ??= fixture.AddMixedChoiceOwnerProfile(package);
                DbElement profileRoot = DbElement.SelectSingle(fixture.Connection, StructureKey: profile.Key, ResourceFieldOrder: 0)!;
                profileRoot.BaseStructureKey = coding.Key;
                profileRoot.BaseElementKey = codingRoot.Key;
                profileRoot.BasePath = "Coding";
                DbElement.Update(fixture.Connection, [profileRoot]);
            }
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddMixedChoiceElement(extension, "Extension", []);
            fixture.AddMixedChoiceElement(extension, "Extension.value[x]", ["Coding", "boolean", "code", "string", "uri"]);
            DbStructureDefinition parameters = fixture.AddStructure(package, "Parameters", FhirArtifactClassEnum.Resource);
            DbStructureDefinition backbone = fixture.AddStructure(package, "BackboneElement");
            fixture.AddMixedChoiceElement(backbone, "BackboneElement", []);
            fixture.AddMixedChoiceElement(parameters, "Parameters", []);
            fixture.AddMixedChoiceElement(parameters, "Parameters.parameter", ["BackboneElement"], max: -1);
            fixture.AddMixedChoiceElement(parameters, "Parameters.parameter.name", ["string"], min: 1);

            DbValueSet? binding = null;
            bool source = package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5;
            if (primitiveAlternative == "code" && !(source && primitiveQualification == "missing-binding"))
            {
                TestValueSet row = new(package)
                {
                    Id = "mixed-choice-primitive",
                    Name = "MixedChoicePrimitive",
                    UnversionedUrl = "http://example.org/ValueSet/mixed-choice-primitive",
                    Version = "primitive-valueset-v3",
                    VersionedUrl = "http://example.org/ValueSet/mixed-choice-primitive|primitive-valueset-v3",
                    CanExpand = false,
                    Compose = new()
                    {
                        Include = [new()
                        {
                            System = MixedPrimitiveSystem,
                            Version = source && primitiveQualification == "unknown-version" ? null
                                : source && primitiveQualification == "version-nonmatch" ? "primitive-v2" : MixedPrimitiveVersion,
                            Concept = source && primitiveQualification == "nonmember"
                                ? [new() { Code = "safe" }] : [new() { Code = "safe" }, new() { Code = "unsafe" }],
                        }],
                    },
                };
                if (source && primitiveQualification == "multi-system")
                {
                    row.Compose.Include.Add(new()
                    {
                        System = "urn:example:ambiguous",
                        Version = "other-v1",
                        Concept = [new() { Code = "safe" }, new() { Code = "unsafe" }],
                    });
                }

                row.Key = DbValueSet.Insert(fixture.Connection, row);
                binding = row;
            }

            fixture.AddMixedChoiceElement(parameters, MixedChoiceElement, ["Coding", primitiveAlternative], binding: binding);
            foreach (DbStructureDefinition structure in DbStructureDefinition.SelectList(fixture.Connection, FhirPackageKey: package.Key))
            {
                structure.SnapshotCount = DbElement.SelectList(fixture.Connection, StructureKey: structure.Key).Count;
                DbStructureDefinition.Update(fixture.Connection, [structure]);
            }
        }

        foreach ((int source, int target) in new[] { (2, 1), (1, 0), (2, 0) })
        {
            string direction = (source == 1 ? "4B" : "5") + "to" + (target == 1 ? "4B" : "4");
            fixture.WriteMixedChoiceMap("resources", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/resource-types",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/resource-types", ["Parameters"]);
            fixture.WriteMixedChoiceMap("types", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/data-types",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/data-types", ["Coding", "Extension"]);
            fixture.WriteMixedChoiceMap("elements", direction,
                "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/element-names",
                "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/element-names",
                [.. DbElement.SelectList(fixture.Connection, FhirPackageKey: fixture.Packages[source].Key).Select(element => element.Id)]);
        }
        fixture.WriteMixedChoiceMap("types", "fallback", "http://hl7.org/fhir/fhir-types", "http://hl7.org/fhir/fhir-types",
            ["boolean", "code", "string", "uri"]);
        fixture.WriteConfiguration(enableRules
            ? primitiveAlternative == "boolean"
                ? """[{"system":"urn:example:codes","code":"unsafe"}]"""
                : """[{"system":"urn:example:codes","code":"unsafe"},{"system":"urn:example:primitive","version":"primitive-v1","code":"unsafe"}]"""
            : "[]");
        fixture.PublishAuthoredDefinitions();
        return fixture;
    }

    private DbStructureDefinition AddMixedChoiceOwnerProfile(DbFhirPackage package, string id = "SelectedCoding")
    {
        DbStructureDefinition profile = AddStructure(package, id, FhirArtifactClassEnum.Profile);
        _authoredDefinitions[profile.Key] = ("Coding", StructureDefinition.StructureDefinitionKind.ComplexType,
            "http://example.org/StructureDefinition/Coding|" + package.PackageVersion);
        profile.Version = "coding-profile-v7";
        profile.VersionedUrl = profile.UnversionedUrl + "|" + profile.Version;
        DbStructureDefinition.Update(Connection, [profile]);
        AddMixedChoiceElement(profile, "Coding", []);
        return profile;
    }

    internal void AddMixedChoiceRootCollisionMaps(int targetIndex, bool sourceProfile, bool targetProfile)
    {
        if (sourceProfile)
        {
            DbStructureDefinition unmapped = AddMixedChoiceOwnerProfile(Packages[2], "UnmappedCoding");
            DbElement root = DbElement.SelectSingle(Connection, StructureKey: unmapped.Key, ResourceFieldOrder: 0)!;
            root.BaseStructureKey = Structure(Packages[2], "Coding").Key;
            root.BaseElementKey = DbElement.SelectSingle(Connection, StructureKey: root.BaseStructureKey, ResourceFieldOrder: 0)!.Key;
            root.BasePath = "Coding";
            DbElement.Update(Connection, [root]);
        }

        foreach ((int source, int target) in new[] { (2, 1), (1, 0), (2, 0) })
        {
            bool hasSourceProfile = DbStructureDefinition.SelectSingle(Connection,
                FhirPackageKey: Packages[source].Key, Id: "SelectedCoding") is not null;
            bool hasTargetProfile = DbStructureDefinition.SelectSingle(Connection,
                FhirPackageKey: Packages[target].Key, Id: "SelectedCoding") is not null;
            string suffix = (source == 1 ? "4B" : "5") + "to" + (target == 1 ? "4B" : "4");
            string filename = Path.Combine(SourceRoot, "input", "types", "ConceptMap-types-" + suffix + ".json");
            ConceptMap map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
            if (hasSourceProfile)
            {
                map.Group[0].Element.Add(new()
                {
                    Code = "SelectedCoding",
                    Target = [new() { Code = hasTargetProfile ? "SelectedCoding" : "Coding", Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                });
            }
            else if (hasTargetProfile)
            {
                map.Group[0].Element.Single(element => element.Code == "Coding").Target.Add(new()
                {
                    Code = "SelectedCoding", Relationship = ConceptMap.ConceptMapRelationship.Equivalent,
                });
            }
            if (source == 2 && sourceProfile)
            {
                map.Group[0].Element.Add(new() { Code = "UnmappedCoding", NoMap = true });
            }
            File.WriteAllText(filename, map.ToJson());

            filename = Path.Combine(SourceRoot, "input", "elements", "ConceptMap-elements-" + suffix + ".json");
            map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
            map.Group[0].Element.RemoveAll(element => element.Code == "Coding");
            File.WriteAllText(filename, map.ToJson());
        }
        PublishAuthoredDefinitions();
    }

    internal DbElement AddDuplicateMixedChoiceRoot(DbStructureDefinition structure)
    {
        DbElement duplicate = AddMixedChoiceElement(structure, "MalformedRoot", []);
        duplicate.ResourceFieldOrder = 0;
        DbElement.Update(Connection, [duplicate]);
        return duplicate;
    }

    internal void WriteMixedChoiceOwnerObservation(string stage)
    {
        DbStructureDefinition[] owners = [.. DbStructureDefinition.SelectList(Connection)
            .Where(structure => structure.Id is "Coding" or "SelectedCoding" or "UnmappedCoding")];
        HashSet<int> ownerKeys = [.. owners.Select(owner => owner.Key)];
        WriteMixedChoiceObservation("MEASURED_H2_OWNERS", new
        {
            stage, SourceRoot,
            authored = Directory.GetFiles(Path.Combine(SourceRoot, "input"), "*.json", SearchOption.AllDirectories)
                .Where(filename => filename.Contains("ConceptMap-elements-") || filename.Contains("ConceptMap-types-"))
                .Select(filename => new
                {
                    filename = Path.GetRelativePath(SourceRoot, filename),
                    groups = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename)).Group.Select(group => new
                    {
                        group.Source, group.Target,
                        elements = group.Element.Where(element => element.Code?.Contains("Coding", StringComparison.Ordinal) == true),
                    }),
                }),
            owners = owners.Select(owner => new { owner.Key, owner.FhirPackageKey, owner.Id, owner.Name, owner.VersionedUrl }),
            roots = DbElement.SelectList(Connection, ResourceFieldOrder: 0).Where(root => ownerKeys.Contains(root.StructureKey))
                .Select(root => new { root.Key, root.FhirPackageKey, root.StructureKey, root.Id, root.Path, root.BaseElementKey }),
            structureMaps = DbStructureMapping.SelectList(Connection).Where(map => ownerKeys.Contains(map.SourceStructureKey)),
            elementMaps = DbElementMapping.SelectList(Connection).Where(map => ownerKeys.Contains(map.SourceStructureKey)),
            structureComparisons = HasTable("StructureComparisons")
                ? DbStructureComparison.SelectList(Connection).Where(row => ownerKeys.Contains(row.SourceStructureKey))
                    .Select(row => new { row.Key, row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                        row.SourceStructureKey, row.TargetStructureKey, row.StructureMappingKey, row.NotMapped, row.ContentKeys }).ToArray() : [],
            elementComparisons = HasTable("ElementComparisons")
                ? DbElementComparison.SelectList(Connection).Where(row => ownerKeys.Contains(row.SourceStructureKey))
                    .Select(row => new { row.Key, row.StructureComparisonKey, row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                        row.SourceStructureKey, row.SourceElementKey, row.SourceElementId,
                        row.TargetStructureKey, row.TargetElementKey, row.TargetElementId,
                        row.ElementMappingKey, row.NotMapped, row.ContentKeys }).ToArray() : [],
            structureOutcomes = HasTable("StructureOutcomes")
                ? DbStructureOutcome.SelectList(Connection).Where(row => row.SourceStructureKey is int key && ownerKeys.Contains(key)).ToArray() : [],
            elementOutcomes = HasTable("ElementOutcomes")
                ? DbElementOutcome.SelectList(Connection).Where(row => ownerKeys.Contains(row.SourceStructureKey))
                    .Select(row => new { row.Key, row.SourceFhirPackageKey, row.TargetFhirPackageKey,
                        row.SourceStructureKey, row.SourceElementKey, row.SourceResourceOrder }).ToArray() : [],
            targets = HasTable("ElementOutcomeTargets") ? DbElementOutcomeTarget.SelectList(Connection) : [],
        });

        bool HasTable(string table)
        {
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);
            return (long)command.ExecuteScalar()! != 0;
        }
    }

    private DbElement AddMixedChoiceElement(
        DbStructureDefinition structure, string id, string[] types, int min = 0, int max = 1, DbValueSet? binding = null)
    {
        DbFhirPackage package = Packages.Single(package => package.Key == structure.FhirPackageKey);
        List<DbElement> siblings = DbElement.SelectList(Connection, StructureKey: structure.Key);
        int separator = id.LastIndexOf('.');
        TestElement row = new(package, structure.Key, id)
        {
            ParentElementKey = separator < 0 ? null : siblings.Single(parent => parent.Id == id[..separator]).Key,
            ResourceFieldOrder = siblings.Count,
            ComponentFieldOrder = siblings.Count,
            MinCardinality = min,
            MaxCardinality = max,
            MaxCardinalityString = max < 0 ? "*" : max.ToString(),
            FullCollatedTypeLiteral = string.Join(',', types),
            DistinctTypeLiterals = string.Join(',', types),
            DistinctTypeCount = types.Length,
            IsChoiceType = types.Length > 1,
            IsSimpleType = types.Length == 1,
            IsInherited = false,
            BaseElementKey = null,
            BaseStructureKey = null,
            BasePath = null,
            DefinedAsContentReference = false,
            ContentReferenceSourceKey = null,
            BindingValueSet = binding?.VersionedUrl,
            BindingValueSetKey = binding?.Key,
            ValueSetBindingStrength = binding is null ? null : BindingStrength.Required,
        };
        row.Key = DbElement.Insert(Connection, row);
        if (row.ParentElementKey is int parentKey)
        {
            DbElement parent = siblings.Single(parent => parent.Key == parentKey);
            parent.ChildElementCount++;
            DbElement.Update(Connection, [parent]);
        }
        foreach (string type in types)
        {
            AddType(row, type, Structure(package, type));
        }
        return row;
    }

    private void WriteMixedChoiceMap(string category, string suffix, string source, string target, string[] ids)
    {
        ConceptMap map = new()
        {
            Id = "mixed-choice-" + category + "-" + suffix,
            Url = "http://example.org/ConceptMap/mixed-choice-" + category + "-" + suffix,
            Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = source,
                Target = target,
                Element = [.. ids.Select(id => new ConceptMap.SourceElementComponent
                {
                    Code = id,
                    Target = [new() { Code = id, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                })],
            }],
        };
        string directory = Path.Combine(SourceRoot, "input", category);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ConceptMap-" + category + "-" + suffix + ".json"), map.ToJson());
    }

    internal SortedDictionary<string, string[]> MixedChoiceRepresentationState()
    {
        // All persisted columns participate; only safety policy/completion may differ between staged twins.
        Dictionary<string, Dictionary<long, SortedDictionary<string, object?>>> rows = [];
        Dictionary<string, Dictionary<string, string>> references = [];
        List<string> tables = [];
        using (SqliteCommand schema = Connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "AND name NOT IN ('UnsafeCodeInputState', 'UnsafeCodePairState', 'UnsafeCodeRules', 'UnsafeCodeTriggers') ORDER BY name";
            using SqliteDataReader reader = schema.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        foreach (string table in tables)
        {
            rows[table] = [];
            references[table] = [];
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\"";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    SortedDictionary<string, object?> row = new(StringComparer.Ordinal);
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                    }
                    rows[table].Add((long)row["Key"]!, row);
                }
            }
            command.CommandText = "PRAGMA foreign_key_list(\"" + table + "\")";
            using SqliteDataReader foreignKeys = command.ExecuteReader();
            while (foreignKeys.Read())
            {
                references[table].Add(foreignKeys.GetString(3), foreignKeys.GetString(2));
            }
        }

        Dictionary<(string Table, long Key), string> identities = [];
        HashSet<(string Table, long Key)> visiting = [];
        SortedDictionary<string, string[]> state = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            state.Add(table, [.. rows[table].Keys.Select(key => Normalize(table, key)).Order(StringComparer.Ordinal)]);
        }
        return state;

        string Normalize(string table, long key)
        {
            if (identities.TryGetValue((table, key), out string? cached))
            {
                return cached;
            }
            if (!visiting.Add((table, key)))
            {
                throw new InvalidOperationException("Unexpected cyclic mixed-choice fixture foreign key: " + table);
            }
            SortedDictionary<string, object?> normalized = new(StringComparer.Ordinal);
            foreach ((string column, object? value) in rows[table][key])
            {
                if (column == "Key")
                {
                    continue;
                }
                if (TryNormalizeInventoryValue(table, column, value, out object? inventoryValue))
                {
                    normalized[column] = inventoryValue;
                    continue;
                }
                string? reference = references[table].GetValueOrDefault(column) ?? column switch
                {
                    "FhirPackageKey" or "SourceFhirPackageKey" or "TargetFhirPackageKey" => "FhirPackages",
                    "ConceptMapSourceKey" or "FmlSourceKey" => "MappingSourceFiles",
                    "StructureKey" or "SourceStructureKey" or "TargetStructureKey" or "BaseStructureKey" or
                        "TypeStructureKey" or "TypeProfileStructureKey" or "TargetProfileStructureKey" => "Structures",
                    "ElementKey" or "SourceElementKey" or "TargetElementKey" or "BaseElementKey" or
                        "ParentElementKey" or "ContentReferenceSourceKey" or "ContextElementKey" or
                        "CardinalityContextElementKey" => "Elements",
                    "SourceElementTypeKey" or "TargetElementTypeKey" => "ElementTypes",
                    "BindingValueSetKey" or "SourceValueSetKey" or "TargetValueSetKey" => "ValueSets",
                    "SourceValueSetConceptKey" or "TargetValueSetConceptKey" => "ValueSetConcepts",
                    "StructureMappingKey" => "StructureMappings",
                    "ElementMappingKey" => "ElementMappings",
                    "ValueSetMappingKey" => "ValueSetMappings",
                    "StructureComparisonKey" => "StructureComparisons",
                    "ElementComparisonKey" => "ElementComparisons",
                    "ValueSetComparisonKey" or "BoundValueSetComparisonKey" => "ValueSetComparisons",
                    "StructureOutcomeKey" => "StructureOutcomes",
                    "ElementOutcomeKey" or "ParentElementOutcomeKey" or "AncestorElementOutcomeKey" or
                        "ContentReferenceOutcomeKey" or "SourceAncestorContentReferenceOutcomeKey" => "ElementOutcomes",
                    "ExtensionSubstitutionKey" => "ExtensionSubstitutions",
                    _ => null,
                };
                if (column.StartsWith("ContentKeyR", StringComparison.Ordinal))
                {
                    reference = table switch
                    {
                        "StructureComparisons" => "Structures",
                        "ElementComparisons" => "Elements",
                        "ElementTypeComparisons" => "ElementTypes",
                        "ValueSetComparisons" => "ValueSets",
                        "ValueSetConceptComparisons" => "ValueSetConcepts",
                        _ => throw new InvalidOperationException("Unknown mixed-choice content reference: " + table),
                    };
                }
                if (value is long foreignKey && reference is not null)
                {
                    normalized[column] = Identity(reference, foreignKey);
                }
                else if (value is string literal && column.EndsWith("KeysLiteral", StringComparison.Ordinal))
                {
                    string memberTable = column.Contains("ChildElement", StringComparison.Ordinal) ? "Elements" : "ElementTypes";
                    normalized[column] = literal.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(member => Identity(memberTable, long.Parse(member))).Order(StringComparer.Ordinal).ToArray();
                }
                else if (value is long && column.EndsWith("Key", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unaccounted mixed-choice foreign key: " + table + "." + column);
                }
                else
                {
                    normalized[column] = value;
                }
            }
            visiting.Remove((table, key));
            string result = JsonSerializer.Serialize(normalized);
            identities.Add((table, key), result);
            return result;
        }

        string Identity(string table, long key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(table, key))));
    }

    internal void WriteMixedChoiceStageObservation(FhirPackageComparisonPair pair, string alternative, string qualification)
    {
        WriteMixedChoiceObservation("MEASURED_H2_STAGE", new
        {
            sourceRoot = SourceRoot, alternative, qualification,
            packages = Packages.Select(package => new { package.Key, package.PackageId, package.PackageVersion }),
            input = InputState, state = PairState(pair), reopens = ConnectionReopens,
            choices = DbElement.SelectList(Connection, Id: MixedChoiceElement).Select(element => new
            {
                element.Key, element.FhirPackageKey, element.StructureKey, element.ParentElementKey,
                element.IsChoiceType, element.IsSimpleType, element.DistinctTypeCount, element.DistinctTypeLiterals,
                element.FullCollatedTypeLiteral, element.BindingValueSet, element.BindingValueSetKey,
                types = DbElementType.SelectList(Connection, ElementKey: element.Key),
            }),
            bindings = DbValueSet.SelectList(Connection).Select(binding => new
                { binding.Key, binding.FhirPackageKey, binding.VersionedUrl, binding.Version, binding.CanExpand,
                    includes = binding.Compose!.Include.Select(include => new { include.System, include.Version,
                        codes = include.Concept.Select(concept => concept.Code) }) }),
            maps = DbElementMapping.SelectList(Connection, SourceElementId: MixedChoiceElement).Select(map => new
                { map.Key, map.SourceFhirPackageKey, map.TargetFhirPackageKey, map.SourceElementId, map.TargetElementId,
                    map.ConceptMapFilename, map.ExplicitNoMap }),
            comparisons = DbElementComparison.SelectList(Connection, SourceElementId: MixedChoiceElement).Select(comparison => new
            {
                comparison.Key, comparison.SourceFhirPackageKey, comparison.TargetFhirPackageKey,
                comparison.SourceElementId, comparison.TargetElementId, comparison.ElementMappingKey, comparison.Steps,
                comparison.ContentKeys, comparison.Relationship,
                types = DbElementTypeComparison.SelectList(Connection, ElementComparisonKey: comparison.Key)
                    .Select(type => new { type.SourceTypeLiteral, type.TargetTypeLiteral, type.Relationship,
                        type.SourceElementTypeKey, type.TargetElementTypeKey }),
                targets = DbElementOutcomeTarget.SelectList(Connection, ElementComparisonKey: comparison.Key),
            }),
            outcomes = DbStructureOutcome.SelectList(Connection).Select(outcome => new
                { outcome.Key, outcome.SourceId, outcome.TargetId, outcome.GenUrl }),
            rules = DbUnsafeCodeRule.SelectList(Connection), triggers = DbUnsafeCodeTrigger.SelectList(Connection),
        });
    }

    internal static void WriteMixedChoiceObservation(string label, object observation)
    {
        Console.WriteLine(label + " " + JsonSerializer.Serialize(observation));
    }

    internal static object MixedChoiceAttribution()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AGENTS.md")))
        {
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        }

        string baseline = Git("rev-parse", "HEAD").Trim();
        return new
        {
            productionBase = baseline,
            productionDiffSha256 = Hash(Git("--no-pager", "diff", "--binary", "HEAD", "--", "src/Fhir.CodeGen.Comparison")),
            testDiffSha256 = Hash(Git("--no-pager", "diff", "--binary", "HEAD", "--",
                "src/Fhir.CodeGen.Lib.Tests/UnsafeCodeTestFixture.cs", "src/Fhir.CodeGen.Lib.Tests/UnsafeCodeProfileTests.cs")),
        };

        string Git(params string[] arguments)
        {
            ProcessStartInfo start = new("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Cannot attribute mixed-choice measurement: " + error);
            }
            return output;
        }

        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    internal static string ParametersJson(IEnumerable<string>? parameters, string? uri = null)
    {
        return """{"resourceType":"Parameters","id":"condition-control" """ +
            (uri is null ? "" : ""","implicitRules":""" + JsonSerializer.Serialize(uri)) +
            (parameters is null ? "" : ""","parameter":[""" + string.Join(',', parameters) + "]") + "}";
    }

    internal void PersistLegacyMixedChoicePlans(FhirPackageComparisonPair pair)
    {
        IReadOnlyList<UnsafeCodeConstraintPlan> plans = UnsafeCodeInputStore.ReadConstraintPlans(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        foreach (UnsafeCodeConstraintPlan plan in plans)
        {
            if (plan.SourcePath[^1].TypeName == "code")
            {
                DbUnsafeCodeTrigger.Delete(Connection, Key: plan.Row.Key);
                continue;
            }

            DbUnsafeCodeTrigger row = plan.Row;
            row.SourcePathJson = JsonSerializer.Serialize(plan.SourcePath[..^1]);
            DbUnsafeCodeTrigger.Update(Connection, [row]);
        }
        ReopenPersistedDatabase();
    }

    internal void AddMixedChoiceRecursion()
    {
        foreach (DbFhirPackage package in Packages)
        {
            DbStructureDefinition structure = Structure(package, "Parameters");
            DbElement parent = DbElement.SelectSingle(Connection, StructureKey: structure.Key, Id: "Parameters.parameter")!;
            DbElement part = AddMixedChoiceElement(structure, "Parameters.parameter.part", [], max: -1);
            part.ContentReferenceSourceKey = parent.Key;
            part.DefinedAsContentReference = true;
            DbElement.Update(Connection, [part]);
            structure.SnapshotCount++;
            DbStructureDefinition.Update(Connection, [structure]);
        }

        foreach (string filename in Directory.GetFiles(Path.Combine(SourceRoot, "input", "elements"), "*.json"))
        {
            ConceptMap map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
            map.Group[0].Element.Add(new()
            {
                Code = "Parameters.parameter.part",
                Target = [new() { Code = "Parameters.parameter.part", Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
            });
            File.WriteAllText(filename, map.ToJson());
        }
        PublishAuthoredDefinitions();
    }

    internal const string MixedCodingProfile = "http://example.org/StructureDefinition/SelectedCoding|coding-profile-v7";

    internal void AddMixedChoiceCodingProfile()
    {
        DbFhirPackage package = Packages[2];
        DbStructureDefinition basis = Structure(package, "Coding");
        DbElement basisRoot = DbElement.SelectSingle(Connection, StructureKey: basis.Key, Id: "Coding")!;
        DbStructureDefinition profile = AddStructure(package, "SelectedCoding", FhirArtifactClassEnum.Profile);
        DeclareProfile(profile, basis);
        profile.UnversionedUrl = MixedCodingProfile.Split('|')[0];
        profile.Version = MixedCodingProfile.Split('|')[1];
        profile.VersionedUrl = MixedCodingProfile;
        DbStructureDefinition.Update(Connection, [profile]);
        DbElement root = AddMixedChoiceElement(profile, "Coding", []);
        root.BaseStructureKey = basis.Key;
        root.BaseElementKey = basisRoot.Key;
        root.BasePath = "Coding";
        DbElement.Update(Connection, [root]);
        DbElement choice = DbElement.SelectSingle(Connection, FhirPackageKey: package.Key, Id: MixedChoiceElement)!;
        DbElementType coding = DbElementType.SelectList(Connection, ElementKey: choice.Key).Single(type => type.TypeName == "Coding");
        coding.TypeProfile = MixedCodingProfile;
        coding.TypeProfileStructureKey = profile.Key;
        DbElementType.Update(Connection, [coding]);
        List<DbElementType> types = DbElementType.SelectList(Connection, ElementKey: choice.Key);
        choice.FullCollatedTypeLiteral = string.Join(',', types.Select(type => type.Literal));
        string[] typeNames = [.. types.Select(type => type.TypeName!).Distinct().Order()];
        choice.DistinctTypeLiterals = string.Join(',', typeNames);
        choice.DistinctTypeCount = typeNames.Length;
        DbElement.Update(Connection, [choice]);
        PublishAuthoredDefinitions();
    }

    internal string SafetyPersistenceSnapshot() => JsonSerializer.Serialize(new
    {
        Input = DbUnsafeCodeInputState.SelectList(Connection),
        Pairs = DbUnsafeCodePairState.SelectList(Connection),
        Triggers = DbUnsafeCodeTrigger.SelectList(Connection),
        Rules = DbUnsafeCodeRule.SelectList(Connection),
        Evidence = DbUnsafeCodeMapEvidence.SelectList(Connection),
        Annotations = DbUnsafeCodeElementAnnotation.SelectList(Connection),
    });

    internal string SchemaSnapshot()
    {
        using SqliteCommand command = Connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string Name, string Sql)> schema = [];
        while (reader.Read())
        {
            schema.Add((reader.GetString(0), reader.GetString(1)));
        }

        return JsonSerializer.Serialize(schema.Select(table => new { table.Name, table.Sql }));
    }

    internal static UnsafeCodeTestFixture CreateProfileInputs(bool initializeIndices = true, bool persistDatabase = false)
    {
        UnsafeCodeTestFixture fixture = new(initializeIndices, persistDatabase, publishDefinitions: false, includeLegacyPackages: true);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            DbStructureDefinition code = fixture.AddStructure(package, "code", FhirArtifactClassEnum.PrimitiveType);
            DbStructureDefinition text = fixture.AddStructure(package, "string", FhirArtifactClassEnum.PrimitiveType);
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddElement(extension, "Extension");
            DbElement value = fixture.AddElement(extension, "Extension.value[x]", "code,string");
            fixture.AddType(value, "code", code);
            fixture.AddType(value, "string", text);

            DbStructureDefinition structure = fixture.Structure(package);
            fixture.AddElement(structure, "ValueSet");
            fixture.AddElement(structure, "ValueSet.compose", "BackboneElement");
            fixture.AddElement(structure, "ValueSet.compose.include", "BackboneElement");
            fixture.AddElement(structure, "ValueSet.compose.include.filter", "BackboneElement");
            DbElement op = fixture.AddElement(structure, FilterElement, "code", FilterValueSet);
            fixture.AddType(op, "code", code);
            if (package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5)
            {
                op.MaxCardinality = -1;
                op.MaxCardinalityString = "*";
                DbElement.Update(fixture.Connection, [op]);
            }

            DbElement filterValue = fixture.AddElement(structure, "ValueSet.compose.include.filter.value", "string");
            fixture.AddType(filterValue, "string", text);
            fixture.SetBinding(package, systemVersion: package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5
                ? "source-system,version" : package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R4 ? "target-system-r4" : "target-system-r4b",
                valueSetVersion: package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5
                    ? "source-vs" : package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R4 ? "target-vs-r4" : "target-vs-r4b");
        }

        fixture.PublishAuthoredDefinitions();
        return fixture;
    }

    internal static UnsafeCodeTestFixture CreateNoFeatureInputs(bool initializeIndices = true, bool persistDatabase = false)
    {
        UnsafeCodeTestFixture fixture = CreateProfileInputs(initializeIndices, persistDatabase);
        foreach (string filename in Directory.GetFiles(Path.Combine(fixture.SourceRoot, "input", "elements"), "*.json"))
        {
            ConceptMap map = new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(filename));
            foreach (ConceptMap.TargetElementComponent target in map.Group.SelectMany(group => group.Element).SelectMany(element => element.Target))
            {
                target.Property.RemoveAll(property => property.Code == UnsafeCodeConstants.PropertyCode && property.Value is FhirBoolean { Value: true });
            }

            File.WriteAllText(filename, map.ToJson());
        }

        if (File.Exists(fixture.ConfigurationFilename))
        {
            File.Delete(fixture.ConfigurationFilename);
        }

        return fixture;
    }

    internal FhirPackageComparisonPair LoadCompareAndGenerate(int targetIndex)
    {
        LoadProfileMaps();
        FhirPackageComparisonPair pair = new(Packages[2], Packages[targetIndex]);
        CompareProfilePair(pair);
        GenerateProfileOutcomes(pair);
        return pair;
    }

    internal void GenerateProfileOutcomes(params FhirPackageComparisonPair[] pairs)
    {
        _persistedStage = "GenerateProfileOutcomes entered";
        new OutcomeGenerator(Connection, NullLoggerFactory.Instance).GenerateOutcomes(processValueSets: false,
            specificPairs: pairs.Select(pair => pair.SequencePair).ToHashSet());
        _persistedStage = "GenerateProfileOutcomes completed";
        ReopenPersistedDatabase();
    }

    internal StructureFhirExporter.BuiltDefinitions BuildDefinitions(FhirPackageComparisonPair pair)
    {
        _persistedStage = "BuildDefinitions entered";
        StructureFhirExporter.BuiltDefinitions built = new StructureFhirExporter(new XVerExporter(Connection, new ConfigXVer { XverArtifactVersion = "0.1.0-test" }),
            Connection, NullLoggerFactory.Instance).BuildDefinitions(new IgExporter.XVerIgExportTrackingRecord
            {
                PackagePair = pair,
                PackageId = "hl7.fhir.uv.xver-r5." + pair.TargetFhirSequence.ToRLiteral().ToLowerInvariant(),
            });
        _persistedStage = "BuildDefinitions completed";
        ReopenPersistedDatabase();
        return built;
    }

    internal void ReopenPersistedDatabase()
    {
        if (!_persistDatabase)
        {
            return;
        }

        string connectionString = Connection.ConnectionString;
        WriteMixedChoiceLifetimeObservation("physical-close");
        Connection.Dispose();
        Connection = new(connectionString);
        Connection.Open();
        ConnectionReopens++;
        WriteMixedChoiceLifetimeObservation("physical-open");
    }

    internal static Hl7.Fhir.Model.ElementDefinition.ConstraintComponent[] SerializedConstraints(
        Hl7.Fhir.Model.StructureDefinition profile, int targetIndex)
    {
        ParserSettings settings = new() { PermissiveParsing = true };
        Hl7.Fhir.Model.StructureDefinition recovered = targetIndex == 0
            ? new r4::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<Hl7.Fhir.Model.StructureDefinition>(profile.ToJson())
            : new r4b::Hl7.Fhir.Serialization.FhirJsonParser(settings).Parse<Hl7.Fhir.Model.StructureDefinition>(profile.ToJson());
        return [.. recovered.Differential.Element.SelectMany(element => element.Constraint)
            .Where(constraint => constraint.Key.StartsWith("xv-unsafe-code-", StringComparison.Ordinal))];
    }

    internal static bool EvaluateFilter(
        Hl7.Fhir.Model.ElementDefinition.ConstraintComponent[] constraints, int targetIndex, string? code,
        string? uri = null, string? extension = null, bool mixed = false)
    {
        string filter = "{\"property\":\"status\",\"op\":" + JsonSerializer.Serialize(extension is null ? code : "=") +
            ",\"value\":\"active\"" + (extension is null ? "" : ",\"extension\":[{\"url\":" +
                JsonSerializer.Serialize(extension) + ",\"valueCode\":" + JsonSerializer.Serialize(code) + "}]") + "}";
        string json = "{\"resourceType\":\"ValueSet\",\"status\":\"active\"" +
            (uri is null ? "" : ",\"implicitRules\":" + JsonSerializer.Serialize(uri)) +
            (code is null ? "" : ",\"compose\":{\"include\":[{\"filter\":[" +
                (mixed ? "{\"property\":\"status\",\"op\":\"=\",\"value\":\"active\"}," : "") + filter + "]}]}") + "}";
        ITypedElement instance = TargetInstance(targetIndex, json);
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        bool valid = true;
        foreach (Hl7.Fhir.Model.ElementDefinition.ConstraintComponent constraint in constraints)
        {
            ITypedElement[] results = [.. compiler.Compile(constraint.Expression)(instance, new EvaluationContext())];
            if (results.Length != 1 || results[0].Value is not bool result)
            {
                throw new InvalidOperationException("The serialized invariant did not return exactly one Boolean.");
            }

            valid &= result;
        }

        return valid;
    }

    internal void LoadProfileMaps()
    {
        _persistedStage = "LoadProfileMaps entered";
        if (!Load())
        {
            throw new InvalidOperationException("The synthetic cross-version maps did not load.");
        }

        _persistedStage = "LoadProfileMaps completed";
        ReopenPersistedDatabase();
    }

    internal void CompareProfilePair(FhirPackageComparisonPair pair)
    {
        CompareProfilePairs(new(Packages[2], Packages[1]), new(Packages[1], Packages[0]), pair);
    }

    internal void CompareProfilePairs(params FhirPackageComparisonPair[] pairs)
    {
        _persistedStage = "CompareProfilePairs entered";
        // Match FhirDbComparer.Compare's reset boundary when reusing a database.
        DbComparisonClasses.DropTables(Connection, forValueSets: false);
        DbComparisonClasses.CreateTables(Connection);
        new StructureComparer(Connection, NullLoggerFactory.Instance).CompareStructures(
            specificPairs: pairs.Select(pair => pair.SequencePair).ToHashSet());
        _persistedStage = "CompareProfilePairs completed";
        ReopenPersistedDatabase();
    }

    internal static UnsafeCodeTestFixture CreateRepresentationInputs(bool persistDatabase = false)
    {
        UnsafeCodeTestFixture fixture = new(persistDatabase: persistDatabase, publishDefinitions: false);
        fixture.InsertRepresentationGraph(includeOtherCarriers: true);
        fixture.PublishAuthoredDefinitions();
        return fixture;
    }

    internal static UnsafeCodeTestFixture CreateProcessingEligibilityInputs()
    {
        UnsafeCodeTestFixture fixture = new(initializeIndices: true, publishDefinitions: false, includeLegacyPackages: true);
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input"), recursive: true);
        fixture.InsertRepresentationGraph(includeOtherCarriers: true);
        string[] nativeTypes = ["Age", "Count", "Distance", "Duration", "Money"];
        string[] passiveBases = ["Age", "Coding", "ValueSet", "Money"];
        foreach (DbFhirPackage package in fixture.Packages)
        {
            DbStructureDefinition quantity = fixture.Structure(package, "Quantity");
            foreach (string name in nativeTypes)
            {
                DbStructureDefinition native = fixture.AddStructure(package, name);
                fixture.AddElement(native, name);
                fixture.AddTypedElement(native, name + ".code", "code");
                if (name != "Money")
                {
                    fixture.DeclareBaseDefinition(native, quantity);
                }
            }
            fixture.AddTypedElement(fixture.Structure(package), "ValueSet.code", "code");

            foreach (string name in passiveBases)
            {
                DbStructureDefinition basis = fixture.Structure(package, name);
                DbStructureDefinition profile = fixture.AddStructure(package,
                    name == "Money" ? "MoneyQuantity" : "Passive" + name, FhirArtifactClassEnum.Profile);
                profile.Version = "profile-definition-7";
                profile.VersionedUrl = profile.UnversionedUrl + "|" + profile.Version;
                DbStructureDefinition.Update(fixture.Connection, [profile]);
                fixture.DeclareProfile(profile, basis);
                fixture.AddElement(profile, name);
                fixture.AddTypedElement(profile, name + ".code", "code");
            }

            bool isProfile = package.DefinitionFhirSequence != FhirReleases.FhirSequenceCodes.R4B;
            DbStructureDefinition simple = fixture.AddStructure(package, "SimpleQuantity",
                isProfile ? FhirArtifactClassEnum.Profile : FhirArtifactClassEnum.ComplexType);
            if (isProfile)
            {
                simple.Version = "quantity-definition-7";
                simple.VersionedUrl = simple.UnversionedUrl + "|" + simple.Version;
                DbStructureDefinition.Update(fixture.Connection, [simple]);
                fixture.DeclareProfile(simple, quantity);
            }
            else
            {
                fixture.DeclareBaseDefinition(simple, quantity);
            }
            string rootId = isProfile ? "Quantity:simplequantity" : "SimpleQuantity";
            fixture.AddElement(simple, rootId);
            DbElement code = fixture.AddTypedElement(simple, rootId + ".code", "code");
            if (isProfile)
            {
                DbElement root = DbElement.SelectSingle(fixture.Connection, StructureKey: simple.Key, ResourceFieldOrder: 0)!;
                root.Path = "Quantity";
                code.Path = "Quantity.code";
                DbElement.Update(fixture.Connection, [root, code]);
            }
        }
        fixture.PublishAuthoredDefinitions();
        return fixture;
    }

    internal void WriteOwnerElementMap(FhirPackageComparisonPair pair, DbStructureDefinition source,
        DbStructureDefinition target, string name, bool fml = false, bool annotate = false)
    {
        DbElement sourceRoot = DbElement.SelectSingle(Connection, StructureKey: source.Key, ResourceFieldOrder: 0)!;
        DbElement targetRoot = DbElement.SelectSingle(Connection, StructureKey: target.Key, ResourceFieldOrder: 0)!;
        if (fml)
        {
            string directory = Path.Combine(SourceRoot, "input", pair.SourcePackageShortName + "to" + pair.TargetPackageShortName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".fml"), $$"""
                /// url = "http://example.org/StructureMap/{{name}}"
                /// name = "{{name}}"
                uses "{{source.VersionedUrl}}" alias SourceOwner as source
                uses "{{target.VersionedUrl}}" alias TargetOwner as target
                group {{name}}(source src : SourceOwner, target tgt : TargetOwner) {
                  src.code -> tgt.code;
                }
                """);
            return;
        }

        ConceptMap map = new()
        {
            Id = name, Url = "http://example.org/ConceptMap/" + name, Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = source.VersionedUrl, Target = target.VersionedUrl,
                Element = [new()
                {
                    Code = sourceRoot.Id + ".code",
                    Target = [new()
                    {
                        Code = targetRoot.Id + ".code", Relationship = ConceptMap.ConceptMapRelationship.Equivalent,
                        Property = annotate ? [new() { Code = UnsafeCodeConstants.PropertyCode, Value = new FhirBoolean(true) }] : [],
                    }],
                }],
            }],
        };
        if (annotate)
        {
            map.Property = [new()
            {
                Code = UnsafeCodeConstants.PropertyCode, System = UnsafeCodeConstants.PropertySystem,
                Type = ConceptMap.ConceptMapPropertyType.Boolean,
            }];
        }
        string direction = pair.SourcePackageShortName[1..] + "to" + pair.TargetPackageShortName[1..];
        WriteInputMap(map, "elements", "ConceptMap-elements-" + name + "-" + direction + ".json");
    }

    internal void WriteProcessingTypeMaps()
    {
        WriteInputMap(new ConceptMap
        {
            Id = "processing-fallback", Url = "http://example.org/ConceptMap/processing-fallback", Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = "http://hl7.org/fhir/fhir-types", Target = "http://hl7.org/fhir/fhir-types",
                Element = [.. DbStructureDefinition.SelectList(Connection, FhirPackageKey: Packages[2].Key)
                    .Select(owner => new ConceptMap.SourceElementComponent
                    {
                        Code = owner.Id,
                        Target = [new()
                        {
                            Code = owner.Id,
                            Relationship = owner.Id == "SimpleQuantity" ? ConceptMap.ConceptMapRelationship.NotRelatedTo
                                : ConceptMap.ConceptMapRelationship.Equivalent,
                            Comment = "Authored fallback authority",
                        }],
                    })],
            }],
        }, "types", "ConceptMap-types-fallback.json");
        (int Source, int Target)[] neighbors = [(2, 1), (1, 2), (1, 0), (0, 1)];
        foreach ((int source, int target) in neighbors)
        {
            string direction = Packages[source].ShortName[1..] + "to" + Packages[target].ShortName[1..];
            WriteInputMap(new ConceptMap
            {
                Id = "quantity-" + direction, Url = "http://example.org/ConceptMap/quantity-" + direction, Status = PublicationStatus.Active,
                Group = [new()
                {
                    Source = "http://hl7.org/fhir/" + Packages[source].FhirVersionShort + "/data-types",
                    Target = "http://hl7.org/fhir/" + Packages[target].FhirVersionShort + "/data-types",
                    Element = [new()
                    {
                        Code = "SimpleQuantity",
                        Target = [new()
                        {
                            Code = "SimpleQuantity", Relationship = ConceptMap.ConceptMapRelationship.Equivalent,
                            Comment = "Authored primary Quantity authority",
                        }],
                    }],
                }],
            }, "types", "ConceptMap-types-quantity-" + direction + ".json");
        }
    }

    internal (FhirPackageComparisonPair Pair, DbStructureOutcome Owner, DbElement Source, DbElement Target)
        AddOwnerProjectionAlternatives(int targetIndex, string alternatives, bool commonRestriction = false)
    {
        FhirPackageComparisonPair pair = PreparePair(2, targetIndex);
        Dictionary<int, DbElement> uses = [];
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            DbStructureDefinition coding = Structure(package, "Coding");
            foreach (DbElement child in DbElement.SelectList(Connection, StructureKey: coding.Key).Where(element => element.Id.Contains('.')))
            {
                child.MinCardinality = 0;
                DbElement.Update(Connection, [child]);
            }
            DbStructureDefinition resource = AddStructure(package, "Parameters", FhirArtifactClassEnum.Resource);
            AddElement(resource, "Parameters");
            AddTypedElement(resource, "Parameters.parameter", "BackboneElement");
            DbElement use = AddElement(resource, MixedChoiceElement, "Coding");
            use.IsChoiceType = true;
            DbElement.Update(Connection, [use]);
            uses.Add(package.Key, use);
            if (package.Key == pair.TargetPackageKey)
            {
                AddType(use, "Coding", coding);
                continue;
            }
            List<string> profiles = [];
            foreach (string name in new[] { "AlternativeCodingA", "AlternativeCodingB" })
            {
                if (name == "AlternativeCodingA" && alternatives is "unprofiled-type" or "other-type")
                {
                    string type = alternatives == "other-type" ? "CodeableConcept" : "Coding";
                    AddType(use, type, Structure(package, type));
                    continue;
                }
                DbStructureDefinition profile = AddStructure(package, name, FhirArtifactClassEnum.Profile);
                DeclareProfile(profile, coding);
                AddElement(profile, "Coding");
                DbElement code = AddTypedElement(profile, "Coding.code", "code");
                code.MinCardinality = 0;
                DbElement.Update(Connection, [code]);
                if (commonRestriction || name == "AlternativeCodingB")
                {
                    ConfigureDefinition(profile, definition =>
                        definition.Snapshot.Element.Single(element => element.Path == "Coding.code").Fixed = new Hl7.Fhir.Model.Code("selected"));
                }
                AddType(use, "Coding", coding, profile);
                profiles.Add(profile.VersionedUrl);
            }
            if (alternatives == "profiles")
            {
                ConfigureDefinition(resource, definition =>
                    definition.Snapshot.Element.Single(element => element.ElementId == use.Id).Type =
                        [new() { Code = "Coding", Profile = profiles }]);
            }
        }
        DbElement source = uses[pair.SourcePackageKey];
        DbElement target = uses[pair.TargetPackageKey];
        DbStructureOutcome owner = AddOutcome(pair, Structure(pair.SourcePackage, "Parameters"), Structure(pair.TargetPackage, "Parameters"));
        Connect(pair, owner, source, target);
        CompleteAuthoredArrangement(pair);
        return (pair, owner, source, target);
    }

    private void InsertRepresentationGraph(bool includeOtherCarriers)
    {
        UnsafeCodeTestFixture fixture = this;
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "code", "string", "uri" })
            {
                fixture.AddStructure(package, primitive, FhirArtifactClassEnum.PrimitiveType);
            }

            DbStructureDefinition coding = fixture.AddStructure(package, "Coding");
            fixture.AddElement(coding, "Coding");
            fixture.AddTypedElement(coding, "Coding.system", "uri");
            fixture.AddTypedElement(coding, "Coding.version", "string");
            fixture.AddTypedElement(coding, "Coding.code", "code");
            DbStructureDefinition concept = fixture.AddStructure(package, "CodeableConcept");
            fixture.AddElement(concept, "CodeableConcept");
            fixture.AddTypedElement(concept, "CodeableConcept.coding", "Coding");
            if (includeOtherCarriers)
            {
                DbStructureDefinition quantity = fixture.AddStructure(package, "Quantity");
                fixture.AddElement(quantity, "Quantity");
                fixture.AddTypedElement(quantity, "Quantity.system", "uri");
                fixture.AddTypedElement(quantity, "Quantity.code", "code");
            }
            DbStructureDefinition extension = fixture.AddStructure(package, "Extension");
            fixture.AddElement(extension, "Extension");
            DbElement value = fixture.AddElement(extension, "Extension.value[x]");
            foreach (string type in includeOtherCarriers
                ? new[] { "code", "string", "uri", "Coding", "CodeableConcept", "Quantity" }
                : new[] { "code", "string", "uri", "Coding", "CodeableConcept" })
            {
                fixture.AddType(value, type, fixture.Structure(package, type));
            }

            fixture.AddElement(fixture.Structure(package), "ValueSet");
            if (includeOtherCarriers)
            {
                DbStructureDefinition basic = fixture.AddStructure(package, "Basic", FhirArtifactClassEnum.Resource);
                fixture.AddElement(basic, "Basic");
                fixture.AddTypedElement(basic, "Basic.code", "CodeableConcept");
            }
        }
    }

    internal static UnsafeCodeTestFixture CreateImportedNativeCodingInputs(
        int targetIndex, ImportedNativeScenario scenario, bool enableRule, bool includeLocalContexts = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetIndex, 1);
        UnsafeCodeTestFixture fixture = new(initializeIndices: true, persistDatabase: true, publishDefinitions: false, includeLegacyPackages: true);
        fixture.InsertRepresentationGraph(includeOtherCarriers: false);
        if (includeLocalContexts)
        {
            // Bound extensions query terminology outcomes even when this staged run processes only structures.
            DbOutcomeClasses.CreateTables(fixture.Connection, forValueSets: true, forStructures: false);
        }
        Directory.Delete(Path.Combine(fixture.SourceRoot, "input"), recursive: true);
        foreach (DbFhirPackage package in fixture.Packages)
        {
            foreach (string primitive in new[] { "code", "string", "uri" })
            {
                fixture.AddElement(fixture.Structure(package, primitive), primitive);
            }

            bool source = package.DefinitionFhirSequence == FhirReleases.FhirSequenceCodes.R5;
            (string system, string version, string code) = source
                ? (ImportedSourceSystem, ImportedSystemVersion, "unsafe") : ImportedTargetIdentity(scenario);
            DbValueSet inner = fixture.AddImportedValueSet(package, "inner", new()
            {
                Include = [new() { System = system, Version = version,
                    Concept = [new() { Code = source && scenario == ImportedNativeScenario.SourceNonmember ||
                        !source && scenario == ImportedNativeScenario.TargetNonmember ? "sibling-only" : code }] }],
            });
            Hl7.Fhir.Model.ValueSet.ComposeComponent compose = new() { Include = [new() { ValueSet = [inner.VersionedUrl] }] };
            if (source && scenario == ImportedNativeScenario.SourceNonmember)
            {
                compose.Include.Add(new() { System = ImportedSourceSystem, Version = ImportedSystemVersion, Concept = [new() { Code = "unsafe" }] });
            }
            if (!source && scenario == ImportedNativeScenario.TargetNonmember)
            {
                compose.Include.Add(new() { System = system, Version = version, Concept = [new() { Code = code }] });
            }

            DbValueSet outer = fixture.AddImportedValueSet(package, "outer", compose);
            DbElement jurisdiction = fixture.AddTypedElement(fixture.Structure(package), ImportedNativeElement, "CodeableConcept");
            jurisdiction.BindingValueSet = outer.VersionedUrl;
            jurisdiction.BindingValueSetKey = outer.Key;
            jurisdiction.MinCardinality = 0;
            jurisdiction.MaxCardinality = -1;
            jurisdiction.MaxCardinalityString = "*";
            DbElement coding = DbElement.SelectSingle(fixture.Connection, FhirPackageKey: package.Key, Id: "CodeableConcept.coding")!;
            coding.MinCardinality = 0;
            coding.MaxCardinality = -1;
            coding.MaxCardinalityString = "*";
            DbElement.Update(fixture.Connection, [jurisdiction, coding]);
            if (includeLocalContexts)
            {
                DbValueSet unrelated = fixture.AddImportedValueSet(package, "unrelated", new()
                {
                    Include = [new() { System = "urn:example:unrelated", Concept = [new() { Code = "safe" }] }],
                });
                foreach (string use in source ? new[] { "sourceUnrelated", "targetUnrelated", "extensionOnly" }
                    : new[] { "sourceUnrelated", "targetUnrelated" })
                {
                    DbElement site = fixture.AddTypedElement(fixture.Structure(package), "ValueSet." + use, "CodeableConcept");
                    DbValueSet binding = source && use == "sourceUnrelated" || !source && use == "targetUnrelated" ? unrelated : outer;
                    site.BindingValueSet = binding.VersionedUrl;
                    site.BindingValueSetKey = binding.Key;
                    DbElement.Update(fixture.Connection, [site]);
                }
            }
        }

        foreach ((int source, int target) in new[] { (2, 1), (1, 0), (2, 0) })
        {
            string direction = (source == 1 ? "4B" : "5") + "to" +
                (target == 1 ? "4B" : "4");
            foreach ((string category, string suffix, string[] types) in new[]
                {
                    ("resources", "resource-types", new[] { "ValueSet" }),
                    ("types", "data-types", new[] { "Coding", "CodeableConcept", "Extension" }),
                })
            {
                fixture.WriteInputMap(new()
                {
                    Id = "imported-native-" + category + "-" + direction,
                    Url = "http://example.org/ConceptMap/imported-native-" + category + "-" + direction,
                    Status = PublicationStatus.Active,
                    Group = [new()
                    {
                        Source = "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/" + suffix,
                        Target = "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/" + suffix,
                        Element = [.. types.Select(type => new ConceptMap.SourceElementComponent
                        {
                            Code = type,
                            Target = [new() { Code = type, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                        })],
                    }],
                }, category, "ConceptMap-" + category + "-" + direction + ".json");
            }

            ConceptMap map = new()
            {
                Id = "imported-native-elements-" + direction,
                Url = "http://example.org/ConceptMap/imported-native-elements-" + direction,
                Status = PublicationStatus.Active,
                Group = [new()
                {
                    Source = "http://hl7.org/fhir/" + fixture.Packages[source].FhirVersionShort + "/element-names",
                    Target = "http://hl7.org/fhir/" + fixture.Packages[target].FhirVersionShort + "/element-names",
                    Element = [.. DbElement.SelectList(fixture.Connection, FhirPackageKey: fixture.Packages[source].Key)
                        .Select(element => new ConceptMap.SourceElementComponent
                        {
                            Code = element.Id,
                            NoMap = element.Id == "ValueSet.extensionOnly" ? true : null,
                            Target = element.Id == "ValueSet.extensionOnly" ? []
                                : [new() { Code = element.Id, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                        })],
                }],
            };
            fixture.WriteInputMap(map, "elements", "ConceptMap-elements-" + direction + ".json");
        }

        fixture.WriteInputMap(new()
        {
            Id = "imported-native-primitives",
            Url = "http://example.org/ConceptMap/imported-native-primitives",
            Status = PublicationStatus.Active,
            Group = [new()
            {
                Source = "http://hl7.org/fhir/fhir-types",
                Target = "http://hl7.org/fhir/fhir-types",
                Element = [.. new[] { "code", "string", "uri" }.Select(type => new ConceptMap.SourceElementComponent
                {
                    Code = type,
                    Target = [new() { Code = type, Relationship = ConceptMap.ConceptMapRelationship.Equivalent }],
                })],
            }],
        }, "types", "ConceptMap-types-fallback.json");
        DbValueSet sourceInner = fixture.ImportedValueSet(fixture.Packages[2], "inner");
        DbValueSet targetInner = fixture.ImportedValueSet(fixture.Packages[targetIndex], "inner");
        (string targetSystem, string targetVersion, string targetCode) = ImportedTargetIdentity(scenario);
        string destination = targetIndex == 0 ? "R4" : "R4B";
        fixture.WriteInputMap(new()
        {
            Id = "imported-native-R5-" + destination,
            Url = "http://example.org/ConceptMap/imported-native-R5-" + destination,
            Status = PublicationStatus.Active,
            SourceScope = new Hl7.Fhir.Model.Canonical(scenario == ImportedNativeScenario.SourceValueSetVersionMismatch
                ? sourceInner.UnversionedUrl + "|wrong-vs-version" : sourceInner.VersionedUrl),
            TargetScope = new Hl7.Fhir.Model.Canonical(scenario == ImportedNativeScenario.TargetValueSetVersionMismatch
                ? targetInner.UnversionedUrl + "|wrong-vs-version" : targetInner.VersionedUrl),
            Group = [new()
            {
                Source = ImportedSourceSystem + "|" + (scenario == ImportedNativeScenario.SourceSystemVersionMismatch ? "conflicting-system-version" : ImportedSystemVersion),
                Target = targetSystem + "|" + (scenario == ImportedNativeScenario.TargetSystemVersionMismatch ? "conflicting-system-version" : targetVersion),
                Element = [new() { Code = "unsafe", Target = [new() { Code = targetCode, Relationship = ConceptMap.ConceptMapRelationship.RelatedTo }] }],
            }],
        }, "codes", "ConceptMap-R5-imported-native-" + destination + ".json");
        if (enableRule)
        {
            fixture.WriteConfiguration("""[{"system":"urn:example:source","code":"unsafe"}]""");
        }

        fixture.PublishAuthoredDefinitions();
        return fixture;
    }

    internal static (string System, string Version, string Code) ImportedTargetIdentity(ImportedNativeScenario scenario) => scenario switch
    {
        ImportedNativeScenario.IdentityPreserving => (ImportedSourceSystem, ImportedSystemVersion, "unsafe"),
        ImportedNativeScenario.CodeOnly => (ImportedSourceSystem, ImportedSystemVersion, "changed"),
        ImportedNativeScenario.SystemOnly => ("urn:example:target", ImportedSystemVersion, "unsafe"),
        ImportedNativeScenario.SystemVersionOnly => (ImportedSourceSystem, "system-version-8", "unsafe"),
        _ => ("urn:example:target", ImportedSystemVersion, "changed"),
    };

    internal void WriteEquivalentImportedMaps(int targetIndex)
    {
        string destination = targetIndex == 0 ? "R4" : "R4B";
        ConceptMap original = ReadMap("codes", "ConceptMap-R5-imported-native-" + destination + ".json");
        foreach (DbFhirPackage package in new[] { Packages[2], Packages[targetIndex] })
        {
            DbValueSet inner = ImportedValueSet(package, "inner");
            inner.Compose!.Include[0].Concept.Add(new() { Code = "safe" });
            DbValueSet.Update(Connection, [inner]);
        }
        foreach (string name in new[] { "A", "z" })
        {
            ConceptMap map = (ConceptMap)original.DeepCopy();
            map.Id = name == "A" ? null : "equivalent-imported-" + destination;
            map.Url = "http://example.org/ConceptMap/equivalent-imported-" + name + "-" + destination;
            map.Group = [];
            for (int group = 0; group < 3; group++)
            {
                map.Group.Add(new()
                {
                    Source = group == 2 ? ImportedSourceSystem : original.Group[0].Source,
                    Target = group == 2 ? original.Group[0].Target.Split('|')[0] : original.Group[0].Target,
                    Element = [.. new[] { "unsafe", "safe" }.Select(code => new ConceptMap.SourceElementComponent
                    {
                        Code = code,
                        Target = [.. Enumerable.Range(0, group == 2 ? 1 : 2).Select(index => new ConceptMap.TargetElementComponent
                        {
                            Code = code == "unsafe" ? original.Group[0].Element[0].Target[0].Code : code,
                            Relationship = index == 0 ? ConceptMap.ConceptMapRelationship.RelatedTo : ConceptMap.ConceptMapRelationship.Equivalent,
                        })],
                    })],
                });
            }

            WriteInputMap(map, "codes", "ConceptMap-R5-" + name + "-equivalent-imported-" + destination + ".json");
        }
    }

    internal void WriteInputMap(ConceptMap map, string category, string filename)
    {
        string directory = Path.Combine(SourceRoot, "input", category);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, filename), map.ToJson());
    }

    internal DbValueSet ImportedValueSet(DbFhirPackage package, string scope) =>
        DbValueSet.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "imported-native-" + scope)!;

    private DbValueSet AddImportedValueSet(DbFhirPackage package, string scope, Hl7.Fhir.Model.ValueSet.ComposeComponent compose)
    {
        string release = package.DefinitionFhirSequence.ToRLiteral().ToLowerInvariant();
        TestValueSet row = new(package)
        {
            Id = "imported-native-" + scope,
            Name = "ImportedNative" + scope,
            UnversionedUrl = "http://example.org/ValueSet/" + release + "-" + scope,
            Version = scope + "-vs-" + release + "-3",
            Compose = compose,
            CanExpand = false,
        };
        row.VersionedUrl = row.UnversionedUrl + "|" + row.Version;
        row.Key = DbValueSet.Insert(Connection, row);
        return row;
    }

    internal static bool EvaluateImportedNativeCondition(string expression, int targetIndex, string? system, string? code, string? uri = null,
        bool mixed = false)
    {
        string json = "{\"resourceType\":\"ValueSet\",\"status\":\"active\"" +
            (uri is null ? "" : ",\"implicitRules\":" + JsonSerializer.Serialize(uri)) +
            (code is null ? "" : ",\"jurisdiction\":[{\"coding\":[" +
                (mixed ? "{\"system\":\"urn:example:source\",\"code\":\"safe\"}," : "") +
                "{\"system\":" + JsonSerializer.Serialize(system) + ",\"code\":" + JsonSerializer.Serialize(code) + "}]}]") + "}";
        ITypedElement instance = TargetInstance(targetIndex, json);
        FhirPathCompiler compiler = new(new SymbolTable().AddStandardFP().AddFhirExtensions());
        ITypedElement[] results = [.. compiler.Compile(expression)(instance, new EvaluationContext())];
        if (results.Length != 1 || results[0].Value is not bool result)
        {
            throw new InvalidOperationException("The recovered imported-native condition did not return exactly one Boolean.");
        }

        return result;
    }

    internal SortedDictionary<string, string[]> ImportedNativeSemanticState()
    {
        // Compare every persisted field, replacing only primary/foreign keys with semantic identities.
        List<string> tables = [];
        using (SqliteCommand schema = Connection.CreateCommand())
        {
            schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "AND name NOT IN ('UnsafeCodeInputState', 'UnsafeCodePairState', 'UnsafeCodeRules', 'UnsafeCodeTriggers') ORDER BY name";
            using SqliteDataReader reader = schema.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        Dictionary<string, Dictionary<long, SortedDictionary<string, object?>>> rows = [];
        Dictionary<string, Dictionary<string, string>> references = [];
        foreach (string table in tables)
        {
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table + "\"";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                rows[table] = [];
                while (reader.Read())
                {
                    SortedDictionary<string, object?> row = new(StringComparer.Ordinal);
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                    }

                    rows[table].Add((long)row["Key"]!, row);
                }
            }

            command.CommandText = "PRAGMA foreign_key_list(\"" + table + "\")";
            using SqliteDataReader foreignKeys = command.ExecuteReader();
            references[table] = [];
            while (foreignKeys.Read())
            {
                references[table].Add(foreignKeys.GetString(3), foreignKeys.GetString(2));
            }
        }

        Dictionary<(string Table, long Key), string> identities = [];
        SortedDictionary<string, string[]> state = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            state.Add(table, [.. rows[table].Keys.Select(key => Normalize(table, key)).Order(StringComparer.Ordinal)]);
        }

        return state;

        string Normalize(string table, long key)
        {
            if (identities.TryGetValue((table, key), out string? cached))
            {
                return cached;
            }

            SortedDictionary<string, object?> normalized = new(StringComparer.Ordinal);
            foreach ((string column, object? value) in rows[table][key])
            {
                if (column == "Key")
                {
                    continue;
                }
                if (TryNormalizeInventoryValue(table, column, value, out object? inventoryValue))
                {
                    normalized[column] = inventoryValue;
                    continue;
                }

                string? reference = references[table].GetValueOrDefault(column);
                reference ??= column switch
                {
                    "ParentElementKey" or "ContentReferenceSourceKey" => "Elements",
                    "BindingValueSetKey" => "ValueSets",
                    _ => null,
                };
                if (column.StartsWith("ContentKeyR", StringComparison.Ordinal))
                {
                    reference = table switch
                    {
                        "StructureComparisons" => "Structures",
                        "ElementComparisons" => "Elements",
                        "ElementTypeComparisons" => "ElementTypes",
                        "ValueSetComparisons" => "ValueSets",
                        "ValueSetConceptComparisons" => "ValueSetConcepts",
                        _ => throw new InvalidOperationException("Unknown comparison content key: " + table),
                    };
                }

                if (value is long foreignKey && reference is not null)
                {
                    normalized[column] = Identity(reference, foreignKey);
                }
                else if (value is string literal && column.EndsWith("KeysLiteral", StringComparison.Ordinal))
                {
                    string memberTable = column.Contains("ChildElement", StringComparison.Ordinal) ? "Elements" : "ElementTypes";
                    normalized[column] = literal.Split(',').Select(member => Identity(memberTable, long.Parse(member))).ToArray();
                }
                else
                {
                    normalized[column] = value;
                }
            }

            string result = JsonSerializer.Serialize(normalized);
            identities.Add((table, key), result);
            return result;
        }

        string Identity(string table, long key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(table, key))));
    }

    private bool TryNormalizeInventoryValue(string table, string column, object? value, out object? normalized)
    {
        normalized = value;
        if (table == DbProfileMapOwnerFacts.DefaultTableName && column == nameof(DbProfileMapOwnerFacts.InputSnapshotId))
        {
            normalized = Equals(value, InputState.SnapshotId) ? "input-snapshot" : value;
            return true;
        }
        if (table != DbDefinitionInventoryState.DefaultTableName)
        {
            return false;
        }

        DbDefinitionInventoryState inventory = DefinitionInventoryStore.ReadState(Connection)!;
        if (column is nameof(DbDefinitionInventoryState.StartedRevision) or nameof(DbDefinitionInventoryState.CompletedRevision))
        {
            normalized = Equals(value, inventory.StartedRevision) ? "inventory-revision" : value;
            return true;
        }
        if (column is nameof(DbDefinitionInventoryState.PendingMapInventoryBindingsJson) or nameof(DbDefinitionInventoryState.CompletedMapInventoryBindingsJson))
        {
            Dictionary<string, string> bindings = JsonSerializer.Deserialize<Dictionary<string, string>>((string)value!)!;
            normalized = new SortedDictionary<string, string>(bindings.ToDictionary(
                binding => binding.Key == InputState.SnapshotId ? "input-snapshot" : binding.Key,
                binding => binding.Value == inventory.StartedRevision ? "inventory-revision" : binding.Value), StringComparer.Ordinal);
            return true;
        }
        return false;
    }

    internal DbElement AddTypedElement(DbStructureDefinition structure, string id, string type)
    {
        DbElement element = AddElement(structure, id, type);
        AddType(element, type, Structure(Packages.Single(package => package.Key == structure.FhirPackageKey), type));
        return element;
    }

    internal DbStructureOutcome[] AddResourceOwnerGraph(FhirPackageComparisonPair pair, params string[] resourceTypes)
    {
        foreach (DbFhirPackage package in new[] { pair.SourcePackage, pair.TargetPackage })
        {
            AddElement(AddStructure(package, "Resource", FhirArtifactClassEnum.Resource), "Resource");
            foreach (string type in resourceTypes)
            {
                DbStructureDefinition resource = AddStructure(package, type, FhirArtifactClassEnum.Resource);
                AddElement(resource, type);
                AddTypedElement(resource, type + ".contained", "Resource");
                if (type == "Bundle")
                {
                    AddTypedElement(resource, "Bundle.type", "code");
                    AddElement(resource, "Bundle.entry");
                    AddTypedElement(resource, "Bundle.entry.resource", "Resource");
                }
                else if (type == "Parameters")
                {
                    AddElement(resource, "Parameters.parameter");
                    AddTypedElement(resource, "Parameters.parameter.resource", "Resource");
                    DbElement value = AddTypedElement(resource, "Parameters.parameter.value[x]", "Coding");
                    value.IsChoiceType = true;
                    DbElement.Update(Connection, [value]);
                }
                else if (type == "Observation")
                {
                    AddTypedElement(resource, "Observation.code", "CodeableConcept");
                }
            }
        }

        return [.. resourceTypes.Select(type => AddOutcome(pair, Structure(pair.SourcePackage, type), Structure(pair.TargetPackage, type)))];
    }

    internal static ITypedElement TargetInstance(int targetIndex, string json)
    {
        ModelInspector inspector = targetIndex == 0
            ? r4::Hl7.Fhir.Model.ModelInfo.ModelInspector
            : r4b::Hl7.Fhir.Model.ModelInfo.ModelInspector;
        BaseFhirJsonPocoDeserializer deserializer = new(inspector);
        Utf8JsonReader reader = new(System.Text.Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 512 });
        return deserializer.DeserializeResource(ref reader).ToTypedElement(inspector);
    }

    internal DbElementOutcome AddRepresentationOutcome(FhirPackageComparisonPair pair, DbElement source,
        string? context = null, DbElementOutcome? parent = null, bool cardinalityOnly = false)
    {
        TestElementOutcome outcome = new(pair, source)
        {
            RequiresXVerDefinition = true,
            RequiresExtensionDefinition = !cardinalityOnly && parent is null,
            RequiresCardinalityDefinition = cardinalityOnly,
            RequiresSliceDefinition = parent is not null,
            ParentElementOutcomeKey = parent?.Key,
            ExtensionContextsLiteral = cardinalityOnly ? null : context,
            CardinalityExtensionContextsLiteral = cardinalityOnly ? context : null,
        };
        outcome.Key = DbElementOutcome.Insert(Connection, outcome);
        return outcome;
    }

    internal DbValueSetOutcome AddVocabularyMapOutcome(FhirPackageComparisonPair pair, string name)
    {
        DbValueSet source = SetBinding(pair.SourcePackage);
        DbValueSet target = SetBinding(pair.TargetPackage);
        TestValueSetOutcome outcome = new(pair, source, target, name);
        outcome.Key = DbValueSetOutcome.Insert(Connection, outcome);
        return outcome;
    }

    internal void WriteConfiguration(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFilename)!);
        File.WriteAllText(ConfigurationFilename, json);
    }

    internal ConceptMap ReadMap(string category, string filename) =>
        new FhirJsonParser().Parse<ConceptMap>(File.ReadAllText(Path.Combine(SourceRoot, "input", category, filename)));

    internal void Capture(ConceptMap map, string category = "elements")
    {
        UnsafeCodeInputStore.BeginInputs(Connection, "fixture", null);
        UnsafeCodeSourceReader.Capture(Connection, map, category, Path.Combine(SourceRoot, "test.json"), "input/test.json", Packages[2].Key, Packages[0].Key);
    }

    internal void SetExpandable(bool value)
    {
        List<DbValueSet> rows = DbValueSet.SelectList(Connection);
        foreach (DbValueSet row in rows)
        {
            row.CanExpand = value;
        }

        rows.Update(Connection);
    }

    internal void InsertFilterElements()
    {
        foreach (DbFhirPackage package in Packages)
        {
            DbStructureDefinition owner = Structure(package);
            AddElement(owner, "ValueSet.compose", "BackboneElement");
            AddElement(owner, "ValueSet.compose.include", "BackboneElement");
            AddElement(owner, "ValueSet.compose.include.filter", "BackboneElement");
            AddElement(owner, FilterElement, "code", FilterValueSet);
        }
        PublishAuthoredDefinitions();
    }

    internal FhirPackageComparisonPair PreparePair(int source = 2, int target = 0)
    {
        PublishAuthoredDefinitions();
        UnsafeCodeInputStore.ReplaceWithEmptyInputs(Connection, Packages);
        DbComparisonClasses.CreateTables(Connection);
        DbOutcomeClasses.CreateTables(Connection, forValueSets: false);
        FhirPackageComparisonPair pair = new(Packages[source], Packages[target]);
        StampComparison(pair);
        return pair;
    }

    internal void StampComparison(FhirPackageComparisonPair pair)
    {
        string? revision = UnsafeCodeInputStore.BeginComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey);
        UnsafeCodeInputStore.CompleteComparison(Connection, pair.SourcePackageKey, pair.TargetPackageKey, revision);
    }

    internal void GenerateSafety(FhirPackageComparisonPair pair) =>
        new UnsafeCodeOutcomeGenerator(Connection).GenerateForPair(pair);

    internal void AddTrigger(DbStructureOutcome outcome, ImmutableArray<UnsafeCodePathStep> path,
        string system = FilterSystem, string code = "unsafe", string? sourceVersion = null,
        bool wildcard = true, params string[] versions)
    {
        DbUnsafeCodeTrigger.Insert(Connection, new DbUnsafeCodeTrigger
        {
            StructureOutcomeKey = outcome.Key,
            SourceFhirPackageKey = outcome.SourceFhirPackageKey,
            TargetFhirPackageKey = outcome.TargetFhirPackageKey,
            SourceElementId = path[^1].ElementId,
            TargetElementId = "",
            SourcePathJson = JsonSerializer.Serialize(path),
            System = system,
            Code = code,
            SourceVersion = sourceVersion,
            IsWildcardVersion = wildcard,
            VersionsJson = JsonSerializer.Serialize(versions),
            EvidenceKeysJson = "[]",
            RuleKeysJson = "[]",
        });
    }

    internal DbUnsafeCodePairState PairState(FhirPackageComparisonPair pair) =>
        DbUnsafeCodePairState.SelectSingle(Connection, SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey)!;

    internal DbStructureDefinition AddStructure(DbFhirPackage package, string id, FhirArtifactClassEnum artifactClass = FhirArtifactClassEnum.ComplexType)
    {
        DbStructureDefinition? existing = DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: id);
        if (existing is not null)
        {
            return existing;
        }
        TestStructure row = new(package)
        {
            Id = id,
            Name = id,
            ArtifactClass = artifactClass,
            UnversionedUrl = "http://example.org/StructureDefinition/" + id,
            VersionedUrl = "http://example.org/StructureDefinition/" + id + "|" + package.PackageVersion,
        };
        row.Key = DbStructureDefinition.Insert(Connection, row);
        if (artifactClass != FhirArtifactClassEnum.Profile)
        {
            _authoredDefinitions.Add(row.Key, (id, artifactClass == FhirArtifactClassEnum.Resource
                ? StructureDefinition.StructureDefinitionKind.Resource : artifactClass == FhirArtifactClassEnum.PrimitiveType
                    ? StructureDefinition.StructureDefinitionKind.PrimitiveType : StructureDefinition.StructureDefinitionKind.ComplexType, null));
        }
        return row;
    }

    internal DbStructureDefinition Structure(DbFhirPackage package, string id = "ValueSet") =>
        DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: id)!;

    internal DbElement AddElement(DbStructureDefinition structure, string id, string type = "", string? binding = null, BindingStrength strength = BindingStrength.Required)
    {
        DbFhirPackage package = Packages.Single(package => package.Key == structure.FhirPackageKey);
        foreach (string name in type.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            AuthorNativeType(package, name);
        }
        List<DbElement> siblings = DbElement.SelectList(Connection, StructureKey: structure.Key);
        DbElement? existing = siblings.SingleOrDefault(element => element.Id == id);
        if (existing is not null)
        {
            return existing;
        }
        int separator = id.LastIndexOf('.');
        TestElement row = new(package, structure.Key, id)
        {
            ResourceFieldOrder = siblings.Count,
            ParentElementKey = separator < 0 ? null : siblings.SingleOrDefault(parent => parent.Id == id[..separator])?.Key,
            FullCollatedTypeLiteral = type,
            DistinctTypeLiterals = type,
            DistinctTypeCount = type.Length == 0 ? 0 : 1,
            BindingValueSet = binding,
            ValueSetBindingStrength = strength,
        };
        row.Key = DbElement.Insert(Connection, row);
        if (row.ParentElementKey is int parentKey)
        {
            DbElement parent = siblings.Single(parent => parent.Key == parentKey);
            parent.ChildElementCount++;
            DbElement.Update(Connection, [parent]);
        }

        return row;
    }

    private void AuthorNativeType(DbFhirPackage package, string name)
    {
        if (DbStructureDefinition.SelectSingle(Connection, FhirPackageKey: package.Key, Id: name) is not null)
        {
            return;
        }

        if (name is not ("boolean" or "code" or "string" or "uri" or "decimal" or "Coding" or
            "Reference" or "Quantity" or "Age" or "BackboneElement"))
        {
            return;
        }

        bool primitive = name is "boolean" or "code" or "string" or "uri" or "decimal";
        DbStructureDefinition owner = AddStructure(package, name,
            primitive ? FhirArtifactClassEnum.PrimitiveType : FhirArtifactClassEnum.ComplexType);
        AddElement(owner, name);
        if (primitive || name is "Reference" or "BackboneElement")
        {
            return;
        }
        AddElement(owner, name + ".system", "uri");
        AddElement(owner, name + ".code", "code");
        if (name == "Coding")
        {
            AddElement(owner, "Coding.version", "string");
        }
        if (name == "Age")
        {
            AuthorNativeType(package, "Quantity");
            DeclareBaseDefinition(owner, Structure(package, "Quantity"));
        }
    }

    internal void AddType(DbElement element, string typeName, DbStructureDefinition type, DbStructureDefinition? profile = null)
    {
        DbElementType.Insert(Connection, new DbElementType
        {
            FhirPackageKey = element.FhirPackageKey,
            StructureKey = element.StructureKey,
            ElementKey = element.Key,
            TypeName = typeName,
            TypeStructureKey = type.Key,
            TypeProfile = profile?.VersionedUrl,
            TypeProfileStructureKey = profile?.Key,
            TargetProfile = null,
            TargetProfileStructureKey = null,
        });
    }

    internal DbValueSet SetBinding(DbFhirPackage package, string system = FilterSystem, string? systemVersion = null, string valueSetVersion = "source-vs")
    {
        DbValueSet row = DbValueSet.SelectSingle(Connection, FhirPackageKey: package.Key, Id: "filter-operator")!;
        row.Version = valueSetVersion;
        row.VersionedUrl = row.UnversionedUrl + "|" + valueSetVersion;
        row.Compose = new() { Include = [new() { System = system, Version = systemVersion }] };
        DbValueSet.Update(Connection, [row]);
        return row;
    }

    internal DbValueSet BindRequired(DbElement element, string id, string version = "binding-1")
    {
        DbFhirPackage package = Packages.Single(package => package.Key == element.FhirPackageKey);
        DbValueSet binding = AuthorBinding(package, id, version);
        element.BindingValueSet = binding.VersionedUrl;
        element.BindingValueSetKey = binding.Key;
        DbElement.Update(Connection, [element]);
        return binding;
    }

    internal DbValueSet AuthorBinding(DbFhirPackage package, string id, string version = "binding-1")
    {
        string url = "http://example.org/ValueSet/" + id;
        DbValueSet? binding = DbValueSet.SelectSingle(Connection, FhirPackageKey: package.Key, UnversionedUrl: url, Version: version);
        if (binding is null)
        {
            binding = new TestValueSet(package)
            {
                Id = id, Name = id.Replace("-", "", StringComparison.Ordinal), Version = version,
                UnversionedUrl = url, VersionedUrl = url + "|" + version,
                Compose = new() { Include = [new() { System = "urn:example:profile-values", Concept = [new() { Code = "selected" }] }] },
            };
            binding.Key = DbValueSet.Insert(Connection, binding);
        }
        return binding;
    }

    internal DbStructureOutcome AddOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target = null)
    {
        TestStructureOutcome row = new(pair, source, target);
        row.Key = DbStructureOutcome.Insert(Connection, row);
        return row;
    }

    internal void AddRule(string system = FilterSystem, string code = "unsafe", string? version = null, bool noMapOnly = false,
        string excludedRelationships = "", string excludedVersions = "")
    {
        DbUnsafeCodeRule.Insert(Connection, new DbUnsafeCodeRule
        {
            System = system,
            Code = code,
            Version = version,
            NoMapOnly = noMapOnly,
            ExcludedRelationshipLiteral = excludedRelationships,
            ExcludedVersionLiteral = excludedVersions,
            SourceRelativeFilename = "input/ig-support/unsafe-code-mappings.json",
            EntryIndex = DbUnsafeCodeRule.SelectList(Connection).Count,
        });
    }

    internal void AddAnnotation(FhirPackageComparisonPair pair, DbElement source, DbElement target)
    {
        DbUnsafeCodeElementAnnotation annotation = new()
        {
            SourceFhirPackageKey = pair.SourcePackageKey,
            TargetFhirPackageKey = pair.TargetPackageKey,
            SourceElementId = source.Id,
            TargetElementId = target.Id,
            RequiresOmittedMandatoryCode = true,
            SourceRelativeFilename = "input/elements/test.json",
            GroupIndex = 0,
            SourceIndex = DbUnsafeCodeElementAnnotation.SelectList(Connection).Count,
            TargetIndex = 0,
        };
        annotation.Key = DbUnsafeCodeElementAnnotation.Insert(Connection, annotation);
        DbElementMapping[] mappings = [.. DbElementMapping.SelectList(Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey,
            SourceElementKey: source.Key, TargetElementKey: target.Key)];
        foreach (int? mappingKey in mappings.Length == 0 ? new int?[] { null } : mappings.Select(mapping => (int?)mapping.Key))
        {
            DbProfileMapOwnerFacts.Insert(Connection, new DbProfileMapOwnerFacts
            {
                AnnotationKey = annotation.Key,
                InputSnapshotId = InputState.SnapshotId,
                SourceFhirPackageKey = pair.SourcePackageKey,
                TargetFhirPackageKey = pair.TargetPackageKey,
                SourceStructureKey = source.StructureKey,
                TargetStructureKey = target.StructureKey,
                SourceOwnerContext = source.Id,
                TargetOwnerContext = target.Id,
                GroupSourceCanonical = DbStructureDefinition.SelectSingle(Connection, Key: source.StructureKey)!.VersionedUrl,
                GroupTargetCanonical = DbStructureDefinition.SelectSingle(Connection, Key: target.StructureKey)!.VersionedUrl,
                GroupSourceVersion = DbStructureDefinition.SelectSingle(Connection, Key: source.StructureKey)!.Version,
                GroupTargetVersion = DbStructureDefinition.SelectSingle(Connection, Key: target.StructureKey)!.Version,
                ElementMappingKey = mappingKey,
                ResolutionState = mappingKey is null ? ProfileMapOwnerResolution.Unnormalized : ProfileMapOwnerResolution.Resolved,
                SourceElementId = source.Id,
                TargetElementId = target.Id,
                SourceRelativeFilename = annotation.SourceRelativeFilename,
                GroupIndex = annotation.GroupIndex,
                SourceIndex = annotation.SourceIndex,
                TargetIndex = annotation.TargetIndex,
            });
        }
    }

    internal DbUnsafeCodeMapEvidence AddEvidence(FhirPackageComparisonPair pair, string code = "unsafe", string? sourceScope = FilterValueSet,
        string? targetScope = FilterValueSet, string? sourceVersion = null, string? targetVersion = null, bool noMap = true,
        ConceptMap.ConceptMapRelationship? relationship = null, string sourceSystem = FilterSystem)
    {
        DbUnsafeCodeMapEvidence row = new()
        {
            SourceFhirPackageKey = pair.SourcePackageKey,
            TargetFhirPackageKey = pair.TargetPackageKey,
            SourceValueSetUrl = sourceScope,
            TargetValueSetUrl = targetScope,
            SourceValueSetVersion = sourceVersion,
            TargetValueSetVersion = targetVersion,
            SourceSystem = sourceSystem,
            SourceCode = code,
            ExplicitNoMap = noMap,
            Relationship = relationship,
            SourceRelativeFilename = "input/codes/test.json",
            GroupIndex = 0,
            SourceIndex = DbUnsafeCodeMapEvidence.SelectList(Connection).Count,
        };
        row.Key = DbUnsafeCodeMapEvidence.Insert(Connection, row);
        return row;
    }

    internal void Connect(FhirPackageComparisonPair pair, DbStructureOutcome outcome, DbElement source, DbElement target, DbElementOutcome? existing = null)
    {
        TestElementMapping mapping = new(pair, source, target);
        mapping.Key = DbElementMapping.Insert(Connection, mapping);
        foreach (DbProfileMapOwnerFacts owner in DbProfileMapOwnerFacts.SelectList(Connection,
            SourceFhirPackageKey: pair.SourcePackageKey, TargetFhirPackageKey: pair.TargetPackageKey,
            SourceStructureKey: source.StructureKey, TargetStructureKey: target.StructureKey,
            SourceElementId: source.Id, TargetElementId: target.Id, ResolutionState: ProfileMapOwnerResolution.Unnormalized))
        {
            owner.ElementMappingKey = mapping.Key;
            owner.ResolutionState = ProfileMapOwnerResolution.Resolved;
            DbProfileMapOwnerFacts.Update(Connection, owner);
        }
        TestElementComparison comparison = new(pair, source, target) { ElementMappingKey = mapping.Key };
        comparison.Key = DbElementComparison.Insert(Connection, comparison);
        DbElementOutcome elementOutcome = existing ?? new TestElementOutcome(pair, source);
        if (existing is null)
        {
            elementOutcome.Key = DbElementOutcome.Insert(Connection, elementOutcome);
        }
        DbElementOutcomeTarget.Insert(Connection, new TestElementTarget(pair, outcome, elementOutcome, comparison, target));
    }

    private sealed class TestValueSetOutcome : DbValueSetOutcome
    {
        [SetsRequiredMembers]
        internal TestValueSetOutcome(FhirPackageComparisonPair pair, DbValueSet source, DbValueSet target, string name)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceValueSetKey = source.Key;
            TargetValueSetKey = target.Key;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceVersion = source.Version;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            TargetId = target.Id;
            TargetName = target.Name;
            TargetVersion = target.Version;
            TargetCanonicalVersioned = target.VersionedUrl;
            TargetCanonicalUnversioned = target.UnversionedUrl;
            RequiresXVerDefinition = true;
            ConceptMapLongId = "Map-vocabulary-collision";
            ConceptMapShortId = ConceptMapLongId;
            ConceptMapUrl = "http://example.org/ConceptMap/vocabulary-collision";
            ConceptMapName = name;
            ConceptMapFileName = "ConceptMap-vocabulary-collision";
            Comments = "";
        }
    }

    private sealed class TestStructureOutcome : DbStructureOutcome
    {
        [SetsRequiredMembers]
        internal TestStructureOutcome(FhirPackageComparisonPair pair, DbStructureDefinition source, DbStructureDefinition? target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceFhirSequence = pair.SourceFhirSequence;
            TargetFhirSequence = pair.TargetFhirSequence;
            SourceStructureKey = source.Key;
            TargetStructureKey = target?.Key;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceCanonicalVersioned = source.VersionedUrl;
            SourceCanonicalUnversioned = source.UnversionedUrl;
            SourceVersion = source.Version;
            SourceArtifactClass = source.ArtifactClass;
            TargetArtifactClass = target?.ArtifactClass;
            TargetId = target?.Id;
            TargetName = target?.Name;
            TargetCanonicalUnversioned = target?.UnversionedUrl;
            TargetCanonicalVersioned = target?.VersionedUrl;
            GenShortId = "profile-" + source.Id;
            GenLongId = GenShortId;
            GenName = "Profile" + source.Name;
            GenUrl = "http://example.org/StructureDefinition/profile-" + source.Id;
            GenFileName = "StructureDefinition-profile-" + source.Id;
            ElementConceptMapLongId = "elements-" + source.Id;
            ElementConceptMapShortId = ElementConceptMapLongId;
            ElementConceptMapName = "Elements" + source.Name;
            ElementConceptMapUrl = "http://example.org/ConceptMap/" + ElementConceptMapLongId;
            ElementConceptMapFileName = "ConceptMap-" + ElementConceptMapLongId;
            Comments = "";
        }
    }

    private sealed class TestElementMapping : DbElementMapping
    {
        [SetsRequiredMembers]
        internal TestElementMapping(FhirPackageComparisonPair pair, DbElement source, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = target.StructureKey;
            SourceElementKey = source.Key;
            TargetElementKey = target.Key;
            SourceElementId = source.Id;
            TargetElementId = target.Id;
        }
    }

    private sealed class TestElementComparison : DbElementComparison
    {
        [SetsRequiredMembers]
        internal TestElementComparison(FhirPackageComparisonPair pair, DbElement source, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            TargetStructureKey = target.StructureKey;
            SourceElementKey = source.Key;
            TargetElementKey = target.Key;
            SourceElementId = source.Id;
            TargetElementId = target.Id;
        }
    }

    private sealed class TestElementOutcome : DbElementOutcome
    {
        [SetsRequiredMembers]
        internal TestElementOutcome(FhirPackageComparisonPair pair, DbElement source)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            SourceStructureKey = source.StructureKey;
            SourceElementKey = source.Key;
            SourceId = source.Id;
            SourceName = source.Name;
            SourceCanonicalVersioned = "";
            SourceCanonicalUnversioned = "";
            SourceVersion = "";
            SourceMaxCardinalityString = source.MaxCardinalityString;
            SourceMinCardinality = source.MinCardinality;
            SourceResourceOrder = source.ResourceFieldOrder;
            SourceChildElementCount = source.ChildElementCount;
            GenShortId = "ext-" + source.Id;
            GenName = "Extension" + source.Id.Replace(".", "_", StringComparison.Ordinal);
            GenUrl = "http://example.org/StructureDefinition/ext-" + source.Id;
            GenFileName = "StructureDefinition-ext-" + source.Id;
            GenSliceName = "fixed-local-" + source.Name;
            Comments = "";
        }
    }

    private sealed class TestElementTarget : DbElementOutcomeTarget
    {
        [SetsRequiredMembers]
        internal TestElementTarget(FhirPackageComparisonPair pair, DbStructureOutcome outcome, DbElementOutcome element, DbElementComparison comparison, DbElement target)
        {
            SourceFhirPackageKey = pair.SourcePackageKey;
            TargetFhirPackageKey = pair.TargetPackageKey;
            ElementOutcomeKey = element.Key;
            StructureOutcomeKey = outcome.Key;
            ElementComparisonKey = comparison.Key;
            TargetStructureKey = target.StructureKey;
            TargetElementKey = target.Key;
            TargetElementId = target.Id;
        }
    }

    private void InsertStructure(DbFhirPackage package)
    {
        TestStructure row = new(package);
        row.Key = DbStructureDefinition.Insert(Connection, row);
        _authoredDefinitions.Add(row.Key, ("ValueSet", StructureDefinition.StructureDefinitionKind.Resource, null));
    }

    private void InsertValueSet(DbFhirPackage package)
    {
        TestValueSet row = new(package);
        row.Key = DbValueSet.Insert(Connection, row);
    }

    // Test-only subclasses avoid repeating the nullable metadata fields on these minimal definitions.
    private sealed class TestStructure : DbStructureDefinition
    {
        [SetsRequiredMembers]
        internal TestStructure(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "ValueSet";
            Name = "ValueSet";
            Version = package.PackageVersion;
            UnversionedUrl = "http://hl7.org/fhir/StructureDefinition/ValueSet";
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
            ArtifactClass = FhirArtifactClassEnum.Resource;
        }
    }

    private sealed class TestValueSet : DbValueSet
    {
        [SetsRequiredMembers]
        internal TestValueSet(DbFhirPackage package)
        {
            FhirPackageKey = package.Key;
            Id = "filter-operator";
            Name = "FilterOperator";
            Version = package.PackageVersion;
            UnversionedUrl = FilterValueSet;
            VersionedUrl = UnversionedUrl + "|" + package.PackageVersion;
            Status = PublicationStatus.Active;
        }
    }

    private sealed class TestElement : DbElement
    {
        [SetsRequiredMembers]
        internal TestElement(DbFhirPackage package, int structureKey, string id)
        {
            FhirPackageKey = package.Key;
            StructureKey = structureKey;
            Id = id;
            Path = id;
            Name = id.Split('.')[^1];
            MinCardinality = 1;
            MaxCardinality = 1;
            MaxCardinalityString = "1";
            FullCollatedTypeLiteral = "code";
            DistinctTypeLiterals = "code";
            DistinctTypeCount = 1;
            BindingValueSet = FilterValueSet;
            ValueSetBindingStrength = BindingStrength.Required;
            IsSimpleType = true;
        }
    }

    public void Dispose()
    {
        WriteMixedChoiceLifetimeObservation("dispose-entered");
        Connection.Dispose();
        WriteMixedChoiceLifetimeObservation("connection-disposed");
        try
        {
            if (Directory.Exists(SourceRoot))
            {
                Directory.Delete(SourceRoot, true);
            }
        }
        catch (IOException failure)
        {
            WriteMixedChoiceLifetimeObservation("cleanup-failed", failure);
            throw;
        }
        WriteMixedChoiceLifetimeObservation("cleanup-completed");
    }

    private void WriteMixedChoiceLifetimeObservation(string boundary, IOException? failure = null)
    {
        if (!_persistDatabase)
        {
            return;
        }
        WriteMixedChoiceObservation("MEASURED_H2_LIFETIME", new
        {
            SourceRoot, snapshot = Path.Combine(SourceRoot, "snapshot.sqlite"),
            process = Environment.ProcessId, boundary, stage = _persistedStage, ConnectionReopens,
            connectionState = Connection.State.ToString(),
            failure = failure is null ? null : new
            {
                type = failure.GetType().FullName, failure.Message, failure.HResult,
                failure.StackTrace, inner = failure.InnerException?.ToString(),
            },
        });
    }
}
